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

/// <summary>
/// Full SampleGame host + GM + observability, logging into a private temp directory. Without a directory from the caller the host
/// owns one and deletes it on dispose; a test that reads the files after the host stopped passes its own
/// <see cref="TempLogDirectory"/>.
/// </summary>
public sealed class ObservedHost : IAsyncDisposable
{
    public const string ProbeRoute = "/v1/test/log-probe";
    public const string Pepper = "pepper-SECRET-9f3a71";
    public const string StaffPassword = "staff-PASSWORD-77e1";

    private readonly TempLogDirectory? _ownedLogDirectory;

    private ObservedHost(TestHost host, string logDirectory, TempLogDirectory? ownedLogDirectory, List<Activity> activities, List<MetricSnapshot> metrics)
    {
        Host = host;
        LogDirectory = logDirectory;
        _ownedLogDirectory = ownedLogDirectory;
        Activities = activities;
        Metrics = metrics;
    }

    public TestHost Host { get; }

    public string LogDirectory { get; }

    public List<Activity> Activities { get; }

    public List<MetricSnapshot> Metrics { get; }

    public static async Task<ObservedHost> StartAsync(
        string? logDirectory = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null)
    {
        var owned = logDirectory is null ? new TempLogDirectory() : null;
        logDirectory ??= owned!.Path;
        var activities = new List<Activity>();
        var metrics = new List<MetricSnapshot>();
        TestHost host;
        try
        {
            host = await StartHostAsync(logDirectory, settings, beforeUseGameServer, activities, metrics);
        }
        catch
        {
            owned?.Dispose();
            throw;
        }

        return new ObservedHost(host, logDirectory, owned, activities, metrics);
    }

    private static Task<TestHost> StartHostAsync(
        string logDirectory,
        Action<Dictionary<string, string?>>? settings,
        Action<WebApplication>? beforeUseGameServer,
        List<Activity> activities,
        List<MetricSnapshot> metrics) =>
        TestHost.StartAsync(
            b =>
            {
                GameHttp.FullGame(b.AddModule<GmModule>());
                b.AddObservability();
            },
            values =>
            {
                values["Observability:InstanceId"] = "it-1";
                values["Observability:ReadyCacheMilliseconds"] = "0";
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

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        _ownedLogDirectory?.Dispose();
    }
}
