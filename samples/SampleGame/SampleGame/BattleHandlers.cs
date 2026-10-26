using GameServer.Commands;
using GameServer.Modules.Economy;

namespace SampleGame;

public enum ActiveBattleDecision
{
    /// <summary>Abandon the open battle (stats.abandoned + 1) and start the new one (default, TBD-02).</summary>
    Replace,

    /// <summary>Refuse with BATTLE_IN_PROGRESS.</summary>
    Reject,
}

/// <summary>Replaceable start rule (EXT-02): what to do when a battle is still open.</summary>
public interface IBattleStartPolicy
{
    ActiveBattleDecision OnActiveBattle(ActiveBattle current, int requestedStage);
}

public sealed class ReplaceActiveBattlePolicy : IBattleStartPolicy
{
    public ActiveBattleDecision OnActiveBattle(ActiveBattle current, int requestedStage) => ActiveBattleDecision.Replace;
}

public sealed class RejectActiveBattlePolicy : IBattleStartPolicy
{
    public ActiveBattleDecision OnActiveBattle(ActiveBattle current, int requestedStage) => ActiveBattleDecision.Reject;
}

/// <summary>Replaceable evidence check. The sample is eligibility-only (G7): any reported result is accepted.</summary>
public interface IBattleResultPolicy
{
    bool Accept(ActiveBattle battle, EndBattleCommand request);
}

public sealed class EligibilityOnlyResultPolicy : IBattleResultPolicy
{
    public bool Accept(ActiveBattle battle, EndBattleCommand request) => true;
}

public sealed record StartBattleCommand(int Stage);

public sealed record StartBattleResult(string BattleId, long Seed, DateTimeOffset ExpiresAtUtc);

/// <summary><c>battle.start</c> (SMP-02): Touches battle, Reads progression.</summary>
public sealed class StartBattleHandler(BattleCatalog catalog, IBattleStartPolicy policy) : ICommandHandler<StartBattleCommand, StartBattleResult>
{
    public const string Operation = "battle.start";

    public static CommandDescriptor Descriptor { get; } =
        new(Operation, touches: [SampleGameAggregates.Battle], reads: [SampleGameAggregates.Progression]);

    public ValueTask<CommandOutcome<StartBattleResult>> HandleAsync(StartBattleCommand request, ICommandContext context)
    {
        var progression = context.Get<ProgressionState>(SampleGameAggregates.Progression);
        if (request.Stage < 1 || request.Stage > progression.Stage || !catalog.Stages.TryGetValue(request.Stage, out var stage))
        {
            return Reject(SampleGameErrors.StageLocked);
        }

        var battle = context.Get<BattleState>(SampleGameAggregates.Battle);
        var stats = battle.Stats;
        if (battle.ActiveBattle is { } open)
        {
            if (open.ExpiresAtUtc <= context.Now)
            {
                stats = stats with { Expired = stats.Expired + 1 };
            }
            else if (policy.OnActiveBattle(open, request.Stage) == ActiveBattleDecision.Reject)
            {
                return Reject(SampleGameErrors.BattleInProgress);
            }
            else
            {
                stats = stats with { Abandoned = stats.Abandoned + 1 };
            }
        }

        var active = new ActiveBattle
        {
            BattleId = context.NewId("battle"),
            Stage = request.Stage,
            StartedAtUtc = context.Now,
            ExpiresAtUtc = context.Now.AddHours(catalog.BattleHours),
            Seed = context.NewSeed("battle"),
            ConfigVersion = context.ConfigVersion,
            Xp = stage.Xp,
            Rewards = stage.Rewards,
            Drops = stage.Drops,
        };
        context.Set(SampleGameAggregates.Battle, battle with { ActiveBattle = active, Stats = stats with { Started = stats.Started + 1 } });
        return ValueTask.FromResult(CommandOutcome<StartBattleResult>.Ok(new StartBattleResult(active.BattleId, active.Seed, active.ExpiresAtUtc)));
    }

    private static ValueTask<CommandOutcome<StartBattleResult>> Reject(GameServer.Errors.ErrorCode code) =>
        ValueTask.FromResult(CommandOutcome<StartBattleResult>.Reject(code));
}

public sealed record EndBattleCommand(string BattleId, bool Won);

public sealed record EndBattleResult(IReadOnlyList<RewardLine> Rewards, long XpGained, int StageUnlocked);

/// <summary>
/// <c>battle.end</c> (SMP-03): Touches battle, progression, wallet, inventory — one transaction. Rewards come from the
/// table captured at start and are granted through Economy (<c>sourceType = battle</c>).
/// </summary>
public sealed class EndBattleHandler(BattleCatalog catalog, ResourceCatalog resources, IBattleResultPolicy policy)
    : ICommandHandler<EndBattleCommand, EndBattleResult>
{
    public const string Operation = "battle.end";
    public const string LedgerSourceType = "battle";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [SampleGameAggregates.Battle, SampleGameAggregates.Progression, EconomyAggregates.Wallet, EconomyAggregates.Inventory]);

    private static readonly EndBattleResult Nothing = new([], 0, 0);

    public ValueTask<CommandOutcome<EndBattleResult>> HandleAsync(EndBattleCommand request, ICommandContext context)
    {
        var battle = context.Get<BattleState>(SampleGameAggregates.Battle);
        if (battle.ActiveBattle is not { } active || !string.Equals(active.BattleId, request.BattleId, StringComparison.Ordinal))
        {
            return Done(CommandOutcome<EndBattleResult>.Reject(SampleGameErrors.NoActiveBattle));
        }

        if (active.ExpiresAtUtc <= context.Now)
        {
            // The expired battle is cleared and reported, nothing is granted (8.4).
            context.Set(SampleGameAggregates.Battle, battle with { ActiveBattle = null, Stats = battle.Stats with { Expired = battle.Stats.Expired + 1 } });
            return Done(CommandOutcome<EndBattleResult>.Commit(Nothing, SampleGameErrors.BattleExpired));
        }

        if (!policy.Accept(active, request))
        {
            return Done(CommandOutcome<EndBattleResult>.Reject(SampleGameErrors.ResultRejected));
        }

        if (!request.Won)
        {
            context.Set(SampleGameAggregates.Battle, battle with { ActiveBattle = null, Stats = battle.Stats with { Lost = battle.Stats.Lost + 1 } });
            return Done(CommandOutcome<EndBattleResult>.Ok(Nothing));
        }

        var source = new LedgerSource(LedgerSourceType, ExternalReference: active.BattleId);
        foreach (var line in active.Rewards.Concat(active.Drops))
        {
            if (EconomyOperations.Grant(context, resources, line.Resource, line.Quantity, source) is { } error)
            {
                return Done(CommandOutcome<EndBattleResult>.Reject(error));
            }
        }

        var progression = context.Get<ProgressionState>(SampleGameAggregates.Progression).AddXp(active.Xp);
        var unlocked = 0;
        if (active.Stage == progression.Stage && progression.Stage < catalog.MaxStage)
        {
            progression = progression with { Stage = progression.Stage + 1 };
            unlocked = progression.Stage;
        }

        context.Set(SampleGameAggregates.Progression, progression);
        context.Set(SampleGameAggregates.Battle, battle with { ActiveBattle = null, Stats = battle.Stats with { Won = battle.Stats.Won + 1 } });
        return Done(CommandOutcome<EndBattleResult>.Ok(new EndBattleResult([.. active.Rewards, .. active.Drops], active.Xp, unlocked)));
    }

    private static ValueTask<CommandOutcome<EndBattleResult>> Done(CommandOutcome<EndBattleResult> outcome) => ValueTask.FromResult(outcome);
}
