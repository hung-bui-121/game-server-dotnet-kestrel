using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Jobs;

/// <summary>
/// A lease held by this instance. <see cref="Document"/> is the document as last written by the holder (its CAS guards
/// the next renewal or release).
/// </summary>
public sealed record JobLease(string JobName, string Owner, long FencingToken, DateTimeOffset LeaseUntil, StoredDocument Document);

/// <summary>Why an acquisition was refused.</summary>
public enum LeaseRefusal
{
    None,

    /// <summary>Released and its <c>nextRunAtUtc</c> is in the future.</summary>
    NotDue,

    /// <summary>Another owner holds an unexpired lease, or won the race for it.</summary>
    HeldElsewhere,
}

/// <summary>Result of <see cref="LeaseStore.TryAcquireAsync"/>: the lease, or why it was refused.</summary>
public sealed record LeaseAcquisition(JobLease? Lease, LeaseRefusal Refusal)
{
    public static LeaseAcquisition Refused(LeaseRefusal refusal) => new(null, refusal);
}

/// <summary>The lease was taken over by another instance; the run must stop (FR-003).</summary>
public sealed class LeaseLostException(string jobName, long token)
    : Exception($"Lease of job '{jobName}' with fencing token {token} was lost.")
{
    public string JobName { get; } = jobName;

    public long FencingToken { get; } = token;
}

/// <summary>
/// Job leases <c>leases/leases::{jobName}</c> (JOB-01, research R1): key-value documents written with CAS only, never with
/// expiry (DAT-03), times from <see cref="IClock"/> (TIME-01). Every acquisition increments the fencing token.
/// </summary>
public sealed class LeaseStore(IKeyValueStore store, IClock clock, GameServerOptions options, ILogger<LeaseStore> logger)
{
    public const string KeyPrefix = "leases::";

    public static DocumentKey KeyOf(string jobName) => new(PlatformCollections.Leases, KeyPrefix + jobName);

    /// <summary>Owner id of one runner: <c>{machine}:{pid}:{8 hex}</c>, unique per runner instance.</summary>
    public static string NewOwner() =>
        $"{Environment.MachineName}:{Environment.ProcessId}:{RandomNumberGenerator.GetHexString(8, lowercase: true)}";

    /// <summary>
    /// Acquires when the lease is missing (token 1), released and due (<c>nextRunAtUtc</c> &lt;= now), or held but expired
    /// (<c>leaseUntilUtc</c> &lt;= now, takeover). A lost insert/CAS race means another instance won: no retry.
    /// </summary>
    public async Task<LeaseAcquisition> TryAcquireAsync(string jobName, string owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var key = KeyOf(jobName);
        var now = clock.UtcNow;
        var current = await store.GetAsync(key, cancellationToken);
        if (current is null)
        {
            var created = Holding(new JsonObject { ["jobName"] = jobName, ["nextRunAtUtc"] = Format(now), ["fencingToken"] = 0L }, owner, 1, now);
            if (!await store.InsertAsync(key, created, null, cancellationToken))
            {
                return LeaseAcquisition.Refused(LeaseRefusal.HeldElsewhere);
            }

            return await ConfirmAsync(jobName, owner, 1, cancellationToken) is { } inserted
                ? new LeaseAcquisition(inserted, LeaseRefusal.None)
                : LeaseAcquisition.Refused(LeaseRefusal.HeldElsewhere);
        }

        var state = LeaseState.From(current.Content);
        if (state.Owner is not null && state.LeaseUntil > now)
        {
            return LeaseAcquisition.Refused(LeaseRefusal.HeldElsewhere);
        }

        if (state.Owner is null && state.NextRunAt > now)
        {
            return LeaseAcquisition.Refused(LeaseRefusal.NotDue);
        }

        var token = state.FencingToken + 1;
        if (!await store.ReplaceAsync(current, Holding(current.Content, owner, token, now), null, cancellationToken))
        {
            return LeaseAcquisition.Refused(LeaseRefusal.HeldElsewhere);
        }

        if (state.Owner is not null)
        {
            logger.LogWarning(
                "Took over expired lease of job {Job} from owner {PreviousOwner} (token {PreviousToken}); owner {Owner} token {Token}.",
                jobName,
                state.Owner,
                state.FencingToken,
                owner,
                token);
        }

        return await ConfirmAsync(jobName, owner, token, cancellationToken) is { } acquired
            ? new LeaseAcquisition(acquired, LeaseRefusal.None)
            : LeaseAcquisition.Refused(LeaseRefusal.HeldElsewhere);
    }

    /// <summary>
    /// Extends the lease (same token). A CAS mismatch is re-read: the same owner and token mean an earlier ambiguous write of
    /// ours landed, and the renewal is retried on the fresh document; anything else means the lease is lost (null).
    /// </summary>
    public async Task<JobLease?> TryRenewAsync(JobLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var document = lease.Document;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = clock.UtcNow;
            var content = Clone(document.Content);
            content["leaseUntilUtc"] = Format(now + LeaseDuration);
            content["renewedAtUtc"] = Format(now);
            if (await store.ReplaceAsync(document, content, null, cancellationToken))
            {
                return await ConfirmAsync(lease.JobName, lease.Owner, lease.FencingToken, cancellationToken);
            }

            var fresh = await store.GetAsync(KeyOf(lease.JobName), cancellationToken);
            if (fresh is null || !IsHeldBy(LeaseState.From(fresh.Content), lease.Owner, lease.FencingToken))
            {
                return null;
            }

            document = fresh;
        }

        return null;
    }

    /// <summary>
    /// Releases the lease: owner null, <c>leaseUntilUtc = now</c>, <paramref name="nextRunAt"/> and the run summary. Returns false
    /// when the lease is no longer ours (taken over), in which case nothing is written.
    /// </summary>
    public async Task<bool> ReleaseAsync(JobLease lease, DateTimeOffset nextRunAt, string result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var document = lease.Document;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = clock.UtcNow;
            var content = Clone(document.Content);
            content["owner"] = null;
            content["leaseUntilUtc"] = Format(now);
            content["nextRunAtUtc"] = Format(nextRunAt);
            content["lastRun"] = new JsonObject
            {
                ["startedAtUtc"] = content["acquiredAtUtc"]?.DeepClone(),
                ["completedAtUtc"] = Format(now),
                ["result"] = result,
            };
            if (await store.ReplaceAsync(document, content, null, cancellationToken))
            {
                logger.LogInformation(
                    "Released lease of job {Job}: owner {Owner} token {Token}, result {Result}, next run at {NextRunAt}.",
                    lease.JobName,
                    lease.Owner,
                    lease.FencingToken,
                    result,
                    nextRunAt);
                return true;
            }

            var fresh = await store.GetAsync(KeyOf(lease.JobName), cancellationToken);
            if (fresh is null || !IsHeldBy(LeaseState.From(fresh.Content), lease.Owner, lease.FencingToken))
            {
                logger.LogWarning(
                    "Lease of job {Job} (owner {Owner}, token {Token}) was taken over before release.", lease.JobName, lease.Owner, lease.FencingToken);
                return false;
            }

            document = fresh;
        }

        return false;
    }

    /// <summary>
    /// Whether the lease document currently names an owner with <paramref name="fencingToken"/>: lets a job that met a larger
    /// token where it stores progress tell a lost lease (the document moved on) from progress written under an older,
    /// recreated lease document (its token restarted at 1, see PCR platform §16 "never delete <c>leases::*</c>").
    /// </summary>
    public async Task<bool> IsHeldAsync(string jobName, long fencingToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        var document = await store.GetAsync(KeyOf(jobName), cancellationToken);
        return document is not null && LeaseState.From(document.Content) is { Owner: not null } state && state.FencingToken == fencingToken;
    }

    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(options.Jobs.LeaseSeconds);

    private JsonObject Holding(JsonObject previous, string owner, long token, DateTimeOffset now)
    {
        var content = Clone(previous);
        content["owner"] = owner;
        content["fencingToken"] = token;
        content["leaseUntilUtc"] = Format(now + LeaseDuration);
        content["acquiredAtUtc"] = Format(now);
        content["renewedAtUtc"] = Format(now);
        content["gameId"] = options.GameId;
        content["environmentId"] = options.EnvironmentId;
        return content;
    }

    /// <summary>
    /// Reads back the document just written: the key-value store returns no new CAS, and the next renewal needs it. Only the
    /// holder writes a valid lease, so a different owner or token here means the write was overtaken.
    /// </summary>
    private async Task<JobLease?> ConfirmAsync(string jobName, string owner, long token, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(KeyOf(jobName), cancellationToken);
        if (document is null)
        {
            return null;
        }

        var state = LeaseState.From(document.Content);
        return IsHeldBy(state, owner, token) ? new JobLease(jobName, owner, token, state.LeaseUntil, document) : null;
    }

    private static bool IsHeldBy(LeaseState state, string owner, long token) => state.Owner == owner && state.FencingToken == token;

    private static string Format(DateTimeOffset time) => AggregateDocumentSerializer.FormatTime(time);

    private static JsonObject Clone(JsonObject content) => (JsonObject)content.DeepClone();

    private sealed record LeaseState(string? Owner, long FencingToken, DateTimeOffset LeaseUntil, DateTimeOffset NextRunAt)
    {
        public static LeaseState From(JsonObject content) => new(
            content["owner"]?.GetValue<string>(),
            content["fencingToken"]?.GetValue<long>() ?? 0,
            Time(content["leaseUntilUtc"]),
            Time(content["nextRunAtUtc"]));

        private static DateTimeOffset Time(JsonNode? node) =>
            node?.GetValue<string>() is { } value ? AggregateDocumentSerializer.ParseTime(value) : DateTimeOffset.MinValue;
    }
}
