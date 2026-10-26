using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Observability;

/// <summary>
/// Shares one readiness evaluation between callers for <see cref="ObservabilityOptions.ReadyCacheMilliseconds"/>: the
/// Public <c>/health/ready</c> can be hit by anyone, so a flood must not become a flood of store probes (OBS-03).
/// </summary>
public sealed class HealthReportCache(HealthCheckService health, IOptions<ObservabilityOptions> options)
{
    private readonly Lock _lock = new();
    private Task<HealthReport>? _current;
    private long _startedAt;

    public Task<HealthReport> GetReadyAsync()
    {
        var ttl = TimeSpan.FromMilliseconds(options.Value.ReadyCacheMilliseconds);
        lock (_lock)
        {
            if (_current is not null && (!_current.IsCompleted || Stopwatch.GetElapsedTime(_startedAt) < ttl))
            {
                return _current;
            }

            // Not tied to a request token: one caller disconnecting must not cancel the shared evaluation.
            _current = health.CheckHealthAsync(check => check.Tags.Contains(ObservabilityModule.ReadyTag), CancellationToken.None);
            _startedAt = Stopwatch.GetTimestamp();
            return _current;
        }
    }
}
