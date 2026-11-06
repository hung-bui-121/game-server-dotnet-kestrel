using System.Security.Claims;
using GameServer.Platform;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// GM sign-in through the company OpenID Connect provider (SEC-03, research R1/R2): authorization code + PKCE,
/// <c>response_mode=query</c> (the callback is a same-site GET, so <c>SameSite=Lax</c> correlation cookies arrive).
/// The handler's sign-in step is replaced: on a valid id_token the identity is matched to a staff record, MFA is
/// checked, and a framework GM session (protected session cookie, <see cref="GmCookies"/>) is created. Every redirect target
/// (success, each denial) comes from <see cref="GmConsoleUrls"/> (slice 018).
/// </summary>
public static class GmSso
{
    public const string SchemeName = "GmOidc";
    public const string StartRoute = "/gm/v1/auth/sso/start";
    public const string CallbackRoute = "/gm/v1/auth/sso/callback";

    /// <summary>
    /// Kept in the OIDC properties when the <c>returnUrl</c> is missing or refused: never empty (the OIDC handler would store the
    /// start URL with the refused value instead); it lands on the console root, or on the default without a console.
    /// </summary>
    private const string RefusedReturnPath = "/";

    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<StaffIdentityMatcher>();
        services.AddSingleton<GmConsoleUrls>();
        services.AddAuthentication().AddOpenIdConnect(SchemeName, _ => { });
        services.AddOptions<OpenIdConnectOptions>(SchemeName).Configure<IOptions<GmOptions>, GameServerOptions>((o, gm, gameServer) =>
        {
            var oidc = gm.Value.Oidc;
            o.Authority = oidc.Authority ?? "https://sso-not-configured.invalid";
            o.ClientId = oidc.ClientId ?? "not-configured";
            o.ClientSecret = oidc.ClientSecret;
            o.ResponseType = "code";
            o.ResponseMode = "query";
            o.UsePkce = true;
            o.CallbackPath = CallbackRoute;
            o.SignInScheme = GmCookieAuthenticationHandler.SchemeName;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !gameServer.AllowsDevelopmentTools;
            o.Scope.Clear();
            foreach (var scope in oidc.Scopes)
            {
                o.Scope.Add(scope);
            }

            foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
            {
                cookie.Path = CallbackRoute;
                cookie.SameSite = SameSiteMode.Lax;
                cookie.SecurePolicy = gameServer.IsDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            }

            if (oidc.BackchannelHandler is not null)
            {
                o.BackchannelHttpHandler = oidc.BackchannelHandler;
            }

            o.Events = new OpenIdConnectEvents
            {
                OnTicketReceived = OnTicketReceivedAsync,
                OnRemoteFailure = OnRemoteFailureAsync,
            };
        });
    }

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapDeclared(GmRoutes.SsoStart, async (HttpContext http, string? returnUrl) =>
            {
                var accepted = http.RequestServices.GetRequiredService<GmConsoleUrls>().AcceptReturnPath(returnUrl);
                await http.ChallengeAsync(SchemeName, new AuthenticationProperties { RedirectUri = accepted ?? RefusedReturnPath });
                return Results.Empty;
            })
            .WithSummary("Start the SSO sign-in: redirect to the identity provider.")
            .AsPublicEndpoint(protobuf: false)
            .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy)
            .RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy);

        // The pipeline has no authentication middleware: the callback is an explicit Public endpoint that hands the
        // request to the OIDC handler (state, nonce, PKCE and the id_token are validated there).
        routes.MapDeclared(GmRoutes.SsoCallback, (Func<HttpContext, Task<IResult>>)(async http =>
            {
                var handlers = http.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>();
                if (await handlers.GetHandlerAsync(http, SchemeName) is IAuthenticationRequestHandler handler && await handler.HandleRequestAsync())
                {
                    return Results.Empty;
                }

                return Results.Redirect(http.RequestServices.GetRequiredService<GmConsoleUrls>().DeniedTarget());
            }))
            .WithSummary("Complete the SSO sign-in (identity provider callback) and start a GM session.")
            .AsPublicEndpoint(protobuf: false)
            .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy)
            .RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy);
    }

    private static async Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GmSso));
        var gm = services.GetRequiredService<IOptions<GmOptions>>().Value;
        var gameServer = services.GetRequiredService<GameServerOptions>();
        var urls = services.GetRequiredService<GmConsoleUrls>();
        var principal = context.Principal ?? new ClaimsPrincipal();
        var subject = principal.FindFirst("sub");
        var issuer = principal.FindFirst("iss")?.Value ?? subject?.Issuer;
        context.HandleResponse();

        string? denial = null;
        StaffRecord? staff = null;
        if (subject is null || string.IsNullOrEmpty(issuer))
        {
            denial = "id_token without issuer or subject";
        }
        else if ((gm.Oidc.RequireMfa ?? !gameServer.AllowsDevelopmentTools)
            && !principal.FindAll("amr").Any(a => gm.Oidc.MfaAmrValues.Contains(a.Value, StringComparer.OrdinalIgnoreCase)))
        {
            denial = "no MFA in the amr claim";
        }
        else
        {
            var emailVerified = string.Equals(principal.FindFirst(gm.Oidc.EmailVerifiedClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase);
            staff = await services.GetRequiredService<StaffIdentityMatcher>().MatchAsync(
                issuer, subject.Value, principal.FindFirst("email")?.Value, emailVerified, http.TraceIdentifier, http.RequestAborted);
            if (staff is null)
            {
                denial = "identity is not an authorized staff member of this deployment";
            }
            else if (!staff.CanSignIn)
            {
                denial = "staff member is disabled";
            }
        }

        if (denial is not null || staff is null)
        {
            // Security log: reason, issuer and the hashed identity key (same as gm_staff_identities); no token, no subject.
            var identityKey = subject is not null && issuer is not null ? StaffAdminService.OidcKey(issuer, subject.Value).Key : "none";
            logger.LogWarning("GM SSO sign-in denied: {Reason} (issuer {Issuer}, identity {IdentityKey}).", denial, issuer, identityKey);
            http.Response.Redirect(urls.DeniedTarget());
            return;
        }

        var sessions = services.GetRequiredService<GmSessionService>();
        await sessions.EndCurrentAsync(http);
        var (cookie, expiresAt) = await sessions.CreateAsync(staff, http.RequestAborted);
        http.Response.Cookies.Append(GmCookies.SessionName(gameServer), cookie, GmCookies.Session(http, expiresAt));
        logger.LogInformation("GM SSO sign-in for staff {StaffId}.", staff.StaffId);
        http.Response.Redirect(urls.SuccessTarget(context.ReturnUri));
    }

    private static Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GmSso));
        logger.LogWarning("GM SSO callback rejected: {Failure}.", context.Failure?.GetType().Name ?? "unknown");
        context.HandleResponse();
        context.Response.Redirect(context.HttpContext.RequestServices.GetRequiredService<GmConsoleUrls>().DeniedTarget());
        return Task.CompletedTask;
    }
}
