using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>
    /// <see cref="Exception.Data"/> key set on an exception of a transaction call this context made on behalf of the handler or
    /// initializer: it is the store's failure, so a catch around module code lets it reach the store unchanged (T082 M1).
    /// </summary>
    internal const string StoreFailureKey = "GameServer.StoreFailure";

    private readonly IReadOnlyDictionary<string, LoadedAggregate> _loaded;
    private readonly CommandDescriptor _descriptor;
    private readonly ITransactionContext _transaction;
    private readonly string _receiptId;
    private readonly IReadOnlySet<string>? _collections;
    private readonly Dictionary<string, byte[]> _newStates = new(StringComparer.Ordinal);
    private readonly Dictionary<DocumentKey, JsonObject> _claims = [];
    private readonly List<OutboxEntry> _outbox = [];

    /// <param name="collections">
    /// The host's provisioned collections (<see cref="Registry.ModuleRegistry.ProvisionedCollections"/>): a claim outside them fails
    /// before the transaction read. Null skips that check (the test kit's double, which has no registry).
    /// </param>
    public CommandContext(
        ActorContext actor,
        DateTimeOffset now,
        IGameConfig config,
        IReadOnlyDictionary<string, LoadedAggregate> loaded,
        CommandDescriptor descriptor,
        ITransactionContext transaction,
        string receiptId,
        IReadOnlySet<string>? collections = null)
    {
        Actor = actor;
        Now = now;
        Config = config;
        _loaded = loaded;
        _descriptor = descriptor;
        _transaction = transaction;
        _receiptId = receiptId;
        _collections = collections;
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

    public ValueTask<bool> TryClaimAsync(string collection, string claimKey) => TryClaimAsync(collection, claimKey, null);

    public async ValueTask<bool> TryClaimAsync(string collection, string claimKey, IReadOnlyDictionary<string, string>? attributes)
    {
        if (collection == ReceiptDocument.Collection || collection.StartsWith("pa_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Claim markers cannot live in collection '{collection}'.");
        }

        // Module bugs fail here, before the transaction read, so they are never mistaken for the store's failure (T082 round 2, m1).
        if (_collections is not null && !_collections.Contains(collection))
        {
            throw new InvalidOperationException(
                $"Claim markers cannot live in collection '{collection}': no module declares it (DAT-07). Fix: declare it in ModuleDescriptor.Collections.");
        }

        if (string.IsNullOrEmpty(claimKey))
        {
            throw new InvalidOperationException($"Claim key in collection '{collection}' is empty. Fix: pass a non-empty claim key.");
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

        IReadOnlyList<StoredDocument?> existing;
        try
        {
            existing = await _transaction.GetManyAsync([key]);
        }
        catch (Exception ex)
        {
            ex.Data[StoreFailureKey] = true;
            throw;
        }

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
        if (attributes is { Count: > 0 })
        {
            var node = new JsonObject();
            foreach (var (name, value) in attributes)
            {
                node[name] = value;
            }

            _claims[key]["attributes"] = node;
        }

        return true;
    }

    public void AddOutbox(string kind, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), GameServerJson.Options);
        _outbox.Add(new OutboxEntry($"{_receiptId}:{_outbox.Count}", kind, node));
    }

    public string NewId(string purpose)
    {
        var hash = Hash(purpose);
        return Convert.ToHexStringLower(hash.AsSpan(0, 10));
    }

    public long NewSeed(string purpose) => BinaryPrimitives.ReadInt64LittleEndian(Hash("seed|" + purpose));

    private byte[] Hash(string purpose) => SHA256.HashData(Encoding.UTF8.GetBytes(_receiptId + "|" + purpose));

    /// <summary>True for an exception of a transaction call made through a context (<see cref="StoreFailureKey"/>).</summary>
    internal static bool IsStoreFailure(Exception exception) => exception.Data[StoreFailureKey] is true;

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
