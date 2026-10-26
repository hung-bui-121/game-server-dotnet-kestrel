using GameServer.Aggregates;
using GameServer.Config;
using GameServer.Errors;

namespace SampleGame;

/// <summary>Aggregate <c>progression</c>: <see cref="Stage"/> is the highest unlocked stage.</summary>
public sealed record ProgressionState
{
    public int Level { get; init; } = 1;

    public long Xp { get; init; }

    public int Stage { get; init; } = 1;

    public const int XpPerLevel = 100;

    public ProgressionState AddXp(long xp)
    {
        var total = checked(Xp + xp);
        return this with { Xp = total, Level = 1 + (int)(total / XpPerLevel) };
    }
}

public sealed record RewardLine(string Resource, long Quantity);

/// <summary>The single open battle (TBD-02). Rewards are captured at start so later config changes do not alter them (PLY-05).</summary>
public sealed record ActiveBattle
{
    public required string BattleId { get; init; }

    public required int Stage { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required long Seed { get; init; }

    public required string ConfigVersion { get; init; }

    public required long Xp { get; init; }

    public IReadOnlyList<RewardLine> Rewards { get; init; } = [];

    public IReadOnlyList<RewardLine> Drops { get; init; } = [];
}

public sealed record BattleStats
{
    public int Started { get; init; }

    public int Won { get; init; }

    public int Lost { get; init; }

    public int Abandoned { get; init; }

    public int Expired { get; init; }
}

/// <summary>Aggregate <c>battle</c> (8.4).</summary>
public sealed record BattleState
{
    public ActiveBattle? ActiveBattle { get; init; }

    public BattleStats Stats { get; init; } = new();
}

public sealed record StageDefinition(int Stage, long Xp, IReadOnlyList<RewardLine> Rewards, IReadOnlyList<RewardLine> Drops);

/// <summary>Battle table from <c>battle.json</c>.</summary>
public sealed class BattleCatalog
{
    public const string ConfigFile = "battle.json";

    public required int MaxStage { get; init; }

    public int BattleHours { get; init; } = 24;

    public required IReadOnlyDictionary<int, StageDefinition> Stages { get; init; }

    public static BattleCatalog Load(IGameConfig config)
    {
        var file = config.GetFile<BattleFile>(ConfigFile);
        return new BattleCatalog
        {
            MaxStage = file.MaxStage,
            BattleHours = file.BattleHours ?? 24,
            Stages = file.Stages.ToDictionary(s => s.Stage),
        };
    }

    private sealed record BattleFile(int MaxStage, int? BattleHours, IReadOnlyList<StageDefinition> Stages);
}

public sealed record ShopPrice(string Resource, long Amount);

public sealed record ShopGrant(string Resource, long Quantity);

public sealed record ShopItem(ShopPrice Price, ShopGrant Grant);

/// <summary>Virtual shop from <c>shop.json</c> (SMP-05).</summary>
public sealed class ShopCatalog
{
    public const string ConfigFile = "shop.json";

    public required IReadOnlyDictionary<string, ShopItem> Items { get; init; }

    public static ShopCatalog Load(IGameConfig config) =>
        new() { Items = config.GetFile<ShopFile>(ConfigFile).Items };

    private sealed record ShopFile(Dictionary<string, ShopItem> Items);
}

/// <summary>SampleGame error codes, range 40000–40099.</summary>
public static class SampleGameErrors
{
    public static readonly ErrorCode StageLocked = new(40001, "STAGE_LOCKED", 200, false, "The stage is not unlocked.");
    public static readonly ErrorCode BattleInProgress = new(40002, "BATTLE_IN_PROGRESS", 200, false, "A battle is already in progress.");
    public static readonly ErrorCode NoActiveBattle = new(40003, "NO_ACTIVE_BATTLE", 200, false, "There is no matching battle in progress.");
    public static readonly ErrorCode BattleExpired = new(40004, "BATTLE_EXPIRED", 200, false, "The battle has expired.");
    public static readonly ErrorCode UnknownShopItem = new(40005, "UNKNOWN_SHOP_ITEM", 200, false, "The shop item does not exist.");
    public static readonly ErrorCode ResultRejected = new(40006, "BATTLE_RESULT_REJECTED", 200, false, "The battle result was not accepted.");

    public static ErrorCodeSet All { get; } = new(40000, 40099, StageLocked, BattleInProgress, NoActiveBattle, BattleExpired, UnknownShopItem, ResultRejected);
}

public static class SampleGameAggregates
{
    public const string Progression = "progression";
    public const string Battle = "battle";

    public static PlayerAggregateDescriptor<ProgressionState> ProgressionDescriptor { get; } =
        new(Progression, schemaVersion: 1, clientVisible: true, createDefault: () => new ProgressionState());

    public static PlayerAggregateDescriptor<BattleState> BattleDescriptor { get; } =
        new(Battle, schemaVersion: 1, clientVisible: true, createDefault: () => new BattleState());
}
