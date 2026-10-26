using System.Diagnostics;
using GameServer.Modules.Gm;
using GameServer.Platform.Observability;
using GameServer.Platform.Web.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace GameServer.Observability.Tests;

/// <summary>Full SampleGame host + GM + observability, logging into a private temp directory.</summary>
public sealed class ObservedHost : IAsyncDisposable
{
    public const string ProbeRoute = "/v1/test/log-probe";
    public const string Pepper = "pepper-SECRET-9f3a71";
    public const string StaffPassword = "staff-PASSWORD-77e1";

    private ObservedHost(TestHost host, string logDirectory, List<Activity> activities, List<MetricSnapshot> metrics)
    {
        Host = host;
        LogDirectory = logDirectory;
        Activities = activities;
        Metrics = metrics;
    }

    public TestHost Host { get; }

    public string LogDirectory { get; }

    public List<Activity> Activities { get; }

    public List<MetricSnapshot> Metrics { get; }

    public static string NewLogDirectory() => Path.Combine(Path.GetTempPath(), "gs-logs-" + Guid.NewGuid().ToString("N"));

    public static async Task<ObservedHost> StartAsync(
        string? logDirectory = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null)
    {
        logDirectory ??= NewLogDirectory();
        var activities = new List<Activity>();
        var metrics = new List<MetricSnapshot>();
        var host = await TestHost.StartAsync(
            b =>
            {
                GameHttp.FullGame(b.AddModule<GmModule>());
                b.AddObservability();
            },
            values =>
            {
                values["Observability:InstanceId"] = "it-1";
                values["Observability:Logging:Directory"] = logDirectory;
                values["Observability:Logging:MinimumLevel"] = "Debug";
                values["Identity:GuestPepper"] = Pepper;
                values["Gm:Bootstrap:Staff:0:StaffId"] = "admin";
                values["Gm:Bootstrap:Staff:0:Password"] = StaffPassword;
                values["Gm:Bootstrap:Staff:0:Roles:0"] = "Administrator";
                values["GameServer:PublicEndpointAllowlist:0"] = "/v1/time";
                values["GameServer:PublicEndpointAllowlist:1"] = ProbeRoute;
                settings?.Invoke(values);
            },
            beforeUseGameServer: app =>
            {
                app.MapGet(ProbeRoute, (ILogger<ObservedHost> logger, string? value) =>
                    {
                        // Sensitive-looking property names must be masked by the redactor (LOG-05).
                        logger.LogInformation(
                            "Probe {ProbeValue} {SessionToken} {Password} {@Detail}",
                            value,
                            "token-should-not-appear",
                            "password-should-not-appear",
                            new { ClientSecret = "nested-secret-should-not-appear", Visible = "ok" });
                        return Results.Text(value ?? string.Empty);
                    })
                    .AsPublicEndpoint(protobuf: false);
                beforeUseGameServer?.Invoke(app);
            },
            services: s =>
            {
                s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(activities));
                s.ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(metrics));
            });
        return new ObservedHost(host, logDirectory, activities, metrics);
    }

    /// <summary>Every line of every log file (call after disposing the host so the queue is flushed).</summary>
    public static string ReadAllLogs(string directory) =>
        Directory.Exists(directory)
            ? string.Join('\n', Directory.GetFiles(directory).Order(StringComparer.Ordinal).Select(ReadShared))
            : string.Empty;

    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}
