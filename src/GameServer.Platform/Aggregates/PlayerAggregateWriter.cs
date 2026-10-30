using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Time;

namespace GameServer.Platform.Aggregates;

/// <summary>
/// Writes one player aggregate in its own transaction without a receipt — for framework operations that are not
/// player commands: login session epoch, account purge (DAT-09). Commands use <c>ICommandRunner</c>.
/// </summary>
public interface IPlayerAggregateWriter
{
    /// <summary>Reads, mutates and writes the aggregate (rev + 1 when changed). Returns the committed state and rev.</summary>
    Task<(TState State, long Rev)> UpdateAsync<TState>(string playerId, string aggregateId, Func<TState, TState> mutate, CancellationToken cancellationToken)
        where TState : class;

    /// <summary>
    /// Like <see cref="UpdateAsync{TState}(string, string, Func{TState, TState}, CancellationToken)"/>, but an aggregate
    /// that does not exist yet is only created while <paramref name="creationAnchor"/> exists and names the same
    /// <c>playerId</c>; the anchor is then rewritten unchanged in the same transaction, so the creation write-conflicts with
    /// a concurrent removal of the anchor (research R9: a login racing the account purge cannot recreate the profile).
    /// When the aggregate exists the anchor is not read. Throws <see cref="AggregateAnchorMissingException"/> (nothing
    /// written) when the anchor is missing or belongs to another player.
    /// </summary>
    Task<(TState State, long Rev)> UpdateAsync<TState>(
        string playerId,
        string aggregateId,
        Func<TState, TState> mutate,
        DocumentKey? creationAnchor,
        CancellationToken cancellationToken)
        where TState : class;
}

/// <summary>The creation anchor of a missing aggregate is gone or names another player; nothing was written.</summary>
public sealed class AggregateAnchorMissingException(DocumentKey anchor)
    : Exception($"Creation anchor {anchor} is missing or belongs to another player.")
{
    public DocumentKey Anchor { get; } = anchor;
}

public sealed class PlayerAggregateWriter(
    ModuleRegistry registry,
    ITransactionalDocumentStore store,
    AggregateDocumentSerializer serializer,
    IClock clock,
    GameServerOptions options,
    PlatformMetrics metrics) : IPlayerAggregateWriter
{
    private const string AnchorPlayerField = "playerId";

    public Task<(TState State, long Rev)> UpdateAsync<TState>(
        string playerId,
        string aggregateId,
        Func<TState, TState> mutate,
        CancellationToken cancellationToken)
        where TState : class =>
        UpdateAsync(playerId, aggregateId, mutate, null, cancellationToken);

    public async Task<(TState State, long Rev)> UpdateAsync<TState>(
        string playerId,
        string aggregateId,
        Func<TState, TState> mutate,
        DocumentKey? creationAnchor,
        CancellationToken cancellationToken)
        where TState : class
    {
        var descriptor = registry.GetAggregate(aggregateId);
        var key = AggregateDocumentSerializer.KeyFor(descriptor, playerId);
        cancellationToken.ThrowIfCancellationRequested();

        for (var round = 1; ; round++)
        {
            try
            {
                return await UpdateOnceAsync(descriptor, key, playerId, aggregateId, mutate, creationAnchor);
            }
            catch (Exception ex) when (round < MaxRaceRounds && ex is DocumentExistsException or StaleReadException)
            {
                // Two first writes raced on a missing document (Couchbase reports "exists"), or a stale bundle read: re-run.
            }
        }
    }

    private const int MaxRaceRounds = 3;

    private async Task<(TState State, long Rev)> UpdateOnceAsync<TState>(
        GameServer.Aggregates.PlayerAggregateDescriptor descriptor,
        DocumentKey key,
        string playerId,
        string aggregateId,
        Func<TState, TState> mutate,
        DocumentKey? creationAnchor)
        where TState : class
    {
        // The request token is not passed to the commit (PERF-02).
        return await store.RunTransactionAsync(async tx =>
        {
            var loaded = serializer.Load(descriptor, playerId, (await tx.GetManyAsync([key]))[0]);
            var next = mutate((TState)AggregateDocumentSerializer.DeserializeState(loaded.StateJson, descriptor.StateType));
            var json = AggregateDocumentSerializer.SerializeState(next, descriptor.StateType);
            if (json.AsSpan().SequenceEqual(loaded.StateJson))
            {
                return (next, loaded.Rev);
            }

            var (document, size) = serializer.BuildDocument(loaded, json, loaded.Rev + 1, clock.UtcNow);
            if (size > descriptor.HardBytes)
            {
                throw new GameServerException(CoreErrorCodes.AggregateTooLarge, $"{aggregateId} of {playerId} would be {size} bytes.");
            }

            if (loaded.Document is null)
            {
                if (creationAnchor is { } anchorKey)
                {
                    await RewriteAnchorAsync(tx, anchorKey, playerId);
                }

                await tx.InsertAsync(key, document);
            }
            else
            {
                await tx.ReplaceAsync(loaded.Document, document);
            }

            metrics.AggregateWritten(aggregateId, size, size >= descriptor.WarnBytes);
            return (next, loaded.Rev + 1);
        }, new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds)));
    }

    /// <summary>A read alone is not conflict-detected by Couchbase transactions; the unchanged rewrite is (write lock).</summary>
    private static async Task RewriteAnchorAsync(ITransactionContext tx, DocumentKey anchorKey, string playerId)
    {
        var anchor = (await tx.GetManyAsync([anchorKey]))[0];
        var owner = anchor?.Content[AnchorPlayerField] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;
        if (anchor is null || !string.Equals(owner, playerId, StringComparison.Ordinal))
        {
            throw new AggregateAnchorMissingException(anchorKey);
        }

        await tx.ReplaceAsync(anchor, (JsonObject)anchor.Content.DeepClone());
    }
}
