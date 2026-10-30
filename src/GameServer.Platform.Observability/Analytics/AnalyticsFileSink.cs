using GameServer.Platform.Analytics;
using GameServer.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Observability.Analytics;

/// <summary>
/// Default analytics sink (owner Q8, R9): one append-only JSONL file per instance, event type and UTC day of the server
/// clock (FR-022, FR-023), guarded by a per-instance lock file (FR-025). Lifecycle ([VERIFY] V3): the directory and lock
/// are taken in <see cref="StartingAsync"/>, before any hosted service starts and before the server listens; handles are
/// closed in <see cref="StoppedAsync"/>, after the pipeline drained. Inactive analytics: no directory, lock or file.
/// </summary>
public sealed class AnalyticsFileSink : IAnalyticsSink, IHostedLifecycleService, IDisposable
{
    private const byte NewLine = (byte)'\n';

    private readonly AnalyticsFileNames _names;
    private readonly string _instanceId;
    private readonly int? _retainDays;
    private readonly IClock _clock;
    private readonly ILogger<AnalyticsFileSink> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileStream> _handles = new(StringComparer.Ordinal);
    private FileStream? _lock;
    private DateOnly _day;

    /// <param name="directoryPath">Absolute directory (<c>GameServer:Analytics:Directory</c> resolved against the content root).</param>
    /// <param name="gameId">Deployment game id.</param>
    /// <param name="instanceId">Resolved <c>Observability:InstanceId</c> (→ HOSTNAME → machine name).</param>
    /// <param name="isActive">False: the sink does nothing at all (US5-2).</param>
    /// <param name="retainDays">Optional retention of this instance's own files (FR-029).</param>
    public AnalyticsFileSink(string directoryPath, string gameId, string instanceId, bool isActive, int? retainDays, IClock clock, ILogger<AnalyticsFileSink> logger)
    {
        DirectoryPath = directoryPath;
        _names = new AnalyticsFileNames(gameId, instanceId);
        _instanceId = instanceId;
        IsActive = isActive;
        _retainDays = retainDays;
        _clock = clock;
        _logger = logger;
    }

    public string Name => "file";

    /// <summary>Absolute analytics directory.</summary>
    public string DirectoryPath { get; }

    public bool IsActive { get; }

    public AnalyticsFileNames Names => _names;

    /// <summary>Free bytes of the volume holding the directory; null when unknown.</summary>
    public long? FreeBytes() => DiskSpace.FreeBytes(DirectoryPath);

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (IsActive)
        {
            Open();
        }

        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            if (_lock is null)
            {
                throw new InvalidOperationException("The analytics file sink is not started.");
            }

            // Monotonic UTC day per batch: an NTP step back across 00:00 UTC never reopens a closed day (FR-023).
            var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
            if (today > _day)
            {
                CloseHandles();
                _day = today;
                ApplyRetention();
            }

            var touched = new HashSet<FileStream>();
            var durable = false;
            try
            {
                foreach (var record in batch)
                {
                    var stream = Handle(record.Event);
                    stream.Write(record.Json.Span);
                    stream.WriteByte(NewLine);
                    touched.Add(stream);
                    durable |= record.Guaranteed;
                }

                // Acks wait for this. A batch with an outbox (payment) delivery goes to disk, so an acknowledged payment
                // survives a node crash; login-only batches are only handed to the OS (survive process death, not power loss).
                foreach (var stream in touched)
                {
                    stream.Flush(flushToDisk: durable);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next batch reopens the files and repairs a torn last line.
                CloseHandles();
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Close();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => Close();

    private void Open()
    {
        lock (_gate)
        {
            if (_lock is not null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(DirectoryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new GameServerStartupException(
                    $"[analytics] {AnalyticsOptions.DirectoryKey}: the directory '{DirectoryPath}' cannot be created ({ex.GetType().Name}: {ex.Message}). " +
                    "Fix: point it at a writable local or block volume.");
            }

            var lockPath = Path.Combine(DirectoryPath, _names.LockFile);
            try
            {
                // Single writer per (game, instance, directory): held for the process lifetime, released by the OS on a crash.
                _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new GameServerStartupException(
                    $"[analytics] {AnalyticsOptions.DirectoryKey}: the directory '{DirectoryPath}' is not writable ({ex.Message}). Fix: grant write access.");
            }
            catch (IOException ex)
            {
                throw new GameServerStartupException(
                    $"[analytics] the analytics directory '{DirectoryPath}' is already used by another process with instance id '{_instanceId}' " +
                    $"(lock {_names.LockFile}: {ex.Message}). Fix: give every process sharing the directory its own Observability:InstanceId.");
            }

            _day = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
            ApplyRetention();
        }
    }

    private FileStream Handle(string eventName)
    {
        if (_handles.TryGetValue(eventName, out var stream))
        {
            return stream;
        }

        var path = Path.Combine(DirectoryPath, _names.DataFile(eventName, _day));
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0)
            {
                // A line cut by a crash stays one unparseable line: the next record starts on its own line (FR-024, SD2).
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != NewLine)
                {
                    stream.WriteByte(NewLine);
                }
            }

            stream.Seek(0, SeekOrigin.End);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _handles[eventName] = stream;
        return stream;
    }

    /// <summary>Deletes only this instance's own files whose UTC day is more than <c>RetainLocalDays</c> before today (FR-029).</summary>
    private void ApplyRetention()
    {
        if (_retainDays is not { } days)
        {
            return;
        }

        var oldest = _day.AddDays(-days);
        try
        {
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, _names.Prefix + "*" + AnalyticsFileNames.Extension))
            {
                if (_names.IsOwnDataFile(Path.GetFileName(path), out var fileDay) && fileDay < oldest)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Analytics retention pass failed in {Directory}; it runs again at the next UTC day.", DirectoryPath);
        }
    }

    private void CloseHandles()
    {
        foreach (var stream in _handles.Values)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Closing an analytics file failed in {Directory}.", DirectoryPath);
            }
        }

        _handles.Clear();
    }

    private void Close()
    {
        lock (_gate)
        {
            CloseHandles();
            _lock?.Dispose();
            _lock = null;
        }
    }
}
