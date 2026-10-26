using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Couchbase;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.KeyValue;
using Couchbase.Query;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using CbCasMismatch = Couchbase.Core.Exceptions.CasMismatchException;
using CbDocumentExists = Couchbase.Core.Exceptions.KeyValue.DocumentExistsException;
using CbDocumentNotFound = Couchbase.Core.Exceptions.KeyValue.DocumentNotFoundException;
using CbTransactionAmbiguous = Couchbase.Client.Transactions.Error.TransactionCommitAmbiguousException;
using CbTransactionExpired = Couchbase.Client.Transactions.Error.TransactionExpiredException;
using CbTransactionFailed = Couchbase.Client.Transactions.Error.TransactionFailedException;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// <see cref="ITransactionalDocumentStore"/> and <see cref="IKeyValueStore"/> on Couchbase (DAT-04, DAT-09, DAT-11,
/// CMD-05). The only component of the framework that calls the Couchbase SDK.
/// </summary>
public sealed class CouchbaseDocumentStore(CouchbaseConnection connection) : ITransactionalDocumentStore, IKeyValueStore, IPagedQueryStore
{
    private const int CasRetries = 5;

    public async Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options)
    {
        var cluster = await connection.GetClusterAsync();
        T result = default!;
        Exception? bodyException = null;
        try
        {
            await cluster.Transactions.RunAsync(
                async attempt =>
                {
                    bodyException = null;
                    try
                    {
                        result = await body(new CouchbaseTransactionContext(attempt, connection));
                    }
                    catch (Exception ex)
                    {
                        // Remember what the body threw; the SDK may wrap it in a way that hides it (review B1).
                        bodyException = ex;
                        throw;
                    }
                },
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
            // Exceptions thrown by the body itself are rethrown unchanged (contract of ITransactionalDocumentStore).
            if (bodyException is not null && !CouchbaseErrors.IsSdkException(bodyException))
            {
                ExceptionDispatchInfo.Capture(bodyException).Throw();
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

    public Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken) =>
        MutateCommittedReceiptAsync(receiptKey, specs => specs.Upsert("outboxPending", false), cancellationToken);

    public Task MarkOutboxFailedAsync(DocumentKey receiptKey, int attempts, DateTimeOffset nextAttemptAt, bool deadLetter, CancellationToken cancellationToken) =>
        MutateCommittedReceiptAsync(
            receiptKey,
            specs => specs
                .Upsert("outboxAttempts", attempts)
                .Upsert("outboxNextAttemptAtUtc", AggregateDocumentSerializer.FormatTime(nextAttemptAt))
                .Upsert("outboxDeadLetter", deadLetter),
            cancellationToken);

    public async Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(
        DateTimeOffset committedBefore,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        var scope = await connection.GetScopeAsync();
        var result = await scope.QueryAsync<PendingRow>(
            "SELECT META(r).id AS id, r AS doc FROM receipts AS r " +
            "WHERE r.outboxPending = true AND r.committedAtUtc < $before " +
            "AND IFMISSINGORNULL(r.outboxDeadLetter, false) = false " +
            "AND (r.outboxNextAttemptAtUtc IS MISSING OR r.outboxNextAttemptAtUtc IS NULL OR r.outboxNextAttemptAtUtc <= $now) " +
            "ORDER BY r.committedAtUtc LIMIT $limit",
            new QueryOptions()
                .Parameter("before", AggregateDocumentSerializer.FormatTime(committedBefore))
                .Parameter("now", AggregateDocumentSerializer.FormatTime(now))
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

    public async Task<Page> QueryPageAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        query.Validate();
        var after = PageCursor.Decode(query.Cursor);
        var filter = Path(query.FilterField);
        var order = Path(query.OrderByField);
        var direction = query.Descending ? "DESC" : "ASC";
        var comparison = query.Descending ? "<" : ">";
        var statement =
            $"SELECT META(d).id AS id, d AS doc, TOSTRING(IFMISSINGORNULL({order}, '')) AS ord FROM `{query.Collection}` AS d " +
            $"WHERE {filter} = $value " +
            (after is null ? string.Empty : $"AND (TOSTRING(IFMISSINGORNULL({order}, '')) {comparison} $cv OR (TOSTRING(IFMISSINGORNULL({order}, '')) = $cv AND META(d).id {comparison} $ck)) ") +
            $"ORDER BY ord {direction}, META(d).id {direction} LIMIT $limit";
        var options = new QueryOptions()
            .Parameter("value", query.FilterValue)
            .Parameter("limit", query.PageSize + 1)
            .ScanConsistency(QueryScanConsistency.RequestPlus)
            .CancellationToken(cancellationToken);
        if (after is not null)
        {
            options.Parameter("cv", after.Value).Parameter("ck", after.Key);
        }

        var scope = await connection.GetScopeAsync();
        var result = await scope.QueryAsync<PagedRow>(statement, options);
        var rows = new List<PagedRow>();
        await foreach (var row in result.Rows.WithCancellation(cancellationToken))
        {
            rows.Add(row);
        }

        var items = rows.Take(query.PageSize).Select(r => new StoredDocument(new DocumentKey(query.Collection, r.Id), r.Doc, null)).ToArray();
        var next = rows.Count > query.PageSize ? new PageCursor(rows[query.PageSize - 1].Ord, rows[query.PageSize - 1].Id).Encode() : null;
        return new Page(items, next);
    }

    private static string Path(string field) => "d." + string.Join('.', field.Split('.').Select(s => $"`{s}`"));

    async Task<bool> IKeyValueStore.InsertAsync(DocumentKey key, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken)
    {
        KeyValueCollections.EnsureWritable(key);
        var collection = await connection.GetCollectionAsync(key.Collection);
        var options = new InsertOptions().Durability(DurabilityLevel.Majority).CancellationToken(cancellationToken);
        if (expiry is { } e)
        {
            options.Expiry(e);
        }

        try
        {
            await collection.InsertAsync(key.Key, content, options);
            return true;
        }
        catch (CbDocumentExists)
        {
            return false;
        }
    }

    async Task<bool> IKeyValueStore.ReplaceAsync(StoredDocument existing, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken)
    {
        KeyValueCollections.EnsureWritable(existing.Key);
        var collection = await connection.GetCollectionAsync(existing.Key.Collection);
        var options = new ReplaceOptions().Cas((ulong)existing.Handle!).Durability(DurabilityLevel.Majority).CancellationToken(cancellationToken);
        if (expiry is { } e)
        {
            options.Expiry(e);
        }

        try
        {
            await collection.ReplaceAsync(existing.Key.Key, content, options);
            return true;
        }
        catch (Exception ex) when (ex is CbCasMismatch or CbDocumentNotFound)
        {
            return false;
        }
    }

    async Task IKeyValueStore.RemoveAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        KeyValueCollections.EnsureWritable(key);
        var collection = await connection.GetCollectionAsync(key.Collection);
        try
        {
            await collection.RemoveAsync(key.Key, new RemoveOptions().Durability(DurabilityLevel.Majority).CancellationToken(cancellationToken));
        }
        catch (CbDocumentNotFound)
        {
        }
    }

    /// <summary>
    /// Sub-document update of a committed receipt guarded by the CAS of a fresh read (review M2): a receipt still being
    /// unstaged is invisible (not found) and left to the sweeper; a concurrent change retries.
    /// </summary>
    private async Task MutateCommittedReceiptAsync(DocumentKey receiptKey, Action<MutateInSpecBuilder> specs, CancellationToken cancellationToken)
    {
        var collection = await connection.GetCollectionAsync(receiptKey.Collection);
        for (var attempt = 0; attempt < CasRetries; attempt++)
        {
            ulong cas;
            try
            {
                cas = (await collection.GetAsync(receiptKey.Key, new GetOptions().CancellationToken(cancellationToken))).Cas;
            }
            catch (CbDocumentNotFound)
            {
                return;
            }

            try
            {
                await collection.MutateInAsync(
                    receiptKey.Key,
                    specs,
                    new MutateInOptions().Cas(cas).Durability(DurabilityLevel.Majority).CancellationToken(cancellationToken));
                return;
            }
            catch (CbCasMismatch)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private sealed class PagedRow
    {
        public string Id { get; set; } = string.Empty;

        public JsonObject Doc { get; set; } = [];

        public string Ord { get; set; } = string.Empty;
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
            ? await CouchbaseGetMultiAdapter.GetMultiAsync(attempt, keys, collections, connection.Transcoder)
            : await CouchbaseGetMultiAdapter.GetSequentialAsync(attempt, keys, collections);
    }

    public async Task InsertAsync(DocumentKey key, JsonObject content)
    {
        var collection = await connection.GetCollectionAsync(key.Collection);
        try
        {
            await attempt.InsertAsync(collection, key.Key, content);
        }
        catch (Exception ex) when (CouchbaseErrors.Contains<CbDocumentExists>(ex))
        {
            // Whatever wrapper the SDK uses, a duplicate insert becomes the platform's DocumentExistsException (review B1).
            throw new DocumentExistsException(key);
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

/// <summary>Inspection of SDK exception chains (InnerException and the transaction "Cause" property).</summary>
internal static class CouchbaseErrors
{
    public static bool IsSdkException(Exception exception) =>
        exception.GetType().Namespace?.StartsWith("Couchbase", StringComparison.Ordinal) == true;

    public static bool Contains<TException>(Exception exception)
        where TException : Exception
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current is TException)
            {
                return true;
            }

            if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }

            if (current.GetType().GetProperty("Cause")?.GetValue(current) is Exception cause)
            {
                pending.Push(cause);
            }
        }

        return false;
    }
}
