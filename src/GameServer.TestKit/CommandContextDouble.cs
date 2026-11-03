using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.TestKit;

/// <summary>
/// ICommandContext for handler unit tests without host, store or DI (FR-020..FR-026). Wraps the runner's own context, so
/// every access rule, exception type and message is the runner's. Configure first; the first ICommandContext call freezes
/// the configuration. Not simulated: transactions, retries, receipts and the outbox relay.
/// </summary>
public sealed class CommandContextDouble : ICommandContext
{
    /// <summary>Request id of the receipt id when none is configured (and correlation id of the default actors).</summary>
    private const string DefaultRequestId = "00000000-0000-4000-8000-000000000001";
    private const string DefaultPlayerId = "p_test";
    private const string DefaultStaffId = "staff_test";
    private const string TestScope = "test";

    private static readonly DateTimeOffset DefaultNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ModuleRegistry _registry;
    private readonly CommandRegistration _registration;
    private readonly Dictionary<string, PlayerAggregateDescriptor> _aggregates;
    private readonly Dictionary<string, object> _seeds = new(StringComparer.Ordinal);
    private readonly HashSet<DocumentKey> _seededClaims = [];
    private readonly List<RecordedWrite> _writes = [];
    private readonly List<RecordedClaim> _claims = [];
    private ActorContext _actor;
    private DateTimeOffset _now = DefaultNow;
    private IGameConfig _config = TestGameConfig.Empty;
    private string _requestId = DefaultRequestId;
    private string? _receiptId;
    private CommandContext? _inner;

    private CommandContextDouble(ModuleRegistry registry, CommandRegistration registration, Dictionary<string, PlayerAggregateDescriptor> aggregates)
    {
        _registry = registry;
        _registration = registration;
        _aggregates = aggregates;
        var descriptor = registration.Descriptor;
        _actor = descriptor.Profile == SecurityProfile.Gm
            ? GmActor(DefaultStaffId, DefaultPlayerId, descriptor.Permission is null ? [] : [descriptor.Permission])
            : PlayerActor(DefaultPlayerId);
    }

    /// <summary>
    /// Builds the double for <paramref name="operation"/> from the modules' descriptors (only <c>Describe()</c> runs). Throws
    /// <see cref="ArgumentException"/> when no given module registers the operation or an aggregate of its Touches ∪ Reads.
    /// </summary>
    public static CommandContextDouble Create(string operation, params IGameModule[] modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(modules);
        var registry = new ModuleRegistry();
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module, nameof(modules));
            registry.Add(module, serviceOnly: false);
        }

        if (!registry.Commands.TryGetValue(operation, out var registration))
        {
            var registered = registry.Commands.Count == 0 ? "none" : string.Join(", ", registry.Commands.Keys.Order(StringComparer.Ordinal));
            throw new ArgumentException(
                $"Operation '{operation}' is not registered by the given modules. Registered operations: {registered}. " +
                "Fix: pass the module that registers the command.",
                nameof(operation));
        }

        var aggregates = new Dictionary<string, PlayerAggregateDescriptor>(StringComparer.Ordinal);
        foreach (var aggregateId in registration.Descriptor.Touches.Concat(registration.Descriptor.Reads))
        {
            aggregates[aggregateId] = registry.Aggregates.TryGetValue(aggregateId, out var aggregate)
                ? aggregate
                : throw new ArgumentException(
                    $"Command '{operation}' declares aggregate '{aggregateId}' in Touches or Reads, but no given module registers it. " +
                    "Fix: pass the module that registers it.",
                    nameof(modules));
        }

        return new CommandContextDouble(registry, registration, aggregates);
    }

    public CommandDescriptor Descriptor => _registration.Descriptor;

    /// <summary>The configured receipt id, else <c>ReceiptIds.Compute(actorId, playerId, operation, requestId)</c> like the runner.</summary>
    public string ReceiptId =>
        _receiptId ?? ReceiptIds.Compute(_actor.ActorId, _actor.PlayerId ?? string.Empty, Descriptor.Operation, _requestId);

    /// <summary>Aggregates set by the handler, in first-<c>Set</c> order, each with its latest state.</summary>
    public IReadOnlyList<RecordedWrite> Writes => _writes;

    /// <summary>Outbox entries of the runner's context in call order (audit and analytics entries included).</summary>
    public IReadOnlyList<OutboxEntry> Outbox => _inner?.Outbox ?? [];

    /// <summary>Claim markers reserved by the handler, in call order.</summary>
    public IReadOnlyList<RecordedClaim> Claims => _claims;

    public ActorContext Actor => Inner.Actor;

    public DateTimeOffset Now => Inner.Now;

    public string ConfigVersion => Inner.ConfigVersion;

    public IGameConfig Config => Inner.Config;

    public CommandContextDouble AsPlayer(string playerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerId);
        return Configure(() => _actor = PlayerActor(playerId));
    }

    public CommandContextDouble AsGm(string staffId, string targetPlayerId, params string[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staffId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPlayerId);
        ArgumentNullException.ThrowIfNull(permissions);
        return Configure(() => _actor = GmActor(staffId, targetPlayerId, permissions));
    }

    public CommandContextDouble WithActor(ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Configure(() => _actor = actor);
    }

    public CommandContextDouble At(DateTimeOffset now) => Configure(() => _now = now);

    public CommandContextDouble WithConfig(IGameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Configure(() => _config = config);
    }

    public CommandContextDouble WithRequestId(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return Configure(() => _requestId = requestId);
    }

    public CommandContextDouble WithReceiptId(string receiptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        return Configure(() => _receiptId = receiptId);
    }

    /// <summary>Existing state of an aggregate of Touches ∪ Reads (rev 1); the state must be of the aggregate's state type.</summary>
    public CommandContextDouble Seed<TState>(string aggregateId, TState state)
        where TState : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentNullException.ThrowIfNull(state);
        if (!_aggregates.TryGetValue(aggregateId, out var aggregate))
        {
            throw new ArgumentException(
                $"Aggregate '{aggregateId}' is not declared in Touches or Reads of '{Descriptor.Operation}' ({string.Join(", ", _aggregates.Keys)}).",
                nameof(aggregateId));
        }

        if (!aggregate.StateType.IsInstanceOfType(state))
        {
            throw new ArgumentException(
                $"Aggregate '{aggregateId}' has state type {aggregate.StateType.Name}, not {state.GetType().Name}.", nameof(state));
        }

        return Configure(() => _seeds[aggregateId] = state);
    }

    /// <summary>A claim marker that already exists in the store: <c>TryClaimAsync</c> on it returns false.</summary>
    public CommandContextDouble SeedClaim(string collection, string claimKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);
        return Configure(() => _seededClaims.Add(new DocumentKey(collection, claimKey)));
    }

    public TState Get<TState>(string aggregateId)
        where TState : class => Inner.Get<TState>(aggregateId);

    public void Set<TState>(string aggregateId, TState state)
        where TState : class
    {
        var inner = Inner;
        inner.Set(aggregateId, state);
        inner.TryGetNewState(aggregateId, out var json);
        var write = new RecordedWrite(aggregateId, AggregateDocumentSerializer.DeserializeState(json, _aggregates[aggregateId].StateType));
        var index = _writes.FindIndex(w => w.AggregateId == aggregateId);
        if (index < 0)
        {
            _writes.Add(write);
        }
        else
        {
            _writes[index] = write;
        }
    }

    /// <summary>The latest state the handler set for <paramref name="aggregateId"/>, or null when it never set it.</summary>
    public TState? Written<TState>(string aggregateId)
        where TState : class => (TState?)_writes.Find(w => w.AggregateId == aggregateId)?.State;

    public ValueTask<bool> TryClaimAsync(string collection, string claimKey) => TryClaimAsync(collection, claimKey, null);

    public async ValueTask<bool> TryClaimAsync(string collection, string claimKey, IReadOnlyDictionary<string, string>? attributes)
    {
        var claimed = await Inner.TryClaimAsync(collection, claimKey, attributes);
        if (claimed)
        {
            _claims.Add(new RecordedClaim(collection, claimKey, new Dictionary<string, string>(attributes ?? new Dictionary<string, string>(), StringComparer.Ordinal)));
        }

        return claimed;
    }

    public void AddOutbox(string kind, object payload) => Inner.AddOutbox(kind, payload);

    public string NewId(string purpose) => Inner.NewId(purpose);

    public long NewSeed(string purpose) => Inner.NewSeed(purpose);

    /// <summary>
    /// What the runner would refuse at commit (FR-026): uncatalogued or non-200 commit code, GM command without exactly one
    /// audit entry, non-GM command with one. Empty for a rejection or a committable outcome. Messages = runner messages.
    /// </summary>
    public IReadOnlyList<string> CheckCommit(ICommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!outcome.IsOk)
        {
            return [];
        }

        string?[] violations = [CommitRules.CheckCode(_registration, _registry, outcome.Error), CommitRules.CheckAudit(Descriptor, Outbox)];
        return violations.OfType<string>().ToArray();
    }

    private CommandContext Inner => _inner ??= CreateInner();

    private CommandContext CreateInner()
    {
        var playerId = _actor.PlayerId
            ?? throw new InvalidOperationException($"The actor {_actor.ActorId} of '{Descriptor.Operation}' has no target player.");
        var loaded = new Dictionary<string, LoadedAggregate>(StringComparer.Ordinal);
        foreach (var (aggregateId, aggregate) in _aggregates)
        {
            loaded[aggregateId] = _seeds.TryGetValue(aggregateId, out var seeded) ? Existing(aggregate, playerId, seeded) : Missing(aggregate, playerId);
        }

        return new CommandContext(_actor, _now, _config, loaded, Descriptor, new DetachedTransaction(_seededClaims), ReceiptId);
    }

    /// <summary>Like the runner's load of a missing document: the descriptor's default state at rev 0.</summary>
    private static LoadedAggregate Missing(PlayerAggregateDescriptor aggregate, string playerId)
    {
        var state = aggregate.CreateDefault();
        return new LoadedAggregate(aggregate, playerId, null, 0, aggregate.SchemaVersion, false, state, AggregateDocumentSerializer.SerializeState(state, aggregate.StateType), null);
    }

    /// <summary>A stored document at rev 1 holding the canonical JSON of the seeded state.</summary>
    private LoadedAggregate Existing(PlayerAggregateDescriptor aggregate, string playerId, object seeded)
    {
        var json = AggregateDocumentSerializer.SerializeState(seeded, aggregate.StateType);
        var createdAt = AggregateDocumentSerializer.FormatTime(_now);
        var document = new StoredDocument(
            AggregateDocumentSerializer.KeyFor(aggregate, playerId),
            new JsonObject
            {
                ["schemaVersion"] = aggregate.SchemaVersion,
                ["rev"] = 1,
                ["createdAtUtc"] = createdAt,
                ["state"] = JsonNode.Parse(json),
            },
            handle: null);
        var state = AggregateDocumentSerializer.DeserializeState(json, aggregate.StateType);
        return new LoadedAggregate(aggregate, playerId, document, 1, aggregate.SchemaVersion, false, state, json, createdAt);
    }

    private CommandContextDouble Configure(Action change)
    {
        if (_inner is not null)
        {
            throw new InvalidOperationException(
                $"CommandContextDouble for '{Descriptor.Operation}' is already in use: configure the double before running the handler.");
        }

        change();
        return this;
    }

    private static ActorContext PlayerActor(string playerId) =>
        new(playerId, ActorType.Player, playerId, TestScope, TestScope, new HashSet<string>(StringComparer.Ordinal), DefaultRequestId);

    private static ActorContext GmActor(string staffId, string targetPlayerId, IEnumerable<string> permissions) =>
        new(staffId, ActorType.Gm, targetPlayerId, TestScope, TestScope, new HashSet<string>(permissions, StringComparer.Ordinal), DefaultRequestId);
}
