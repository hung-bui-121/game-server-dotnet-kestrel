using GameServer.Errors;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Aggregates;

/// <summary>
/// Reads client-visible aggregates outside a transaction, in parallel (PLY-02, DAT-11). Each aggregate is
/// consistent at its own <c>rev</c>; no cross-aggregate consistency is promised (API-10).
/// </summary>
public sealed class PlayerSnapshotReader(ModuleRegistry registry, ITransactionalDocumentStore store, AggregateDocumentSerializer serializer)
{
    public async Task<IReadOnlyList<LoadedAggregate>> ReadAsync(string playerId, IReadOnlyList<string>? aggregateIds, CancellationToken cancellationToken)
    {
        var descriptors = aggregateIds is null || aggregateIds.Count == 0
            ? registry.Aggregates.Values.Where(a => a.ClientVisible).OrderBy(a => a.AggregateId, StringComparer.Ordinal).ToArray()
            : aggregateIds.Distinct(StringComparer.Ordinal).Select(id =>
                registry.Aggregates.TryGetValue(id, out var d) && d.ClientVisible
                    ? d
                    : throw new GameServerException(CoreErrorCodes.BadRequest, $"Unknown or hidden section '{id}'.")).ToArray();

        var documents = await store.GetManyAsync(descriptors.Select(d => AggregateDocumentSerializer.KeyFor(d, playerId)).ToArray(), cancellationToken);
        return descriptors.Select((d, i) => serializer.Load(d, playerId, documents[i])).ToArray();
    }

    /// <summary>Reads one aggregate of a player (service use by other modules, ARC-04).</summary>
    public async Task<LoadedAggregate> ReadOneAsync(string playerId, string aggregateId, CancellationToken cancellationToken)
    {
        var descriptor = registry.GetAggregate(aggregateId);
        var document = await store.GetAsync(AggregateDocumentSerializer.KeyFor(descriptor, playerId), cancellationToken);
        return serializer.Load(descriptor, playerId, document);
    }
}
