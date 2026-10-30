using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Testing;

/// <summary>
/// Startup side of the file store (research R8, R9): crash recovery (temporary files, journal replay), then a parallel load of
/// the declared collections. Malformed files fail the start all at once, by name (FR-024); expired documents are dropped and
/// their files deleted (FR-022, D7).
/// </summary>
internal static class FileStoreLoader
{
    /// <summary>Malformed files named in the startup error; the rest are counted.</summary>
    public const int MaxNamedErrors = 20;

    private const string Fix = "Fix it or delete it while the host is stopped, or reset the local data.";

    /// <summary>Deletes every <c>*.tmp</c> and replays <c>journal.json</c> when present (idempotent).</summary>
    public static void Recover(FileStoreFolder folder, ILogger logger)
    {
        foreach (var temp in Directory.EnumerateFiles(folder.Root, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(FileStoreFolder.TempSuffix, StringComparison.Ordinal)))
        {
            FileRetry.Delete(temp);
        }

        if (!File.Exists(folder.JournalPath))
        {
            return;
        }

        IReadOnlyList<DocumentChange> changes;
        try
        {
            changes = FileDocumentFormat.ParseJournal(File.ReadAllBytes(folder.JournalPath));
        }
        catch (FormatException ex)
        {
            throw new GameServerStartupException(
                $"[filestore] '{folder.JournalPath}' cannot be replayed ({ex.Message}). " +
                "Fix: delete it to drop the last commit, or reset the local data.");
        }

        // Each write is replayed alone (temp file + rename): applying it twice gives the same file.
        var writer = new FileCommitWriter(folder, logger);
        foreach (var change in changes)
        {
            writer.Persist([change]);
        }

        FileRetry.Delete(folder.JournalPath);
        logger.LogInformation("Replayed the last commit ({Count} documents) from {Journal}.", changes.Count, folder.JournalPath);
    }

    /// <summary>Documents of the declared collections (every collection folder when <paramref name="declared"/> is null).</summary>
    public static IReadOnlyList<(DocumentKey Key, JsonObject Content, DateTimeOffset? ExpiresAt)> Load(
        FileStoreFolder folder,
        IReadOnlySet<string>? declared,
        IClock clock,
        ILogger logger)
    {
        if (!Directory.Exists(folder.GameFolder))
        {
            return [];
        }

        var present = Directory.EnumerateDirectories(folder.GameFolder).Select(d => Path.GetFileName(d)).ToArray();
        var collections = present.Where(c => declared is null || declared.Contains(c)).ToArray();
        var undeclared = present.Except(collections, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (undeclared.Length > 0)
        {
            logger.LogInformation("Ignored folders of undeclared collections in {Folder}: {Collections}.", folder.GameFolder, string.Join(", ", undeclared));
        }

        var files = collections.SelectMany(c => Directory.EnumerateFiles(folder.CollectionFolder(c)).Select(path => (Collection: c, Path: path))).ToArray();
        var now = clock.UtcNow;
        var documents = new ConcurrentBag<(DocumentKey Key, JsonObject Content, DateTimeOffset? ExpiresAt)>();
        var expired = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<(string Path, string Reason)>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
        {
            var name = Path.GetFileName(file.Path);
            if (!name.EndsWith(FileStoreFolder.DocumentExtension, StringComparison.Ordinal))
            {
                errors.Add((file.Path, "not a document file name (<encoded key>.json)"));
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add((file.Path, "unreadable: " + ex.Message));
                return;
            }

            if (FileDocumentFormat.ParseDocument(bytes, out var reason) is not { } document)
            {
                errors.Add((file.Path, reason));
                return;
            }

            if (!FileKeyCodec.Matches(name[..^FileStoreFolder.DocumentExtension.Length], document.Key))
            {
                errors.Add((file.Path, $"the file name does not match its key '{document.Key}'"));
                return;
            }

            if (document.ExpiresAt <= now)
            {
                expired.Add(file.Path);
                return;
            }

            documents.Add((new DocumentKey(file.Collection, document.Key), document.Content, document.ExpiresAt));
        });

        if (!errors.IsEmpty)
        {
            var sorted = errors.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
            var lines = sorted.Take(MaxNamedErrors).Select(e => $"[filestore] '{e.Path}' is not a valid document ({e.Reason}). {Fix}").ToList();
            if (sorted.Length > MaxNamedErrors)
            {
                lines.Add($"[filestore] ... and {sorted.Length - MaxNamedErrors} more malformed files in '{folder.Root}'. {Fix}");
            }

            throw new GameServerStartupException(lines);
        }

        foreach (var path in expired)
        {
            FileRetry.Delete(path);
        }

        return documents.ToArray();
    }
}
