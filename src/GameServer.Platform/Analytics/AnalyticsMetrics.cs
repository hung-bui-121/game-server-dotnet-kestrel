using System.Diagnostics.Metrics;

namespace GameServer.Platform.Analytics;

/// <summary>
/// Meter <c>GameServer.Analytics</c> (FR-026, OBS-01). Tags are only the enumerated reasons, the event type name and the
/// sink name; never a player, event or transaction id (OBS-02). One instance per host.
/// </summary>
public sealed class AnalyticsMetrics : IDisposable
{
    public const string MeterName = "GameServer.Analytics";

    private readonly Counter<long> _written;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _sinkErrors;
    private readonly Counter<long> _deliveryFailures;
    private Func<int>? _queueLength;
    private Func<long?>? _freeBytes;

    public AnalyticsMetrics()
    {
        Meter = new Meter(MeterName);
        _written = Meter.CreateCounter<long>("analytics_events_written_total", description: "Analytics records written, by event and sink");
        _dropped = Meter.CreateCounter<long>("analytics_events_dropped_total", description: "Best-effort analytics records lost, by event and reason");
        _sinkErrors = Meter.CreateCounter<long>("analytics_sink_errors_total", description: "Failed sink batches, by sink");
        _deliveryFailures = Meter.CreateCounter<long>("analytics_delivery_failures_total", description: "Outbox deliveries not acknowledged, by reason");
        Meter.CreateObservableGauge("analytics_queue_entries", () => _queueLength?.Invoke() ?? 0, description: "Records waiting in the analytics queue");
    }

    /// <summary>This host's meter (tests filter a <see cref="MeterListener"/> on it; parallel hosts each have their own).</summary>
    public Meter Meter { get; }

    public void Written(string eventName, string sink, int count) =>
        _written.Add(count, new KeyValuePair<string, object?>("event", eventName), new KeyValuePair<string, object?>("sink", sink));

    public void Dropped(string eventName, AnalyticsDropReason reason, int count = 1) =>
        _dropped.Add(count, new KeyValuePair<string, object?>("event", eventName), new KeyValuePair<string, object?>("reason", Tag(reason)));

    public void SinkError(string sink) => _sinkErrors.Add(1, new KeyValuePair<string, object?>("sink", sink));

    public void DeliveryFailed(AnalyticsDeliveryFailure reason) => _deliveryFailures.Add(1, new KeyValuePair<string, object?>("reason", Tag(reason)));

    /// <summary>Set once by the pipeline.</summary>
    public void ObserveQueue(Func<int> queueLength) => _queueLength = queueLength;

    /// <summary>
    /// Registers the gauge <c>analytics_dir_free_bytes</c> (file sink, R10); later calls replace the probe. Unknown free
    /// space reports nothing.
    /// </summary>
    public void ObserveDirectoryFreeBytes(Func<long?> freeBytes)
    {
        var first = Interlocked.Exchange(ref _freeBytes, freeBytes) is null;
        if (first)
        {
            Meter.CreateObservableGauge(
                "analytics_dir_free_bytes",
                () => _freeBytes?.Invoke() is { } free ? [new Measurement<long>(free)] : Array.Empty<Measurement<long>>(),
                "By",
                "Free bytes of the volume holding the analytics directory");
        }
    }

    public static string Tag(AnalyticsDropReason reason) => reason switch
    {
        AnalyticsDropReason.QueueFull => "queue_full",
        AnalyticsDropReason.SinkError => "sink_error",
        _ => "shutdown",
    };

    public static string Tag(AnalyticsDeliveryFailure reason) => reason switch
    {
        AnalyticsDeliveryFailure.QueueFull => "queue_full",
        AnalyticsDeliveryFailure.Timeout => "timeout",
        AnalyticsDeliveryFailure.SinkError => "sink_error",
        AnalyticsDeliveryFailure.Stopped => "stopped",
        _ => "malformed",
    };

    public void Dispose() => Meter.Dispose();
}
