using GameServer.Actors;
using GameServer.Errors;

namespace GameServer.Platform.Commands;

/// <summary>A decoded command ready to run (API-07).</summary>
/// <param name="Operation">Registered operation name.</param>
/// <param name="Actor">Authenticated actor; <see cref="ActorContext.PlayerId"/> is the target player.</param>
/// <param name="RequestId">Client-generated UUID from <c>X-Request-Id</c>.</param>
/// <param name="BodyHash">SHA-256 of the raw request bytes (CMD-02).</param>
/// <param name="Request">Domain request (never a wire type, ARC-02).</param>
/// <param name="EncodeResult">Encodes the domain result to the bytes stored as resultCompact and returned to the client.</param>
public sealed record CommandInvocation(
    string Operation,
    ActorContext Actor,
    string RequestId,
    string BodyHash,
    object Request,
    Func<object, byte[]> EncodeResult);

/// <summary>An aggregate changed by a command, for <c>SectionUpdate</c> (API-10).</summary>
public sealed record AggregateChange(string AggregateId, long BaseRev, long NewRev, object State);

/// <summary>Outcome of <see cref="ICommandRunner.ExecuteAsync"/>.</summary>
/// <param name="Code">OK or a business code (HTTP 200).</param>
/// <param name="ReceiptId">Receipt of a committed command; null when rejected.</param>
/// <param name="Updates">Client-visible aggregates changed by the command.</param>
/// <param name="Result">Module result bytes (empty when rejected).</param>
/// <param name="Replayed">True when returned from an already committed receipt.</param>
/// <param name="ConfigVersion">Config version of this instance.</param>
public sealed record CommandExecution(
    ErrorCode Code,
    string? ReceiptId,
    IReadOnlyList<AggregateChange> Updates,
    byte[] Result,
    bool Replayed,
    string ConfigVersion);

/// <summary>Single entry point for state-changing commands (DAT-04, CMD-01..09).</summary>
public interface ICommandRunner
{
    /// <param name="invocation">The command.</param>
    /// <param name="requestAborted">Used only before the commit step (PERF-02).</param>
    Task<CommandExecution> ExecuteAsync(CommandInvocation invocation, CancellationToken requestAborted);
}
