using System.Security.Claims;
using System.Text.Encodings.Web;
using GameServer.Errors;
using GameServer.Platform.Web.Auth;
using GameServer.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>
/// Player scheme <c>PlayerBearer</c> (SEC-02, IDN-09, IDN-10): <c>Authorization: Bearer</c> → session → profile checks in
/// the order deleted → suspended → replaced; unknown or expired token → UNAUTHENTICATED.
/// </summary>
public sealed class PlayerBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    ProfileCache profiles,
    IClock clock,
    IdentityMetrics metrics) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PlayerBearer";
    public const string SessionClaim = "gs:session";
    private const string Prefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[Prefix.Length..].Trim();
        if (token.Length is 0 or > 128)
        {
            return Fail(CoreErrorCodes.Unauthenticated);
        }

        var session = await sessions.ValidateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return Fail(CoreErrorCodes.Unauthenticated);
        }

        var profile = await profiles.GetAsync(session.PlayerId, Context.RequestAborted);
        if (AccountChecks.CheckAccess(profile, clock.UtcNow) is { } denial)
        {
            return Fail(denial.Code, denial.Details);
        }

        if (session.Epoch < profile.SessionEpoch)
        {
            metrics.SessionReplaced();
            return Fail(CoreErrorCodes.SessionReplaced);
        }

        await sessions.TouchIfDueAsync(session, Context.RequestAborted);

        var identity = new ClaimsIdentity(
            [
                new Claim(ActorClaims.ActorType, ActorClaims.PlayerType),
                new Claim(ActorClaims.ActorId, session.PlayerId),
                new Claim(SessionClaim, session.Key),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private AuthenticateResult Fail(ErrorCode code, IReadOnlyDictionary<string, string>? details = null)
    {
        AuthenticationFailures.Report(Context, code, details);
        return AuthenticateResult.Fail(code.Name);
    }
}
