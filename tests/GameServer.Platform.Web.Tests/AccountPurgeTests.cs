using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Analytics;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Jobs;
using GameServer.Platform.Persistence;
using GameServer.Platform.Privacy;
using GameServer.Platform.Registry;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using ProfileState = GameServer.Modules.Identity.ProfileState;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 010 US2: the account purge job end to end on the full sample game (PRIV-01, ACC-37): every player aggregate,
/// identity and session removed; receipts, ledger, audit, claims and projections kept under one anonymous id; the purge
/// record without it (FR-013..FR-018, SC-002, SC-003, SC-006). Failures, resume, budget, limits and fencing (US2-4, US4-3);
/// login and stale sessions racing the purge (US2-5, research R9). Runs on the in-memory and the file store (slice 011
/// SC-003): the subclasses below choose the store; <c>host.Store</c> is the in-memory store (inner store of the file store).
/// </summary>
public abstract class AccountPurgeTests
{
    private const string IncrementRoute = "/api/v1/counter/increment";
    private const string AnonymousIdPattern = "^anon_[0-9a-hjkmnp-tv-z]{26}$";
    private static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    /// <summary>Lease interval of the purge job: the next run on the same lease is due after it.</summary>
    private static readonly TimeSpan NextRun = TimeSpan.FromSeconds(AccountPurgeJob.DefaultIntervalSeconds + 1);

    private sealed record Leaver(string PlayerId, string LocalId, byte[] Secret, string Pgs, string Token);

    /// <summary>What the player left in the retained collections before the purge (SC-003).</summary>
    private sealed record Footprint(int Receipts, int Ledger, IReadOnlyDictionary<string, long> LedgerSums, int Audits, int CounterLog, int Claims, DocumentKey AnyReceipt);

    /// <summary>
    /// Sessions outlive the grace period here (sliding 60 days instead of 30), so the purge itself must remove them and stale
    /// tokens still reach the profile check.
    /// </summary>
    /// <summary>The store each host of this class runs on (a host sharing another's store keeps that store).</summary>
    protected abstract TestStoreKind StoreKind { get; }

    private Task<TestHost> StartAsync(Action<Dictionary<string, string?>>? settings = null, InMemoryDocumentStore? store = null, FakeClock? clock = null) =>
        TestHost.StartAsync(
            modules: b => GameHttp.FullGame(b.AddModule<GmModule>()),
            settings: s =>
            {
                s["Identity:SessionSlidingDays"] = "60";
                settings?.Invoke(s);
            },
            store: store,
            clock: clock,
            storeKind: StoreKind);

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static Task<JobRunResult> RunPurgeAsync(TestHost host) =>
        host.Services.GetRequiredService<LeaseJobRunner>().RunOnceAsync(AccountPurgeJob.JobName, CancellationToken.None);

    private static Task<HttpResponseMessage> RequestDeletionAsync(TestHost host, string token) =>
        host.BearerClient(token).PostProtobufAsync(IdentityModule.DeleteAccountRoute, new RequestAccountDeletionRequest(), ProtobufHttp.NewRequestId());

    private static Task<HttpResponseMessage> ProviderLoginAsync(TestHost host, string pgs) =>
        host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = Pgs(pgs) });

    private static IEnumerable<JsonObject> Owned(TestHost host, string collection, string playerId) =>
        host.Store.All(collection).Select(d => d.Content).Where(d => d["playerId"]?.GetValue<string>() == playerId);

    private static JsonObject? Record(TestHost host, string playerId) => host.Store.Peek(AccountDeletionRecord.KeyOf(playerId));

    private static ProfileState? Profile(TestHost host, string playerId)
    {
        var loaded = host.Services.GetRequiredService<PlayerSnapshotReader>()
            .ReadOneAsync(playerId, IdentityAggregates.Profile, CancellationToken.None).GetAwaiter().GetResult();
        return loaded.Exists ? (ProfileState)loaded.State : null;
    }

    /// <summary>
    /// A player with history in every retained collection (receipts, ledger, extref claims, counter log, a GM audit from a
    /// cancelled first request), a guest and a Play Games identity, who requested deletion again.
    /// </summary>
    private static async Task<Leaver> LeaverAsync(TestHost host, int increments = 2)
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var pgs = "g" + Guid.NewGuid().ToString("N")[..20];
        var first = await host.Client().LoginAsync(localId, secret);
        var player = new GamePlayer(host.BearerClient(first.SessionToken), first.PlayerId);
        (await player.CommandAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(pgs) })).Code.ShouldBe("OK");
        (await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = "Leaving " + localId[^4..] })).Code.ShouldBe("OK");
        for (var i = 0; i < increments; i++)
        {
            (await player.CommandAsync(IncrementRoute, new Sample.Counter.Protocol.V1.IncrementRequest { By = 1 })).Code.ShouldBe("OK");
        }

        (await player.CheatAsync("gold", 10)).Code.ShouldBe("OK");
        (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
        (await RequestDeletionAsync(host, first.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A GM cancel leaves a gm_audit entry about the player; the player then asks again.
        var cancel = new HttpRequestMessage(HttpMethod.Post, IdentityModule.CancelDeletionRoute.Replace("{playerId}", first.PlayerId, StringComparison.Ordinal))
        {
            Content = JsonContent.Create(new { reason = "asked by mistake" }),
        };
        cancel.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        (await host.GmClient("admin", IdentityPermissions.PlayerBan).SendAsync(cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await host.Client().LoginAsync(localId, secret);
        (await RequestDeletionAsync(host, second.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Leaver(first.PlayerId, localId, secret, pgs, second.SessionToken);
    }

    private static Footprint Measure(TestHost host, string playerId)
    {
        var ledger = Owned(host, EconomyOperations.LedgerCollection, playerId).ToArray();
        var footprint = new Footprint(
            Owned(host, ReceiptDocument.Collection, playerId).Count(),
            ledger.Length,
            ledger.GroupBy(d => d["payload"]!["resource"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Sum(d => d["payload"]!["delta"]!.GetValue<long>())),
            Owned(host, GmModule.AuditCollection, playerId).Count(),
            Owned(host, Sample.Counter.CounterModuleDefinition.LogCollection, playerId).Count(),
            Owned(host, EconomyOperations.ClaimCollection, playerId).Count(),
            host.Store.All(ReceiptDocument.Collection).First(d => d.Content["playerId"]?.GetValue<string>() == playerId).Key);
        footprint.Receipts.ShouldBeGreaterThan(5);
        footprint.Ledger.ShouldBeGreaterThan(0);
        footprint.Audits.ShouldBe(1);
        footprint.CounterLog.ShouldBeGreaterThan(0);
        footprint.Claims.ShouldBe(1);
        return footprint;
    }

    /// <summary>The end state of a completed purge (US2-1..3, FR-014..FR-018, SC-002, SC-003); returns the anonymous id.</summary>
    private static string ShouldBePurged(TestHost host, Leaver leaver, Footprint before)
    {
        var anonymousId = host.Store.Peek(before.AnyReceipt)!["playerId"]!.GetValue<string>();
        anonymousId.ShouldMatch(AnonymousIdPattern);

        // One anonymous id across every retained record; counts and per-resource sums unchanged (SC-003).
        Measure(host, anonymousId).ShouldBe(before, new FootprintComparer());
        Owned(host, ReceiptDocument.Collection, anonymousId).ShouldAllBe(r => r["resultCompact"]!.GetValue<string>() == string.Empty && r["bodyHash"]!.GetValue<string>() == string.Empty);
        Owned(host, ReceiptDocument.Collection, anonymousId).Single(r => r["operation"]!.GetValue<string>() == LinkIdentityHandler.Operation)["claimKeys"]!.AsArray()
            .Select(k => k!.GetValue<string>()).ShouldBe([$"{IdentityCollections.Identities}/redacted"]);
        Owned(host, GmModule.AuditCollection, anonymousId).Single()["payload"]!["targetPlayerId"]!.GetValue<string>().ShouldBe(anonymousId);

        // Review B1: no receipt key can be recomputed from the old id and any stored value (request / correlation ids cleared).
        Owned(host, ReceiptDocument.Collection, anonymousId).ShouldAllBe(r => r["requestId"]!.GetValue<string>() == string.Empty && r["correlationId"]!.GetValue<string>() == string.Empty);
        Owned(host, GmModule.AuditCollection, anonymousId).Single()["payload"]!["correlationId"]!.GetValue<string>().ShouldBeEmpty();
        ReproducibleReceiptKeys(host, leaver.PlayerId, anonymousId).ShouldBeEmpty();

        // Every aggregate, identity and session gone (US2-1, US2-3).
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        registry.Aggregates.Values.ShouldAllBe(a => host.Store.Peek(new DocumentKey(a.Collection, leaver.PlayerId)) == null);
        host.Store.Peek(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(leaver.LocalId))).ShouldBeNull();
        Owned(host, IdentityCollections.Identities, leaver.PlayerId).ShouldBeEmpty();
        Owned(host, SessionService.Collection, leaver.PlayerId).ShouldBeEmpty();

        // SC-002: the old id survives only in the purge record.
        foreach (var collection in registry.RequiredCollections().Where(c => c != PlatformCollections.AccountDeletions))
        {
            foreach (var (key, content) in host.Store.All(collection))
            {
                key.Key.ShouldNotContain(leaver.PlayerId);
                content.ToJsonString().ShouldNotContain(leaver.PlayerId, customMessage: $"{key} still holds the purged id");
            }
        }

        // FR-018: the purge record keeps no anonymous id and no progress.
        var record = Record(host, leaver.PlayerId)!;
        record["state"]!.GetValue<string>().ShouldBe("purged");
        record["purgedAtUtc"].ShouldNotBeNull();
        record.ContainsKey("anonymousId").ShouldBeFalse();
        record.ContainsKey("completedSteps").ShouldBeFalse();
        record.ToJsonString().ShouldNotContain(anonymousId);

        // LOG-05: never logged.
        host.Logs.ShouldNotContain(line => line.Contains(anonymousId, StringComparison.Ordinal));
        return anonymousId;
    }

    /// <summary>
    /// Receipts held by <paramref name="owner"/> whose key <c>SHA-256(actor | player | operation | requestId)</c> can be
    /// recomputed with <paramref name="playerId"/> and any string stored anywhere (except the purge records): what an
    /// attacker needs to undo the anonymization (review B1).
    /// </summary>
    private static IReadOnlyList<string> ReproducibleReceiptKeys(TestHost host, string playerId, string owner)
    {
        var stored = host.Services.GetRequiredService<ModuleRegistry>().RequiredCollections()
            .Where(c => c != PlatformCollections.AccountDeletions)
            .SelectMany(c => host.Store.All(c))
            .SelectMany(d => Strings(d.Content))
            .ToHashSet(StringComparer.Ordinal);
        var reproducible = new List<string>();
        foreach (var receipt in Owned(host, ReceiptDocument.Collection, owner))
        {
            var receiptId = receipt["receiptId"]!.GetValue<string>();
            var operation = receipt["operation"]!.GetValue<string>();
            var actor = receipt["actorId"]!.GetValue<string>();
            string[] actors = actor == owner ? [playerId] : [actor, playerId];
            if (stored.Any(value => actors.Any(a => ReceiptIds.Compute(a, playerId, operation, value) == receiptId)))
            {
                reproducible.Add(receiptId);
            }
        }

        return reproducible;
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => Strings(p.Value)),
        JsonArray array => array.SelectMany(Strings),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
        _ => [],
    };

    [Fact]
    public async Task One_run_purges_a_due_account_and_leaves_players_not_due_untouched()
    {
        var purges = new ConcurrentQueue<(string Name, long Value, string? Tag)>();
        using var listener = PurgeMetrics(purges);
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        var before = Measure(host, leaver.PlayerId);
        ReproducibleReceiptKeys(host, leaver.PlayerId, leaver.PlayerId).Count.ShouldBe(before.Receipts); // the B1 check is not vacuous
        host.Clock.Advance(TimeSpan.FromDays(29));
        var later = await LeaverAsync(host);
        var stays = await host.NewPlayerAsync();
        (await stays.Player.CommandAsync(IncrementRoute, new Sample.Counter.Protocol.V1.IncrementRequest { By = 1 })).Code.ShouldBe("OK");
        var laterProfile = host.Store.Peek(new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, later.PlayerId))!;
        var staysReceipts = Owned(host, ReceiptDocument.Collection, stays.Player.PlayerId).Count();
        host.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        ShouldBePurged(host, leaver, before);
        purges.ShouldContain(m => m.Name == "account_purges_total" && m.Tag == "completed");
        purges.ShouldContain(m => m.Name == "account_purge_documents_total" && m.Tag == "anonymize");
        purges.ShouldContain(m => m.Name == "account_purge_documents_total" && m.Tag == "remove");
        host.Logs.ShouldContain(l => l.Contains($"Account of player {leaver.PlayerId} purged", StringComparison.Ordinal));

        // US2-6: not yet due and never deleted players are untouched.
        JsonNode.DeepEquals(host.Store.Peek(new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, later.PlayerId)), laterProfile).ShouldBeTrue();
        Record(host, later.PlayerId).ShouldBeNull();
        Owned(host, ReceiptDocument.Collection, stays.Player.PlayerId).Count().ShouldBe(staysReceipts);
        Profile(host, stays.Player.PlayerId).ShouldNotBeNull();

        // US2-3: the same credentials now create new players.
        var guest = await host.Client().LoginAsync(leaver.LocalId, leaver.Secret);
        guest.PlayerId.ShouldNotBe(leaver.PlayerId);
        var provider = await ProviderLoginAsync(host, leaver.Pgs);
        provider.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await provider.ReadProtobufAsync<LoginResponse>()).PlayerId.ShouldNotBeOneOf(leaver.PlayerId, guest.PlayerId);
    }

    /// <summary>
    /// Slice 012 T028 (FR-012, FR-036): a payment record whose delivery failed is delivered by the purge's relay of the
    /// rewritten receipt and carries the anonymous id with the event id computed before the purge; a record written before
    /// the purge keeps the raw id (written files are never rewritten).
    /// </summary>
    [Fact]
    public async Task A_pending_payment_record_is_delivered_by_the_purge_with_the_anonymous_id()
    {
        await using var host = await StartAsync();
        var (player, token) = await host.NewPlayerAsync();
        (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
        host.Analytics.FailWrites = true;
        var pending = await player.IapAsync("apple", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));
        host.Analytics.FailWrites = false;
        pending.Code.ShouldBe("OK");
        var receiptKey = ReceiptDocument.KeyFor(pending.ReceiptId);
        var receipt = host.Store.Peek(receiptKey)!;
        receipt["outboxPending"]!.GetValue<bool>().ShouldBeTrue();
        var expectedEventId = AnalyticsEventIds.ForOutboxEntry(receipt["outbox"]!.AsArray().Last()!["entryId"]!.GetValue<string>(), receipt["committedAtUtc"]!.GetValue<string>());
        (await RequestDeletionAsync(host, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var anonymousId = host.Store.Peek(receiptKey)!["playerId"]!.GetValue<string>();
        anonymousId.ShouldMatch(AnonymousIdPattern);
        host.Store.Peek(receiptKey)!["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
        var payments = host.Analytics.OfEvent("payment");
        payments.Count.ShouldBe(2);
        payments[0]["userID"]!.GetValue<string>().ShouldBe(player.PlayerId);
        payments[1]["userID"]!.GetValue<string>().ShouldBe(anonymousId);
        payments[1]["eventId"]!.GetValue<string>().ShouldBe(expectedEventId);
        payments[1]["receiptId"]!.GetValue<string>().ShouldBe(pending.ReceiptId);
        foreach (var collection in host.Services.GetRequiredService<ModuleRegistry>().RequiredCollections().Where(c => c != PlatformCollections.AccountDeletions))
        {
            host.Store.All(collection).ShouldAllBe(d => !d.Content.ToJsonString().Contains(player.PlayerId, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_failed_step_keeps_the_player_purging_and_a_later_run_completes_with_the_same_end_state()
    {
        var failures = new ConcurrentQueue<(string Name, long Value, string? Tag)>();
        using var listener = PurgeMetrics(failures);

        // A job interval shorter than the 10-minute backoff, so a run happens while the player waits.
        await using var host = await StartAsync(s => s[$"GameServer:Jobs:Settings:{AccountPurgeJob.JobName}:IntervalSeconds"] = "60");
        var leaver = await LeaverAsync(host);
        var before = Measure(host, leaver.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        host.Store.Faults.OnlyWhenWritingCollection = IdentityCollections.Identities;
        host.Store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var failedAt = host.Clock.UtcNow;
        var record = AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.Attempts.ShouldBe(1);
        record.LastFailure.ShouldBe(new PurgeFailure(AccountPurgeService.RemoveStep(IdentityCollections.Identities), typeof(TransactionFailedException).FullName!, failedAt));
        record.NextAttemptAtUtc.ShouldBe(failedAt.AddMinutes(10));
        record.CompletedSteps.ShouldContain(AccountPurgeService.ReceiptsStep);
        Profile(host, leaver.PlayerId)!.Deleted!.PurgeStartedAtUtc.ShouldBe(failedAt);
        failures.ShouldContain(m => m.Name == "account_purge_step_failures_total" && m.Tag == "remove:identities");
        failures.ShouldContain(m => m.Name == "account_purges_total" && m.Tag == "failed");
        host.Logs.ShouldContain(l => l.Contains($"Purge of player {leaver.PlayerId} failed at step remove:identities", StringComparison.Ordinal));
        host.Store.Faults.OnlyWhenWritingCollection = null;

        // Not due before its backoff: the next run leaves it alone.
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!).State.ShouldBe(AccountDeletionState.Purging);

        host.Clock.Advance(TimeSpan.FromMinutes(10));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        ShouldBePurged(host, leaver, before);
    }

    [Fact]
    public async Task A_store_failure_in_the_middle_of_a_step_resumes_where_it_stopped()
    {
        await using var host = await StartAsync(s => s["GameServer:Purge:BatchSize"] = "2");
        var leaver = await LeaverAsync(host, increments: 5);
        var before = Measure(host, leaver.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        host.Store.Faults.OnlyWhenWritingCollection = Sample.Counter.CounterModuleDefinition.LogCollection;
        host.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(1);
        host.Store.Faults.Enqueue(StoreFault.DelayCommit); // first batch of counter_log commits
        host.Store.Faults.Enqueue(StoreFault.FailBeforeCommit); // second batch fails

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var record = AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.LastFailure!.Step.ShouldBe(AccountPurgeService.AnonymizeStep(Sample.Counter.CounterModuleDefinition.LogCollection));
        record.CompletedSteps.ShouldContain(AccountPurgeService.AnonymizeStep(EconomyOperations.LedgerCollection));
        Owned(host, Sample.Counter.CounterModuleDefinition.LogCollection, leaver.PlayerId).Count().ShouldBe(before.CounterLog - 2);
        host.Store.Faults.OnlyWhenWritingCollection = null;

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        ShouldBePurged(host, leaver, before);
    }

    [Fact]
    public async Task An_exhausted_budget_leaves_the_purge_due_and_the_next_run_completes_it()
    {
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        var before = Measure(host, leaver.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        var ticket = (await BeginAsync(host, leaver.PlayerId, 1)).ShouldNotBeNull();

        var outcome = await host.Services.GetRequiredService<AccountPurgeService>().RunAsync(ticket, new PurgeBudget(3), CancellationToken.None);

        outcome.ShouldBe(PurgeOutcome.Incomplete);
        var record = AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.DocumentsProcessed.ShouldBe(3);
        record.NextAttemptAtUtc.ShouldNotBeNull();
        record.NextAttemptAtUtc.Value.ShouldBeLessThanOrEqualTo(host.Clock.UtcNow);
        record.Attempts.ShouldBe(0);

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        ShouldBePurged(host, leaver, before);
    }

    [Fact]
    public async Task A_run_purges_at_most_MaxPlayersPerRun_players()
    {
        await using var host = await StartAsync(s => s["GameServer:Purge:MaxPlayersPerRun"] = "1");
        var first = await LeaverAsync(host, increments: 1);
        var firstBefore = Measure(host, first.PlayerId);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await LeaverAsync(host, increments: 1);
        var secondBefore = Measure(host, second.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        ShouldBePurged(host, first, firstBefore);
        Record(host, second.PlayerId).ShouldBeNull();
        Profile(host, second.PlayerId)!.Deleted!.PurgeStartedAtUtc.ShouldBeNull();

        host.Clock.Advance(NextRun);
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        ShouldBePurged(host, second, secondBefore);
    }

    [Fact]
    public async Task A_stale_holder_is_fenced_out_and_its_run_stops()
    {
        var fenced = new ConcurrentQueue<(string Name, long Value, string? Tag)>();
        using var listener = PurgeMetrics(fenced);
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        var stale = (await BeginAsync(host, leaver.PlayerId, 1)).ShouldNotBeNull();
        (await BeginAsync(host, leaver.PlayerId, 50)).ShouldNotBeNull(); // a newer lease holder claimed the purge
        var receipts = Owned(host, ReceiptDocument.Collection, leaver.PlayerId).Count();

        await Should.ThrowAsync<PurgeFencedOutException>(
            () => host.Services.GetRequiredService<AccountPurgeService>().RunAsync(stale, new PurgeBudget(1000), CancellationToken.None));

        AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!).FencingToken.ShouldBe(50);
        AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!).Attempts.ShouldBe(0);
        Owned(host, ReceiptDocument.Collection, leaver.PlayerId).Count().ShouldBe(receipts);
    }

    [Fact]
    public async Task A_job_run_whose_lease_was_taken_over_ends_as_a_lost_lease()
    {
        var fenced = new ConcurrentQueue<(string Name, long Value, string? Tag)>();
        using var listener = PurgeMetrics(fenced);
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        var leases = host.Services.GetRequiredService<LeaseStore>();
        (await leases.TryAcquireAsync(AccountPurgeJob.JobName, "stale-owner", CancellationToken.None)).Lease!.FencingToken.ShouldBe(1);
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        (await leases.TryAcquireAsync(AccountPurgeJob.JobName, "new-owner", CancellationToken.None)).Lease!.FencingToken.ShouldBe(2);
        (await BeginAsync(host, leaver.PlayerId, 2)).ShouldNotBeNull(); // the new holder claimed the purge
        var job = host.Services.GetRequiredService<AccountPurgeJob>();

        // The stale holder's run (token 1) meets token 2 on the record, and the lease document confirms the takeover.
        await Should.ThrowAsync<LeaseLostException>(() => job.RunAsync(new JobContext(AccountPurgeJob.JobName, 1, CancellationToken.None)));

        AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!).FencingToken.ShouldBe(2);
        host.Logs.ShouldContain(l => l.Contains("Account purge run stopped", StringComparison.Ordinal));
        fenced.ShouldContain(m => m.Name == "account_purges_total" && m.Tag == "fenced_out");
    }

    [Fact]
    public async Task A_run_holding_a_recreated_lease_skips_a_player_with_a_larger_record_token_and_goes_on()
    {
        var fenced = new ConcurrentQueue<(string Name, long Value, string? Tag)>();
        using var listener = PurgeMetrics(fenced);
        await using var host = await StartAsync();
        var stuck = await LeaverAsync(host);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var other = await LeaverAsync(host);
        var otherBefore = Measure(host, other.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        (await BeginAsync(host, stuck.PlayerId, 50)).ShouldNotBeNull(); // claimed under a lease document that no longer exists
        var receipts = Owned(host, ReceiptDocument.Collection, stuck.PlayerId).Count();

        // The fresh lease document starts at token 1 and this run holds it: not a lost lease (review minor 1).
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        var record = AccountDeletionRecord.FromJson(Record(host, stuck.PlayerId)!);
        record.FencingToken.ShouldBe(50);
        record.State.ShouldBe(AccountDeletionState.Purging);
        record.Attempts.ShouldBe(0);
        Owned(host, ReceiptDocument.Collection, stuck.PlayerId).Count().ShouldBe(receipts);
        host.Logs.ShouldContain(l => l.Contains($"Purge of player {stuck.PlayerId} skipped", StringComparison.Ordinal) && l.Contains("never delete leases::*", StringComparison.Ordinal));
        fenced.ShouldContain(m => m.Name == "account_purges_total" && m.Tag == "fenced_out");
        ShouldBePurged(host, other, otherBefore);
        host.Store.Peek(LeaseStore.KeyOf(AccountPurgeJob.JobName))!["owner"].ShouldBeNull(); // released normally
    }

    [Fact]
    public async Task An_ambiguous_final_transaction_that_committed_counts_as_completed()
    {
        var purges = new ConcurrentQueue<(string Name, long Value, string? Tag)>();
        using var listener = PurgeMetrics(purges);
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        var before = Measure(host, leaver.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        (await BeginAsync(host, leaver.PlayerId, 1)).ShouldNotBeNull();

        // Only the final transaction writes pa_profile now: it commits, then reports an ambiguous error (review minor 1).
        host.Store.Faults.OnlyWhenWritingCollection = IdentityAggregates.ProfileDescriptor.Collection;
        host.Store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        host.Store.Faults.OnlyWhenWritingCollection = null;
        ShouldBePurged(host, leaver, before);
        purges.ShouldContain(m => m.Name == "account_purges_total" && m.Tag == "completed");
        purges.ShouldNotContain(m => m.Name == "account_purges_total" && (m.Tag == "failed" || m.Tag == "fenced_out"));
        host.Logs.ShouldContain(l => l.Contains("but the purge had completed", StringComparison.Ordinal));
        host.Logs.ShouldNotContain(l => l.Contains("was fenced out", StringComparison.Ordinal) || l.Contains("Account purge run stopped", StringComparison.Ordinal));
        host.Store.Peek(LeaseStore.KeyOf(AccountPurgeJob.JobName))!["owner"].ShouldBeNull(); // the lease was released
    }

    [Fact]
    public async Task A_cached_session_of_a_purged_player_answers_account_deleted_then_unauthenticated()
    {
        await using var a = await StartAsync(s => s["Identity:CacheSeconds"] = "60");
        await using var b = await StartAsync(s => s["Identity:CacheSeconds"] = "60", a.Store, a.Clock);
        var leaver = await LeaverAsync(a);
        a.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        var onB = b.BearerClient(leaver.Token);
        (await onB.PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await RunPurgeAsync(a)).ShouldBe(JobRunResult.Completed);

        // B still has the session cached; its profile entry is refreshed first (e.g. by a GM lookup) and finds no document.
        (await b.Services.GetRequiredService<ProfileCache>().ReadFreshAsync(leaver.PlayerId, CancellationToken.None)).ShouldBeNull();
        var cached = await onB.PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest());
        cached.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var error = await cached.ReadErrorAsync();
        error.Code.ShouldBe("ACCOUNT_DELETED");
        error.Details.ShouldBeEmpty();

        // Once the session entry expires the token is unknown (D2).
        a.Clock.Advance(TimeSpan.FromSeconds(61));
        var unknown = await onB.PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest());
        (await unknown.ReadErrorAsync()).Code.ShouldBe("UNAUTHENTICATED");
    }

    [Fact]
    public async Task A_login_paused_until_the_purge_completed_creates_no_profile_and_no_session()
    {
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        var before = Measure(host, leaver.PlayerId);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        var completion = host.Services.GetRequiredService<LoginCompletion>();
        var purged = JobRunResult.NotDue;
        completion.AfterMappingRead = async _ =>
        {
            completion.AfterMappingRead = null;
            purged = await RunPurgeAsync(host);
        };

        var refused = await host.Client().PostLoginAsync(leaver.LocalId, leaver.Secret);

        purged.ShouldBe(JobRunResult.Completed);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await refused.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
        ShouldBePurged(host, leaver, before);

        // The credential is free again: the next login creates a new player.
        (await host.Client().LoginAsync(leaver.LocalId, leaver.Secret)).PlayerId.ShouldNotBe(leaver.PlayerId);
    }

    [Fact]
    public async Task Credentials_and_tokens_of_a_player_being_purged_are_refused()
    {
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        host.Store.Faults.OnlyWhenWritingCollection = IdentityCollections.Identities;
        host.Store.Faults.Enqueue(StoreFault.FailBeforeCommit);
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!).State.ShouldBe(AccountDeletionState.Purging);
        var players = host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection);

        foreach (var response in new[]
                 {
                     await host.Client().PostLoginAsync(leaver.LocalId, leaver.Secret),
                     await ProviderLoginAsync(host, leaver.Pgs),
                     await host.BearerClient(leaver.Token).PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest()),
                 })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var error = await response.ReadErrorAsync();
            error.Code.ShouldBe("ACCOUNT_DELETED");
            error.Details.ShouldContainKey("purgeAfter");
        }

        host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(players);
    }

    [Fact]
    public async Task A_purge_failing_after_every_retained_step_still_refuses_the_old_credentials()
    {
        // Review minor 2: the identity mappings go last, so a purge stuck at the aggregates step keeps answering ACCOUNT_DELETED.
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var aggregate = registry.Aggregates.Values
            .Select(a => a.Collection)
            .First(c => c != IdentityAggregates.ProfileDescriptor.Collection && host.Store.Peek(new DocumentKey(c, leaver.PlayerId)) is not null);
        host.Store.Faults.OnlyWhenWritingCollection = aggregate;
        host.Store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        host.Store.Faults.OnlyWhenWritingCollection = null;
        var record = AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!);
        record.LastFailure!.Step.ShouldBe(AccountPurgeService.AggregatesStep);
        record.CompletedSteps.ShouldContain(AccountPurgeService.VerifyStep);
        record.CompletedSteps.ShouldNotContain(AccountPurgeService.RemoveStep(IdentityCollections.Identities));
        var players = host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection);
        foreach (var response in new[] { await host.Client().PostLoginAsync(leaver.LocalId, leaver.Secret), await ProviderLoginAsync(host, leaver.Pgs) })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await response.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
        }

        host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(players);
    }

    [Fact]
    public async Task A_login_between_the_mapping_removal_and_the_final_transaction_creates_a_new_player_the_purge_leaves_alone()
    {
        // Review minor 2, the documented residual window (PCR identity): once remove:identities committed, the old
        // credentials are free — the same outcome as after the purge. The resumed purge must not touch the new player.
        await using var host = await StartAsync();
        var leaver = await LeaverAsync(host);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        (await BeginAsync(host, leaver.PlayerId, 1)).ShouldNotBeNull();
        host.Store.Faults.OnlyWhenWritingCollection = IdentityAggregates.ProfileDescriptor.Collection; // only the final transaction
        host.Store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        host.Store.Faults.OnlyWhenWritingCollection = null;
        var record = AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!);
        record.LastFailure!.Step.ShouldBe(AccountPurgeService.CompleteStep);
        record.CompletedSteps.ShouldContain(AccountPurgeService.RemoveStep(IdentityCollections.Identities));
        var newcomer = await host.Client().LoginAsync(leaver.LocalId, leaver.Secret);
        newcomer.PlayerId.ShouldNotBe(leaver.PlayerId);

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        AccountDeletionRecord.FromJson(Record(host, leaver.PlayerId)!).State.ShouldBe(AccountDeletionState.Purged);
        Profile(host, leaver.PlayerId).ShouldBeNull();
        Profile(host, newcomer.PlayerId).ShouldNotBeNull();
        host.Store.Peek(new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(leaver.LocalId)))!["playerId"]!.GetValue<string>()
            .ShouldBe(newcomer.PlayerId);
        Owned(host, SessionService.Collection, newcomer.PlayerId).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_overdue_gauge_covers_pending_deletions_even_when_resumed_purges_fill_the_run()
    {
        // Review minor 5: with MaxPlayersPerRun = 1 the resumed purge fills the run; the older pending deletion still counts.
        var overdue = new ConcurrentQueue<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName && instrument.Name == "account_purge_overdue_seconds")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => overdue.Enqueue(value));
        listener.Start();
        await using var host = await StartAsync(s => s["GameServer:Purge:MaxPlayersPerRun"] = "1");
        var older = await LeaverAsync(host, increments: 1);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = await LeaverAsync(host, increments: 1);
        host.Clock.Advance(Grace + TimeSpan.FromSeconds(9_876)); // older is 9 877 s overdue, resumed 9 876 s
        (await BeginAsync(host, resumed.PlayerId, 1)).ShouldNotBeNull();

        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);

        Record(host, older.PlayerId).ShouldBeNull(); // not processed in this run
        listener.RecordObservableInstruments();
        overdue.ShouldContain(9_877);
    }

    private static Task<PurgeTicket?> BeginAsync(TestHost host, string playerId, long token)
    {
        var now = host.Clock.UtcNow;
        return host.Services.GetRequiredService<IAccountPurgeWriter>().BeginAsync<ProfileState>(
            playerId,
            token,
            p => AccountPurgeStart.Decide(p, now) is { Deleted: { } deleted } marked ? new PurgeStart<ProfileState>(marked, deleted.RequestedAtUtc, deleted.PurgeAfterUtc) : null,
            CancellationToken.None);
    }

    private static MeterListener PurgeMetrics(ConcurrentQueue<(string Name, long Value, string? Tag)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName && instrument.Name.StartsWith("account_purge", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tag = null;
            foreach (var t in tags)
            {
                tag = t.Value?.ToString();
            }

            sink.Enqueue((instrument.Name, value, tag));
        });
        listener.Start();
        return listener;
    }

    /// <summary>Compares footprints by content (the dictionary of sums by value).</summary>
    private sealed class FootprintComparer : IEqualityComparer<Footprint>
    {
        public bool Equals(Footprint? x, Footprint? y) =>
            x is not null && y is not null
            && x.Receipts == y.Receipts && x.Ledger == y.Ledger && x.Audits == y.Audits && x.CounterLog == y.CounterLog && x.Claims == y.Claims
            && x.LedgerSums.Count == y.LedgerSums.Count && x.LedgerSums.All(kv => y.LedgerSums.TryGetValue(kv.Key, out var v) && v == kv.Value);

        public int GetHashCode(Footprint obj) => obj.Receipts;
    }
}

/// <summary>The purge on the in-memory store.</summary>
/// <remarks>Both variants share one collection: the purge metrics are read through a process-wide MeterListener.</remarks>
[Collection(nameof(AccountPurgeTests))]
public sealed class InMemoryAccountPurgeTests : AccountPurgeTests
{
    protected override TestStoreKind StoreKind => TestStoreKind.InMemory;
}

/// <summary>The purge on the file store: same semantics, every write persisted (slice 011 T023).</summary>
[Collection(nameof(AccountPurgeTests))]
public sealed class FileAccountPurgeTests : AccountPurgeTests
{
    protected override TestStoreKind StoreKind => TestStoreKind.File;
}
