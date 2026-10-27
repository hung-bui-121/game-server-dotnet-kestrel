using System.Net;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Web.Middleware;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US1 of 002: guest login (ACC-01, ACC-02, IDN-01, IDN-02, IDN-07).</summary>
public sealed class IdentityLoginTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task New_guest_gets_player_session_and_default_snapshot()
    {
        var login = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        login.PlayerId.ShouldMatch("^p_[0-9a-z]{26}$");
        login.SessionToken.Length.ShouldBeGreaterThanOrEqualTo(43);
        login.ConfigVersion.ShouldStartWith("test-1+");
        login.ServerTimeUtcMs.ShouldBe(_host.Clock.UtcNow.ToUnixTimeMilliseconds());
        login.SessionExpiresAtUtcMs.ShouldBe(_host.Clock.UtcNow.AddDays(30).ToUnixTimeMilliseconds());
        login.Sections.Select(s => s.Id).ShouldBe(["counter", "profile"]);
        var profile = ProfileState.Parser.ParseFrom(login.Sections.Single(s => s.Id == "profile").Payload);
        profile.SessionEpoch.ShouldBe(1);
        login.Sections.Single(s => s.Id == "counter").Rev.ShouldBe(0);
    }

    [Fact]
    public async Task Same_credential_returns_the_same_player_with_its_data()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var first = await _host.Client().LoginAsync(localId, secret);
        await _host.BearerClient(first.SessionToken).PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 7 }, ProtobufHttp.NewRequestId());

        var second = await _host.Client().LoginAsync(localId, secret);

        second.PlayerId.ShouldBe(first.PlayerId);
        CounterState.Parser.ParseFrom(second.Sections.Single(s => s.Id == "counter").Payload).Value.ShouldBe(7);
    }

    [Fact]
    public async Task Wrong_secret_is_401_without_revealing_the_account()
    {
        var localId = IdentityHttp.NewLocalId();
        await _host.Client().LoginAsync(localId, IdentityHttp.NewSecret());

        var wrong = await _host.Client().PostLoginAsync(localId, IdentityHttp.NewSecret());
        var garbageToken = await _host.BearerClient("not-a-real-token").PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var error = await wrong.ReadErrorAsync();
        error.Code.ShouldBe("UNAUTHENTICATED");
        error.Message.ShouldBe((await garbageToken.ReadErrorAsync()).Message);
    }

    [Fact]
    public async Task Concurrent_first_logins_create_one_player()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();

        var logins = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _host.Client().LoginAsync(localId, secret)));

        logins.Select(l => l.PlayerId).Distinct().Count().ShouldBe(1);
        _host.Store.Count("identities").ShouldBe(1);
    }

    [Fact]
    public async Task Logout_revokes_the_token_and_relogin_keeps_the_player()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var login = await _host.Client().LoginAsync(localId, secret);
        var player = _host.BearerClient(login.SessionToken);

        (await player.PostProtobufAsync("/api/v1/identity/logout", new LogoutRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await player.PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _host.Client().LoginAsync(localId, secret)).PlayerId.ShouldBe(login.PlayerId);
    }

    [Theory]
    [InlineData("", 32)]
    [InlineData("has space", 32)]
    [InlineData("ok-id", 16)]
    public async Task Invalid_credential_format_is_400(string localId, int secretBytes)
    {
        var response = await _host.Client().PostLoginAsync(localId, new byte[secretBytes]);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _host.Store.Count("identities").ShouldBe(0);
    }

    [Fact]
    public async Task Bearer_actor_only_reaches_its_own_data()
    {
        var victim = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await _host.BearerClient(victim.SessionToken).PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 9 }, ProtobufHttp.NewRequestId());
        var attacker = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var snapshot = await (await _host.BearerClient(attacker.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest()))
            .ReadProtobufAsync<SnapshotResponse>();

        CounterState.Parser.ParseFrom(snapshot.Sections.Single(s => s.Id == "counter").Payload).Value.ShouldBe(0);
    }

    [Fact]
    public async Task Secrets_and_tokens_never_reach_the_logs()
    {
        var secret = IdentityHttp.NewSecret();
        var login = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), secret);
        await _host.BearerClient(login.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

        var logs = string.Join("\n", _host.Logs);
        logs.ShouldNotContain(login.SessionToken);
        logs.ShouldNotContain(Convert.ToBase64String(secret));
        logs.ShouldNotContain(Convert.ToHexString(secret), Case.Insensitive);
    }

    [Fact]
    public async Task Login_goes_through_the_maintenance_gate()
    {
        await using var host = await TestHost.StartAsync(services: s => s.AddSingleton<IGateEvaluator, MaintenanceGate>());

        var response = await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadErrorAsync()).Code.ShouldBe("MAINTENANCE");
    }

    private sealed class MaintenanceGate : IGateEvaluator
    {
        public ValueTask<GateResult> EvaluateAsync(HttpContext context) => ValueTask.FromResult(new GateResult(GateDecision.Maintenance));
    }
}
