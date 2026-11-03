using System.Net;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Tests.Shared;
using Shouldly;
using PlayGames = GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential;
using WireProfile = GameServer.Modules.Identity.Protocol.V1.ProfileState;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T019 (FR-001, FR-002, US1-1..US1-4, SC-001): <c>LoginResponse.is_new_player</c> is true exactly for the login that
/// created the player, and in every row the analytics login record's <c>isNewUser</c> equals the flag.
/// </summary>
public sealed class NewPlayerFlagTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await SampleTestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_new_guest_is_new_and_the_same_guest_later_is_not()
    {
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        var first = await _host.Client().LoginAsync(localId, secret);
        var second = await _host.Client().LoginAsync(localId, secret);

        first.IsNewPlayer.ShouldBeTrue();
        second.IsNewPlayer.ShouldBeFalse();
        second.PlayerId.ShouldBe(first.PlayerId);
        (await AnalyticsFlagsAsync(first.PlayerId)).ShouldBe([true, false]);
    }

    [Fact]
    public async Task A_new_provider_identity_is_new_and_the_same_identity_later_is_not()
    {
        var credential = Pgs(NewPgsId());

        var first = await ProviderLoginOkAsync(_host.Client(), credential);
        var second = await ProviderLoginOkAsync(_host.Client(), credential);

        first.IsNewPlayer.ShouldBeTrue();
        second.IsNewPlayer.ShouldBeFalse();
        second.PlayerId.ShouldBe(first.PlayerId);
        (await AnalyticsFlagsAsync(first.PlayerId)).ShouldBe([true, false]);
    }

    [Fact]
    public async Task A_confirmed_switch_into_a_new_account_is_new_and_an_unconfirmed_one_creates_nothing()
    {
        var guest = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var credential = Pgs(NewPgsId());
        var profilesBefore = _host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection);
        var device = _host.BearerClient(guest.SessionToken);

        var refused = await ProviderLoginAsync(device, credential, switchAccount: false);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadErrorAsync()).Code.ShouldBe("SWITCH_ACCOUNT");
        _host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(profilesBefore);

        var switched = await ProviderLoginOkAsync(device, credential, switchAccount: true);

        switched.IsNewPlayer.ShouldBeTrue();
        switched.PlayerId.ShouldNotBe(guest.PlayerId);
        _host.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(profilesBefore + 1);
        (await AnalyticsFlagsAsync(switched.PlayerId)).ShouldBe([true]);
        (await AnalyticsFlagsAsync(guest.PlayerId)).ShouldBe([true]);
    }

    [Fact]
    public async Task A_takeover_from_a_second_device_is_not_new()
    {
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var first = await _host.Client().LoginAsync(localId, secret);

        // Same credential on another device: single session, the first one is replaced (IDN-10).
        var takeover = await _host.Client().LoginAsync(localId, secret);

        takeover.IsNewPlayer.ShouldBeFalse();
        var oldSession = await _host.BearerClient(first.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest());
        (await oldSession.ReadErrorAsync()).Code.ShouldBe("SESSION_REPLACED");
        (await AnalyticsFlagsAsync(first.PlayerId)).ShouldBe([true, false]);
    }

    [Fact]
    public async Task A_retry_after_a_lost_first_login_response_is_not_new_and_moves_the_epoch_once()
    {
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

        // The server committed the creation; the client never saw the response.
        var lost = await _host.Client().PostLoginAsync(localId, secret);
        lost.StatusCode.ShouldBe(HttpStatusCode.OK);
        lost.Dispose();
        var retry = await _host.Client().LoginAsync(localId, secret);

        retry.IsNewPlayer.ShouldBeFalse();
        Epoch(retry).ShouldBe(2);
        (await AnalyticsFlagsAsync(retry.PlayerId)).ShouldBe([true, false]);
    }

    private static long Epoch(LoginResponse login) =>
        WireProfile.Parser.ParseFrom(login.Sections.Single(s => s.Id == IdentityAggregates.Profile).Payload).SessionEpoch;

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

    /// <summary>The <c>isNewUser</c> of the player's login records, in order (the analytics source of truth for "new").</summary>
    private async Task<bool[]> AnalyticsFlagsAsync(string playerId)
    {
        await _host.FlushAnalyticsAsync();
        return _host.Analytics.OfEvent("login")
            .Where(r => r["source"]!.GetValue<string>() == "login" && r["userID"]!.GetValue<string>() == playerId)
            .Select(r => r["isNewUser"]!.GetValue<bool>())
            .ToArray();
    }
}
