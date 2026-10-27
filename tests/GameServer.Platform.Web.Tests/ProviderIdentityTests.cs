using System.Net;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Persistence;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Protocol.V1;
using Shouldly;
using ProfileState = GameServer.Modules.Identity.ProfileState;
using LinkResult = GameServer.Modules.Identity.Protocol.V1.LinkIdentityResult;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 009: provider login, link, conflict, switch (IDN-03..06, IDN-11; ACC-01 provider, ACC-03, ACC-29) with the
/// Development fake adapters (<c>fake:{id}</c> Play Games codes) on two instances sharing one store.
/// </summary>
public sealed class ProviderIdentityTests : IAsyncLifetime
{
    private TestHost _a = null!;
    private TestHost _b = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await TestHost.StartAsync();
        _b = await TestHost.StartAsync(store: _a.Store, clock: _a.Clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static string NewPgsId() => "g" + Guid.NewGuid().ToString("N")[..20];

    private static Task<HttpResponseMessage> ProviderLoginAsync(HttpClient client, ProviderCredential credential, bool switchAccount = false) =>
        client.PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = credential, SwitchAccount = switchAccount });

    private static async Task<LoginResponse> ProviderLoginOkAsync(HttpClient client, ProviderCredential credential, bool switchAccount = false)
    {
        var response = await ProviderLoginAsync(client, credential, switchAccount);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, (await response.Content.ReadAsByteArrayAsync()).Length.ToString());
        return await response.ReadProtobufAsync<LoginResponse>();
    }

    private static Task<HttpResponseMessage> LinkAsync(HttpClient client, ProviderCredential credential, string? requestId = null) =>
        client.PostProtobufAsync(IdentityModule.LinkRoute, new LinkIdentityRequest { Credential = credential }, requestId ?? ProtobufHttp.NewRequestId());

    private ProfileState Profile(string playerId) =>
        _a.Services.GetRequiredService<ProfileCache>().ReadFreshAsync(playerId, CancellationToken.None).GetAwaiter().GetResult();

    private string? MappedPlayer(string mappingKey) =>
        _a.Store.All(IdentityCollections.Identities).Where(d => d.Key.Key == mappingKey).Select(d => d.Content["playerId"]?.GetValue<string>()).SingleOrDefault();

    [Fact]
    public async Task Linking_maps_the_identity_to_the_player_in_one_command()
    {
        var guest = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var id = NewPgsId();

        var response = await LinkAsync(_a.BearerClient(guest.SessionToken), Pgs(id));
        var command = await response.ReadProtobufAsync<CommandResponse>();

        command.Code.ShouldBe("OK");
        command.ReceiptId.ShouldNotBeNullOrEmpty();
        var result = LinkResult.Parser.ParseFrom(command.Result);
        result.Provider.ShouldBe("pgs");
        result.AlreadyLinked.ShouldBeFalse();
        MappedPlayer("pgs:" + id).ShouldBe(guest.PlayerId);
        Profile(guest.PlayerId).Identities.ShouldContain("pgs:" + id);
        command.Updates.Single().Id.ShouldBe("profile");

        // Same identity again: nothing to do.
        var again = await (await LinkAsync(_a.BearerClient(guest.SessionToken), Pgs(id))).ReadProtobufAsync<CommandResponse>();
        again.Code.ShouldBe("OK");
        LinkResult.Parser.ParseFrom(again.Result).AlreadyLinked.ShouldBeTrue();

        // A second Play Games account on the same player is refused (one per provider).
        var second = await (await LinkAsync(_a.BearerClient(guest.SessionToken), Pgs(NewPgsId()))).ReadProtobufAsync<CommandResponse>();
        second.Code.ShouldBe("PROVIDER_ALREADY_LINKED");
    }

    [Fact]
    public async Task Identity_of_another_player_is_a_conflict_and_changes_nothing()
    {
        var owner = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var other = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var id = NewPgsId();
        (await LinkAsync(_a.BearerClient(owner.SessionToken), Pgs(id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var receipts = _a.Store.All(GameServer.Platform.Commands.ReceiptDocument.Collection).Count;

        var conflict = await LinkAsync(_b.BearerClient(other.SessionToken), Pgs(id));

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var error = await conflict.ReadErrorAsync();
        error.Code.ShouldBe("IDENTITY_CONFLICT");
        error.Details["provider"].ShouldBe("pgs");
        MappedPlayer("pgs:" + id).ShouldBe(owner.PlayerId);
        Profile(other.PlayerId).Identities.ShouldNotContain("pgs:" + id);
        _a.Store.All(GameServer.Platform.Commands.ReceiptDocument.Collection).Count.ShouldBe(receipts);
    }

    [Fact]
    public async Task Racing_links_on_two_instances_have_one_winner()
    {
        for (var round = 0; round < 10; round++)
        {
            var p1 = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            var p2 = await _b.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
            var id = NewPgsId();

            var responses = await Task.WhenAll(LinkAsync(_a.BearerClient(p1.SessionToken), Pgs(id)), LinkAsync(_b.BearerClient(p2.SessionToken), Pgs(id)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
            var winner = responses[0].StatusCode == HttpStatusCode.OK ? p1.PlayerId : p2.PlayerId;
            var loser = winner == p1.PlayerId ? p2.PlayerId : p1.PlayerId;
            MappedPlayer("pgs:" + id).ShouldBe(winner);
            Profile(loser).Identities.ShouldNotContain("pgs:" + id);
        }
    }

    [Fact]
    public async Task Invalid_proof_or_outage_writes_nothing()
    {
        var guest = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var mappings = _a.Store.All(IdentityCollections.Identities).Count;

        var invalid = await LinkAsync(_a.BearerClient(guest.SessionToken), new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = "forged" } });
        invalid.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await invalid.ReadErrorAsync()).Code.ShouldBe("IDENTITY_PROOF_INVALID");

        var outage = await LinkAsync(_a.BearerClient(guest.SessionToken), new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = FakeIdentityAdapter.UnavailableCode } });
        outage.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = await outage.ReadErrorAsync();
        error.Code.ShouldBe("PROVIDER_UNAVAILABLE");
        error.Retryable.ShouldBeTrue();

        var missing = await LinkAsync(_a.BearerClient(guest.SessionToken), new ProviderCredential());
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var unknown = await LinkAsync(_a.BearerClient(guest.SessionToken), new ProviderCredential { Custom = new GameServer.Modules.Identity.Protocol.V1.CustomCredential { Provider = "acme" } });
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        _a.Store.All(IdentityCollections.Identities).Count.ShouldBe(mappings);
        Profile(guest.PlayerId).Identities.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reinstall_login_returns_the_linked_player_and_replaces_the_old_session()
    {
        var original = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await _a.BearerClient(original.SessionToken).PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 5 }, ProtobufHttp.NewRequestId());
        var id = NewPgsId();
        (await LinkAsync(_a.BearerClient(original.SessionToken), Pgs(id))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // New device: no guest secret, no session.
        var login = await ProviderLoginOkAsync(_b.Client(), Pgs(id));

        login.PlayerId.ShouldBe(original.PlayerId);
        CounterState.Parser.ParseFrom(login.Sections.Single(s => s.Id == "counter").Payload).Value.ShouldBe(5);
        var old = await _a.BearerClient(original.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());
        old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await old.ReadErrorAsync()).Code.ShouldBe("SESSION_REPLACED");
    }

    [Fact]
    public async Task First_provider_login_creates_one_player_even_when_racing()
    {
        var id = NewPgsId();

        var logins = await Task.WhenAll(ProviderLoginOkAsync(_a.Client(), Pgs(id)), ProviderLoginOkAsync(_b.Client(), Pgs(id)));

        logins[0].PlayerId.ShouldBe(logins[1].PlayerId);
        MappedPlayer("pgs:" + id).ShouldBe(logins[0].PlayerId);
        Profile(logins[0].PlayerId).Identities.ShouldBe(["pgs:" + id]);

        var gameCenter = new ProviderCredential
        {
            GameCenter = new GameServer.Modules.Identity.Protocol.V1.GameCenterCredential { PublicKeyUrl = FakeIdentityAdapter.Prefix, TeamPlayerId = "T:_" + id, GamePlayerId = "A:_" + id },
        };
        var ios = await ProviderLoginOkAsync(_a.Client(), gameCenter);
        ios.PlayerId.ShouldNotBe(logins[0].PlayerId);
        _a.Store.All(IdentityCollections.Identities).Single(d => d.Key.Key == "gamecenter:T:_" + id).Content["attributes"]![GameCenterAdapter.UnverifiedGamePlayerIdAttribute]!.GetValue<string>().ShouldBe("A:_" + id);
    }

    [Fact]
    public async Task Device_session_of_another_player_needs_a_confirmed_switch()
    {
        var b = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var id = NewPgsId();
        (await LinkAsync(_a.BearerClient(b.SessionToken), Pgs(id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var a = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var refused = await ProviderLoginAsync(_b.BearerClient(a.SessionToken), Pgs(id));
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var error = await refused.ReadErrorAsync();
        error.Code.ShouldBe("SWITCH_ACCOUNT");
        error.Details["provider"].ShouldBe("pgs");
        error.Details["account"].ShouldBe("existing");

        // Nothing changed: A's session lives on, nothing merged.
        (await _a.BearerClient(a.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
        Profile(a.PlayerId).Identities.Count.ShouldBe(1);

        var switched = await ProviderLoginOkAsync(_b.BearerClient(a.SessionToken), Pgs(id), switchAccount: true);
        switched.PlayerId.ShouldBe(b.PlayerId);

        // An unmapped identity would be a new account: same question.
        var fresh = await ProviderLoginAsync(_a.BearerClient(a.SessionToken), Pgs(NewPgsId()));
        var freshError = await fresh.ReadErrorAsync();
        freshError.Code.ShouldBe("SWITCH_ACCOUNT");
        freshError.Details["account"].ShouldBe("new");

        // The session of the mapped player itself, or a broken token, is a normal login.
        (await ProviderLoginOkAsync(_a.BearerClient(switched.SessionToken), Pgs(id))).PlayerId.ShouldBe(b.PlayerId);
        (await ProviderLoginOkAsync(_a.BearerClient("not-a-token"), Pgs(id))).PlayerId.ShouldBe(b.PlayerId);
    }

    [Fact]
    public async Task Banned_or_deleted_players_cannot_log_in_with_a_provider()
    {
        var guest = await _a.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var id = NewPgsId();
        (await LinkAsync(_a.BearerClient(guest.SessionToken), Pgs(id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _a.Services.GetRequiredService<GameServer.Platform.Aggregates.IPlayerAggregateWriter>().UpdateAsync<ProfileState>(
            guest.PlayerId,
            IdentityAggregates.Profile,
            p => p with { Ban = new BanInfo { Reason = "test" } },
            CancellationToken.None);

        var response = await ProviderLoginAsync(_a.Client(), Pgs(id));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");

        await _a.Services.GetRequiredService<GameServer.Platform.Aggregates.IPlayerAggregateWriter>().UpdateAsync<ProfileState>(
            guest.PlayerId,
            IdentityAggregates.Profile,
            p => p with { Ban = null, Deleted = new DeletionInfo { RequestedAtUtc = _a.Clock.UtcNow, PurgeAfterUtc = _a.Clock.UtcNow.AddDays(30) } },
            CancellationToken.None);

        var deleted = await ProviderLoginAsync(_a.Client(), Pgs(id));

        deleted.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await deleted.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_DELETED");
    }

    [Fact]
    public async Task Link_attempts_are_limited_per_player_and_failed_proofs_count_per_ip()
    {
        await using var host = await TestHost.StartAsync(settings: s =>
        {
            s["Identity:Providers:LinkPerPlayerPerMinute"] = "3";
            s["Identity:FailedLoginPerIpPerMinute"] = "2";
        });
        var guest = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var forged = new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = "forged" } };

        (await LinkAsync(host.BearerClient(guest.SessionToken), forged)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkAsync(host.BearerClient(guest.SessionToken), forged)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The IP spent its failure budget: logins from it are refused too.
        (await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret())).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await LinkAsync(host.BearerClient(guest.SessionToken), Pgs(NewPgsId()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkAsync(host.BearerClient(guest.SessionToken), Pgs(NewPgsId()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkAsync(host.BearerClient(guest.SessionToken), Pgs(NewPgsId()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkAsync(host.BearerClient(guest.SessionToken), Pgs(NewPgsId()))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Failed_proofs_count_against_the_ip_limit()
    {
        await using var host = await TestHost.StartAsync(settings: s => s["Identity:FailedLoginPerIpPerMinute"] = "2");
        for (var i = 0; i < 2; i++)
        {
            (await ProviderLoginAsync(host.Client(), new ProviderCredential { PlayGames = new PlayGames { ServerAuthCode = "forged" } })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var limited = await ProviderLoginAsync(host.Client(), Pgs(NewPgsId()));
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
