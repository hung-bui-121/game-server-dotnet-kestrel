using GameServer.Platform.Analytics;
using GameServer.Platform.Web.Hosting;
using GameServer.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Observability.Analytics;

public static class AnalyticsFileExtensions
{
    /// <summary>Name of the readiness check.</summary>
    public const string HealthCheckName = "analytics";

    /// <summary>
    /// Default analytics sink (owner Q8, D18): one JSONL file per instance, event type and UTC day under
    /// <c>GameServer:Analytics:Directory</c> (relative to the content root), readiness check <c>analytics</c> and the gauge
    /// <c>analytics_dir_free_bytes</c>. Instance id = <c>Observability:InstanceId</c> → <c>HOSTNAME</c> → machine name (LOG-03).
    /// Idempotent: a second call registers nothing (one sink, one lock, one health check).
    /// </summary>
    public static GameServerBuilder AddAnalyticsFiles(this GameServerBuilder gameServer)
    {
        ArgumentNullException.ThrowIfNull(gameServer);
        if (gameServer.Services.Any(d => d.ServiceType == typeof(AnalyticsFileSink)))
        {
            return gameServer;
        }

        var host = gameServer.Host;
        var observability = host.Configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        var instanceId = observability.ResolveInstanceId();
        var contentRoot = host.Environment.ContentRootPath;
        var gameId = gameServer.Options.GameId;
        var services = gameServer.Services;

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
            var directory = Path.GetFullPath(Path.IsPathRooted(options.Directory) ? options.Directory : Path.Combine(contentRoot, options.Directory));
            var active = sp.GetRequiredService<AnalyticsActivation>().IsActive(options);
            var sink = new AnalyticsFileSink(
                directory,
                gameId,
                instanceId,
                active,
                options.RetainLocalDays,
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<AnalyticsFileSink>>());
            if (active)
            {
                sp.GetRequiredService<AnalyticsMetrics>().ObserveDirectoryFreeBytes(sink.FreeBytes);
            }

            return sink;
        });
        services.AddSingleton<IAnalyticsSink>(sp => sp.GetRequiredService<AnalyticsFileSink>());
        services.AddHostedService(sp => sp.GetRequiredService<AnalyticsFileSink>());
        services.AddHealthChecks().AddCheck<AnalyticsHealthCheck>(HealthCheckName, tags: [ObservabilityModule.ReadyTag]);
        return gameServer;
    }
}
