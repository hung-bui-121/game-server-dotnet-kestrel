using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>A unique temporary folder for file store tests, deleted afterwards (also when a test left a store open).</summary>
internal sealed class FileStoreTestFolder : IDisposable
{
    public const string GameId = "t";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gs-filestore-" + Guid.NewGuid().ToString("N"));

    public string GameFolder => Path.Combine(Root, "g_" + GameId);

    public FileDocumentStore Create(FakeClock clock, bool reset = false, string? folder = null, string? contentRoot = null) =>
        new(new FileStoreSettings(folder ?? Root, GameId, reset, contentRoot ?? AppContext.BaseDirectory), clock, NullLogger<FileDocumentStore>.Instance);

    public FileDocumentStore Open(FakeClock clock, bool reset = false, string? folder = null, string? contentRoot = null)
    {
        var store = Create(clock, reset, folder, contentRoot);
        store.Open();
        return store;
    }

    public string DocumentPath(DocumentKey key) => Path.Combine(GameFolder, key.Collection, FileKeyCodec.Encode(key.Key) + ".json");

    /// <summary>Every file (relative path) with its bytes, to prove a folder did not change.</summary>
    public IReadOnlyDictionary<string, string> Files() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != ".lock")
                .ToDictionary(f => Path.GetRelativePath(Root, f), f => Convert.ToBase64String(File.ReadAllBytes(f)))
            : new Dictionary<string, string>();

    /// <summary>The committed documents of every collection the folder or <paramref name="collections"/> name, ordered.</summary>
    public IReadOnlyList<string> Snapshot(InMemoryDocumentStore store, params string[] collections)
    {
        var all = Directory.Exists(GameFolder)
            ? Directory.EnumerateDirectories(GameFolder).Select(d => Path.GetFileName(d)).Concat(collections)
            : collections;
        return all.Distinct(StringComparer.Ordinal)
            .SelectMany(c => store.All(c))
            .Select(d => $"{d.Key} = {d.Content.ToJsonString()}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static JsonObject Doc(int value) => new() { ["value"] = value };

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A store not disposed by a failed test still holds .lock: let the finalizer release it, then retry.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }
}
