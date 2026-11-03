using GameServer.Platform.Testing;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Tests.Shared;

/// <summary>Direct file-store construction for the file-store tests (was <c>TestStores.CreateFileStore</c>, kit-internal since 013).</summary>
public static class FileStoreTestSupport
{
    public static FileDocumentStore Create(string folder, string gameId, IClock clock, ILogger<FileDocumentStore> logger) =>
        new(new FileStoreSettings(folder, gameId, ResetOnStart: false, AppContext.BaseDirectory), clock, logger);
}
