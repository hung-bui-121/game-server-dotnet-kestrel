using GameServer.Platform;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace GameServer.Platform.Observability;

public static class ObservabilityExtensions
{
    /// <summary>Activity sources and meters of the framework and its modules.</summary>
    public const string InstrumentationPrefix = "GameServer.*";

    /// <summary>
    /// Adds structured file logging (LOG-01..06), OpenTelemetry traces and metrics (OBS-01, OBS-02, OBS-04) and the
    /// health endpoints (OBS-03). Configuration: section <c>Observability</c>.
    /// </summary>
    public static GameServerBuilder AddObservability(this GameServerBuilder gameServer)
    {
        var host = gameServer.Host;
        var section = host.Configuration.GetSection(ObservabilityOptions.SectionName);
        var options = section.Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        host.Services.Configure<ObservabilityOptions>(section);

        Validate(options);
        var instanceId = options.ResolveInstanceId();
        AddLogging(host.Services, host.Environment.ContentRootPath, gameServer.Options, options, instanceId);
        AddTelemetry(host.Services, gameServer.Options, options, instanceId);
        return gameServer.AddModule<ObservabilityModule>();
    }

    private static void AddLogging(IServiceCollection services, string contentRoot, GameServerOptions gameServer, ObservabilityOptions options, string instanceId)
    {
        var settings = options.Logging;
        var directory = Path.IsPathRooted(settings.Directory) ? settings.Directory : Path.Combine(contentRoot, settings.Directory);
        if (settings.FileEnabled)
        {
            LogSinkMonitor.Instance.Attach(directory);
        }

        services.AddSerilog(
            (_, logger) =>
            {
                logger.MinimumLevel.Is(Enum.Parse<LogEventLevel>(settings.MinimumLevel, ignoreCase: true));
                foreach (var (source, level) in settings.LevelOverrides)
                {
                    logger.MinimumLevel.Override(source, Enum.Parse<LogEventLevel>(level, ignoreCase: true));
                }

                logger
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("InstanceId", instanceId)
                    .Enrich.WithProperty("GameId", gameServer.GameId)
                    .Enrich.WithProperty("EnvironmentId", gameServer.EnvironmentId)
                    .Enrich.With<SensitiveDataRedactor>();
                if (settings.FileEnabled)
                {
                    // One file per instance (LOG-03); never shared between processes. Day + size rolling (LOG-02).
                    var path = Path.Combine(directory, $"{Sanitize(gameServer.GameId)}-{Sanitize(instanceId)}-.log");
                    logger.WriteTo.Async(
                        sink => sink.File(
                            new CompactJsonFormatter(),
                            path,
                            fileSizeLimitBytes: settings.FileSizeLimitBytes,
                            rollingInterval: RollingInterval.Day,
                            rollOnFileSizeLimit: true,
                            retainedFileCountLimit: settings.RetainedFileCountLimit,
                            retainedFileTimeLimit: settings.RetainedFileTimeLimitDays is { } days ? TimeSpan.FromDays(days) : null,
                            shared: false),
                        bufferSize: settings.BufferSize,
                        blockWhenFull: false,
                        monitor: LogSinkMonitor.Instance);
                }
            },
            // Each host owns its logger (disposed = flushed with the container, OPS-03); the static Log.Logger is
            // never replaced, so one host stopping cannot close another host's logger. Other logging providers (the
            // default console provider waits when its queue is full) are not written to: logging never blocks (LOG-06).
            preserveStaticLogger: true,
            writeToProviders: false);
    }

    private static void AddTelemetry(IServiceCollection services, GameServerOptions gameServer, ObservabilityOptions options, string instanceId)
    {
        var otlp = options.Otlp.Endpoint is { Length: > 0 } endpoint ? new Uri(endpoint) : null;
        var protocol = string.Equals(options.Otlp.Protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        // An endpoint set in code is used as is: over HTTP the signal path must be appended explicitly.
        Uri SignalEndpoint(string signal) =>
            protocol == OtlpExportProtocol.HttpProtobuf ? new Uri(otlp!, otlp!.AbsolutePath.TrimEnd('/') + "/v1/" + signal) : otlp!;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: "game-server-" + gameServer.GameId, serviceInstanceId: instanceId)
                .AddAttributes([new("deployment.environment.name", gameServer.EnvironmentId)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(InstrumentationPrefix)
                    .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation();
                if (otlp is not null)
                {
                    tracing.AddOtlpExporter(o => (o.Endpoint, o.Protocol) = (SignalEndpoint("traces"), protocol));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(InstrumentationPrefix)
                    .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.RateLimiting")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (otlp is not null)
                {
                    metrics.AddOtlpExporter(o => (o.Endpoint, o.Protocol) = (SignalEndpoint("metrics"), protocol));
                }
            });

        // The platform meter is created by DI; make sure it exists before the first collection.
        services.AddHostedService<PlatformMetricsWarmup>();
    }

    /// <summary>Invalid settings fail startup with a readable violation instead of a raw exception.</summary>
    private static void Validate(ObservabilityOptions options)
    {
        var errors = new List<string>();
        var logging = options.Logging;
        static bool IsLevel(string? value) => Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) &&
            !int.TryParse(value, out _);

        if (!IsLevel(logging.MinimumLevel))
        {
            errors.Add($"[observability] Observability:Logging:MinimumLevel '{logging.MinimumLevel}' is not a level ({string.Join(", ", Enum.GetNames<LogEventLevel>())}).");
        }

        foreach (var (source, level) in logging.LevelOverrides)
        {
            if (string.IsNullOrWhiteSpace(source) || !IsLevel(level))
            {
                errors.Add($"[observability] Observability:Logging:LevelOverrides:{source} '{level}' is not a level ({string.Join(", ", Enum.GetNames<LogEventLevel>())}).");
            }
        }

        if (logging.FileSizeLimitBytes < 1024 || logging.RetainedFileCountLimit < 1 || logging.BufferSize < 1 || logging.RetainedFileTimeLimitDays < 1)
        {
            errors.Add("[observability] Observability:Logging limits must be positive (FileSizeLimitBytes >= 1024).");
        }

        if (options.Otlp.Endpoint is { Length: > 0 } endpoint && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            errors.Add($"[observability] Observability:Otlp:Endpoint '{endpoint}' is not an absolute URI.");
        }

        if (options.Otlp.Protocol is not ("grpc" or "http/protobuf"))
        {
            errors.Add($"[observability] Observability:Otlp:Protocol must be 'grpc' or 'http/protobuf', not '{options.Otlp.Protocol}'.");
        }

        if (errors.Count > 0)
        {
            throw new GameServerStartupException(errors);
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private sealed class PlatformMetricsWarmup(PlatformMetrics metrics) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            GC.KeepAlive(metrics);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
