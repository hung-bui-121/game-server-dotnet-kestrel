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
    /// <summary>
    /// Throws when a non-transactional write targets a runner-owned collection (CMD-05, DAT-09) or one declared
    /// <see cref="GameServer.Modules.CollectionSpec.TransactionOnly"/>.
    /// </summary>
    public static void EnsureWritable(DocumentKey key, IReadOnlySet<string> transactionOnly)
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

        if (transactionOnly.Contains(key.Collection))
        {
            // A plain insert onto a key that a transaction is inserting wins silently and the transaction's write is
            // lost (TST-04 V15-T2): collections written in transactions are never written outside them.
            throw new InvalidOperationException($"Collection '{key.Collection}' is written only inside transactions (DAT-04).");
        }
    }
}
