using System.Net;
using Couchbase;
using Couchbase.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Retry of the provisioner's cluster-manager calls (scope listing, scope and collection creation). The cluster manager answers
/// 503 "Service is temporarily unavailable" while it is busy with other manifest changes, for creates and reads alike: measured
/// on EE 7.6.12 under full-suite load (2026-10-30), <c>CreateCollectionAsync</c> with several scopes provisioned at once, and
/// <c>GetAllScopesAsync</c> after 6.5 s. Only that answer is retried; every other error propagates at once.
/// </summary>
internal static class CouchbaseManagementRetry
{
    /// <summary>Attempts of one call (also of an index statement); with <see cref="Backoff"/> about 22 s of waiting in total.</summary>
    public const int Attempts = 8;

    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    /// <summary>Wait before attempt <paramref name="attempt"/> + 1: doubles from <paramref name="initial"/>, capped at <see cref="MaxBackoff"/>.</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan initial)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var doublings = Math.Min(attempt - 1, 30);
        var delay = initial * Math.Pow(2, doublings);
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    public static bool IsServiceUnavailable(Exception exception) =>
        exception is CouchbaseException { Context: ManagementErrorContext { HttpStatus: HttpStatusCode.ServiceUnavailable } };

    /// <summary>
    /// Runs <paramref name="call"/>, retrying a 503 of the cluster manager up to <see cref="Attempts"/> times. The last 503 and any
    /// other exception propagate unchanged. <paramref name="initialBackoff"/> defaults to <see cref="InitialBackoff"/> (tests shorten it).
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> call, ILogger logger, CancellationToken cancellationToken, TimeSpan? initialBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(logger);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (CouchbaseException ex) when (attempt < Attempts && IsServiceUnavailable(ex))
            {
                var delay = Backoff(attempt, initialBackoff ?? InitialBackoff);
                logger.LogWarning(
                    "Cluster manager temporarily unavailable (HTTP 503, attempt {Attempt} of {Attempts}); retrying in {DelayMs} ms.",
                    attempt,
                    Attempts,
                    (long)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
