using GameServer.Commands;

namespace Sample.Counter;

/// <summary>Replaceable rule (EXT-02): maximum amount per increment.</summary>
public interface ICounterLimitPolicy
{
    long MaxPerIncrement { get; }
}

public sealed class DefaultCounterLimitPolicy : ICounterLimitPolicy
{
    public long MaxPerIncrement => 10;
}

/// <summary>Outbox payload of kind <c>counter_log</c>.</summary>
public sealed record CounterLogEntry(long By, string? Note, long NewValue);

/// <summary><c>counter.increment</c>: adds <c>By</c> to the counter if the policy allows it.</summary>
public sealed class IncrementHandler(ICounterLimitPolicy policy) : ICommandHandler<IncrementCommand, IncrementResult>
{
    public const string Operation = "counter.increment";
    public const string LogKind = "counter_log";

    public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [CounterAggregates.Counter]);

    public ValueTask<CommandOutcome<IncrementResult>> HandleAsync(IncrementCommand request, ICommandContext context)
    {
        if (request.By <= 0)
        {
            return ValueTask.FromResult(CommandOutcome<IncrementResult>.Reject(CounterErrors.InvalidAmount));
        }

        if (request.By > policy.MaxPerIncrement)
        {
            return ValueTask.FromResult(CommandOutcome<IncrementResult>.Reject(CounterErrors.LimitExceeded));
        }

        var state = context.Get<CounterState>(CounterAggregates.Counter);
        var next = state with { Value = checked(state.Value + request.By), Increments = state.Increments + 1 };
        context.Set(CounterAggregates.Counter, next);
        context.AddOutbox(LogKind, new CounterLogEntry(request.By, request.Note, next.Value));
        return ValueTask.FromResult(CommandOutcome<IncrementResult>.Ok(new IncrementResult(next.Value)));
    }
}
