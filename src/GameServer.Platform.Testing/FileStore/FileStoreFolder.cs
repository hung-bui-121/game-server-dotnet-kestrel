using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Testing;

/// <summary>
/// The file store's folder (research R7, R9): the exclusive <c>.lock</c>, the <c>store.json</c> format file, document paths
/// <c>g_&lt;gameId&gt;/&lt;collection&gt;/&lt;name&gt;.json</c>, the reset safety rules (D4) and the Windows path budget.
/// </summary>
internal sealed class FileStoreFolder : IDisposable
{
    public const string FormatName = "gameserver-file-store";
    public const int FormatVersion = 1;
    public const string FormatFileName = "store.json";
    public const string LockFileName = ".lock";
    public const string JournalFileName = "journal.json";
    public const string TempSuffix = ".tmp";
    public const string DocumentExtension = ".json";

    /// <summary>Longest path Windows accepts without the long-path policy (MAX_PATH 260 minus the terminator).</summary>
    public const int WindowsMaxPath = 259;

    /// <summary>Longest document file name, temporary suffix included: <c>&lt;stem&gt;.json.tmp</c>.</summary>
    public static readonly int MaxFileNameLength = FileKeyCodec.MaxStemLength + DocumentExtension.Length + TempSuffix.Length;

    private const string ResetGuidance = "Fix: reset the local data (run with --FileStore:ResetOnStart=true, or delete the folder while the host is stopped).";

    private FileStream? _lock;

    public FileStoreFolder(string root, string gameId)
    {
        Root = root;
        GameFolder = Path.Combine(root, "g_" + gameId);
    }

    public string Root { get; }

    public string GameFolder { get; }

    public string JournalPath => Path.Combine(Root, JournalFileName);

    public string JournalTempPath => JournalPath + TempSuffix;

    private string FormatPath => Path.Combine(Root, FormatFileName);

    public string CollectionFolder(string collection) => Path.Combine(GameFolder, collection);

    public string DocumentPath(DocumentKey key) => Path.Combine(CollectionFolder(key.Collection), FileKeyCodec.Encode(key.Key) + DocumentExtension);

    /// <summary>Longest path a document of these collections can have, temporary suffix included (R7 path budget).</summary>
    public static int LongestDocumentPath(string folder, string gameId, IEnumerable<string> collections) =>
        Path.TrimEndingDirectorySeparator(folder).Length + 1 + ("g_" + gameId).Length + 1 + collections.DefaultIfEmpty(string.Empty).Max(c => c.Length) + 1
        + MaxFileNameLength;

    /// <summary>
    /// Refuses a folder that holds something else before anything is created in it: a non-empty folder with neither
    /// <c>store.json</c> nor <c>.lock</c> is not a store folder.
    /// </summary>
    public void ThrowIfForeign()
    {
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any() && !File.Exists(FormatPath) && !File.Exists(Path.Combine(Root, LockFileName)))
        {
            throw NotAStoreFolder();
        }
    }

    /// <summary>Takes the exclusive lock (SC-007); released by <see cref="Dispose"/> or by the OS when the process dies.</summary>
    public void AcquireLock()
    {
        if (_lock is not null)
        {
            return;
        }

        Directory.CreateDirectory(Root);
        try
        {
            _lock = new FileStream(Path.Combine(Root, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new GameServerStartupException(
                $"[filestore] the data folder '{Root}' is used by another host ({ex.Message}). Stop it, or run this host with another FileStore:Folder.");
        }
    }

    /// <summary>Creates <c>store.json</c> in an empty folder, or checks it (format and version) in a store folder.</summary>
    public void EnsureFormat()
    {
        if (!File.Exists(FormatPath))
        {
            if (Directory.EnumerateFileSystemEntries(Root).Any(e => !IsLockFile(e)))
            {
                throw NotAStoreFolder();
            }

            WriteFormat();
            return;
        }

        var (format, version) = ReadFormat();
        if (format != FormatName || version != FormatVersion)
        {
            throw new GameServerStartupException(
                $"[filestore] '{FormatPath}' declares format '{format ?? "(unreadable)"}' version {version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}; " +
                $"this host reads '{FormatName}' version {FormatVersion}. {ResetGuidance}");
        }
    }

    /// <summary>
    /// Path rules of <c>FileStore:ResetOnStart</c> (D4), checked before anything is created: never a file-system root, the
    /// user profile, the content root or one of its ancestors, or a folder holding a <c>.git</c> entry. Null when allowed.
    /// </summary>
    public static string? ResetRefusal(string folder, string contentRoot)
    {
        var target = Normalize(folder);
        if (string.Equals(Normalize(Path.GetPathRoot(target) ?? target), target, PathComparison))
        {
            return "a file-system root";
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && string.Equals(Normalize(profile), target, PathComparison))
        {
            return "the user profile folder";
        }

        var content = Normalize(contentRoot);
        if (string.Equals(content, target, PathComparison) || content.StartsWith(target + Path.DirectorySeparatorChar, PathComparison))
        {
            return "the content root or one of its ancestors";
        }

        if (File.Exists(Path.Combine(target, ".git")) || Directory.Exists(Path.Combine(target, ".git")))
        {
            return "a folder with a .git entry";
        }

        return null;
    }

    /// <summary>Deletes everything but <c>.lock</c> and recreates <c>store.json</c>; only after the lock, only in a store folder.</summary>
    public void Reset()
    {
        if (_lock is null)
        {
            throw new InvalidOperationException("Reset needs the folder lock.");
        }

        var entries = Directory.EnumerateFileSystemEntries(Root).Where(e => !IsLockFile(e)).ToArray();
        if (entries.Length > 0 && (!File.Exists(FormatPath) || ReadFormat().Format != FormatName))
        {
            throw new GameServerStartupException(
                $"[filestore] FileStore:ResetOnStart refused for '{Root}': it is not a file store folder (no {FormatFileName} of format " +
                $"'{FormatName}'). Fix: point FileStore:Folder at the local data folder.");
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                FileRetry.Run(() => Directory.Delete(entry, recursive: true));
            }
            else
            {
                FileRetry.Delete(entry);
            }
        }

        WriteFormat();
    }

    /// <summary>Releases the lock (the OS does the same when the process dies).</summary>
    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsLockFile(string entry) => string.Equals(Path.GetFileName(entry), LockFileName, StringComparison.Ordinal);

    private GameServerStartupException NotAStoreFolder() => new(
        $"[filestore] '{Root}' is not empty and has no {FormatFileName}: it is not a file store folder. " +
        "Fix: point FileStore:Folder at an empty or new folder (the sample uses ../_database/dbbase), or empty this folder.");

    private void WriteFormat()
    {
        var format = new JsonObject { ["format"] = FormatName, ["version"] = FormatVersion };
        File.WriteAllText(FormatPath, format.ToJsonString());
    }

    private (string? Format, int? Version) ReadFormat()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllBytes(FormatPath)) as JsonObject;
            var format = node?["format"] is JsonValue f && f.TryGetValue<string>(out var name) ? name : null;
            var version = node?["version"] is JsonValue v && v.TryGetValue<int>(out var number) ? number : (int?)null;
            return (format, version);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
