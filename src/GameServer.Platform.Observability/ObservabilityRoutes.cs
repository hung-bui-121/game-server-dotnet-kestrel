using GameServer.Actors;
using GameServer.Routing;

namespace GameServer.Platform.Observability;

/// <summary>Health probe routes of <see cref="ObservabilityModule"/> (FG-026, OBS-03): Public, unversioned, mapped by every host.</summary>
public static class ObservabilityRoutes
{
    /// <summary><c>/health/live</c>, any method (<c>MapHealthChecks</c>), runs no dependency check.</summary>
    public static RouteDeclaration Live { get; } =
        RouteDeclaration.AnyMethod("platform.health_live", ObservabilityModule.LiveRoute, SecurityProfile.Public, RouteSurface.Host);

    /// <summary><c>GET /health/ready</c>, runs the <c>ready</c> checks.</summary>
    public static RouteDeclaration Ready { get; } =
        RouteDeclaration.Get("platform.health_ready", ObservabilityModule.ReadyRoute, SecurityProfile.Public, RouteSurface.Host);
}
