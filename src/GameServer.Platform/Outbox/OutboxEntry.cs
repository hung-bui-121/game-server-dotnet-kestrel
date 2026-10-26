using System.Text.Json.Nodes;

namespace GameServer.Platform.Outbox;

/// <summary>Outbox entry committed inside a receipt (JOB-02). <c>EntryId</c> is <c>&lt;receiptId&gt;:&lt;index&gt;</c>.</summary>
public sealed record OutboxEntry(string EntryId, string Kind, JsonNode? Payload);

/// <summary>An entry plus the receipt facts a consumer may need.</summary>
public sealed record OutboxDelivery(
    OutboxEntry Entry,
    string ReceiptId,
    string PlayerId,
    string ActorId,
    string Operation,
    string CommittedAtUtc);

/// <summary>
/// Moves outbox entries of one kind to their destination. Must be idempotent by <see cref="OutboxEntry.EntryId"/>
/// because delivery is at-least-once (inline relay + sweeper).
/// </summary>
public interface IOutboxConsumer
{
    string Kind { get; }

    Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken);
}
