using GameServer.Platform;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// Redirect targets after SSO (slice 018, FR-022..FR-025, architect A1). Never an absolute URL taken from input. A return path
/// is 1..512 printable ASCII characters and contains none of <c>//</c>, <c>\</c>, <c>..</c> or <c>%2e</c> in any case (browsers
/// resolve <c>%2e%2e</c>, <c>.%2e</c> and <c>%2e.</c> as <c>..</c>; review T024 M1). Then:
/// <list type="bullet">
/// <item>with a console (<c>Gm:Console:BaseUrl</c>): only <c>/</c> or <c>/#&lt;fragment&gt;</c>, the hash route the console
/// sends, so no path segment and no query reach the console's origin; success lands on <c>BaseUrl</c> + the value without its
/// leading <c>/</c>, every denial on <c>BaseUrl#/login?error=sso_denied</c>;</item>
/// <item>without a console: only paths under <c>/gm/</c>; the default is the Development page where development tools are on,
/// else <c>/gm/v1/me</c>; every denial lands on that default + <c>?error=sso_denied</c>.</item>
/// </list>
/// A missing or refused value lands on <c>BaseUrl</c>, or on the default without a console.
/// </summary>
public sealed class GmConsoleUrls(IOptions<GmOptions> options, GameServerOptions gameServer)
{
    public const string DevelopmentDefault = "/gm/v1/ui";
    public const string NoConsoleDefault = "/gm/v1/me";
    public const string DeniedQuery = "?error=sso_denied";
    public const string DeniedFragment = "#/login?error=sso_denied";

    private const int MaxReturnPathLength = 512;
    private const string LegacyPrefix = "/gm/";
    private const string ConsoleRoot = "/";
    private const string ConsoleHashPrefix = "/#";
    private const string EncodedDot = "%2e";

    /// <summary>The console root (validated at startup), or null when this deployment has no console.</summary>
    private string? BaseUrl => options.Value.Console.BaseUrl is { Length: > 0 } baseUrl ? baseUrl : null;

    /// <summary>Where a deployment without console lands: the GM page only exists with development tools (FR-030).</summary>
    private string Default => gameServer.AllowsDevelopmentTools ? DevelopmentDefault : NoConsoleDefault;

    /// <summary>The relative value kept in the OIDC properties: the accepted return path, or null.</summary>
    public string? AcceptReturnPath(string? returnUrl) =>
        returnUrl is { Length: > 0 and <= MaxReturnPathLength }
        && returnUrl.All(c => c is > ' ' and < (char)127)
        && !returnUrl.Contains("//", StringComparison.Ordinal)
        && !returnUrl.Contains('\\', StringComparison.Ordinal)
        && !returnUrl.Contains("..", StringComparison.Ordinal)
        && !returnUrl.Contains(EncodedDot, StringComparison.OrdinalIgnoreCase)
        && (BaseUrl is not null
            ? returnUrl == ConsoleRoot || returnUrl.StartsWith(ConsoleHashPrefix, StringComparison.Ordinal)
            : returnUrl.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            ? returnUrl
            : null;

    /// <summary>
    /// Where a successful sign-in lands: <c>BaseUrl</c> + the value without its leading <c>/</c> (string concatenation keeps a
    /// sub-path console), or the path itself without a console; the default when the value is missing or refused. The value is
    /// checked again, so a caller can never turn input into an absolute target.
    /// </summary>
    public string SuccessTarget(string? acceptedReturnPath)
    {
        var path = AcceptReturnPath(acceptedReturnPath);
        return BaseUrl is { } baseUrl
            ? path is null ? baseUrl : baseUrl + path[1..]
            : path ?? Default;
    }

    /// <summary>Where every denial lands; no reason in the URL (the security log keeps it).</summary>
    public string DeniedTarget() => BaseUrl is { } baseUrl ? baseUrl + DeniedFragment : Default + DeniedQuery;
}
