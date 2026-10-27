using GameServer.Actors;
using GameServer.Errors;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Deny-by-default (SEC-08, ACC-12): an endpoint without a security profile is refused even if the startup validator
/// was disabled. Authenticates with the schemes of the endpoint's profile only (SEC-02), enforces GM permissions
/// (SEC-07) and the body limit of the profile (API-06).
/// </summary>
public sealed class ProfileEnforcementMiddleware(
    RequestDelegate next,
    ErrorResponseWriter writer,
    GameServerOptions options,
    IOptions<ActorSchemeOptions> schemes,
    IOptions<CsrfOptions> csrf,
    IAntiforgery antiforgery,
    ILogger<ProfileEnforcementMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            await next(context);
            return;
        }

        var profile = endpoint.GetProfile();
        if (profile is null && endpoint.DisplayName == MethodNotAllowedEndpoint)
        {
            // Routing's own endpoint for "path exists, method does not" (e.g. GET logout, SEC-05): answer 405 with the
            // usual error contract instead of its empty body.
            await writer.WriteAsync(context, CoreErrorCodes.MethodNotAllowed);
            return;
        }

        if (profile is null)
        {
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        if (profile.Value == SecurityProfile.Gm && IsCrossSiteMutation(context.Request))
        {
            // P0 part of SEC-05: a cookie-authenticated GM mutation must come from the same site.
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        if (profile.Value == SecurityProfile.Gm)
        {
            // GM responses may hold PII (player.read.pii): never cache them in browsers or proxies.
            context.Response.Headers.CacheControl = "no-store";
        }

        SetBodyLimit(context, profile.Value);
        string? scheme = null;
        if (profile.Value != SecurityProfile.Public)
        {
            scheme = profile.Value switch
            {
                SecurityProfile.Player => await AuthenticateAsync(context, schemes.Value.PlayerSchemes, ActorType.Player),
                SecurityProfile.Gm => await AuthenticateAsync(context, schemes.Value.GmSchemes, ActorType.Gm),
                _ => await AuthenticateAsync(context, schemes.Value.PlayerSchemes, ActorType.Player)
                     ?? await AuthenticateAsync(context, schemes.Value.GmSchemes, ActorType.Gm),
            };
            if (scheme is null)
            {
                var (code, details) = AuthenticationFailures.Get(context);
                await writer.WriteAsync(context, code, details: details);
                return;
            }
        }

        // SEC-05: cookie-authenticated mutations (and endpoints that ask for it, e.g. local GM login) need the CSRF
        // token; header/bearer schemes cannot be replayed by a browser and do not.
        if (CsrfValidation.IsUnsafe(context.Request.Method)
            && (endpoint.Metadata.GetMetadata<CsrfRequiredMetadata>() is not null || (scheme is not null && csrf.Value.CookieSchemes.Contains(scheme)))
            && !await CsrfValidation.IsValidAsync(context, antiforgery, csrf.Value))
        {
            logger.LogWarning("CSRF validation failed for {Method} {Path}.", context.Request.Method, context.Request.Path.Value);
            await writer.WriteAsync(context, CoreErrorCodes.CsrfFailed);
            return;
        }

        var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
        if (permission is not null && !context.User.HasClaim(ActorClaims.Permission, permission))
        {
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        await next(context);
    }

    /// <summary>Display name of the endpoint ASP.NET Core routing selects when only the HTTP method does not match.</summary>
    private const string MethodNotAllowedEndpoint = "405 HTTP Method Not Supported";

    /// <summary>Returns the scheme that authenticated the caller, or null.</summary>
    private static async Task<string?> AuthenticateAsync(HttpContext context, IReadOnlyList<string> schemeNames, ActorType expected)
    {
        foreach (var scheme in schemeNames)
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Succeeded && ActorClaims.GetActorType(result.Principal) == expected)
            {
                context.User = result.Principal;
                return scheme;
            }
        }

        return null;
    }

    private static bool IsCrossSiteMutation(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method)
        && !HttpMethods.IsOptions(request.Method)
        && string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase);

    private void SetBodyLimit(HttpContext context, SecurityProfile profile)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = profile == SecurityProfile.Gm ? options.Limits.GmBodyBytes : options.Limits.PlayerBodyBytes;
        }
    }
}
