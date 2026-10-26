using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Wire = SampleGame.Protocol.V1;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>US1 of 003: BuyVirtualItem through Economy (SMP-05, ECO-01..07, ACC-06, ACC-21, ACC-36).</summary>
public sealed class ShopTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await GameHttp.StartGameAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Buying_debits_and_credits_in_one_commit_with_two_ledger_entries()
    {
        var (player, _) = await _host.NewPlayerAsync();
        await player.CheatAsync("gold", 100);

        var response = await player.BuyAsync("potion_pack", 1);

        response.Code.ShouldBe("OK");
        Wire.BuyItemResult.Parser.ParseFrom(response.Result).ShouldBe(new Wire.BuyItemResult { PricePaid = 60, Granted = 1 });
        response.Updates.Select(u => u.Id).OrderBy(x => x).ShouldBe(["inventory", "wallet"]);
        (await player.WalletAsync()).Balances["gold"].ShouldBe(40);
        (await player.InventoryAsync()).Items["potion"].ShouldBe(1);

        var shopLedger = _host.Ledger(player.PlayerId).Where(d => d["receiptId"]!.GetValue<string>() == response.ReceiptId).ToArray();
        shopLedger.Length.ShouldBe(2);
        shopLedger.ShouldContain(d => d["payload"]!["resource"]!.GetValue<string>() == "gold"
                                      && d["payload"]!["delta"]!.GetValue<long>() == -60
                                      && d["payload"]!["balanceAfter"]!.GetValue<long>() == 40
                                      && d["payload"]!["sourceType"]!.GetValue<string>() == "shop");
        shopLedger.ShouldContain(d => d["payload"]!["resource"]!.GetValue<string>() == "potion" && d["payload"]!["delta"]!.GetValue<long>() == 1);
    }

    [Fact]
    public async Task Missing_funds_change_nothing()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await player.BuyAsync("potion_pack", 1);

        response.Code.ShouldBe("INSUFFICIENT_FUNDS");
        response.ReceiptId.ShouldBeEmpty();
        _host.Ledger(player.PlayerId).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("broken", 1, "UNKNOWN_ITEM")]
    [InlineData("potion_pack", 0, "INVALID_QUANTITY")]
    [InlineData("potion_pack", 101, "INVALID_QUANTITY")]
    [InlineData("huge", 2, "QUANTITY_LIMIT_EXCEEDED")]
    [InlineData("sword_bundle", 3, "QUANTITY_LIMIT_EXCEEDED")]
    [InlineData("no_such_item", 1, "UNKNOWN_SHOP_ITEM")]
    public async Task Invalid_purchases_are_rejected_without_side_effects(string shopItemId, int quantity, string code)
    {
        var (player, _) = await _host.NewPlayerAsync();
        await player.CheatAsync("gold", 1000);
        var ledgerBefore = _host.Ledger(player.PlayerId).Count;

        var response = await player.BuyAsync(shopItemId, quantity);

        response.Code.ShouldBe(code);
        (await player.WalletAsync()).Balances["gold"].ShouldBe(1000);
        _host.Ledger(player.PlayerId).Count.ShouldBe(ledgerBefore);
    }

    [Fact]
    public async Task Retrying_a_purchase_replays_without_a_second_debit()
    {
        var (player, _) = await _host.NewPlayerAsync();
        await player.CheatAsync("gold", 200);
        var requestId = ProtobufHttp.NewRequestId();

        var first = await player.BuyAsync("potion_pack", 1, requestId);
        var replay = await player.BuyAsync("potion_pack", 1, requestId);

        replay.Replayed.ShouldBeTrue();
        replay.ReceiptId.ShouldBe(first.ReceiptId);
        (await player.WalletAsync()).Balances["gold"].ShouldBe(140);
    }

    [Fact]
    public async Task Two_concurrent_purchases_on_two_instances_with_funds_for_one()
    {
        var clock = new FakeClock();
        var store = new InMemoryDocumentStore(clock);
        await using var other = await GameHttp.StartGameAsync(store: store, clock: clock);
        await using var first = await GameHttp.StartGameAsync(store: store, clock: clock);
        var (player, token) = await first.NewPlayerAsync();
        await player.CheatAsync("gold", 60);
        var onOther = player.On(other, token);

        var results = await Task.WhenAll(player.BuyAsync("potion_pack", 1), onOther.BuyAsync("potion_pack", 1));

        results.Select(r => r.Code).OrderBy(c => c).ShouldBe(["INSUFFICIENT_FUNDS", "OK"]);
        var wallet = await player.WalletAsync();
        wallet.Balances.ContainsKey("gold").ShouldBeFalse();
        first.LedgerSum(player.PlayerId, "gold").ShouldBe(0);
        first.LedgerSum(player.PlayerId, "potion").ShouldBe((await player.InventoryAsync()).Items["potion"]);
    }
}
