using GameServer.Config;
using GameServer.Modules.Economy;
using SampleGame.Adapters;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// <c>shop.buy</c> without a host (slice 015 T015): a shop item whose grant names no catalog resource is rejected. Such a config
/// no longer starts a host (rule <c>sample.shop</c>), so the case runs on the kit's command-context double.
/// </summary>
public sealed class BuyItemHandlerTests
{
    private const string Resources = """
        {"resources": [
          {"id": "gold", "kind": "currency", "maxQuantity": 1000000000, "displayName": "Gold"},
          {"id": "potion", "kind": "item", "maxQuantity": 999, "displayName": "Potion"}
        ]}
        """;

    private const string Shop = """
        {"items": {
          "broken": {"price": {"resource": "gold", "amount": 1}, "grant": {"resource": "unknown_item", "quantity": 1}}
        }}
        """;

    [Fact]
    public async Task A_grant_of_an_unknown_resource_is_rejected_and_nothing_commits()
    {
        var config = TestGameConfig.FromJson(new Dictionary<string, string> { [ResourceCatalog.ConfigFile] = Resources, [ShopCatalog.ConfigFile] = Shop });
        var handler = new BuyItemHandler(ShopCatalog.Load(config), ResourceCatalog.Load(config));
        var context = CommandContextDouble.Create(BuyItemHandler.Operation, new EconomyModule(), new SampleGameModule())
            .AsPlayer("p_1")
            .WithConfig(config)
            .Seed(EconomyAggregates.Wallet, new WalletState { Balances = new Dictionary<string, long> { ["gold"] = 1000 } });

        var outcome = await handler.HandleAsync(new BuyItemCommand("broken", 1), context);

        // A rejected outcome commits nothing: the runner discards every staged change (no receipt, no ledger entry).
        outcome.IsOk.ShouldBeFalse();
        outcome.Error.ShouldBe(EconomyErrors.UnknownItem);
        context.CheckCommit(outcome).ShouldBeEmpty();
        context.Written<InventoryState>(EconomyAggregates.Inventory).ShouldBeNull();
    }
}
