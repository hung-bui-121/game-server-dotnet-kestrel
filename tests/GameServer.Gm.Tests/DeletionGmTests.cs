using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Jobs;
using GameServer.Platform.Privacy;
using GameServer.Platform.Registry;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Security;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using ProfileState = GameServer.Modules.Identity.ProfileState;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 010 US3: GM sees a pending account deletion and cancels it while the grace period runs (PRIV-01, FR-011,
/// FR-012, GM-02, SEC-14); GM sees purging and purged players produced by the purge job, and a cancel racing the purge start
/// commits only one of them (US2-7, US3-6).
/// </summary>
public sealed class DeletionGmTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private InMemoryDocumentStore _store = null!;
    private TestHost _host = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new InMemoryDocumentStore(_clock);
        _host = await GmTestHost.StartAsync(store: _store, clock: _clock);
        _admin = await _host.GmClientAsync("admin");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private sealed record PendingPlayer(string PlayerId, string LocalId, byte[] Secret, string Pgs, string Token);

    private static string Cancel(string playerId) => IdentityModule.CancelDeletionRoute.Replace("{playerId}", playerId, StringComparison.Ordinal);

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static string Iso(DateTimeOffset time) => AggregateDocumentSerializer.FormatTime(time);

    private Task<HttpResponseMessage> RequestDeletionAsync(string token) =>
        _host.BearerClient(token).PostProtobufAsync(IdentityModule.DeleteAccountRoute, new RequestAccountDeletionRequest(), ProtobufHttp.NewRequestId());

    private Task<HttpResponseMessage> ProviderLoginAsync(string pgs) =>
        _host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = Pgs(pgs) });

    /// <summary>A player with a display name, a guest and a Play Games identity, who requested deletion.</summary>
    private async Task<PendingPlayer> PendingAsync(string displayName = "Leaving Player")
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var pgs = "g" + Guid.NewGuid().ToString("N")[..20];
        var login = await _host.Client().LoginAsync(localId, secret);
        var player = _host.BearerClient(login.SessionToken);
        (await (await player.PostProtobufAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = displayName }, ProtobufHttp.NewRequestId()))
            .ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        (await (await player.PostProtobufAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(pgs) }, ProtobufHttp.NewRequestId()))
            .ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        (await RequestDeletionAsync(login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return new PendingPlayer(login.PlayerId, localId, secret, pgs, login.SessionToken);
    }

    private Task<JobRunResult> RunPurgeAsync() =>
        _host.Services.GetRequiredService<LeaseJobRunner>().RunOnceAsync(AccountPurgeJob.JobName, CancellationToken.None);

    /// <summary>
    /// The purge start as an instance whose clock runs <paramref name="skew"/> ahead would decide it: without skew the time
    /// alone keeps cancel (now &lt; purgeAfter) and start (now &gt;= purgeAfter) apart; the profile write conflict covers skew.
    /// </summary>
    private Task<PurgeTicket?> BeginWithSkewAsync(string playerId, TimeSpan skew)
    {
        var skewedNow = _clock.UtcNow + skew;
        return _host.Services.GetRequiredService<IAccountPurgeWriter>().BeginAsync<ProfileState>(
            playerId,
            1,
            p => AccountPurgeStart.Decide(p, skewedNow) is { Deleted: { } deleted } marked
                ? new PurgeStart<ProfileState>(marked, deleted.RequestedAtUtc, deleted.PurgeAfterUtc)
                : null,
            CancellationToken.None);
    }

    /// <summary>Waits (bounded) until a transaction consumed the queued fault, i.e. it staged its writes and waits in its commit.</summary>
    private async Task WaitUntilFaultsConsumedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_store.Faults.Pending > 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the delayed commit was never reached");
            await Task.Delay(5);
        }
    }

    private ProfileState Profile(string playerId) =>
        (ProfileState)_host.Services.GetRequiredService<PlayerSnapshotReader>()
            .ReadOneAsync(playerId, IdentityAggregates.Profile, CancellationToken.None).GetAwaiter().GetResult().State;

    private JsonObject[] Audits(string playerId) =>
        _store.All(GmModule.AuditCollection).Select(d => d.Content)
            .Where(d => d["payload"]?["action"]?.GetValue<string>() == CancelDeletionHandler.Operation && d["playerId"]?.GetValue<string>() == playerId)
            .ToArray();

    private static void ShouldBePending(JsonNode view, DateTimeOffset requestedAt)
    {
        var deletion = view["deletion"]!;
        deletion["state"]!.GetValue<string>().ShouldBe("pending");
        deletion["requestedAtUtc"]!.GetValue<string>().ShouldBe(Iso(requestedAt));
        deletion["purgeAfterUtc"]!.GetValue<string>().ShouldBe(Iso(requestedAt.AddDays(30)));
        deletion["purgeStartedAtUtc"].ShouldBeNull();
        deletion["purgedAtUtc"].ShouldBeNull();
    }

    [Fact]
    public async Task Gm_sees_a_pending_deletion_by_id_mapping_key_and_display_name()
    {
        var requestedAt = _clock.UtcNow;
        var pending = await PendingAsync("Pending Pat");
        var other = await _host.NewPlayerAsync();

        ShouldBePending(await _admin.GetAsync($"/gm/v1/players/{pending.PlayerId}").ShouldBeAsync(200), requestedAt);
        var byMapping = await _admin.GetAsync($"/gm/v1/players/by-mapping/guest:{pending.LocalId}").ShouldBeAsync(200);
        byMapping["playerId"]!.GetValue<string>().ShouldBe(pending.PlayerId);
        ShouldBePending(byMapping, requestedAt);
        var search = await _admin.GetAsync("/gm/v1/players/search?displayName=Pending%20Pat&pageSize=10").ShouldBeAsync(200);
        search["items"]!.AsArray().Select(i => i!["playerId"]!.GetValue<string>()).ShouldContain(pending.PlayerId);

        (await _admin.GetAsync($"/gm/v1/players/{other.PlayerId}").ShouldBeAsync(200))["deletion"].ShouldBeNull();
    }

    [Fact]
    public async Task Cancel_restores_access_with_one_audit_while_old_sessions_stay_revoked()
    {
        var pending = await PendingAsync("Coming Back");
        var before = Profile(pending.PlayerId).Deleted!;

        var response = await _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "player changed their mind" }).ShouldBeAsync(200);

        response["code"]!.GetValue<string>().ShouldBe("OK");
        response["result"]!["before"]!["purgeAfterUtc"].ShouldNotBeNull();
        Profile(pending.PlayerId).Deleted.ShouldBeNull();
        (await _admin.GetAsync($"/gm/v1/players/{pending.PlayerId}").ShouldBeAsync(200))["deletion"].ShouldBeNull();

        // SEC-14: actor, permission/action, target, reason, before/after, UTC time, correlation.
        var audit = Audits(pending.PlayerId).ShouldHaveSingleItem();
        audit["actorId"]!.GetValue<string>().ShouldBe("admin");
        audit["payload"]!["permission"]!.GetValue<string>().ShouldBe(IdentityPermissions.PlayerBan);
        audit["payload"]!["action"]!.GetValue<string>().ShouldBe(CancelDeletionHandler.Operation);
        audit["payload"]!["targetPlayerId"]!.GetValue<string>().ShouldBe(pending.PlayerId);
        audit["payload"]!["reason"]!.GetValue<string>().ShouldBe("player changed their mind");
        DateTimeOffset.Parse(audit["payload"]!["before"]!["purgeAfterUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBe(before.PurgeAfterUtc);
        audit["payload"]!["after"].ShouldBeNull();
        audit["receiptId"]!.GetValue<string>().ShouldBe(response["receiptId"]!.GetValue<string>());
        DateTimeOffset.Parse(audit["committedAtUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBe(_clock.UtcNow);
        audit["payload"]!["correlationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        // The request revoked every session; cancelling does not bring them back (US3-3).
        var stale = await _host.BearerClient(pending.Token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());
        stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stale.ReadErrorAsync()).Code.ShouldBe("SESSION_REPLACED");

        // Same credentials, same player, data intact.
        var guest = await _host.Client().LoginAsync(pending.LocalId, pending.Secret);
        guest.PlayerId.ShouldBe(pending.PlayerId);
        var provider = await ProviderLoginAsync(pending.Pgs);
        provider.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await provider.ReadProtobufAsync<LoginResponse>()).PlayerId.ShouldBe(pending.PlayerId);
        Profile(pending.PlayerId).DisplayName.ShouldBe("Coming Back");
    }

    [Fact]
    public async Task Cancel_needs_player_ban_and_a_csrf_token()
    {
        var pending = await PendingAsync();
        var support = await _host.GmClientAsync("support");
        await support.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "x" }).ShouldBeAsync(403);

        var admin = await _host.GmClientAsync("admin");
        admin.DefaultRequestHeaders.Remove(CsrfOptions.HeaderName);
        (await admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "x" }).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");

        await _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "" }).ShouldBeAsync(400);
        Profile(pending.PlayerId).Deleted.ShouldNotBeNull();
        Audits(pending.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_without_a_pending_deletion_or_of_an_unknown_player_is_refused()
    {
        var player = await _host.NewPlayerAsync();

        (await _admin.PostCommandAsync(Cancel(player.PlayerId), new { reason = "x" }).ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_NOT_PENDING");
        await _admin.PostCommandAsync(Cancel("p_unknown0000000000000000000"), new { reason = "x" }).ShouldBeAsync(404);
        Audits(player.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_after_the_grace_period_or_once_the_purge_started_is_refused()
    {
        var late = await PendingAsync();
        var started = await PendingAsync();
        await _host.Services.GetRequiredService<IPlayerAggregateWriter>().UpdateAsync<ProfileState>(
            started.PlayerId,
            IdentityAggregates.Profile,
            p => p with { Deleted = p.Deleted! with { PurgeStartedAtUtc = _clock.UtcNow } },
            CancellationToken.None);

        (await _admin.PostCommandAsync(Cancel(started.PlayerId), new { reason = "too late" }).ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");
        (await _admin.GetAsync($"/gm/v1/players/{started.PlayerId}").ShouldBeAsync(200))["deletion"]!["state"]!.GetValue<string>().ShouldBe("purging");

        _clock.Advance(TimeSpan.FromDays(30));
        var admin = await _host.GmClientAsync("admin"); // GM sessions idle out on the fake clock.
        (await admin.PostCommandAsync(Cancel(late.PlayerId), new { reason = "too late" }).ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");
        Profile(late.PlayerId).Deleted.ShouldNotBeNull();
        Audits(late.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_request_after_a_cancel_starts_a_new_grace_period()
    {
        var pending = await PendingAsync();
        _clock.Advance(TimeSpan.FromDays(10));
        var admin = await _host.GmClientAsync("admin"); // GM sessions idle out on the fake clock.
        await admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "mistake" }).ShouldBeAsync(200);
        var login = await _host.Client().LoginAsync(pending.LocalId, pending.Secret);

        var again = await (await RequestDeletionAsync(login.SessionToken)).ReadProtobufAsync<CommandResponse>();

        again.Code.ShouldBe("OK");
        RequestAccountDeletionResult.Parser.ParseFrom(again.Result).PurgeAfterUtcMs.ShouldBe(_clock.UtcNow.AddDays(30).ToUnixTimeMilliseconds());
        Profile(pending.PlayerId).Deleted!.PurgeAfterUtc.ShouldBe(_clock.UtcNow.AddDays(30));
    }

    [Fact]
    public async Task Other_gm_mutations_of_a_pending_player_answer_not_found()
    {
        var pending = await PendingAsync();

        await _admin.PostCommandAsync($"/gm/v1/players/{pending.PlayerId}/ban", new { reason = "x" }).ShouldBeAsync(404);
        await _admin.PostCommandAsync(EconomyModule.AdjustRoute.Replace("{playerId}", pending.PlayerId, StringComparison.Ordinal), new { resource = "gold", delta = 5, reason = "x" })
            .ShouldBeAsync(404);
    }

    [Fact]
    public async Task A_retry_replays_and_a_changed_body_with_the_same_request_id_conflicts()
    {
        var pending = await PendingAsync();
        var requestId = Guid.NewGuid().ToString("D");

        var first = await _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "ticket 9" }, requestId).ShouldBeAsync(200);
        var replay = await _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "ticket 9" }, requestId).ShouldBeAsync(200);

        replay["replayed"]!.GetValue<bool>().ShouldBeTrue();
        replay["receiptId"]!.GetValue<string>().ShouldBe(first["receiptId"]!.GetValue<string>());
        (await _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "ticket 10" }, requestId).ShouldBeAsync(409))["code"]!.GetValue<string>()
            .ShouldBe("IDEMPOTENCY_PAYLOAD_MISMATCH");
        Audits(pending.PlayerId).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Gm_sees_purging_and_purged_players_of_the_purge_job_and_cannot_cancel_them()
    {
        var pending = await PendingAsync("Purge Me Soon");
        _clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        _store.Faults.OnlyWhenWritingCollection = IdentityCollections.Identities;
        _store.Faults.Enqueue(StoreFault.FailBeforeCommit);
        (await RunPurgeAsync()).ShouldBe(JobRunResult.Completed);
        _store.Faults.OnlyWhenWritingCollection = null;
        var startedAt = _clock.UtcNow;
        var admin = await _host.GmClientAsync("admin"); // GM sessions idle out on the fake clock.

        var purging = await admin.GetAsync($"/gm/v1/players/{pending.PlayerId}").ShouldBeAsync(200);
        purging["deletion"]!["state"]!.GetValue<string>().ShouldBe("purging");
        purging["deletion"]!["purgeStartedAtUtc"]!.GetValue<string>().ShouldBe(Iso(startedAt));
        purging["deletion"]!["purgedAtUtc"].ShouldBeNull();
        purging.ToJsonString().ShouldNotContain("anon_");
        (await admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "too late" }).ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");

        _clock.Advance(TimeSpan.FromMinutes(11)); // past the 10-minute retry backoff and the job interval
        (await RunPurgeAsync()).ShouldBe(JobRunResult.Completed);
        admin = await _host.GmClientAsync("admin");

        var purged = await admin.GetAsync($"/gm/v1/players/{pending.PlayerId}").ShouldBeAsync(200);
        purged["aggregates"]!.AsArray().ShouldBeEmpty();
        purged["deletion"]!["state"]!.GetValue<string>().ShouldBe("purged");
        purged["deletion"]!["purgeStartedAtUtc"]!.GetValue<string>().ShouldBe(Iso(startedAt));
        purged["deletion"]!["purgedAtUtc"]!.GetValue<string>().ShouldBe(Iso(_clock.UtcNow));
        purged.ToJsonString().ShouldNotContain("Purge Me Soon");
        purged.ToJsonString().ShouldNotContain("anon_");

        // No path from personal data to the old id any more (FR-012).
        await admin.GetAsync($"/gm/v1/players/by-mapping/guest:{pending.LocalId}").ShouldBeAsync(404);
        var search = await admin.GetAsync("/gm/v1/players/search?displayName=Purge%20Me%20Soon&pageSize=10").ShouldBeAsync(200);
        search["items"]!.AsArray().ShouldBeEmpty();
        (await admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "too late" }).ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");
        Audits(pending.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancel_committing_while_the_purge_start_waits_wins_and_the_start_sees_no_deletion()
    {
        var pending = await PendingAsync();
        _store.Faults.OnlyWhenWritingCollection = PlatformCollections.AccountDeletions;
        _store.Faults.CommitDelay = TimeSpan.FromMilliseconds(300);
        _store.Faults.Enqueue(StoreFault.DelayCommit);

        // The start stages profile + record and waits in its commit (in-memory transaction bodies run synchronously up to it).
        var begin = BeginWithSkewAsync(pending.PlayerId, TimeSpan.FromDays(31));
        _store.Faults.Pending.ShouldBe(0);
        await _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "race" }).ShouldBeAsync(200);

        (await begin).ShouldBeNull();
        Profile(pending.PlayerId).Deleted.ShouldBeNull();
        _store.Peek(AccountDeletionRecord.KeyOf(pending.PlayerId)).ShouldBeNull();
        Audits(pending.PlayerId).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_purge_start_committing_while_the_cancel_waits_wins_and_the_cancel_answers_grace_over()
    {
        var pending = await PendingAsync();
        _store.Faults.OnlyWhenWritingCollection = "receipts";
        _store.Faults.CommitDelay = TimeSpan.FromMilliseconds(300);
        _store.Faults.Enqueue(StoreFault.DelayCommit);

        var cancel = _admin.PostCommandAsync(Cancel(pending.PlayerId), new { reason = "race" });
        await WaitUntilFaultsConsumedAsync();
        var ticket = await BeginWithSkewAsync(pending.PlayerId, TimeSpan.FromDays(31));

        ticket.ShouldNotBeNull();
        (await cancel.ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");
        Profile(pending.PlayerId).Deleted!.PurgeStartedAtUtc.ShouldNotBeNull();
        AccountDeletionRecord.FromJson(_store.Peek(AccountDeletionRecord.KeyOf(pending.PlayerId))!).State.ShouldBe(AccountDeletionState.Purging);
        Audits(pending.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_purge_record_without_aggregates_is_shown_as_purged_and_cannot_be_cancelled()
    {
        // Seeded record (the purge itself is Phase 5): what GM sees once every aggregate of the player is gone (D7).
        const string purgedId = "p_purgedplayer000000000000000";
        var record = new AccountDeletionRecord
        {
            PlayerId = purgedId,
            GameId = "test",
            EnvironmentId = "dev",
            State = AccountDeletionState.Purged,
            RequestedAtUtc = _clock.UtcNow.AddDays(-31),
            PurgeAfterUtc = _clock.UtcNow.AddDays(-1),
            PurgeStartedAtUtc = _clock.UtcNow.AddHours(-1),
            PurgedAtUtc = _clock.UtcNow,
            FencingToken = 1,
        };
        _store.Seed(AccountDeletionRecord.KeyOf(purgedId), record.ToJson());

        var view = await _admin.GetAsync($"/gm/v1/players/{purgedId}").ShouldBeAsync(200);

        view["aggregates"]!.AsArray().ShouldBeEmpty();
        view["deletion"]!["state"]!.GetValue<string>().ShouldBe("purged");
        view["deletion"]!["purgedAtUtc"]!.GetValue<string>().ShouldBe(Iso(_clock.UtcNow));
        view.ToJsonString().ShouldNotContain("anon_");
        (await _admin.PostCommandAsync(Cancel(purgedId), new { reason = "x" }).ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("DELETION_GRACE_OVER");
        await _admin.PostCommandAsync($"/gm/v1/players/{purgedId}/ban", new { reason = "x" }).ShouldBeAsync(404);
        await _admin.GetAsync("/gm/v1/players/p_neverexisted00000000000000").ShouldBeAsync(404);
    }
}
