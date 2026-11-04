using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Registry;

namespace GameServer.TestKit;

/// <summary>The double of an account initializer (FG-015, FR-017): the account writer's view and rules, without host or store.</summary>
public sealed partial class CommandContextDouble
{
    /// <summary>Body hash of the creation receipt (SHA-256 of the empty body), exactly as the account writer stores it.</summary>
    private static readonly string EmptyBodyHash = ReceiptIds.BodyHash([]);

    private CommandContextDouble(
        ModuleRegistry registry,
        RegisteredAccountInitializer initializer,
        CommandDescriptor descriptor,
        Dictionary<string, PlayerAggregateDescriptor> aggregates)
    {
        _registry = registry;
        _initializer = initializer;
        _descriptor = descriptor;
        _aggregates = aggregates;
        _actor = SystemActor(DefaultPlayerId);
    }

    /// <summary>
    /// The double for an account initializer: the initializer's touches at their default state (rev 0) unless seeded, the
    /// creation's system actor for the default player (<c>p_test</c>), operation <c>identity.create_account</c> (FR-017). Reads and
    /// writes go through the account writer's view, so an undeclared aggregate throws the writer's message. Throws
    /// <see cref="ArgumentException"/> when no given module registers the initializer or one of its aggregates.
    /// </summary>
    public static CommandContextDouble ForAccountInitializer(string initializerName, params IGameModule[] modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initializerName);
        ArgumentNullException.ThrowIfNull(modules);
        var registry = new ModuleRegistry();
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module, nameof(modules));
            registry.Add(module, serviceOnly: false);
        }

        var initializers = registry.AccountInitializers;
        var initializer = initializers.FirstOrDefault(i => string.Equals(i.Registration.Name, initializerName, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Account initializer '{initializerName}' is not registered by the given modules. Registered initializers: " +
                $"{(initializers.Count == 0 ? "none" : string.Join(", ", initializers.Select(i => i.Registration.Name)))}. " +
                "Fix: pass the module that registers the initializer.",
                nameof(initializerName));

        var aggregates = new Dictionary<string, PlayerAggregateDescriptor>(StringComparer.Ordinal);
        foreach (var aggregateId in initializer.Registration.Touches)
        {
            aggregates[aggregateId] = registry.Aggregates.TryGetValue(aggregateId, out var aggregate)
                ? aggregate
                : throw new ArgumentException(
                    $"Account initializer '{initializerName}' declares aggregate '{aggregateId}' in Touches, but no given module registers it. " +
                    "Fix: pass the module that registers it.",
                    nameof(modules));
        }

        var descriptor = new CommandDescriptor(
            AccountCreation.Operation,
            initializer.Registration.Touches,
            maxClaimMarkers: initializer.Registration.MaxClaimMarkers,
            profile: SecurityProfile.Player);
        return new CommandContextDouble(registry, initializer, descriptor, aggregates);
    }

    /// <summary>
    /// What the account writer would refuse for the initializer's run so far, as its rule lines (errors-and-report-lines §5):
    /// undeclared reads and writes and claims over the limit were already thrown; aggregate sizes (Touches order), audit entries
    /// (0 expected), then the receipt size (<c>ReceiptMaxBytes</c>) measured with the writer's receipt builder from the creation's
    /// inputs: empty result, SHA-256 of the empty body, the configured actor, player, request id, config version and <c>At</c>.
    /// The receipt is measured only when the run wrote an aggregate, reserved a claim or added an outbox entry (no receipt
    /// otherwise). Empty when the creation would commit.
    /// </summary>
    public IReadOnlyList<string> CheckAccountCreation()
    {
        var initializer = _initializer ?? throw new InvalidOperationException(
            $"The double of command '{Descriptor.Operation}' checks with CheckCommit, not CheckAccountCreation().");
        var view = View!;
        var inner = _inner!;
        var operation = AccountCreation.InitializerOperation(initializer.Registration.Name);
        var violations = new List<string?>();
        var writes = new List<ReceiptAggregate>();
        foreach (var aggregateId in Descriptor.Touches)
        {
            if (inner.TryGetNewState(aggregateId, out var json) && CommitRules.ChangesState(_loaded![aggregateId], json))
            {
                violations.Add(CommitRules.CheckAggregateSize(operation, _actor.GameId, _actor.EnvironmentId, _loaded[aggregateId], json, _now));
                writes.Add(new ReceiptAggregate(aggregateId, _loaded[aggregateId].Rev + 1));
            }
        }

        violations.Add(CommitRules.CheckAudit(new CommandDescriptor(operation, [], profile: SecurityProfile.Player), view.Outbox));
        if (writes.Count > 0 || inner.Claims.Count > 0 || inner.Outbox.Count > 0)
        {
            var receipt = ReceiptBuilder.Build(new ReceiptInputs(
                ReceiptId,
                _actor.GameId,
                _actor.EnvironmentId,
                _actor.ActorId,
                _actor.ActorType,
                _actor.PlayerId ?? string.Empty,
                AccountCreation.Operation,
                _requestId,
                EmptyBodyHash,
                CoreErrorCodes.Ok,
                [],
                _config.ConfigVersion,
                Descriptor.Touches.Select(id => _aggregates[id]),
                writes,
                inner.Claims.Keys,
                inner.Outbox,
                _actor.CorrelationId,
                _now));
            violations.Add(CommitRules.CheckReceiptSize(operation, ReceiptDocument.SizeOf(receipt.ToJson()), _commands)?.Line);
        }

        return violations.OfType<string>().ToArray();
    }

    /// <summary>The account writer's view of an initializer double (created with the inner context); null for a command.</summary>
    private InitializerContext? View
    {
        get
        {
            _ = Inner;
            return _view;
        }
    }

    /// <summary>The creation's actor (research R2): the player as a System actor, no permissions, correlation = default request id.</summary>
    private static ActorContext SystemActor(string playerId) =>
        new(playerId, ActorType.System, playerId, TestScope, TestScope, new HashSet<string>(StringComparer.Ordinal), DefaultRequestId);
}
