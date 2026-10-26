using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Shouldly;
using Wire = SampleGame.Protocol.V1;

namespace SampleGame.Tests;

/// <summary>
/// ACC-53: the five SampleGame commands end-to-end on two instances sharing one store — Login, StartBattle, EndBattle,
/// BuyVirtualItem, IAPurchase — with replays crossing instances.
/// </summary>
public sealed class SampleGameEndToEndTests
{
    [Fact]
    public async Task Five_commands_across_two_instances()
    {
        var clock = new FakeClock();
        var store = new InMemoryDocumentStore(clock);
        await using var a = await GameHttp.StartGameAsync(store: store, clock: clock);
        await using var b = await GameHttp.StartGameAsync(store: store, clock: clock);

        // Login on A, play on both.
        var (onA, token) = await a.NewPlayerAsync();
        var onB = onA.On(b, token);

        var startRequest = ProtobufHttp.NewRequestId();
        var battleId = Wire.StartBattleResult.Parser.ParseFrom((await onA.StartBattleAsync(1, startRequest)).Result).BattleId;
        (await onB.StartBattleAsync(1, startRequest)).Replayed.ShouldBeTrue();

        var end = await onB.EndBattleAsync(battleId, won: true);
        end.Updates.Count.ShouldBe(4);
        (await onA.EndBattleAsync(battleId, won: true)).Code.ShouldBe("NO_ACTIVE_BATTLE");

        var buyRequest = ProtobufHttp.NewRequestId();
        (await onB.BuyAsync("potion_pack", 1, buyRequest)).Code.ShouldBe("INSUFFICIENT_FUNDS");
        await onA.CheatAsync("gold", 10);
        var bought = await onB.BuyAsync("potion_pack", 1);
        bought.Code.ShouldBe("OK");

        (await onA.IapAsync("google", "gems_100", "valid:e2e-token")).Code.ShouldBe("OK");
        (await onB.IapAsync("google", "gems_100", "valid:e2e-token")).Code.ShouldBe("ALREADY_GRANTED");

        var wallet = await onA.WalletAsync();
        wallet.Balances.ContainsKey("gold").ShouldBeFalse(); // zero balances are not stored
        wallet.Balances["gem"].ShouldBe(100);
        (await onB.InventoryAsync()).Items["potion"].ShouldBe(2);
        (await onA.ProgressionAsync()).Stage.ShouldBe(2);

        // Ledger reconciles with balances (ECO-06).
        a.LedgerSum(onA.PlayerId, "gold").ShouldBe(0);
        a.LedgerSum(onA.PlayerId, "gem").ShouldBe(100);
        a.LedgerSum(onA.PlayerId, "potion").ShouldBe(2);
    }
}
