namespace GameServer.Platform.Jobs;

/// <summary>Section <c>GameServer:Jobs</c>: lease job runner (JOB-01, OPS-05). Bounds in data-model.md, PCR platform.</summary>
public sealed class JobSettings
{
    public const int MinLeaseSeconds = 30;
    public const int MaxLeaseSeconds = 600;
    public const int MinPollSeconds = 5;
    public const int MinIntervalSeconds = 10;
    public const int MaxIntervalSeconds = 86400;

    /// <summary>A renewal must succeed within this margin before <c>leaseUntilUtc</c>, otherwise the lease counts as lost.</summary>
    public static readonly TimeSpan RenewalSafetyMargin = TimeSpan.FromSeconds(5);

    /// <summary>The lease must outlive several transactions (a job stops only between them), hence ≥ 3 × the expiration.</summary>
    private const int LeaseToTransactionFactor = 3;

    /// <summary>Renewal must be possible at least this many times per lease.</summary>
    private const int RenewalsPerLease = 3;

    /// <summary>Runs background jobs on this instance. Off in tests that drive runs through <c>RunOnceAsync</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lease duration; another instance takes over a crashed holder's lease after it.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>Renewal period while a run is in progress (≤ LeaseSeconds / 3).</summary>
    public int RenewSeconds { get; set; } = 20;

    /// <summary>Lease check period of every instance (± 20 % jitter).</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Per-job overrides, keyed by job name.</summary>
    public Dictionary<string, JobOverride> Settings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether the job may run: its override, otherwise true.</summary>
    public bool IsJobEnabled(string jobName) => Settings.GetValueOrDefault(jobName)?.Enabled ?? true;

    /// <summary>Cluster-wide interval between finished runs: the override, otherwise the descriptor default.</summary>
    public TimeSpan Interval(string jobName, int defaultIntervalSeconds) =>
        TimeSpan.FromSeconds(Settings.GetValueOrDefault(jobName)?.IntervalSeconds ?? defaultIntervalSeconds);

    public IEnumerable<string> Validate(TransactionSettings transactions)
    {
        if (LeaseSeconds is < MinLeaseSeconds or > MaxLeaseSeconds || LeaseSeconds < LeaseToTransactionFactor * transactions.ExpirationSeconds)
        {
            yield return
                $"[jobs] GameServer:Jobs:LeaseSeconds ({LeaseSeconds}) must be {MinLeaseSeconds}..{MaxLeaseSeconds} and >= " +
                $"{LeaseToTransactionFactor} x GameServer:Transactions:ExpirationSeconds ({transactions.ExpirationSeconds}) (JOB-01).";
        }

        if (RenewSeconds < 1 || RenewSeconds > LeaseSeconds / RenewalsPerLease)
        {
            yield return $"[jobs] GameServer:Jobs:RenewSeconds ({RenewSeconds}) must be 1..LeaseSeconds/{RenewalsPerLease} ({LeaseSeconds / RenewalsPerLease}).";
        }

        if (PollSeconds < MinPollSeconds || PollSeconds > LeaseSeconds)
        {
            yield return $"[jobs] GameServer:Jobs:PollSeconds ({PollSeconds}) must be {MinPollSeconds}..LeaseSeconds ({LeaseSeconds}).";
        }

        foreach (var (job, value) in Settings)
        {
            if (value?.IntervalSeconds is { } interval && interval is < MinIntervalSeconds or > MaxIntervalSeconds)
            {
                yield return $"[jobs] GameServer:Jobs:Settings:{job}:IntervalSeconds ({interval}) must be {MinIntervalSeconds}..{MaxIntervalSeconds}.";
            }
        }
    }
}

/// <summary>Configuration override of one job (<c>GameServer:Jobs:Settings:{job}</c>).</summary>
public sealed class JobOverride
{
    /// <summary>False = the job never acquires its lease on this instance; null = enabled.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Overrides the descriptor's default interval (10..86400 s).</summary>
    public int? IntervalSeconds { get; set; }
}
