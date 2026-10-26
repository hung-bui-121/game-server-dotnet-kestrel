using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using GameServer.Modules.Identity;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US4 of 002: login rate limits (SEC-13, ACC-52) and separate player/GM schemes (SEC-02, ACC-09).</summary>
public sealed class LoginLimitAndSchemeTests
{
    [Fact]
    public async Task Eleventh_login_per_minute_for_one_local_id_is_429()
    {
        await using var host = await TestHost.StartAsync();
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        for (var i = 0; i < 10; i++)
        {
            (await host.Client().PostLoginAsync(localId, secret)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await host.Client().PostLoginAsync(localId, secret);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await limited.ReadErrorAsync()).Code.ShouldBe("RATE_LIMITED");

        host.Clock.Advance(TimeSpan.FromSeconds(61));
        (await host.Client().PostLoginAsync(localId, secret)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sixty_failures_per_minute_from_one_ip_block_further_logins()
    {
        await using var host = await TestHost.StartAsync();
        // Spread failures so no localId exceeds its own 10/min limit first (creation login + at most 9 failures each).
        var victims = Enumerable.Range(0, 7).Select(_ => (Id: IdentityHttp.NewLocalId(), Secret: IdentityHttp.NewSecret())).ToArray();
        foreach (var victim in victims)
        {
            await host.Client().LoginAsync(victim.Id, victim.Secret);
        }

        for (var i = 0; i < 60; i++)
        {
            var target = victims[i % victims.Length];
            (await host.Client().PostLoginAsync(target.Id, IdentityHttp.NewSecret())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var blocked = await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Many_valid_players_behind_one_ip_are_not_blocked_but_raise_a_warning()
    {
        var warnings = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == IdentityMetrics.MeterName && instrument.Name == "login_ip_burst_warnings_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => warnings.Enqueue(value));
        listener.Start();
        await using var host = await TestHost.StartAsync(settings: s => s["Identity:LoginPerIpWarnPerMinute"] = "50");

        for (var i = 0; i < 100; i++)
        {
            (await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        warnings.Sum().ShouldBe(50);
    }

    [Fact]
    public async Task Player_token_is_rejected_on_gm_api_and_gm_identity_on_player_api()
    {
        await using var host = await TestHost.StartAsync();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var playerOnGm = await host.BearerClient(login.SessionToken).GetAsync($"/gm/v1/counter/{login.PlayerId}");
        playerOnGm.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var gmOnPlayer = await host.GmClient("staff", "counter.read").PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());
        gmOnPlayer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
