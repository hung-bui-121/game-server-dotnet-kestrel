using System.Net;
using GameServer.Modules.Economy;
using GameServer.Platform;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>US4/US5 of 003: GetConfig (CFG-02, CFG-03, ACC-35) and Development cheats (OPS-09, ACC-32).</summary>
public sealed class ConfigAndCheatTests
{
    private const string ConfigRoute = "/api/v1/config/get";

    [Fact]
    public async Task GetConfig_returns_only_client_visible_files_and_not_modified_for_current_version()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();

        var full = await (await player.Client.PostProtobufAsync(ConfigRoute, new GetConfigRequest())).ReadProtobufAsync<GetConfigResponse>();

        full.NotModified.ShouldBeFalse();
        full.ConfigVersion.ShouldStartWith("test-1+");
        full.Files.Select(f => f.Name).ShouldBe(["battle.json", "counter.json", "resources.json", "shop.json"]);
        full.Files.Single(f => f.Name == "shop.json").Content.ToStringUtf8().ShouldContain("potion_pack");

        var cached = await (await player.Client.PostProtobufAsync(ConfigRoute, new GetConfigRequest { ConfigVersion = full.ConfigVersion }))
            .ReadProtobufAsync<GetConfigResponse>();
        cached.NotModified.ShouldBeTrue();
        cached.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetConfig_requires_a_player_and_enforces_the_bundle_limit()
    {
        await using var host = await GameHttp.StartGameAsync(settings: s => s["GameServer:Limits:ConfigBundleBytes"] = "200");
        var (player, _) = await host.NewPlayerAsync();

        (await host.Client().PostProtobufAsync(ConfigRoute, new GetConfigRequest())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var tooLarge = await player.Client.PostProtobufAsync(ConfigRoute, new GetConfigRequest());
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await tooLarge.ReadErrorAsync()).Code.ShouldBe("PAYLOAD_TOO_LARGE");
    }

    [Fact]
    public async Task Receipts_record_the_config_version_of_the_instance()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();
        var response = await player.CheatAsync("gold", 10);

        var receipt = host.Store.Peek(GameServer.Platform.Commands.ReceiptDocument.KeyFor(response.ReceiptId))!;

        receipt["configVersion"]!.GetValue<string>().ShouldBe(response.ConfigVersion);
    }

    [Fact]
    public async Task Cheat_grant_is_ledgered_as_cheat()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();

        var response = await player.CheatAsync("gold", 100);

        response.Code.ShouldBe("OK");
        (await player.WalletAsync()).Balances["gold"].ShouldBe(100);
        host.Ledger(player.PlayerId).ShouldHaveSingleItem()["payload"]!["sourceType"]!.GetValue<string>().ShouldBe(CheatGrantHandler.SourceType);
    }

    [Fact]
    public async Task Cheat_module_in_production_fails_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(
                modules: GameHttp.FullGame,
                settings: s =>
                {
                    s["GameServer:DeploymentEnvironment"] = "Production";
                    s["Identity:GuestPepper"] = "prod-pepper";
                },
                testAuthentication: false);
        });

        ex.Message.ShouldContain("POST /api/v1/cheat/grant] uses profile Dev in Production");
    }
}
