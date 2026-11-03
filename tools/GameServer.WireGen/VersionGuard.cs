using System.Text.Json;

namespace GameServer.WireGen;

/// <summary>
/// Step 5 of contracts/wiregen-cli.md §4 (FR-022, research R12, plan N3): the export comes from the generator's framework version
/// and the host's <c>Google.Protobuf</c> is the pinned one; with <c>catalog.hostProject</c>, the host's central package versions
/// of <c>Grpc.Tools</c> and <c>Google.Protobuf</c> are the pinned ones too. Each message names both versions and the side to change.
/// </summary>
internal static class VersionGuard
{
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<string> CheckExport(WireContract contract, ToolInfo tool)
    {
        var errors = new List<string>();
        if (!string.Equals(contract.FrameworkVersion, tool.FrameworkVersion, StringComparison.Ordinal))
        {
            errors.Add($"[version] catalog exported by framework {contract.FrameworkVersion}, generator is {tool.FrameworkVersion}. Fix: build the host " +
                "against the same framework checkout as the generator (FR-022).");
        }

        if (!string.Equals(contract.ProtobufVersion, tool.ProtobufVersion, StringComparison.Ordinal))
        {
            errors.Add($"[version] the host resolves Google.Protobuf {contract.ProtobufVersion}, the Unity package ships {tool.ProtobufVersion}. Fix: " +
                $"use the framework's pinned Google.Protobuf {tool.ProtobufVersion} in the host (remove its own version or VersionOverride) (FR-022).");
        }

        return errors;
    }

    /// <summary>
    /// <c>dotnet msbuild &lt;host&gt; -getItem:PackageVersion</c> (V10). A host without central package management has no items:
    /// <paramref name="note"/> tells so and only the export checks apply.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckHostPackagesAsync(string dotnet, string hostProject, ToolInfo tool, Action<string> note)
    {
        var workingDirectory = Directory.Exists(hostProject) ? hostProject : Path.GetDirectoryName(hostProject)!;
        var result = await ChildProcess.RunAsync(dotnet, ["msbuild", hostProject, "-getItem:PackageVersion"], workingDirectory, EvaluationTimeout);
        if (result.ExitCode != 0)
        {
            throw new WireGenException(
                new[] { $"[version] evaluating the package versions of {hostProject} failed with code {result.ExitCode}:" }
                    .Concat(result.FailureLines().Select(line => "  " + line))
                    .Append("Fix: make the host project evaluate (dotnet msbuild <host> -getItem:PackageVersion).")
                    .ToArray());
        }

        return CheckPackageVersions(result.Output, hostProject, tool, note);
    }

    internal static IReadOnlyList<string> CheckPackageVersions(string msbuildJson, string hostProject, ToolInfo tool, Action<string> note)
    {
        Dictionary<string, string> versions;
        try
        {
            using var document = JsonDocument.Parse(msbuildJson);
            versions = document.RootElement.GetProperty("Items").GetProperty("PackageVersion").EnumerateArray()
                .Where(item => item.TryGetProperty("Identity", out _) && item.TryGetProperty("Version", out _))
                .GroupBy(item => item.GetProperty("Identity").GetString()!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().GetProperty("Version").GetString()!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new WireGenException($"[version] the package versions of {hostProject} cannot be read ({ex.Message}). Fix: check the host project.");
        }

        if (versions.Count == 0)
        {
            note($"note: {hostProject} has no central package versions: its Grpc.Tools version is not checked (Google.Protobuf is checked through the export).");
            return [];
        }

        var errors = new List<string>();
        Compare(versions, "Grpc.Tools", tool.GrpcToolsVersion, hostProject, errors);
        Compare(versions, "Google.Protobuf", tool.ProtobufVersion, hostProject, errors);
        return errors;
    }

    private static void Compare(Dictionary<string, string> versions, string package, string pinned, string hostProject, List<string> errors)
    {
        if (versions.TryGetValue(package, out var version) && !string.Equals(version, pinned, StringComparison.Ordinal))
        {
            errors.Add($"[version] {hostProject} resolves {package} {version}, the framework pins {pinned}. Fix: use the framework's " +
                $"Directory.Packages.props version (remove the game's own PackageVersion of {package}) (FR-022).");
        }
    }
}
