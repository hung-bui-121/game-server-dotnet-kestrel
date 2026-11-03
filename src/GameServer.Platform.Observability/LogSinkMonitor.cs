using System.Diagnostics;
using System.Diagnostics.Metrics;
using Serilog.Debugging;

namespace GameServer.Platform.Observability;

/// <summary>
/// Health of the log pipeline (LOG-06, ACC-17): Serilog <c>SelfLog</c> errors, entries dropped by the bounded queues and
/// free space of the log directory. <c>SelfLog</c> is process-wide, so the monitor is too. The file sink and the console sink
/// (FR-087) have their own queue and their own drop count: <c>log_dropped_total</c> carries the tag <c>sink=file|console</c>.
/// </summary>
public sealed class LogSinkMonitor
{
    public const string MeterName = "GameServer.Observability";

    /// <summary>Value of the <c>sink</c> tag of <c>log_dropped_total</c> and <c>log_queue_entries</c> for the operational log file.</summary>
    public const string FileSink = "file";

    /// <summary>Value of the <c>sink</c> tag for the console sink.</summary>
    public const string ConsoleSink = "console";

    private static readonly Meter Meter = new(MeterName);

    private long _sinkErrors;
    private long _lastErrorTimestamp;
    private volatile string? _directory;
    private volatile string? _lastError;

    private LogSinkMonitor()
    {
        Meter.CreateObservableCounter("log_sink_errors_total", () => Interlocked.Read(ref _sinkErrors), description: "Serilog sink errors (SelfLog)");
        Meter.CreateObservableCounter(
            "log_dropped_total",
            () => new[] { FileQueue.DroppedMeasurement(), ConsoleQueue.DroppedMeasurement() },
            description: "Log events dropped by the bounded queue, by sink (file, console)");
        Meter.CreateObservableGauge("log_queue_entries", () => new[] { FileQueue.QueueMeasurement(), ConsoleQueue.QueueMeasurement() });
        Meter.CreateObservableGauge("log_dir_free_bytes", () => FreeBytes() ?? 0, "By");
    }

    public static LogSinkMonitor Instance { get; } = new();

    /// <summary>Queue monitor of the operational log file sink.</summary>
    public LogQueueMonitor FileQueue { get; } = new(FileSink);

    /// <summary>Queue monitor of the console sink.</summary>
    public LogQueueMonitor ConsoleQueue { get; } = new(ConsoleSink);

    public long SinkErrors => Interlocked.Read(ref _sinkErrors);

    /// <summary>Both sinks together, cumulative for the process (including sinks of hosts that already stopped).</summary>
    public long DroppedCount => FileQueue.DroppedCount + ConsoleQueue.DroppedCount;

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
}
