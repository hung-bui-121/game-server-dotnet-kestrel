namespace GameServer.Actors;

public enum ActorType
{
    Player,
    Gm,
    System,
}

/// <summary>Security profile every endpoint must declare (API-02, SEC-08).</summary>
public enum SecurityProfile
{
    Public,
    Player,
    Gm,
    Dev,
}

/// <summary>
/// Identity and scope of the caller. GameId/EnvironmentId always come from deployment config,
/// never from claims, routes or bodies (ARC-10, API-12).
/// </summary>
/// <param name="ActorId">Player id or GM staff id.</param>
/// <param name="ActorType">Kind of actor.</param>
/// <param name="PlayerId">Player whose aggregates the request targets (the actor itself for players).</param>
/// <param name="GameId">Deployment game id.</param>
/// <param name="EnvironmentId">Deployment logical environment id.</param>
/// <param name="Permissions">Expanded GM permissions; empty for players.</param>
/// <param name="CorrelationId">Request correlation id (X-Request-Id).</param>
public sealed record ActorContext(
    string ActorId,
    ActorType ActorType,
    string? PlayerId,
    string GameId,
    string EnvironmentId,
    IReadOnlySet<string> Permissions,
    string CorrelationId)
{
    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
