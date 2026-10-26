namespace GameServer.Platform.Testing;

public enum StoreFault
{
    /// <summary>Attempt fails before commit; nothing is written (TransactionFailedException).</summary>
    FailBeforeCommit,

    /// <summary>Ambiguous error reported but the commit did NOT happen.</summary>
    AmbiguousBeforeCommit,

    /// <summary>Commit happened, then an ambiguous error is reported (CMD-04).</summary>
    AmbiguousAfterCommit,

    /// <summary>The attempt hits a write conflict and is retried.</summary>
    ConflictOnce,

    /// <summary>Commit is delayed by <see cref="StoreFaultInjector.CommitDelay"/>.</summary>
    DelayCommit,
}

/// <summary>Queue of faults consumed by the next transactions of <see cref="InMemoryDocumentStore"/>.</summary>
public sealed class StoreFaultInjector
{
    private readonly Queue<StoreFault> _faults = new();
    private readonly Lock _lock = new();

    public TimeSpan CommitDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>When true, every read and transaction fails as if the store were unreachable (health tests).</summary>
    public bool Unavailable { get; set; }

    internal void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new InvalidOperationException("Store unavailable (injected fault).");
        }
    }

    /// <summary>When set, only transactions that write a key in this collection consume faults.</summary>
    public string? OnlyWhenWritingCollection { get; set; }

    public void Enqueue(StoreFault fault, int times = 1)
    {
        lock (_lock)
        {
            for (var i = 0; i < times; i++)
            {
                _faults.Enqueue(fault);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _faults.Clear();
        }
    }

    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _faults.Count;
            }
        }
    }

    internal StoreFault? Next(IEnumerable<string> writtenCollections)
    {
        lock (_lock)
        {
            if (_faults.Count == 0)
            {
                return null;
            }

            if (OnlyWhenWritingCollection is { } only && !writtenCollections.Contains(only, StringComparer.Ordinal))
            {
                return null;
            }

            return _faults.Dequeue();
        }
    }
}
