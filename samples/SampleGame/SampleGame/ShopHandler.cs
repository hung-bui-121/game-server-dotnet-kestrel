using GameServer.Commands;
using GameServer.Modules;
using GameServer.Modules.Economy;

namespace SampleGame;

public sealed record BuyItemCommand(string ShopItemId, int Quantity);

public sealed record BuyItemResult(long PricePaid, long Granted);

/// <summary>
/// <c>shop.buy</c> (SMP-05): Touches wallet and inventory; debit and credit commit together with two ledger entries
/// sharing the receipt. Missing funds → INSUFFICIENT_FUNDS and nothing changes.
/// </summary>
public sealed class BuyItemHandler(ShopCatalog shop, ResourceCatalog resources) : ICommandHandler<BuyItemCommand, BuyItemResult>
{
    public const string Operation = "shop.buy";
    public const string LedgerSourceType = "shop";
    public const int MaxQuantityPerPurchase = 100;

    public static CommandDescriptor Descriptor { get; } =
        new(Operation, touches: [EconomyAggregates.Wallet, EconomyAggregates.Inventory]);

    public ValueTask<CommandOutcome<BuyItemResult>> HandleAsync(BuyItemCommand request, ICommandContext context)
    {
        if (!shop.Items.TryGetValue(request.ShopItemId, out var item))
        {
            return Reject(SampleGameErrors.UnknownShopItem);
        }

        if (request.Quantity is < 1 or > MaxQuantityPerPurchase)
        {
            return Reject(EconomyErrors.InvalidQuantity);
        }

        long price;
        long granted;
        try
        {
            price = checked(item.Price.Amount * request.Quantity);
            granted = checked(item.Grant.Quantity * request.Quantity);
        }
        catch (OverflowException)
        {
            return Reject(EconomyErrors.QuantityLimitExceeded);
        }

        var source = new LedgerSource(LedgerSourceType, ExternalReference: request.ShopItemId);
        if (EconomyOperations.Debit(context, resources, item.Price.Resource, price, source) is { } debitError)
        {
            return Reject(debitError);
        }

        if (EconomyOperations.Grant(context, resources, item.Grant.Resource, granted, source) is { } grantError)
        {
            return Reject(grantError);
        }

        return ValueTask.FromResult(CommandOutcome<BuyItemResult>.Ok(new BuyItemResult(price, granted)));
    }

    private static ValueTask<CommandOutcome<BuyItemResult>> Reject(GameServer.Errors.ErrorCode code) =>
        ValueTask.FromResult(CommandOutcome<BuyItemResult>.Reject(code));
}

/// <summary>Business half of the SampleGame module descriptor.</summary>
public static class SampleGameModuleDefinition
{
    public const string ModuleName = "sample.game";

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.1.0",
        DependsOn = ["identity", EconomyModuleDefinition.ModuleName],
        RequiredConfigFiles = [BattleCatalog.ConfigFile, ShopCatalog.ConfigFile],
        Aggregates = [SampleGameAggregates.ProgressionDescriptor, SampleGameAggregates.BattleDescriptor],
        Commands =
        [
            Command.Define<StartBattleCommand, StartBattleResult>(StartBattleHandler.Descriptor).HandledBy<StartBattleHandler>(),
            Command.Define<EndBattleCommand, EndBattleResult>(EndBattleHandler.Descriptor).HandledBy<EndBattleHandler>(),
            Command.Define<BuyItemCommand, BuyItemResult>(BuyItemHandler.Descriptor).HandledBy<BuyItemHandler>(),
        ],
        ErrorCodes = SampleGameErrors.All,
    };
}
