using GameServer.Aggregates;
using GameServer.Errors;

namespace Sample.Counter;

/// <summary>State of aggregate <c>counter</c>. Schema v2 added <see cref="Increments"/>.</summary>
public sealed record CounterState
{
    public long Value { get; init; }

    public int Increments { get; init; }
}

public sealed record IncrementCommand(long By, string? Note);

public sealed record IncrementResult(long Value);

public static class CounterAggregates
{
    public const string Counter = "counter";

    /// <summary>v1 → v2: <c>increments</c> did not exist; start at 0 (DAT-02).</summary>
    public static IAggregateUpgrader V1ToV2 { get; } = new DelegateAggregateUpgrader(1, state =>
    {
        state["increments"] ??= 0;
        return state;
    });

    public static PlayerAggregateDescriptor<CounterState> CounterDescriptor { get; } =
        new(Counter, schemaVersion: 2, clientVisible: true, createDefault: () => new CounterState(), upgraders: [V1ToV2]);
}

/// <summary>Error codes of the module, range 10000–10099 (API-11).</summary>
public static class CounterErrors
{
    public static readonly ErrorCode LimitExceeded = new(10001, "COUNTER_LIMIT_EXCEEDED", 200, false, "The increment exceeds the allowed amount.");
    public static readonly ErrorCode InvalidAmount = new(10002, "COUNTER_INVALID_AMOUNT", 200, false, "The increment must be positive.");

    public static ErrorCodeSet All { get; } = new(10000, 10099, LimitExceeded, InvalidAmount);
}
