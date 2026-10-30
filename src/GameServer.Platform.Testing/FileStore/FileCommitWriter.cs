using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Testing;

/// <summary>
/// Writes every committed change to the folder before it becomes visible (research R8). One change: temp file, then rename
/// (the commit point). Several changes: <c>journal.json.tmp</c>, rename to <c>journal.json</c> (the commit point), apply, delete
/// the journal. A failure before the commit point cleans up and throws (nothing applied); a failure after it returns normally
/// (the journal replays at the next start) and faults the writer: every later write throws until the host restarts.
/// Durability = flushed to the operating system, no fsync (D5).
/// </summary>
internal sealed class FileCommitWriter(FileStoreFolder folder, ILogger logger) : IDocumentPersistence
{
    private volatile bool _faulted;
    private volatile bool _closed;

    /// <summary>True after a write failed past its commit point (readiness reports the store unhealthy).</summary>
    public bool IsFaulted => _faulted;

    /// <summary>Test hook: the writer stops at this step without any further disk operation, like a killed process.</summary>
    internal FileCommitStep? CrashAt { get; set; }

    /// <summary>Refuses every later write (the store was disposed and its folder lock released).</summary>
    public void Close() => _closed = true;

    public void Persist(IReadOnlyList<DocumentChange> changes)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_faulted)
        {
            throw new IOException(
                "[filestore] the store is faulted: an earlier write failed after its commit point. Restart the host; the journal replays that commit.");
        }

        if (changes.Count == 1)
        {
            PersistSingle(changes[0]);
        }
        else if (changes.Count > 1)
        {
            PersistJournal(changes);
        }
    }

    private void PersistSingle(DocumentChange change)
    {
        var path = folder.DocumentPath(change.Key);
        if (change.Content is null)
        {
            // The delete is the commit point: a failure leaves the file in place and is reported to the caller.
            FileRetry.Delete(path);
            return;
        }

        WriteDocument(path, change, FileCommitStep.SingleTempWritten, replaced: null);
    }

    private void PersistJournal(IReadOnlyList<DocumentChange> changes)
    {
        // Paths first: a key that cannot be stored fails here, before anything is written.
        var paths = changes.Select(c => folder.DocumentPath(c.Key)).ToArray();
        var temp = folder.JournalTempPath;
        try
        {
            File.WriteAllBytes(temp, FileDocumentFormat.Journal(Guid.NewGuid().ToString("N"), changes));
            Crash(FileCommitStep.JournalTempWritten);
            FileRetry.Move(temp, folder.JournalPath);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            FileRetry.TryDelete(temp);
            throw;
        }

        Crash(FileCommitStep.JournalCommitted);
        var current = folder.JournalPath;
        try
        {
            for (var i = 0; i < changes.Count; i++)
            {
                current = paths[i];
                if (changes[i].Content is null)
                {
                    FileRetry.Delete(paths[i]);
                    Crash(FileCommitStep.DocumentReplaced(i));
                }
                else
                {
                    WriteDocument(paths[i], changes[i], FileCommitStep.DocumentTempWritten(i), FileCommitStep.DocumentReplaced(i));
                }
            }

            current = folder.JournalPath;
            FileRetry.Delete(folder.JournalPath);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            // Past the commit point the change is durable (the journal replays it): report success, refuse what follows.
            _faulted = true;
            logger.LogError(
                ex,
                "[filestore] a write failed after its commit point on '{File}': {Cause}. The commit is kept in {Journal} and replays at the next start; " +
                "every later write fails until the host restarts.",
                current,
                ex.Message,
                folder.JournalPath);
            return;
        }

        Crash(FileCommitStep.JournalDeleted);
    }

    /// <summary>Temp file then rename; a failure before the rename removes the temp file and throws.</summary>
    private void WriteDocument(string path, DocumentChange change, FileCommitStep tempWritten, FileCommitStep? replaced)
    {
        var temp = path + FileStoreFolder.TempSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temp, FileDocumentFormat.Document(change.Key.Key, change.ExpiresAt, change.Content!));
            Crash(tempWritten);
            FileRetry.Move(temp, path);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            FileRetry.TryDelete(temp);
            throw;
        }

        if (replaced is { } step)
        {
            Crash(step);
        }
    }

    private void Crash(FileCommitStep step)
    {
        if (CrashAt == step)
        {
            _closed = true;
            throw new SimulatedCrashException(step);
        }
    }
}

internal enum FileCommitStepKind
{
    JournalTempWritten,
    JournalCommitted,
    DocumentTempWritten,
    DocumentReplaced,
    JournalDeleted,
    SingleTempWritten,
}

/// <summary>A crash point of <see cref="FileCommitWriter"/> (research R8); <see cref="Index"/> is the change's position in the commit.</summary>
internal readonly record struct FileCommitStep(FileCommitStepKind Kind, int Index = 0)
{
    public static FileCommitStep JournalTempWritten => new(FileCommitStepKind.JournalTempWritten);

    public static FileCommitStep JournalCommitted => new(FileCommitStepKind.JournalCommitted);

    public static FileCommitStep JournalDeleted => new(FileCommitStepKind.JournalDeleted);

    public static FileCommitStep SingleTempWritten => new(FileCommitStepKind.SingleTempWritten);

    public static FileCommitStep DocumentTempWritten(int index) => new(FileCommitStepKind.DocumentTempWritten, index);

    public static FileCommitStep DocumentReplaced(int index) => new(FileCommitStepKind.DocumentReplaced, index);
}

/// <summary>Thrown at a test crash point: the writer stops there and never writes again, like a killed process.</summary>
internal sealed class SimulatedCrashException(FileCommitStep step) : Exception($"Simulated crash at {step}.");
