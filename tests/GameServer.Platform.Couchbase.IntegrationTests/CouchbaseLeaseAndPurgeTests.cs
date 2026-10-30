using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Couchbase.KeyValue;
using Couchbase.Query;
using GameServer.Jobs;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Jobs;
using GameServer.Platform.Persistence;
using GameServer.Platform.Privacy;
using GameServer.Platform.Registry;
using GameServer.Platform.Testing;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using ProfileState = GameServer.Modules.Identity.ProfileState;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 010 on a real Couchbase: lease contention, takeover and fencing between instances (US4, FR-002, FR-003, SC-005), and
/// the account deletion end to end on the full sample game: request and login refusal, purge with a scan of every collection,
/// crash and resume on another instance, the cancel-versus-start and login-versus-purge races on real transactions, and the
/// query plans of the due and reference queries (US1..US3, PRIV-01, PERF-07).
/// </summary>
/// <remarks>
/// All tests of the class share one scope (provisioned by the hosts only) and therefore the purge job's lease document and every pending
/// deletion; they share one clock that only moves forward, so a lease released by one test is due in the next. Tests that
/// start purges directly finish them, so no record with a foreign fencing token is left behind.
/// </remarks>
public sealed class CouchbaseLeaseAndPurgeTests(CouchbaseGameFixture fixture) : IClassFixture<CouchbaseGameFixture>
{
    private const string IncrementRoute = "/api/v1/counter/increment";
    private const string AnonymousIdPattern = "^anon_[0-9a-hjkmnp-tv-z]{26}$";
    private const int RaceRounds = 4;

    /// <summary>
    /// Delay of the faster side of the cancel race, per round: the rounds stay concurrent but the start order varies, so both
    /// winners occur (a bare <c>Task.WhenAll</c> on a local cluster always lets the shorter transaction win).
    /// </summary>
    private static readonly int[] StaggerMilliseconds = [0, 20, 40, 80];

    /// <summary>Per round of the anchor race: positive = the removal waits, negative = the login waits (both orders occur).</summary>
    private static readonly int[] AnchorStaggerMilliseconds = [0, 20, -20, -40];
    private const int ShortLeaseSeconds = 30;
    private static readonly TimeSpan Grace = TimeSpan.FromDays(30);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>Purge job lease (default 60 s) plus one second: an unreleased lease can be taken over after it.</summary>
    private static readonly TimeSpan PastPurgeLease = TimeSpan.FromSeconds(61);

    /// <summary>Monotonic across the tests of the class (shared lease documents).</summary>
    private static readonly FakeClock Clock = new();

    private sealed record Leaver(string PlayerId, string LocalId, byte[] Secret, string Pgs, string Token);

    /// <summary>What the player left in the retained collections before the purge (SC-003); <c>FirstReceipt</c> by key order.</summary>
    private sealed record Footprint(int Receipts, int Ledger, string LedgerSums, int Audits, int CounterLog, int Claims, string FirstReceipt);

    private Task<CouchbaseGameHost> StartHostAsync(Action<Dictionary<string, string?>>? settings = null)
    {
        fixture.RequireCouchbase();
        return CouchbaseGameHost.StartAsync(
            fixture,
            Clock,
            s =>
            {
                // Sessions outlive the grace period, so the purge itself must remove them.
                s["Identity:SessionSlidingDays"] = "60";
                settings?.Invoke(s);
            });
    }

    // ---------------------------------------------------------------- T051: leases between instances (US4)

    [Fact]
    public async Task Two_instances_polling_one_lease_never_run_the_job_concurrently()
    {
        var job = NewJobName();
        var probe = new JobProbe { Behavior = (_, context) => Task.Delay(300, context.Cancellation) };
        await using var a = await StartProbeHostAsync(job, probe, "a", loop: true);
        await using var b = await StartProbeHostAsync(job, probe, "b", loop: true);

        await WaitUntilAsync(async () => probe.Runs.Count >= 1 && (await LeaseAsync(job))?["owner"] is null, "the first run and release");
        Clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => probe.Runs.Count >= 2 && (await LeaseAsync(job))?["owner"] is null, "the second run and release");

        probe.MaxConcurrent.ShouldBe(1);
        probe.Runs.ShouldAllBe(r => r.RunnerOwnsLease);
        probe.Runs.Select(r => r.Token).ShouldBe([1L, 2L]);
        var lease = (await LeaseAsync(job))!;
        lease["fencingToken"]!.GetValue<long>().ShouldBe(2);
        lease["lastRun"]!["result"]!.GetValue<string>().ShouldBe(nameof(JobRunResult.Completed));
    }

    [Fact]
    public async Task A_holder_stopped_without_release_is_taken_over_after_its_lease_with_a_larger_token()
    {
        var job = NewJobName();
        var probe = new JobProbe();
        var a = await StartProbeHostAsync(job, probe, "a", loop: false);
        await using var b = await StartProbeHostAsync(job, probe, "b", loop: false);

        // A acquires and then dies: nothing releases its lease.
        var held = await a.Services.GetRequiredService<LeaseStore>()
            .TryAcquireAsync(job, a.Services.GetRequiredService<LeaseJobRunner>().Owner, CancellationToken.None);
        held.Lease.ShouldNotBeNull().FencingToken.ShouldBe(1);
        await a.DisposeAsync();

        var runner = b.Services.GetRequiredService<LeaseJobRunner>();
        (await runner.RunOnceAsync(job, CancellationToken.None)).ShouldBe(JobRunResult.HeldElsewhere);
        Clock.Advance(TimeSpan.FromSeconds(ShortLeaseSeconds - 1));
        (await runner.RunOnceAsync(job, CancellationToken.None)).ShouldBe(JobRunResult.HeldElsewhere);
        Clock.Advance(TimeSpan.FromSeconds(2));
        (await runner.RunOnceAsync(job, CancellationToken.None)).ShouldBe(JobRunResult.Completed);

        probe.Runs.Select(r => (r.Host, r.Token)).ShouldBe([("b", 2L)]);
        var lease = (await LeaseAsync(job))!;
        lease["owner"].ShouldBeNull();
        lease["fencingToken"]!.GetValue<long>().ShouldBe(2);

        // DAT-03: lease documents never expire.
        var leases = await fixture.CollectionAsync(PlatformCollections.Leases);
        (await leases.GetAsync(LeaseStore.KeyOf(job).Key, new GetOptions().Expiry())).ExpiryTime.ShouldBeNull();
    }

    [Fact]
    public async Task A_paused_holder_is_fenced_out_of_the_purge_the_instance_that_took_over_completed()
    {
        await using var a = await StartHostAsync();
        await using var b = await StartHostAsync();
        var leaver = await LeaverAsync(a);
        var before = await MeasureAsync(leaver.PlayerId);
        Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        // A holds the purge lease, starts the purge, then stalls (GC or VM pause) without renewing.
        var aLease = (await a.Services.GetRequiredService<LeaseStore>().TryAcquireAsync(
            AccountPurgeJob.JobName,
            a.Services.GetRequiredService<LeaseJobRunner>().Owner,
            CancellationToken.None)).Lease.ShouldNotBeNull();
        var stale = (await BeginAsync(a, leaver.PlayerId, aLease.FencingToken)).ShouldNotBeNull();

        // B takes the lease over with a larger token and purges.
        Clock.Advance(PastPurgeLease);
        (await RunPurgeAsync(b)).ShouldBe(JobRunResult.Completed);
        var anonymousId = await ShouldBePurgedAsync(b, leaver, before);

        // A wakes up: every purge write is refused, its release finds the lease taken over (FR-003, SC-005).
        var refused = await Should.ThrowAsync<PurgeFencedOutException>(
            () => a.Services.GetRequiredService<AccountPurgeService>().RunAsync(stale, new PurgeBudget(1000), CancellationToken.None));
        refused.FencingToken.ShouldBe(aLease.FencingToken);
        refused.CurrentToken.ShouldBe(aLease.FencingToken + 1);
        (await a.Services.GetRequiredService<LeaseStore>().ReleaseAsync(aLease, Clock.UtcNow, nameof(JobRunResult.Completed), CancellationToken.None))
            .ShouldBeFalse();
        (await ShouldBePurgedAsync(b, leaver, before)).ShouldBe(anonymousId);
        (await RecordAsync(leaver.PlayerId))!.FencingToken.ShouldBe(aLease.FencingToken + 1);
    }

    // ---------------------------------------------------------------- T052: deletion and purge (US1..US3)

    [Fact]
    public async Task A_deletion_request_revokes_the_token_and_refuses_both_logins()
    {
        await using var host = await StartHostAsync();
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var pgs = NewPgsId();

        // First login creates the profile anchored on the mapping the guest login inserted by key-value write (research R9).
        var login = await host.Client().LoginAsync(localId, secret);
        var player = new GamePlayer(host.BearerClient(login.SessionToken), login.PlayerId);
        (await player.CommandAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(pgs) })).Code.ShouldBe("OK");
        var mapping = (await GetAsync(host, new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(localId)))).ShouldNotBeNull();
        var players = await CountAsync(IdentityAggregates.ProfileDescriptor.Collection);

        var response = await RequestDeletionAsync(host, login.SessionToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var command = await response.ReadProtobufAsync<CommandResponse>();
        command.Code.ShouldBe("OK");
        var purgeAfter = Clock.UtcNow.AddDays(30);
        RequestAccountDeletionResult.Parser.ParseFrom(command.Result).PurgeAfterUtcMs.ShouldBe(purgeAfter.ToUnixTimeMilliseconds());
        var expectedDetail = purgeAfter.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var refused in new[]
                 {
                     await host.BearerClient(login.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest()),
                     await host.Client().PostLoginAsync(localId, secret),
                     await ProviderLoginAsync(host, pgs),
                 })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var error = await refused.ReadErrorAsync();
            error.Code.ShouldBe("ACCOUNT_DELETED");
            error.Details["purgeAfter"].ShouldBe(expectedDetail);
        }

        // No session, no new player, the mapping untouched (US1-3, FR-008).
        (await CountAsync(IdentityAggregates.ProfileDescriptor.Collection)).ShouldBe(players);
        JsonNode.DeepEquals((await GetAsync(host, mapping.Key))!.Content, mapping.Content).ShouldBeTrue();
        (await OwnedAsync(SessionService.Collection, login.PlayerId)).Count.ShouldBe(1);
        (await ProfileAsync(host, login.PlayerId))!.Deleted!.PurgeAfterUtc.ShouldBe(purgeAfter);
    }

    [Fact]
    public async Task One_run_purges_a_due_account_and_a_scan_of_every_collection_finds_the_old_id_only_in_its_record()
    {
        await using var host = await StartHostAsync();
        var leaver = await LeaverAsync(host);
        var before = await MeasureAsync(leaver.PlayerId);
        Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await RunPurgeAsync(host)).ShouldBe(JobRunResult.Completed);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Purge run of one player ({before.Receipts} receipts, {before.Ledger} ledger entries): {watch.ElapsedMilliseconds} ms");

        await ShouldBePurgedAsync(host, leaver, before);

        // US2-3: the same credentials now create new players.
        var guest = await host.Client().LoginAsync(leaver.LocalId, leaver.Secret);
        guest.PlayerId.ShouldNotBe(leaver.PlayerId);
        var provider = await ProviderLoginAsync(host, leaver.Pgs);
        provider.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await provider.ReadProtobufAsync<LoginResponse>()).PlayerId.ShouldNotBeOneOf(leaver.PlayerId, guest.PlayerId);
    }

    [Fact]
    public async Task A_purge_interrupted_by_a_crash_on_one_instance_is_completed_by_another_with_the_same_end_state()
    {
        var a = await StartHostAsync(s => s["GameServer:Purge:BatchSize"] = "2");
        await using var b = await StartHostAsync();
        var leaver = await LeaverAsync(a);
        var before = await MeasureAsync(leaver.PlayerId);
        Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        // A holds the lease and gets three documents into the purge, then the process dies (no release, no failure record).
        var aLease = (await a.Services.GetRequiredService<LeaseStore>().TryAcquireAsync(
            AccountPurgeJob.JobName,
            a.Services.GetRequiredService<LeaseJobRunner>().Owner,
            CancellationToken.None)).Lease.ShouldNotBeNull();
        var ticket = (await BeginAsync(a, leaver.PlayerId, aLease.FencingToken)).ShouldNotBeNull();
        (await a.Services.GetRequiredService<AccountPurgeService>().RunAsync(ticket, new PurgeBudget(3), CancellationToken.None))
            .ShouldBe(PurgeOutcome.Incomplete);
        await a.DisposeAsync();
        var interrupted = (await RecordAsync(leaver.PlayerId)).ShouldNotBeNull();
        interrupted.State.ShouldBe(AccountDeletionState.Purging);
        interrupted.DocumentsProcessed.ShouldBeGreaterThan(0);
        (await OwnedAsync(ReceiptDocument.Collection, ticket.AnonymousId)).ShouldNotBeEmpty();

        // B cannot run while the dead holder's lease lasts, then takes over and resumes from the record.
        (await RunPurgeAsync(b)).ShouldBe(JobRunResult.HeldElsewhere);
        Clock.Advance(PastPurgeLease);
        (await RunPurgeAsync(b)).ShouldBe(JobRunResult.Completed);

        (await ShouldBePurgedAsync(b, leaver, before)).ShouldBe(ticket.AnonymousId);
        (await RecordAsync(leaver.PlayerId))!.FencingToken.ShouldBe(aLease.FencingToken + 1);
    }

    [Fact]
    public async Task Two_instances_beginning_one_purge_at_once_leave_one_record_claimed_by_the_larger_token()
    {
        await using var a = await StartHostAsync();
        await using var b = await StartHostAsync();
        var pending = new List<string>();
        for (var i = 0; i < RaceRounds; i++)
        {
            pending.Add((await PendingAsync(a)).PlayerId);
        }

        Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        foreach (var playerId in pending)
        {
            // Lost profile write or lost record insert (Couchbase reports it as "exists"): the loser reads again.
            var smaller = Task.Run(() => BeginAsync(a, playerId, 1));
            var larger = Task.Run(() => BeginAsync(b, playerId, 2));
            var winner = (await larger).ShouldNotBeNull();
            PurgeTicket? loser;
            try
            {
                loser = await smaller;
            }
            catch (PurgeFencedOutException fenced)
            {
                fenced.CurrentToken.ShouldBe(2);
                loser = null;
            }

            var record = (await RecordAsync(playerId)).ShouldNotBeNull();
            record.State.ShouldBe(AccountDeletionState.Purging);
            record.FencingToken.ShouldBe(2);
            record.AnonymousId.ShouldBe(winner.AnonymousId);
            loser?.AnonymousId.ShouldBe(winner.AnonymousId);
            (await ProfileAsync(a, playerId))!.Deleted!.PurgeStartedAtUtc.ShouldNotBeNull();

            (await b.Services.GetRequiredService<AccountPurgeService>().RunAsync(winner, new PurgeBudget(1000), CancellationToken.None))
                .ShouldBe(PurgeOutcome.Completed);
        }
    }

    [Fact]
    public async Task A_cancel_racing_the_purge_start_on_real_transactions_has_exactly_one_winner()
    {
        await using var host = await StartHostAsync();
        var outcomes = new List<string>();
        for (var round = 0; round < RaceRounds; round++)
        {
            var pending = await PendingAsync(host);

            // The start's clock runs past the grace period, the cancel's does not: only the conflict on the profile decides (US3-6).
            var stagger = StaggerMilliseconds[round];
            var cancel = Task.Run(() => CancelAsync(host, pending.PlayerId));
            var begin = Task.Run(async () =>
            {
                await Task.Delay(stagger);
                return await BeginAsync(host, pending.PlayerId, 1, TimeSpan.FromDays(31));
            });
            var cancelled = await cancel;
            var ticket = await begin;

            var profile = (await ProfileAsync(host, pending.PlayerId)).ShouldNotBeNull();
            var record = await RecordAsync(pending.PlayerId);
            if (cancelled.StatusCode == HttpStatusCode.OK)
            {
                ticket.ShouldBeNull();
                profile.Deleted.ShouldBeNull();
                record.ShouldBeNull();
                (await OwnedAsync(GmModule.AuditCollection, pending.PlayerId)).Count.ShouldBe(1);
                outcomes.Add("cancel");
            }
            else
            {
                cancelled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                JsonNode.Parse(await cancelled.Content.ReadAsStringAsync())!["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");
                ticket.ShouldNotBeNull();
                profile.Deleted!.PurgeStartedAtUtc.ShouldNotBeNull();
                record.ShouldNotBeNull().State.ShouldBe(AccountDeletionState.Purging);
                (await OwnedAsync(GmModule.AuditCollection, pending.PlayerId)).ShouldBeEmpty();
                (await host.Services.GetRequiredService<AccountPurgeService>().RunAsync(ticket, new PurgeBudget(1000), CancellationToken.None))
                    .ShouldBe(PurgeOutcome.Completed);
                outcomes.Add("start");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine("Winners per round: " + string.Join(", ", outcomes));
        outcomes.ShouldContain("cancel", "the staggered rounds must let the cancel commit first at least once");
        outcomes.ShouldContain("start", "the staggered rounds must let the purge start commit first at least once");
    }

    [Fact]
    public async Task A_login_paused_until_the_purge_completed_creates_no_profile_and_no_session()
    {
        // The purge runs inside the paused login request; give it more than the 10 s request budget.
        await using var host = await StartHostAsync(s => s["GameServer:Limits:PlayerTimeoutSeconds"] = "60");
        var leaver = await LeaverAsync(host);
        var before = await MeasureAsync(leaver.PlayerId);
        Clock.Advance(Grace + TimeSpan.FromSeconds(1));
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
        await ShouldBePurgedAsync(host, leaver, before);
        (await host.Client().LoginAsync(leaver.LocalId, leaver.Secret)).PlayerId.ShouldNotBe(leaver.PlayerId);
    }

    [Fact]
    public async Task A_first_login_racing_the_removal_of_its_mapping_creates_the_profile_only_if_it_committed_first()
    {
        await using var host = await StartHostAsync();
        var writer = host.Services.GetRequiredService<IPlayerAggregateWriter>();
        var store = host.Services.GetRequiredService<ITransactionalDocumentStore>();
        var kv = host.Services.GetRequiredService<IKeyValueStore>();
        var outcomes = new List<string>();
        for (var round = 0; round < RaceRounds; round++)
        {
            var playerId = "p" + Guid.NewGuid().ToString("N")[..24];
            var anchor = new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(IdentityHttp.NewLocalId()));
            var profileKey = new DocumentKey(IdentityAggregates.ProfileDescriptor.Collection, playerId);

            // Guest login inserts the mapping by key-value write; the profile transaction then rewrites it as its anchor.
            (await kv.InsertAsync(anchor, new JsonObject { ["mappingKey"] = anchor.Key, ["playerId"] = playerId }, null, CancellationToken.None)).ShouldBeTrue();
            var stagger = AnchorStaggerMilliseconds[round];
            var login = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(0, -stagger));
                return await writer.UpdateAsync<ProfileState>(
                    playerId, IdentityAggregates.Profile, p => p with { SessionEpoch = p.SessionEpoch + 1 }, anchor, CancellationToken.None);
            });
            var removal = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(0, stagger));
                return await store.RunTransactionAsync(
                    async tx =>
                    {
                        // What the purge's remove:identities transaction does to the mapping.
                        if ((await tx.GetManyAsync([anchor]))[0] is not { } mapping)
                        {
                            return false;
                        }

                        await tx.RemoveAsync(mapping);
                        return true;
                    },
                    new TransactionRunOptions(TimeSpan.FromSeconds(10)));
            });

            bool created;
            try
            {
                await login;
                created = true;
            }
            catch (AggregateAnchorMissingException)
            {
                created = false;
            }

            (await removal).ShouldBeTrue();
            (await store.GetAsync(anchor, CancellationToken.None)).ShouldBeNull();
            (await store.GetAsync(profileKey, CancellationToken.None) is not null).ShouldBe(created);
            outcomes.Add(created ? "login" : "removal");
        }

        TestContext.Current.TestOutputHelper?.WriteLine("Winners per round: " + string.Join(", ", outcomes));
        outcomes.ShouldContain("login", "the staggered rounds must let the login commit first at least once");
        outcomes.ShouldContain("removal", "the staggered rounds must let the mapping removal commit first at least once");
    }

    // ---------------------------------------------------------------- T052: query plans (PERF-07)

    [Fact]
    public async Task Due_queries_use_their_partial_indexes()
    {
        await using var host = await StartHostAsync();

        var records = await ExplainAsync(CouchbaseDocumentStore.DueStatement(
            new DueQuery(PlatformCollections.AccountDeletions, AccountDeletionRecords.NextAttemptField, Clock.UtcNow, 50, AccountDeletionRecords.PurgedField)), Due());
        var profiles = await ExplainAsync(CouchbaseDocumentStore.DueStatement(
            new DueQuery(IdentityAggregates.ProfileDescriptor.Collection, AccountPurgeJob.PurgeAfterField, Clock.UtcNow, 50, AccountPurgeJob.PurgeStartedField, DueTimeFormat.Json)), Due());

        records.ShouldUse(CouchbaseProvisioner.AccountDeletionsDueIndex);
        profiles.ShouldUse(IdentityModule.ProfileDeletionDueIndex);

        // (due, META().id) keys: the oldest-first ORDER BY reads index order, the LIMIT is pushed down (no sort of the backlog).
        records.Sorts.ShouldBeFalse(records.Statement);
        profiles.Sorts.ShouldBeFalse(profiles.Statement);
    }

    [Fact]
    public async Task Reference_queries_of_every_anonymized_or_removed_collection_use_a_player_index()
    {
        await using var host = await StartHostAsync();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var provisioner = host.Services.GetRequiredService<CouchbaseProvisioner>();
        var targets = registry.DeletionPlan()
            .Where(d => d.Treatment is DeletionTreatment.Anonymize or DeletionTreatment.Remove && !d.IsPlayerAggregate)
            .ToArray();
        targets.Select(t => t.Collection).ShouldBe(
            ["receipts", "gm_audit", "identities", "sessions", "ledger", "extref_claims", "counter_log"],
            ignoreOrder: true);

        foreach (var target in targets)
        {
            // Every target gets its (field, META().id) index, history collections included (review minor 4).
            var expected = provisioner.ReferenceIndexes().Single(r => r.Collection == target.Collection).Name;
            expected.ShouldBe($"ix_{target.Collection}_player_ref");
            foreach (var afterKey in new string?[] { null, "rcpt::0" })
            {
                var plan = await ExplainAsync(
                    CouchbaseDocumentStore.ReferenceStatement(new ReferenceQuery(target.Collection, target.PlayerField, "p_explain", 25, afterKey)),
                    o =>
                    {
                        o.Parameter("value", "p_explain").Parameter("limit", 25);
                        if (afterKey is not null)
                        {
                            o.Parameter("after", afterKey);
                        }
                    });
                plan.ShouldUse(expected);

                // Key order read from the index, LIMIT pushed down: no per-batch sort of the player's history.
                plan.Sorts.ShouldBeFalse(plan.Statement);

                TestContext.Current.TestOutputHelper?.WriteLine($"{target.Collection} after={afterKey ?? "-"}: {expected}, sort={plan.Sorts}");
            }
        }
    }

    // ---------------------------------------------------------------- helpers: players

    private static string NewPgsId() => "g" + Guid.NewGuid().ToString("N")[..20];

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static Task<HttpResponseMessage> RequestDeletionAsync(CouchbaseGameHost host, string token) =>
        host.BearerClient(token).PostProtobufAsync(IdentityModule.DeleteAccountRoute, new RequestAccountDeletionRequest(), ProtobufHttp.NewRequestId());

    private static Task<HttpResponseMessage> ProviderLoginAsync(CouchbaseGameHost host, string pgs) =>
        host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = Pgs(pgs) });

    private static Task<HttpResponseMessage> CancelAsync(CouchbaseGameHost host, string playerId)
    {
        var cancel = new HttpRequestMessage(HttpMethod.Post, IdentityModule.CancelDeletionRoute.Replace("{playerId}", playerId, StringComparison.Ordinal))
        {
            Content = JsonContent.Create(new { reason = "integration test" }),
        };
        cancel.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        return host.GmClient("admin", IdentityPermissions.PlayerBan).SendAsync(cancel);
    }

    private static async Task<LoginResponse> PendingAsync(CouchbaseGameHost host)
    {
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        (await RequestDeletionAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return login;
    }

    /// <summary>
    /// A player with history in every retained collection (receipts, ledger, extref claims, counter log, a GM audit from a
    /// cancelled first request), a guest and a Play Games identity, who requested deletion again.
    /// </summary>
    private static async Task<Leaver> LeaverAsync(CouchbaseGameHost host)
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var pgs = NewPgsId();
        var first = await host.Client().LoginAsync(localId, secret);
        var player = new GamePlayer(host.BearerClient(first.SessionToken), first.PlayerId);
        (await player.CommandAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(pgs) })).Code.ShouldBe("OK");
        (await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = "Leaving " + localId[^4..] })).Code.ShouldBe("OK");
        for (var i = 0; i < 2; i++)
        {
            (await player.CommandAsync(IncrementRoute, new Sample.Counter.Protocol.V1.IncrementRequest { By = 1 })).Code.ShouldBe("OK");
        }

        (await player.CheatAsync("gold", 10)).Code.ShouldBe("OK");
        (await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"))).Code.ShouldBe("OK");
        (await RequestDeletionAsync(host, first.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CancelAsync(host, first.PlayerId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await host.Client().LoginAsync(localId, secret);
        (await RequestDeletionAsync(host, second.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Leaver(first.PlayerId, localId, secret, pgs, second.SessionToken);
    }

    private static Task<JobRunResult> RunPurgeAsync(CouchbaseGameHost host) =>
        host.Services.GetRequiredService<LeaseJobRunner>().RunOnceAsync(AccountPurgeJob.JobName, CancellationToken.None);

    /// <summary>The job's start decision, evaluated at the shared clock plus <paramref name="skew"/>.</summary>
    private static Task<PurgeTicket?> BeginAsync(CouchbaseGameHost host, string playerId, long token, TimeSpan skew = default)
    {
        var now = Clock.UtcNow + skew;
        return host.Services.GetRequiredService<IAccountPurgeWriter>().BeginAsync<ProfileState>(
            playerId,
            token,
            p => AccountPurgeStart.Decide(p, now) is { Deleted: { } deleted } marked ? new PurgeStart<ProfileState>(marked, deleted.RequestedAtUtc, deleted.PurgeAfterUtc) : null,
            CancellationToken.None);
    }

    private static async Task<ProfileState?> ProfileAsync(CouchbaseGameHost host, string playerId)
    {
        var loaded = await host.Services.GetRequiredService<PlayerSnapshotReader>()
            .ReadOneAsync(playerId, IdentityAggregates.Profile, CancellationToken.None);
        return loaded.Exists ? (ProfileState)loaded.State : null;
    }

    private static Task<StoredDocument?> GetAsync(CouchbaseGameHost host, DocumentKey key) =>
        host.Services.GetRequiredService<ITransactionalDocumentStore>().GetAsync(key, CancellationToken.None);

    private async Task<AccountDeletionRecord?> RecordAsync(string playerId) =>
        (await fixture.Store.GetAsync(AccountDeletionRecord.KeyOf(playerId), CancellationToken.None)) is { } document
            ? AccountDeletionRecord.FromJson(document.Content)
            : null;

    private async Task<JsonObject?> LeaseAsync(string jobName) =>
        (await fixture.Store.GetAsync(LeaseStore.KeyOf(jobName), CancellationToken.None))?.Content;

    // ---------------------------------------------------------------- helpers: scans and end state

    /// <summary>
    /// Every document of a collection at RequestPlus: a sequential scan (Couchbase ≥ 7.6), about 1.5 s per collection
    /// whatever its size on a local node, so callers scan collections in parallel.
    /// </summary>
    private async Task<List<(string Id, JsonObject Content)>> AllAsync(string collection)
    {
        var scope = await fixture.Connection.GetScopeAsync();
        var result = await scope.QueryAsync<ScanRow>(
            $"SELECT META(d).id AS id, d AS doc FROM `{collection}` AS d",
            new QueryOptions().ScanConsistency(QueryScanConsistency.RequestPlus));
        var rows = new List<(string, JsonObject)>();
        await foreach (var row in result.Rows)
        {
            rows.Add((row.Id, row.Doc));
        }

        return rows;
    }

    private async Task<int> CountAsync(string collection) => (await AllAsync(collection)).Count;

    private async Task<List<JsonObject>> OwnedAsync(string collection, string playerId) =>
        (await OwnedWithIdsAsync(collection, playerId)).Select(r => r.Content).ToList();

    /// <summary>Documents whose <c>playerId</c> is <paramref name="playerId"/>, through the collection's player index (RequestPlus).</summary>
    private async Task<List<(string Id, JsonObject Content)>> OwnedWithIdsAsync(string collection, string playerId)
    {
        var scope = await fixture.Connection.GetScopeAsync();
        var result = await scope.QueryAsync<ScanRow>(
            $"SELECT META(d).id AS id, d AS doc FROM `{collection}` AS d WHERE d.playerId = $player",
            new QueryOptions().Parameter("player", playerId).ScanConsistency(QueryScanConsistency.RequestPlus));
        var rows = new List<(string, JsonObject)>();
        await foreach (var row in result.Rows)
        {
            rows.Add((row.Id, row.Doc));
        }

        return rows;
    }

    private async Task<Footprint> MeasureAsync(string playerId)
    {
        var receipts = await OwnedWithIdsAsync(ReceiptDocument.Collection, playerId);
        var ledger = await OwnedAsync(EconomyOperations.LedgerCollection, playerId);
        var sums = string.Join(
            ",",
            ledger.GroupBy(d => d["payload"]!["resource"]!.GetValue<string>())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Sum(d => d["payload"]!["delta"]!.GetValue<long>())}"));
        return new Footprint(
            receipts.Count,
            ledger.Count,
            sums,
            (await OwnedAsync(GmModule.AuditCollection, playerId)).Count,
            (await OwnedAsync(Sample.Counter.CounterModuleDefinition.LogCollection, playerId)).Count,
            (await OwnedAsync(EconomyOperations.ClaimCollection, playerId)).Count,
            receipts.Select(r => r.Id).DefaultIfEmpty(string.Empty).Min(StringComparer.Ordinal)!);
    }

    /// <summary>The end state of a completed purge (US2-1..3, FR-014..FR-018, SC-002, SC-003); returns the anonymous id.</summary>
    private async Task<string> ShouldBePurgedAsync(CouchbaseGameHost host, Leaver leaver, Footprint before)
    {
        before.Receipts.ShouldBeGreaterThan(5);
        before.Ledger.ShouldBeGreaterThan(0);
        before.Audits.ShouldBe(1);
        before.CounterLog.ShouldBeGreaterThan(0);
        before.Claims.ShouldBe(1);

        // FR-018: the purge record keeps no anonymous id and no progress.
        var record = (await fixture.Store.GetAsync(AccountDeletionRecord.KeyOf(leaver.PlayerId), CancellationToken.None)).ShouldNotBeNull().Content;
        record["state"]!.GetValue<string>().ShouldBe("purged");
        record["purgedAtUtc"].ShouldNotBeNull();
        record.ContainsKey("anonymousId").ShouldBeFalse();
        record.ContainsKey("completedSteps").ShouldBeFalse();

        // One anonymous id across every retained record; counts and per-resource sums unchanged (SC-003).
        var anonymousId = (await GetAsync(host, new DocumentKey(ReceiptDocument.Collection, before.FirstReceipt))).ShouldNotBeNull()
            .Content["playerId"]!.GetValue<string>();
        anonymousId.ShouldMatch(AnonymousIdPattern);
        record.ToJsonString().ShouldNotContain(anonymousId);
        (await MeasureAsync(anonymousId)).ShouldBe(before);
        (await OwnedAsync(GmModule.AuditCollection, anonymousId)).Single()["payload"]!["targetPlayerId"]!.GetValue<string>().ShouldBe(anonymousId);
        var anonymized = await OwnedAsync(ReceiptDocument.Collection, anonymousId);
        anonymized.ShouldAllBe(r => r["resultCompact"]!.GetValue<string>() == string.Empty && r["bodyHash"]!.GetValue<string>() == string.Empty);
        anonymized.Single(r => r["operation"]!.GetValue<string>() == LinkIdentityHandler.Operation)["claimKeys"]!.AsArray()
            .Select(k => k!.GetValue<string>()).ShouldBe([$"{IdentityCollections.Identities}/redacted"]);

        // Every aggregate, identity and session gone (US2-1, US2-3).
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        foreach (var aggregate in registry.Aggregates.Values)
        {
            (await GetAsync(host, new DocumentKey(aggregate.Collection, leaver.PlayerId))).ShouldBeNull();
        }

        (await GetAsync(host, new DocumentKey(IdentityCollections.Identities, GuestCredentials.MappingKey(leaver.LocalId)))).ShouldBeNull();

        // SC-002: a scan of every collection finds the old id only in the purge record.
        var collections = registry.RequiredCollections().Where(c => c != PlatformCollections.AccountDeletions).ToArray();
        var scans = await Task.WhenAll(collections.Select(AllAsync));
        for (var i = 0; i < collections.Length; i++)
        {
            foreach (var (id, content) in scans[i])
            {
                id.ShouldNotContain(leaver.PlayerId);
                content.ToJsonString().ShouldNotContain(leaver.PlayerId, customMessage: $"{collections[i]}/{id} still holds the purged id");
            }
        }

        return anonymousId;
    }

    // ---------------------------------------------------------------- helpers: EXPLAIN

    private static Action<QueryOptions> Due() => o => o.Parameter("now", "2026-01-01T00:00:00.000Z").Parameter("limit", 50);

    private async Task<QueryPlan> ExplainAsync(string statement, Action<QueryOptions> parameters)
    {
        var options = new QueryOptions();
        parameters(options);
        var scope = await fixture.Connection.GetScopeAsync();
        var result = await scope.QueryAsync<JsonObject>("EXPLAIN " + statement, options);
        var operators = new List<string>();
        var indexes = new List<string>();
        await foreach (var row in result.Rows)
        {
            Collect(row, operators, indexes);
        }

        return new QueryPlan(statement, operators, indexes);
    }

    private static void Collect(JsonNode? node, List<string> operators, List<string> indexes)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["#operator"]?.GetValue<string>() is { } op)
                {
                    operators.Add(op);
                    if (op.StartsWith("IndexScan", StringComparison.Ordinal) || op.StartsWith("PrimaryScan", StringComparison.Ordinal))
                    {
                        indexes.Add(obj["index"]?.GetValue<string>() ?? "?");
                    }
                }

                foreach (var (_, child) in obj)
                {
                    Collect(child, operators, indexes);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Collect(child, operators, indexes);
                }

                break;
        }
    }

    // ---------------------------------------------------------------- helpers: lease probe job

    private static string NewJobName() => "it.probe-" + Guid.NewGuid().ToString("N")[..8];

    private Task<CouchbaseGameHost> StartProbeHostAsync(string job, JobProbe probe, string name, bool loop)
    {
        fixture.RequireCouchbase();
        return CouchbaseGameHost.StartAsync(
            fixture,
            Clock,
            s =>
            {
                s["GameServer:Jobs:Enabled"] = loop ? "true" : "false";
                s["GameServer:Jobs:LeaseSeconds"] = ShortLeaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                s["GameServer:Jobs:RenewSeconds"] = "1";
                s["GameServer:Jobs:PollSeconds"] = "5";
                s[$"GameServer:Jobs:Settings:{job}:IntervalSeconds"] = "10";
                s[$"GameServer:Jobs:Settings:{AccountPurgeJob.JobName}:Enabled"] = "false";
            },
            extraModules: b => b.AddModule(new ProbeJobModule(job)),
            services: s =>
            {
                s.AddSingleton(probe);
                s.AddSingleton(new HostTag(name));
            });
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }

    private sealed class ScanRow
    {
        public string Id { get; set; } = string.Empty;

        public JsonObject Doc { get; set; } = [];
    }

    public sealed record HostTag(string Name);

    public sealed record JobRun(string Host, long Token, bool RunnerOwnsLease);

    /// <summary>Shared by every host of a test: records runs and concurrency.</summary>
    public sealed class JobProbe
    {
        private int _running;
        private int _maxConcurrent;

        public ConcurrentQueue<JobRun> Runs { get; } = new();

        public Func<string, IJobContext, Task> Behavior { get; set; } = (_, _) => Task.CompletedTask;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public async Task RunAsync(string host, IJobContext context, bool runnerOwnsLease)
        {
            Runs.Enqueue(new JobRun(host, context.FencingToken, runnerOwnsLease));
            var running = Interlocked.Increment(ref _running);
            int max;
            while (running > (max = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, running, max) != max)
            {
            }

            try
            {
                await Behavior(host, context);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    public sealed class ProbeJob(JobProbe probe, HostTag host, LeaseJobRunner runner, IKeyValueStore store) : ILeaseJob
    {
        public async Task RunAsync(IJobContext context)
        {
            var lease = (await store.GetAsync(LeaseStore.KeyOf(context.JobName), context.Cancellation))!.Content;
            var owns = lease["owner"]?.GetValue<string>() == runner.Owner && lease["fencingToken"]!.GetValue<long>() == context.FencingToken;
            await probe.RunAsync(host.Name, context, owns);
        }
    }

    private sealed class ProbeJobModule(string jobName) : IGameModule
    {
        public ModuleDescriptor Describe() => new() { Name = "probejobs", Jobs = [new JobDescriptor(jobName, typeof(ProbeJob), 10)] };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    /// <summary>Operators and indexes of an EXPLAIN plan.</summary>
    private sealed record QueryPlan(string Statement, IReadOnlyList<string> Operators, IReadOnlyList<string> Indexes)
    {
        /// <summary>The plan sorts the index entries (an <c>Order</c> operator) instead of reading them in index order.</summary>
        public bool Sorts => Operators.Contains("Order");

        /// <summary>Only <paramref name="index"/> is scanned: no primary or sequential scan (PERF-07).</summary>
        public void ShouldUse(string index)
        {
            Operators.ShouldNotContain(o => o.StartsWith("PrimaryScan", StringComparison.Ordinal), customMessage: Statement);
            Indexes.ShouldBe([index], customMessage: Statement);
        }
    }
}
