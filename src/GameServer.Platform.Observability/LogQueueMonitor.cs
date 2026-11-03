using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Serilog.Sinks.Async;

namespace GameServer.Platform.Observability;

/// <summary>
/// Drop count and queue length of one log sink's bounded queues (one queue per host that enabled the sink), reported with the
/// tag <c>sink</c> = <see cref="Sink"/> (<see cref="LogSinkMonitor.FileSink"/>, <see cref="LogSinkMonitor.ConsoleSink"/>).
/// </summary>
public sealed class LogQueueMonitor : IAsyncLogEventSinkMonitor
{
    private readonly ConcurrentDictionary<IAsyncLogEventSinkInspector, byte> _inspectors = new();
    private readonly KeyValuePair<string, object?> _tag;
    private long _droppedByStoppedSinks;

    internal LogQueueMonitor(string sink)
    {
        Sink = sink;
        _tag = new("sink", sink);
    }

    /// <summary>The <c>sink</c> tag value.</summary>
    public string Sink { get; }

    /// <summary>Cumulative for the process, including queues of hosts that already stopped.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedByStoppedSinks) + _inspectors.Keys.Sum(i => i.DroppedMessagesCount);

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => _inspectors.TryAdd(inspector, 0);

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        if (_inspectors.TryRemove(inspector, out _))
        {
            Interlocked.Add(ref _droppedByStoppedSinks, inspector.DroppedMessagesCount);
        }
    }

    internal Measurement<long> DroppedMeasurement() => new(DroppedCount, _tag);

    internal Measurement<long> QueueMeasurement() => new(_inspectors.Keys.Sum(i => (long)i.Count), _tag);
}
