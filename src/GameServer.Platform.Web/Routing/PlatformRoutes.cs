using GameServer.Actors;
using GameServer.Routing;

namespace GameServer.Platform.Web.Routing;

/// <summary>Routes the platform maps itself (owner "platform"). Consumers read them instead of copying strings (FG-026 (4)).</summary>
public static class PlatformRoutes
{
    internal const string TimeTemplate = "/v1/time";
    internal const string SnapshotTemplate = "/api/v1/player/snapshot";
    internal const string ConfigTemplate = "/api/v1/config/get";
    internal const string ReceiptTemplate = "/api/v1/player/receipts/{receiptId}";

    /// <summary>Server time (TIME-01), Public, mapped by every host.</summary>
    public static RouteDeclaration Time { get; } = RouteDeclaration.Get("platform.time", TimeTemplate, SecurityProfile.Public, RouteSurface.Host);

    /// <summary>Player snapshot (PLY-02, API-10).</summary>
    public static RouteDeclaration Snapshot { get; } = RouteDeclaration.Post("platform.snapshot", SnapshotTemplate, SecurityProfile.Player);

    /// <summary>Client-visible game config (CFG-01).</summary>
    public static RouteDeclaration Config { get; } = RouteDeclaration.Post("platform.config", ConfigTemplate, SecurityProfile.Player);

    /// <summary>Receipt lookup of the caller's own commands (CMD-09).</summary>
    public static RouteDeclaration Receipt { get; } = RouteDeclaration.Get("platform.receipt", ReceiptTemplate, SecurityProfile.Player);

    /// <summary>GM OpenAPI document: mapped with the GM API where development tools are allowed (Development/Test).</summary>
    public static RouteDeclaration GmOpenApi { get; } = RouteDeclaration.Get(
        "platform.gm_openapi",
        OpenApi.GmOpenApi.Route,
        SecurityProfile.Public,
        RouteSurface.GmApi,
        condition: RouteConditions.DevelopmentTools);

    public static IReadOnlyList<RouteDeclaration> All { get; } = [Time, Snapshot, Config, Receipt, GmOpenApi];
}
