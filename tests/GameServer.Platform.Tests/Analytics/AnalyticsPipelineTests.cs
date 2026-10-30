using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GameServer.Platform.Analytics;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>Queue, dispatcher, ack rule, timeout/abandon, batches, drain and metrics (T018, R5, FR-004, FR-013, FR-028, SC-008).</summary>
public sealed class AnalyticsPipelineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Inactive_pipeline_emits_nothing_and_counts_nothing()
    {
        await using var disabled = await PipelineKit.StartAsync(new AnalyticsOptions { Enabled = false }, new InMemoryAnalyticsSink());
        await using var noSink = await PipelineKit.StartAsync(new AnalyticsOptions());

        disabled.Pipeline.IsActive.ShouldBeFalse();
        noSink.Pipeline.IsActive.ShouldBeFalse();
        disabled.Emit("p1").ShouldBeFalse();
        noSink.Emit("p1").ShouldBeFalse();
        await disabled.Pipeline.DeliverAsync(PipelineKit.Delivery("r:0"), CancellationToken.None);
        disabled.Sum("analytics_events_dropped_total").ShouldBe(0);
        disabled.Pipeline.Status.Dropped.ShouldBe(0);
    }

    [Fact]
    public async Task A_full_queue_drops_and_counts_and_never_exceeds_its_capacity()
    {
        var gate = new GateSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { QueueCapacity = 100 }, gate);

        kit.Emit("first").ShouldBeTrue();
        await gate.Entered.WaitAsync(Wait);
        for (var i = 0; i < 100; i++)
        {
            kit.Emit("p" + i).ShouldBeTrue();
        }

        kit.Emit("overflow").ShouldBeFalse();
        await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:0"), CancellationToken.None));

        kit.Pipeline.Status.QueueLength.ShouldBe(100);
        kit.Gauge("analytics_queue_entries").ShouldBe(100);
        kit.Sum("analytics_events_dropped_total", "reason", "queue_full").ShouldBe(1);
        kit.Sum("analytics_delivery_failures_total", "reason", "queue_full").ShouldBe(1);
        gate.Open();
        await kit.Pipeline.FlushAsync(CancellationToken.None).WaitAsync(Wait);
        gate.Inner.Lines.Count.ShouldBe(101);
    }

    [Fact]
    public async Task A_delivery_completes_only_after_every_sink_wrote_it()
    {
        var gate = new GateSink();
        var memory = new InMemoryAnalyticsSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { DeliveryTimeoutMs = 5_000 }, memory, gate);

        var delivery = kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:1"), CancellationToken.None);
        await gate.Entered.WaitAsync(Wait);
        memory.Lines.Count.ShouldBe(1);
        delivery.IsCompleted.ShouldBeFalse();

        gate.Open();
        await delivery.WaitAsync(Wait);
        gate.Inner.Lines.ShouldBe(memory.Lines);
        kit.Sum("analytics_events_written_total", "sink", "memory").ShouldBe(1);
        kit.Sum("analytics_events_written_total", "sink", "gate").ShouldBe(1);
    }

    [Fact]
    public async Task One_failing_sink_fails_the_delivery_but_the_other_sink_still_gets_the_batch()
    {
        var good = new InMemoryAnalyticsSink();
        var bad = new InMemoryAnalyticsSink { Name = "bad", FailWrites = true };
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), bad, good);

        var ex = await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:2"), CancellationToken.None));
        kit.Emit("p_login").ShouldBeTrue();
        await kit.Pipeline.FlushAsync(CancellationToken.None).WaitAsync(Wait);

        ex.Reason.ShouldBe(AnalyticsDeliveryFailure.SinkError);
        good.Lines.Count.ShouldBe(2);
        kit.Sum("analytics_sink_errors_total", "sink", "bad").ShouldBe(2);
        kit.Sum("analytics_delivery_failures_total", "reason", "sink_error").ShouldBe(1);
        kit.Sum("analytics_events_dropped_total", "reason", "sink_error").ShouldBe(1);
        kit.Sum("analytics_events_dropped_total", "event", "payment").ShouldBe(0);
        var status = kit.Pipeline.Status;
        status.SinkErrors.ShouldBe(2);
        status.Dropped.ShouldBe(1);
        status.LastError.ShouldNotBeNull().ShouldContain("IOException");
        status.LastErrorAtUtc.ShouldBe(kit.Clock.UtcNow);
    }

    [Fact]
    public async Task A_slow_sink_times_the_delivery_out_within_the_bound()
    {
        var slow = new InMemoryAnalyticsSink { WriteDelay = TimeSpan.FromSeconds(2) };
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { DeliveryTimeoutMs = 50 }, slow);

        var watch = Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:3"), CancellationToken.None));

        ex.Reason.ShouldBe(AnalyticsDeliveryFailure.Timeout);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1_000));
        kit.Sum("analytics_delivery_failures_total", "reason", "timeout").ShouldBe(1);
    }

    [Fact]
    public async Task A_timed_out_delivery_not_yet_taken_is_skipped_but_a_taken_one_may_still_be_written()
    {
        var gate = new GateSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { DeliveryTimeoutMs = 50 }, gate);

        // Taken by the dispatcher (alone in the queue), then timed out while the sink blocks: written later (duplicate).
        await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:taken"), CancellationToken.None));
        await gate.Entered.WaitAsync(Wait);

        // Queued behind the blocked batch and timed out before the dispatcher takes it: never written.
        await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:queued"), CancellationToken.None));

        gate.Open();
        await kit.Pipeline.FlushAsync(CancellationToken.None).WaitAsync(Wait);
        gate.Inner.Records.Select(r => r["n"]!.GetValue<string>()).ShouldBe(["r:taken"]);
    }

    [Fact]
    public async Task Batches_hold_at_most_512_records_in_order_and_every_sink_gets_identical_bytes()
    {
        var gate = new GateSink();
        var memory = new InMemoryAnalyticsSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), gate, memory);

        kit.Emit("u0000").ShouldBeTrue();
        await gate.Entered.WaitAsync(Wait);
        for (var i = 1; i < 1500; i++)
        {
            kit.Emit($"u{i:D4}").ShouldBeTrue();
        }

        gate.Open();
        await kit.Pipeline.FlushAsync(CancellationToken.None).WaitAsync(Wait);

        gate.BatchSizes.ShouldAllBe(size => size <= 512);
        gate.BatchSizes.Sum().ShouldBe(1500);
        gate.Inner.Records.Select(r => r["userID"]!.GetValue<string>()).ShouldBe(Enumerable.Range(0, 1500).Select(i => $"u{i:D4}"));
        memory.Lines.ShouldBe(gate.Inner.Lines);
        kit.Sum("analytics_events_written_total", "event", "login").ShouldBe(3000);
    }

    [Fact]
    public async Task Stop_drains_queued_records()
    {
        var memory = new InMemoryAnalyticsSink { WriteDelay = TimeSpan.FromMilliseconds(20) };
        var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), memory);
        for (var i = 0; i < 50; i++)
        {
            kit.Emit("p" + i).ShouldBeTrue();
        }

        await kit.Pipeline.StopAsync(CancellationToken.None);

        memory.Lines.Count.ShouldBe(50);
        kit.Sum("analytics_events_dropped_total").ShouldBe(0);
        kit.Emit("late").ShouldBeFalse();
        kit.Sum("analytics_events_dropped_total", "reason", "shutdown").ShouldBe(1);
        (await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:late"), CancellationToken.None)))
            .Reason.ShouldBe(AnalyticsDeliveryFailure.Stopped);
        await kit.DisposeAsync();
    }

    [Fact]
    public async Task Stop_past_the_drain_budget_counts_the_rest_and_fails_pending_deliveries()
    {
        var gate = new GateSink();
        var kit = await PipelineKit.StartAsync(new AnalyticsOptions { DeliveryTimeoutMs = 5_000 }, gate);
        kit.Emit("blocked").ShouldBeTrue();
        await gate.Entered.WaitAsync(Wait);
        for (var i = 0; i < 10; i++)
        {
            kit.Emit("p" + i).ShouldBeTrue();
        }

        var delivery = kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:pending"), CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await kit.Pipeline.StopAsync(budget.Token);

        (await Should.ThrowAsync<AnalyticsDeliveryException>(() => delivery)).Reason.ShouldBe(AnalyticsDeliveryFailure.Stopped);
        kit.Sum("analytics_events_dropped_total", "reason", "shutdown").ShouldBe(11);
        kit.Sum("analytics_delivery_failures_total", "reason", "stopped").ShouldBe(1);
        gate.Inner.Lines.ShouldBeEmpty();
        await kit.DisposeAsync();
    }

    [Fact]
    public async Task Metric_tags_are_only_enumerated_values()
    {
        var bad = new InMemoryAnalyticsSink { Name = "bad", FailWrites = true };
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), bad);
        kit.Emit("p_secret_player").ShouldBeTrue();
        await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:x", "p_secret_player"), CancellationToken.None));

        foreach (var name in new[] { "analytics_events_dropped_total", "analytics_sink_errors_total", "analytics_delivery_failures_total" })
        {
            foreach (var tags in kit.TagsOf(name))
            {
                tags.Keys.ShouldBeSubsetOf(["event", "reason", "sink"]);
                tags.Values.ShouldAllBe(v => v != null && !v.Contains("p_secret_player") && !v.Contains("r:x"));
            }
        }
    }

    [Fact]
    public async Task Invalid_emissions_are_refused_without_throwing()
    {
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), new InMemoryAnalyticsSink());

        kit.Pipeline.TryEmit(new("Bad-Name", 1, "t"), "p", kit.Clock.UtcNow, new JsonObject()).ShouldBeFalse();
        kit.Pipeline.TryEmit(PipelineKit.Login, string.Empty, kit.Clock.UtcNow, new JsonObject()).ShouldBeFalse();
    }

    [Fact]
    public async Task Outbox_deliveries_are_marked_guaranteed_and_best_effort_records_are_not()
    {
        var recorder = new RecordingSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), recorder);

        kit.Emit("p_login").ShouldBeTrue();
        await kit.Pipeline.FlushAsync(CancellationToken.None).WaitAsync(Wait);
        await kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:g"), CancellationToken.None);

        recorder.Records.Select(r => (r.Event, r.Guaranteed)).ShouldBe([("login", false), ("payment", true)]);
    }

    [Fact]
    public async Task An_unexpected_dispatch_failure_fails_the_batch_and_keeps_the_dispatcher_running()
    {
        var sink = new InMemoryAnalyticsSink { FailWrites = true };
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { DeliveryTimeoutMs = 5_000 }, sink);
        var failing = new StrongBox<bool>(true);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, kit.Metrics.Meter) && instrument.Name == "analytics_sink_errors_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (failing.Value)
            {
                throw new InvalidOperationException("metrics listener bug");
            }
        });
        listener.Start();

        // The sink fails and counting that failure throws: the exception leaves the per-sink handling of the dispatcher.
        kit.Emit("p_lost").ShouldBeTrue();
        var ex = await Should.ThrowAsync<AnalyticsDeliveryException>(() => kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:bug"), CancellationToken.None));

        ex.Reason.ShouldBe(AnalyticsDeliveryFailure.SinkError);
        kit.Pipeline.Status.Dropped.ShouldBe(1);
        kit.Pipeline.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();

        failing.Value = false;
        sink.FailWrites = false;
        await kit.Pipeline.DeliverAsync(PipelineKit.Delivery("r:after"), CancellationToken.None);
        sink.Records.Select(r => r["n"]!.GetValue<string>()).ShouldBe(["r:after"]);
    }

    /// <summary>Keeps the records themselves (flags included).</summary>
    private sealed class RecordingSink : IAnalyticsSink
    {
        private readonly ConcurrentQueue<AnalyticsRecord> _records = new();

        public string Name => "recording";

        public IReadOnlyList<AnalyticsRecord> Records => [.. _records];

        public ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken)
        {
            foreach (var record in batch)
            {
                _records.Enqueue(record);
            }

            return ValueTask.CompletedTask;
        }
    }
}
