using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GameServer.Analytics;
using GameServer.Platform.Analytics;
using GameServer.Platform.Outbox;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>Outbox entry → envelope → ack rules (T019, R4, FR-003, FR-013, FR-014).</summary>
public sealed class AnalyticsOutboxConsumerTests
{
    private const string ReceiptId = "q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e";
    private const string CommittedAt = "2026-10-30T14:05:09.123Z";

    private readonly ConcurrentQueue<string> _logs = new();

    private static OutboxDelivery Delivery(string playerId = "p_buyer", JsonNode? payload = null) => new(
        new OutboxEntry(ReceiptId + ":2", AnalyticsOutbox.Kind, payload ?? Payload()),
        ReceiptId,
        playerId,
        playerId,
        "iap.purchase",
        CommittedAt);

    private static JsonObject Payload() => new()
    {
        ["event"] = "payment",
        ["schemaVersion"] = 1,
        ["timeField"] = "chargeTime",
        ["fields"] = new JsonObject { ["transactionID"] = "google:GPA.1", ["store"] = "google", ["isTest"] = true },
    };

    private AnalyticsOutboxConsumer Consumer(PipelineKit kit) => new(new Lazy<AnalyticsPipeline>(kit.Pipeline), kit.Metrics, new QueueLogger(_logs));

    [Fact]
    public async Task The_envelope_comes_from_the_receipt()
    {
        var memory = new InMemoryAnalyticsSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { TimeZone = "Asia/Ho_Chi_Minh" }, memory);

        await Consumer(kit).HandleAsync(Delivery(), CancellationToken.None);

        var record = memory.Records.ShouldHaveSingleItem();
        record["event"]!.GetValue<string>().ShouldBe("payment");
        record["userID"]!.GetValue<string>().ShouldBe("p_buyer");
        record["chargeTimeUtc"]!.GetValue<string>().ShouldBe(CommittedAt);
        record["chargeTime"]!.GetValue<string>().ShouldBe("2026-10-30T21:05:09.123+07:00");
        record["receiptId"]!.GetValue<string>().ShouldBe(ReceiptId);
        record["eventId"]!.GetValue<string>().ShouldBe(AnalyticsEventIds.ForOutboxEntry(ReceiptId + ":2", CommittedAt));
        record["transactionID"]!.GetValue<string>().ShouldBe("google:GPA.1");
        record["isTest"]!.GetValue<bool>().ShouldBeTrue();
        Consumer(kit).Kind.ShouldBe("analytics");
    }

    [Fact]
    public async Task The_same_delivery_twice_gives_identical_lines_and_an_anonymized_receipt_keeps_the_event_id()
    {
        var memory = new InMemoryAnalyticsSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), memory);
        var consumer = Consumer(kit);

        await consumer.HandleAsync(Delivery(), CancellationToken.None);
        await consumer.HandleAsync(Delivery(), CancellationToken.None);
        await consumer.HandleAsync(Delivery("anon_7f3a"), CancellationToken.None);

        memory.Lines[0].ShouldBe(memory.Lines[1]);
        var anonymized = memory.Records[2];
        anonymized["userID"]!.GetValue<string>().ShouldBe("anon_7f3a");
        anonymized["eventId"]!.GetValue<string>().ShouldBe(memory.Records[0]["eventId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Inactive_analytics_acknowledges_without_writing_or_metrics()
    {
        var memory = new InMemoryAnalyticsSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { Enabled = false }, memory);

        await Consumer(kit).HandleAsync(Delivery(payload: new JsonObject()), CancellationToken.None);

        memory.Lines.ShouldBeEmpty();
        kit.Sum("analytics_delivery_failures_total").ShouldBe(0);
    }

    [Theory]
    [InlineData("""{"event":"payment","schemaVersion":1,"timeField":"chargeTime"}""")]
    [InlineData("""{"event":"Bad-Name","schemaVersion":1,"timeField":"chargeTime","fields":{}}""")]
    [InlineData("""{"event":"payment","schemaVersion":"one","timeField":"chargeTime","fields":{}}""")]
    [InlineData("""{"event":"payment","schemaVersion":0,"timeField":"chargeTime","fields":{}}""")]
    [InlineData("""[1,2]""")]
    public async Task A_malformed_payload_throws_and_logs_the_entry_id(string json)
    {
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions(), new InMemoryAnalyticsSink());

        var ex = await Should.ThrowAsync<AnalyticsDeliveryException>(() => Consumer(kit).HandleAsync(Delivery(payload: JsonNode.Parse(json)), CancellationToken.None));

        ex.Reason.ShouldBe(AnalyticsDeliveryFailure.Malformed);
        _logs.ShouldContain(line => line.Contains(ReceiptId + ":2"));
        kit.Sum("analytics_delivery_failures_total", "reason", "malformed").ShouldBe(1);
    }

    [Fact]
    public async Task Sink_error_timeout_and_stopped_all_throw()
    {
        var failing = new InMemoryAnalyticsSink { FailWrites = true };
        // A bound loose enough for a sink error under full-suite load; the timeout case uses a write slower than it.
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { DeliveryTimeoutMs = 1_000 }, failing);
        var consumer = Consumer(kit);

        (await Should.ThrowAsync<AnalyticsDeliveryException>(() => consumer.HandleAsync(Delivery(), CancellationToken.None))).Reason.ShouldBe(AnalyticsDeliveryFailure.SinkError);

        failing.FailWrites = false;
        failing.WriteDelay = TimeSpan.FromSeconds(3);
        (await Should.ThrowAsync<AnalyticsDeliveryException>(() => consumer.HandleAsync(Delivery(), CancellationToken.None))).Reason.ShouldBe(AnalyticsDeliveryFailure.Timeout);

        failing.WriteDelay = TimeSpan.Zero;
        await kit.Pipeline.StopAsync(CancellationToken.None);
        (await Should.ThrowAsync<AnalyticsDeliveryException>(() => consumer.HandleAsync(Delivery(), CancellationToken.None))).Reason.ShouldBe(AnalyticsDeliveryFailure.Stopped);
    }

    [Fact]
    public async Task A_full_queue_throws()
    {
        var gate = new GateSink();
        await using var kit = await PipelineKit.StartAsync(new AnalyticsOptions { QueueCapacity = 100 }, gate);
        kit.Emit("first").ShouldBeTrue();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        while (kit.Emit("filler"))
        {
        }

        (await Should.ThrowAsync<AnalyticsDeliveryException>(() => Consumer(kit).HandleAsync(Delivery(), CancellationToken.None))).Reason.ShouldBe(AnalyticsDeliveryFailure.QueueFull);
        gate.Open();
    }

    private sealed class QueueLogger(ConcurrentQueue<string> logs) : ILogger<AnalyticsOutboxConsumer>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Enqueue(formatter(state, exception));
    }
}
