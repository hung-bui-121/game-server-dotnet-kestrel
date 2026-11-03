using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T016/T017 (FR-001, FR-012, FR-014, US1-1..US1-3, BC-10, research R1): the login write decides create-or-update in
/// each transaction attempt from what that attempt read, and answers store failures with the command codes.
/// </summary>
public sealed class AccountWriterTests : IDisposable
{
    private const string PlayerId = "p_writer";
    private const string MappingKey = "guest:writer";

    private static readonly DocumentKey Anchor = new(IdentityCollections.Identities, MappingKey);
    private static readonly DocumentKey ProfileKey = new("pa_profile", PlayerId);

    private readonly RunnerHarness _h;
    private readonly RecordingStore _store;
    private readonly AccountWriter _writer;

    public AccountWriterTests()
        : this(expirationSeconds: null)
    {
    }

    private AccountWriterTests(int? expirationSeconds)
    {
        _h = new RunnerHarness(
            o =>
            {
                if (expirationSeconds is { } seconds)
                {
                    o.Transactions.ExpirationSeconds = seconds;
                }
            },
            new DescriptorModule(new ModuleDescriptor
            {
                Name = "anchors",
                Collections = [new CollectionSpec(IdentityCollections.Identities, Deletion: DeletionTreatment.NoPlayerData)],
            }));
        _store = new RecordingStore(_h.Store);
        _writer = new AccountWriter(_h.Registry, _store, _h.Serializer, _h.Clock, _h.Options, _h.Metrics);
    }

    [Fact]
    public async Task A_first_login_rewrites_the_anchor_and_inserts_the_profile_at_rev_1()
    {
        SeedAnchor(PlayerId);

        var result = await LoginAsync();

        result.Created.ShouldBeTrue();
        result.Rev.ShouldBe(1);
        result.Profile.SessionEpoch.ShouldBe(1);
        result.CreationReceiptId.ShouldBeNull();
        _store.Operations.ShouldBe(["read pa_profile", "read identities", "replace identities", "insert pa_profile"]);
        StoredEpoch().ShouldBe(1);
    }

    [Fact]
    public async Task A_returning_login_reads_and_replaces_only_the_profile()
    {
        SeedAnchor(PlayerId);
        await LoginAsync();
        _store.Operations.Clear();

        var result = await LoginAsync();

        result.Created.ShouldBeFalse();
        result.Rev.ShouldBe(2);
        result.Profile.SessionEpoch.ShouldBe(2);
        _store.Operations.ShouldBe(["read pa_profile", "replace pa_profile"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_anchor_or_one_naming_another_player_creates_nothing(bool otherPlayer)
    {
        if (otherPlayer)
        {
            SeedAnchor("p_someone_else");
        }

        await Should.ThrowAsync<AggregateAnchorMissingException>(LoginAsync);

        _h.Store.Count("pa_profile").ShouldBe(0);
        _store.Operations.ShouldNotContain(op => op.StartsWith("insert", StringComparison.Ordinal) || op.StartsWith("replace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_body_run_twice_by_the_store_decides_from_the_second_read()
    {
        SeedAnchor(PlayerId);
        var raced = false;
        _store.AfterInsert = async key =>
        {
            if (key == ProfileKey && !raced)
            {
                // A concurrent first login commits after this attempt staged its insert: the commit conflicts and the store
                // runs the body again.
                raced = true;
                await CommitCompetingProfileAsync(epoch: 5);
            }
        };

        var result = await LoginAsync();

        _store.BodyRuns.ShouldBe(2);
        result.Created.ShouldBeFalse();
        result.Profile.SessionEpoch.ShouldBe(6);
        result.Rev.ShouldBe(2);
        StoredEpoch().ShouldBe(6);
    }

    [Fact]
    public async Task An_insert_race_makes_the_next_round_a_normal_login()
    {
        SeedAnchor(PlayerId);
        var raced = false;
        _store.AfterRead = async keys =>
        {
            if (keys.Contains(Anchor) && !raced)
            {
                // The other login commits between this attempt's profile read and its insert: the insert reports "exists".
                raced = true;
                await CommitCompetingProfileAsync(epoch: 1);
            }
        };

        var result = await LoginAsync();

        result.Created.ShouldBeFalse();
        result.Profile.SessionEpoch.ShouldBe(2);
        StoredEpoch().ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mutation_that_throws_a_game_server_exception_propagates_and_writes_nothing(bool returning)
    {
        SeedAnchor(PlayerId);
        if (returning)
        {
            await LoginAsync();
        }

        var before = _h.Store.Peek(ProfileKey)?.ToJsonString();
        var refused = new GameServerException(CoreErrorCodes.AccountSuspended, "late ban");

        var ex = await Should.ThrowAsync<GameServerException>(() => _writer.WriteLoginAsync(
            new AccountLoginWrite<ProfileState>(PlayerId, _ => throw refused, Anchor, "corr"), CancellationToken.None));

        ex.ShouldBeSameAs(refused);
        _h.Store.Peek(ProfileKey)?.ToJsonString().ShouldBe(before);
    }

    [Fact]
    public async Task The_profile_type_must_be_the_registered_state_type()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.WriteLoginAsync(
            new AccountLoginWrite<JsonObject>(PlayerId, p => p, Anchor, "corr"), CancellationToken.None));
    }

    // T017: store failures answer the command codes (research R1 table, BC-10); nothing is written.
    [Theory]
    [InlineData(StoreFault.FailBeforeCommit, false, "INTERNAL", null)]
    [InlineData(StoreFault.FailBeforeCommit, true, "INTERNAL", null)]
    [InlineData(StoreFault.AmbiguousBeforeCommit, false, "COMMIT_UNKNOWN_RETRY", 1000)]
    [InlineData(StoreFault.AmbiguousBeforeCommit, true, "COMMIT_UNKNOWN_RETRY", 1000)]
    public async Task Store_faults_map_to_the_command_codes_and_write_nothing(StoreFault fault, bool returning, string code, int? retryAfterMs)
    {
        SeedAnchor(PlayerId);
        if (returning)
        {
            await LoginAsync();
        }

        var before = _h.Store.Peek(ProfileKey)?.ToJsonString();
        _h.Store.Faults.Enqueue(fault);

        var ex = await Should.ThrowAsync<GameServerException>(LoginAsync);

        ex.Code.Name.ShouldBe(code);
        ex.RetryAfterMs.ShouldBe(retryAfterMs);
        _h.Store.Peek(ProfileKey)?.ToJsonString().ShouldBe(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_expired_transaction_is_concurrent_update(bool returning)
    {
        using var expired = new AccountWriterTests(expirationSeconds: 0);
        expired.SeedAnchor(PlayerId);
        if (returning)
        {
            await expired.LoginAsync();
        }

        var before = expired._h.Store.Peek(ProfileKey)?.ToJsonString();
        expired._h.Store.Faults.Enqueue(StoreFault.ConflictOnce);

        var ex = await Should.ThrowAsync<GameServerException>(expired.LoginAsync);

        ex.Code.ShouldBe(CoreErrorCodes.ConcurrentUpdate);
        ex.RetryAfterMs.ShouldBe(200);
        expired._h.Store.Peek(ProfileKey)?.ToJsonString().ShouldBe(before);
    }

    [Fact]
    public async Task A_conflict_inside_the_expiration_is_retried_by_the_store()
    {
        SeedAnchor(PlayerId);
        _h.Store.Faults.Enqueue(StoreFault.ConflictOnce);

        var result = await LoginAsync();

        result.Created.ShouldBeTrue();
        _store.BodyRuns.ShouldBe(2);
    }

    [Fact]
    public async Task Exhausted_race_rounds_are_concurrent_update_and_write_nothing()
    {
        SeedAnchor(PlayerId);
        _store.InsertRacesToFake = 4;

        var ex = await Should.ThrowAsync<GameServerException>(LoginAsync);

        ex.Code.ShouldBe(CoreErrorCodes.ConcurrentUpdate);
        ex.RetryAfterMs.ShouldBe(200);
        ex.InnerException.ShouldBeOfType<DocumentExistsException>();
        _store.BodyRuns.ShouldBe(3);
        _h.Store.Count("pa_profile").ShouldBe(0);
    }

    [Fact]
    public async Task Two_insert_races_still_create_in_the_third_round()
    {
        SeedAnchor(PlayerId);
        _store.InsertRacesToFake = 2;

        var result = await LoginAsync();

        result.Created.ShouldBeTrue();
        _store.BodyRuns.ShouldBe(3);
    }

    public void Dispose() => _h.Dispose();

    private Task<AccountWriteResult<ProfileState>> LoginAsync() =>
        _writer.WriteLoginAsync(
            new AccountLoginWrite<ProfileState>(PlayerId, p => p with { SessionEpoch = p.SessionEpoch + 1 }, Anchor, "corr-" + PlayerId),
            CancellationToken.None);

    private void SeedAnchor(string playerId) => _h.Store.Seed(Anchor, new JsonObject { ["playerId"] = playerId });

    private long StoredEpoch() => _h.Store.Peek(ProfileKey)!["state"]!["sessionEpoch"]!.GetValue<long>();

    private Task CommitCompetingProfileAsync(long epoch)
    {
        var descriptor = IdentityAggregates.ProfileDescriptor;
        var initial = _h.Serializer.Load(descriptor, PlayerId, null);
        var json = AggregateDocumentSerializer.SerializeState(new ProfileState { SessionEpoch = epoch }, descriptor.StateType);
        var (document, _) = _h.Serializer.BuildDocument(initial, json, 1, _h.Clock.UtcNow);
        return _h.Store.RunTransactionAsync(
            async tx =>
            {
                await tx.InsertAsync(ProfileKey, document);
                return true;
            },
            new TransactionRunOptions(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Records the operations of the writer's transaction attempts and lets a test act between them.</summary>
    private sealed class RecordingStore(ITransactionalDocumentStore inner) : ITransactionalDocumentStore
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

        public Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

        public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken) =>
            inner.GetManyAsync(keys, cancellationToken);

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
}
