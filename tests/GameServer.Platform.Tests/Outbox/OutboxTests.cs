using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Outbox;

/// <summary>Outbox in receipt, inline relay and sweeper (JOB-02, OPS-05).</summary>
public sealed class OutboxTests : IDisposable
{
    private readonly MetricCollector _metrics = new();

    public void Dispose() => _metrics.Dispose();

    private static bool Pending(RunnerHarness h, string receiptId) =>
        h.Store.Peek(ReceiptDocument.KeyFor(receiptId))!["outboxPending"]!.GetValue<bool>();

    [Fact]
    public async Task Inline_relay_projects_entries_and_clears_pending()
    {
        using var h = new RunnerHarness();

        var execution = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(2, "hello"));

        var log = h.Store.Peek(new DocumentKey(CounterModuleDefinition.LogCollection, $"counter_log::{execution.ReceiptId}:0")).ShouldNotBeNull();
        log["payload"]!["note"]!.GetValue<string>().ShouldBe("hello");
        log["payload"]!["newValue"]!.GetValue<long>().ShouldBe(2);
        log["receiptId"]!.GetValue<string>().ShouldBe(execution.ReceiptId);
        Pending(h, execution.ReceiptId!).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweeper_relays_what_inline_relay_missed_without_duplicates()
    {
        using var h = new RunnerHarness(o => o.Outbox.InlineRelayEnabled = false);
        var execution = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(2, null));
        Pending(h, execution.ReceiptId!).ShouldBeTrue();
        h.Store.Count(CounterModuleDefinition.LogCollection).ShouldBe(0);

        // Too young for the sweeper: left to the inline relay window.
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).ShouldBe(0);

        h.Clock.Advance(TimeSpan.FromSeconds(11));
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).ShouldBe(1);
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).ShouldBe(0);

        h.Store.Count(CounterModuleDefinition.LogCollection).ShouldBe(1);
        Pending(h, execution.ReceiptId!).ShouldBeFalse();
        _metrics.Values("outbox_pending_age_seconds").ShouldContain(v => v >= 11);
    }

    [Fact]
    public async Task Relaying_the_same_receipt_twice_is_idempotent()
    {
        using var h = new RunnerHarness();
        var execution = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(2, null));
        var key = ReceiptDocument.KeyFor(execution.ReceiptId!);
        var receipt = ReceiptDocument.FromJson(h.Store.Peek(key)!);

        (await h.Relay.TryRelayAsync(key, receipt, CancellationToken.None)).ShouldBeTrue();
        (await h.Relay.TryRelayAsync(key, receipt, CancellationToken.None)).ShouldBeTrue();

        h.Store.Count(CounterModuleDefinition.LogCollection).ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_command_relays_nothing()
    {
        using var h = new RunnerHarness();

        var execution = await h.RunAsync(IncrementHandler.Operation, new IncrementCommand(50, null));

        execution.Code.ShouldBe(CounterErrors.LimitExceeded);
        h.Store.Count(CounterModuleDefinition.LogCollection).ShouldBe(0);
    }

    [Fact]
    public async Task Relay_failure_keeps_command_successful_and_receipt_pending()
    {
        using var h = new RunnerHarness();

        // probe_log has no consumer: relay fails, command still commits.
        var execution = await h.RunAsync("probe.outboxflood", 10);

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        Pending(h, execution.ReceiptId!).ShouldBeTrue();
        _metrics.Sum("outbox_relay_inline_failures_total", "kind", "probe_log").ShouldBe(1);
    }
}
