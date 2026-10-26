using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>
/// Behaviour every <see cref="ITransactionalDocumentStore"/> must have (research R3). Runs against the
/// in-memory store here and against Couchbase in the integration test project.
/// </summary>
public abstract class StoreConformanceTests
{
    /// <summary>Collections the store must have: <c>conf_docs</c> and <c>receipts</c>.</summary>
    protected const string Docs = "conf_docs";

    protected static readonly TransactionRunOptions Options = new(TimeSpan.FromSeconds(10));

    protected abstract ITransactionalDocumentStore Store { get; }

    protected static DocumentKey NewKey(string collection = Docs) => new(collection, "k-" + Guid.NewGuid().ToString("N"));

    protected static JsonObject Doc(int value) => new() { ["value"] = value };

    [Fact]
    public async Task Commit_makes_all_writes_visible_together()
    {
        var a = NewKey();
        var b = NewKey();
        await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(a, Doc(1));
            await tx.InsertAsync(b, Doc(2));
            return true;
        }, Options);

        var read = await Store.GetManyAsync([a, b, NewKey()], CancellationToken.None);
        read[0]!.Content["value"]!.GetValue<int>().ShouldBe(1);
        read[1]!.Content["value"]!.GetValue<int>().ShouldBe(2);
        read[2].ShouldBeNull();
    }

    [Fact]
    public async Task Body_exception_rolls_back_and_is_rethrown()
    {
        var a = NewKey();
        await Should.ThrowAsync<ApplicationException>(() => Store.RunTransactionAsync<bool>(async tx =>
        {
            await tx.InsertAsync(a, Doc(1));
            throw new ApplicationException("boom");
        }, Options));

        (await Store.GetAsync(a, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Staged_insert_is_invisible_outside_the_transaction()
    {
        var a = NewKey();
        StoredDocument? seenOutside = null;
        await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(a, Doc(1));
            seenOutside = await Store.GetAsync(a, CancellationToken.None);
            return true;
        }, Options);

        seenOutside.ShouldBeNull();
        (await Store.GetAsync(a, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Transaction_reads_its_own_writes()
    {
        var a = NewKey();
        var value = await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(a, Doc(5));
            var read = await tx.GetManyAsync([a]);
            return read[0]!.Content["value"]!.GetValue<int>();
        }, Options);

        value.ShouldBe(5);
    }

    [Fact]
    public async Task Insert_of_existing_key_fails()
    {
        var a = NewKey();
        await Store.InsertIfAbsentAsync(a, Doc(1), CancellationToken.None);

        await Should.ThrowAsync<DocumentExistsException>(() => Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(a, Doc(2));
            return true;
        }, Options));

        (await Store.GetAsync(a, CancellationToken.None))!.Content["value"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_read_modify_write_loses_no_update()
    {
        var a = NewKey();
        await Store.InsertIfAbsentAsync(a, Doc(0), CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.RunTransactionAsync(async tx =>
        {
            var current = (await tx.GetManyAsync([a]))[0]!;
            await tx.ReplaceAsync(current, Doc(current.Content["value"]!.GetValue<int>() + 1));
            return true;
        }, Options))));

        (await Store.GetAsync(a, CancellationToken.None))!.Content["value"]!.GetValue<int>().ShouldBe(8);
    }

    [Fact]
    public async Task Concurrent_insert_of_same_key_commits_once()
    {
        var a = NewKey();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            try
            {
                return await Store.RunTransactionAsync(async tx =>
                {
                    var existing = (await tx.GetManyAsync([a]))[0];
                    if (existing is not null)
                    {
                        return "seen";
                    }

                    await tx.InsertAsync(a, Doc(i));
                    return "inserted";
                }, Options);
            }
            catch (DocumentExistsException)
            {
                // Couchbase reports a committed concurrent insert as "exists" (not a retry); the runner re-runs.
                return "exists";
            }
        })));

        outcomes.Count(o => o == "inserted").ShouldBe(1);
        outcomes.ShouldAllBe(o => o == "inserted" || o == "seen" || o == "exists");
    }

    [Fact]
    public async Task InsertIfAbsent_is_idempotent()
    {
        var a = NewKey();
        (await Store.InsertIfAbsentAsync(a, Doc(1), CancellationToken.None)).ShouldBeTrue();
        (await Store.InsertIfAbsentAsync(a, Doc(2), CancellationToken.None)).ShouldBeFalse();
        (await Store.GetAsync(a, CancellationToken.None))!.Content["value"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Pending_outbox_receipts_can_be_queried_and_marked_relayed()
    {
        var key = new DocumentKey("receipts", "rcpt::conf-" + Guid.NewGuid().ToString("N"));
        var receipt = new JsonObject { ["outboxPending"] = true, ["committedAtUtc"] = "2000-01-01T00:00:00.000Z" };
        await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(key, receipt);
            return true;
        }, Options);

        var pending = await Store.QueryPendingOutboxAsync(new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero), 1000, CancellationToken.None);
        pending.ShouldContain(d => d.Key == key);

        await Store.MarkOutboxRelayedAsync(key, CancellationToken.None);
        var after = await Store.QueryPendingOutboxAsync(new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero), 1000, CancellationToken.None);
        after.ShouldNotContain(d => d.Key == key);
        (await Store.GetAsync(key, CancellationToken.None))!.Content["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
    }
}
