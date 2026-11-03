using GameServer.Modules;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Observability;

/// <summary>
/// Health endpoints (OBS-03): <c>/health/live</c> runs no dependency check (no restart storms when the store is down);
/// <c>/health/ready</c> runs the <c>ready</c> checks. Both are Public and return only the status word; details are
/// served to GM with <c>ops.read</c> (module gm).
/// </summary>
public sealed class ObservabilityModule : IGameModule, IHostEndpointsModule
{
    public const string ModuleName = "platform.observability";
    public const string LiveRoute = "/health/live";
    public const string ReadyRoute = "/health/ready";
    public const string ReadyTag = "ready";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.5.0",
        Routes = [ObservabilityRoutes.Live, ObservabilityRoutes.Ready],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddSingleton<HealthReportCache>();
        services.AddSingleton<IStartupCheck, ConsoleSinkCheck>();
        services.AddHealthChecks()
            .AddCheck<StoreHealthCheck>("store", tags: [ReadyTag])
            .AddCheck<ConfigHealthCheck>("config", tags: [ReadyTag])
            .AddCheck<LogHealthCheck>("log", tags: [ReadyTag])
            .AddCheck<ShutdownHealthCheck>("shutdown", tags: [ReadyTag]);
    }

    public void MapHostEndpoints(IEndpointRouteBuilder routes)
    {
        routes.MapHealthChecks(LiveRoute, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteStatusOnly })
            .AsPublicEndpoint(protobuf: false)
            .WithDeclaration(ObservabilityRoutes.Live);
        routes.MapDeclared(ObservabilityRoutes.Ready, async (HttpContext http, HealthReportCache cache) =>
            {
                var report = await cache.GetReadyAsync();
                http.Response.StatusCode = report.Status == HealthStatus.Unhealthy
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;
                await WriteStatusOnly(http, report);
            })
            .AsPublicEndpoint(protobuf: false);
    }

    /// <summary>FR-087: the console sink is allowed outside Development/Test and announced once at startup.</summary>
    private sealed class ConsoleSinkCheck(IOptions<ObservabilityOptions> observability, GameServerOptions options) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            if (observability.Value.Logging.Console.Enabled && !options.AllowsDevelopmentTools)
            {
                result.Warnings.Add(
                    $"[observability] Observability:Logging:Console:Enabled=true in deployment environment '{options.DeploymentEnvironment}': " +
                    "every operational event at or above Observability:Logging:Console:MinimumLevel is also written to stdout " +
                    "(bounded queue, drops counted in log_dropped_total); the log files are unchanged (FR-087, FR-088).");
            }
        }
    }

    /// <summary>Public profile: status only, never dependency names or errors (OBS-03, ACC-51).</summary>
    private static Task WriteStatusOnly(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(report.Status.ToString());
    }
}
