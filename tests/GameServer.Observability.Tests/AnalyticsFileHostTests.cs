using System.Text.Json.Nodes;
using GameServer.Modules.Gm;
using GameServer.Platform.Analytics;
using GameServer.Platform.Commands;
using GameServer.Platform.Observability;
using GameServer.Platform.Observability.Analytics;
using GameServer.Platform.Outbox;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>A host that writes analytics files (AddAnalyticsFiles on its own temp folder and instance id).</summary>
internal sealed class AnalyticsFileHost : IAsyncDisposable
{
    private readonly TempLogDirectory _directory;
    private bool _stopped;

    private AnalyticsFileHost(ObservedHost observed, TempLogDirectory directory, string instanceId)
    {
        Observed = observed;
        _directory = directory;
        InstanceId = instanceId;
    }

    public ObservedHost Observed { get; }

    public TestHost Host => Observed.Host;

    public string InstanceId { get; }

    public string Path => _directory.Path;

    public static async Task<AnalyticsFileHost> StartAsync(Action<Dictionary<string, string?>>? settings = null, bool addTwice = false)
    {
        var instanceId = "af-" + Guid.NewGuid().ToString("N")[..8];
        var directory = new TempLogDirectory();
        try
        {
            var observed = await ObservedHost.StartAsync(
                settings: s =>
                {
                    s["Observability:InstanceId"] = instanceId;
                    s["GameServer:Analytics:Directory"] = directory.Path;
                    settings?.Invoke(s);
                },
                extra: b =>
                {
                    b.AddAnalyticsFiles();
                    if (addTwice)
                    {
                        b.AddAnalyticsFiles();
                    }
                });
            return new AnalyticsFileHost(observed, directory, instanceId);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public string FileOf(string eventName) =>
        System.IO.Path.Combine(Path, $"test-{InstanceId}-{eventName}-{Host.Clock.UtcNow:yyyyMMdd}.jsonl");

    public string[] LinesOf(string eventName) =>
        File.Exists(FileOf(eventName)) ? ObservedHost.ReadShared(FileOf(eventName)).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    public async Task<HealthReportEntry> AnalyticsHealthAsync()
    {
        var report = await Host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == AnalyticsFileExtensions.HealthCheckName);
        return report.Entries[AnalyticsFileExtensions.HealthCheckName];
    }

    /// <summary>Stops the host (graceful drain), keeping the files for inspection; idempotent.</summary>
    public async Task StopAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            await Observed.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _directory.Dispose();
    }
}

/// <summary>Analytics files from a running host: content, failures and drain (T042, US4-5, US4-7, US5-3, SC-004).</summary>
public sealed class AnalyticsFileHostTests
{
    private static Task<int> SweepAsync(TestHost host) => host.Services.GetRequiredService<OutboxSweeper>().SweepOnceAsync(CancellationToken.None);

    [Fact]
    public async Task Login_and_payment_files_hold_the_same_bytes_as_the_in_memory_sink()
    {
        await using var af = await AnalyticsFileHost.StartAsync();
        var (player, _) = await af.Host.NewPlayerAsync();

        (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
        await af.Host.FlushAnalyticsAsync();

        af.LinesOf("login").ShouldBe(af.Host.Analytics.Lines.Where(l => l.Contains("\"event\":\"login\"", StringComparison.Ordinal)));
        af.LinesOf("payment").ShouldBe(af.Host.Analytics.Lines.Where(l => l.Contains("\"event\":\"payment\"", StringComparison.Ordinal)));
        af.LinesOf("payment").ShouldHaveSingleItem();
        File.Exists(Path.Combine(af.Path, $".test-{af.InstanceId}.lock")).ShouldBeTrue();
    }

    [Fact]
    public async Task Adding_the_file_sink_twice_registers_it_once()
    {
        await using var af = await AnalyticsFileHost.StartAsync(addTwice: true);
        var (player, _) = await af.Host.NewPlayerAsync();

        (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
        await af.Host.FlushAnalyticsAsync();

        // One file sink next to the test host's memory sink: each record is written once, the health check is registered once.
        af.Host.Services.GetServices<IAnalyticsSink>().OfType<AnalyticsFileSink>().ShouldHaveSingleItem();
        af.Host.Services.GetRequiredService<AnalyticsActivation>().SinkCount.ShouldBe(2);
        af.LinesOf("payment").ShouldHaveSingleItem();
        af.LinesOf("login").ShouldBe(af.Host.Analytics.Lines.Where(l => l.Contains("\"event\":\"login\"", StringComparison.Ordinal)));
        (await af.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task An_unwritable_analytics_directory_never_fails_a_login_or_purchase_and_payments_arrive_after_the_fix()
    {
        await using var af = await AnalyticsFileHost.StartAsync();

        // Today's files cannot be created (their paths are taken): every batch of the file sink fails.
        Directory.CreateDirectory(af.FileOf("login"));
        Directory.CreateDirectory(af.FileOf("payment"));
        var (player, _) = await af.Host.NewPlayerAsync();
        var purchase = await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));
        await af.Host.FlushAnalyticsAsync();

        purchase.Code.ShouldBe("OK");
        af.Host.Store.Peek(ReceiptDocument.KeyFor(purchase.ReceiptId))!["outboxPending"]!.GetValue<bool>().ShouldBeTrue();
        var status = af.Host.Services.GetRequiredService<AnalyticsPipeline>().Status;
        status.SinkErrors.ShouldBeGreaterThanOrEqualTo(2);
        status.Dropped.ShouldBeGreaterThanOrEqualTo(1);
        (await af.AnalyticsHealthAsync()).Status.ShouldBe(HealthStatus.Degraded);
        (await af.Host.Client().GetStringAsync(ObservabilityModule.ReadyRoute)).ShouldBeOneOf("Healthy", "Degraded");

        Directory.Delete(af.FileOf("login"));
        Directory.Delete(af.FileOf("payment"));
        af.Host.Clock.Advance(TimeSpan.FromSeconds(31));
        (await SweepAsync(af.Host)).ShouldBe(1);
        (await SweepAsync(af.Host)).ShouldBe(0);

        var payment = JsonNode.Parse(af.LinesOf("payment").ShouldHaveSingleItem())!;
        payment["receiptId"]!.GetValue<string>().ShouldBe(purchase.ReceiptId);
        af.LinesOf("login").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_graceful_stop_writes_and_flushes_queued_login_records()
    {
        await using var af = await AnalyticsFileHost.StartAsync();
        var file = af.FileOf("login");
        af.Host.Analytics.WriteDelay = TimeSpan.FromMilliseconds(300);
        for (var i = 0; i < 6; i++)
        {
            await af.Host.NewPlayerAsync();
        }

        af.Host.Services.GetRequiredService<AnalyticsPipeline>().Status.QueueLength.ShouldBeGreaterThan(0);
        await af.StopAsync();

        ObservedHost.ReadShared(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(6);
    }

    [Fact]
    public async Task Gm_health_detail_carries_the_analytics_data_and_the_public_ready_only_the_status_word()
    {
        await using var af = await AnalyticsFileHost.StartAsync();
        var admin = await af.Host.GmLoginAsync("admin", ObservedHost.StaffPassword);

        var detail = JsonNode.Parse(await admin.GetStringAsync(GmModule.HealthRoute))!;
        var entry = detail["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "analytics")!;
        entry["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldContain("ready");
        var data = entry["data"]!.AsObject();
        data.Select(p => p.Key).ShouldBeSubsetOf(["sinkErrors", "dropped", "queue", "freeBytes", "lastError"]);
        data.ContainsKey("freeBytes").ShouldBeTrue();
        data.ContainsKey("sinkErrors").ShouldBeTrue();

        var ready = await af.Host.Client().GetStringAsync(ObservabilityModule.ReadyRoute);
        ready.ShouldBeOneOf("Healthy", "Degraded");
    }
}
