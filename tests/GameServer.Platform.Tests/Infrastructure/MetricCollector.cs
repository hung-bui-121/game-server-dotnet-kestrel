using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GameServer.Platform.Diagnostics;

namespace GameServer.Platform.Tests.Infrastructure;

/// <summary>Collects measurements of the platform meter for assertions.</summary>
public sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

    /// <param name="only">Listen to this instance's meter only (parallel test classes share the meter name); null = every instance.</param>
    public MetricCollector(PlatformMetrics? only = null)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (only is null ? instrument.Meter.Name == PlatformMetrics.MeterName : ReferenceEquals(instrument.Meter, only.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => _measurements.Enqueue((i.Name, v, t.ToArray())));
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => _measurements.Enqueue((i.Name, v, t.ToArray())));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => _measurements.Enqueue((i.Name, v, t.ToArray())));
        _listener.Start();
    }

    public double Sum(string name, string? tagKey = null, string? tagValue = null) =>
        _measurements
            .Where(m => m.Name == name && (tagKey is null || m.Tags.Any(t => t.Key == tagKey && Equals(t.Value, tagValue))))
            .Sum(m => m.Value);

    /// <summary>Sum of <paramref name="name"/> over the measurements carrying every tag of <paramref name="tags"/>.</summary>
    public double SumTagged(string name, params (string Key, string Value)[] tags) =>
        _measurements
            .Where(m => m.Name == name && tags.All(tag => m.Tags.Any(t => t.Key == tag.Key && Equals(t.Value, tag.Value))))
            .Sum(m => m.Value);

    public IReadOnlyList<double> Values(string name) => _measurements.Where(m => m.Name == name).Select(m => m.Value).ToArray();

    /// <summary>Takes one reading of every observable instrument (gauges report only when observed).</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();
}
