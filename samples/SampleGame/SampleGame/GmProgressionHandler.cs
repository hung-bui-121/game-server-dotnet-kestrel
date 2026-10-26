using GameServer.Actors;
using GameServer.Commands;

namespace SampleGame;

/// <summary>GM permissions owned by the SampleGame module.</summary>
public static class SampleGamePermissions
{
    public const string ProgressionEdit = "progression.edit";
}

/// <summary>GM edit of progression; null fields keep their current value.</summary>
public sealed record SetProgressionCommand(int? Level, long? Xp, int? Stage, string Reason);

public sealed record SetProgressionResult(ProgressionState Before, ProgressionState After);

/// <summary>
/// <c>progression.set</c>: GM sets level/xp/stage of the target player. Progression has no ledger, so the audit
/// records before and after (SEC-14).
/// </summary>
public sealed class SetProgressionHandler(BattleCatalog battles) : ICommandHandler<SetProgressionCommand, SetProgressionResult>
{
    public const string Operation = "progression.set";

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [SampleGameAggregates.Progression],
        profile: SecurityProfile.Gm,
        permission: SampleGamePermissions.ProgressionEdit);

    public ValueTask<CommandOutcome<SetProgressionResult>> HandleAsync(SetProgressionCommand request, ICommandContext context)
    {
        // Range checks of the body happen in the endpoint; the stage bound depends on config.
        if (request.Stage > battles.MaxStage)
        {
            return ValueTask.FromResult(CommandOutcome<SetProgressionResult>.Reject(SampleGameErrors.StageLocked));
        }

        var before = context.Get<ProgressionState>(SampleGameAggregates.Progression);
        var after = before with
        {
            Level = request.Level ?? before.Level,
            Xp = request.Xp ?? before.Xp,
            Stage = request.Stage ?? before.Stage,
        };
        context.Set(SampleGameAggregates.Progression, after);
        context.AddAudit(SampleGamePermissions.ProgressionEdit, Operation, request.Reason, before, after);
        return ValueTask.FromResult(CommandOutcome<SetProgressionResult>.Ok(new SetProgressionResult(before, after)));
    }
}
