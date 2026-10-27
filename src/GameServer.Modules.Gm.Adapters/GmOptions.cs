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

    /// <summary>A session unused for this long ends (SEC-10); last activity is written lazily.</summary>
    public int IdleMinutes { get; set; } = 60;

    /// <summary>Per-instance cache of staff and sessions; bounds revocation delay (SEC-10: ≤ 60 s).</summary>
    public int CacheSeconds { get; set; } = 60;

    public GmBootstrapOptions Bootstrap { get; set; } = new();

    public GmOidcOptions Oidc { get; set; } = new();

    public GmCorsOptions Cors { get; set; } = new();
}

/// <summary>Section <c>Gm:Oidc</c>: company identity provider for GM sign-in (SEC-03, research R1/R2/R11).</summary>
public sealed class GmOidcOptions
{
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Secret provider only (OPS-08); a value in a configuration file fails startup outside Development/Test.</summary>
    public string? ClientSecret { get; set; }

    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Require an MFA method in the id_token <c>amr</c> claim; null = true outside Development/Test.</summary>
    public bool? RequireMfa { get; set; }

    public List<string> MfaAmrValues { get; set; } = ["mfa", "otp", "hwk", "swk", "sms", "fpt", "face"];

    /// <summary>
    /// Claim whose value must be <c>true</c> before an email invitation binds (default <c>email_verified</c>). Some IdPs
    /// (e.g. Microsoft Entra ID) do not send it by default: configure an optional claim, or invite by issuer + subject.
    /// </summary>
    public string EmailVerifiedClaim { get; set; } = "email_verified";

    /// <summary>First staff managers, created when the deployment has none (email invitation or issuer + subject).</summary>
    public List<GmBootstrapManager> BootstrapManagers { get; set; } = [];

    /// <summary>Back-channel HTTP handler for tests (fake identity provider); never set from configuration.</summary>
    public HttpMessageHandler? BackchannelHandler { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
}

public sealed class GmBootstrapManager
{
    public string? StaffId { get; set; }

    public string? Email { get; set; }

    public string? Issuer { get; set; }

    public string? Subject { get; set; }
}

/// <summary>Section <c>Gm:Cors</c>: off by default (same-origin, TBD-05a); exact origins only (SEC-06).</summary>
public sealed class GmCorsOptions
{
    public List<string> AllowedOrigins { get; set; } = [];

    public bool Enabled => AllowedOrigins.Count > 0;
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

        if (options.IdleMinutes is < 5 or > 480)
        {
            errors.Add("Gm:IdleMinutes must be 5..480.");
        }

        var oidc = options.Oidc;
        if (!gameServer.AllowsDevelopmentTools
            && (string.IsNullOrWhiteSpace(oidc.Authority) || string.IsNullOrWhiteSpace(oidc.ClientId) || string.IsNullOrWhiteSpace(oidc.ClientSecret)))
        {
            errors.Add(
                $"Gm:Oidc:Authority, ClientId and ClientSecret are required in '{gameServer.DeploymentEnvironment}': GM sign-in outside " +
                "Development/Test is SSO only (SEC-03). The client secret comes from the secret provider (Gm__Oidc__ClientSecret).");
        }

        if (oidc.IsConfigured && (!Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttps && !gameServer.AllowsDevelopmentTools)))
        {
            errors.Add("Gm:Oidc:Authority must be an absolute https URI.");
        }

        foreach (var manager in oidc.BootstrapManagers)
        {
            var hasEmail = !string.IsNullOrWhiteSpace(manager.Email);
            var hasIdentity = !string.IsNullOrWhiteSpace(manager.Issuer) && !string.IsNullOrWhiteSpace(manager.Subject);
            if (hasEmail == hasIdentity || manager.StaffId is null || !StaffIdPattern().IsMatch(manager.StaffId))
            {
                errors.Add("Gm:Oidc:BootstrapManagers entries need a valid staffId and either an email or issuer + subject.");
            }
        }

        errors.AddRange(GmCorsValidation.Validate(options.Cors, gameServer));

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
