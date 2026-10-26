using System.Net;
using GameServer.Modules.Identity;
using GameServer.Platform.Persistence;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US2 of 002: single session + takeover (ACC-20, IDN-08, IDN-10).</summary>
public sealed class SessionTests
{
    private static Task<HttpResponseMessage> SnapshotAsync(TestHost host, string token) =>
        host.BearerClient(token).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

    private static async Task ShouldBeRejectedWith(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe(code);
        error.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task New_login_replaces_the_previous_session()
    {
        await using var host = await TestHost.StartAsync();
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var first = await host.Client().LoginAsync(localId, secret);
        var second = await host.Client().LoginAsync(localId, secret);

        await ShouldBeRejectedWith(await SnapshotAsync(host, first.SessionToken), HttpStatusCode.Unauthorized, "SESSION_REPLACED");
        (await SnapshotAsync(host, second.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Takeover_on_another_instance_reaches_every_instance_within_the_cache_window()
    {
        var clock = new GameServer.Platform.Testing.FakeClock();
        var store = new GameServer.Platform.Testing.InMemoryDocumentStore(clock);
        void Cache60(Dictionary<string, string?> s) => s["Identity:CacheSeconds"] = "60";
        await using var instanceA = await TestHost.StartAsync(settings: Cache60, store: store, clock: clock);
        await using var instanceB = await TestHost.StartAsync(settings: Cache60, store: store, clock: clock);
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();

        var deviceA = await instanceA.Client().LoginAsync(localId, secret);
        (await SnapshotAsync(instanceA, deviceA.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SnapshotAsync(instanceB, deviceA.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var deviceB = await instanceB.Client().LoginAsync(localId, secret);

        // Instance B learned the new epoch at login; instance A may serve the old session until its cache expires.
        await ShouldBeRejectedWith(await SnapshotAsync(instanceB, deviceA.SessionToken), HttpStatusCode.Unauthorized, "SESSION_REPLACED");
        clock.Advance(TimeSpan.FromSeconds(61));
        await ShouldBeRejectedWith(await SnapshotAsync(instanceA, deviceA.SessionToken), HttpStatusCode.Unauthorized, "SESSION_REPLACED");
        (await SnapshotAsync(instanceA, deviceB.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Idle_session_expires_after_thirty_days()
    {
        await using var host = await TestHost.StartAsync();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        host.Clock.Advance(TimeSpan.FromDays(29));
        (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Clock.Advance(TimeSpan.FromDays(31));

        await ShouldBeRejectedWith(await SnapshotAsync(host, login.SessionToken), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Active_session_still_ends_after_ninety_days()
    {
        await using var host = await TestHost.StartAsync();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        for (var day = 0; day < 4; day++)
        {
            host.Clock.Advance(TimeSpan.FromDays(20));
            (await SnapshotAsync(host, login.SessionToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        host.Clock.Advance(TimeSpan.FromDays(11));
        await ShouldBeRejectedWith(await SnapshotAsync(host, login.SessionToken), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Use_after_a_day_touches_the_session_lazily()
    {
        await using var host = await TestHost.StartAsync();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var key = new DocumentKey(SessionService.Collection, SessionService.KeyOf(login.SessionToken));
        var created = host.Store.Peek(key)!["lastTouchAtUtc"]!.GetValue<string>();

        host.Clock.Advance(TimeSpan.FromHours(2));
        await SnapshotAsync(host, login.SessionToken);
        host.Store.Peek(key)!["lastTouchAtUtc"]!.GetValue<string>().ShouldBe(created);

        host.Clock.Advance(TimeSpan.FromHours(23));
        await SnapshotAsync(host, login.SessionToken);
        host.Store.Peek(key)!["lastTouchAtUtc"]!.GetValue<string>().ShouldNotBe(created);
    }

    [Fact]
    public async Task Token_is_only_accepted_from_the_authorization_header()
    {
        await using var host = await TestHost.StartAsync();
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var client = host.Client();
        client.DefaultRequestHeaders.Add("X-Session-Token", login.SessionToken);

        var response = await client.PostProtobufAsync($"/api/v1/player/snapshot?token={login.SessionToken}", new SnapshotRequest());

        await ShouldBeRejectedWith(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }
}
