using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Commands;

/// <summary>
/// <see cref="ICommandContext"/> of one transaction attempt. Buffers state changes, claim markers and
/// outbox entries; the runner writes them only if the handler succeeds.
/// </summary>
internal sealed class CommandContext : ICommandContext
{
    private readonly IReadOnlyDictionary<string, LoadedAggregate> _loaded;
    private readonly CommandDescriptor _descriptor;
    private readonly ITransactionContext _transaction;
    private readonly string _receiptId;
    private readonly Dictionary<string, byte[]> _newStates = new(StringComparer.Ordinal);
    private readonly Dictionary<DocumentKey, JsonObject> _claims = [];
    private readonly List<OutboxEntry> _outbox = [];

    public CommandContext(
        ActorContext actor,
        DateTimeOffset now,
        IGameConfig config,
        IReadOnlyDictionary<string, LoadedAggregate> loaded,
        CommandDescriptor descriptor,
        ITransactionContext transaction,
        string receiptId)
    {
        Actor = actor;
        Now = now;
        Config = config;
        _loaded = loaded;
        _descriptor = descriptor;
        _transaction = transaction;
        _receiptId = receiptId;
    }

    public ActorContext Actor { get; }

    public DateTimeOffset Now { get; }

    public string ConfigVersion => Config.ConfigVersion;

    public IGameConfig Config { get; }

    public IReadOnlyDictionary<DocumentKey, JsonObject> Claims => _claims;

    public IReadOnlyList<OutboxEntry> Outbox => _outbox;

    public TState Get<TState>(string aggregateId)
        where TState : class
    {
        var loaded = GetLoaded<TState>(aggregateId);
        var json = _newStates.TryGetValue(aggregateId, out var changed) ? changed : loaded.StateJson;
        return (TState)AggregateDocumentSerializer.DeserializeState(json, loaded.Descriptor.StateType);
    }

    public void Set<TState>(string aggregateId, TState state)
        where TState : class
    {
        var loaded = GetLoaded<TState>(aggregateId);
        if (!_descriptor.Touches.Contains(aggregateId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Command '{_descriptor.Operation}' wrote aggregate '{aggregateId}' which is not declared in Touches (DAT-09).");
        }

        _newStates[aggregateId] = AggregateDocumentSerializer.SerializeState(state, loaded.Descriptor.StateType);
    }

    public async ValueTask<bool> TryClaimAsync(string collection, string claimKey)
    {
        if (collection == ReceiptDocument.Collection || collection.StartsWith("pa_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Claim markers cannot live in collection '{collection}'.");
        }

        var key = new DocumentKey(collection, claimKey);
        if (_claims.ContainsKey(key))
        {
            return false;
        }

        if (_claims.Count >= _descriptor.MaxClaimMarkers)
        {
            throw new InvalidOperationException(
                $"Command '{_descriptor.Operation}' exceeded MaxClaimMarkers = {_descriptor.MaxClaimMarkers} (DAT-04).");
        }

        var existing = await _transaction.GetManyAsync([key]);
        if (existing[0] is not null)
        {
            return false;
        }

        _claims[key] = new JsonObject
        {
            ["claimKey"] = claimKey,
            ["receiptId"] = _receiptId,
            ["playerId"] = Actor.PlayerId,
            ["gameId"] = Actor.GameId,
            ["environmentId"] = Actor.EnvironmentId,
            ["createdAtUtc"] = AggregateDocumentSerializer.FormatTime(Now),
        };
        return true;
    }

    public void AddOutbox(string kind, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), GameServerJson.Options);
        _outbox.Add(new OutboxEntry($"{_receiptId}:{_outbox.Count}", kind, node));
    }

    /// <summary>New serialized state of an aggregate if the handler called <see cref="Set{TState}"/>.</summary>
    public bool TryGetNewState(string aggregateId, out byte[] json) => _newStates.TryGetValue(aggregateId, out json!);

    private LoadedAggregate GetLoaded<TState>(string aggregateId)
    {
        if (!_loaded.TryGetValue(aggregateId, out var loaded))
        {
            throw new InvalidOperationException(
                $"Command '{_descriptor.Operation}' accessed aggregate '{aggregateId}' which is not declared in Touches or Reads (DAT-11).");
        }

        if (!typeof(TState).IsAssignableFrom(loaded.Descriptor.StateType))
        {
            throw new InvalidOperationException(
                $"Aggregate '{aggregateId}' has state type {loaded.Descriptor.StateType.Name}, not {typeof(TState).Name}.");
        }

        return loaded;
    }
}
