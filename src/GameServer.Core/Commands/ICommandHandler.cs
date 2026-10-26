using GameServer.Actors;
using GameServer.Config;
using GameServer.Errors;

namespace GameServer.Commands;

/// <summary>
/// Business logic of a command. It must be pure with respect to <see cref="ICommandContext"/>:
/// no HTTP calls or other side effects, because the transaction may run it several times (CMD-04).
/// </summary>
public interface ICommandHandler<in TRequest, TResult>
{
    ValueTask<CommandOutcome<TResult>> HandleAsync(TRequest request, ICommandContext context);
}

/// <summary>Untyped view of a command outcome.</summary>
public interface ICommandOutcome
{
    /// <summary>True when the changes are committed (with or without a business code).</summary>
    bool IsOk { get; }

    object? Result { get; }

    /// <summary>
    /// Business code returned with HTTP 200: the rejection reason when <see cref="IsOk"/> is false, or the code of a
    /// committed outcome (e.g. BATTLE_EXPIRED); null means OK.
    /// </summary>
    ErrorCode? Error { get; }
}

/// <summary>Result of a handler: success with a result, or a business rejection (HTTP 200 + code).</summary>
public readonly record struct CommandOutcome<TResult> : ICommandOutcome
{
    private CommandOutcome(bool isOk, TResult? result, ErrorCode? error)
    {
        IsOk = isOk;
        Result = result;
        Error = error;
    }

    public bool IsOk { get; }

    public TResult? Result { get; }

    public ErrorCode? Error { get; }

    object? ICommandOutcome.Result => Result;

    public static CommandOutcome<TResult> Ok(TResult result) => new(true, result, null);

    /// <summary>Commits the changes but reports a business code (HTTP 200), e.g. an expired battle that is cleared.</summary>
    public static CommandOutcome<TResult> Commit(TResult result, ErrorCode code) => new(true, result, code);

    /// <summary>Rejects the command: nothing is written and no receipt is created.</summary>
    public static CommandOutcome<TResult> Reject(ErrorCode error) => new(false, default, error);
}

/// <summary>Everything a handler may use. Writes are buffered and committed in one transaction (DAT-04).</summary>
public interface ICommandContext
{
    ActorContext Actor { get; }

    /// <summary>Server time fixed for this attempt.</summary>
    DateTimeOffset Now { get; }

    string ConfigVersion { get; }

    IGameConfig Config { get; }

    /// <summary>Returns a fresh copy of an aggregate declared in Touches or Reads (default state if missing).</summary>
    TState Get<TState>(string aggregateId)
        where TState : class;

    /// <summary>Replaces the state of an aggregate declared in Touches.</summary>
    void Set<TState>(string aggregateId, TState state)
        where TState : class;

    /// <summary>
    /// Reserves an insert-only claim marker. Returns false if the marker already exists.
    /// The marker is inserted only if the command commits.
    /// </summary>
    ValueTask<bool> TryClaimAsync(string collection, string claimKey);

    /// <summary>Adds an outbox entry committed inside the receipt (JOB-02).</summary>
    void AddOutbox(string kind, object payload);

    /// <summary>
    /// Deterministic id for <paramref name="purpose"/>: identical across transaction attempts and request retries of this
    /// command (derived from the receipt), different for every other command.
    /// </summary>
    string NewId(string purpose);

    /// <summary>Deterministic 64-bit seed for <paramref name="purpose"/> (PLY-05: server-issued randomness).</summary>
    long NewSeed(string purpose);
}
