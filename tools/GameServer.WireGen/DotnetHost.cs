namespace GameServer.WireGen;

/// <summary>
/// The <c>dotnet</c> that runs the host export and the version evaluation (V15): <c>--dotnet &lt;path&gt;</c>, else
/// <c>DOTNET_HOST_PATH</c>, else the <c>dotnet</c> running this tool, else <c>DOTNET_ROOT/dotnet</c>, else <c>dotnet</c> on PATH.
/// </summary>
internal static class DotnetHost
{
    public static string Resolve(string? explicitPath) => Resolve(explicitPath, Environment.GetEnvironmentVariable, Environment.ProcessPath);

    internal static string Resolve(string? explicitPath, Func<string, string?> environment, string? processPath)
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (explicitPath is not null)
        {
            return File.Exists(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : throw new WireGenException($"[dotnet] --dotnet '{explicitPath}' does not exist. Fix: pass the path of the dotnet executable.");
        }

        if (environment("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
        {
            return hostPath;
        }

        if (processPath is not null && string.Equals(Path.GetFileName(processPath), exe, StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        if (environment("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        var onPath = (environment("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), exe))
            .FirstOrDefault(File.Exists);
        return onPath ?? throw new WireGenException(
            "[dotnet] no dotnet executable found (DOTNET_HOST_PATH, DOTNET_ROOT, PATH). Fix: pass --dotnet <path of dotnet>.");
    }
}
