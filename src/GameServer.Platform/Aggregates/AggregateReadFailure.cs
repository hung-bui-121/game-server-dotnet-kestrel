using System.Diagnostics.CodeAnalysis;
using GameServer.Errors;

namespace GameServer.Platform.Aggregates;

/// <summary>Why a stored player aggregate could not reach a reader (data-model §2.1).</summary>
public enum AggregateReadFailureReason
{
    /// <summary>Stored <c>schemaVersion</c> is newer than the code's (a rolling deploy); retryable.</summary>
    VersionAhead,

    /// <summary>An upgrader is missing for a step, or threw.</summary>
    UpgradeFailed,

    /// <summary>An envelope field has the wrong type, the state is not a JSON object, or it does not deserialize into the state type.</summary>
    Unreadable,

    /// <summary>The wire mapper threw for the loaded state (Platform.Web).</summary>
    MapperFailed,
}

/// <summary>
/// One failed read of a stored aggregate (FG-029). Carries names and versions only, never state content or an exception message
/// (API-04). <see cref="ToException"/> is how a reader that cannot degrade refuses with the typed code.
/// </summary>
/// <param name="ExceptionType">Full name of the exception that broke the read; empty for <see cref="AggregateReadFailureReason.VersionAhead"/>.</param>
/// <param name="StoredSchemaVersion">The document's <c>schemaVersion</c>; 0 when the envelope cannot be read.</param>
/// <param name="CodeSchemaVersion">The descriptor's schema version.</param>
public sealed record AggregateReadFailure(
    string AggregateId,
    string PlayerId,
    AggregateReadFailureReason Reason,
    string ExceptionType,
    int StoredSchemaVersion,
    int CodeSchemaVersion)
{
    /// <summary>Retry delay of <c>AGGREGATE_VERSION_AHEAD</c> (errors-and-report-lines §1).</summary>
    public const int VersionAheadRetryAfterMs = 1000;

    /// <summary>The <c>details</c> key naming the aggregate.</summary>
    public const string AggregateDetail = "aggregate";

    /// <summary><see cref="Exception.Data"/> key of the reported marker (the error middleware writes no second line, research R5).</summary>
    private const string MarkerKey = "GameServer.AggregateReadFailure";

    /// <summary><c>AGGREGATE_VERSION_AHEAD</c> for <see cref="AggregateReadFailureReason.VersionAhead"/>, else <c>AGGREGATE_UNREADABLE</c>.</summary>
    public ErrorCode Code => Reason == AggregateReadFailureReason.VersionAhead ? CoreErrorCodes.AggregateVersionAhead : CoreErrorCodes.AggregateUnreadable;

    /// <summary><c>version_ahead</c> | <c>upgrade_failed</c> | <c>unreadable</c> | <c>mapper_failed</c> (log and metric name).</summary>
    public string ReasonName => Reason switch
    {
        AggregateReadFailureReason.VersionAhead => "version_ahead",
        AggregateReadFailureReason.UpgradeFailed => "upgrade_failed",
        AggregateReadFailureReason.Unreadable => "unreadable",
        _ => "mapper_failed",
    };

    /// <summary>
    /// <see cref="GameServerException"/> with <see cref="Code"/>, details <c>aggregate</c> only, the retry delay for
    /// <see cref="AggregateReadFailureReason.VersionAhead"/>, no inner exception (an upgrader's message may hold state content), and
    /// marked as a reported read failure (<see cref="TryGet"/>).
    /// </summary>
    public GameServerException ToException()
    {
        var exception = new GameServerException(Code, $"Aggregate '{AggregateId}' of player {PlayerId} is unavailable: {ReasonName}.")
        {
            Details = new Dictionary<string, string>(StringComparer.Ordinal) { [AggregateDetail] = AggregateId },
            RetryAfterMs = Reason == AggregateReadFailureReason.VersionAhead ? VersionAheadRetryAfterMs : null,
        };
        exception.Data[MarkerKey] = this;
        return exception;
    }

    /// <summary>The failure carried by an exception from <see cref="ToException"/>; false for any other exception.</summary>
    public static bool TryGet(Exception exception, [NotNullWhen(true)] out AggregateReadFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(exception);
        failure = exception is GameServerException ? exception.Data[MarkerKey] as AggregateReadFailure : null;
        return failure is not null;
    }
}
