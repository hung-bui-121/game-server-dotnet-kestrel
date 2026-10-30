using System.Text.Json.Nodes;
using GameServer.Errors;
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
        var store = Store;
        var a = NewKey();
        await Should.ThrowAsync<ApplicationException>(() => store.RunTransactionAsync<bool>(async tx =>
        {
            await tx.InsertAsync(a, Doc(1));
            throw new ApplicationException("boom");
        }, Options));

        (await store.GetAsync(a, CancellationToken.None)).ShouldBeNull();
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
    public async Task Only_write_write_conflicts_abort_commits()
    {
        var store = Store;
        var readOnly = NewKey();
        var written = NewKey();
        await store.InsertIfAbsentAsync(readOnly, Doc(1), CancellationToken.None);
        var attempts = 0;

        await store.RunTransactionAsync(async tx =>
        {
            attempts++;
            await tx.GetManyAsync([readOnly]);
            if (attempts == 1)
            {
                // Another transaction changes the document this one only read.
                await store.RunTransactionAsync(async other =>
                {
                    var current = (await other.GetManyAsync([readOnly]))[0]!;
                    await other.ReplaceAsync(current, Doc(2));
                    return true;
                }, Options);
            }

            await tx.InsertAsync(written, Doc(1));
            return true;
        }, Options);

        attempts.ShouldBe(1);
        (await store.GetAsync(written, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Key_value_insert_cas_replace_and_remove()
    {
        var kv = (IKeyValueStore)Store;
        var key = NewKey();

        (await kv.InsertAsync(key, Doc(1), null, CancellationToken.None)).ShouldBeTrue();
        (await kv.InsertAsync(key, Doc(2), null, CancellationToken.None)).ShouldBeFalse();
        var read = (await kv.GetAsync(key, CancellationToken.None))!;
        (await kv.ReplaceAsync(read, Doc(3), null, CancellationToken.None)).ShouldBeTrue();
        (await kv.ReplaceAsync(read, Doc(4), null, CancellationToken.None)).ShouldBeFalse();
        (await kv.GetAsync(key, CancellationToken.None))!.Content["value"]!.GetValue<int>().ShouldBe(3);

        await kv.RemoveAsync(key, CancellationToken.None);
        (await kv.GetAsync(key, CancellationToken.None)).ShouldBeNull();
        await kv.RemoveAsync(key, CancellationToken.None);
    }

    [Fact]
    public async Task Key_value_writes_to_runner_owned_collections_are_refused()
    {
        var kv = (IKeyValueStore)Store;

        await Should.ThrowAsync<InvalidOperationException>(() => kv.InsertAsync(new DocumentKey("receipts", "x"), Doc(1), null, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => kv.InsertAsync(new DocumentKey("pa_counter", "x"), Doc(1), null, CancellationToken.None));
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

        var before = new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var pending = await Store.QueryPendingOutboxAsync(before, before, 1000, CancellationToken.None);
        pending.ShouldContain(d => d.Key == key);

        await Store.MarkOutboxRelayedAsync(key, CancellationToken.None);
        var after = await Store.QueryPendingOutboxAsync(before, before, 1000, CancellationToken.None);
        after.ShouldNotContain(d => d.Key == key);
        (await Store.GetAsync(key, CancellationToken.None))!.Content["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_relays_back_off_and_dead_letters_leave_the_queue()
    {
        var store = Store;
        var key = new DocumentKey("receipts", "rcpt::conf-" + Guid.NewGuid().ToString("N"));
        await store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(key, new JsonObject { ["outboxPending"] = true, ["committedAtUtc"] = "2000-01-01T00:00:00.000Z" });
            return true;
        }, Options);
        var committedBefore = new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2000, 1, 3, 0, 0, 0, TimeSpan.Zero);

        await store.MarkOutboxFailedAsync(key, 1, now.AddMinutes(5), deadLetter: false, CancellationToken.None);
        (await store.QueryPendingOutboxAsync(committedBefore, now, 1000, CancellationToken.None)).ShouldNotContain(d => d.Key == key);
        (await store.QueryPendingOutboxAsync(committedBefore, now.AddMinutes(6), 1000, CancellationToken.None)).ShouldContain(d => d.Key == key);

        await store.MarkOutboxFailedAsync(key, 10, now, deadLetter: true, CancellationToken.None);
        (await store.QueryPendingOutboxAsync(committedBefore, now.AddDays(1), 1000, CancellationToken.None)).ShouldNotContain(d => d.Key == key);
        var stored = (await store.GetAsync(key, CancellationToken.None))!.Content;
        stored["outboxAttempts"]!.GetValue<int>().ShouldBe(10);
        stored["outboxDeadLetter"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Mark_relayed_on_a_missing_receipt_is_a_no_op()
    {
        await Store.MarkOutboxRelayedAsync(new DocumentKey("receipts", "rcpt::missing-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
    }

    [Fact]
    public async Task Transactional_remove_reads_missing_in_the_attempt_and_after_commit()
    {
        var store = Store;
        var a = NewKey();
        await store.InsertIfAbsentAsync(a, Doc(1), CancellationToken.None);

        var seenAfterRemove = await store.RunTransactionAsync(async tx =>
        {
            var current = (await tx.GetManyAsync([a]))[0]!;
            await tx.RemoveAsync(current);
            return (await tx.GetManyAsync([a]))[0];
        }, Options);

        seenAfterRemove.ShouldBeNull();
        (await store.GetAsync(a, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Rolled_back_remove_keeps_the_document()
    {
        var store = Store;
        var a = NewKey();
        await store.InsertIfAbsentAsync(a, Doc(1), CancellationToken.None);

        await Should.ThrowAsync<ApplicationException>(() => store.RunTransactionAsync<bool>(async tx =>
        {
            await tx.RemoveAsync((await tx.GetManyAsync([a]))[0]!);
            throw new ApplicationException("boom");
        }, Options));

        (await store.GetAsync(a, CancellationToken.None))!.Content["value"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Remove_conflicts_with_a_concurrent_replace_and_retries()
    {
        var store = Store;
        var a = NewKey();
        await store.InsertIfAbsentAsync(a, Doc(1), CancellationToken.None);
        var attempts = 0;
        var removedValues = new List<int>();

        await store.RunTransactionAsync(async tx =>
        {
            attempts++;
            var current = (await tx.GetManyAsync([a]))[0]!;
            if (attempts == 1)
            {
                // Another transaction replaces the document after this attempt read it.
                await store.RunTransactionAsync(async other =>
                {
                    await other.ReplaceAsync((await other.GetManyAsync([a]))[0]!, Doc(2));
                    return true;
                }, Options);
            }

            removedValues.Add(current.Content["value"]!.GetValue<int>());
            await tx.RemoveAsync(current);
            return true;
        }, Options);

        attempts.ShouldBeGreaterThanOrEqualTo(2);
        removedValues[^1].ShouldBe(2);
        (await store.GetAsync(a, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Due_query_returns_due_unset_documents_oldest_first_up_to_the_limit()
    {
        var maintenance = (IMaintenanceQueryStore)Store;
        var group = "due-" + Guid.NewGuid().ToString("N");
        await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(new(Docs, group + "-a"), new JsonObject { ["dueAt"] = "2026-01-01T00:00:01.000Z" });
            await tx.InsertAsync(new(Docs, group + "-b"), new JsonObject { ["dueAt"] = "2026-01-01T00:00:03.000Z" });
            await tx.InsertAsync(new(Docs, group + "-c"), new JsonObject { ["dueAt"] = "2026-01-01T00:00:02.000Z" });
            await tx.InsertAsync(new(Docs, group + "-d"), new JsonObject { ["dueAt"] = "2026-01-01T00:00:09.000Z" });
            await tx.InsertAsync(new(Docs, group + "-e"), new JsonObject { ["dueAt"] = "2026-01-01T00:00:01.000Z", ["doneAt"] = "x" });
            await tx.InsertAsync(new(Docs, group + "-f"), new JsonObject { ["dueAt"] = "2026-01-01T00:00:05.000Z", ["doneAt"] = null });
            await tx.InsertAsync(new(Docs, group + "-g"), new JsonObject { ["other"] = 1 });
            return true;
        }, Options);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 5, TimeSpan.Zero);

        var all = await maintenance.QueryDueAsync(new DueQuery(Docs, "dueAt", now, 100, "doneAt"), CancellationToken.None);
        all.Select(d => d.Key.Key).ShouldBe([group + "-a", group + "-c", group + "-b", group + "-f"]);
        all[0].Content["dueAt"]!.GetValue<string>().ShouldBe("2026-01-01T00:00:01.000Z");

        var limited = await maintenance.QueryDueAsync(new DueQuery(Docs, "dueAt", now, 2, "doneAt"), CancellationToken.None);
        limited.Select(d => d.Key.Key).ShouldBe([group + "-a", group + "-c"]);
    }

    [Fact]
    public async Task Due_query_compares_json_written_times_in_their_own_format()
    {
        var maintenance = (IMaintenanceQueryStore)Store;
        var group = "jdue-" + Guid.NewGuid().ToString("N");
        await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(new(Docs, group + "-a"), new JsonObject { ["jsonDueAt"] = "2026-01-01T00:00:05+00:00" });
            await tx.InsertAsync(new(Docs, group + "-b"), new JsonObject { ["jsonDueAt"] = "2026-01-01T00:00:05.5+00:00" });
            await tx.InsertAsync(new(Docs, group + "-c"), new JsonObject { ["jsonDueAt"] = "2026-01-01T00:00:05.75+00:00" });
            return true;
        }, Options);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 5, 500, TimeSpan.Zero);

        var due = await maintenance.QueryDueAsync(new DueQuery(Docs, "jsonDueAt", now, 100, Format: DueTimeFormat.Json), CancellationToken.None);

        due.Select(d => d.Key.Key).ShouldBe([group + "-a", group + "-b"]);
    }

    [Fact]
    public async Task Reference_query_returns_matching_keys_in_key_order_up_to_the_limit()
    {
        var maintenance = (IMaintenanceQueryStore)Store;
        var value = "p_" + Guid.NewGuid().ToString("N");
        await Store.RunTransactionAsync(async tx =>
        {
            await tx.InsertAsync(new(Docs, value + "-b"), new JsonObject { ["ref"] = value });
            await tx.InsertAsync(new(Docs, value + "-c"), new JsonObject { ["ref"] = value });
            await tx.InsertAsync(new(Docs, value + "-a"), new JsonObject { ["ref"] = value });
            await tx.InsertAsync(new(Docs, value + "-x"), new JsonObject { ["ref"] = value + "x" });
            await tx.InsertAsync(new(Docs, value + "-y"), new JsonObject { ["other"] = value });
            return true;
        }, Options);

        var all = await maintenance.QueryReferencesAsync(new ReferenceQuery(Docs, "ref", value, 100), CancellationToken.None);
        all.ShouldBe([new DocumentKey(Docs, value + "-a"), new DocumentKey(Docs, value + "-b"), new DocumentKey(Docs, value + "-c")]);

        var limited = await maintenance.QueryReferencesAsync(new ReferenceQuery(Docs, "ref", value, 2), CancellationToken.None);
        limited.Select(k => k.Key).ShouldBe([value + "-a", value + "-b"]);

        var next = await maintenance.QueryReferencesAsync(new ReferenceQuery(Docs, "ref", value, 2, AfterKey: value + "-b"), CancellationToken.None);
        next.Select(k => k.Key).ShouldBe([value + "-c"]);
    }

    [Fact]
    public async Task Maintenance_queries_refuse_injected_field_paths()
    {
        var maintenance = (IMaintenanceQueryStore)Store;

        await Should.ThrowAsync<ArgumentException>(() => maintenance.QueryReferencesAsync(new ReferenceQuery(Docs, "ref` OR 1=1", "x", 10), CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(
            () => maintenance.QueryDueAsync(new DueQuery(Docs, "dueAt", DateTimeOffset.UnixEpoch, 10, "x;DROP"), CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => maintenance.QueryReferencesAsync(new ReferenceQuery(Docs, "ref", "x", 0), CancellationToken.None));
    }

    private async Task<string> SeedGroupAsync(params string[] orderValues)
    {
        var group = "g-" + Guid.NewGuid().ToString("N");
        await Store.RunTransactionAsync(async tx =>
        {
            for (var i = 0; i < orderValues.Length; i++)
            {
                await tx.InsertAsync(new DocumentKey(Docs, $"{group}-{i}"), new JsonObject { ["group"] = group, ["at"] = orderValues[i], ["value"] = i });
            }

            await tx.InsertAsync(NewKey(), new JsonObject { ["group"] = "other", ["at"] = "0", ["value"] = -1 });
            return true;
        }, Options);
        return group;
    }

    private async Task<List<int>> WalkAsync(string group, bool descending, int pageSize)
    {
        var paged = (IPagedQueryStore)Store;
        var values = new List<int>();
        string? cursor = null;
        for (var pages = 0; pages < 20; pages++)
        {
            var page = await paged.QueryPageAsync(new PagedQuery(Docs, "group", group, "at", descending, pageSize, cursor), CancellationToken.None);
            page.Items.Count.ShouldBeLessThanOrEqualTo(pageSize);
            values.AddRange(page.Items.Select(d => d.Content["value"]!.GetValue<int>()));
            cursor = page.NextCursor;
            if (cursor is null)
            {
                return values;
            }
        }

        throw new ShouldAssertException("Cursor walk did not terminate.");
    }

    [Fact]
    public async Task Paged_query_walks_every_match_once_in_order()
    {
        // Equal order values (indexes 1 and 2) are ordered by key.
        var group = await SeedGroupAsync("2026-01-03", "2026-01-01", "2026-01-01", "2026-01-05", "2026-01-02");

        (await WalkAsync(group, descending: false, pageSize: 2)).ShouldBe([1, 2, 4, 0, 3]);
        (await WalkAsync(group, descending: true, pageSize: 2)).ShouldBe([3, 0, 4, 2, 1]);
        (await WalkAsync(group, descending: true, pageSize: 200)).ShouldBe([3, 0, 4, 2, 1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Paged_query_rejects_page_size_outside_1_to_200(int pageSize)
    {
        var paged = (IPagedQueryStore)Store;
        var ex = await Should.ThrowAsync<GameServerException>(
            () => paged.QueryPageAsync(new PagedQuery(Docs, "group", "x", "at", false, pageSize, null), CancellationToken.None));
        ex.Code.ShouldBe(CoreErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Paged_query_rejects_a_malformed_cursor()
    {
        var paged = (IPagedQueryStore)Store;
        var ex = await Should.ThrowAsync<GameServerException>(
            () => paged.QueryPageAsync(new PagedQuery(Docs, "group", "x", "at", false, 10, "not-a-cursor!"), CancellationToken.None));
        ex.Code.ShouldBe(CoreErrorCodes.BadRequest);
    }
}
