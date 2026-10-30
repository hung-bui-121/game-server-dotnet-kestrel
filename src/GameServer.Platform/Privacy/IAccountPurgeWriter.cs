using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Privacy;

/// <summary>
/// The only writer of the account purge (DAT-09 purge path, plan D3): every call is one transaction that also rewrites the
/// deletion record after checking <c>record.fencingToken &lt;= ticket.FencingToken</c> and storing the ticket's token
/// (FR-003, FR-017). Never creates <c>pa_*</c> or receipt documents; never passes the caller's token into the commit
/// (PERF-02). Writes through <see cref="ITransactionalDocumentStore"/> directly: it is the single, field-level exception to
/// "audit entries are immutable" (Complexity Tracking).
/// </summary>
public interface IAccountPurgeWriter
{
    /// <summary>
    /// No record: loads profile and record in one transaction, calls <paramref name="start"/> (pure; null = not eligible),
    /// writes the profile (rev + 1) and inserts the record (purging, new anonymous id, <c>nextAttemptAtUtc = now</c>).
    /// Record purging: claims it for <paramref name="fencingToken"/>. Record purged, or no profile: null.
    /// </summary>
    Task<PurgeTicket?> BeginAsync<TProfile>(string playerId, long fencingToken, Func<TProfile, PurgeStart<TProfile>?> start, CancellationToken cancellationToken)
        where TProfile : class;

    /// <summary>Applies <paramref name="action"/> to up to <c>BatchSize</c> documents (missing = done); returns the documents written.</summary>
    Task<int> ApplyAsync(PurgeTicket ticket, IReadOnlyList<DocumentKey> keys, PurgeAction action, CancellationToken cancellationToken);

    /// <summary>Module step rewrite of up to <c>BatchSize</c> documents of module collections: new content, or null = remove.</summary>
    Task<int> RewriteAsync(PurgeTicket ticket, IReadOnlyList<DocumentKey> keys, Func<JsonObject, JsonObject?> rewrite, CancellationToken cancellationToken);

    /// <summary>Rewrites the payloads of <paramref name="kind"/> outbox entries inside up to <c>BatchSize</c> of the player's receipts.</summary>
    Task<int> ScrubOutboxAsync(PurgeTicket ticket, IReadOnlyList<DocumentKey> receiptKeys, string kind, Func<JsonNode?, JsonNode?> scrub, CancellationToken cancellationToken);

    /// <summary>Records <paramref name="step"/> as finished; a resumed purge skips it.</summary>
    Task<PurgeTicket> CompleteStepAsync(PurgeTicket ticket, string step, CancellationToken cancellationToken);

    /// <summary>Removes <c>pa_profile</c> and turns the record into the purge record (purged; anonymous id and progress dropped).</summary>
    Task CompleteAsync(PurgeTicket ticket, CancellationToken cancellationToken);

    /// <summary><c>attempts + 1</c>, <c>lastFailure {step, errorType, atUtc}</c>, <c>nextAttemptAtUtc = now + backoff</c>.</summary>
    Task RecordFailureAsync(PurgeTicket ticket, string step, Exception error, CancellationToken cancellationToken);
}
