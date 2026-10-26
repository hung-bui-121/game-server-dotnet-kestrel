using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

public sealed class InMemoryStoreConformanceTests : StoreConformanceTests
{
    private readonly InMemoryDocumentStore _store = new();

    protected override ITransactionalDocumentStore Store => _store;
}

/// <summary>Fault injection behaviour specific to the in-memory store.</summary>
public sealed class InMemoryDocumentStoreFaultTests
{
    private readonly InMemoryDocumentStore _store = new();
    private static readonly TransactionRunOptions Options = new(TimeSpan.FromSeconds(10));

    private Task Insert(DocumentKey key) => _store.RunTransactionAsync(async tx =>
    {
        await tx.InsertAsync(key, new() { ["v"] = 1 });
        return true;
    }, Options);

    [Fact]
    public async Task Ambiguous_after_commit_commits()
    {
        var key = new DocumentKey("d", "a");
        _store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        await Should.ThrowAsync<TransactionCommitAmbiguousException>(() => Insert(key));
        _store.Peek(key).ShouldNotBeNull();
    }

    [Fact]
    public async Task Ambiguous_before_commit_does_not_commit()
    {
        var key = new DocumentKey("d", "b");
        _store.Faults.Enqueue(StoreFault.AmbiguousBeforeCommit);

        await Should.ThrowAsync<TransactionCommitAmbiguousException>(() => Insert(key));
        _store.Peek(key).ShouldBeNull();
    }

    [Fact]
    public async Task Fail_before_commit_does_not_commit()
    {
        var key = new DocumentKey("d", "c");
        _store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        await Should.ThrowAsync<TransactionFailedException>(() => Insert(key));
        _store.Peek(key).ShouldBeNull();
    }

    [Fact]
    public async Task Conflict_is_retried_then_commits()
    {
        var key = new DocumentKey("d", "e");
        _store.Faults.Enqueue(StoreFault.ConflictOnce, 2);

        await Insert(key);
        _store.Peek(key).ShouldNotBeNull();
        _store.TransactionAttempts.ShouldBe(3);
    }

    [Fact]
    public async Task Faults_can_target_one_collection()
    {
        _store.Faults.OnlyWhenWritingCollection = "receipts";
        _store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        await Insert(new DocumentKey("other", "x"));
        _store.Faults.Pending.ShouldBe(1);
        await Should.ThrowAsync<TransactionFailedException>(() => Insert(new DocumentKey("receipts", "x")));
    }
}
