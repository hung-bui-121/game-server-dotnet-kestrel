using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GameServer.Analytics;
using GameServer.Platform.Analytics;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>A started pipeline over the given sinks, with a meter listener bound to this pipeline's meter only.</summary>
internal sealed class PipelineKit : IAsyncDisposable
{
    public static readonly AnalyticsEventType Login = new("login", 1, "activeTime");
    public static readonly AnalyticsEventType Payment = new("payment", 1, "chargeTime");

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Name, long Value, Dictionary<string, string?> Tags)> _measurements = new();

    private PipelineKit(AnalyticsOptions options, IAnalyticsSink[] sinks)
    {
        Options = options;
        Metrics = new AnalyticsMetrics();
        var zone = new AnalyticsTimeZone(options.TimeZone);
        var gameServer = new GameServerOptions { GameId = "g", EnvironmentId = "test" };
        Pipeline = new AnalyticsPipeline(
            Microsoft.Extensions.Options.Options.Create(options),
            new AnalyticsActivation(sinks.Length),
            sinks,
            new AnalyticsRecordFormatter(gameServer, zone, NullLogger<AnalyticsRecordFormatter>.Instance),
            zone,
            Metrics,
            Clock,
            NullLogger<AnalyticsPipeline>.Instance);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, Metrics.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()))));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()))));
        _listener.Start();
    }

    public AnalyticsOptions Options { get; }

    public AnalyticsMetrics Metrics { get; }

    public AnalyticsPipeline Pipeline { get; }

    public FakeClock Clock { get; } = new();

    public static async Task<PipelineKit> StartAsync(AnalyticsOptions? options = null, params IAnalyticsSink[] sinks)
    {
        var kit = new PipelineKit(options ?? new AnalyticsOptions(), sinks);
        await kit.Pipeline.StartAsync(CancellationToken.None);
        return kit;
    }

    /// <summary>Sum of a counter, optionally for one tag value.</summary>
    public long Sum(string instrument, string? tag = null, string? value = null) =>
        _measurements.Where(m => m.Name == instrument && (tag is null || (m.Tags.TryGetValue(tag, out var v) && v == value))).Sum(m => m.Value);

    public IEnumerable<Dictionary<string, string?>> TagsOf(string instrument) => _measurements.Where(m => m.Name == instrument).Select(m => m.Tags);

    public long Gauge(string instrument)
    {
        long? last = null;
        _listener.RecordObservableInstruments();
        foreach (var m in _measurements.Where(m => m.Name == instrument))
        {
            last = m.Value;
        }

        return last ?? -1;
    }

    public static AnalyticsEnvelope Delivery(string entryId, string userId = "p1") =>
        new(Payment, AnalyticsEventIds.ForOutboxEntry(entryId, "2026-10-30T14:05:09.123Z"), userId, DateTimeOffset.UnixEpoch, "r", new System.Text.Json.Nodes.JsonObject { ["n"] = entryId });

    public bool Emit(string userId) => Pipeline.TryEmit(Login, userId, Clock.UtcNow, new { source = "login" });

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Pipeline.StopAsync(stop.Token);
        Pipeline.Dispose();
        _listener.Dispose();
        Metrics.Dispose();
    }
}

/// <summary>Blocks every write until <see cref="Open"/>; signals when a write started.</summary>
internal sealed class GateSink(string name = "gate") : IAnalyticsSink
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name { get; } = name;

    public InMemoryAnalyticsSink Inner { get; } = new();

    public ConcurrentQueue<int> BatchSizes { get; } = new();

    public Task Entered => _entered.Task;

    public void Open() => _open.TrySetResult();

    public async ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken)
    {
        BatchSizes.Enqueue(batch.Count);
        _entered.TrySetResult();
        await _open.Task.WaitAsync(cancellationToken);
        await Inner.WriteAsync(batch, cancellationToken);
    }
}
