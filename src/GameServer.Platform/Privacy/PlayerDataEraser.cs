using System.Text.Json.Nodes;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Privacy;

namespace GameServer.Platform.Privacy;

/// <summary>
/// What a module's <see cref="IAccountDeletionStep"/> may do (research R10): find and rewrite documents of the collections its
/// own module declares, and scrub its own outbox kind inside the player's receipts. Every write is one fenced purge
/// transaction; <c>pa_*</c>, <c>receipts</c> (directly), <c>gm_audit</c>, platform and foreign collections are refused.
/// </summary>
public sealed class PlayerDataEraser : IPlayerDataEraser
{
    private readonly string _module;
    private readonly PurgeTicket _ticket;
    private readonly IAccountPurgeWriter _writer;
    private readonly IMaintenanceQueryStore _queries;
    private readonly PurgeTargets _targets;
    private readonly IReadOnlySet<string> _ownKinds;
    private readonly PurgeBudget _budget;

    public PlayerDataEraser(
        string module,
        IReadOnlySet<string> ownKinds,
        PurgeTicket ticket,
        IAccountPurgeWriter writer,
        IMaintenanceQueryStore queries,
        PurgeTargets targets,
        int batchSize,
        PurgeBudget budget)
    {
        _module = module;
        _ownKinds = ownKinds;
        _ticket = ticket;
        _writer = writer;
        _queries = queries;
        _targets = targets;
        _budget = budget;
        BatchSize = batchSize;
    }

    public string PlayerId => _ticket.PlayerId;

    public string AnonymousId => _ticket.AnonymousId;

    public int BatchSize { get; }

    /// <summary>
    /// At most <c>min(limit, BatchSize)</c> keys, so a find → rewrite loop never exceeds one transaction (review minor 10), and
    /// never more than the run's remaining budget; a spent budget ends the step for this run (<see cref="PurgeBudgetExhaustedException"/>).
    /// </summary>
    public async Task<IReadOnlyList<string>> FindAsync(string collection, string field, string value, int limit, CancellationToken cancellationToken)
    {
        EnsureOwned(collection);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var keys = await _queries.QueryReferencesAsync(new ReferenceQuery(collection, field, value, Math.Min(limit, NextBatch())), cancellationToken);
        return keys.Select(k => k.Key).ToArray();
    }

    public async Task RewriteAsync(string collection, IReadOnlyList<string> keys, Func<JsonObject, JsonObject?> rewrite, CancellationToken cancellationToken)
    {
        EnsureOwned(collection);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(rewrite);
        if (keys.Count > BatchSize)
        {
            throw new ArgumentException($"[module {_module}] RewriteAsync takes at most BatchSize ({BatchSize}) keys per transaction; got {keys.Count}.", nameof(keys));
        }

        if (keys.Count == 0)
        {
            return;
        }

        NextBatch();
        await _writer.RewriteAsync(_ticket, keys.Select(k => new DocumentKey(collection, k)).ToArray(), rewrite, cancellationToken);
        _budget.Consume(keys.Count);
    }

    public async Task ScrubOutboxAsync(string kind, Func<JsonNode?, JsonNode?> scrub, CancellationToken cancellationToken)
    {
        if (!_ownKinds.Contains(kind))
        {
            throw new InvalidOperationException($"[module {_module}] outbox kind '{kind}' is not a projection kind of this module; a deletion step scrubs only its own kinds.");
        }

        ArgumentNullException.ThrowIfNull(scrub);

        // Receipts already carry the anonymous id (the receipts step ran first); the scrub never changes playerId, so page by key.
        string? after = null;
        while (true)
        {
            var keys = await _queries.QueryReferencesAsync(
                new ReferenceQuery(ReceiptDocument.Collection, "playerId", _ticket.AnonymousId, NextBatch(), after),
                cancellationToken);
            if (keys.Count == 0)
            {
                return;
            }

            await _writer.ScrubOutboxAsync(_ticket, keys, kind, scrub, cancellationToken);
            _budget.Consume(keys.Count);
            after = keys[^1].Key;
        }
    }

    /// <summary>Documents the next transaction may hold; throws when the run's budget is spent (the step resumes next run).</summary>
    private int NextBatch()
    {
        var size = _budget.Next(BatchSize);
        return size > 0 ? size : throw new PurgeBudgetExhaustedException();
    }

    private void EnsureOwned(string collection)
    {
        var entry = _targets.EnsureModuleOwned(collection);
        if (entry.Owner != _module)
        {
            throw new InvalidOperationException(
                $"[module {_module}] collection '{collection}' belongs to module '{entry.Owner}'; a deletion step touches only its own module's collections.");
        }
    }
}
