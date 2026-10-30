namespace GameServer.Platform.Testing;

/// <summary>Section <c>FileStore</c> (data-model §5): the development-only file store of the local flavor.</summary>
public sealed class FileStoreOptions
{
    public const string SectionName = "FileStore";

    /// <summary>Data folder; relative paths resolve against the content root (FR-017). Required when <c>GameServer:Store=File</c>.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Deletes the folder content after taking the lock (D4); passed on the command line by convention.</summary>
    public bool ResetOnStart { get; set; }
}

/// <summary>Resolved settings of one <see cref="FileDocumentStore"/>.</summary>
/// <param name="Folder">Absolute data folder; empty when not configured (reported by the startup check).</param>
/// <param name="GameId">Game id: documents live under <c>g_&lt;gameId&gt;/</c>.</param>
/// <param name="ResetOnStart">Reset the folder content when the store opens.</param>
/// <param name="ContentRoot">Content root of the host: the reset never touches it or its ancestors.</param>
public sealed record FileStoreSettings(string Folder, string GameId, bool ResetOnStart, string ContentRoot);
