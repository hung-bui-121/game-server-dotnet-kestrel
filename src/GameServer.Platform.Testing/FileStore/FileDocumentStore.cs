using System.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Testing;

/// <summary>
/// Development-only document store backed by a folder of JSON files, one per document (research R6). It is an
/// <see cref="InMemoryDocumentStore"/> loaded from the folder when the host starts and persisting every mutation before it
/// becomes visible, so all store semantics are the in-memory store's. Refused outside Development/Test by the
/// <see cref="IDevelopmentOnlyStore"/> rule, before the folder is touched (the folder opens in
/// <see cref="EnsureInitializedAsync"/>).
/// </summary>
public sealed class FileDocumentStore : IStoreInitializer, IHostedService, IDisposable
{
    private readonly IClock _clock;
    private readonly ILogger<FileDocumentStore> _logger;
    private readonly FileStoreFolder _folder;
    private readonly Lock _gate = new();
    private Task? _initialization;
    private bool _opened;
    private bool _disposed;

    public FileDocumentStore(FileStoreSettings settings, IClock clock, ILogger<FileDocumentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        Settings = settings;
        _clock = clock;
        _logger = logger;
        _folder = new FileStoreFolder(settings.Folder, settings.GameId);
        Writer = new FileCommitWriter(_folder, logger);
        Store = new InMemoryDocumentStore(clock, Writer);
    }

    /// <summary>The store to register; refuses access until <see cref="Open"/> ran.</summary>
    public InMemoryDocumentStore Store { get; }

    public string Folder => Settings.Folder;

    /// <summary>True after a write failed past its commit point; every later write fails until restart (R8).</summary>
    public bool IsFaulted => Writer.IsFaulted;

    internal FileStoreSettings Settings { get; }

    /// <summary>The persistence of <see cref="Store"/> (crash points in tests).</summary>
    internal FileCommitWriter Writer { get; }

    /// <summary>
    /// Lock, format check (after an optional reset), journal recovery, load. Idempotent. Throws
    /// <see cref="GameServerStartupException"/> naming the folder or the files at fault, also when the file system refuses
    /// access (read-only folder, ACL, a file in place of the folder); the lock is released on failure.
    /// </summary>
    public void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opened)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(Folder))
            {
                throw new GameServerStartupException("[filestore] FileStore:Folder is not set. Fix: set it (relative to the content root) or pass --FileStore:Folder=<path>.");
            }

            try
            {
                OpenFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Exit 1 with the startup report instead of an unhandled exception (FR-009).
                throw new GameServerStartupException(
                    $"[filestore] the data folder '{Folder}' cannot be opened ({ex.GetType().Name}: {ex.Message}). Fix: make it a folder this " +
                    "user can read and write, or point FileStore:Folder at another folder.");
            }
        }
    }

    /// <summary>The body of <see cref="Open"/>, under its lock.</summary>
    private void OpenFolder()
    {
        var stopwatch = Stopwatch.StartNew();
        if (Settings.ResetOnStart && FileStoreFolder.ResetRefusal(Folder, Settings.ContentRoot) is { } refusal)
        {
            throw new GameServerStartupException(
                $"[filestore] FileStore:ResetOnStart refused for '{Folder}': it is {refusal}. Fix: point FileStore:Folder at the local data folder.");
        }

        _folder.ThrowIfForeign();
        _folder.AcquireLock();
        try
        {
            if (Settings.ResetOnStart)
            {
                _folder.Reset();
                _logger.LogWarning("Reset the local data in {Folder} (FileStore:ResetOnStart).", Folder);
            }

            _folder.EnsureFormat();
            FileStoreLoader.Recover(_folder, _logger);
            var documents = FileStoreLoader.Load(_folder, Store.DeclaredCollections, _clock, _logger);
            Store.LoadCommitted(documents);
            _opened = true;
            _logger.LogInformation("Loaded {Count} documents from {Folder} in {Ms} ms", documents.Count, Folder, stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            // The host does not start: release the folder so the next start (or another host) is not blocked.
            _folder.Dispose();
            throw;
        }
    }

    /// <summary>Opens the folder once, off the caller's thread; every caller awaits the same result.</summary>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _initialization ??= Task.Run(Open, CancellationToken.None);
        }

        return _initialization.WaitAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => EnsureInitializedAsync(cancellationToken);

    /// <summary>Keeps the lock: hosted services stopping after this one (lease release, key ring) still write. <see cref="Dispose"/> releases it.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Refuses further writes and releases the folder lock.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Writer.Close();
            _folder.Dispose();
        }
    }
}
