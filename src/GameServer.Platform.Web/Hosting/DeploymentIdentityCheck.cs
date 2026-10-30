using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GameServer.Platform.Web.Hosting;

/// <summary>How <c>GameServer:DeploymentEnvironment</c> was resolved for this host (research R2).</summary>
/// <param name="ConfiguredValue">The configured value as written; null when absent.</param>
/// <param name="HostEnvironmentName">The host environment name (the flavor).</param>
/// <param name="Effective">Canonical value when valid; the raw value otherwise (never Development/Test, so it fails closed).</param>
/// <param name="IsValid">Whether <paramref name="Effective"/> is a deployment environment.</param>
/// <param name="FlavorFileFound">Whether <c>appsettings.{HostEnvironmentName}.json</c> exists as a configuration source.</param>
/// <param name="ContentRoot">Content root of the host, for the message.</param>
internal sealed record DeploymentEnvironmentResolution(
    string? ConfiguredValue,
    string HostEnvironmentName,
    string Effective,
    bool IsValid,
    bool FlavorFileFound,
    string ContentRoot)
{
    private const string Key = GameServerOptions.SectionName + ":DeploymentEnvironment";

    public string FlavorFileName => $"appsettings.{HostEnvironmentName}.json";

    public static DeploymentEnvironmentResolution Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[Key];
        var hostName = environment.EnvironmentName;
        var found = FlavorFileExists(configuration, $"appsettings.{hostName}.json");
        if (configured is not null)
        {
            return DeploymentEnvironments.TryNormalize(configured, out var normalized)
                ? new(configured, hostName, normalized, true, found, environment.ContentRootPath)
                : new(configured, hostName, configured, false, found, environment.ContentRootPath);
        }

        // Without a configured value, only a host name that is itself a deployment environment is accepted (no silent fallback for Local/Dev).
        return DeploymentEnvironments.TryNormalize(hostName, out var fromHost)
            ? new(null, hostName, fromHost, true, found, environment.ContentRootPath)
            : new(null, hostName, hostName, false, found, environment.ContentRootPath);
    }

    private static bool FlavorFileExists(IConfiguration configuration, string fileName) =>
        configuration is IConfigurationBuilder builder
        && builder.Sources.OfType<FileConfigurationSource>().Any(source =>
            string.Equals(source.Path, fileName, StringComparison.Ordinal) && source.FileProvider?.GetFileInfo(fileName).Exists == true);
}

/// <summary>
/// FR-003 (the deployment environment is valid) and FR-004 (a standard environment id matches its flavor's deployment
/// environment). <c>Test</c> is not exempt: test hosts use the environment id <c>test</c>, which is not a standard flavor.
/// </summary>
internal sealed class DeploymentIdentityCheck(DeploymentEnvironmentResolution resolution, GameServerOptions options) : IStartupCheck
{
    public void Check(StartupCheckResult result)
    {
        var allowed = string.Join(", ", DeploymentEnvironments.All);
        var fix =
            $"Fix: set GameServer:DeploymentEnvironment in {resolution.FlavorFileName} (found: {(resolution.FlavorFileFound ? "yes" : "no")}, " +
            $"content root {resolution.ContentRoot}) or through the environment. Standard flavors: {StandardFlavors.Describe()}.";
        if (!resolution.IsValid)
        {
            result.Errors.Add(resolution.ConfiguredValue is null
                ? $"[deployment] GameServer:DeploymentEnvironment is not set and the host environment name '{resolution.HostEnvironmentName}' " +
                  $"is not a deployment environment (allowed: {allowed}). {fix}"
                : $"[deployment] GameServer:DeploymentEnvironment '{resolution.ConfiguredValue}' is not a deployment environment " +
                  $"(allowed: {allowed}). {fix}");
            return;
        }

        if (StandardFlavors.DeploymentEnvironmentById.TryGetValue(options.EnvironmentId, out var expected)
            && !string.Equals(expected, resolution.Effective, StringComparison.Ordinal))
        {
            result.Errors.Add(
                $"[deployment] GameServer:EnvironmentId '{options.EnvironmentId}' is a standard flavor that requires " +
                $"GameServer:DeploymentEnvironment={expected}, but it is '{resolution.Effective}'. Fix: correct one of the two keys " +
                $"(host environment '{resolution.HostEnvironmentName}'). Standard flavors: {StandardFlavors.Describe()}.");
        }
    }
}
