using System.Text.RegularExpressions;
using GameServer.Platform;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>Section <c>Gm</c>.</summary>
public sealed class GmOptions
{
    public const string SectionName = "Gm";

    /// <summary>Lifetime of a GM cookie session (research R5).</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>Per-instance cache of staff and sessions; bounds revocation delay (SEC-10: ≤ 60 s).</summary>
    public int CacheSeconds { get; set; } = 60;

    public GmBootstrapOptions Bootstrap { get; set; } = new();
}

/// <summary>Development/Test seed of local staff accounts (FR-004). Refused in other environments.</summary>
public sealed class GmBootstrapOptions
{
    public List<GmBootstrapStaff> Staff { get; set; } = [];
}

public sealed class GmBootstrapStaff
{
    public string StaffId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Development password; hashed with PBKDF2 before storage, never logged.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Role templates expanded into permissions at seed time (SEC-15).</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Extra permissions (must exist in the catalog).</summary>
    public List<string> Permissions { get; set; } = [];

    public bool Disabled { get; set; }

    /// <summary>Scope of the grant; defaults to this deployment. Another value seeds a grant this deployment ignores.</summary>
    public string? GameId { get; set; }

    public string? EnvironmentId { get; set; }
}

public sealed partial class GmOptionsValidator(GameServerOptions gameServer) : IValidateOptions<GmOptions>
{
    public ValidateOptionsResult Validate(string? name, GmOptions options)
    {
        var errors = new List<string>();
        if (options.SessionHours is < 1 or > 24)
        {
            errors.Add("Gm:SessionHours must be 1..24.");
        }

        if (options.CacheSeconds is < 0 or > 60)
        {
            errors.Add("Gm:CacheSeconds must be 0..60 (SEC-10).");
        }

        if (options.Bootstrap.Staff.Count > 0 && !gameServer.AllowsDevelopmentTools)
        {
            errors.Add($"Gm:Bootstrap:Staff is set in '{gameServer.DeploymentEnvironment}'. Local GM accounts exist only in Development/Test (SEC-03).");
        }

        foreach (var staff in options.Bootstrap.Staff)
        {
            if (!StaffIdPattern().IsMatch(staff.StaffId))
            {
                errors.Add($"Gm:Bootstrap:Staff staffId '{staff.StaffId}' must be 1..64 of [a-z0-9._-].");
            }

            if (string.IsNullOrEmpty(staff.Password))
            {
                errors.Add($"Gm:Bootstrap:Staff '{staff.StaffId}' has no password.");
            }

            errors.AddRange(staff.Roles.Where(r => !GmRoles.Names.Contains(r, StringComparer.Ordinal))
                .Select(r => $"Gm:Bootstrap:Staff '{staff.StaffId}' has unknown role '{r}'. Known: {string.Join(", ", GmRoles.Names)}."));
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    internal static partial Regex StaffIdPattern();
}
