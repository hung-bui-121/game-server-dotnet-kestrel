using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Commands;

/// <summary>
/// Everything a receipt is built from (research R14). The same inputs give byte-identical receipts in the runner, the account
/// creation and the test kit; the variable-length values are the ids, the config version, the revisions and the outbox.
/// </summary>
/// <param name="SchemaAggregates">Aggregates whose schema versions the receipt records, in that order (Touches ∪ Reads).</param>
/// <param name="Aggregates">Written aggregates with their new revision, in write order.</param>
/// <param name="ClaimKeys">Claim markers reserved by the run, in reservation order.</param>
internal readonly record struct ReceiptInputs(
    string ReceiptId,
    string GameId,
    string EnvironmentId,
    string ActorId,
    ActorType ActorType,
    string PlayerId,
    string Operation,
    string RequestId,
    string BodyHash,
    ErrorCode ResultCode,
    byte[] ResultCompact,
    string ConfigVersion,
    IEnumerable<PlayerAggregateDescriptor> SchemaAggregates,
    IEnumerable<ReceiptAggregate> Aggregates,
    IEnumerable<DocumentKey> ClaimKeys,
    IEnumerable<OutboxEntry> Outbox,
    string CorrelationId,
    DateTimeOffset CommittedAt);

/// <summary>The one definition of a receipt's content (CMD-03), shared by the runner and the test kit (SC-005).</summary>
internal static class ReceiptBuilder
{
    internal static ReceiptDocument Build(in ReceiptInputs inputs)
    {
        var outbox = inputs.Outbox.ToList();
        return new ReceiptDocument
        {
            ReceiptId = inputs.ReceiptId,
            GameId = inputs.GameId,
            EnvironmentId = inputs.EnvironmentId,
            ActorId = inputs.ActorId,
            ActorType = inputs.ActorType switch
            {
                ActorType.Player => "player",
                ActorType.Gm => "gm",
                _ => "system",
            },
            PlayerId = inputs.PlayerId,
            Operation = inputs.Operation,
            RequestId = inputs.RequestId,
            BodyHash = inputs.BodyHash,
            ResultCode = inputs.ResultCode.Name,
            ResultCompact = Convert.ToBase64String(inputs.ResultCompact),
            ConfigVersion = inputs.ConfigVersion,
            SchemaVersions = inputs.SchemaAggregates.ToDictionary(a => a.AggregateId, a => a.SchemaVersion, StringComparer.Ordinal),
            Aggregates = inputs.Aggregates.ToList(),
            ClaimKeys = inputs.ClaimKeys.Select(k => $"{k.Collection}/{k.Key}").ToList(),
            CommittedAtUtc = AggregateDocumentSerializer.FormatTime(inputs.CommittedAt),
            Outbox = outbox,
            OutboxPending = outbox.Count > 0,
            CorrelationId = inputs.CorrelationId,
        };
    }
}
