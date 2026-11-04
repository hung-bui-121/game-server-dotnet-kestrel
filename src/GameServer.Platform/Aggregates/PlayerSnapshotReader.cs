using GameServer.Aggregates;
using GameServer.Errors;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Aggregates;

/// <summary>Readable aggregates (in read order) and the failures of one read, ordered by aggregate id (FG-029).</summary>
public sealed record PlayerSnapshot(IReadOnlyList<LoadedAggregate> Sections, IReadOnlyList<AggregateReadFailure> Unavailable);

/// <summary>
/// Reads client-visible aggregates outside a transaction, in parallel (PLY-02, DAT-11). Each aggregate is
/// consistent at its own <c>rev</c>; no cross-aggregate consistency is promised (API-10). A stored aggregate that cannot be read
/// throws its typed read failure from the <c>Read*</c> members and is listed by the <c>Read*AvailableAsync</c> members (FG-029).
/// </summary>
public sealed class PlayerSnapshotReader(ModuleRegistry registry, ITransactionalDocumentStore store, AggregateDocumentSerializer serializer)
{
    /// <summary>Throws the typed read failure of the first broken aggregate (BC-5).</summary>
    public async Task<IReadOnlyList<LoadedAggregate>> ReadAsync(string playerId, IReadOnlyList<string>? aggregateIds, CancellationToken cancellationToken)
    {
        var descriptors = Requested(aggregateIds);
        var documents = await ReadDocumentsAsync(descriptors, playerId, cancellationToken);
        return descriptors.Select((d, i) => serializer.Load(d, playerId, documents[i])).ToArray();
    }

    /// <summary>Reads every registered aggregate of a player, hidden ones included (GM inspection, GM-02); throws like <see cref="ReadAsync"/>.</summary>
    public async Task<IReadOnlyList<LoadedAggregate>> ReadAllAsync(string playerId, CancellationToken cancellationToken)
    {
        var descriptors = All();
        var documents = await ReadDocumentsAsync(descriptors, playerId, cancellationToken);
        return descriptors.Select((d, i) => serializer.Load(d, playerId, documents[i])).ToArray();
    }

    /// <summary>Reads one aggregate of a player (service use by other modules, ARC-04); throws the typed read failure.</summary>
    public async Task<LoadedAggregate> ReadOneAsync(string playerId, string aggregateId, CancellationToken cancellationToken)
    {
        var descriptor = registry.GetAggregate(aggregateId);
        var document = await store.GetAsync(AggregateDocumentSerializer.KeyFor(descriptor, playerId), cancellationToken);
        return serializer.Load(descriptor, playerId, document);
    }

    /// <summary>
    /// Like <see cref="ReadAsync"/>, but a broken aggregate is listed (reported once) instead of failing the read. Unknown or
    /// hidden ids are still <c>BAD_REQUEST</c>.
    /// </summary>
    public async Task<PlayerSnapshot> ReadAvailableAsync(string playerId, IReadOnlyList<string>? aggregateIds, CancellationToken cancellationToken)
    {
        var descriptors = Requested(aggregateIds);
        return Load(descriptors, playerId, await ReadDocumentsAsync(descriptors, playerId, cancellationToken));
    }

    /// <summary>Like <see cref="ReadAllAsync"/> (GM), with broken aggregates listed.</summary>
    public async Task<PlayerSnapshot> ReadAllAvailableAsync(string playerId, CancellationToken cancellationToken)
    {
        var descriptors = All();
        return Load(descriptors, playerId, await ReadDocumentsAsync(descriptors, playerId, cancellationToken));
    }

    private PlayerSnapshot Load(PlayerAggregateDescriptor[] descriptors, string playerId, IReadOnlyList<StoredDocument?> documents)
    {
        var sections = new List<LoadedAggregate>(descriptors.Length);
        var unavailable = new List<AggregateReadFailure>();
        for (var i = 0; i < descriptors.Length; i++)
        {
            if (serializer.TryLoad(descriptors[i], playerId, documents[i], out var loaded, out var failure))
            {
                sections.Add(loaded);
            }
            else
            {
                unavailable.Add(failure);
            }
        }

        // Sections keep the read order (an explicit list keeps the caller's order, as ReadAsync); failures are ordered by id (wire-changes §2).
        return new PlayerSnapshot(sections, unavailable.OrderBy(f => f.AggregateId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Every client-visible aggregate, or the requested ones, ordered by id (one parallel KV read, PLY-02).</summary>
    private PlayerAggregateDescriptor[] Requested(IReadOnlyList<string>? aggregateIds) =>
        aggregateIds is null || aggregateIds.Count == 0
            ? registry.Aggregates.Values.Where(a => a.ClientVisible).OrderBy(a => a.AggregateId, StringComparer.Ordinal).ToArray()
            : aggregateIds.Distinct(StringComparer.Ordinal).Select(id =>
                registry.Aggregates.TryGetValue(id, out var d) && d.ClientVisible
                    ? d
                    : throw new GameServerException(CoreErrorCodes.BadRequest, $"Unknown or hidden section '{id}'.")).ToArray();

    private PlayerAggregateDescriptor[] All() => registry.Aggregates.Values.OrderBy(a => a.AggregateId, StringComparer.Ordinal).ToArray();

    private Task<IReadOnlyList<StoredDocument?>> ReadDocumentsAsync(PlayerAggregateDescriptor[] descriptors, string playerId, CancellationToken cancellationToken) =>
        store.GetManyAsync(descriptors.Select(d => AggregateDocumentSerializer.KeyFor(d, playerId)).ToArray(), cancellationToken);
}
