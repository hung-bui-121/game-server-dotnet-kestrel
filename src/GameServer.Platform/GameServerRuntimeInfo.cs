using System.Reflection;

namespace GameServer.Platform;

/// <summary>
/// Framework version (FR-050..FR-052): declared once in the framework's <c>Directory.Build.props</c>
/// (<c>GameServerFrameworkVersion</c>) and stamped into every framework assembly, with <c>+{git sha}</c> in the informational
/// version when built from a git checkout.
/// </summary>
public static class GameServerVersion
{
    private const string Unknown = "unknown";

    /// <summary>Declared SemVer of the framework build files, e.g. <c>0.7.0</c>.</summary>
    public static string Framework { get; } = Of(typeof(GameServerVersion).Assembly);

    /// <summary>Informational version, e.g. <c>0.7.0+{sha}</c> (no suffix without git metadata).</summary>
    public static string FrameworkInformational { get; } = InformationalOf(typeof(GameServerVersion).Assembly);

    /// <summary>
    /// SemVer core of an assembly's informational version (the part before <c>+</c>), else <c>AssemblyName.Version</c> in
    /// three parts, else <c>unknown</c>.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (Informational(assembly) is { } informational)
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return ThreeParts(assembly) ?? Unknown;
    }

    /// <summary>The informational version of an assembly, else <c>AssemblyName.Version</c> in three parts, else <c>unknown</c>.</summary>
    public static string InformationalOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Informational(assembly) ?? ThreeParts(assembly) ?? Unknown;
    }

    private static string? Informational(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } value ? value : null;

    private static string? ThreeParts(Assembly assembly) =>
        assembly.GetName().Version is { } version ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0)).ToString(3) : null;
}

/// <summary>
/// Identity and versions of the running instance (FR-052..FR-055): one singleton per host, resolved by <c>AddGameServer</c>
/// and shown by the startup line, the telemetry resource and the GM ops version read.
/// </summary>
/// <param name="GameId"><c>GameServer:GameId</c>.</param>
/// <param name="EnvironmentId"><c>GameServer:EnvironmentId</c>.</param>
/// <param name="DeploymentEnvironment">Resolved deployment environment (Development, Test, Staging, Production).</param>
/// <param name="InstanceId">Instance id of this process, see <see cref="ResolveInstanceId(string?)"/>.</param>
/// <param name="GameAssembly">Name of the host (game) entry assembly, <c>unknown</c> when it cannot be found.</param>
/// <param name="GameVersion">Informational version of <paramref name="GameAssembly"/> (<see cref="GameServerVersion.InformationalOf"/>).</param>
/// <param name="FrameworkVersion"><see cref="GameServerVersion.FrameworkInformational"/>.</param>
public sealed record GameServerRuntimeInfo(
    string GameId,
    string EnvironmentId,
    string DeploymentEnvironment,
    string InstanceId,
    string GameAssembly,
    string GameVersion,
    string FrameworkVersion)
{
    /// <summary>
    /// The instance-id rule (LOG-03), its only implementation: the configured value (<c>Observability:InstanceId</c>), else
    /// <c>HOSTNAME</c> (stable pod name), else the machine name. Log file names, <c>service.instance.id</c>, analytics file
    /// names and the GM version read all use it.
    /// </summary>
    public static string ResolveInstanceId(string? configured) => ResolveInstanceId(configured, Environment.GetEnvironmentVariable);

    internal static string ResolveInstanceId(string? configured, Func<string, string?> environment) =>
        !string.IsNullOrWhiteSpace(configured) ? configured
        : environment("HOSTNAME") is { Length: > 0 } host ? host
        : Environment.MachineName;
}
