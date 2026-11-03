using GameServer.Accounts;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Platform.Web.Aggregates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Tests.Shared;

// Business half: aggregates, codes and the initializer.

/// <summary>State of the client-visible test aggregate <c>starter</c> (wire message <c>sample.counter.v1.CounterState</c>).</summary>
public sealed record StarterState
{
    public long Value { get; init; }

    public int Increments { get; init; }

    /// <summary>Hidden from the wire: makes the document large (<see cref="StartingStateMode.OversizeAggregate"/>) or marks it.</summary>
    public string Padding { get; init; } = string.Empty;
}

/// <summary>State of the client-visible test aggregate <c>broken_probe</c> (schema 2; its 1→2 upgrader fails for a marker).</summary>
public sealed record BrokenProbeState
{
    public long Value { get; init; }

    /// <summary>Hidden from the wire. A version-1 document with <c>fail: true</c> makes the upgrader throw.</summary>
    public bool Fail { get; init; }
}

public static class StartingStateAggregates
{
    public const string Starter = "starter";
    public const string BrokenProbe = "broken_probe";

    /// <summary>The starter's document budget is small, so <see cref="StartingStateMode.OversizeAggregate"/> stays cheap.</summary>
    public const int StarterHardBytes = 8 * 1024;

    /// <summary><see cref="StarterState.Padding"/> value for which the starter's wire mapper throws (mapper failure, FG-029).</summary>
    public const string MapperFailMarker = "mapper-fail";

    public static PlayerAggregateDescriptor<StarterState> StarterDescriptor { get; } =
        new(Starter, 1, clientVisible: true, () => new StarterState(), warnBytes: 4 * 1024, hardBytes: StarterHardBytes);

    public static PlayerAggregateDescriptor<BrokenProbeState> BrokenProbeDescriptor { get; } =
        new(BrokenProbe, 2, clientVisible: true, () => new BrokenProbeState(), [new DelegateAggregateUpgrader(1, UpgradeV1)]);

    private static System.Text.Json.Nodes.JsonObject UpgradeV1(System.Text.Json.Nodes.JsonObject state) =>
        state["fail"]?.GetValue<bool>() == true ? throw new InvalidOperationException("broken_probe upgrade failed (test marker).") : state;
}

/// <summary>Codes of the test module (range 21000-21099, outside the game band).</summary>
public static class StartingStateErrors
{
    public static readonly ErrorCode Rejected = new(21001, "TEST_STARTING_STATE_REJECTED", 200, false, "The starting state was refused.");
    public static readonly ErrorCode AlreadyClaimed = new(21002, "TEST_STARTING_STATE_CLAIMED", 200, false, "The starting claim was already taken.");

    public static ErrorCodeSet All { get; } = new(21000, 21099, Rejected, AlreadyClaimed);
}

/// <summary>What <see cref="StartingStateInitializer"/> does in a host (see <see cref="StartingStateBehaviour"/>).</summary>
public enum StartingStateMode
{
    /// <summary>Changes nothing (no creation receipt).</summary>
    Nothing,

    /// <summary>Grants <see cref="StartingStateBehaviour.Amount"/> gold (ledger source <c>account_init</c>) and sets <c>starter</c>.</summary>
    Grant,

    /// <summary>Returns <see cref="StartingStateBehaviour.Code"/>.</summary>
    Reject,

    /// <summary>Throws <see cref="InvalidOperationException"/>.</summary>
    Throw,

    /// <summary>Sets <c>inventory</c>, which it does not declare.</summary>
    WriteUndeclared,

    /// <summary>Adds a GM audit entry (the creation expects none, SEC-14).</summary>
    AddAudit,

    /// <summary>Sets <c>starter</c> above its HardBytes.</summary>
    OversizeAggregate,

    /// <summary>Sets <c>starter</c> and adds <see cref="StartingStateBehaviour.Count"/> outbox entries (receipt size).</summary>
    FloodOutbox,

    /// <summary>Sets <c>starter</c> and reserves <see cref="StartingStateBehaviour.Count"/> claim markers.</summary>
    Claim,
}

/// <summary>
/// The behaviour of <see cref="StartingStateInitializer"/>, registered per host (<c>services.AddSingleton(StartingStateBehaviour.Grant(100))</c>;
/// the module registers <see cref="Nothing"/> when the host does not). <see cref="Become"/> switches a running host, and
/// <see cref="Invocations"/> counts the initializer runs (every transaction attempt).
/// </summary>
public sealed class StartingStateBehaviour
{
    public const string LedgerSourceType = "account_init";
    public const string Resource = "gold";

    private int _invocations;

    private StartingStateBehaviour(StartingStateMode mode, long amount = 0, ErrorCode? code = null, int count = 0)
    {
        Mode = mode;
        Amount = amount;
        Code = code;
        Count = count;
    }

    public StartingStateMode Mode { get; private set; }

    public long Amount { get; private set; }

    public ErrorCode? Code { get; private set; }

    public int Count { get; private set; }

    public int Invocations => Volatile.Read(ref _invocations);

    public static StartingStateBehaviour Nothing() => new(StartingStateMode.Nothing);

    public static StartingStateBehaviour Grant(long amount) => new(StartingStateMode.Grant, amount: amount);

    public static StartingStateBehaviour Reject(ErrorCode code) => new(StartingStateMode.Reject, code: code);

    public static StartingStateBehaviour Throw() => new(StartingStateMode.Throw);

    public static StartingStateBehaviour WriteUndeclared() => new(StartingStateMode.WriteUndeclared);

    public static StartingStateBehaviour AddAudit() => new(StartingStateMode.AddAudit);

    public static StartingStateBehaviour OversizeAggregate() => new(StartingStateMode.OversizeAggregate);

    public static StartingStateBehaviour FloodOutbox(int entries) => new(StartingStateMode.FloodOutbox, count: entries);

    public static StartingStateBehaviour Claim(int markers) => new(StartingStateMode.Claim, count: markers);

    /// <summary>Takes the behaviour of <paramref name="next"/>; the invocation count is kept.</summary>
    public void Become(StartingStateBehaviour next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Mode = next.Mode;
        Amount = next.Amount;
        Code = next.Code;
        Count = next.Count;
    }

    internal void CountInvocation() => Interlocked.Increment(ref _invocations);
}

/// <summary>Initializer <c>test.starting_state</c> (touches <c>wallet</c>, <c>starter</c>; up to 2 claim markers).</summary>
public sealed class StartingStateInitializer(StartingStateBehaviour behaviour, ResourceCatalog resources) : IAccountInitializer
{
    /// <summary>Payload length of one <see cref="StartingStateMode.FloodOutbox"/> entry.</summary>
    public const int FloodEntryChars = 64;

    public async ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
    {
        behaviour.CountInvocation();
        switch (behaviour.Mode)
        {
            case StartingStateMode.Nothing:
                return null;
            case StartingStateMode.Grant:
                SetStarter(context, behaviour.Amount, string.Empty);
                return EconomyOperations.Grant(context, resources, StartingStateBehaviour.Resource, behaviour.Amount, new LedgerSource(StartingStateBehaviour.LedgerSourceType));
            case StartingStateMode.Reject:
                return behaviour.Code ?? StartingStateErrors.Rejected;
            case StartingStateMode.Throw:
                throw new InvalidOperationException("starting state test initializer threw (message must never be logged).");
            case StartingStateMode.WriteUndeclared:
                context.Set(EconomyAggregates.Inventory, new InventoryState());
                return null;
            case StartingStateMode.AddAudit:
                SetStarter(context, 1, string.Empty);
                context.AddAudit("test.probe", StartingStateModule.InitializerName, "test");
                return null;
            case StartingStateMode.OversizeAggregate:
                SetStarter(context, 1, new string('x', StartingStateAggregates.StarterHardBytes + 1));
                return null;
            case StartingStateMode.FloodOutbox:
                SetStarter(context, 1, string.Empty);
                for (var i = 0; i < behaviour.Count; i++)
                {
                    context.AddOutbox(StartingStateModule.OutboxKind, new { index = i, text = new string('o', FloodEntryChars) });
                }

                return null;
            case StartingStateMode.Claim:
                SetStarter(context, 1, string.Empty);
                for (var i = 0; i < behaviour.Count; i++)
                {
                    if (!await context.TryClaimAsync(StartingStateModule.ClaimCollection, $"{context.Actor.PlayerId}:{i}"))
                    {
                        return StartingStateErrors.AlreadyClaimed;
                    }
                }

                return null;
            default:
                throw new InvalidOperationException($"Unknown starting state mode {behaviour.Mode}.");
        }
    }

    private static void SetStarter(ICommandContext context, long value, string padding)
    {
        var starter = context.Get<StarterState>(StartingStateAggregates.Starter);
        context.Set(StartingStateAggregates.Starter, starter with { Value = starter.Value + value, Increments = starter.Increments + 1, Padding = padding });
    }
}

// Adapters half: the module and its wire mappers.

/// <summary>
/// Slice 016 test module (T015): aggregates <c>starter</c> and <c>broken_probe</c> (client-visible, typed mappers) and the
/// account initializer <c>test.starting_state</c>. Registered only where a test adds it; needs Economy (and its config).
/// </summary>
public sealed class StartingStateModule : IGameModule
{
    public const string ModuleName = "test";
    public const string InitializerName = "test.starting_state";
    public const string ClaimCollection = "starting_state_claims";
    public const string OutboxKind = "test_starting_state";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        DependsOn = [EconomyModuleDefinition.ModuleName],
        Aggregates = [StartingStateAggregates.StarterDescriptor, StartingStateAggregates.BrokenProbeDescriptor],
        ErrorCodes = StartingStateErrors.All,
        Collections = [new CollectionSpec(ClaimCollection, Deletion: DeletionTreatment.Anonymize)],
        AccountInitializers =
        [
            AccountInitializerRegistration.Of<StartingStateInitializer>(InitializerName, [EconomyAggregates.Wallet, StartingStateAggregates.Starter], maxClaimMarkers: 2),
        ],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.TryAddSingleton(StartingStateBehaviour.Nothing());
        services.AddAggregateWireMapper<StarterState, Wire.CounterState>(StartingStateAggregates.Starter, StarterToWire, o => o.Hidden(nameof(StarterState.Padding)));
        services.AddAggregateWireMapper<BrokenProbeState, Wire.CounterState>(
            StartingStateAggregates.BrokenProbe,
            state => new Wire.CounterState { Value = state.Value },
            o => o.Hidden(nameof(BrokenProbeState.Fail)));
    }

    /// <summary>Throws for <see cref="StartingStateAggregates.MapperFailMarker"/> (the mapper-failed seed of <see cref="BrokenDocuments"/>).</summary>
    public static Wire.CounterState StarterToWire(StarterState state) =>
        state.Padding == StartingStateAggregates.MapperFailMarker
            ? throw new InvalidOperationException("starter mapper failed (test marker).")
            : new Wire.CounterState { Value = state.Value, Increments = state.Increments };
}
