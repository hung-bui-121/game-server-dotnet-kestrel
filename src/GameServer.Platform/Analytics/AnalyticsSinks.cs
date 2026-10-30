using GameServer.Analytics;

namespace GameServer.Platform.Analytics;

/// <summary>Destination of analytics records (default: JSONL files; tests: in memory; later: Couchbase, Kafka, HTTP).</summary>
public interface IAnalyticsSink
{
    /// <summary>Metric and health label, e.g. <c>file</c>. A fixed word, never an id (OBS-02).</summary>
    string Name { get; }

    /// <summary>
    /// Writes the batch in order and returns once every record is handed to the destination (file: written and flushed to
    /// the OS, to disk when the batch holds a <see cref="AnalyticsRecord.Guaranteed"/> record). Throws on any failure; the
    /// whole batch then counts as failed for this sink, and an outbox delivery in it is not acknowledged (ack rule: a payment
    /// is acknowledged only when every sink returned).
    /// </summary>
    ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken);
}

/// <summary>One finished record: UTF-8 JSON of one line, without the trailing newline. Every sink gets the same bytes.</summary>
public sealed record AnalyticsRecord(string Event, string EventId, ReadOnlyMemory<byte> Json)
{
    /// <summary>
    /// An outbox delivery (payment, at least once): its receipt is acknowledged once every sink returned, so a sink that
    /// buffers should make a batch holding such a record durable before returning (file sink: flushed to disk). Best-effort
    /// records (login) are false. Set by the pipeline.
    /// </summary>
    public bool Guaranteed { get; init; }
}

/// <summary>Best-effort emission for code outside commands (login, resume).</summary>
public interface IAnalyticsEmitter
{
    /// <summary>False when analytics is disabled or no sink is registered (callers skip their own work).</summary>
    bool IsActive { get; }

    /// <summary>
    /// Queues the event and returns at once. Never blocks, never throws: false when inactive (nothing counted), when the
    /// queue is full or the pipeline is stopping (dropped and counted), or when the type is invalid (logged).
    /// </summary>
    bool TryEmit(AnalyticsEventType type, string userId, DateTimeOffset occurredAtUtc, object fields);
}

/// <summary>An event before formatting: envelope inputs plus the type-specific fields.</summary>
/// <param name="Type">Event type (name, schema version, time field).</param>
/// <param name="EventId">32 lowercase hex characters (<see cref="AnalyticsEventIds"/>).</param>
/// <param name="UserId">Internal player id (or the anonymous id of a purged account).</param>
/// <param name="OccurredAtUtc">Instant of the event; formatted in UTC and in the deployment zone.</param>
/// <param name="ReceiptId">Receipt of the command, outbox events only.</param>
/// <param name="Fields">A <see cref="System.Text.Json.Nodes.JsonObject"/> or any object serializing to a JSON object.</param>
public sealed record AnalyticsEnvelope(
    AnalyticsEventType Type,
    string EventId,
    string UserId,
    DateTimeOffset OccurredAtUtc,
    string? ReceiptId,
    object Fields);

/// <summary>Pipeline counters for health and the GM detail (per host).</summary>
public sealed record AnalyticsStatus(long SinkErrors, long Dropped, int QueueLength, DateTimeOffset? LastErrorAtUtc, string? LastError);

/// <summary>Why an outbox delivery was not acknowledged (metric tag <c>reason</c> of <c>analytics_delivery_failures_total</c>).</summary>
public enum AnalyticsDeliveryFailure
{
    QueueFull,
    Timeout,
    SinkError,
    Stopped,
    Malformed,
}

/// <summary>Why a best-effort record was lost (metric tag <c>reason</c> of <c>analytics_events_dropped_total</c>).</summary>
public enum AnalyticsDropReason
{
    QueueFull,
    SinkError,
    Shutdown,
}

/// <summary>An outbox delivery was not acknowledged; the relay records an attempt and the sweeper retries.</summary>
public sealed class AnalyticsDeliveryException(AnalyticsDeliveryFailure reason, Exception? inner = null)
    : Exception($"Analytics delivery failed: {AnalyticsMetrics.Tag(reason)}.", inner)
{
    public AnalyticsDeliveryFailure Reason { get; } = reason;
}
