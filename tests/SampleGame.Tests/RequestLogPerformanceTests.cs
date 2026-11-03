using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GameServer.Platform.Observability;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace SampleGame.Tests;

/// <summary>
/// Opt-in measurements (slice 013 T047, FR-085, SC-008; <c>GAMESERVER_PERF=1</c>): p95 of an in-process command round trip with
/// the request event and the console sink (null writer) on versus both off, and 2 000 requests with a stalled console and a
/// saturated file queue. Numbers are printed (and appended to <c>GAMESERVER_PERF_OUT</c> when set) for T076.
/// </summary>
[Collection(ConsoleErrorCollection.Name)]
public sealed class RequestLogPerformanceTests
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the request-log latency and the blocked-sink bound (SC-008).";
    private const string IncrementRoute = "/api/v1/counter/increment";
    private const int WarmUp = 100;
    private const int Samples = 1000;
    private const int BlockedRequests = 2000;
    private const double MaxAddedP95Ms = 1.0;

    [Fact]
    public async Task Request_logging_and_console_add_at_most_1_ms_to_the_p95_of_a_command()
    {
        RequireOptIn();
        var off = await MeasureAsync(on: false);
        var on = await MeasureAsync(on: true);

        Print($"request-log p95 command round trip: off {off:0.000} ms, on (event + console) {on:0.000} ms, delta {on - off:0.000} ms");
        (on - off).ShouldBeLessThanOrEqualTo(MaxAddedP95Ms);
    }

    [Fact]
    public async Task With_a_stalled_console_and_a_saturated_file_queue_every_request_succeeds()
    {
        RequireOptIn();
        using var console = new BlockingWriter();
        var dropped = LogSinkMonitor.Instance.DroppedCount;
        var (fileDropped, consoleDropped) = (LogSinkMonitor.Instance.FileQueue.DroppedCount, LogSinkMonitor.Instance.ConsoleQueue.DroppedCount);
        var folder = TempFolder();
        try
        {
            await using var host = await StartAsync(folder, on: true, console, s =>
            {
                s["Observability:Logging:Console:BufferSize"] = "10";

                // The file sink cannot be stalled from a test: a one-entry queue keeps it saturated instead.
                s["Observability:Logging:BufferSize"] = "1";
            });
            try
            {
                var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
                var statuses = new ConcurrentBag<int>();
                var watch = Stopwatch.StartNew();
                // One player per request: the per-player queue (CMD-06) must not be what refuses requests here.
                await Parallel.ForEachAsync(Enumerable.Range(0, BlockedRequests), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, _) =>
                {
                    using var client = host.PlayerClient("p_perf_" + i);
                    statuses.Add((int)(await client.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId())).StatusCode);
                });
                watch.Stop();
                var memory = GC.GetTotalMemory(forceFullCollection: true) - memoryBefore;
                var drops = LogSinkMonitor.Instance.DroppedCount - dropped;

                Print($"request-log blocked sinks: {statuses.Count(s => s == 200)}/{BlockedRequests} OK in {watch.Elapsed.TotalSeconds:0.00} s, drops {drops} " +
                    $"(file {LogSinkMonitor.Instance.FileQueue.DroppedCount - fileDropped}, console {LogSinkMonitor.Instance.ConsoleQueue.DroppedCount - consoleDropped}), " +
                    $"managed memory delta {memory / 1024} KiB");
                statuses.ShouldAllBe(status => status == 200);
                statuses.Count.ShouldBe(BlockedRequests);
                drops.ShouldBeGreaterThanOrEqualTo(BlockedRequests - 11);
                memory.ShouldBeLessThan(64L * 1024 * 1024);
            }
            finally
            {
                console.Release();
            }
        }
        finally
        {
            TestStores.DeleteFolder(folder);
        }
    }

    private static void RequireOptIn() => Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);

    private static void Print(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("GAMESERVER_PERF_OUT") is { Length: > 0 } file)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "gs-perf-" + Guid.NewGuid().ToString("N"));

    private static Task<TestHost> StartAsync(string folder, bool on, TextWriter console, Action<Dictionary<string, string?>>? settings = null) =>
        SampleTestHost.StartAsync(
            b => GameHttp.FullGame(b.AddObservability()),
            s =>
            {
                s["Observability:InstanceId"] = "perf-" + Guid.NewGuid().ToString("N")[..8];
                s["Observability:Logging:Directory"] = Path.Combine(folder, "logs");
                s["GameServer:Analytics:Enabled"] = "false";
                s["GameServer:RequestLog:Enabled"] = on ? "true" : "false";
                s["Observability:Logging:Console:Enabled"] = on ? "true" : "false";
                settings?.Invoke(s);
            },
            services: s => s.AddSingleton(new ConsoleOutputOverride(console)));

    private static async Task<double> MeasureAsync(bool on)
    {
        var folder = TempFolder();
        try
        {
            await using var host = await StartAsync(folder, on, TextWriter.Null);
            var client = host.PlayerClient("p_perf_" + (on ? "on" : "off"));
            var durations = new List<double>(Samples);
            for (var i = 0; i < WarmUp + Samples; i++)
            {
                var watch = Stopwatch.StartNew();
                (await client.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId())).EnsureSuccessStatusCode();
                if (i >= WarmUp)
                {
                    durations.Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            durations.Sort();
            return durations[(int)Math.Ceiling(0.95 * durations.Count) - 1];
        }
        finally
        {
            TestStores.DeleteFolder(folder);
        }
    }

    /// <summary>A stalled console: every write waits until released.</summary>
    private sealed class BlockingWriter : TextWriter
    {
        private readonly ManualResetEventSlim _released = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => _released.Wait();

        public override void Write(string? value) => _released.Wait();

        public override void Write(ReadOnlySpan<char> buffer) => _released.Wait();

        public void Release() => _released.Set();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _released.Set();
                _released.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
