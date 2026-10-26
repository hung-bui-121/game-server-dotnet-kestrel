using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Commands;

/// <summary>Committed aggregate revision recorded in a receipt.</summary>
public sealed record ReceiptAggregate(string AggregateId, long NewRev);

/// <summary>
/// Receipt <c>rcpt::{receiptId}</c> inserted in the same transaction as the state change (CMD-03, data-model.md).
/// It is the idempotency entry (bodyHash, result) and the outbox carrier.
/// </summary>
public sealed class ReceiptDocument
{
    public const string Collection = "receipts";
    public const string KeyPrefix = "rcpt::";

    public required string ReceiptId { get; init; }

    public required string GameId { get; init; }

    public required string EnvironmentId { get; init; }

    public required string ActorId { get; init; }

    public required string ActorType { get; init; }

    public required string PlayerId { get; init; }

    public required string Operation { get; init; }

    public required string RequestId { get; init; }

    public required string BodyHash { get; init; }

    public required string ResultCode { get; init; }

    /// <summary>Base64 of the module result bytes (≤ 512 B before encoding).</summary>
    public required string ResultCompact { get; init; }

    public required string ConfigVersion { get; init; }

    public required Dictionary<string, int> SchemaVersions { get; init; }

    public required List<ReceiptAggregate> Aggregates { get; init; }

    public required List<string> ClaimKeys { get; init; }

    public required string CommittedAtUtc { get; init; }

    public required List<OutboxEntry> Outbox { get; init; }

    public required bool OutboxPending { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Failed relay attempts (sweeper backoff, review M1).</summary>
    public int OutboxAttempts { get; init; }

    public string? OutboxNextAttemptAtUtc { get; init; }

    /// <summary>Relay gave up after the maximum attempts; needs operator attention.</summary>
    public bool OutboxDeadLetter { get; init; }

    public static DocumentKey KeyFor(string receiptId) => new(Collection, KeyPrefix + receiptId);

    public DateTimeOffset CommittedAt => AggregateDocumentSerializer.ParseTime(CommittedAtUtc);

    public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, GameServerJson.Options)!;

    public static ReceiptDocument FromJson(JsonObject json) =>
        json.Deserialize<ReceiptDocument>(GameServerJson.Options)
        ?? throw new InvalidOperationException("Receipt document is empty.");

    public static int SizeOf(JsonObject json) => Encoding.UTF8.GetByteCount(json.ToJsonString(GameServerJson.Options));
}

/// <summary>Deterministic ids and hashes of the idempotency contract (CMD-01, CMD-02).</summary>
public static class ReceiptIds
{
    /// <summary>
    /// <c>base64url(SHA-256(actorId | playerId | operation | requestId))[0..32]</c>. The target player is part of the
    /// key so a GM reusing a requestId for another player never replays the first player's receipt.
    /// </summary>
    public static string Compute(string actorId, string playerId, string operation, string requestId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{actorId}|{playerId}|{operation}|{requestId}"));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..32];
    }

    /// <summary>Lower-case hex SHA-256 over the raw request bytes as received.</summary>
    public static string BodyHash(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));
}
