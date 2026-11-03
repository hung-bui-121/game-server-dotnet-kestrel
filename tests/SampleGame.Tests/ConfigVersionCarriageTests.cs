using System.Net;
using GameServer.Config;
using GameServer.Platform.Commands;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// FR-043, FR-044, FR-047, SC-004 (slice 015 T036): every player response carries the server version in <c>config_version</c>
/// (meaning unchanged, API-03) and the client version in <c>client_config_version</c>; GetConfig accepts either.
/// </summary>
public sealed class ConfigVersionCarriageTests : IAsyncLifetime
{
    private const string ConfigRoute = "/api/v1/config/get";

    /// <summary>The <c>config_version</c> of tests/Shared/config before this slice (T001 e): the v0.6.0 format.</summary>
    private const string PreviousFormatVersion = "test-1+223d18ea9fa1";

    private TestHost _host = null!;
    private IGameConfig _config = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GameHttp.StartGameAsync();
        _config = _host.Services.GetRequiredService<IGameConfig>();
        _config.ClientConfigVersion.ShouldNotBe(_config.ConfigVersion);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Login_and_snapshot_carry_both_versions()
    {
        var login = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var player = new GamePlayer(_host.BearerClient(login.SessionToken), login.PlayerId);
        var snapshot = await player.SnapshotAsync();

        (login.ConfigVersion, login.ClientConfigVersion).ShouldBe((_config.ConfigVersion, _config.ClientConfigVersion));
        (snapshot.ConfigVersion, snapshot.ClientConfigVersion).ShouldBe((_config.ConfigVersion, _config.ClientConfigVersion));
    }

    [Fact]
    public async Task Command_ok_rejection_and_replay_carry_both_versions_and_the_receipt_keeps_the_server_version()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var requestId = GameServer.TestKit.ProtobufHttp.NewRequestId();

        var ok = await player.CommandAsync(GameServer.Modules.Economy.CheatModule.GrantRoute, Grant(), requestId);
        var replay = await player.CommandAsync(GameServer.Modules.Economy.CheatModule.GrantRoute, Grant(), requestId);
        var (poor, _) = await _host.NewPlayerAsync();
        var rejected = await poor.BuyAsync("potion_pack", 1);

        replay.Replayed.ShouldBeTrue();
        rejected.Code.ShouldBe("INSUFFICIENT_FUNDS");
        foreach (var response in new[] { ok, replay, rejected })
        {
            (response.ConfigVersion, response.ClientConfigVersion).ShouldBe((_config.ConfigVersion, _config.ClientConfigVersion));
        }

        _host.Store.Peek(ReceiptDocument.KeyFor(ok.ReceiptId))!["configVersion"]!.GetValue<string>().ShouldBe(_config.ConfigVersion);
        var view = await (await player.Client.GetAsync($"/api/v1/player/receipts/{ok.ReceiptId}")).ReadProtobufAsync<ReceiptView>();
        view.ConfigVersion.ShouldBe(_config.ConfigVersion);
    }

    [Fact]
    public async Task Error_envelopes_from_a_handler_and_from_middleware_carry_both_versions()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var fromHandler = await player.Client.GetAsync("/api/v1/player/receipts/unknown");
        var fromMiddleware = await _host.Client().PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

        fromHandler.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        fromMiddleware.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        foreach (var envelope in new[] { await fromHandler.ReadErrorAsync(), await fromMiddleware.ReadErrorAsync() })
        {
            (envelope.ConfigVersion, envelope.ClientConfigVersion).ShouldBe((_config.ConfigVersion, _config.ClientConfigVersion));
        }
    }

    [Fact]
    public async Task GetConfig_names_the_files_with_the_client_version_and_answers_not_modified_for_either_version()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var full = await GetConfigAsync(player, string.Empty);
        var byClient = await GetConfigAsync(player, _config.ClientConfigVersion);
        var byServer = await GetConfigAsync(player, _config.ConfigVersion);

        full.NotModified.ShouldBeFalse();
        full.Files.ShouldNotBeEmpty();
        foreach (var response in new[] { full, byClient, byServer })
        {
            (response.ConfigVersion, response.ClientConfigVersion).ShouldBe((_config.ConfigVersion, _config.ClientConfigVersion));
        }

        byClient.NotModified.ShouldBeTrue();
        byClient.Files.ShouldBeEmpty();
        byServer.NotModified.ShouldBeTrue();
        byServer.Files.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PreviousFormatVersion)]
    [InlineData("garbage")]
    [InlineData("TEST-1+0000")]
    public async Task GetConfig_sends_the_full_bundle_for_any_other_version(string held)
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await GetConfigAsync(player, held);

        response.NotModified.ShouldBeFalse();
        response.Files.Select(f => f.Name).ShouldBe(["battle.json", "counter.json", "resources.json", "shop.json"]);
    }

    [Fact]
    public void The_previous_format_value_is_not_a_current_version()
    {
        PreviousFormatVersion.ShouldNotBe(_config.ConfigVersion);
        PreviousFormatVersion.ShouldNotBe(_config.ClientConfigVersion);
    }

    private static GameServer.Modules.Economy.Protocol.V1.CheatGrantRequest Grant() => new() { Resource = "gold", Quantity = 5 };

    private static async Task<GetConfigResponse> GetConfigAsync(GamePlayer player, string held) =>
        await (await player.Client.PostProtobufAsync(ConfigRoute, new GetConfigRequest { ConfigVersion = held })).ReadProtobufAsync<GetConfigResponse>();
}
