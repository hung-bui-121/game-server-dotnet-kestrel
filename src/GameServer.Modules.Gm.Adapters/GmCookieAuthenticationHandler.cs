using System.Security.Claims;
using System.Text.Encodings.Web;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// GM scheme <c>GmCookie</c> (SEC-02, SEC-04): protected cookie <c>gs_gm</c> → server-side ticket → staff. Permissions
/// are those of the grant for this deployment's game and environment only (SEC-07), read from the (≤ 60 s) staff
/// cache on every request. A disabled, invited or unknown staff, or a ticket from an older session epoch (sessions
/// revoked, staff disabled — SEC-10), is UNAUTHENTICATED.
/// </summary>
public sealed class GmCookieAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GmSessionService sessions,
    StaffDirectory staff,
    GameServerOptions gameServer) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GmCookie";
    public const string CookieName = "gs_gm";
    public const string SessionClaim = "gs:gm_session";
    public const string DisplayNameClaim = "gs:display_name";

    /// <summary>A protected session id is ~150 characters; anything far longer is rejected without decrypting.</summary>
    private const int MaxCookieLength = 512;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        if (token.Length > MaxCookieLength || await sessions.ValidateAsync(token, Context.RequestAborted) is not { } session)
        {
            return Fail();
        }

        var record = await staff.GetAsync(session.StaffId, Context.RequestAborted);
        if (record is null || !record.CanSignIn || record.SessionEpoch != session.Epoch)
        {
            return Fail();
        }

        var claims = new List<Claim>
        {
            // Antiforgery binds tokens to this unique claim: one CSRF token per session, stable across grant changes.
            new(ClaimTypes.NameIdentifier, "gmsession:" + session.Key),
            new(ActorClaims.ActorType, ActorClaims.GmType),
            new(ActorClaims.ActorId, record.StaffId),
            new(SessionClaim, session.Key),
            new(DisplayNameClaim, record.DisplayName),
        };
        claims.AddRange(record.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Select(p => new Claim(ActorClaims.Permission, p)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    private AuthenticateResult Fail()
    {
        AuthenticationFailures.Report(Context, CoreErrorCodes.Unauthenticated);
        return AuthenticateResult.Fail(CoreErrorCodes.Unauthenticated.Name);
    }
}
