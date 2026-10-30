using System.Diagnostics;
using GameServer.Jobs;
using GameServer.Modules;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Jobs;

/// <summary>
/// Runs every job declared in <c>ModuleDescriptor.Jobs</c> on the instance holding its lease (JOB-01, OPS-05, research R1).
/// Each job polls its lease every <c>PollSeconds</c> (± 20 %); the holder renews it while the run lasts, cancels the run when
/// the lease is lost, and releases it afterwards: next run after the interval when finished, at once when interrupted
/// (FR-004). Lease times come from <see cref="IClock"/>; only the poll and renewal waits use real time.
/// </summary>
public sealed class LeaseJobRunner(
    IServiceScopeFactory scopes,
    IEnumerable<IStoreInitializer> initializers,
    ModuleRegistry registry,
    LeaseStore leases,
    IClock clock,
    GameServerOptions options,
    PlatformMetrics metrics,
    ILogger<LeaseJobRunner> logger) : BackgroundService
{
    /// <summary>Best-effort release after a run; never takes the stopping token (OPS-03).</summary>
    private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(2);

    /// <summary>Poll jitter (± 20 %) so instances do not poll in lockstep.</summary>
    private const double PollJitter = 0.2;

    /// <summary>Owner id written into the leases this runner holds.</summary>
    public string Owner { get; } = LeaseStore.NewOwner();

    private JobSettings Settings => options.Jobs;

    /// <summary>
    /// Acquire → run → release once, synchronously (tests, ops). Respects the job's enable flag and lease; returns why
    /// nothing ran when the lease was not acquired.
    /// </summary>
    public Task<JobRunResult> RunOnceAsync(string jobName, CancellationToken cancellationToken)
    {
        var job = registry.Modules.SelectMany(m => m.Descriptor.Jobs).FirstOrDefault(j => j.Name == jobName)
            ?? throw new ArgumentException($"No module declares job '{jobName}'.", nameof(jobName));
        return RunAsync(job, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobs = registry.Modules.SelectMany(m => m.Descriptor.Jobs).Where(j => Settings.IsJobEnabled(j.Name)).ToArray();
        if (!Settings.Enabled || jobs.Length == 0)
        {
            return;
        }

        try
        {
            foreach (var initializer in initializers)
            {
                await initializer.EnsureInitializedAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // One loop per job: a long run of one job never delays the lease checks of another.
        await Task.WhenAll(jobs.Select(job => PollLoopAsync(job, stoppingToken)));
    }

    private async Task PollLoopAsync(JobDescriptor job, CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Settings.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(job, stoppingToken);
                await Task.Delay(poll * (1 - PollJitter + (2 * PollJitter * Random.Shared.NextDouble())), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Store unavailable or similar: the next poll retries.
                logger.LogError(ex, "Lease check of job {Job} failed (owner {Owner}).", job.Name, Owner);
                await DelayQuietlyAsync(poll, stoppingToken);
            }
        }
    }

    private async Task<JobRunResult> RunAsync(JobDescriptor job, CancellationToken stoppingToken)
    {
        if (!Settings.IsJobEnabled(job.Name))
        {
            return JobRunResult.Disabled;
        }

        var acquisition = await leases.TryAcquireAsync(job.Name, Owner, stoppingToken);
        if (acquisition.Lease is not { } lease)
        {
            return acquisition.Refusal == LeaseRefusal.NotDue ? JobRunResult.NotDue : JobRunResult.HeldElsewhere;
        }

        metrics.JobLeaseAcquired(job.Name);
        logger.LogInformation("Acquired lease of job {Job}: owner {Owner} token {Token}.", job.Name, lease.Owner, lease.FencingToken);
        using var activity = PlatformMetrics.ActivitySource.StartActivity($"job {job.Name}");
        activity?.SetTag("gs.job", job.Name);
        activity?.SetTag("gs.fencing_token", lease.FencingToken);
        var started = Stopwatch.StartNew();

        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var stopRenewal = new CancellationTokenSource();
        var renewal = new LeaseRenewal(lease);
        var renewalTask = RenewLoopAsync(renewal, run, stopRenewal.Token);

        var result = await ExecuteJobAsync(job, new JobContext(job.Name, lease.FencingToken, run.Token), renewal, stoppingToken);

        await stopRenewal.CancelAsync();
        await renewalTask;
        if (renewal.Lost)
        {
            result = JobRunResult.LeaseLost;
        }
        else
        {
            var now = clock.UtcNow;
            var nextRunAt = result == JobRunResult.Cancelled ? now : now + Settings.Interval(job.Name, job.DefaultIntervalSeconds);
            await ReleaseAsync(renewal.Current, nextRunAt, result);
        }

        activity?.SetTag("gs.result", result.ToString());
        metrics.JobRun(job.Name, result.ToString(), started.Elapsed);
        return result;
    }

    private async Task<JobRunResult> ExecuteJobAsync(JobDescriptor job, JobContext context, LeaseRenewal renewal, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var instance = (ILeaseJob)ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, job.JobType);
            await instance.RunAsync(context);
            return JobRunResult.Completed;
        }
        catch (LeaseLostException) when (!renewal.Lost)
        {
            // The job found a larger fencing token where it stores progress: another instance holds the lease now.
            renewal.MarkLost();
            metrics.JobLeaseLost(job.Name);
            logger.LogWarning("Job {Job} was fenced out (owner {Owner}, token {Token}).", job.Name, Owner, context.FencingToken);
            return JobRunResult.LeaseLost;
        }
        catch (Exception) when (renewal.Lost)
        {
            return JobRunResult.LeaseLost;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Job {Job} stopped for shutdown (owner {Owner}, token {Token}).", job.Name, Owner, context.FencingToken);
            return JobRunResult.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Job} failed (owner {Owner}, token {Token}).", job.Name, Owner, context.FencingToken);
            return JobRunResult.Failed;
        }
    }

    /// <summary>
    /// Renews every <c>RenewSeconds</c>. The lease is lost when a renewal CAS is lost, or when no renewal succeeded before
    /// <c>leaseUntilUtc − 5 s</c> (another instance may take over at <c>leaseUntilUtc</c>); loss cancels the run.
    /// </summary>
    private async Task RenewLoopAsync(LeaseRenewal renewal, CancellationTokenSource run, CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(Settings.RenewSeconds);
        while (!stop.IsCancellationRequested)
        {
            var remaining = renewal.Current.LeaseUntil - JobSettings.RenewalSafetyMargin - clock.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining < period ? remaining : period, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (clock.UtcNow >= renewal.Current.LeaseUntil - JobSettings.RenewalSafetyMargin)
            {
                await LoseAsync(renewal, run, "not renewed before the safety margin");
                return;
            }

            try
            {
                if (await leases.TryRenewAsync(renewal.Current, stop) is { } renewed)
                {
                    renewal.Current = renewed;
                    continue;
                }

                await LoseAsync(renewal, run, "renewal compare-and-swap lost");
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Ambiguous or failed write: the next attempt re-reads and recognizes our own write (research R1).
                logger.LogWarning(ex, "Renewal of the lease of job {Job} failed (owner {Owner}, token {Token}); retrying.",
                    renewal.Current.JobName, renewal.Current.Owner, renewal.Current.FencingToken);
            }
        }
    }

    private async Task LoseAsync(LeaseRenewal renewal, CancellationTokenSource run, string reason)
    {
        renewal.MarkLost();
        metrics.JobLeaseLost(renewal.Current.JobName);
        logger.LogWarning(
            "Lost the lease of job {Job} ({Reason}): owner {Owner} token {Token}; cancelling the run.",
            renewal.Current.JobName,
            reason,
            renewal.Current.Owner,
            renewal.Current.FencingToken);
        await run.CancelAsync();
    }

    private async Task ReleaseAsync(JobLease lease, DateTimeOffset nextRunAt, JobRunResult result)
    {
        using var budget = new CancellationTokenSource(ReleaseBudget);
        try
        {
            await leases.ReleaseAsync(lease, nextRunAt, result.ToString(), budget.Token);
        }
        catch (Exception ex)
        {
            // Not released: another instance takes over after leaseUntilUtc.
            logger.LogWarning(ex, "Release of the lease of job {Job} failed (owner {Owner}, token {Token}).", lease.JobName, lease.Owner, lease.FencingToken);
        }
    }

    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Lease state shared by the run and its renewal loop.</summary>
    private sealed class LeaseRenewal(JobLease lease)
    {
        private volatile JobLease _current = lease;
        private volatile bool _lost;

        public JobLease Current
        {
            get => _current;
            set => _current = value;
        }

        public bool Lost => _lost;

        public void MarkLost() => _lost = true;
    }
}
