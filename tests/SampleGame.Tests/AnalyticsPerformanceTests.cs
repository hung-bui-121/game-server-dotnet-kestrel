using System.Diagnostics;
using System.Globalization;
using GameServer.Platform.Analytics;
using GameServer.Platform.Observability.Analytics;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Opt-in measurements (T063, SC-004, SC-008; <c>GAMESERVER_PERF=1</c>): request latency with analytics files on and off, a
/// sustained login burst through the default queue, and the memory bound of a blocked sink. Numbers are printed for PCR §18.
/// </summary>
[Collection(ConsoleErrorCollection.Name)]
public sealed class AnalyticsPerformanceTests
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure analytics latency, burst drops and the blocked-sink bound (SC-004, SC-008).";
    private const int WarmUp = 50;
    private const int Samples = 400;

    private static void RequireOptIn() => Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);

    /// <summary>Diagnostic message, and a line in <c>GAMESERVER_PERF_OUT</c> when set (numbers for PCR §18).</summary>
    private static void Print(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("GAMESERVER_PERF_OUT") is { Length: > 0 } file)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    private static async Task<TestHost> StartAsync(bool analytics, string directory, Action<Dictionary<string, string?>>? settings = null) =>
        await GameHttp.StartGameAsync(
            s =>
            {
                s["GameServer:Analytics:Enabled"] = analytics ? "true" : "false";
                s["GameServer:Analytics:Directory"] = directory;
                s["Observability:InstanceId"] = "perf-" + Guid.NewGuid().ToString("N")[..8];
                settings?.Invoke(s);
            },
            extra: b => b.AddAnalyticsFiles());

    private static double P95(List<double> values)
    {
        values.Sort();
        return values[(int)Math.Ceiling(0.95 * values.Count) - 1];
    }

    private static async Task<(double Login, double Purchase)> MeasureAsync(bool analytics)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gs-perf-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StartAsync(analytics, directory);
            var logins = new List<double>();
            var purchases = new List<double>();
            for (var i = 0; i < WarmUp + Samples; i++)
            {
                var watch = Stopwatch.StartNew();
                var (player, _) = await host.NewPlayerAsync();
                var login = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
                var purchase = watch.Elapsed.TotalMilliseconds;
                if (i >= WarmUp)
                {
                    logins.Add(login);
                    purchases.Add(purchase);
                }
            }

            return (P95(logins), P95(purchases));
        }
        finally
        {
            TestStores.DeleteFolder(directory);
        }
    }

    [Fact]
    public async Task Analytics_files_add_at_most_1_ms_to_login_p95_and_100_ms_to_purchase_p95()
    {
        RequireOptIn();
        await MeasureAsync(true);
        var off = await MeasureAsync(false);
        var on = await MeasureAsync(true);

        Print(string.Create(CultureInfo.InvariantCulture, $"SC-004 p95 off: login {off.Login:F2} ms, purchase {off.Purchase:F2} ms"));
        Print(string.Create(CultureInfo.InvariantCulture, $"SC-004 p95 on:  login {on.Login:F2} ms, purchase {on.Purchase:F2} ms"));
        (on.Login - off.Login).ShouldBeLessThanOrEqualTo(1.0);
        // SC-004 as amended 2026-10-31: one durable flush per payment batch (fsync) on the ack path.
        (on.Purchase - off.Purchase).ShouldBeLessThanOrEqualTo(100.0);
    }

    [Fact]
    public async Task A_burst_of_180_logins_per_second_for_60_seconds_drops_nothing()
    {
        RequireOptIn();
        const int PerSecond = 180;
        const int Seconds = 60;
        var directory = Path.Combine(Path.GetTempPath(), "gs-perf-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StartAsync(true, directory);
            var pipeline = host.Services.GetRequiredService<AnalyticsPipeline>();
            var calls = new List<Task>();
            var clock = Stopwatch.StartNew();
            var maxQueue = 0;
            for (var i = 0; i < PerSecond * Seconds; i++)
            {
                var due = TimeSpan.FromSeconds((double)i / PerSecond);
                if (due > clock.Elapsed)
                {
                    await Task.Delay(due - clock.Elapsed);
                }

                calls.Add(host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret()));
                maxQueue = Math.Max(maxQueue, pipeline.Status.QueueLength);
            }

            await Task.WhenAll(calls);
            await host.FlushAnalyticsAsync();
            Print($"SC-008 burst: {calls.Count} logins in {clock.Elapsed.TotalSeconds:F1} s, max queue {maxQueue}, dropped {pipeline.Status.Dropped}");
            pipeline.Status.Dropped.ShouldBe(0);
            host.Analytics.OfEvent("login").Count.ShouldBe(PerSecond * Seconds);
        }
        finally
        {
            TestStores.DeleteFolder(directory);
        }
    }

    [Fact]
    public async Task A_blocked_sink_keeps_the_queue_and_memory_bounded_and_counts_the_drops()
    {
        RequireOptIn();
        const int Emissions = 200_000;
        var directory = Path.Combine(Path.GetTempPath(), "gs-perf-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StartAsync(true, directory, s => s["GameServer:Hosting:ShutdownTimeoutSeconds"] = "2");
            var pipeline = host.Services.GetRequiredService<AnalyticsPipeline>();
            host.Analytics.WriteDelay = TimeSpan.FromHours(1);
            var emitter = host.Services.GetRequiredService<IAnalyticsEmitter>();
            var type = new GameServer.Analytics.AnalyticsEventType("login", 1, "activeTime");
            GC.Collect();
            var before = GC.GetTotalMemory(forceFullCollection: true);

            for (var i = 0; i < Emissions; i++)
            {
                emitter.TryEmit(type, "p_" + i.ToString(CultureInfo.InvariantCulture), host.Clock.UtcNow, new { source = "resume", isNewUser = false });
            }

            var after = GC.GetTotalMemory(forceFullCollection: true);
            var status = pipeline.Status;
            Print($"SC-008 blocked sink: queue {status.QueueLength}, dropped {status.Dropped}, managed heap growth {(after - before) / 1024 / 1024} MiB");
            status.QueueLength.ShouldBeLessThanOrEqualTo(10_000);
            // Everything but the queue (10 000) and the one batch (<= 512) held by the blocked sink is dropped and counted.
            status.Dropped.ShouldBeGreaterThanOrEqualTo(Emissions - 10_000 - 512, $"queue {status.QueueLength}, dropped {status.Dropped}");
            (after - before).ShouldBeLessThan(64L * 1024 * 1024);
        }
        finally
        {
            TestStores.DeleteFolder(directory);
        }
    }
}
