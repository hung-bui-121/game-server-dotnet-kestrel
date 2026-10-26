using System.Security.Claims;
using GameServer.Actors;
using GameServer.Errors;
using GameServer.Platform.Web.Middleware;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Auth;

/// <summary>Claim types every authentication scheme of the framework must emit.</summary>
public static class ActorClaims
{
    public const string ActorType = "gs:actor_type";
    public const string ActorId = "gs:actor_id";
    public const string Permission = "gs:permission";

    public const string PlayerType = "player";
    public const string GmType = "gm";

    public static ActorType? GetActorType(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirstValue(ActorType) switch
            {
                PlayerType => GameServer.Actors.ActorType.Player,
                GmType => GameServer.Actors.ActorType.Gm,
                _ => null,
            }
            : null;
}

/// <summary>
/// Builds <see cref="ActorContext"/> from the authenticated principal and deployment config only;
/// identity in payloads or query strings is never used (API-12, ARC-10).
/// </summary>
public sealed class ActorContextAccessor(GameServerOptions options)
{
    public ActorContext GetActor(HttpContext context)
    {
        var type = ActorClaims.GetActorType(context.User)
            ?? throw new GameServerException(CoreErrorCodes.Unauthenticated, "No authenticated actor.");
        var actorId = context.User.FindFirstValue(ActorClaims.ActorId)
            ?? throw new GameServerException(CoreErrorCodes.Unauthenticated, "Actor id claim missing.");
        var permissions = context.User.FindAll(ActorClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        return new ActorContext(
            actorId,
            type,
            type == ActorType.Player ? actorId : null,
            options.GameId,
            options.EnvironmentId,
            permissions,
            CorrelationMiddleware.GetCorrelationId(context));
    }
}
