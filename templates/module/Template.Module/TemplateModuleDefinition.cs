using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;

namespace Template.Module;

// Copy this project, rename "Template"/"template" and adjust ids, ranges and rules.

/// <summary>State of the module's player aggregate.</summary>
public sealed record TemplateState
{
    public long Points { get; init; }
}

public sealed record EarnCommand(long Amount);

public sealed record EarnResult(long Points);

/// <summary>Replaceable rule (EXT-02).</summary>
public interface ITemplatePolicy
{
    bool Allows(EarnCommand command);
}

public sealed class DefaultTemplatePolicy : ITemplatePolicy
{
    public bool Allows(EarnCommand command) => command.Amount is > 0 and <= 100;
}

public static class TemplateErrors
{
    public static readonly ErrorCode NotAllowed = new(90001, "TEMPLATE_NOT_ALLOWED", 200, false, "The action is not allowed.");

    public static ErrorCodeSet All { get; } = new(90000, 90099, NotAllowed);
}

public sealed class EarnHandler(ITemplatePolicy policy) : ICommandHandler<EarnCommand, EarnResult>
{
    public const string Operation = "template.earn";

    public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [TemplateModuleDefinition.AggregateId]);

    public ValueTask<CommandOutcome<EarnResult>> HandleAsync(EarnCommand request, ICommandContext context)
    {
        if (!policy.Allows(request))
        {
            return ValueTask.FromResult(CommandOutcome<EarnResult>.Reject(TemplateErrors.NotAllowed));
        }

        var state = context.Get<TemplateState>(TemplateModuleDefinition.AggregateId);
        var next = state with { Points = state.Points + request.Amount };
        context.Set(TemplateModuleDefinition.AggregateId, next);
        return ValueTask.FromResult(CommandOutcome<EarnResult>.Ok(new EarnResult(next.Points)));
    }
}

public static class TemplateModuleDefinition
{
    public const string ModuleName = "template.module";
    public const string AggregateId = "template";

    public static PlayerAggregateDescriptor<TemplateState> Aggregate { get; } =
        new(AggregateId, schemaVersion: 1, clientVisible: true, createDefault: () => new TemplateState());

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        DependsOn = ["identity"],
        Aggregates = [Aggregate],
        Commands = [Command.Define<EarnCommand, EarnResult>(EarnHandler.Descriptor).HandledBy<EarnHandler>()],
        ErrorCodes = TemplateErrors.All,
    };
}
