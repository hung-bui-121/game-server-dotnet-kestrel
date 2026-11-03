using System.Diagnostics;
using System.Text;

namespace GameServer.Architecture.Tests;

/// <summary>Repository paths and child processes for the repository and tooling tests (build seal, run-local).</summary>
internal static class RepositoryTooling
{
    public static string Root { get; } = FindRoot();

    /// <summary><c>DOTNET_HOST_PATH</c> (set by the dotnet CLI) → <c>DOTNET_ROOT/dotnet</c> → <c>dotnet</c> on PATH; null when none exists.</summary>
    public static string? Dotnet()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
        {
            return hostPath;
        }

        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        return FindOnPath(exe);
    }

    /// <summary>The first <paramref name="exe"/> of the PATH folders, or null.</summary>
    public static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), exe))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Runs <paramref name="fileName"/> to completion with captured output. MSBuild variables of the calling <c>dotnet test</c>
    /// are removed so a child <c>dotnet</c> resolves its own SDK from the working directory's <c>global.json</c>.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(fileName + " did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"{Path.GetFileName(fileName)} {string.Join(' ', start.ArgumentList)} did not finish within {timeout}.");
        }

        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("GameServer.slnx not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Exit code and captured output of one child process.</summary>
internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string All => Output + Error;
}
