using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>
/// Slice 011 T012: the internal persistence seam of <see cref="InMemoryDocumentStore"/> (research R6). Every mutation path
/// persists once, with its full change set, after its checks and before the change is visible; the store refuses access
/// until it is loaded.
/// </summary>
public sealed class InMemoryPersistenceSeamTests
{
    private static readonly TransactionRunOptions Options = new(TimeSpan.FromSeconds(10));
    private static readonly DocumentKey Doc = new("docs", "a");
    private static readonly DocumentKey Receipt = new("receipts", "rcpt::a");

    private readonly FakeClock _clock = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly InMemoryDocumentStore _store;

    public InMemoryPersistenceSeamTests()
    {
        _store = new InMemoryDocumentStore(_clock, _persistence);
        _store.LoadCommitted([]);
    }

    private IKeyValueStore Kv => _store;

    [Fact]
    public async Task A_transaction_persists_its_whole_change_set_once()
    {
        _store.Seed(Doc, new JsonObject { ["v"] = 0 });
        _persistence.Calls.Clear();
        var other = new DocumentKey("docs", "b");

        await _store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(other, new JsonObject { ["v"] = 1 });
            await tx.RemoveAsync((await tx.GetManyAsync([Doc]))[0]!);
            return true;
        }, Options);

        var call = _persistence.Calls.ShouldHaveSingleItem();
        call.Count.ShouldBe(2);
        call.ShouldContain(c => c.Key == other && c.Content!["v"]!.GetValue<int>() == 1 && c.ExpiresAt == null);
        call.ShouldContain(c => c.Key == Doc && c.Content == null);
    }

    [Fact]
    public async Task Key_value_writes_persist_once_with_the_absolute_expiry()
    {
        var expiry = TimeSpan.FromMinutes(10);

        (await Kv.InsertAsync(Doc, new JsonObject { ["v"] = 1 }, expiry, CancellationToken.None)).ShouldBeTrue();
        (await Kv.InsertAsync(Doc, new JsonObject { ["v"] = 2 }, expiry, CancellationToken.None)).ShouldBeFalse();
        var insert = _persistence.Calls.ShouldHaveSingleItem().ShouldHaveSingleItem();
        insert.ExpiresAt.ShouldBe(_clock.UtcNow + expiry);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var read = (await Kv.GetAsync(Doc, CancellationToken.None))!;
        (await Kv.ReplaceAsync(read, new JsonObject { ["v"] = 3 }, expiry, CancellationToken.None)).ShouldBeTrue();
        (await Kv.ReplaceAsync(read, new JsonObject { ["v"] = 4 }, expiry, CancellationToken.None)).ShouldBeFalse();
        _persistence.Calls.Count.ShouldBe(2);
        _persistence.Calls[1].ShouldHaveSingleItem().ExpiresAt.ShouldBe(_clock.UtcNow + expiry);

        await Kv.RemoveAsync(Doc, CancellationToken.None);
        await Kv.RemoveAsync(Doc, CancellationToken.None);
        _persistence.Calls.Count.ShouldBe(3);
        _persistence.Calls[2].ShouldHaveSingleItem().ShouldBe(new DocumentChange(Doc, null, null));
    }

    [Fact]
    public async Task Projection_outbox_and_seed_writes_persist_once()
    {
        (await _store.InsertIfAbsentAsync(Doc, new JsonObject { ["v"] = 1 }, CancellationToken.None)).ShouldBeTrue();
        (await _store.InsertIfAbsentAsync(Doc, new JsonObject { ["v"] = 2 }, CancellationToken.None)).ShouldBeFalse();
        _persistence.Calls.Count.ShouldBe(1);

        _store.Seed(Receipt, new JsonObject { ["outboxPending"] = true });
        _persistence.Calls.Count.ShouldBe(2);

        await _store.MarkOutboxFailedAsync(Receipt, 1, _clock.UtcNow, deadLetter: false, CancellationToken.None);
        _persistence.Calls[2].ShouldHaveSingleItem().Content!["outboxAttempts"]!.GetValue<int>().ShouldBe(1);

        await _store.MarkOutboxRelayedAsync(Receipt, CancellationToken.None);
        _persistence.Calls[3].ShouldHaveSingleItem().Content!["outboxPending"]!.GetValue<bool>().ShouldBeFalse();

        await _store.MarkOutboxRelayedAsync(new DocumentKey("receipts", "rcpt::missing"), CancellationToken.None);
        _persistence.Calls.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Expiry_at_read_time_is_not_persisted()
    {
        await Kv.InsertAsync(Doc, new JsonObject { ["v"] = 1 }, TimeSpan.FromSeconds(1), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(2));

        (await _store.GetAsync(Doc, CancellationToken.None)).ShouldBeNull();
        _persistence.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_throwing_persistence_applies_nothing()
    {
        _persistence.Failure = new IOException("disk full");

        var failed = await Should.ThrowAsync<TransactionFailedException>(() => _store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(Doc, new JsonObject { ["v"] = 1 });
            return true;
        }, Options));
        failed.InnerException.ShouldBeOfType<IOException>();
        await Should.ThrowAsync<IOException>(() => Kv.InsertAsync(Doc, new JsonObject { ["v"] = 1 }, null, CancellationToken.None));
        await Should.ThrowAsync<IOException>(() => _store.InsertIfAbsentAsync(Doc, new JsonObject { ["v"] = 1 }, CancellationToken.None));

        _persistence.Failure = null;
        _store.Peek(Doc).ShouldBeNull();
        _persistence.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_conflict_retried_transaction_persists_only_the_committed_attempt()
    {
        _store.Seed(Doc, new JsonObject { ["v"] = 0 });
        var attempts = 0;

        await _store.RunTransactionAsync(async tx =>
        {
            attempts++;
            var current = (await tx.GetManyAsync([Doc]))[0]!;
            if (attempts == 1)
            {
                await _store.RunTransactionAsync(async other =>
                {
                    await other.ReplaceAsync((await other.GetManyAsync([Doc]))[0]!, new JsonObject { ["v"] = 10 });
                    return true;
                }, Options);
            }

            await tx.ReplaceAsync(current, new JsonObject { ["v"] = current.Content["v"]!.GetValue<int>() + 1 });
            return true;
        }, Options);

        attempts.ShouldBe(2);
        _persistence.Calls.Count.ShouldBe(3); // seed, the concurrent replace, the committed second attempt
        _persistence.Calls[2].ShouldHaveSingleItem().Content!["v"]!.GetValue<int>().ShouldBe(11);
    }

    [Fact]
    public async Task Access_before_loading_throws_but_collection_declarations_are_allowed()
    {
        var store = new InMemoryDocumentStore(_clock, _persistence);

        store.UseCollections(["docs"]);
        store.UseTransactionOnlyCollections(new HashSet<string>(StringComparer.Ordinal));
        await Should.ThrowAsync<InvalidOperationException>(() => store.GetAsync(Doc, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => store.RunTransactionAsync(_ => Task.FromResult(true), Options));
        Should.Throw<InvalidOperationException>(() => store.Seed(Doc, new JsonObject()));

        store.LoadCommitted([(Doc, new JsonObject { ["v"] = 7 }, null)]);
        store.Peek(Doc)!["v"]!.GetValue<int>().ShouldBe(7);
        store.DeclaredCollections.ShouldNotBeNull().ShouldContain("docs");
    }

    [Fact]
    public async Task An_injected_failure_before_commit_fires_before_persisting()
    {
        _store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        await Should.ThrowAsync<TransactionFailedException>(() => _store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(Doc, new JsonObject { ["v"] = 1 });
            return true;
        }, Options));

        _persistence.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_public_constructor_has_no_gate_and_no_persistence()
    {
        var store = new InMemoryDocumentStore(_clock);

        await ((IKeyValueStore)store).InsertAsync(Doc, new JsonObject { ["v"] = 1 }, null, CancellationToken.None);
        store.Peek(Doc).ShouldNotBeNull();
        store.DeclaredCollections.ShouldBeNull();
    }

    private sealed class RecordingPersistence : IDocumentPersistence
    {
        public List<IReadOnlyList<DocumentChange>> Calls { get; } = [];

        public Exception? Failure { get; set; }

        public void Persist(IReadOnlyList<DocumentChange> changes)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Calls.Add(changes.ToArray());
        }
    }
}
