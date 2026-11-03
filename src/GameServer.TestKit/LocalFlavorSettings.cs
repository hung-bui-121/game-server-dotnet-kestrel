namespace GameServer.TestKit;

/// <summary>
/// The documented set of settings that keep a local-flavor host in tests away from the content root and background work
/// (FR-012): data, analytics and logs under one root, no sweeper unless asked, no lease jobs, no drain delay and an instance
/// id of its own. A framework setting that writes to disk, starts background work or writes to the console joins this list
/// in the slice that introduces it.
/// </summary>
public static class LocalFlavorSettings
{
    private const string FileStoreFolder = "FileStore:Folder";
    private const string OutboxSweeper = "GameServer:Outbox:SweeperEnabled";
    private const string JobsEnabled = "GameServer:Jobs:Enabled";
    private const string DrainDelay = "GameServer:Hosting:DrainDelaySeconds";
    private const string AnalyticsDirectory = "GameServer:Analytics:Directory";
    private const string InstanceId = "Observability:InstanceId";
    private const string LogDirectory = "Observability:Logging:Directory";
    private const string ConsoleEnabled = "Observability:Logging:Console:Enabled";

    public static IReadOnlyList<string> Keys { get; } =
        [FileStoreFolder, OutboxSweeper, JobsEnabled, DrainDelay, AnalyticsDirectory, InstanceId, LogDirectory, ConsoleEnabled];

    public static IReadOnlyDictionary<string, string?> For(string root, string instanceId, bool outboxSweeper = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [FileStoreFolder] = DataFolderOf(root),
            [OutboxSweeper] = outboxSweeper ? "true" : "false",
            [JobsEnabled] = "false",
            [DrainDelay] = "0",
            [AnalyticsDirectory] = AnalyticsFolderOf(root),
            [InstanceId] = instanceId,
            [LogDirectory] = LogFolderOf(root),

            // A host flavor file may turn the console sink on (sample Local/Dev); tests read the log files instead (FR-087).
            [ConsoleEnabled] = "false",
        };
    }

    internal static string DataFolderOf(string root) => Path.Combine(root, "data");

    internal static string AnalyticsFolderOf(string root) => Path.Combine(root, "analytics");

    internal static string LogFolderOf(string root) => Path.Combine(root, "logs");
}
