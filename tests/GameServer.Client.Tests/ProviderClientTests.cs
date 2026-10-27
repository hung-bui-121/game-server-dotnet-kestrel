extern alias sdk;

using FakeIdentityAdapter = GameServer.Modules.Identity.FakeIdentityAdapter;
using GameServer.Tests.Shared;
using sdk::GameServer.Client;
using sdk::GameServer.Modules.Identity.Protocol.V1;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 009 end to end: the SDK against a real host with the Development fake Play Games adapter (<c>fake:{id}</c>):
/// link, reinstall login, SWITCH_ACCOUNT keeps the session, IDENTITY_CONFLICT (ACC-03, ACC-29, SDK-02).
/// </summary>
public sealed class ProviderClientTests
{
    private static ProviderCredential Pgs(string id) => new() { PlayGames = new PlayGamesCredential { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    private static (GameServerClient Client, RecordingDelay Delay, ScriptedTransport Transport) NewClient(TestHost host)
    {
        var http = host.Client();
        var delay = new RecordingDelay();
        var transport = new ScriptedTransport(new HttpClientTransport(http));
        return (new GameServerClient(new GameServerClientOptions(http.BaseAddress!, "1.0.0", "android", "b1"), transport, delay: delay), delay, transport);
    }

    [Fact]
    public async Task Link_then_reinstall_login_returns_the_same_player()
    {
        await using var host = await TestHost.StartAsync(GameHttp.FullGame);
        var id = "g" + Guid.NewGuid().ToString("N")[..16];
        var (phone, _, _) = NewClient(host);
        var (localId, secret) = SdkHarness.NewDevice();
        var guest = await phone.LoginGuestAsync(localId, secret);

        var link = await phone.LinkProviderAsync(Pgs(id));
        link.IsOk.ShouldBeTrue();
        link.Result!.Provider.ShouldBe("pgs");
        link.Result.AlreadyLinked.ShouldBeFalse();
        (await phone.LinkProviderAsync(Pgs("g-other"))).Code.ShouldBe(ErrorCodes.ProviderAlreadyLinked);

        // Reinstall: a fresh client without the guest secret.
        var (reinstalled, _, _) = NewClient(host);
        var login = await reinstalled.LoginWithProviderAsync(Pgs(id));
        login.PlayerId.ShouldBe(guest.PlayerId);
        reinstalled.PlayerId.ShouldBe(guest.PlayerId);
        reinstalled.Sections.TryGet("profile", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Switch_account_keeps_the_session_until_the_player_confirms()
    {
        await using var host = await TestHost.StartAsync(GameHttp.FullGame);
        var id = "g" + Guid.NewGuid().ToString("N")[..16];
        var (owner, _, _) = NewClient(host);
        var (ownerLocal, ownerSecret) = SdkHarness.NewDevice();
        var ownerLogin = await owner.LoginGuestAsync(ownerLocal, ownerSecret);
        (await owner.LinkProviderAsync(Pgs(id))).IsOk.ShouldBeTrue();

        var (device, delay, transport) = NewClient(host);
        var (localId, secret) = SdkHarness.NewDevice();
        var guest = await device.LoginGuestAsync(localId, secret);
        GameServerError? blocked = null;
        device.AccountBlocked += e => blocked = e;

        var ex = await Should.ThrowAsync<GameServerException>(() => device.LoginWithProviderAsync(Pgs(id)));

        ex.Error.Code.ShouldBe(ErrorCodes.SwitchAccount);
        ex.Error.Details["account"].ShouldBe("existing");
        blocked.ShouldNotBeNull();
        device.HasSession.ShouldBeTrue();
        device.PlayerId.ShouldBe(guest.PlayerId);
        delay.Delays.ShouldBeEmpty();
        transport.RequestsTo(GameServerClient.ProviderLoginRoute).Single().Headers["Authorization"].ShouldStartWith("Bearer ");

        var switched = await device.LoginWithProviderAsync(Pgs(id), switchAccount: true);
        switched.PlayerId.ShouldBe(ownerLogin.PlayerId);
        device.PlayerId.ShouldBe(ownerLogin.PlayerId);
    }

    [Fact]
    public async Task Errors_about_the_target_account_keep_the_current_session()
    {
        await using var host = await TestHost.StartAsync(GameHttp.FullGame);
        var id = "g" + Guid.NewGuid().ToString("N")[..16];
        var (owner, _, _) = NewClient(host);
        var (ownerLocal, ownerSecret) = SdkHarness.NewDevice();
        var ownerLogin = await owner.LoginGuestAsync(ownerLocal, ownerSecret);
        (await owner.LinkProviderAsync(Pgs(id))).IsOk.ShouldBeTrue();
        await host.Services.GetRequiredService<GameServer.Platform.Aggregates.IPlayerAggregateWriter>().UpdateAsync<GameServer.Modules.Identity.ProfileState>(
            ownerLogin.PlayerId,
            GameServer.Modules.Identity.IdentityAggregates.Profile,
            p => p with { Deleted = new GameServer.Modules.Identity.DeletionInfo { RequestedAtUtc = host.Clock.UtcNow, PurgeAfterUtc = host.Clock.UtcNow.AddDays(30) } },
            CancellationToken.None);

        var (device, _, _) = NewClient(host);
        var (localId, secret) = SdkHarness.NewDevice();
        var guest = await device.LoginGuestAsync(localId, secret);

        var ex = await Should.ThrowAsync<GameServerException>(() => device.LoginWithProviderAsync(Pgs(id), switchAccount: true));

        ex.Error.Code.ShouldBe(ErrorCodes.AccountDeleted);
        device.HasSession.ShouldBeTrue();
        device.PlayerId.ShouldBe(guest.PlayerId);
    }

    [Fact]
    public async Task Identity_of_another_player_is_reported_as_a_conflict()
    {
        await using var host = await TestHost.StartAsync(GameHttp.FullGame);
        var id = "g" + Guid.NewGuid().ToString("N")[..16];
        var (owner, _, _) = NewClient(host);
        var (ownerLocal, ownerSecret) = SdkHarness.NewDevice();
        await owner.LoginGuestAsync(ownerLocal, ownerSecret);
        (await owner.LinkProviderAsync(Pgs(id))).IsOk.ShouldBeTrue();

        var (other, delay, _) = NewClient(host);
        var (localId, secret) = SdkHarness.NewDevice();
        await other.LoginGuestAsync(localId, secret);

        var ex = await Should.ThrowAsync<GameServerException>(() => other.LinkProviderAsync(Pgs(id)));

        ex.Error.Code.ShouldBe(ErrorCodes.IdentityConflict);
        ex.Error.HttpStatus.ShouldBe(409);
        ex.Error.Details["provider"].ShouldBe("pgs");
        delay.Delays.ShouldBeEmpty();
        other.HasSession.ShouldBeTrue();
    }
}
