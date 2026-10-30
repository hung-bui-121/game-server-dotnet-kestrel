using System.Globalization;
using GameServer.Jobs;
using GameServer.Platform;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Jobs;
using GameServer.Platform.Persistence;
using GameServer.Platform.Privacy;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Modules.Identity;

/// <summary>
/// <c>identity.account-purge</c> (PRIV-01, FR-013, research R4/R7): decides <em>when</em> accounts are purged. Each run takes
/// the purges due for a retry or resume first, then pending deletions whose grace period is over, up to
/// <c>MaxPlayersPerRun</c> and <c>MaxDocumentsPerRun</c>; per player it marks the purge started (or claims it) and lets the
/// platform's <see cref="AccountPurgeService"/> erase the data. A larger fencing token on a purge record ends the run as a
/// lost lease once the lease document confirms another holder.
/// </summary>
public sealed class AccountPurgeJob(
    IMaintenanceQueryStore queries,
    AccountDeletionRecords records,
    IAccountPurgeWriter writer,
    AccountPurgeService purges,
    LeaseStore leases,
    GameServerOptions options,
    IClock clock,
    PlatformMetrics metrics,
    ILogger<AccountPurgeJob> logger) : ILeaseJob
{
    public const string JobName = "identity.account-purge";

    /// <summary>Cluster-wide time between finished runs (≈ 7 200 players/day at the default limits).</summary>
    public const int DefaultIntervalSeconds = 600;

    /// <summary>Due field of pending deletions, served by <c>ix_profile_deletion_due</c> (partial: not started).</summary>
    public const string PurgeAfterField = "state.deleted.purgeAfterUtc";

    public const string PurgeStartedField = "state.deleted.purgeStartedAtUtc";

    /// <summary>Step id (metric label) of a purge that failed before its record could be claimed.</summary>
    public const string BeginStep = "begin";

    /// <summary>
    /// The overdue gauge outlives the run by this many job intervals: the next run (possibly on another instance) replaces
    /// it; when no run happens at all the value disappears (alert on absence).
    /// </summary>
    private const int OverdueValidIntervals = 2;

    public async Task RunAsync(IJobContext context)
    {
        var cancellation = context.Cancellation;
        var settings = options.Purge;
        var now = clock.UtcNow;
        var (due, oldestPurgeAfter) = await FindDueAsync(now, settings.MaxPlayersPerRun, cancellation);
        metrics.SetAccountPurgeOverdue(
            oldestPurgeAfter is { } oldest ? now - oldest : TimeSpan.Zero,
            options.Jobs.Interval(JobName, DefaultIntervalSeconds) * OverdueValidIntervals);
        logger.LogInformation("Account purge run (token {Token}): {Due} players due.", context.FencingToken, due.Count);

        var budget = new PurgeBudget(settings.MaxDocumentsPerRun);
        foreach (var playerId in due)
        {
            if (budget.IsExhausted)
            {
                break;
            }

            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (await BeginAsync(playerId, context.FencingToken, now, cancellation) is { } ticket)
                {
                    await purges.RunAsync(ticket, budget, cancellation);
                }
            }
            catch (PurgeFencedOutException ex)
            {
                metrics.AccountPurge("fenced_out");
                await OnFencedOutAsync(context, ex);
            }
        }
    }

    /// <summary>
    /// A purge record holds a larger token than this run. When the lease document moved on, another instance holds the lease
    /// (FR-003): stop at once, release nothing. When it still names this run's token, the lease document was recreated and its
    /// token restarted below the tokens stored in purge records: skipping the player is the only safe choice (writing would
    /// break fencing); it stays stuck until an operator raises the lease token (runbook in PCR platform §16).
    /// </summary>
    private async Task OnFencedOutAsync(IJobContext context, PurgeFencedOutException fenced)
    {
        if (!await leases.IsHeldAsync(context.JobName, context.FencingToken, context.Cancellation))
        {
            logger.LogWarning(
                "Account purge run stopped: player {PlayerId} is claimed by fencing token {Current}, this run holds {Token}.",
                fenced.PlayerId,
                fenced.CurrentToken,
                fenced.FencingToken);
            throw new LeaseLostException(context.JobName, context.FencingToken);
        }

        logger.LogError(
            "Purge of player {PlayerId} skipped: its record holds fencing token {Current} but the lease document of job {Job} is at {Token} " +
            "while this run holds it. The lease document was deleted or reset; raise its fencingToken above the record's (never delete leases::*).",
            fenced.PlayerId,
            fenced.CurrentToken,
            context.JobName,
            fenced.FencingToken);
    }

    /// <summary>
    /// Marks the purge started or claims it; null when not eligible. A player whose purge cannot even begin (store error,
    /// unreadable profile) is logged and skipped so it never blocks the players behind it; the next run tries it again.
    /// </summary>
    private async Task<PurgeTicket?> BeginAsync(string playerId, long fencingToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await writer.BeginAsync<ProfileState>(playerId, fencingToken, profile => Start(profile, now), cancellationToken);
        }
        catch (Exception ex) when (ex is not PurgeFencedOutException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            metrics.AccountPurgeStepFailed(BeginStep);
            metrics.AccountPurge("failed");
            logger.LogError("Purge of player {PlayerId} could not begin ({ErrorType}); retried next run.", playerId, ex.GetType().FullName);
            return null;
        }
    }

    /// <summary>
    /// Resumes and retries first (a failed player waits for its backoff), then newly due pending deletions. The oldest
    /// <c>purgeAfter</c> covers both lists, and the pending one is always queried (one entry beyond the run's limit), so a full
    /// resume list never hides an older pending deletion from the overdue gauge (review minor 5).
    /// </summary>
    private async Task<(IReadOnlyList<string> Due, DateTimeOffset? OldestPurgeAfter)> FindDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var resumed = await records.QueryDueAsync(now, limit, cancellationToken);
        var pending = await queries.QueryDueAsync(
            new DueQuery(IdentityAggregates.ProfileDescriptor.Collection, PurgeAfterField, now, Math.Max(1, limit - resumed.Count), PurgeStartedField, DueTimeFormat.Json),
            cancellationToken);
        var purgeAfter = resumed.Select(r => r.PurgeAfterUtc).Concat(pending.Select(PurgeAfterOf)).ToArray();
        var due = resumed.Select(r => r.PlayerId)
            .Concat(pending.Select(p => p.Key.Key))
            .Distinct(StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return (due, purgeAfter.Length == 0 ? null : purgeAfter.Min());
    }

    private static DateTimeOffset PurgeAfterOf(StoredDocument profile) =>
        DateTimeOffset.Parse(PageCursor.StringAt(profile.Content, PurgeAfterField)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static PurgeStart<ProfileState>? Start(ProfileState profile, DateTimeOffset now) =>
        AccountPurgeStart.Decide(profile, now) is { Deleted: { } deleted } marked
            ? new PurgeStart<ProfileState>(marked, deleted.RequestedAtUtc, deleted.PurgeAfterUtc)
            : null;
}
