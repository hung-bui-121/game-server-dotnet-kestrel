using GameServer.Config;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Observability;

/// <summary>Readiness: the document store answers within 2 s (OBS-03).</summary>
public sealed class StoreHealthCheck(IStoreHealthProbe probe) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await probe.CheckAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The exception type only; messages may carry connection details.
            return HealthCheckResult.Unhealthy($"Store unreachable ({ex.GetType().Name}).");
        }
    }
}

/// <summary>Readiness: the mandatory game config is loaded (OBS-03).</summary>
public sealed class ConfigHealthCheck(IGameConfig config) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy(
            "Config loaded.",
            new Dictionary<string, object> { ["configVersion"] = config.ConfigVersion }));
}

/// <summary>LOG-06 / ACC-17: Degraded after a sink error or when the log disk is nearly full; logging never fails readiness.</summary>
public sealed class LogHealthCheck(IOptions<ObservabilityOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Logging;
        var monitor = LogSinkMonitor.Instance;
        var data = new Dictionary<string, object>
        {
            ["sinkErrors"] = monitor.SinkErrors,
            ["dropped"] = monitor.DroppedCount,
        };
        if (monitor.LastError is { } lastError)
        {
            data["lastError"] = lastError;
        }

        if (monitor.FreeBytes() is { } free)
        {
            data["freeBytes"] = free;
            if (free < settings.LowDiskBytes)
            {
                return Task.FromResult(HealthCheckResult.Degraded("Log disk nearly full.", data: data));
            }
        }

        return Task.FromResult(monitor.HasRecentError(TimeSpan.FromSeconds(settings.SinkErrorWindowSeconds))
            ? HealthCheckResult.Degraded("Recent log sink errors.", data: data)
            : HealthCheckResult.Healthy(data: data));
    }
}

/// <summary>OPS-03: not ready once shutdown started, so the load balancer drains this instance.</summary>
public sealed class ShutdownHealthCheck(IHostApplicationLifetime lifetime, ShutdownSignal signal) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(signal.IsStopping || lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthCheckResult.Unhealthy("Shutting down.")
            : HealthCheckResult.Healthy());
}
