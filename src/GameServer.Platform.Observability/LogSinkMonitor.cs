using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Serilog.Debugging;
using Serilog.Sinks.Async;

namespace GameServer.Platform.Observability;

/// <summary>
/// Health of the log pipeline (LOG-06, ACC-17): Serilog <c>SelfLog</c> errors, entries dropped by the bounded queue and
/// free space of the log directory. <c>SelfLog</c> is process-wide, so the monitor is too.
/// </summary>
public sealed class LogSinkMonitor : IAsyncLogEventSinkMonitor
{
    public const string MeterName = "GameServer.Observability";

    private static readonly Meter Meter = new(MeterName);

    private readonly ConcurrentDictionary<IAsyncLogEventSinkInspector, byte> _inspectors = new();
    private long _sinkErrors;
    private long _droppedByStoppedSinks;
    private long _lastErrorTimestamp;
    private volatile string? _directory;
    private volatile string? _lastError;

    private LogSinkMonitor()
    {
        Meter.CreateObservableCounter("log_sink_errors_total", () => Interlocked.Read(ref _sinkErrors), description: "Serilog sink errors (SelfLog)");
        Meter.CreateObservableCounter("log_dropped_total", () => DroppedCount, description: "Log events dropped by the bounded queue");
        Meter.CreateObservableGauge("log_queue_entries", () => _inspectors.Keys.Sum(i => (long)i.Count));
        Meter.CreateObservableGauge("log_dir_free_bytes", () => FreeBytes() ?? 0, "By");
    }

    public static LogSinkMonitor Instance { get; } = new();

    public long SinkErrors => Interlocked.Read(ref _sinkErrors);

    /// <summary>Cumulative for the process, including sinks of hosts that already stopped.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedByStoppedSinks) + _inspectors.Keys.Sum(i => i.DroppedMessagesCount);

    /// <summary>Last sink error message (for the GM health detail).</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Routes Serilog's internal errors here (idempotent; replaces any other SelfLog output of the process).
    /// Queue-full notices are already counted as drops and are not sink errors.
    /// </summary>
    public void Attach(string directory)
    {
        _directory = directory;
        SelfLog.Enable(message =>
        {
            if (!message.Contains("unable to enqueue", StringComparison.OrdinalIgnoreCase))
            {
                RecordSinkError(message);
            }
        });
    }

    public void RecordSinkError(string? message = null)
    {
        if (message is not null)
        {
            _lastError = message.Length > 500 ? message[..500] : message;
        }

        Interlocked.Increment(ref _sinkErrors);
        Interlocked.Exchange(ref _lastErrorTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>True when a sink error happened within <paramref name="window"/>.</summary>
    public bool HasRecentError(TimeSpan window)
    {
        var last = Interlocked.Read(ref _lastErrorTimestamp);
        return last != 0 && Stopwatch.GetElapsedTime(last) < window;
    }

    /// <summary>Free bytes of the drive holding the log directory; null when unknown.</summary>
    public long? FreeBytes() => _directory is { } directory ? DiskSpace.FreeBytes(directory) : null;

    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => _inspectors.TryAdd(inspector, 0);

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    {
        if (_inspectors.TryRemove(inspector, out _))
        {
            Interlocked.Add(ref _droppedByStoppedSinks, inspector.DroppedMessagesCount);
        }
    }
}
