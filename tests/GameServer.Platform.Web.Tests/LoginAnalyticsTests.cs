using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Nodes;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Analytics;
using GameServer.Platform.Jobs;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Protocol.V1;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using ProfileState = GameServer.Modules.Identity.ProfileState;

namespace GameServer.Platform.Web.Tests;

/// <summary>Login and resume records (T032, T033, US2, FR-015..FR-019, SC-002).</summary>
public sealed class LoginAnalyticsTests
{
    private const string Zone = "Asia/Ho_Chi_Minh";

    private static readonly string[] LoginKeys =
    [
        "event", "schemaVersion", "eventId", "gameId", "environmentId", "userID", "activeTime", "activeTimeUtc", "timeZone", "utcOffset",
        "source", "provider", "isNewUser",
    ];

    private static readonly string[] ResumeKeys = LoginKeys.Where(k => k != "provider").ToArray();

    /// <summary>2026-10-30T16:00Z = 23:00 local in Ho Chi Minh (local midnight = 17:00Z).</summary>
    private static readonly DateTimeOffset Evening = new(2026, 10, 30, 16, 0, 0, TimeSpan.Zero);

    private static Task<TestHost> StartAsync(Action<Dictionary<string, string?>>? settings = null, Action<IServiceCollection>? services = null) =>
        TestHost.StartAsync(
            settings: s =>
            {
                s["GameServer:Analytics:TimeZone"] = Zone;
                settings?.Invoke(s);
            },
            services: services);

    private static async Task<IReadOnlyList<JsonObject>> RecordsAsync(TestHost host, string source)
    {
        await host.FlushAnalyticsAsync();
        return host.Analytics.OfEvent("login").Where(r => r["source"]!.GetValue<string>() == source).ToArray();
    }

    private static async Task<IReadOnlyList<JsonObject>> RecordsAsync(TestHost host, string source, string playerId) =>
        (await RecordsAsync(host, source)).Where(r => r["userID"]!.GetValue<string>() == playerId).ToArray();

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static string NewPgsId() => "g" + Guid.NewGuid().ToString("N")[..20];

    private static Task<HttpResponseMessage> ProviderLoginAsync(HttpClient client, ProviderCredential credential, bool switchAccount = false) =>
        client.PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential, SwitchAccount = switchAccount });

    private static async Task<LoginResponse> ProviderLoginOkAsync(HttpClient client, ProviderCredential credential, bool switchAccount = false)
    {
        var response = await ProviderLoginAsync(client, credential, switchAccount);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadProtobufAsync<LoginResponse>();
    }

    private static Task<HttpResponseMessage> SnapshotAsync(TestHost host, string token) =>
        host.BearerClient(token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

    private static Task SetProfileAsync(TestHost host, string playerId, Func<ProfileState, ProfileState> mutate) =>
        host.Services.GetRequiredService<IPlayerAggregateWriter>().UpdateAsync(playerId, IdentityAggregates.Profile, mutate, CancellationToken.None);

    [Fact]
    public async Task Guest_provider_and_confirmed_switch_logins_each_emit_one_record()
    {
        await using var host = await StartAsync();
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();

        var first = await host.Client().LoginAsync(localId, secret);
        var second = await host.Client().LoginAsync(localId, secret);
        var pgsId = NewPgsId();
        var pgs = await ProviderLoginOkAsync(host.Client(), Pgs(pgsId));
        var gcId = NewPgsId();
        var gameCenter = await ProviderLoginOkAsync(host.Client(), new ProviderCredential
        {
            GameCenter = new GameServer.Modules.Identity.Protocol.V1.GameCenterCredential { PublicKeyUrl = FakeIdentityAdapter.Prefix, TeamPlayerId = "T:_" + gcId, GamePlayerId = "A:_" + gcId },
        });

        // Unconfirmed switch: SWITCH_ACCOUNT, no record; confirmed: one record for the target player.
        var refused = await ProviderLoginAsync(host.BearerClient(second.SessionToken), Pgs(pgsId));
        (await refused.ReadErrorAsync()).Code.ShouldBe("SWITCH_ACCOUNT");
        var switched = await ProviderLoginOkAsync(host.BearerClient(second.SessionToken), Pgs(pgsId), switchAccount: true);
        switched.PlayerId.ShouldBe(pgs.PlayerId);

        var guest = await RecordsAsync(host, "login", first.PlayerId);
        guest.Count.ShouldBe(2);
        guest.ShouldAllBe(r => r["provider"]!.GetValue<string>() == "guest");
        guest.Select(r => r["isNewUser"]!.GetValue<bool>()).ShouldBe([true, false]);
        guest[0].Select(p => p.Key).ShouldBe(LoginKeys);
        guest[0]["timeZone"]!.GetValue<string>().ShouldBe(Zone);
        var provider = await RecordsAsync(host, "login", pgs.PlayerId);
        provider.Select(r => (r["provider"]!.GetValue<string>(), r["isNewUser"]!.GetValue<bool>())).ShouldBe([("pgs", true), ("pgs", false)]);
        (await RecordsAsync(host, "login", gameCenter.PlayerId)).ShouldHaveSingleItem()["provider"]!.GetValue<string>().ShouldBe("gamecenter");
        (await RecordsAsync(host, "login")).Count.ShouldBe(5);
    }

    [Fact]
    public async Task Refused_and_failed_logins_and_identity_links_emit_nothing()
    {
        await using var host = await StartAsync();

        // Pending deletion and login ban.
        var deletedId = IdentityHttp.NewLocalId();
        var deletedSecret = IdentityHttp.NewSecret();
        var deleted = await host.Client().LoginAsync(deletedId, deletedSecret);
        await SetProfileAsync(host, deleted.PlayerId, p => p with { Deleted = new DeletionInfo { RequestedAtUtc = host.Clock.UtcNow, PurgeAfterUtc = host.Clock.UtcNow.AddDays(30) } });
        var bannedId = IdentityHttp.NewLocalId();
        var bannedSecret = IdentityHttp.NewSecret();
        var banned = await host.Client().LoginAsync(bannedId, bannedSecret);
        await SetProfileAsync(host, banned.PlayerId, p => p with { Ban = new BanInfo { Reason = "test" } });
        var before = (await RecordsAsync(host, "login")).Count;

        (await host.Client().PostLoginAsync(deletedId, deletedSecret)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await host.Client().PostLoginAsync(bannedId, bannedSecret)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await host.Client().PostLoginAsync(bannedId, IdentityHttp.NewSecret())).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await ProviderLoginAsync(host.Client(), new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = "forged" } })).StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // Identity link is a command, not a login (the bearer request is not a resume either: seen at login today).
        var linker = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        (await host.BearerClient(linker.SessionToken).PostProtobufAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = Pgs(NewPgsId()) }, ProtobufHttp.NewRequestId()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Rate limited: the 11th login of one local id within a minute.
        var limitedId = IdentityHttp.NewLocalId();
        var limitedSecret = IdentityHttp.NewSecret();
        for (var i = 0; i < 10; i++)
        {
            (await host.Client().PostLoginAsync(limitedId, limitedSecret)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await host.Client().PostLoginAsync(limitedId, limitedSecret)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        (await RecordsAsync(host, "login")).Count.ShouldBe(before + 1 + 10);
        (await RecordsAsync(host, "resume")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_or_full_analytics_queue_never_fails_a_login()
    {
        var gate = new GateSink();
        await using var host = await StartAsync(s => s["GameServer:Analytics:QueueCapacity"] = "100", s => s.AddSingleton<IAnalyticsSink>(gate));
        var pipeline = host.Services.GetRequiredService<AnalyticsPipeline>();

        host.Analytics.FailWrites = true;
        (await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        host.Analytics.FailWrites = false;
        for (var i = 0; i < 110; i++)
        {
            (await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        pipeline.Status.Dropped.ShouldBeGreaterThanOrEqualTo(10);
        gate.Open();
        await host.FlushAnalyticsAsync();
        pipeline.Status.Dropped.ShouldBeGreaterThanOrEqualTo(11);
        pipeline.Status.SinkErrors.ShouldBe(1);
    }

    [Fact]
    public async Task A_stored_session_resumes_once_per_local_day_and_the_boundary_is_local_midnight()
    {
        await using var host = await StartAsync();
        host.Clock.Set(Evening.AddHours(-6)); // 17:00 local, Oct 30
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        // Same local day as the login: no resume (US2-4).
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Clock.Set(new DateTimeOffset(2026, 10, 30, 16, 59, 59, 999, TimeSpan.Zero));
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RecordsAsync(host, "resume")).ShouldBeEmpty();

        // 17:00:00.000Z = local midnight: first request of Oct 31 resumes, the next does not.
        host.Clock.Set(new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero));
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // UTC midnight is not a boundary (still Oct 31 locally).
        host.Clock.Set(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Next local day.
        host.Clock.Set(new DateTimeOffset(2026, 10, 31, 17, 0, 0, 1, TimeSpan.Zero));
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var resumes = await RecordsAsync(host, "resume", login.PlayerId);
        resumes.Select(r => r["activeTime"]!.GetValue<string>()).ShouldBe(["2026-10-31T00:00:00.000+07:00", "2026-11-01T00:00:00.001+07:00"]);
        resumes[0]["activeTimeUtc"]!.GetValue<string>().ShouldBe("2026-10-30T17:00:00.000Z");
        resumes[0].Select(p => p.Key).ShouldBe(ResumeKeys);
        resumes.ShouldAllBe(r => !r["isNewUser"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Expired_replaced_deleted_suspended_and_purged_sessions_never_resume()
    {
        await using var host = await StartAsync();
        host.Clock.Set(Evening);
        var replaced = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var deleted = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var suspended = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var expired = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var purgedLogin = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        (await host.BearerClient(purgedLogin.SessionToken).PostProtobufAsync(IdentityModule.DeleteAccountRoute, new RequestAccountDeletionRequest(), ProtobufHttp.NewRequestId()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        host.Clock.Advance(TimeSpan.FromDays(1));
        await SetProfileAsync(host, replaced.PlayerId, p => p with { SessionEpoch = p.SessionEpoch + 1 });
        await SetProfileAsync(host, deleted.PlayerId, p => p with { Deleted = new DeletionInfo { RequestedAtUtc = host.Clock.UtcNow, PurgeAfterUtc = host.Clock.UtcNow.AddDays(30) } });
        await SetProfileAsync(host, suspended.PlayerId, p => p with { Ban = new BanInfo { Reason = "test" } });
        (await SnapshotAsync(host, replaced.SessionToken)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await SnapshotAsync(host, deleted.SessionToken)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await SnapshotAsync(host, suspended.SessionToken)).StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // Sliding session (30 days) expired; the purged account's session is refused.
        host.Clock.Advance(TimeSpan.FromDays(31));
        (await host.Services.GetRequiredService<LeaseJobRunner>().RunOnceAsync(AccountPurgeJob.JobName, CancellationToken.None)).ShouldBe(JobRunResult.Completed);
        (await SnapshotAsync(host, expired.SessionToken)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await SnapshotAsync(host, purgedLogin.SessionToken)).StatusCode.ShouldNotBe(HttpStatusCode.OK);

        (await RecordsAsync(host, "resume")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bearer_request_refused_after_authentication_still_counts_as_a_resume()
    {
        await using var host = await StartAsync();
        host.Clock.Set(Evening);
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await SetProfileAsync(host, login.PlayerId, p => p with { Ban = new BanInfo { Scope = ["counter"], UntilUtc = null } });
        host.Clock.Advance(TimeSpan.FromDays(1));

        var command = await host.BearerClient(login.SessionToken).PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());

        command.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RecordsAsync(host, "resume", login.PlayerId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Test_scheme_gm_and_public_requests_never_resume()
    {
        await using var host = await StartAsync();
        host.Clock.Set(Evening);

        (await host.PlayerClient("p_test_scheme").PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var gm = new HttpRequestMessage(HttpMethod.Post, IdentityModule.CancelDeletionRoute.Replace("{playerId}", "p_test_scheme", StringComparison.Ordinal))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { reason = "test" }),
        };
        gm.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        (await host.GmClient("admin", IdentityPermissions.PlayerBan).SendAsync(gm)).StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
        (await host.Client().GetAsync("/v1/time")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RecordsAsync(host, "resume")).ShouldBeEmpty();
        (await RecordsAsync(host, "login")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_instances_resume_at_most_once_each_and_a_restart_at_most_once_more()
    {
        await using var a = await StartAsync();
        var clock = a.Clock;
        clock.Set(Evening);
        var login = await a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        clock.Advance(TimeSpan.FromDays(1));
        await using (var b = await TestHost.StartAsync(settings: s => s["GameServer:Analytics:TimeZone"] = Zone, store: a.Store, clock: clock))
        {
            for (var i = 0; i < 3; i++)
            {
                (await SnapshotAsync(a, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
                (await SnapshotAsync(b, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            (await RecordsAsync(a, "resume", login.PlayerId)).Count.ShouldBe(1);
            (await RecordsAsync(b, "resume", login.PlayerId)).Count.ShouldBe(1);
        }

        // "Restart": a new instance on the same day forgot the set.
        await using var restarted = await TestHost.StartAsync(settings: s => s["GameServer:Analytics:TimeZone"] = Zone, store: a.Store, clock: clock);
        (await SnapshotAsync(restarted, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SnapshotAsync(restarted, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RecordsAsync(restarted, "resume", login.PlayerId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Inactive_analytics_emits_nothing_and_tracks_nobody()
    {
        await using var host = await StartAsync(s => s["GameServer:Analytics:Enabled"] = "false");
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        host.Analytics.Lines.ShouldBeEmpty();
        host.Services.GetRequiredService<LoginAnalytics>().Seen.ShouldBeNull();
    }

    [Fact]
    public void The_daily_set_clears_at_capacity_counts_the_reset_and_replaces_itself_on_a_new_day()
    {
        var resets = 0;
        var set = new DailyPlayerSet(2, () => resets++);
        var day = new DateOnly(2026, 10, 31);

        set.TryMark("p1", day).ShouldBeTrue();
        set.TryMark("p1", day).ShouldBeFalse();
        set.TryMark("p2", day).ShouldBeTrue();
        set.TryMark("p2", day).ShouldBeFalse();

        // Full: cleared, counted, at most one extra resume per player.
        set.TryMark("p3", day).ShouldBeTrue();
        resets.ShouldBe(1);
        set.Count.ShouldBe(1);
        set.TryMark("p1", day).ShouldBeTrue();
        set.TryMark("p1", day).ShouldBeFalse();

        // Newer day: a new set; an older day is never resumed.
        set.TryMark("p1", day.AddDays(1)).ShouldBeTrue();
        set.Count.ShouldBe(1);
        set.TryMark("p2", day).ShouldBeFalse();
        resets.ShouldBe(1);
    }

    [Fact]
    public void Parallel_adds_keep_the_count_exact_and_seen_players_never_reach_the_capacity_path()
    {
        const int Threads = 16;
        const int PlayersPerThread = 5_000;
        const int Capacity = Threads * PlayersPerThread;
        var resets = 0;
        var set = new DailyPlayerSet(Capacity, () => Interlocked.Increment(ref resets));
        var day = new DateOnly(2026, 10, 31);
        var marked = 0;

        // Every player is offered by two threads at once: exactly one TryMark wins, the count is the number of distinct players.
        Parallel.For(0, Threads * 2, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
        {
            var lane = worker % Threads;
            for (var i = 0; i < PlayersPerThread; i++)
            {
                if (set.TryMark($"p{lane}-{i}", day))
                {
                    Interlocked.Increment(ref marked);
                }
            }
        });

        marked.ShouldBe(Capacity);
        set.Count.ShouldBe(Capacity);
        resets.ShouldBe(0);

        // Full set: already-seen players return on the lookup, before the capacity check, so they never clear it.
        Parallel.For(0, Threads, lane =>
        {
            for (var i = 0; i < PlayersPerThread; i++)
            {
                set.TryMark($"p{lane}-{i}", day).ShouldBeFalse();
            }
        });

        resets.ShouldBe(0);
        set.Count.ShouldBe(Capacity);

        // Only an unseen player at capacity clears the set, once.
        set.TryMark("new", day).ShouldBeTrue();
        resets.ShouldBe(1);
        set.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_tracker_reset_metric_is_exported_on_the_identity_meter()
    {
        await using var host = await StartAsync(s => s["GameServer:Analytics:ResumeTrackerCapacity"] = "1000");
        var values = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "login_resume_tracker_resets_total" && instrument.Meter.Name == IdentityMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Enqueue(value));
        listener.Start();

        var analytics = host.Services.GetRequiredService<LoginAnalytics>();
        for (var i = 0; i <= 1000; i++)
        {
            analytics.Authenticated("p" + i);
        }

        values.Sum().ShouldBeGreaterThanOrEqualTo(1);
        analytics.Seen!.Count.ShouldBe(1);
    }

    /// <summary>Blocks every write until <see cref="Open"/>.</summary>
    private sealed class GateSink : IAnalyticsSink
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "gate";

        public Task Entered => _entered.Task;

        public void Open() => _open.TrySetResult();

        public async ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _open.Task.WaitAsync(cancellationToken);
        }
    }
}
