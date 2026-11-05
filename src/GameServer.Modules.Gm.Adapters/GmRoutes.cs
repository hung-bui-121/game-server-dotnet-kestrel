using GameServer.Actors;
using GameServer.Routing;

namespace GameServer.Modules.Gm;

/// <summary>
/// Routes of the GM module (FG-026): authentication, operations, players, receipts and staff. Permissions are the ones each
/// endpoint requires (SEC-07); the startup check proves the mapping agrees.
/// </summary>
public static class GmRoutes
{
    /// <summary>Hosting condition of the SSO routes: holds when <c>Gm:Oidc</c> is configured.</summary>
    public const string SsoCondition = "gm.sso";

    public static RouteDeclaration AuthCsrf { get; } = Get("gm.auth_csrf", GmModule.CsrfRoute, SecurityProfile.Public);

    /// <summary>Local accounts exist only where development tools are allowed (SEC-03).</summary>
    public static RouteDeclaration AuthLogin { get; } =
        RouteDeclaration.Post("gm.auth_login", GmModule.LoginRoute, SecurityProfile.Public, RouteSurface.GmApi, condition: RouteConditions.DevelopmentTools);

    public static RouteDeclaration AuthLogout { get; } = Post("gm.auth_logout", GmModule.LogoutRoute, GmPermissions.Access);

    public static RouteDeclaration SsoStart { get; } =
        RouteDeclaration.Get("gm.sso_start", GmSso.StartRoute, SecurityProfile.Public, RouteSurface.GmApi, condition: SsoCondition);

    public static RouteDeclaration SsoCallback { get; } =
        RouteDeclaration.Get("gm.sso_callback", GmSso.CallbackRoute, SecurityProfile.Public, RouteSurface.GmApi, condition: SsoCondition);

    public static RouteDeclaration Me { get; } = Get("gm.me", GmModule.MeRoute, SecurityProfile.Gm, GmPermissions.Access);

    /// <summary>Which sign-in methods a console shows (slice 017): Public, allowlisted through this declaration (SEC-09, V10).</summary>
    public static RouteDeclaration AuthMethods { get; } = Get("gm.auth_methods", GmModule.AuthMethodsRoute, SecurityProfile.Public);

    /// <summary>The GM document for a console, in every environment (slice 017): <c>gm.access</c>, ETag/304, not in the document.</summary>
    public static RouteDeclaration ConsoleCatalog { get; } =
        Get("gm.console_catalog", GmModule.ConsoleCatalogRoute, SecurityProfile.Gm, GmPermissions.Access);

    public static RouteDeclaration Ui { get; } = Get("gm.ui", GmModule.UiRoute, SecurityProfile.Public);

    public static RouteDeclaration Health { get; } = Get("gm.health", GmModule.HealthRoute, SecurityProfile.Gm, GmPermissions.OpsRead);

    public static RouteDeclaration OpsVersion { get; } = Get("gm.ops_version", GmModule.VersionRoute, SecurityProfile.Gm, GmPermissions.OpsRead);

    public static RouteDeclaration OpsRotateKeys { get; } = Post("gm.ops_rotate_keys", GmModule.RotateKeysRoute, GmPermissions.KeysRotate);

    public static RouteDeclaration OpsGate { get; } = Get("gm.ops_gate", GateEndpoints.GateRoute, SecurityProfile.Gm, GmPermissions.OpsRead);

    public static RouteDeclaration OpsGateMaintenance { get; } = Put("gm.ops_gate_maintenance", GateEndpoints.MaintenanceRoute, GmPermissions.OpsMaintenance);

    public static RouteDeclaration OpsGateMinClientVersion { get; } =
        Put("gm.ops_gate_min_client_version", GateEndpoints.MinClientVersionRoute, GmPermissions.OpsMaintenance);

    public static RouteDeclaration Player { get; } = Get("gm.player", GmPlayerEndpoints.PlayerRoute, SecurityProfile.Gm, GmPermissions.PlayerRead);

    public static RouteDeclaration PlayerByMapping { get; } =
        Get("gm.player_by_mapping", GmPlayerEndpoints.MappingRoute, SecurityProfile.Gm, GmPermissions.PlayerRead);

    public static RouteDeclaration PlayerSearch { get; } = Get("gm.player_search", GmPlayerEndpoints.SearchRoute, SecurityProfile.Gm, GmPermissions.PlayerRead);

    public static RouteDeclaration PlayerReceipts { get; } =
        Get("gm.player_receipts", GmPlayerEndpoints.ReceiptsRoute, SecurityProfile.Gm, GmPermissions.PlayerRead);

    public static RouteDeclaration PlayerAudit { get; } = Get("gm.player_audit", GmPlayerEndpoints.AuditRoute, SecurityProfile.Gm, GmPermissions.PlayerRead);

    public static RouteDeclaration Receipt { get; } = Get("gm.receipt", GmPlayerEndpoints.ReceiptRoute, SecurityProfile.Gm, GmPermissions.PlayerRead);

    public static RouteDeclaration StaffList { get; } = Get("gm.staff_list", StaffEndpoints.ListRoute, SecurityProfile.Gm, GmPermissions.StaffRead);

    public static RouteDeclaration StaffCreate { get; } = Post("gm.staff_create", StaffEndpoints.ListRoute, GmPermissions.StaffManage);

    public static RouteDeclaration Staff { get; } = Get("gm.staff", StaffEndpoints.StaffRoute, SecurityProfile.Gm, GmPermissions.StaffRead);

    public static RouteDeclaration StaffAudit { get; } = Get("gm.staff_audit", StaffEndpoints.AuditRoute, SecurityProfile.Gm, GmPermissions.StaffRead);

    public static RouteDeclaration StaffGrant { get; } = Put("gm.staff_grant", StaffEndpoints.GrantRoute, GmPermissions.StaffManage);

    public static RouteDeclaration StaffDisable { get; } = Post("gm.staff_disable", StaffEndpoints.DisableRoute, GmPermissions.StaffManage);

    public static RouteDeclaration StaffEnable { get; } = Post("gm.staff_enable", StaffEndpoints.EnableRoute, GmPermissions.StaffManage);

    /// <summary>Revoking one's own sessions needs only <see cref="GmPermissions.Access"/>; the handler checks the target.</summary>
    public static RouteDeclaration StaffRevokeSessions { get; } = Post("gm.staff_revoke_sessions", StaffEndpoints.RevokeSessionsRoute, GmPermissions.Access);

    public static IReadOnlyList<RouteDeclaration> All { get; } =
    [
        AuthCsrf, AuthLogin, AuthLogout, SsoStart, SsoCallback, Me, AuthMethods, ConsoleCatalog, Ui, Health, OpsVersion, OpsRotateKeys,
        OpsGate, OpsGateMaintenance, OpsGateMinClientVersion,
        Player, PlayerByMapping, PlayerSearch, PlayerReceipts, PlayerAudit, Receipt,
        StaffList, StaffCreate, Staff, StaffAudit, StaffGrant, StaffDisable, StaffEnable, StaffRevokeSessions,
    ];

    private static RouteDeclaration Get(string name, string template, SecurityProfile profile, string? permission = null) =>
        RouteDeclaration.Get(name, template, profile, RouteSurface.GmApi, permission);

    private static RouteDeclaration Post(string name, string template, string permission) =>
        RouteDeclaration.Post(name, template, SecurityProfile.Gm, RouteSurface.GmApi, permission);

    private static RouteDeclaration Put(string name, string template, string permission) =>
        RouteDeclaration.Put(name, template, SecurityProfile.Gm, RouteSurface.GmApi, permission);
}
