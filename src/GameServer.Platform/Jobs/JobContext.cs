using GameServer.Jobs;

namespace GameServer.Platform.Jobs;

/// <summary>Context of one job run on the lease holder.</summary>
public sealed class JobContext(string jobName, long fencingToken, CancellationToken cancellation) : IJobContext
{
    public string JobName { get; } = jobName;

    public long FencingToken { get; } = fencingToken;

    public CancellationToken Cancellation { get; } = cancellation;
}

/// <summary>Outcome of one attempt to run a job (metric <c>job_runs_total{result}</c>, lease <c>lastRun.result</c>).</summary>
public enum JobRunResult
{
    /// <summary>The lease is released and the next run is not due yet.</summary>
    NotDue,

    /// <summary>Another instance holds the lease or won the race for it.</summary>
    HeldElsewhere,

    /// <summary>The run finished; the next one is due after the interval.</summary>
    Completed,

    /// <summary>The job threw; the next run is due after the interval.</summary>
    Failed,

    /// <summary>The lease was taken over or could not be renewed in time; the run was cancelled and nothing was released.</summary>
    LeaseLost,

    /// <summary>Shutdown interrupted the run; the lease was released for an immediate run elsewhere.</summary>
    Cancelled,

    /// <summary>The job is disabled on this instance (<c>GameServer:Jobs:Settings:{job}:Enabled = false</c>).</summary>
    Disabled,
}
