using System.Text.Json.Nodes;
using GameServer.Modules;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Privacy;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Privacy;

/// <summary>
/// Slice 010 T041: the purge writer (research R6) — fencing (FR-003, US4-3), batch limit, refusals, the anonymize rule, the
/// receipt rules (D1), missing documents, backoff, and the purge start racing a profile write.
/// </summary>
public sealed class AccountPurgeWriterTests : IDisposable
{
    private const string PlayerId = "p_purge0000000000000000000000";
    private const string OtherId = "p_other0000000000000000000000";
    private const string Refs = "probe_refs";
    private const string Claims = "probe_claims";
    private const string Log = "counter_log";

    private readonly RunnerHarness _h;
    private readonly AccountPurgeWriter _writer;
    private readonly PlayerAggregateWriter _profiles;

    public AccountPurgeWriterTests()
    {
        var removable = new DescriptorModule(new ModuleDescriptor
        {
            Name = "refs",
            Collections = [new CollectionSpec(Refs, Deletion: DeletionTreatment.Remove)],
        });
        _h = new RunnerHarness(o => o.Purge.BatchSize = 3, removable);
        _writer = new AccountPurgeWriter(_h.Registry, new PurgeTargets(_h.Registry), _h.Store, _h.Serializer, _h.Clock, _h.Options, _h.Metrics);
        _profiles = new PlayerAggregateWriter(_h.Registry, _h.Store, _h.Serializer, _h.Clock, _h.Options, _h.Metrics);
    }

    public void Dispose() => _h.Dispose();

    private static DocumentKey ProfileKey(string playerId) => new(IdentityAggregates.ProfileDescriptor.Collection, playerId);

    private JsonObject? Record(string playerId = PlayerId) => _h.Store.Peek(AccountDeletionRecord.KeyOf(playerId));

    private async Task PendingAsync(string playerId = PlayerId) =>
        await _profiles.UpdateAsync<ProfileState>(
            playerId,
            IdentityAggregates.Profile,
            p => p with { DisplayName = "Leaving", Deleted = new DeletionInfo { RequestedAtUtc = _h.Clock.UtcNow, PurgeAfterUtc = _h.Clock.UtcNow } },
            CancellationToken.None);

    private PurgeStart<ProfileState>? Start(ProfileState profile) =>
        AccountPurgeStart.Decide(profile, _h.Clock.UtcNow) is { Deleted: { } deleted } marked
            ? new PurgeStart<ProfileState>(marked, deleted.RequestedAtUtc, deleted.PurgeAfterUtc)
            : null;

    private Task<PurgeTicket?> BeginAsync(long token, string playerId = PlayerId) =>
        _writer.BeginAsync<ProfileState>(playerId, token, Start, CancellationToken.None);

    private async Task<PurgeTicket> BegunAsync(long token = 1)
    {
        await PendingAsync();
        return (await BeginAsync(token)).ShouldNotBeNull();
    }

    private static JsonObject Projection(string playerId) => new()
    {
        ["playerId"] = playerId,
        ["actorId"] = playerId,
        ["note"] = "about " + playerId + " (substring stays)",
        ["payload"] = new JsonObject
        {
            ["amount"] = 5,
            ["nested"] = new JsonObject { ["who"] = playerId, ["list"] = new JsonArray(playerId, "other", 7) },
        },
    };

    [Fact]
    public async Task Begin_marks_the_profile_purging_and_inserts_a_record_with_a_new_anonymous_id()
    {
        await PendingAsync();

        var ticket = (await BeginAsync(4)).ShouldNotBeNull();

        ticket.PlayerId.ShouldBe(PlayerId);
        ticket.AnonymousId.ShouldMatch("^anon_[0-9a-hjkmnp-tv-z]{26}$");
        ticket.FencingToken.ShouldBe(4);
        ticket.CompletedSteps.ShouldBeEmpty();
        ticket.ToString().ShouldNotContain(ticket.AnonymousId);
        var profile = _h.Store.Peek(ProfileKey(PlayerId))!;
        profile["rev"]!.GetValue<long>().ShouldBe(2);
        profile["state"]!["deleted"]!["purgeStartedAtUtc"].ShouldNotBeNull();
        var record = AccountDeletionRecord.FromJson(Record()!);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.AnonymousId.ShouldBe(ticket.AnonymousId);
        record.FencingToken.ShouldBe(4);
        record.PurgeStartedAtUtc.ShouldBe(_h.Clock.UtcNow);
        record.NextAttemptAtUtc.ShouldBe(_h.Clock.UtcNow);
        record.PurgeAfterUtc.ShouldBe(_h.Clock.UtcNow);
    }

    [Fact]
    public async Task Begin_is_null_without_profile_or_when_the_start_decision_refuses()
    {
        (await BeginAsync(1)).ShouldBeNull();
        await _profiles.UpdateAsync<ProfileState>(PlayerId, IdentityAggregates.Profile, p => p with { DisplayName = "Staying" }, CancellationToken.None);

        (await BeginAsync(1)).ShouldBeNull();

        Record().ShouldBeNull();
        _h.Store.Peek(ProfileKey(PlayerId))!["rev"]!.GetValue<long>().ShouldBe(1);
    }

    [Fact]
    public async Task A_newer_token_claims_the_purge_and_the_stale_ticket_is_fenced_out()
    {
        var stale = await BegunAsync(1);
        _h.Store.Seed(new DocumentKey(Log, "log::1"), Projection(PlayerId));

        var claimed = (await BeginAsync(2)).ShouldNotBeNull();

        claimed.AnonymousId.ShouldBe(stale.AnonymousId);
        AccountDeletionRecord.FromJson(Record()!).FencingToken.ShouldBe(2);
        var fenced = await Should.ThrowAsync<PurgeFencedOutException>(() => _writer.ApplyAsync(stale, [new DocumentKey(Log, "log::1")], PurgeAction.Anonymize, CancellationToken.None));
        fenced.CurrentToken.ShouldBe(2);
        await Should.ThrowAsync<PurgeFencedOutException>(() => _writer.CompleteStepAsync(stale, "receipts", CancellationToken.None));
        await Should.ThrowAsync<PurgeFencedOutException>(() => _writer.RecordFailureAsync(stale, "receipts", new InvalidOperationException(), CancellationToken.None));
        await Should.ThrowAsync<PurgeFencedOutException>(() => BeginAsync(1));
        _h.Store.Peek(new DocumentKey(Log, "log::1"))!["playerId"]!.GetValue<string>().ShouldBe(PlayerId);
    }

    [Fact]
    public async Task A_purged_record_is_never_reverted_by_any_ticket()
    {
        var stale = await BegunAsync(1);
        var current = (await BeginAsync(2)).ShouldNotBeNull();
        await _writer.CompleteAsync(current, CancellationToken.None);
        var purged = Record()!;

        await Should.ThrowAsync<PurgeFencedOutException>(() => _writer.CompleteAsync(stale, CancellationToken.None));

        // The completing token itself learns the purge is done (an ambiguous final commit that landed), not "fenced out".
        await Should.ThrowAsync<PurgeAlreadyCompletedException>(() => _writer.CompleteAsync(current, CancellationToken.None));
        await Should.ThrowAsync<PurgeAlreadyCompletedException>(() => _writer.CompleteStepAsync(current, "receipts", CancellationToken.None));
        await Should.ThrowAsync<PurgeAlreadyCompletedException>(() => _writer.RecordFailureAsync(current, "verify", new InvalidOperationException(), CancellationToken.None));
        await Should.ThrowAsync<PurgeAlreadyCompletedException>(() => _writer.ApplyAsync(current, [new DocumentKey(Log, "log::x")], PurgeAction.Anonymize, CancellationToken.None));
        (await BeginAsync(3)).ShouldBeNull();

        JsonNode.DeepEquals(Record(), purged).ShouldBeTrue();
        purged["state"]!.GetValue<string>().ShouldBe("purged");
        purged.ContainsKey("anonymousId").ShouldBeFalse();
        purged.ContainsKey("completedSteps").ShouldBeFalse();
        purged.ContainsKey("attempts").ShouldBeFalse();
        purged.ContainsKey("lastFailure").ShouldBeFalse();
        purged["nextAttemptAtUtc"].ShouldBeNull();
        _h.Store.Peek(ProfileKey(PlayerId)).ShouldBeNull();
    }

    [Fact]
    public async Task Apply_writes_at_most_one_batch_and_refuses_keys_outside_their_treatment()
    {
        var ticket = await BegunAsync();
        var log = Enumerable.Range(0, 4).Select(i => new DocumentKey(Log, $"log::{i}")).ToArray();

        await Should.ThrowAsync<ArgumentException>(() => _writer.ApplyAsync(ticket, log, PurgeAction.Anonymize, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [ProfileKey(PlayerId)], PurgeAction.Remove, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [new DocumentKey("pa_counter", PlayerId)], PurgeAction.Anonymize, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [new DocumentKey("pa_counter", OtherId)], PurgeAction.Remove, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [new DocumentKey(Claims, "c1")], PurgeAction.Remove, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [new DocumentKey(Refs, "r1")], PurgeAction.Anonymize, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [AccountDeletionRecord.KeyOf(PlayerId)], PurgeAction.Remove, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.ApplyAsync(ticket, [new DocumentKey("leases", "leases::x")], PurgeAction.Remove, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.RewriteAsync(ticket, [new DocumentKey("receipts", "rcpt::x")], d => d, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.RewriteAsync(ticket, [new DocumentKey("gm_audit", "a")], d => d, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => _writer.RewriteAsync(ticket, [new DocumentKey("pa_counter", PlayerId)], d => d, CancellationToken.None));
        AccountDeletionRecord.FromJson(Record()!).DocumentsProcessed.ShouldBe(0);
    }

    [Fact]
    public async Task Anonymize_replaces_every_exact_player_id_and_keeps_every_other_field()
    {
        var ticket = await BegunAsync();
        var key = new DocumentKey(Log, "log::nested");
        var other = new DocumentKey(Log, "log::other");
        _h.Store.Seed(key, Projection(PlayerId));
        _h.Store.Seed(other, Projection(OtherId));

        (await _writer.ApplyAsync(ticket, [key, other], PurgeAction.Anonymize, CancellationToken.None)).ShouldBe(1);

        var expected = Projection(ticket.AnonymousId);
        expected["note"] = "about " + PlayerId + " (substring stays)";
        JsonNode.DeepEquals(_h.Store.Peek(key), expected).ShouldBeTrue(_h.Store.Peek(key)!.ToJsonString());
        JsonNode.DeepEquals(_h.Store.Peek(other), Projection(OtherId)).ShouldBeTrue();
        AccountDeletionRecord.FromJson(Record()!).DocumentsProcessed.ShouldBe(1);
    }

    [Fact]
    public async Task Receipts_lose_result_body_hash_request_and_correlation_ids_and_claim_keys_into_removed_collections()
    {
        var ticket = await BegunAsync();
        var key = new DocumentKey("receipts", "rcpt::one");
        _h.Store.Seed(key, new JsonObject
        {
            ["receiptId"] = "one",
            ["playerId"] = PlayerId,
            ["actorId"] = PlayerId,
            ["operation"] = "identity.link",
            ["bodyHash"] = "abc123",
            ["resultCompact"] = "ZGlzcGxheSBuYW1l",
            ["claimKeys"] = new JsonArray($"{Refs}/guest:secret-local-id", $"{Claims}/extref::iap::t1"),
            ["committedAtUtc"] = "2026-10-30T00:00:00.000Z",
            ["requestId"] = "7d0f5d0e-1f6a-4f43-9a52-4b8f0f8c2a11",
            ["correlationId"] = "7d0f5d0e-1f6a-4f43-9a52-4b8f0f8c2a11",
            ["outbox"] = new JsonArray(
                new JsonObject { ["entryId"] = "one:0", ["kind"] = "counter_log", ["payload"] = new JsonObject { ["playerId"] = PlayerId, ["correlationId"] = "kept" } },
                new JsonObject { ["entryId"] = "one:1", ["kind"] = "gm_audit", ["payload"] = new JsonObject { ["targetPlayerId"] = PlayerId, ["correlationId"] = "7d0f5d0e-1f6a-4f43-9a52-4b8f0f8c2a11" } }),
        });

        (await _writer.ApplyAsync(ticket, [key], PurgeAction.Anonymize, CancellationToken.None)).ShouldBe(1);

        var receipt = _h.Store.Peek(key)!;
        receipt["playerId"]!.GetValue<string>().ShouldBe(ticket.AnonymousId);
        receipt["actorId"]!.GetValue<string>().ShouldBe(ticket.AnonymousId);
        receipt["bodyHash"]!.GetValue<string>().ShouldBeEmpty();
        receipt["resultCompact"]!.GetValue<string>().ShouldBeEmpty();
        receipt["claimKeys"]!.AsArray().Select(k => k!.GetValue<string>()).ShouldBe([$"{Refs}/redacted", $"{Claims}/extref::iap::t1"]);
        receipt["outbox"]![0]!["payload"]!["playerId"]!.GetValue<string>().ShouldBe(ticket.AnonymousId);
        receipt["operation"]!.GetValue<string>().ShouldBe("identity.link");
        receipt["committedAtUtc"]!.GetValue<string>().ShouldBe("2026-10-30T00:00:00.000Z");

        // Review B1: the request id (and the same UUID as correlation id) would let anyone recompute the receipt key.
        receipt["requestId"]!.GetValue<string>().ShouldBeEmpty();
        receipt["correlationId"]!.GetValue<string>().ShouldBeEmpty();
        receipt["outbox"]![1]!["payload"]!["correlationId"]!.GetValue<string>().ShouldBeEmpty();
        receipt["outbox"]![1]!["payload"]!["targetPlayerId"]!.GetValue<string>().ShouldBe(ticket.AnonymousId);
        receipt["outbox"]![0]!["payload"]!["correlationId"]!.GetValue<string>().ShouldBe("kept"); // only the audit kind carries a request id
    }

    [Fact]
    public async Task Audit_projections_of_the_player_lose_their_correlation_id()
    {
        var ticket = await BegunAsync();
        JsonObject Audit(string playerId) => new()
        {
            ["receiptId"] = "r-" + playerId,
            ["playerId"] = playerId,
            ["actorId"] = "staff-1",
            ["payload"] = new JsonObject { ["targetPlayerId"] = playerId, ["correlationId"] = "corr-" + playerId, ["reason"] = "r" },
        };
        var mine = new DocumentKey("gm_audit", "gm_audit::mine");
        var theirs = new DocumentKey("gm_audit", "gm_audit::theirs");
        _h.Store.Seed(mine, Audit(PlayerId));
        _h.Store.Seed(theirs, Audit(OtherId));

        (await _writer.ApplyAsync(ticket, [mine, theirs], PurgeAction.Anonymize, CancellationToken.None)).ShouldBe(1);

        var audit = _h.Store.Peek(mine)!;
        audit["payload"]!["correlationId"]!.GetValue<string>().ShouldBeEmpty();
        audit["payload"]!["targetPlayerId"]!.GetValue<string>().ShouldBe(ticket.AnonymousId);
        audit["actorId"]!.GetValue<string>().ShouldBe("staff-1");
        audit["receiptId"]!.GetValue<string>().ShouldBe("r-" + PlayerId); // the receipt reference stays (SEC-14); only the request id goes
        JsonNode.DeepEquals(_h.Store.Peek(theirs), Audit(OtherId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_documents_count_as_done_and_remove_only_touches_documents_of_the_player()
    {
        var ticket = await BegunAsync();
        _h.Store.Seed(new DocumentKey(Refs, "mine"), new JsonObject { ["playerId"] = PlayerId });
        _h.Store.Seed(new DocumentKey(Refs, "theirs"), new JsonObject { ["playerId"] = OtherId });
        _h.Store.Seed(new DocumentKey("pa_counter", PlayerId), new JsonObject { ["playerId"] = PlayerId, ["state"] = new JsonObject() });

        (await _writer.ApplyAsync(ticket, [new DocumentKey(Log, "never-created")], PurgeAction.Anonymize, CancellationToken.None)).ShouldBe(0);
        (await _writer.ApplyAsync(ticket, [new DocumentKey(Refs, "mine"), new DocumentKey(Refs, "theirs"), new DocumentKey(Refs, "gone")], PurgeAction.Remove, CancellationToken.None))
            .ShouldBe(1);
        (await _writer.ApplyAsync(ticket, [new DocumentKey("pa_counter", PlayerId), new DocumentKey("pa_probe", PlayerId)], PurgeAction.Remove, CancellationToken.None))
            .ShouldBe(1);
        (await _writer.RewriteAsync(ticket, [new DocumentKey(Refs, "gone")], _ => new JsonObject { ["created"] = true }, CancellationToken.None)).ShouldBe(0);

        _h.Store.Peek(new DocumentKey(Refs, "mine")).ShouldBeNull();
        _h.Store.Peek(new DocumentKey(Refs, "theirs")).ShouldNotBeNull();
        _h.Store.Peek(new DocumentKey(Refs, "gone")).ShouldBeNull();
        _h.Store.Peek(new DocumentKey("pa_counter", PlayerId)).ShouldBeNull();
        AccountDeletionRecord.FromJson(Record()!).DocumentsProcessed.ShouldBe(2);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(8, 1280)]
    [InlineData(9, 1440)]
    [InlineData(30, 1440)]
    public void Retry_delay_doubles_per_failure_up_to_the_cap(int attempts, int minutes) =>
        AccountPurgeWriter.RetryDelay(new PurgeSettings(), attempts).ShouldBe(TimeSpan.FromMinutes(minutes));

    [Fact]
    public async Task A_failure_records_attempts_step_type_and_the_backoff_and_progress_resets_attempts()
    {
        var ticket = await BegunAsync();
        var now = _h.Clock.UtcNow;

        await _writer.RecordFailureAsync(ticket, "remove:identities", new TimeoutException("contains " + PlayerId), CancellationToken.None);
        await _writer.RecordFailureAsync(ticket, "remove:identities", new TimeoutException(), CancellationToken.None);

        var record = AccountDeletionRecord.FromJson(Record()!);
        record.Attempts.ShouldBe(2);
        record.LastFailure.ShouldBe(new PurgeFailure("remove:identities", typeof(TimeoutException).FullName!, now));
        record.NextAttemptAtUtc.ShouldBe(now.AddMinutes(20));
        Record()!.ToJsonString().ShouldNotContain("contains");

        var advanced = await _writer.CompleteStepAsync(ticket, "receipts", CancellationToken.None);

        advanced.IsCompleted("receipts").ShouldBeTrue();
        AccountDeletionRecord.FromJson(Record()!).Attempts.ShouldBe(0);
        AccountDeletionRecord.FromJson(Record()!).CompletedSteps.ShouldBe(["receipts"]);
    }

    [Fact]
    public async Task Begin_racing_a_profile_write_commits_only_one_of_them()
    {
        await PendingAsync();
        _h.Store.Faults.OnlyWhenWritingCollection = "account_deletions";
        _h.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(200);
        _h.Store.Faults.Enqueue(StoreFault.DelayCommit);

        // The begin has staged profile + record and waits in its commit; a cancel-like profile write commits meanwhile.
        var begin = BeginAsync(1);
        await _profiles.UpdateAsync<ProfileState>(PlayerId, IdentityAggregates.Profile, p => p with { Deleted = null }, CancellationToken.None);

        (await begin).ShouldBeNull();
        Record().ShouldBeNull();
        _h.Store.Peek(ProfileKey(PlayerId))!["state"]!["deleted"].ShouldBeNull();
    }

    [Fact]
    public async Task Scrub_rewrites_only_the_given_kind_inside_the_players_receipts()
    {
        var ticket = await BegunAsync();
        JsonObject Receipt(string owner) => new()
        {
            ["playerId"] = owner,
            ["outbox"] = new JsonArray(
                new JsonObject { ["kind"] = "notes", ["payload"] = new JsonObject { ["text"] = "secret" } },
                new JsonObject { ["kind"] = "counter_log", ["payload"] = new JsonObject { ["text"] = "secret" } }),
        };
        _h.Store.Seed(new DocumentKey("receipts", "rcpt::mine"), Receipt(ticket.AnonymousId));
        _h.Store.Seed(new DocumentKey("receipts", "rcpt::theirs"), Receipt(OtherId));

        var written = await _writer.ScrubOutboxAsync(
            ticket,
            [new DocumentKey("receipts", "rcpt::mine"), new DocumentKey("receipts", "rcpt::theirs")],
            "notes",
            _ => new JsonObject { ["text"] = "[erased]" },
            CancellationToken.None);

        written.ShouldBe(1);
        var mine = _h.Store.Peek(new DocumentKey("receipts", "rcpt::mine"))!["outbox"]!.AsArray();
        mine[0]!["payload"]!["text"]!.GetValue<string>().ShouldBe("[erased]");
        mine[1]!["payload"]!["text"]!.GetValue<string>().ShouldBe("secret");
        JsonNode.DeepEquals(_h.Store.Peek(new DocumentKey("receipts", "rcpt::theirs")), Receipt(OtherId)).ShouldBeTrue();
    }
}
