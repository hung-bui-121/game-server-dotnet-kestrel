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

    public GmCookieOptions Cookies { get; set; } = new();

    public GmConsoleOptions Console { get; set; } = new();
}

/// <summary>Section <c>Gm:Cookies</c> (slice 018): <c>SameSite</c> of the session and CSRF cookies, independent of CORS.</summary>
public sealed class GmCookieOptions
{
    /// <summary>
    /// <c>Lax</c> (default: a console on the same site as the API) or <c>None</c> (Development/Test only, needs
    /// <c>Gm:Cors:AllowedOrigins</c>; the cookies are then always <c>Secure</c>). Case-insensitive; any other value, and <c>None</c>
    /// elsewhere (case B is not supported), fails startup.
    /// </summary>
    public string SameSite { get; set; } = "Lax";

    /// <summary>True for <c>None</c>; false for <c>Lax</c> and for a refused value (startup fails on it first).</summary>
    internal bool IsNone => string.Equals(SameSite, "None", StringComparison.OrdinalIgnoreCase);

    internal bool IsSupported => IsNone || string.Equals(SameSite, "Lax", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Section <c>Gm:Console</c> (slice 018): where the GM console of this environment lives.</summary>
public sealed class GmConsoleOptions
{
    /// <summary>
    /// Absolute URL of the console root, path ending with <c>/</c>, without query, fragment or user info; <c>https</c> outside
    /// Development; its origin is the one in <c>Gm:Cors:AllowedOrigins</c>. Required together with a CORS origin; unset with
    /// none (no console: SSO lands on the API). SSO sign-ins return here (FR-020..FR-025).
    /// </summary>
    public string? BaseUrl { get; set; }
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
        if (!options.Cookies.IsSupported)
        {
            errors.Add($"Gm:Cookies:SameSite '{options.Cookies.SameSite}' is not supported; use Lax (default) or None.");
        }
        else if (options.Cookies.IsNone && !gameServer.AllowsDevelopmentTools)
        {
            // Case B (a console on another site) is not supported (owner decision; review T024 m2).
            errors.Add($"Gm:Cookies:SameSite=None is refused in '{gameServer.DeploymentEnvironment}': a console on another site (case B) " +
                "is not supported; use Lax (default) with a console on the same site.");
        }
        else if (options.Cookies.IsNone && !options.Cors.Enabled)
        {
            errors.Add("Gm:Cookies:SameSite=None needs Gm:Cors:AllowedOrigins: SameSite=None is only for a console on another site (case B, unsupported).");
        }

        errors.AddRange(ValidateConsole(options, gameServer));

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

    /// <summary>
    /// <c>Gm:Console:BaseUrl</c> (FR-020, FR-021): set together with the CORS console origin, never alone; a console URL is the
    /// root of a static site on that origin. Neither setting = no console, valid in every environment.
    /// </summary>
    private static IEnumerable<string> ValidateConsole(GmOptions options, GameServerOptions gameServer)
    {
        var baseUrl = options.Console.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            if (options.Cors.Enabled)
            {
                yield return "Gm:Cors:AllowedOrigins is set but Gm:Console:BaseUrl is not; set the console URL of that origin (SSO returns there).";
            }

            yield break;
        }

        // Printable ASCII only: the value is written verbatim into redirect Location headers (FR-022 applies the same rule to paths).
        if (!baseUrl.All(c => c is > ' ' and < (char)127)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !baseUrl.EndsWith('/')
            || baseUrl.Contains('?', StringComparison.Ordinal)
            || baseUrl.Contains('#', StringComparison.Ordinal)
            || uri.UserInfo.Length > 0)
        {
            yield return $"Gm:Console:BaseUrl '{baseUrl}' must be an absolute http(s) URL whose path ends with '/', without query, fragment or user info.";
            yield break;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !gameServer.IsDevelopment)
        {
            yield return $"Gm:Console:BaseUrl '{baseUrl}' is not https; plain http is allowed only in Development.";
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!options.Cors.AllowedOrigins.Any(o => Uri.TryCreate(o, UriKind.Absolute, out var allowed)
            && string.Equals(allowed.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)))
        {
            yield return $"Gm:Console:BaseUrl origin '{origin}' is not in Gm:Cors:AllowedOrigins; add it (one console origin per environment).";
        }
    }
}
