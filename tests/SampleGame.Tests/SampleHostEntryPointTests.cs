using System.Diagnostics;
using GameServer.Platform.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// T055 m2/m4 on the sample host's entry point: a configuration value of the wrong type and a misspelled provisioning switch end
/// with the startup report on stderr and exit 1, never with an unhandled exception and never by serving traffic.
/// </summary>
[Collection(ConsoleErrorCollection.Name)]
public sealed class SampleHostEntryPointTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData("Dev", "Couchbase:KvTimeoutMs")]
    [InlineData("Local", "GameServer:Limits:PlayerTimeoutSeconds")]
    public async Task A_value_of_the_wrong_type_is_reported_and_the_host_does_not_start(string flavor, string key)
    {
        var error = await ConsoleErrorCapture.RunAsync(async () =>
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
                .UseEnvironment(flavor)
                .UseSetting(key, "abc")
                .UseSetting("GameServer:Analytics:Enabled", "false"));

            // The entry point returns 1 before running the host; the factory then reports a host that never started.
            Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        });

        error.ShouldContain("Game server startup validation failed:");
        error.ShouldContain(" - [configuration] ");
        error.ShouldContain(key);
    }

    [Theory]
    [InlineData("/provision")]
    [InlineData("--provision=true")]
    public async Task A_misspelled_provisioning_switch_exits_1(string spelled)
    {
        var start = new ProcessStartInfo(FileStoreProcessKillTests.DotnetHost())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SampleHost.dll"));
        start.ArgumentList.Add(spelled);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Local";
        start.Environment.Remove("DOTNET_ENVIRONMENT");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(ExitTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        process.ExitCode.ShouldBe(HostExitCodes.InvalidConfiguration, await output + await error);
        (await error).ShouldContain("is not the provisioning switch. Fix: write --provision alone");
        (await output).ShouldNotContain("Now listening on");
    }
}
