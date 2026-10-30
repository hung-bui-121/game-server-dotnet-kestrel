using GameServer.Platform.Analytics;
using GameServer.Time;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Observability.Analytics;

/// <summary>
/// Readiness check <c>analytics</c> (FR-027, R10): Degraded when the analytics directory has less free space than
/// <c>LowDiskBytes</c> or a sink error happened within <c>ErrorWindowSeconds</c> (server clock); never Unhealthy (gameplay
/// continues). State is this host's pipeline, never process-wide. The data is shown to GM only (<c>ops.read</c>).
/// </summary>
public sealed class AnalyticsHealthCheck(AnalyticsPipeline pipeline, AnalyticsFileSink sink, IOptions<AnalyticsOptions> options, IClock clock) : IHealthCheck
{
    private const int MaxErrorLength = 500;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!pipeline.IsActive)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Analytics inactive."));
        }

        var settings = options.Value;
        var status = pipeline.Status;
        var data = new Dictionary<string, object>
        {
            ["sinkErrors"] = status.SinkErrors,
            ["dropped"] = status.Dropped,
            ["queue"] = status.QueueLength,
        };
        if (status.LastError is { } lastError)
        {
            data["lastError"] = lastError.Length > MaxErrorLength ? lastError[..MaxErrorLength] : lastError;
        }

        var free = sink.FreeBytes();
        if (free is { } bytes)
        {
            data["freeBytes"] = bytes;
            if (bytes < settings.LowDiskBytes)
            {
                return Task.FromResult(HealthCheckResult.Degraded("Analytics disk nearly full.", data: data));
            }
        }

        var recentError = status.LastErrorAtUtc is { } at && clock.UtcNow - at < TimeSpan.FromSeconds(settings.ErrorWindowSeconds);
        return Task.FromResult(recentError
            ? HealthCheckResult.Degraded("Recent analytics sink errors.", data: data)
            : HealthCheckResult.Healthy(data: data));
    }
}
