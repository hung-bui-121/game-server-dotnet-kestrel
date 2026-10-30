namespace GameServer.Platform.Privacy;

/// <summary>Section <c>GameServer:Purge</c>: limits of the account purge (PRIV-01, DAT-04). Bounds in data-model.md, PCR platform.</summary>
public sealed class PurgeSettings
{
    public const int MaxPlayersPerRunLimit = 500;
    public const int MaxBatchSize = 100;
    public const int MinDocumentsPerRun = 100;
    public const int MaxDocumentsPerRunLimit = 100_000;
    public const int MaxRetryDelayLimitMinutes = 1440;

    /// <summary>Players processed by one job run (resumed first, then newly due).</summary>
    public int MaxPlayersPerRun { get; set; } = 50;

    /// <summary>Documents written per purge transaction (DAT-04 bounded transactions).</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Documents written per job run across all players; the rest continues in the next run.</summary>
    public int MaxDocumentsPerRun { get; set; } = 5000;

    /// <summary>Delay before retrying a failed player; doubles per consecutive failure.</summary>
    public int RetryDelayMinutes { get; set; } = 10;

    /// <summary>Cap of the doubled retry delay.</summary>
    public int MaxRetryDelayMinutes { get; set; } = 1440;

    public IEnumerable<string> Validate()
    {
        if (MaxPlayersPerRun is < 1 or > MaxPlayersPerRunLimit)
        {
            yield return $"[purge] GameServer:Purge:MaxPlayersPerRun ({MaxPlayersPerRun}) must be 1..{MaxPlayersPerRunLimit}.";
        }

        if (BatchSize is < 1 or > MaxBatchSize)
        {
            yield return $"[purge] GameServer:Purge:BatchSize ({BatchSize}) must be 1..{MaxBatchSize} documents per transaction (DAT-04).";
        }

        if (MaxDocumentsPerRun is < MinDocumentsPerRun or > MaxDocumentsPerRunLimit)
        {
            yield return $"[purge] GameServer:Purge:MaxDocumentsPerRun ({MaxDocumentsPerRun}) must be {MinDocumentsPerRun}..{MaxDocumentsPerRunLimit}.";
        }

        if (RetryDelayMinutes is < 1 or > MaxRetryDelayLimitMinutes)
        {
            yield return $"[purge] GameServer:Purge:RetryDelayMinutes ({RetryDelayMinutes}) must be 1..{MaxRetryDelayLimitMinutes}.";
        }

        if (MaxRetryDelayMinutes < RetryDelayMinutes)
        {
            yield return $"[purge] GameServer:Purge:MaxRetryDelayMinutes ({MaxRetryDelayMinutes}) must be >= RetryDelayMinutes ({RetryDelayMinutes}).";
        }
    }
}
