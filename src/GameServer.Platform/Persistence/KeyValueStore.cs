using System.Text.Json.Nodes;
using GameServer.Platform.Commands;

namespace GameServer.Platform.Persistence;

/// <summary>
/// Non-transactional document access with Insert/CAS (CMD-05) for collections outside the command
/// transaction: <c>identities</c>, <c>sessions</c>, <c>gate</c>, <c>gm_staff</c>… Collections <c>pa_*</c> and
/// <c>receipts</c> are refused: they are written only by the command runner / aggregate writer.
/// </summary>
public interface IKeyValueStore
{
    Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken);

    /// <summary>Inserts if absent. Returns false if the key exists.</summary>
    Task<bool> InsertAsync(DocumentKey key, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken);

    /// <summary>Replaces when the document is unchanged since <paramref name="existing"/> was read (CAS). False on mismatch or missing.</summary>
    Task<bool> ReplaceAsync(StoredDocument existing, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken);

    /// <summary>Removes the document if it exists.</summary>
    Task RemoveAsync(DocumentKey key, CancellationToken cancellationToken);
}

public static class KeyValueCollections
{
    /// <summary>Throws when a non-transactional write targets a runner-owned collection (CMD-05, DAT-09).</summary>
    public static void EnsureWritable(DocumentKey key)
    {
        if (key.Collection == ReceiptDocument.Collection || key.Collection.StartsWith("pa_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Collection '{key.Collection}' is written only through the command runner or IPlayerAggregateWriter (CMD-05).");
        }

        if (key.Collection == GameServer.Commands.AuditExtensions.OutboxKind)
        {
            throw new InvalidOperationException($"Collection '{key.Collection}' is written only by the audit projection (SEC-14).");
        }
    }
}
