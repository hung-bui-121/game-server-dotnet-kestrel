using System.Text.Json.Nodes;
using GameServer.Platform.Commands;

namespace GameServer.Platform.Persistence;

/// <summary>Transaction view for <see cref="IAuditedStore"/>: no player aggregates, no receipts.</summary>
public interface IAuditedTransaction
{
    Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys);

    Task InsertAsync(DocumentKey key, JsonObject content);

    Task ReplaceAsync(StoredDocument existing, JsonObject content);
}

/// <summary>
/// GM changes to documents that are not player data (staff, operational settings) in one transaction with their
/// audit entry (SEC-14): a transaction that writes anything must insert exactly one <c>gm_audit</c> document, else it
/// rolls back. Player aggregates and receipts are refused — they go through the command runner (CMD-05, DAT-09).
/// </summary>
public interface IAuditedStore
{
    /// <summary>The body may run several times (transaction retries); exceptions roll back and are rethrown.</summary>
    Task<T> RunAsync<T>(Func<IAuditedTransaction, Task<T>> body);
}

public sealed class AuditedStore(ITransactionalDocumentStore store, GameServerOptions options) : IAuditedStore
{
    public const string AuditCollection = GameServer.Commands.AuditExtensions.OutboxKind;

    public Task<T> RunAsync<T>(Func<IAuditedTransaction, Task<T>> body) =>
        // The request token is not passed to the commit (PERF-02).
        store.RunTransactionAsync(
            async tx =>
            {
                var guarded = new GuardedTransaction(tx);
                var result = await body(guarded);
                if (guarded.Writes > 0 && guarded.AuditInserts != 1)
                {
                    throw new InvalidOperationException(
                        $"An audited change wrote {guarded.Writes} document(s) with {guarded.AuditInserts} audit entries; exactly one is required (SEC-14).");
                }

                return result;
            },
            new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds)));

    private sealed class GuardedTransaction(ITransactionContext inner) : IAuditedTransaction
    {
        public int Writes { get; private set; }

        public int AuditInserts { get; private set; }

        public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys) => inner.GetManyAsync(keys);

        public Task InsertAsync(DocumentKey key, JsonObject content)
        {
            Guard(key);
            Writes++;
            if (key.Collection == AuditCollection)
            {
                AuditInserts++;
            }

            return inner.InsertAsync(key, content);
        }

        public Task ReplaceAsync(StoredDocument existing, JsonObject content)
        {
            Guard(existing.Key);
            if (existing.Key.Collection == AuditCollection)
            {
                throw new InvalidOperationException("Audit entries are immutable (SEC-14).");
            }

            Writes++;
            return inner.ReplaceAsync(existing, content);
        }

        private static void Guard(DocumentKey key)
        {
            if (key.Collection == ReceiptDocument.Collection || key.Collection.StartsWith("pa_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Collection '{key.Collection}' is written only by the command runner (CMD-05).");
            }
        }
    }
}
