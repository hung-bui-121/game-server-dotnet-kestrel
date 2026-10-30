using GameServer.Platform.Testing;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Tests.Shared;

/// <summary>The store a <see cref="TestHost"/> runs on.</summary>
public enum TestStoreKind
{
    InMemory,

    /// <summary>A <see cref="FileDocumentStore"/> on a unique temporary folder, deleted with the host (slice 011, T022).</summary>
    File,
}

/// <summary>
/// Test store switch: <c>GAMESERVER_TEST_STORE=File</c> runs every <see cref="TestHost"/> that passes no explicit store on the
/// file store, so the whole suite can prove the file store keeps the in-memory semantics (FR-018).
/// </summary>
public static class TestStores
{
    public const string Variable = "GAMESERVER_TEST_STORE";

    /// <summary>The kind used when a test does not choose one.</summary>
    public static TestStoreKind Default =>
        string.Equals(Environment.GetEnvironmentVariable(Variable), nameof(TestStoreKind.File), StringComparison.OrdinalIgnoreCase)
            ? TestStoreKind.File
            : TestStoreKind.InMemory;

    public static string NewFolder() => Path.Combine(Path.GetTempPath(), "gs-teststore-" + Guid.NewGuid().ToString("N"));

    public static FileDocumentStore CreateFileStore(string folder, string gameId, IClock clock, ILogger<FileDocumentStore> logger) =>
        new(new FileStoreSettings(folder, gameId, ResetOnStart: false, AppContext.BaseDirectory), clock, logger);

    /// <summary>Deletes a test folder; retries briefly while the OS releases handles of a store that was just disposed.</summary>
    public static void DeleteFolder(string folder)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
