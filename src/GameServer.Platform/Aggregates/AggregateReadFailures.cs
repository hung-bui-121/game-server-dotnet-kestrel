using GameServer.Platform.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Platform.Aggregates;

/// <summary>
/// Logs (once) and counts each read failure (FR-026, research R6): the loader and the section writer are the only callers, at the
/// two places a failure is detected, so every failed read gives exactly one log entry and one metric increment.
/// </summary>
public sealed class AggregateReadFailures
{
    /// <summary>Log category of the read-failure lines (errors-and-report-lines §2).</summary>
    public const string LogCategory = "GameServer.Aggregates";

    private readonly PlatformMetrics _metrics;
    private readonly ILogger _logger;

    public AggregateReadFailures(PlatformMetrics metrics, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggers);
        _metrics = metrics;
        _logger = loggers.CreateLogger(LogCategory);
    }

    /// <summary>A reporter that only counts (direct constructions of <see cref="AggregateDocumentSerializer"/> in tests).</summary>
    internal static AggregateReadFailures CountOnly(PlatformMetrics metrics) => new(metrics, NullLoggerFactory.Instance);

    public void Report(AggregateReadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Reason == AggregateReadFailureReason.VersionAhead)
        {
            // A deploy-order state, not damage: the newer instance wrote it (PCR platform §6).
            _logger.LogWarning(
                "Aggregate {AggregateId} of player {PlayerId} is unavailable: version_ahead (stored schemaVersion {Stored}, code {Code}). " +
                "Deploy code that reads it before writing it (PCR platform §6).",
                failure.AggregateId,
                failure.PlayerId,
                failure.StoredSchemaVersion,
                failure.CodeSchemaVersion);
        }
        else
        {
            _logger.LogError(
                "Aggregate {AggregateId} of player {PlayerId} is unavailable: {Reason} ({ExceptionType}).",
                failure.AggregateId,
                failure.PlayerId,
                failure.ReasonName,
                failure.ExceptionType);
        }

        _metrics.AggregateReadFailed(failure.AggregateId, failure.ReasonName);
    }
}
