using System.Diagnostics;
using System.Text;

namespace GameServer.WireGen;

/// <summary>Exit code and captured output of one child process.</summary>
internal sealed record ChildResult(int ExitCode, string Output, string Error)
{
    /// <summary>The non-empty lines of standard error, else of standard output (a failed <c>dotnet run</c> build reports on stdout).</summary>
    public IReadOnlyList<string> FailureLines()
    {
        var lines = Lines(Error);
        return lines.Count > 0 ? lines : Lines(Output);
    }

    public static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
}

/// <summary>Runs <c>dotnet</c> and <c>protoc</c> to completion with captured output (V15).</summary>
internal static class ChildProcess
{
    public static async Task<ChildResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
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

        // MSBuild variables of a calling dotnet run/test would steer the child build; the child resolves its own SDK.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new WireGenException($"[process] {fileName} did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new WireGenException($"[process] {fileName} cannot be started: {ex.Message} Fix: check the path (--dotnet <path> for dotnet).");
        }

        using (process)
        {
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
                throw new WireGenException($"[process] {Path.GetFileName(fileName)} {string.Join(' ', start.ArgumentList)} did not finish within {timeout}.");
            }

            return new ChildResult(process.ExitCode, await output, await error);
        }
    }
}
