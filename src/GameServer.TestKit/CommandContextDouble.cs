using System.Text.Json.Nodes;
using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.TestKit;

/// <summary>
/// ICommandContext for handler unit tests without host, store or DI (FR-020..FR-026). Wraps the runner's own context, so
/// every access rule, exception type and message is the runner's. Configure first; the first ICommandContext call freezes
/// the configuration. Not simulated: transactions, retries, receipts (and so the receipt size limit, see
/// <see cref="CheckCommit(ICommandOutcome, Func{object, byte[]})"/>) and the outbox relay. The double of an account initializer
/// (<see cref="ForAccountInitializer"/>) is in AccountCreationCheck.cs.
/// </summary>
public sealed partial class CommandContextDouble : ICommandContext
{
    /// <summary>Request id of the receipt id when none is configured (and correlation id of the default actors).</summary>
    private const string DefaultRequestId = "00000000-0000-4000-8000-000000000001";
    private const string DefaultPlayerId = "p_test";
    private const string DefaultStaffId = "staff_test";
    private const string TestScope = "test";

    private static readonly DateTimeOffset DefaultNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ModuleRegistry _registry;
    private readonly CommandDescriptor _descriptor;

    /// <summary>The command of <see cref="Create"/>; null for an account initializer.</summary>
    private readonly CommandRegistration? _registration;

    /// <summary>The initializer of <see cref="ForAccountInitializer"/>; null for a command.</summary>
    private readonly RegisteredAccountInitializer? _initializer;
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
    private CommandSettings _commands = new();
    private Dictionary<string, LoadedAggregate>? _loaded;
    private CommandContext? _inner;
    private InitializerContext? _view;

    private CommandContextDouble(ModuleRegistry registry, CommandRegistration registration, Dictionary<string, PlayerAggregateDescriptor> aggregates)
    {
        _registry = registry;
        _registration = registration;
        _descriptor = registration.Descriptor;
        _aggregates = aggregates;
        _actor = _descriptor.Profile == SecurityProfile.Gm
            ? GmActor(DefaultStaffId, DefaultPlayerId, _descriptor.Permission is null ? [] : [_descriptor.Permission])
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

    /// <summary>The command's descriptor; for an account initializer, operation <c>identity.create_account</c> over its touches.</summary>
    public CommandDescriptor Descriptor => _descriptor;

    /// <summary>The configured receipt id, else <c>ReceiptIds.Compute(actorId, playerId, operation, requestId)</c> like the runner.</summary>
    public string ReceiptId =>
        _receiptId ?? ReceiptIds.Compute(_actor.ActorId, _actor.PlayerId ?? string.Empty, Descriptor.Operation, _requestId);

    /// <summary>
    /// Aggregates the runner would write, in first-<c>Set</c> order, each with its latest state. Like the runner, a <c>Set</c> whose
    /// state equals the loaded (seeded or default) state is no write: setting it unchanged, or changing it back, removes the entry.
    /// </summary>
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

    /// <summary>
    /// The host's <c>GameServer:Commands</c> limits for <see cref="CheckCommit(ICommandOutcome, Func{object, byte[]})"/> (default:
    /// the framework defaults).
    /// </summary>
    public CommandContextDouble WithCommandSettings(CommandSettings commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return Configure(() => _commands = commands);
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
        where TState : class => Surface.Get<TState>(aggregateId);

    public void Set<TState>(string aggregateId, TState state)
        where TState : class
    {
        var inner = Inner;
        Surface.Set(aggregateId, state);
        inner.TryGetNewState(aggregateId, out var json);
        var index = _writes.FindIndex(w => w.AggregateId == aggregateId);
        if (!CommitRules.ChangesState(_loaded![aggregateId], json))
        {
            // The runner compares the final state with the loaded one and skips the write (no revision bump).
            if (index >= 0)
            {
                _writes.RemoveAt(index);
            }

            return;
        }

        var write = new RecordedWrite(aggregateId, AggregateDocumentSerializer.DeserializeState(json, _aggregates[aggregateId].StateType));
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
        var claimed = await Surface.TryClaimAsync(collection, claimKey, attributes);
        if (claimed)
        {
            _claims.Add(new RecordedClaim(collection, claimKey, new Dictionary<string, string>(attributes ?? new Dictionary<string, string>(), StringComparer.Ordinal)));
        }

        return claimed;
    }

    public void AddOutbox(string kind, object payload) => Surface.AddOutbox(kind, payload);

    public string NewId(string purpose) => Inner.NewId(purpose);

    public long NewSeed(string purpose) => Inner.NewSeed(purpose);

    /// <summary>
    /// What the runner would refuse at commit (FR-026), checked with the runner's rules: uncatalogued or non-200 commit code; a
    /// written aggregate whose stored document would exceed its <c>HardBytes</c> (the runner rejects with
    /// <c>AGGREGATE_TOO_LARGE</c>; the envelope is measured with the actor's GameId and EnvironmentId, "test"/"test" by default:
    /// other ids change the size by their length); GM command without exactly one audit entry, non-GM command with one. Empty
    /// for a rejection or a committable outcome. Messages = runner messages. The result size is checked only by the overload
    /// that takes the result encoder. The receipt size (<c>ReceiptMaxBytes</c>) is never checked: the receipt holds the request
    /// body hash, the config version and the encoded result, which the double does not have as the host builds them.
    /// </summary>
    public IReadOnlyList<string> CheckCommit(ICommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return CheckCommit(outcome, resultBytes: null);
    }

    /// <summary>
    /// <see cref="CheckCommit(ICommandOutcome)"/> plus the resultCompact limit (<c>ResultCompactMaxBytes</c>, CMD-03) on the bytes
    /// <paramref name="encodeResult"/> returns for the outcome's result: pass the endpoint's mapping, e.g.
    /// <c>r =&gt; ToWire((MyResult)r).ToByteArray()</c> for the <c>toWire</c> given to <c>MapPlayerCommand</c>.
    /// </summary>
    public IReadOnlyList<string> CheckCommit(ICommandOutcome outcome, Func<object, byte[]> encodeResult)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(encodeResult);
        return CheckCommit(outcome, outcome.IsOk ? encodeResult(outcome.Result!).Length : null);
    }

    private string[] CheckCommit(ICommandOutcome outcome, int? resultBytes)
    {
        var registration = _registration ?? throw new InvalidOperationException(
            $"The double of account initializer '{_initializer!.Registration.Name}' checks with CheckAccountCreation(), not CheckCommit.");
        if (!outcome.IsOk)
        {
            return [];
        }

        // The runner's order: code, result size, aggregate sizes (Touches order), audit.
        var violations = new List<string?>
        {
            CommitRules.CheckCode(registration, _registry, outcome.Error),
            resultBytes is { } size ? CommitRules.CheckResultSize(Descriptor.Operation, size, _commands) : null,
        };
        foreach (var aggregateId in Descriptor.Touches)
        {
            if (_inner is not null && _inner.TryGetNewState(aggregateId, out var json) && CommitRules.ChangesState(_loaded![aggregateId], json))
            {
                violations.Add(CommitRules.CheckAggregateSize(Descriptor.Operation, _actor.GameId, _actor.EnvironmentId, _loaded[aggregateId], json, _now));
            }
        }

        violations.Add(CommitRules.CheckAudit(Descriptor, Outbox));
        return violations.OfType<string>().ToArray();
    }

    private CommandContext Inner => _inner ??= CreateInner();

    /// <summary>What the code under test calls: the runner's context, or for an initializer the writer's view over it.</summary>
    private ICommandContext Surface => (ICommandContext?)View ?? Inner;

    private CommandContext CreateInner()
    {
        var playerId = _actor.PlayerId
            ?? throw new InvalidOperationException($"The actor {_actor.ActorId} of '{Descriptor.Operation}' has no target player.");
        var loaded = new Dictionary<string, LoadedAggregate>(StringComparer.Ordinal);
        foreach (var (aggregateId, aggregate) in _aggregates)
        {
            loaded[aggregateId] = _seeds.TryGetValue(aggregateId, out var seeded) ? Existing(aggregate, playerId, seeded) : Missing(aggregate, playerId);
        }

        _loaded = loaded;
        var inner = new CommandContext(_actor, _now, _config, loaded, Descriptor, new DetachedTransaction(_seededClaims), ReceiptId);
        if (_initializer is not null)
        {
            _view = new InitializerContext(inner, _initializer.Registration, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        return inner;
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
