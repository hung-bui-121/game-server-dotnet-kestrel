using System.Globalization;

namespace GameServer.Platform.Observability.Analytics;

/// <summary>
/// File names of the analytics sink (R9, data-model §6): <c>{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl</c> and the lock
/// <c>.{gameId}-{instanceId}.lock</c>, game and instance sanitized like the log file name. Names are parsed from the right:
/// event names never contain <c>-</c>, instance ids may.
/// </summary>
public sealed class AnalyticsFileNames
{
    public const string Extension = ".jsonl";
    private const string DateFormat = "yyyyMMdd";

    public AnalyticsFileNames(string gameId, string instanceId)
    {
        Prefix = ObservabilityExtensions.Sanitize(gameId) + "-" + ObservabilityExtensions.Sanitize(instanceId) + "-";
        LockFile = "." + Prefix[..^1] + ".lock";
    }

    /// <summary><c>{gameId}-{instanceId}-</c> (sanitized): every own data file starts with it.</summary>
    public string Prefix { get; }

    /// <summary><c>.{gameId}-{instanceId}.lock</c>.</summary>
    public string LockFile { get; }

    public string DataFile(string eventName, DateOnly utcDay) =>
        Prefix + eventName + "-" + utcDay.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension;

    /// <summary>
    /// Parses a data file name from the right into its parts (any instance); false for anything else.
    /// <paramref name="owner"/> is <c>{gameId}-{instanceId}</c>.
    /// </summary>
    public static bool TryParse(string fileName, out string owner, out string eventName, out DateOnly utcDay)
    {
        owner = eventName = string.Empty;
        utcDay = default;
        if (!fileName.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = fileName[..^Extension.Length];
        var dateDash = stem.LastIndexOf('-');
        if (dateDash <= 0 || !DateOnly.TryParseExact(stem[(dateDash + 1)..], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out utcDay))
        {
            return false;
        }

        var eventDash = stem.LastIndexOf('-', dateDash - 1);
        if (eventDash <= 0)
        {
            return false;
        }

        eventName = stem[(eventDash + 1)..dateDash];
        owner = stem[..eventDash];
        return GameServer.Analytics.AnalyticsEventType.IsValidName(eventName) && owner.Contains('-', StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="fileName"/> is a data file of this game and instance; gives its UTC day.</summary>
    public bool IsOwnDataFile(string fileName, out DateOnly utcDay) =>
        TryParse(fileName, out var owner, out _, out utcDay) && owner + "-" == Prefix;
}
