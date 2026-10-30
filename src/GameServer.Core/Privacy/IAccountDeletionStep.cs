using System.Text.Json.Nodes;

namespace GameServer.Privacy;

/// <summary>
/// A module's part of an account purge (PRIV-01, FR-020), declared in <c>ModuleDescriptor.DeletionSteps</c>. Runs after the
/// player's receipts and anonymized collections, before identities and the profile are removed.
/// </summary>
public interface IAccountDeletionStep
{
    /// <summary>Must be idempotent: a resumed purge runs the step again from the start.</summary>
    ValueTask RunAsync(IPlayerDataEraser eraser, CancellationToken cancellationToken);
}

/// <summary>
/// Fenced, transactional access of a deletion step to the collections its own module declares. Never <c>pa_*</c>,
/// <c>receipts</c>, <c>gm_audit</c> or another module's collections.
/// </summary>
public interface IPlayerDataEraser
{
    /// <summary>Id of the player being purged.</summary>
    string PlayerId { get; }

    /// <summary>Replacement for the player id in records that are kept. Never log it.</summary>
    string AnonymousId { get; }

    /// <summary>Maximum keys per call of <see cref="RewriteAsync"/>.</summary>
    int BatchSize { get; }

    /// <summary>
    /// Keys (at most <c>min(limit, BatchSize)</c>) of the module's collection whose <paramref name="field"/> equals
    /// <paramref name="value"/>. When the purge run's document budget is spent, this and the other calls throw; let the
    /// exception propagate — the step runs again from the start in the next run.
    /// </summary>
    Task<IReadOnlyList<string>> FindAsync(string collection, string field, string value, int limit, CancellationToken cancellationToken);

    /// <summary>One fenced transaction: <paramref name="rewrite"/> returns the new content, or null to remove the document.</summary>
    Task RewriteAsync(string collection, IReadOnlyList<string> keys, Func<JsonObject, JsonObject?> rewrite, CancellationToken cancellationToken);

    /// <summary>Rewrites payloads of the module's own projection kind inside the player's (already anonymized) receipts.</summary>
    Task ScrubOutboxAsync(string kind, Func<JsonNode?, JsonNode?> scrub, CancellationToken cancellationToken);
}
