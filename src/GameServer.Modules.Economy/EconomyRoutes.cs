using GameServer.Actors;
using GameServer.Routing;

namespace GameServer.Modules.Economy;

/// <summary>GM routes of the economy module (FG-026): resource adjustment (command), ledger read and resource options.</summary>
public static class EconomyRoutes
{
    public const string AdjustTemplate = "/gm/v1/players/{playerId}/resources/adjust";
    public const string LedgerTemplate = "/gm/v1/players/{playerId}/ledger";
    public const string ResourceOptionsTemplate = "/gm/v1/economy/resources";

    public static RouteDeclaration Adjust { get; } = RouteDeclaration.Command(AdjustResourceHandler.Descriptor, AdjustTemplate);

    public static RouteDeclaration Ledger { get; } =
        RouteDeclaration.Get("economy.ledger", LedgerTemplate, SecurityProfile.Gm, RouteSurface.GmApi, EconomyPermissions.PlayerRead);

    /// <summary>
    /// The resource catalog as GM options (slice 017): value = resource id, label = display name. Permission <c>gm.access</c>:
    /// an option route needs <c>gm.access</c> or the permission of the command it serves (startup check, review r1 m2).
    /// </summary>
    public static RouteDeclaration ResourceOptions { get; } =
        RouteDeclaration.Get("economy.resource_options", ResourceOptionsTemplate, SecurityProfile.Gm, RouteSurface.GmApi, EconomyPermissions.GmAccess);

    public static IReadOnlyList<RouteDeclaration> All { get; } = [Adjust, Ledger, ResourceOptions];
}
