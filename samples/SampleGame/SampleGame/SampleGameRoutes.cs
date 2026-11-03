using GameServer.Routing;

namespace SampleGame;

/// <summary>Routes of the sample game module (FG-026): battle and shop commands, GM progression edit.</summary>
public static class SampleGameRoutes
{
    public const string StartTemplate = "/api/v1/battle/start";
    public const string EndTemplate = "/api/v1/battle/end";
    public const string BuyTemplate = "/api/v1/shop/buy";
    public const string GmProgressionTemplate = "/gm/v1/players/{playerId}/progression";

    public static RouteDeclaration BattleStart { get; } = RouteDeclaration.Command(StartBattleHandler.Descriptor, StartTemplate);

    public static RouteDeclaration BattleEnd { get; } = RouteDeclaration.Command(EndBattleHandler.Descriptor, EndTemplate);

    public static RouteDeclaration ShopBuy { get; } = RouteDeclaration.Command(BuyItemHandler.Descriptor, BuyTemplate);

    public static RouteDeclaration ProgressionSet { get; } = RouteDeclaration.Command(SetProgressionHandler.Descriptor, GmProgressionTemplate);

    public static IReadOnlyList<RouteDeclaration> All { get; } = [BattleStart, BattleEnd, ShopBuy, ProgressionSet];
}
