using System.Text.Json.Nodes;
using GameServer.Analytics;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Outbox;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Analytics;

/// <summary>
/// Consumer of outbox kind <c>analytics</c> (R4, FR-003, FR-013, FR-014). Inactive analytics acknowledges without writing.
/// Otherwise the envelope comes from the receipt (<c>userID</c> = receipt player, time = commit time, <c>receiptId</c>,
/// event id = digest of entry id and commit time) and the call returns only after every sink wrote the record; any
/// failure throws, so the relay records an attempt and the sweeper retries (backoff, dead letter).
/// The pipeline is resolved on the first delivery, not when the relay is built: building the relay (startup report,
/// duplicate-kind check FR-040) never reads the validated analytics options.
/// </summary>
public sealed class AnalyticsOutboxConsumer(Lazy<AnalyticsPipeline> lazyPipeline, AnalyticsMetrics metrics, ILogger<AnalyticsOutboxConsumer> logger) : IOutboxConsumer
{
    public string Kind => AnalyticsOutbox.Kind;

    public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var pipeline = lazyPipeline.Value;
        if (!pipeline.IsActive)
        {
            return Task.CompletedTask;
        }

        if (!TryReadType(delivery.Entry.Payload, out var type, out var fields))
        {
            logger.LogError("Analytics outbox entry {EntryId} has a malformed payload; it is retried and dead-lettered.", delivery.Entry.EntryId);
            metrics.DeliveryFailed(AnalyticsDeliveryFailure.Malformed);
            throw new AnalyticsDeliveryException(AnalyticsDeliveryFailure.Malformed);
        }

        var envelope = new AnalyticsEnvelope(
            type,
            AnalyticsEventIds.ForOutboxEntry(delivery.Entry.EntryId, delivery.CommittedAtUtc),
            delivery.PlayerId,
            AggregateDocumentSerializer.ParseTime(delivery.CommittedAtUtc),
            delivery.ReceiptId,
            fields);
        return pipeline.DeliverAsync(envelope, cancellationToken);
    }

    /// <summary>Reads <c>{event, schemaVersion, timeField, fields}</c> (<see cref="AnalyticsOutboxEvent"/>) without throwing.</summary>
    private static bool TryReadType(JsonNode? payload, out AnalyticsEventType type, out JsonObject fields)
    {
        type = null!;
        fields = null!;
        try
        {
            if (payload is not JsonObject root
                || root["event"]?.GetValue<string>() is not { } name
                || root["schemaVersion"]?.GetValue<int>() is not { } version
                || root["timeField"]?.GetValue<string>() is not { } timeField
                || root["fields"] is not JsonObject values)
            {
                return false;
            }

            type = new AnalyticsEventType(name, version, timeField);
            fields = values;
            return AnalyticsEventType.IsValidName(name) && version >= 1 && AnalyticsEventType.IsValidTimeField(timeField);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
