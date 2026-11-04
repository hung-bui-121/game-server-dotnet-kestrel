using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Tests.Infrastructure;

/// <summary>
/// Records the operations of transaction attempts (<c>read a,b</c>, <c>insert c</c>, <c>replace c</c>, <c>remove c</c>) and lets a test act
/// between them; a staging spy for the "rules before staging" tests.
/// </summary>
public sealed class RecordingStore(ITransactionalDocumentStore inner) : ITransactionalDocumentStore
{
    public List<string> Operations { get; } = [];

    public int BodyRuns { get; private set; }

    public Func<IReadOnlyList<DocumentKey>, Task>? AfterRead { get; set; }

    public Func<DocumentKey, Task>? AfterInsert { get; set; }

    /// <summary>Profile inserts that report "document exists" (as Couchbase does for a lost insert race) before reaching the store.</summary>
    public int InsertRacesToFake { get; set; }

    public Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options) =>
        inner.RunTransactionAsync(
            tx =>
            {
                BodyRuns++;
                return body(new RecordingContext(this, tx));
            },
            options);

    /// <summary>Key-value reads outside transactions (<see cref="GetAsync"/>, <see cref="GetManyAsync"/>).</summary>
    public int Reads { get; private set; }

    public Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        Reads++;
        return inner.GetAsync(key, cancellationToken);
    }

    public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken)
    {
        Reads++;
        return inner.GetManyAsync(keys, cancellationToken);
    }

    public Task<bool> InsertIfAbsentAsync(DocumentKey key, JsonObject content, CancellationToken cancellationToken) =>
        inner.InsertIfAbsentAsync(key, content, cancellationToken);

    public Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken) =>
        inner.MarkOutboxRelayedAsync(receiptKey, cancellationToken);

    public Task MarkOutboxFailedAsync(DocumentKey receiptKey, int attempts, DateTimeOffset nextAttemptAt, bool deadLetter, CancellationToken cancellationToken) =>
        inner.MarkOutboxFailedAsync(receiptKey, attempts, nextAttemptAt, deadLetter, cancellationToken);

    public Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(DateTimeOffset committedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        inner.QueryPendingOutboxAsync(committedBefore, now, limit, cancellationToken);

    private sealed class RecordingContext(RecordingStore store, ITransactionContext tx) : ITransactionContext
    {
        public async Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys)
        {
            store.Operations.Add("read " + string.Join(",", keys.Select(k => k.Collection)));
            var documents = await tx.GetManyAsync(keys);
            if (store.AfterRead is { } afterRead)
            {
                await afterRead(keys);
            }

            return documents;
        }

        public async Task InsertAsync(DocumentKey key, JsonObject content)
        {
            store.Operations.Add("insert " + key.Collection);
            if (key.Collection == "pa_profile" && store.InsertRacesToFake > 0)
            {
                store.InsertRacesToFake--;
                throw new DocumentExistsException(key);
            }

            await tx.InsertAsync(key, content);
            if (store.AfterInsert is { } afterInsert)
            {
                await afterInsert(key);
            }
        }

        public Task ReplaceAsync(StoredDocument existing, JsonObject content)
        {
            store.Operations.Add("replace " + existing.Key.Collection);
            return tx.ReplaceAsync(existing, content);
        }

        public Task RemoveAsync(StoredDocument existing)
        {
            store.Operations.Add("remove " + existing.Key.Collection);
            return tx.RemoveAsync(existing);
        }
    }
}
