using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using GameServer.Aggregates;
using GameServer.Modules.Economy;
using GameServer.Modules.Economy.Protocol.V1;
using GameServer.Platform.Commands;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Auth;
using GameServer.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CounterWire = Sample.Counter.Protocol.V1;

namespace SampleGame.Tests;

/// <summary>
/// Slice 011 SC-002, opt-in (<c>GAMESERVER_KILL_TEST=1</c>): the real sample host on the local flavor is killed (process tree)
/// at random moments of a command burst, <see cref="Kills"/> times on one folder. After each kill the folder is reopened like a
/// restart: every receipt finds its aggregates at (or past) the committed revision, and every counter revision has its receipt,
/// so no commit is half on disk (FR-020, US2-2).
/// </summary>
public sealed class FileStoreProcessKillTests : IDisposable
{
    public const string Variable = "GAMESERVER_KILL_TEST";

    private const int Kills = 20;
    private const int Players = 4;
    private const int ReadyTimeoutSeconds = 60;
    private const int MinBurstMs = 100;
    private const int MaxBurstMs = 1500;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gs-kill-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "data");

    public void Dispose() => TestStores.DeleteFolder(_root);

    [Fact]
    public async Task Killing_the_host_mid_burst_never_leaves_a_partial_commit()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Variable) == "1",
            $"{Variable} not set to 1: the real-process kill test starts the sample host {Kills} times (opt-in, SC-002).");

        var players = Enumerable.Range(0, Players).Select(i => $"kill-{i}").ToArray();
        var committed = 0;
        for (var kill = 1; kill <= Kills; kill++)
        {
            using var host = await StartHostAsync();
            using var burst = new CancellationTokenSource();
            var senders = players.Select(player => SendUntilKilledAsync(host.Url, player, burst.Token)).ToArray();
            await Task.Delay(Random.Shared.Next(MinBurstMs, MaxBurstMs));

            host.Process.Kill(entireProcessTree: true);
            await host.Process.WaitForExitAsync();
            await burst.CancelAsync();
            await Task.WhenAll(senders);

            committed = await AssertAllOrNothingAsync(kill);
        }

        committed.ShouldBeGreaterThan(0, "the bursts committed nothing, so the kills proved nothing.");
    }

    /// <summary>Counter increments (two documents per commit) and economy commands until the host dies.</summary>
    private static async Task SendUntilKilledAsync(string url, string player, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(url) };
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, player);
        for (var i = 0; !cancellationToken.IsCancellationRequested; i++)
        {
            try
            {
                if (i % 3 == 2)
                {
                    await client.PostProtobufAsync(CheatModule.GrantRoute, new CheatGrantRequest { Resource = "gold", Quantity = 5 }, ProtobufHttp.NewRequestId());
                }
                else
                {
                    await client.PostProtobufAsync("/api/v1/counter/increment", new CounterWire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // The host was killed: the request's fate is decided by the folder, checked after the kill.
                return;
            }
        }
    }

    /// <summary>Reopens the folder, checks it and returns the number of committed receipts.</summary>
    private async Task<int> AssertAllOrNothingAsync(int kill)
    {
        using var reopened = TestStores.CreateFileStore(Folder, "sample", new GameServer.Time.SystemClock(), NullLogger<FileDocumentStore>.Instance);
        await reopened.EnsureInitializedAsync(CancellationToken.None);
        var store = reopened.Store;

        var receipts = store.All(ReceiptDocument.Collection).Select(r => ReceiptDocument.FromJson(r.Content)).ToArray();
        var revisions = new HashSet<(string Aggregate, string Player, long Rev)>();
        foreach (var receipt in receipts)
        {
            foreach (var aggregate in receipt.Aggregates)
            {
                revisions.Add((aggregate.AggregateId, receipt.PlayerId, aggregate.NewRev));
                var document = store.Peek(new(PlayerAggregateDescriptor.CollectionFor(aggregate.AggregateId), receipt.PlayerId));
                document.ShouldNotBeNull($"kill {kill}: receipt {receipt.ReceiptId} without its {aggregate.AggregateId} document.");
                document["rev"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(
                    aggregate.NewRev, $"kill {kill}: receipt {receipt.ReceiptId} committed {aggregate.AggregateId} rev {aggregate.NewRev}.");
            }
        }

        // Only the counter command writes the counter: each of its revisions is one commit with one receipt.
        const string counter = Sample.Counter.CounterAggregates.Counter;
        foreach (var (key, content) in store.All(PlayerAggregateDescriptor.CollectionFor(counter)))
        {
            var rev = content["rev"]!.GetValue<long>();
            for (var r = 1L; r <= rev; r++)
            {
                revisions.ShouldContain((counter, key.Key, r), $"kill {kill}: counter of {key.Key} at rev {rev} has no receipt for rev {r}.");
            }
        }

        return receipts.Length;
    }

    private async Task<RunningHost> StartHostAsync()
    {
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SampleHost.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(url);
        start.ArgumentList.Add($"--FileStore:Folder={Folder}");
        start.ArgumentList.Add($"--Observability:Logging:Directory={Path.Combine(_root, "logs")}");
        start.ArgumentList.Add("--GameServer:Hosting:DrainDelaySeconds=0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Local";
        start.Environment.Remove("DOTNET_ENVIRONMENT");

        var output = new ConcurrentQueue<string>();
        var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        process.OutputDataReceived += (_, e) => Keep(output, e.Data);
        process.ErrorDataReceived += (_, e) => Keep(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(ReadyTimeoutSeconds))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The host exited with {process.ExitCode} before ready:{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
            }

            try
            {
                if ((await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK)
                {
                    return new RunningHost(process, url);
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(200);
        }

        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"The host was not ready in {ReadyTimeoutSeconds} s:{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
    }

    private static void Keep(ConcurrentQueue<string> output, string? line)
    {
        if (line is null)
        {
            return;
        }

        output.Enqueue(line);
        while (output.Count > 50 && output.TryDequeue(out _))
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The <c>dotnet</c> of the runtime running the tests (…/shared/Microsoft.NETCore.App/&lt;version&gt;/ → root).</summary>
    internal static string DotnetHost()
    {
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private sealed record RunningHost(Process Process, string Url) : IDisposable
    {
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }

            Process.Dispose();
        }
    }
}
