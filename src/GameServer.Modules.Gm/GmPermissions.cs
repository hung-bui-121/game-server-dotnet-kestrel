namespace GameServer.Modules.Gm;

/// <summary>Permissions owned by the GM module (SEC-07 catalog). Other modules declare their own (e.g. <c>resource.adjust</c>).</summary>
public static class GmPermissions
{
    /// <summary>Required by every GM endpoint that is not more specific (session, profile of the staff).</summary>
    public const string Access = "gm.access";

    public const string PlayerRead = "player.read";

    /// <summary>Shows identity mapping keys (PII, GM-02).</summary>
    public const string PlayerReadPii = "player.read.pii";

    /// <summary>Operational details: per-check health (OBS-03).</summary>
    public const string OpsRead = "ops.read";

    /// <summary>List and view staff and their audit (SEC-15).</summary>
    public const string StaffRead = "staff.read";

    /// <summary>Create staff, change grants, disable/enable, revoke sessions (SEC-10, SEC-15).</summary>
    public const string StaffManage = "staff.manage";

    /// <summary>Schedule a new Data Protection key (OPS-04).</summary>
    public const string KeysRotate = "ops.keys.rotate";

    public static IReadOnlyList<string> All { get; } = [Access, PlayerRead, PlayerReadPii, OpsRead, StaffRead, StaffManage, KeysRotate];
}
