using GameServer.Platform.Registry;

namespace GameServer.Platform.Testing;

/// <summary>Startup check of the file store settings (data-model §5): folder set, Windows path budget (R7).</summary>
internal sealed class FileStoreCheck(FileStoreSettings settings, ModuleRegistry registry, string? configurationError) : IStartupCheck
{
    public void Check(StartupCheckResult result)
    {
        if (configurationError is not null)
        {
            result.Errors.Add(configurationError);
        }

        if (string.IsNullOrWhiteSpace(settings.Folder))
        {
            result.Errors.Add(
                "[filestore] FileStore:Folder is not set (GameServer:Store=File). Fix: set it in the flavor file, relative to the content root " +
                "(the sample uses ../_database/dbbase), or pass --FileStore:Folder=<path>.");
            return;
        }

        if (OperatingSystem.IsWindows() && PathBudgetError(settings.Folder, settings.GameId, registry.RequiredCollections()) is { } error)
        {
            result.Errors.Add(error);
        }
    }

    /// <summary>Null when the longest document path fits <see cref="FileStoreFolder.WindowsMaxPath"/>.</summary>
    internal static string? PathBudgetError(string folder, string gameId, IEnumerable<string> collections)
    {
        var longest = FileStoreFolder.LongestDocumentPath(folder, gameId, collections);
        return longest <= FileStoreFolder.WindowsMaxPath
            ? null
            : $"[filestore] FileStore:Folder '{folder}' is too long for Windows: the longest document path would have {longest} characters " +
              $"(limit {FileStoreFolder.WindowsMaxPath}). Fix: move the data folder to a shorter path.";
    }
}
