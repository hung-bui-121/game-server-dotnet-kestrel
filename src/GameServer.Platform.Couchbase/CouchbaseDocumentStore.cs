using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Couchbase;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.KeyValue;
using Couchbase.Query;
using GameServer.Platform.Persistence;
using CbDocumentExists = Couchbase.Core.Exceptions.KeyValue.DocumentExistsException;
using CbDocumentNotFound = Couchbase.Core.Exceptions.KeyValue.DocumentNotFoundException;
using CbTransactionAmbiguous = Couchbase.Client.Transactions.Error.TransactionCommitAmbiguousException;
using CbTransactionExpired = Couchbase.Client.Transactions.Error.TransactionExpiredException;
using CbTransactionFailed = Couchbase.Client.Transactions.Error.TransactionFailedException;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// <see cref="ITransactionalDocumentStore"/> on Couchbase distributed transactions (DAT-04, DAT-09, DAT-11).
/// The only component of the framework that calls the Couchbase SDK.
/// </summary>
public sealed class CouchbaseDocumentStore(CouchbaseConnection connection) : ITransactionalDocumentStore
{
    public async Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options)
    {
        var cluster = await connection.GetClusterAsync();
        T result = default!;
        try
        {
            await cluster.Transactions.RunAsync(
                async attempt => result = await body(new CouchbaseTransactionContext(attempt, connection)),
                PerTransactionConfigBuilder.Create().Timeout(options.Expiration).Build());
            return result;
        }
        catch (CbTransactionAmbiguous ex)
        {
            throw new TransactionCommitAmbiguousException("Couchbase reported an ambiguous commit.", ex);
        }
        catch (CbTransactionExpired ex)
        {
            throw new TransactionConflictExpiredException("Couchbase transaction expired.", ex);
        }
        catch (CbTransactionFailed ex)
        {
            // Exceptions thrown by the body are rethrown unchanged (contract of ITransactionalDocumentStore).
            if (FindApplicationException(ex) is { } application)
            {
                ExceptionDispatchInfo.Capture(application).Throw();
            }

            throw new TransactionFailedException("Couchbase transaction failed.", ex);
        }
    }

    public async Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        var collection = await connection.GetCollectionAsync(key.Collection);
        try
        {
            var result = await collection.GetAsync(key.Key, new GetOptions().CancellationToken(cancellationToken));
            return new StoredDocument(key, result.ContentAs<JsonObject>()!, result.Cas);
        }
        catch (CbDocumentNotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken) =>
        await Task.WhenAll(keys.Select(k => GetAsync(k, cancellationToken)));

    public async Task<bool> InsertIfAbsentAsync(DocumentKey key, JsonObject content, CancellationToken cancellationToken)
    {
        var collection = await connection.GetCollectionAsync(key.Collection);
        try
        {
            await collection.InsertAsync(key.Key, content, new InsertOptions().Durability(DurabilityLevel.Majority).CancellationToken(cancellationToken));
            return true;
        }
        catch (CbDocumentExists)
        {
            return false;
        }
    }

    public async Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken)
    {
        // Receipt is committed and never written by a transaction again (research R14).
        var collection = await connection.GetCollectionAsync(receiptKey.Collection);
        await collection.MutateInAsync(
            receiptKey.Key,
            specs => specs.Upsert("outboxPending", false),
            new MutateInOptions().Durability(DurabilityLevel.Majority).CancellationToken(cancellationToken));
    }

    public async Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(DateTimeOffset committedBefore, int limit, CancellationToken cancellationToken)
    {
        var scope = await connection.GetScopeAsync();
        var result = await scope.QueryAsync<PendingRow>(
            "SELECT META(r).id AS id, r AS doc FROM receipts AS r " +
            "WHERE r.outboxPending = true AND r.committedAtUtc < $before " +
            "ORDER BY r.committedAtUtc LIMIT $limit",
            new QueryOptions()
                .Parameter("before", Aggregates.AggregateDocumentSerializer.FormatTime(committedBefore))
                .Parameter("limit", limit)
                .ScanConsistency(QueryScanConsistency.RequestPlus)
                .CancellationToken(cancellationToken));

        var rows = new List<StoredDocument>();
        await foreach (var row in result.Rows.WithCancellation(cancellationToken))
        {
            rows.Add(new StoredDocument(new DocumentKey("receipts", row.Id), row.Doc, null));
        }

        return rows;
    }

    private static Exception? FindApplicationException(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current.GetType().Namespace?.StartsWith("Couchbase", StringComparison.Ordinal) != true)
            {
                return current;
            }
        }

        return null;
    }

    private sealed class PendingRow
    {
        public string Id { get; set; } = string.Empty;

        public JsonObject Doc { get; set; } = [];
    }
}

/// <summary>Transaction attempt wrapper. Operations are awaited sequentially as the SDK requires (V23).</summary>
internal sealed class CouchbaseTransactionContext(AttemptContext attempt, CouchbaseConnection connection) : ITransactionContext
{
    public async Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys)
    {
        var collections = new ICouchbaseCollection[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            collections[i] = await connection.GetCollectionAsync(keys[i].Collection);
        }

        return connection.Options.UseGetMulti
            ? await CouchbaseGetMultiAdapter.GetMultiAsync(attempt, keys, collections)
            : await CouchbaseGetMultiAdapter.GetSequentialAsync(attempt, keys, collections);
    }

    public async Task InsertAsync(DocumentKey key, JsonObject content)
    {
        var collection = await connection.GetCollectionAsync(key.Collection);
        try
        {
            await attempt.InsertAsync(collection, key.Key, content);
        }
        catch (CbDocumentExists ex)
        {
            throw new DocumentExistsException(key) { Source = ex.Message };
        }
    }

    public async Task ReplaceAsync(StoredDocument existing, JsonObject content)
    {
        var current = existing.Handle as TransactionGetResult;
        if (current is null)
        {
            // GetMulti does not return a TransactionGetResult (SDK 3.10): fetch it, and refuse to overwrite a
            // document that changed since the bundle read, so no update is lost.
            var collection = await connection.GetCollectionAsync(existing.Key.Collection);
            current = await attempt.GetAsync(collection, existing.Key.Key);
            if (!JsonNode.DeepEquals(current.ContentAs<JsonObject>(), existing.Content))
            {
                throw new StaleReadException(existing.Key);
            }
        }

        await attempt.ReplaceAsync(current, content);
    }
}
