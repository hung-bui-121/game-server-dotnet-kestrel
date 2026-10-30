using GameServer.Analytics;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Modules.Economy;

namespace GameServer.Modules.Iap;

/// <summary>Outcome of a store verification (IAP-01), produced before the transaction.</summary>
/// <param name="Valid">The store confirms an unrevoked purchase of <paramref name="ProductId"/> in the expected environment.</param>
/// <param name="Store">"google" or "apple".</param>
/// <param name="ProductId">Store product id.</param>
/// <param name="ExternalReference">Google purchaseToken / Apple transactionId (IAP-02).</param>
/// <param name="OrderId">Store order id, kept in the ledger (may be empty for test purchases).</param>
/// <param name="Reason">Why the purchase is invalid (logs only).</param>
public sealed record VerifiedPurchase(bool Valid, string Store, string ProductId, string ExternalReference, string? OrderId, string? Reason)
{
    /// <summary>
    /// Store test or sandbox purchase (Google <c>purchaseType</c> 0; Apple Sandbox/Xcode/LocalTesting; every fake
    /// verification). A custom verifier that does not set it reports real purchases.
    /// </summary>
    public bool IsTest { get; init; }

    /// <summary>Purchase time reported by the store (Google <c>purchaseTimeMillis</c>, Apple <c>purchaseDate</c>), when known.</summary>
    public DateTimeOffset? PurchasedAt { get; init; }

    /// <summary>Store purchase category when reported: <c>test</c>, <c>promo</c>, <c>rewarded</c> (Google <c>purchaseType</c> 0/1/2).</summary>
    public string? PurchaseCategory { get; init; }

    public static VerifiedPurchase Invalid(string store, string productId, string reason) => new(false, store, productId, string.Empty, null, reason);
}

/// <summary>Store adapter (IAP-01). Must be idempotent: the adapter may be called again on every client retry.</summary>
public interface IStoreReceiptVerifier
{
    string Store { get; }

    /// <summary>Verifies <paramref name="receiptPayload"/>; throws <see cref="StoreUnavailableException"/> on transient failures.</summary>
    Task<VerifiedPurchase> VerifyAsync(string productId, string receiptPayload, CancellationToken cancellationToken);
}

/// <summary>The store could not be reached or answered with a transient error: the client retries later (503).</summary>
public sealed class StoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record IapGrant(string Resource, long Quantity);

/// <summary>IAP catalog from <c>iap.json</c>: <c>"{store}:{productId}"</c> → grants (CFG-01, IAP-01).</summary>
public sealed class IapCatalog
{
    public const string ConfigFile = "iap.json";

    public required IReadOnlyDictionary<string, IReadOnlyList<IapGrant>> Products { get; init; }

    public IReadOnlyList<IapGrant>? Find(string store, string productId) => Products.GetValueOrDefault($"{store}:{productId}");

    public static IapCatalog Load(IGameConfig config) =>
        new() { Products = config.GetFile<IapFile>(ConfigFile).Products.ToDictionary(p => p.Key, p => (IReadOnlyList<IapGrant>)p.Value.Grants, StringComparer.Ordinal) };

    private sealed record IapFile(Dictionary<string, IapProduct> Products);

    private sealed record IapProduct(IReadOnlyList<IapGrant> Grants);
}

/// <summary>IAP error codes, range 12000–12099.</summary>
public static class IapErrors
{
    public static readonly ErrorCode InvalidReceipt = new(12001, "INVALID_RECEIPT", 200, false, "The purchase could not be verified.");
    public static readonly ErrorCode StoreUnavailable = new(12002, "STORE_UNAVAILABLE", 503, true, "The store is temporarily unavailable. Retry later.");

    public static ErrorCodeSet All { get; } = new(12000, 12099, InvalidReceipt, StoreUnavailable);
}

public sealed record IapPurchaseCommand(VerifiedPurchase Purchase);

public sealed record IapPurchaseResult(IReadOnlyList<IapGrant> Granted);

/// <summary>
/// <c>iap.purchase</c> (SMP-04, IAP-02, ECO-08): grants the catalog items once per external reference. The claim marker
/// <c>extref::iap_{store}::{reference}</c> commits with the grant and the receipt.
/// </summary>
public sealed class IapPurchaseHandler(IapCatalog catalog, ResourceCatalog resources) : ICommandHandler<IapPurchaseCommand, IapPurchaseResult>
{
    public const string Operation = "iap.purchase";
    public const string LedgerSourceType = "iap";

    public static CommandDescriptor Descriptor { get; } =
        new(Operation, touches: [EconomyAggregates.Wallet, EconomyAggregates.Inventory], maxClaimMarkers: 1);

    public async ValueTask<CommandOutcome<IapPurchaseResult>> HandleAsync(IapPurchaseCommand request, ICommandContext context)
    {
        var purchase = request.Purchase;
        if (!purchase.Valid || catalog.Find(purchase.Store, purchase.ProductId) is not { } grants)
        {
            return CommandOutcome<IapPurchaseResult>.Reject(IapErrors.InvalidReceipt);
        }

        if (!await EconomyOperations.TryClaimExternalAsync(context, "iap_" + purchase.Store, purchase.ExternalReference))
        {
            return CommandOutcome<IapPurchaseResult>.Reject(EconomyErrors.AlreadyGranted);
        }

        var source = new LedgerSource(LedgerSourceType, purchase.ExternalReference, purchase.OrderId);
        foreach (var grant in grants)
        {
            if (EconomyOperations.Grant(context, resources, grant.Resource, grant.Quantity, source) is { } error)
            {
                return CommandOutcome<IapPurchaseResult>.Reject(error);
            }
        }

        // Entry index = number of grants (ledger entries come first): deterministic across transaction attempts.
        context.AddAnalyticsEvent(IapAnalytics.Payment, IapAnalytics.Fields(purchase));
        return CommandOutcome<IapPurchaseResult>.Ok(new IapPurchaseResult(grants));
    }
}

public static class IapModuleDefinition
{
    public const string ModuleName = "iap";

    public static GameServer.Modules.ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.1.0",
        DependsOn = [EconomyModuleDefinition.ModuleName],
        RequiredConfigFiles = [IapCatalog.ConfigFile],
        Commands = [Command.Define<IapPurchaseCommand, IapPurchaseResult>(IapPurchaseHandler.Descriptor).HandledBy<IapPurchaseHandler>()],
        ErrorCodes = IapErrors.All,
    };
}
