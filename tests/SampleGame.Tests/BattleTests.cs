using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Shouldly;
using Wire = SampleGame.Protocol.V1;

namespace SampleGame.Tests;

/// <summary>US2 of 003: StartBattle / EndBattle (8.4, SMP-02, SMP-03, ACC-07, ACC-42, ACC-53).</summary>
public sealed class BattleTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await GameHttp.StartGameAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static Wire.StartBattleResult Started(GameServer.Protocol.Core.V1.CommandResponse response) =>
        Wire.StartBattleResult.Parser.ParseFrom(response.Result);

    [Fact]
    public async Task Start_opens_a_battle_with_server_issued_id_seed_and_expiry()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await player.StartBattleAsync(1);

        response.Code.ShouldBe("OK");
        var started = Started(response);
        started.BattleId.ShouldNotBeNullOrEmpty();
        started.ExpiresAtUtcMs.ShouldBe(_host.Clock.UtcNow.AddHours(24).ToUnixTimeMilliseconds());
        var battle = await player.BattleAsync();
        battle.ActiveBattle.BattleId.ShouldBe(started.BattleId);
        battle.ActiveBattle.Seed.ShouldBe(started.Seed);
        battle.Stats.Started.ShouldBe(1);
    }

    [Fact]
    public async Task Locked_stage_is_refused()
    {
        var (player, _) = await _host.NewPlayerAsync();

        (await player.StartBattleAsync(3)).Code.ShouldBe("STAGE_LOCKED");
        (await player.StartBattleAsync(0)).Code.ShouldBe("STAGE_LOCKED");
    }

    [Fact]
    public async Task Starting_again_replaces_the_open_battle_by_default()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var first = Started(await player.StartBattleAsync(1));

        var second = Started(await player.StartBattleAsync(1));

        second.BattleId.ShouldNotBe(first.BattleId);
        var battle = await player.BattleAsync();
        battle.Stats.Abandoned.ShouldBe(1);
        battle.ActiveBattle.BattleId.ShouldBe(second.BattleId);
        (await player.EndBattleAsync(first.BattleId, true)).Code.ShouldBe("NO_ACTIVE_BATTLE");
    }

    [Fact]
    public async Task Reject_policy_refuses_a_second_battle()
    {
        await using var host = await GameHttp.StartGameAsync(extra: b => b.ConfigureExtensions(x => x.ReplacePolicy<IBattleStartPolicy, RejectActiveBattlePolicy>()));
        var (player, _) = await host.NewPlayerAsync();
        await player.StartBattleAsync(1);

        (await player.StartBattleAsync(1)).Code.ShouldBe("BATTLE_IN_PROGRESS");
    }

    [Fact]
    public async Task Winning_updates_battle_progression_wallet_and_inventory_together()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var battleId = Started(await player.StartBattleAsync(1)).BattleId;

        var response = await player.EndBattleAsync(battleId, won: true);

        response.Code.ShouldBe("OK");
        response.Updates.Select(u => u.Id).OrderBy(x => x).ShouldBe(["battle", "inventory", "progression", "wallet"]);
        var result = Wire.EndBattleResult.Parser.ParseFrom(response.Result);
        result.XpGained.ShouldBe(40);
        result.StageUnlocked.ShouldBe(2);
        (await player.WalletAsync()).Balances["gold"].ShouldBe(50);
        (await player.InventoryAsync()).Items["potion"].ShouldBe(1);
        var progression = await player.ProgressionAsync();
        progression.Stage.ShouldBe(2);
        progression.Xp.ShouldBe(40);
        var battle = await player.BattleAsync();
        battle.ActiveBattle.ShouldBeNull();
        battle.Stats.Won.ShouldBe(1);
        _host.Ledger(player.PlayerId).ShouldAllBe(d => d["payload"]!["sourceType"]!.GetValue<string>() == "battle");
        _host.Ledger(player.PlayerId).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Losing_closes_the_battle_without_rewards()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var battleId = Started(await player.StartBattleAsync(1)).BattleId;

        var response = await player.EndBattleAsync(battleId, won: false);

        response.Code.ShouldBe("OK");
        (await player.BattleAsync()).Stats.Lost.ShouldBe(1);
        _host.Ledger(player.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_battle_id_is_refused()
    {
        var (player, _) = await _host.NewPlayerAsync();
        await player.StartBattleAsync(1);

        (await player.EndBattleAsync("not-the-battle", true)).Code.ShouldBe("NO_ACTIVE_BATTLE");
    }

    [Fact]
    public async Task Expired_battle_is_cleared_and_reported_without_rewards()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var battleId = Started(await player.StartBattleAsync(1)).BattleId;
        _host.Clock.Advance(TimeSpan.FromHours(25));

        var response = await player.EndBattleAsync(battleId, won: true);

        response.Code.ShouldBe("BATTLE_EXPIRED");
        response.ReceiptId.ShouldNotBeNullOrEmpty();
        var battle = await player.BattleAsync();
        battle.ActiveBattle.ShouldBeNull();
        battle.Stats.Expired.ShouldBe(1);
        _host.Ledger(player.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rewarded_end_retried_after_the_replay_window_is_refused()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var battleId = Started(await player.StartBattleAsync(1)).BattleId;
        var requestId = ProtobufHttp.NewRequestId();
        await player.EndBattleAsync(battleId, true, requestId);

        _host.Clock.Advance(TimeSpan.FromHours(25));
        var late = await player.EndBattleAsync(battleId, true, requestId);

        late.Code.ShouldBe("NO_ACTIVE_BATTLE");
        (await player.WalletAsync()).Balances["gold"].ShouldBe(50);
    }

    [Fact]
    public async Task Ambiguous_end_commit_is_all_or_nothing_and_retry_replays()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var battleId = Started(await player.StartBattleAsync(1)).BattleId;
        var requestId = ProtobufHttp.NewRequestId();
        _host.Store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        var ambiguous = await player.PostAsync(SampleGame.Adapters.SampleGameModule.EndRoute, new Wire.EndBattleRequest { BattleId = battleId, Won = true }, requestId);
        ambiguous.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);

        var retry = await player.EndBattleAsync(battleId, true, requestId);
        retry.Replayed.ShouldBeTrue();
        (await player.WalletAsync()).Balances["gold"].ShouldBe(50);
        (await player.ProgressionAsync()).Stage.ShouldBe(2);
        (await player.BattleAsync()).ActiveBattle.ShouldBeNull();
    }

    [Fact]
    public async Task Start_retry_returns_the_same_battle()
    {
        var (player, _) = await _host.NewPlayerAsync();
        var requestId = ProtobufHttp.NewRequestId();

        var first = Started(await player.StartBattleAsync(1, requestId));
        var again = Started(await player.StartBattleAsync(1, requestId));

        again.BattleId.ShouldBe(first.BattleId);
        again.Seed.ShouldBe(first.Seed);
        (await player.BattleAsync()).Stats.Started.ShouldBe(1);
    }
}
