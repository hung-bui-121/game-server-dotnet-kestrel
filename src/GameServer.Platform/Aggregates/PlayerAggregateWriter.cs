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
}

public sealed class PlayerAggregateWriter(
    ModuleRegistry registry,
    ITransactionalDocumentStore store,
    AggregateDocumentSerializer serializer,
    IClock clock,
    GameServerOptions options,
    PlatformMetrics metrics) : IPlayerAggregateWriter
{
    public async Task<(TState State, long Rev)> UpdateAsync<TState>(
        string playerId,
        string aggregateId,
        Func<TState, TState> mutate,
        CancellationToken cancellationToken)
        where TState : class
    {
        var descriptor = registry.GetAggregate(aggregateId);
        var key = AggregateDocumentSerializer.KeyFor(descriptor, playerId);
        cancellationToken.ThrowIfCancellationRequested();

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
}
