using GameServer.Routing;

namespace GameServer.Modules.Economy;

/// <summary>Route of the development cheat module (FG-026, OPS-09): Dev profile, Development/Test only.</summary>
public static class CheatRoutes
{
    public const string GrantTemplate = "/api/v1/cheat/grant";

    public static RouteDeclaration Grant { get; } = RouteDeclaration.Command(CheatGrantHandler.Descriptor, GrantTemplate);
}
