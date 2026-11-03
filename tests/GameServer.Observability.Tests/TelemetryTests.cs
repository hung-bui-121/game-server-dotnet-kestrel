using System.Diagnostics;
using GameServer.Platform;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>One trace per command, OBS-01 metrics, no high-cardinality labels (ACC-41, OBS-02).</summary>
public sealed class TelemetryTests
{
    [Fact]
    public async Task A_command_is_one_trace_from_http_to_the_runner_with_the_request_id()
    {
        await using var observed = await ObservedHost.StartAsync();
        var login = await observed.Host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var player = new GamePlayer(observed.Host.BearerClient(login.SessionToken), login.PlayerId);
        var requestId = Guid.NewGuid().ToString("D");
        await player.CommandAsync(GameServer.Modules.Economy.CheatModule.GrantRoute, new GameServer.Modules.Economy.Protocol.V1.CheatGrantRequest { Resource = "gold", Quantity = 7 }, requestId);

        var command = observed.Activities.Single(a => a.DisplayName == "command cheat.grant");
        var http = observed.Activities.Single(a => a.TraceId == command.TraceId && a.Source.Name == "Microsoft.AspNetCore");
        command.ParentSpanId.ShouldBe(http.SpanId);
        http.GetTagItem("gs.request_id").ShouldBe(requestId);
        command.GetTagItem("gs.request_id").ShouldBe(requestId);
        command.GetTagItem("gs.player_id").ShouldBe(login.PlayerId);
    }

    [Fact]
    public async Task Obs01_metrics_are_recorded_without_player_or_request_labels()
    {
        await using var observed = await ObservedHost.StartAsync();
        var login = await observed.Host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var player = new GamePlayer(observed.Host.BearerClient(login.SessionToken), login.PlayerId);
        await player.CheatAsync("gold", 3);
        await observed.Host.Client().GetAsync("/api/v1/player/snapshot"); // 401 → http_errors_total

        observed.Host.Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();
        var names = observed.Metrics.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "txn_attempts_total", "txn_duration_seconds", "receipt_size_bytes", "aggregate_size_bytes",
                     "command_results_total", "http_errors_total", "config_version_info", "login_total",
                     "http.server.request.duration", "log_sink_errors_total", "log_dropped_total",
                 })
        {
            names.ShouldContain(expected);
        }

        var results = observed.Metrics.Last(m => m.Name == "command_results_total");
        results.MetricPoints.ShouldContain(p => Tag(p, "operation") == "cheat.grant" && Tag(p, "code") == "OK");

        var tagKeys = observed.Metrics.SelectMany(m => m.MetricPoints).SelectMany(Tags).Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "playerId", "player_id", "requestId", "request_id", "gs.player_id", "gs.request_id" })
        {
            tagKeys.ShouldNotContain(forbidden);
        }
    }

    /// <summary>Slice 013 T063 (FR-053, US6-4): exported spans and metrics carry the game and framework versions on the resource.</summary>
    [Fact]
    public async Task Exported_spans_and_metrics_carry_service_version_and_the_framework_version()
    {
        var spans = new ResourceCapture<Activity>();
        var metrics = new ResourceCapture<Metric>();
        await using var observed = await ObservedHost.StartAsync(services: s =>
        {
            s.ConfigureOpenTelemetryTracerProvider(t => t.AddProcessor(new SimpleActivityExportProcessor(spans)));
            s.ConfigureOpenTelemetryMeterProvider(m => m.AddReader(new BaseExportingMetricReader(metrics)));
        });
        var runtime = observed.Host.Services.GetRequiredService<GameServerRuntimeInfo>();
        await observed.Host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        observed.Host.Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();

        foreach (var resource in new[] { spans.Resource, metrics.Resource })
        {
            resource.ShouldNotBeNull("nothing was exported");
            var attributes = resource.Attributes.ToDictionary(a => a.Key, a => a.Value);
            attributes["service.name"].ShouldBe("game-server-" + runtime.GameId);
            attributes["service.instance.id"].ShouldBe("it-1");
            attributes["service.version"].ShouldBe(runtime.GameVersion);
            attributes["gameserver.framework.version"].ShouldBe(GameServerVersion.FrameworkInformational);
            attributes["deployment.environment.name"].ShouldBe(runtime.EnvironmentId);
        }

        runtime.GameVersion.ShouldNotBe("unknown");
    }

    private static IEnumerable<KeyValuePair<string, object?>> Tags(MetricPoint point)
    {
        foreach (var tag in point.Tags)
        {
            yield return tag;
        }
    }

    private static string? Tag(MetricPoint point, string key) =>
        Tags(point).FirstOrDefault(t => t.Key == key).Value?.ToString();

    /// <summary>Remembers the resource of the provider that exported a batch, as the OTLP exporter reads it.</summary>
    private sealed class ResourceCapture<T> : BaseExporter<T>
        where T : class
    {
        public Resource? Resource { get; private set; }

        public override ExportResult Export(in Batch<T> batch)
        {
            if (batch.Count > 0)
            {
                Resource = ParentProvider?.GetResource();
            }

            return ExportResult.Success;
        }
    }
}
