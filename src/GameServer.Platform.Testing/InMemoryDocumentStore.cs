using System.Diagnostics;
using System.Text.Json.Nodes;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Time;

namespace GameServer.Platform.Testing;

/// <summary>
/// In-process transactional store with optimistic concurrency (research R15). Reproduces the semantics the
/// command runner relies on: atomic multi-document commit, conflict retry until expiration, staged writes
/// invisible outside the transaction, and injectable ambiguous/failed commits.
/// </summary>
public sealed class InMemoryDocumentStore(IClock? clock = null)
    : ITransactionalDocumentStore, IKeyValueStore, IPagedQueryStore, ICollectionAwareStore, IDevelopmentOnlyStore, ITransactionOnlyAwareStore
{
    private IReadOnlySet<string> _transactionOnly = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Collections that refuse plain key-value writes (set at startup from the module registry).</summary>
    public void UseTransactionOnlyCollections(IReadOnlySet<string> collections) => _transactionOnly = collections;

    private readonly Dictionary<DocumentKey, Entry> _documents = [];
    private readonly Lock _lock = new();
    private readonly IClock _clock = clock ?? new SystemClock();
    private HashSet<string>? _collections;
    private long _versionSequence;

    public Task<Page> QueryPageAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        query.Validate();
        var after = PageCursor.Decode(query.Cursor);
        lock (_lock)
        {
            var rows = _documents
                .Where(d => d.Key.Collection == query.Collection && Live(d.Key) is not null)
                .Select(d => (d.Key, Content: d.Value.Content, Order: PageCursor.StringAt(d.Value.Content, query.OrderByField)!))
                .Where(r => r.Order is not null && PageCursor.StringAt(r.Content, query.FilterField) == query.FilterValue);
            rows = query.Descending
                ? rows.OrderByDescending(r => r.Order, StringComparer.Ordinal).ThenByDescending(r => r.Key.Key, StringComparer.Ordinal)
                : rows.OrderBy(r => r.Order, StringComparer.Ordinal).ThenBy(r => r.Key.Key, StringComparer.Ordinal);
            if (after is not null)
            {
                rows = rows.Where(r =>
                {
                    var byOrder = string.CompareOrdinal(r.Order, after.Value);
                    var byKey = string.CompareOrdinal(r.Key.Key, after.Key);
                    return query.Descending ? byOrder < 0 || (byOrder == 0 && byKey < 0) : byOrder > 0 || (byOrder == 0 && byKey > 0);
                });
            }

            var page = rows.Take(query.PageSize + 1).ToArray();
            var items = page.Take(query.PageSize).Select(r => new StoredDocument(r.Key, Clone(r.Content), null)).ToArray();
            var next = page.Length > query.PageSize ? new PageCursor(page[query.PageSize - 1].Order, page[query.PageSize - 1].Key.Key).Encode() : null;
            return Task.FromResult(new Page(items, next));
        }
    }

    /// <summary>Restricts the store to provisioned collections, like Couchbase "collection not found" (review M4).</summary>
    public void UseCollections(IEnumerable<string> collections)
    {
        lock (_lock)
        {
            _collections = new HashSet<string>(collections, StringComparer.Ordinal);
        }
    }

    public Task MarkOutboxFailedAsync(DocumentKey receiptKey, int attempts, DateTimeOffset nextAttemptAt, bool deadLetter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Live(receiptKey) is { } entry)
            {
                var content = Clone(entry.Content);
                content["outboxAttempts"] = attempts;
                content["outboxNextAttemptAtUtc"] = AggregateDocumentSerializer.FormatTime(nextAttemptAt);
                content["outboxDeadLetter"] = deadLetter;
                _documents[receiptKey] = new Entry(content, ++_versionSequence, entry.ExpiresAt);
            }

            return Task.CompletedTask;
        }
    }

    public StoreFaultInjector Faults { get; } = new();

    /// <summary>Number of transaction attempts started (including retries).</summary>
    public int TransactionAttempts { get; private set; }

    public async Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options)
    {
        Faults.ThrowIfUnavailable();
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            lock (_lock)
            {
                TransactionAttempts++;
            }

            var attempt = new Attempt(this);
            var result = await body(attempt);

            var fault = Faults.Next(attempt.Writes.Keys.Select(k => k.Collection));
            switch (fault)
            {
                case StoreFault.FailBeforeCommit:
                    throw new TransactionFailedException("Injected failure before commit.");
                case StoreFault.AmbiguousBeforeCommit:
                    throw new TransactionCommitAmbiguousException("Injected ambiguous commit (not committed).");
                case StoreFault.DelayCommit:
                    await Task.Delay(Faults.CommitDelay);
                    break;
            }

            var conflict = fault == StoreFault.ConflictOnce || !TryCommit(attempt);
            if (conflict)
            {
                if (stopwatch.Elapsed >= options.Expiration)
                {
                    throw new TransactionConflictExpiredException("Transaction expired after write conflicts.");
                }

                await Task.Yield();
                continue;
            }

            if (fault == StoreFault.AmbiguousAfterCommit)
            {
                throw new TransactionCommitAmbiguousException("Injected ambiguous commit (committed).");
            }

            return result;
        }
    }

    public Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        Faults.ThrowIfUnavailable();
        lock (_lock)
        {
            return Task.FromResult(ReadCommitted(key));
        }
    }

    Task<bool> IKeyValueStore.InsertAsync(DocumentKey key, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken)
    {
        KeyValueCollections.EnsureWritable(key, _transactionOnly);
        lock (_lock)
        {
            if (Live(key) is not null)
            {
                return Task.FromResult(false);
            }

            _documents[key] = new Entry(Clone(content), ++_versionSequence, ExpiresAt(expiry));
            return Task.FromResult(true);
        }
    }

    Task<bool> IKeyValueStore.ReplaceAsync(StoredDocument existing, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken)
    {
        KeyValueCollections.EnsureWritable(existing.Key, _transactionOnly);
        lock (_lock)
        {
            var current = Live(existing.Key);
            if (current is null || current.Version != (long)existing.Handle!)
            {
                return Task.FromResult(false);
            }

            _documents[existing.Key] = new Entry(Clone(content), ++_versionSequence, ExpiresAt(expiry));
            return Task.FromResult(true);
        }
    }

    Task IKeyValueStore.RemoveAsync(DocumentKey key, CancellationToken cancellationToken)
    {
        KeyValueCollections.EnsureWritable(key, _transactionOnly);
        lock (_lock)
        {
            _documents.Remove(key);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken)
    {
        Faults.ThrowIfUnavailable();
        lock (_lock)
        {
            IReadOnlyList<StoredDocument?> result = keys.Select(ReadCommitted).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> InsertIfAbsentAsync(DocumentKey key, JsonObject content, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Live(key) is not null)
            {
                return Task.FromResult(false);
            }

            _documents[key] = new Entry(Clone(content), ++_versionSequence, null);
            return Task.FromResult(true);
        }
    }

    public Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Live(receiptKey) is { } entry)
            {
                var content = Clone(entry.Content);
                content["outboxPending"] = false;
                _documents[receiptKey] = new Entry(content, ++_versionSequence, entry.ExpiresAt);
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(DateTimeOffset committedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<StoredDocument> result = _documents
                .Where(d => d.Key.Collection == "receipts"
                            && d.Value.Content["outboxPending"]?.GetValue<bool>() == true
                            && d.Value.Content["outboxDeadLetter"]?.GetValue<bool>() != true
                            && (d.Value.Content["outboxNextAttemptAtUtc"]?.GetValue<string>() is not { } next
                                || AggregateDocumentSerializer.ParseTime(next) <= now)
                            && d.Value.Content["committedAtUtc"]?.GetValue<string>() is { } at
                            && AggregateDocumentSerializer.ParseTime(at) < committedBefore)
                .OrderBy(d => d.Value.Content["committedAtUtc"]!.GetValue<string>(), StringComparer.Ordinal)
                .Take(limit)
                .Select(d => new StoredDocument(d.Key, Clone(d.Value.Content), d.Value.Version))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <summary>Writes a committed document directly (test setup only).</summary>
    public void Seed(DocumentKey key, JsonObject content)
    {
        lock (_lock)
        {
            _documents[key] = new Entry(Clone(content), ++_versionSequence, null);
        }
    }

    /// <summary>Committed content of a document, or null.</summary>
    public JsonObject? Peek(DocumentKey key)
    {
        lock (_lock)
        {
            return Live(key) is { } entry ? Clone(entry.Content) : null;
        }
    }

    public int Count(string collection)
    {
        lock (_lock)
        {
            return _documents.Keys.Count(k => k.Collection == collection);
        }
    }

    public IReadOnlyList<(DocumentKey Key, JsonObject Content)> All(string collection)
    {
        lock (_lock)
        {
            return _documents.Where(d => d.Key.Collection == collection).Select(d => (d.Key, Clone(d.Value.Content))).ToArray();
        }
    }

    private StoredDocument? ReadCommitted(DocumentKey key) =>
        Live(key) is { } entry ? new StoredDocument(key, Clone(entry.Content), entry.Version) : null;

    private void EnsureCollection(DocumentKey key)
    {
        if (_collections is not null && !_collections.Contains(key.Collection))
        {
            throw new InvalidOperationException($"Collection '{key.Collection}' is not provisioned (declare it in a ModuleDescriptor).");
        }
    }

    /// <summary>Entry that has not expired; expired entries are removed (caller holds the lock).</summary>
    private Entry? Live(DocumentKey key)
    {
        EnsureCollection(key);
        if (!_documents.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt is { } expiresAt && expiresAt <= _clock.UtcNow)
        {
            _documents.Remove(key);
            return null;
        }

        return entry;
    }

    private DateTimeOffset? ExpiresAt(TimeSpan? expiry) => expiry is { } e ? _clock.UtcNow + e : null;

    private bool TryCommit(Attempt attempt)
    {
        lock (_lock)
        {
            foreach (var (key, write) in attempt.Writes)
            {
                var current = Live(key)?.Version ?? 0;
                if (current != write.ExpectedVersion)
                {
                    return false;
                }
            }

            foreach (var (key, write) in attempt.Writes)
            {
                _documents[key] = new Entry(write.Content, ++_versionSequence, null);
            }

            return true;
        }
    }

    private static JsonObject Clone(JsonObject content) => (JsonObject)content.DeepClone();

    private sealed record Entry(JsonObject Content, long Version, DateTimeOffset? ExpiresAt);

    private sealed record PendingWrite(JsonObject Content, long ExpectedVersion);

    private sealed class Attempt(InMemoryDocumentStore store) : ITransactionContext
    {
        public Dictionary<DocumentKey, PendingWrite> Writes { get; } = [];

        public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys)
        {
            var result = new StoredDocument?[keys.Count];
            lock (store._lock)
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    if (Writes.TryGetValue(key, out var pending))
                    {
                        result[i] = new StoredDocument(key, Clone(pending.Content), pending.ExpectedVersion);
                        continue;
                    }

                    result[i] = store.ReadCommitted(key);
                }
            }

            return Task.FromResult<IReadOnlyList<StoredDocument?>>(result);
        }

        public Task InsertAsync(DocumentKey key, JsonObject content)
        {
            lock (store._lock)
            {
                if (Writes.ContainsKey(key) || store.Live(key) is not null)
                {
                    throw new DocumentExistsException(key);
                }

                Writes[key] = new PendingWrite(Clone(content), 0);
            }

            return Task.CompletedTask;
        }

        public Task ReplaceAsync(StoredDocument existing, JsonObject content)
        {
            var expected = Writes.TryGetValue(existing.Key, out var pending) ? pending.ExpectedVersion : (long)existing.Handle!;
            Writes[existing.Key] = new PendingWrite(Clone(content), expected);
            return Task.CompletedTask;
        }
    }
}
