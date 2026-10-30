namespace GameServer.Platform.Privacy;

/// <summary>Result of one <see cref="AccountPurgeService.RunAsync"/> for one player.</summary>
public enum PurgeOutcome
{
    /// <summary>Every step ran; the profile is removed and the record is the purge record.</summary>
    Completed,

    /// <summary>The run's document budget ran out; the record stays due, so the next run resumes this player first.</summary>
    Incomplete,

    /// <summary>A step failed; the failure is recorded and the player is retried after the backoff.</summary>
    Failed,
}

/// <summary>
/// Documents one job run may still write across all players (<c>GameServer:Purge:MaxDocumentsPerRun</c>): every purge
/// transaction subtracts its document count; at 0 the purge of the current player stops with <see cref="PurgeOutcome.Incomplete"/>.
/// </summary>
public sealed class PurgeBudget(int documents)
{
    public int Remaining { get; private set; } = documents;

    public bool IsExhausted => Remaining <= 0;

    /// <summary>Documents the next transaction may hold: at most <paramref name="batchSize"/>, never more than what remains.</summary>
    public int Next(int batchSize) => Math.Max(0, Math.Min(batchSize, Remaining));

    public void Consume(int count) => Remaining -= count;
}

/// <summary>
/// Thrown by <see cref="PlayerDataEraser"/> when the run's document budget is spent in the middle of a module step: the
/// service ends the purge as <see cref="PurgeOutcome.Incomplete"/> and the step runs again in the next run. A step must
/// let it propagate (steps are idempotent, so the rerun from the start is harmless).
/// </summary>
public sealed class PurgeBudgetExhaustedException()
    : Exception("The purge run's document budget (GameServer:Purge:MaxDocumentsPerRun) is spent; the step continues in the next run.");
