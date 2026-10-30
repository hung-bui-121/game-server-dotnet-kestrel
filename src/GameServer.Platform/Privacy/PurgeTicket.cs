namespace GameServer.Platform.Privacy;

/// <summary>
/// A job's claim on one player's purge (research R6): every purge write carries it and is refused when the deletion record
/// holds a larger fencing token. <see cref="AnonymousId"/> is never logged; <see cref="ToString"/> leaves it out.
/// </summary>
public sealed record PurgeTicket(string PlayerId, string AnonymousId, long FencingToken, IReadOnlySet<string> CompletedSteps)
{
    public bool IsCompleted(string step) => CompletedSteps.Contains(step);

    /// <summary>Without the anonymous id: a log line holding both ids would keep the link FR-018 discards.</summary>
    public override string ToString() => $"PurgeTicket {{ PlayerId = {PlayerId}, FencingToken = {FencingToken}, CompletedSteps = {CompletedSteps.Count} }}";
}

/// <summary>Profile change that marks the player purging, plus the dates copied into the deletion record.</summary>
public sealed record PurgeStart<TProfile>(TProfile Profile, DateTimeOffset RequestedAtUtc, DateTimeOffset PurgeAfterUtc)
    where TProfile : class;

/// <summary>What <see cref="IAccountPurgeWriter.ApplyAsync"/> does to each document; must match the collection's treatment.</summary>
public enum PurgeAction
{
    /// <summary>Every string equal to the player id becomes the anonymous id (receipts: plus the receipt rules, D1).</summary>
    Anonymize,

    /// <summary>The document is removed.</summary>
    Remove,
}

/// <summary>
/// The deletion record holds a larger fencing token than the ticket: another lease holder owns this purge now (FR-003). The
/// job confirms the lease is lost and stops its run without recording a failure.
/// </summary>
public sealed class PurgeFencedOutException(string playerId, long token, long current)
    : Exception($"Purge of player {playerId} with fencing token {token} is fenced out (record token {current}).")
{
    public string PlayerId { get; } = playerId;

    public long FencingToken { get; } = token;

    public long CurrentToken { get; } = current;
}

/// <summary>
/// The deletion record is already <c>purged</c> and holds no larger token than the ticket: an earlier call of this claim
/// completed the purge (typically a final commit reported as ambiguous that did land). Nothing is written; the purge is done.
/// </summary>
public sealed class PurgeAlreadyCompletedException(string playerId)
    : Exception($"Purge of player {playerId} is already complete.")
{
    public string PlayerId { get; } = playerId;
}
