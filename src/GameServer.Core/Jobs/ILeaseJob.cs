namespace GameServer.Jobs;

/// <summary>
/// A background job declared in <c>ModuleDescriptor.Jobs</c> (JOB-01). The platform runs it on the single instance holding
/// the job's lease, at most once per configured interval cluster-wide.
/// </summary>
public interface ILeaseJob
{
    /// <summary>
    /// One run. Must be idempotent (a run interrupted by shutdown or lease loss is repeated by the next holder) and must stop
    /// at the next safe point once <see cref="IJobContext.Cancellation"/> fires.
    /// </summary>
    Task RunAsync(IJobContext context);
}

/// <summary>What a job run knows about its lease.</summary>
public interface IJobContext
{
    string JobName { get; }

    /// <summary>
    /// Identifies this acquisition of the lease; larger than every earlier one (FR-003). Progress a job stores must carry it and
    /// refuse writes from a smaller token.
    /// </summary>
    long FencingToken { get; }

    /// <summary>Cancelled on shutdown or when the lease is lost.</summary>
    CancellationToken Cancellation { get; }
}
