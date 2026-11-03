using GameServer.Actors;
using GameServer.Routing;

namespace GameServer.Modules.Economy;

/// <summary>GM routes of the economy module (FG-026): resource adjustment (command) and ledger read.</summary>
public static class EconomyRoutes
{
    public const string AdjustTemplate = "/gm/v1/players/{playerId}/resources/adjust";
    public const string LedgerTemplate = "/gm/v1/players/{playerId}/ledger";

    public static RouteDeclaration Adjust { get; } = RouteDeclaration.Command(AdjustResourceHandler.Descriptor, AdjustTemplate);

    public static RouteDeclaration Ledger { get; } =
        RouteDeclaration.Get("economy.ledger", LedgerTemplate, SecurityProfile.Gm, RouteSurface.GmApi, EconomyPermissions.PlayerRead);

    public static IReadOnlyList<RouteDeclaration> All { get; } = [Adjust, Ledger];
}
