using GameServer.Errors;

namespace GameServer.Modules.Gm;

/// <summary>Error codes of the GM module (range 13000–13099, API-11).</summary>
public static class GmErrorCodes
{
    /// <summary>The staffId or the invited email is already used.</summary>
    public static readonly ErrorCode StaffExists = new(13000, "STAFF_EXISTS", 409, false, "The staff member or invitation already exists.");

    /// <summary>The change would leave the deployment without an active staff member holding <c>staff.manage</c>.</summary>
    public static readonly ErrorCode LastStaffManager = new(13001, "LAST_STAFF_MANAGER", 409, false, "The last staff manager cannot be disabled or downgraded.");

    /// <summary>A staff manager tried to grant a permission they do not hold themselves.</summary>
    public static readonly ErrorCode PermissionNotGrantable = new(13002, "PERMISSION_NOT_GRANTABLE", 403, false, "You cannot grant a permission you do not hold.");

    public static ErrorCodeSet All { get; } = new(13000, 13099, StaffExists, LastStaffManager, PermissionNotGrantable);
}
