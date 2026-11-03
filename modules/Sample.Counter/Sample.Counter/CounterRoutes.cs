using GameServer.Actors;
using GameServer.Routing;

namespace Sample.Counter;

/// <summary>Routes of the sample counter module (FG-026), declared next to its commands; the adapter maps from them.</summary>
public static class CounterRoutes
{
    public static RouteDeclaration Increment { get; } = RouteDeclaration.Command(IncrementHandler.Descriptor, "/api/v1/counter/increment");

    /// <summary>Query: the caller's counter value and revision.</summary>
    public static RouteDeclaration Get { get; } = RouteDeclaration.Post("counter.get", "/api/v1/counter/get", SecurityProfile.Player);

    /// <summary>GM read of a player's counter.</summary>
    public static RouteDeclaration GmRead { get; } = RouteDeclaration.Get(
        "counter.gm_read",
        "/gm/v1/counter/{playerId}",
        SecurityProfile.Gm,
        RouteSurface.GmApi,
        CounterModuleDefinition.ReadPermission);

    public static IReadOnlyList<RouteDeclaration> All { get; } = [Increment, Get, GmRead];
}
