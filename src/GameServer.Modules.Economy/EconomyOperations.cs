using GameServer.Commands;
using GameServer.Errors;

namespace GameServer.Modules.Economy;

/// <summary>
/// The only way to change resources (ECO-01): operations on the command's <see cref="ICommandContext"/>, so the change,
/// its ledger entry (outbox) and the receipt commit in one transaction (ECO-04, ECO-06). The calling command must
/// declare <c>wallet</c> and/or <c>inventory</c> in Touches. A non-null return is a business error: the handler should
/// reject the command, which rolls everything back.
/// </summary>
public static class EconomyOperations
{
    public const string LedgerKind = "ledger";
    public const string LedgerCollection = "ledger";
    public const string ClaimCollection = "extref_claims";

    public static ErrorCode? Grant(ICommandContext context, ResourceCatalog catalog, string resource, long quantity, LedgerSource source) =>
        quantity <= 0 ? EconomyErrors.InvalidQuantity : Apply(context, catalog, resource, quantity, source);

    public static ErrorCode? Debit(ICommandContext context, ResourceCatalog catalog, string resource, long quantity, LedgerSource source) =>
        quantity <= 0 ? EconomyErrors.InvalidQuantity : Apply(context, catalog, resource, -quantity, source);

    /// <summary>Current amount of a resource in the command's view.</summary>
    public static long Balance(ICommandContext context, ResourceCatalog catalog, string resource) =>
        catalog.Find(resource) switch
        {
            { Kind: ResourceKind.Currency } => context.Get<WalletState>(EconomyAggregates.Wallet).Balances.GetValueOrDefault(resource),
            { Kind: ResourceKind.Item } => context.Get<InventoryState>(EconomyAggregates.Inventory).Items.GetValueOrDefault(resource),
            _ => 0,
        };

    /// <summary>
    /// Reserves the one-time claim of an external grant (ECO-08): <c>extref::{sourceType}::{externalReference}</c> inserted in
    /// the same transaction. False means it was already granted.
    /// </summary>
    public static ValueTask<bool> TryClaimExternalAsync(ICommandContext context, string sourceType, string externalReference) =>
        context.TryClaimAsync(ClaimCollection, $"extref::{sourceType}::{externalReference}");

    private static ErrorCode? Apply(ICommandContext context, ResourceCatalog catalog, string resource, long delta, LedgerSource source)
    {
        var definition = catalog.Find(resource);
        if (definition is null)
        {
            return EconomyErrors.UnknownItem;
        }

        var current = Balance(context, catalog, resource);
        long next;
        try
        {
            next = checked(current + delta);
        }
        catch (OverflowException)
        {
            return EconomyErrors.QuantityLimitExceeded;
        }

        if (next < 0)
        {
            return EconomyErrors.InsufficientFunds;
        }

        if (next > definition.MaxQuantity)
        {
            return EconomyErrors.QuantityLimitExceeded;
        }

        if (definition.Kind == ResourceKind.Currency)
        {
            var wallet = context.Get<WalletState>(EconomyAggregates.Wallet);
            context.Set(EconomyAggregates.Wallet, wallet with { Balances = With(wallet.Balances, resource, next) });
        }
        else
        {
            var inventory = context.Get<InventoryState>(EconomyAggregates.Inventory);
            context.Set(EconomyAggregates.Inventory, inventory with { Items = With(inventory.Items, resource, next) });
        }

        var actor = context.Actor;
        context.AddOutbox(LedgerKind, new LedgerEntry(
            actor.PlayerId ?? string.Empty,
            actor.ActorId,
            source.SourceType,
            resource,
            delta,
            next,
            source.ExternalReference,
            source.OrderId));
        return null;
    }

    private static Dictionary<string, long> With(IReadOnlyDictionary<string, long> current, string key, long value)
    {
        var copy = new Dictionary<string, long>(current, StringComparer.Ordinal);
        if (value == 0)
        {
            copy.Remove(key);
        }
        else
        {
            copy[key] = value;
        }

        return copy;
    }
}
