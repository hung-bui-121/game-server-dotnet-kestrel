using System.Diagnostics;
using System.Text.Json.Nodes;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Testing;

/// <summary>
/// In-process transactional store with optimistic concurrency (research R15). Reproduces the semantics the
/// command runner relies on: atomic multi-document commit, conflict retry until expiration, staged writes
/// invisible outside the transaction, and injectable ambiguous/failed commits.
/// </summary>
public sealed class InMemoryDocumentStore : ITransactionalDocumentStore
{
    private readonly Dictionary<DocumentKey, Entry> _documents = [];
    private readonly Lock _lock = new();
    private long _versionSequence;

    public StoreFaultInjector Faults { get; } = new();

    /// <summary>Number of transaction attempts started (including retries).</summary>
    public int TransactionAttempts { get; private set; }

    public async Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options)
    {
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
        lock (_lock)
        {
            return Task.FromResult(ReadCommitted(key));
        }
    }

    public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken)
    {
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
            if (_documents.ContainsKey(key))
            {
                return Task.FromResult(false);
            }

            _documents[key] = new Entry(Clone(content), ++_versionSequence);
            return Task.FromResult(true);
        }
    }

    public Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_documents.TryGetValue(receiptKey, out var entry))
            {
                var content = Clone(entry.Content);
                content["outboxPending"] = false;
                _documents[receiptKey] = new Entry(content, ++_versionSequence);
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(DateTimeOffset committedBefore, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<StoredDocument> result = _documents
                .Where(d => d.Key.Collection == "receipts"
                            && d.Value.Content["outboxPending"]?.GetValue<bool>() == true
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
            _documents[key] = new Entry(Clone(content), ++_versionSequence);
        }
    }

    /// <summary>Committed content of a document, or null.</summary>
    public JsonObject? Peek(DocumentKey key)
    {
        lock (_lock)
        {
            return _documents.TryGetValue(key, out var entry) ? Clone(entry.Content) : null;
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
        _documents.TryGetValue(key, out var entry) ? new StoredDocument(key, Clone(entry.Content), entry.Version) : null;

    private bool TryCommit(Attempt attempt)
    {
        lock (_lock)
        {
            foreach (var (key, version) in attempt.ReadVersions)
            {
                var current = _documents.TryGetValue(key, out var entry) ? entry.Version : 0;
                if (current != version)
                {
                    return false;
                }
            }

            foreach (var (key, write) in attempt.Writes)
            {
                var current = _documents.TryGetValue(key, out var entry) ? entry.Version : 0;
                if (current != write.ExpectedVersion)
                {
                    return false;
                }
            }

            foreach (var (key, write) in attempt.Writes)
            {
                _documents[key] = new Entry(write.Content, ++_versionSequence);
            }

            return true;
        }
    }

    private static JsonObject Clone(JsonObject content) => (JsonObject)content.DeepClone();

    private sealed record Entry(JsonObject Content, long Version);

    private sealed record PendingWrite(JsonObject Content, long ExpectedVersion);

    private sealed class Attempt(InMemoryDocumentStore store) : ITransactionContext
    {
        /// <summary>Version observed per key; 0 = observed absent.</summary>
        public Dictionary<DocumentKey, long> ReadVersions { get; } = [];

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

                    var committed = store.ReadCommitted(key);
                    var version = committed is null ? 0 : (long)committed.Handle!;
                    ReadVersions.TryAdd(key, version);
                    result[i] = committed;
                }
            }

            return Task.FromResult<IReadOnlyList<StoredDocument?>>(result);
        }

        public Task InsertAsync(DocumentKey key, JsonObject content)
        {
            lock (store._lock)
            {
                if (Writes.ContainsKey(key) || store._documents.ContainsKey(key))
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
