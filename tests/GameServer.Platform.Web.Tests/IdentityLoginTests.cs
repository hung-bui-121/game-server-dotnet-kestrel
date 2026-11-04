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

    public async ValueTask InitializeAsync() => _host = await SampleTestHost.StartAsync();

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
        await using var host = await SampleTestHost.StartAsync(services: s => s.AddSingleton<IGateEvaluator, MaintenanceGate>());

        var response = await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadErrorAsync()).Code.ShouldBe("MAINTENANCE");
    }

    private sealed class MaintenanceGate : IGateEvaluator
    {
        public ValueTask<GateResult> EvaluateAsync(HttpContext context) => ValueTask.FromResult(new GateResult(GateDecision.Maintenance));
    }
}

/// <summary>
/// Slice 016 T039 (FR-003, FR-023, US3-2, US3-6, BC-3, wire-changes §2): the login sections are every client-visible section; a
/// broken non-profile aggregate is listed and the login still succeeds (epoch + 1 once); a profile that cannot be loaded fails the
/// login with its typed code and writes nothing, while a profile whose mapper throws is listed like any section.
/// </summary>
public sealed class IdentityLoginDegradationTests
{
    private const string ProfileMapperFails = "mapper fails";

    [Fact]
    public async Task The_login_sections_are_every_client_visible_section()
    {
        await using var host = await BrokenAggregateHost.StartAsync();

        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var visible = host.Services.GetRequiredService<GameServer.Platform.Registry.ModuleRegistry>().Aggregates.Values
            .Where(a => a.ClientVisible).Select(a => a.AggregateId).Order(StringComparer.Ordinal).ToArray();
        login.Sections.Select(s => s.Id).ShouldBe(visible);
        login.UnavailableSections.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_broken_aggregate_is_listed_and_the_login_succeeds_with_one_epoch_step()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var first = await host.Client().LoginAsync(localId, secret);
        BrokenDocuments.Seed(host.Store, BrokenReason.VersionAhead, first.PlayerId);

        var login = await host.Client().LoginAsync(localId, secret);

        login.Sections.Select(s => s.Id).ShouldNotContain(StartingStateAggregates.Starter);
        login.UnavailableSections.ShouldBe([new UnavailableSection { Id = StartingStateAggregates.Starter, Code = "AGGREGATE_VERSION_AHEAD", CodeNumber = 24 }]);
        StoredEpoch(host, login.PlayerId).ShouldBe(2);
        var snapshot = await host.BearerClient(login.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());
        snapshot.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_profile_whose_mapper_throws_is_listed_like_any_section()
    {
        await using var host = await BrokenAggregateHost.StartAsync(modules: b => b.ConfigureExtensions(e =>
            e.ReplaceWireMapper<GameServer.Modules.Identity.ProfileState, GameServer.Modules.Identity.Protocol.V1.ProfileState>(
                "profile",
                state => state.DisplayName == ProfileMapperFails
                    ? throw new InvalidOperationException("profile mapper failed (test marker).")
                    : new GameServer.Modules.Identity.Protocol.V1.ProfileState { DisplayName = state.DisplayName, SessionEpoch = state.SessionEpoch },
                o => o.Hidden("Ban", "Deleted", "Identities"))));
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var first = await host.Client().LoginAsync(localId, secret);
        var key = ProfileKey(first.PlayerId);
        var profile = host.Store.Peek(key)!;
        profile["state"]!["displayName"] = ProfileMapperFails;
        host.Store.Seed(key, profile);

        var login = await host.Client().LoginAsync(localId, secret);

        login.Sections.Select(s => s.Id).ShouldNotContain("profile");
        login.UnavailableSections.ShouldBe([new UnavailableSection { Id = "profile", Code = "AGGREGATE_UNREADABLE", CodeNumber = 25 }]);
        StoredEpoch(host, login.PlayerId).ShouldBe(2);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, "AGGREGATE_VERSION_AHEAD")]
    [InlineData(false, HttpStatusCode.InternalServerError, "AGGREGATE_UNREADABLE")]
    public async Task A_profile_that_cannot_be_loaded_fails_the_login_typed_and_writes_nothing(bool versionAhead, HttpStatusCode status, string code)
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var first = await host.Client().LoginAsync(localId, secret);
        var key = ProfileKey(first.PlayerId);
        var profile = host.Store.Peek(key)!;
        if (versionAhead)
        {
            profile["schemaVersion"] = GameServer.Modules.Identity.IdentityAggregates.ProfileDescriptor.SchemaVersion + 1;
        }
        else
        {
            profile["state"] = new System.Text.Json.Nodes.JsonArray();
        }

        host.Store.Seed(key, profile);
        var before = host.Store.Peek(key)!.ToJsonString();

        var refused = await host.Client().PostLoginAsync(localId, secret);

        refused.StatusCode.ShouldBe(status);
        var error = await refused.ReadErrorAsync();
        error.Code.ShouldBe(code);
        error.RetryAfterMs.ShouldBe(versionAhead ? 1000 : 0);
        error.Details.ShouldBe(new Dictionary<string, string> { ["aggregate"] = "profile" });
        host.Store.Peek(key)!.ToJsonString().ShouldBe(before);
        host.LogEntries.Count(e => e.Category == GameServer.Platform.Aggregates.AggregateReadFailures.LogCategory).ShouldBe(1);
        host.LogEntries.Lines(typeof(ExceptionMiddleware).FullName!).ShouldNotContain(l => l.Contains(code, StringComparison.Ordinal));
    }

    private static GameServer.Platform.Persistence.DocumentKey ProfileKey(string playerId) => new("pa_profile", playerId);

    private static long StoredEpoch(TestHost host, string playerId) => host.Store.Peek(ProfileKey(playerId))!["state"]!["sessionEpoch"]!.GetValue<long>();
}
