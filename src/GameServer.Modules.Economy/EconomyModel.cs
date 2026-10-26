using GameServer.Aggregates;
using GameServer.Config;
using GameServer.Errors;

namespace GameServer.Modules.Economy;

/// <summary>Aggregate <c>wallet</c>: currency balances (resource id → amount).</summary>
public sealed record WalletState
{
    public IReadOnlyDictionary<string, long> Balances { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);
}

/// <summary>Aggregate <c>inventory</c>: stackable items (item id → quantity). Instanced items are P1 (ECO-07).</summary>
public sealed record InventoryState
{
    public IReadOnlyDictionary<string, long> Items { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);
}

public enum ResourceKind
{
    Currency,
    Item,
}

/// <summary>Resource Catalog entry (ECO-07). Data is owned by the game (config file), schema by the framework.</summary>
public sealed record ResourceDefinition(string Id, ResourceKind Kind, long MaxQuantity, string DisplayName);

/// <summary>Resource Catalog loaded from <c>resources.json</c> (CFG-01).</summary>
public sealed class ResourceCatalog
{
    public const string ConfigFile = "resources.json";

    private readonly Dictionary<string, ResourceDefinition> _resources;

    public ResourceCatalog(IEnumerable<ResourceDefinition> resources)
    {
        _resources = resources.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ResourceDefinition> All => _resources.Values;

    public ResourceDefinition? Find(string id) => _resources.GetValueOrDefault(id);

    public static ResourceCatalog Load(IGameConfig config) =>
        new(config.GetFile<ResourcesFile>(ConfigFile).Resources);

    private sealed record ResourcesFile(IReadOnlyList<ResourceDefinition> Resources);
}

/// <summary>Economy error codes, range 11000–11099 (API-11).</summary>
public static class EconomyErrors
{
    public static readonly ErrorCode InsufficientFunds = new(11001, "INSUFFICIENT_FUNDS", 200, false, "Not enough resources.");
    public static readonly ErrorCode UnknownItem = new(11002, "UNKNOWN_ITEM", 200, false, "The item is not in the resource catalog.");
    public static readonly ErrorCode QuantityLimitExceeded = new(11003, "QUANTITY_LIMIT_EXCEEDED", 200, false, "The quantity limit would be exceeded.");
    public static readonly ErrorCode InvalidQuantity = new(11004, "INVALID_QUANTITY", 200, false, "The quantity must be positive.");
    public static readonly ErrorCode AlreadyGranted = new(11005, "ALREADY_GRANTED", 200, false, "This purchase or grant was already applied.");

    public static ErrorCodeSet All { get; } = new(11000, 11099, InsufficientFunds, UnknownItem, QuantityLimitExceeded, InvalidQuantity, AlreadyGranted);
}

public static class EconomyAggregates
{
    public const string Wallet = "wallet";
    public const string Inventory = "inventory";

    public static PlayerAggregateDescriptor<WalletState> WalletDescriptor { get; } =
        new(Wallet, schemaVersion: 1, clientVisible: true, createDefault: () => new WalletState());

    public static PlayerAggregateDescriptor<InventoryState> InventoryDescriptor { get; } =
        new(Inventory, schemaVersion: 1, clientVisible: true, createDefault: () => new InventoryState());
}

/// <summary>Why a resource changed (ECO-02): source type and optional external references.</summary>
public sealed record LedgerSource(string SourceType, string? ExternalReference = null, string? OrderId = null);

/// <summary>Ledger outbox payload (ECO-06). entryId, receiptId and commit time are added by the projection.</summary>
public sealed record LedgerEntry(
    string PlayerId,
    string ActorId,
    string SourceType,
    string Resource,
    long Delta,
    long BalanceAfter,
    string? ExternalReference,
    string? OrderId);
