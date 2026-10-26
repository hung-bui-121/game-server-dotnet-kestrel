using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Auth;

/// <summary>
/// Header-based authentication for Development and tests only (research R13). Replaced by player sessions
/// (slice 002) and GM authentication (slice 004). The startup validator forbids it in Production.
/// </summary>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GameServerTest";
    public const string PlayerHeader = "X-Test-Player";
    public const string GmHeader = "X-Test-Gm";
    public const string GmPermissionsHeader = "X-Test-Gm-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var player = Request.Headers[PlayerHeader].ToString();
        var gm = Request.Headers[GmHeader].ToString();
        List<Claim> claims;
        if (!string.IsNullOrWhiteSpace(player))
        {
            claims = [new(ActorClaims.ActorType, ActorClaims.PlayerType), new(ActorClaims.ActorId, player)];
        }
        else if (!string.IsNullOrWhiteSpace(gm))
        {
            claims = [new(ActorClaims.ActorType, ActorClaims.GmType), new(ActorClaims.ActorId, gm)];
            var permissions = Request.Headers[GmPermissionsHeader].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            claims.AddRange(permissions.Select(p => new Claim(ActorClaims.Permission, p)));
        }
        else
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
