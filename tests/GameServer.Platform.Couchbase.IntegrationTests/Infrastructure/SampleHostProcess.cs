using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real sample host binary (<c>SampleHost.dll</c> of the test output, content root = test output) to its exit, so a
/// test sees the exit code and the stdout/stderr contract of contracts/operations-cli.md exactly as a pipeline does.
/// </summary>
internal static class SampleHostProcess
{
    /// <summary>Start-up and report time of the process on top of its own provisioning bound.</summary>
    private const int ExitMarginSeconds = 60;

    /// <summary>
    /// Longer than the host's own bound (<see cref="IntegrationCluster.ProvisioningTimeoutSeconds"/>), so a slow provisioning run
    /// ends with its own exit code instead of being killed.
    /// </summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(IntegrationCluster.ProvisioningTimeoutSeconds + ExitMarginSeconds);

    /// <summary>
    /// Starts the host with flavor <paramref name="flavor"/>, <paramref name="settings"/> as environment variables
    /// (<c>Section__Key</c>, like a deployment) and <paramref name="args"/>; kills it after <paramref name="timeout"/>.
    /// </summary>
    public static async Task<HostRun> RunAsync(
        string flavor,
        IReadOnlyDictionary<string, string?> settings,
        IEnumerable<string> args,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SampleHost.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Configuration inherited from this machine (a developer's Couchbase__Password, ASPNETCORE_URLS, …) must not leak in.
        foreach (var key in start.Environment.Keys.Where(IsHostConfiguration).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = flavor;

        // Slice 012: analytics files would go to the test output with the machine name as instance id (parallel runs share
        // that lock); off unless a test sets them.
        start.Environment["GameServer__Analytics__Enabled"] = "false";
        foreach (var (key, value) in settings)
        {
            start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"The sample host did not exit in {(timeout ?? DefaultTimeout).TotalSeconds} s:{Environment.NewLine}{await output}{await error}");
        }

        return new HostRun(process.ExitCode, await output, await error);
    }

    private static bool IsHostConfiguration(string key) =>
        key.Contains("__", StringComparison.Ordinal)
        || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
        || key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>dotnet</c> of the runtime running the tests (…/shared/Microsoft.NETCore.App/&lt;version&gt;/ → root).</summary>
    private static string DotnetHost()
    {
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }
}

/// <summary>Exit code and captured output of one host run.</summary>
internal sealed record HostRun(int ExitCode, string Output, string Error)
{
    /// <summary>The <c>provision:</c> lines of stdout, without the prefix.</summary>
    public IReadOnlyList<string> ProvisionLines => Output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => l.StartsWith("provision: ", StringComparison.Ordinal))
        .Select(l => l["provision: ".Length..])
        .ToArray();

    public override string ToString() => $"exit {ExitCode}{Environment.NewLine}{Output}{Error}";
}
