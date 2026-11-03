using GameServer.Routing;

namespace GameServer.Modules.Iap;

/// <summary>Route of the IAP module (FG-026): the purchase command (receipt verified before the runner, IAP-01).</summary>
public static class IapRoutes
{
    public const string PurchaseTemplate = "/api/v1/iap/purchase";

    public static RouteDeclaration Purchase { get; } = RouteDeclaration.Command(IapPurchaseHandler.Descriptor, PurchaseTemplate);
}
