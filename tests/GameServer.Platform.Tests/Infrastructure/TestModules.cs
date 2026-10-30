using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Modules.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter;

namespace GameServer.Platform.Tests.Infrastructure;

/// <summary>Module built from a descriptor, for tests.</summary>
public sealed class DescriptorModule(ModuleDescriptor descriptor, Action<IServiceCollection>? services = null) : IGameModule
{
    public ModuleDescriptor Describe() => descriptor;

    public void ConfigureServices(IServiceCollection serviceCollection, ModuleContext context) => services?.Invoke(serviceCollection);
}

public static class TestModules
{
    public static DescriptorModule Identity() =>
        new(new ModuleDescriptor { Name = "identity", Aggregates = [IdentityAggregates.ProfileDescriptor] });

    public static DescriptorModule Counter() =>
        new(CounterModuleDefinition.Describe(), s => s.AddSingleton<ICounterLimitPolicy, DefaultCounterLimitPolicy>());

    /// <summary>Probe module: hidden aggregate, size growth, claims, faults, bad writes.</summary>
    public static DescriptorModule Probe() => new(new ModuleDescriptor
    {
        Name = "probe",
        DependsOn = ["identity", "sample.counter"],
        Aggregates = [ProbeAggregates.Descriptor, ProbeAggregates.HiddenDescriptor],
        Commands =
        [
            Command.Define<GrowRequest, Unit>(GrowHandler.Descriptor).HandledBy<GrowHandler>(),
            Command.Define<ClaimRequest, Unit>(ClaimHandler.Descriptor).HandledBy<ClaimHandler>(),
            Command.Define<Unit, Unit>(ThrowHandler.Descriptor).HandledBy<ThrowHandler>(),
            Command.Define<Unit, Unit>(BadWriteHandler.Descriptor).HandledBy<BadWriteHandler>(),
            Command.Define<Unit, Unit>(NoChangeHandler.Descriptor).HandledBy<NoChangeHandler>(),
            Command.Define<int, Unit>(OutboxFloodHandler.Descriptor).HandledBy<OutboxFloodHandler>(),
            Command.Define<int, Unit>(BigResultHandler.Descriptor).HandledBy<BigResultHandler>(),
            Command.Define<long, Unit>(GmAddHandler.Descriptor).HandledBy<GmAddHandler>(),
            Command.Define<Unit, Unit>(ForgeAuditHandler.Descriptor).HandledBy<ForgeAuditHandler>(),
        ],
        Permissions = [GmAddHandler.Permission],
        ErrorCodes = ProbeErrors.All,
        Collections = [new CollectionSpec("probe_claims", Deletion: DeletionTreatment.Anonymize)],
    });
}

public sealed record Unit
{
    public static Unit Value { get; } = new();
}

public sealed record ProbeState
{
    public string Blob { get; init; } = string.Empty;
}

public static class ProbeAggregates
{
    public const string Probe = "probe";
    public const string Hidden = "probe_hidden";

    public static PlayerAggregateDescriptor<ProbeState> Descriptor { get; } = new(Probe, 1, clientVisible: true, () => new ProbeState());

    public static PlayerAggregateDescriptor<ProbeState> HiddenDescriptor { get; } = new(Hidden, 1, clientVisible: false, () => new ProbeState());
}

public static class ProbeErrors
{
    public static readonly ErrorCode AlreadyClaimed = new(20001, "PROBE_ALREADY_CLAIMED", 200, false, "Already claimed.");

    public static ErrorCodeSet All { get; } = new(20000, 20099, AlreadyClaimed);
}

/// <summary>Sets probe (and optionally counter) to a blob of the given size; touches probe + counter.</summary>
public sealed record GrowRequest(int ProbeBytes, int HiddenBytes, long CounterAdd);

public sealed class GrowHandler : ICommandHandler<GrowRequest, Unit>
{
    public static CommandDescriptor Descriptor { get; } =
        new("probe.grow", touches: [ProbeAggregates.Probe, ProbeAggregates.Hidden, CounterAggregates.Counter]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(GrowRequest request, ICommandContext context)
    {
        if (request.ProbeBytes > 0)
        {
            context.Set(ProbeAggregates.Probe, new ProbeState { Blob = new string('x', request.ProbeBytes) });
        }

        if (request.HiddenBytes > 0)
        {
            context.Set(ProbeAggregates.Hidden, new ProbeState { Blob = new string('h', request.HiddenBytes) });
        }

        if (request.CounterAdd != 0)
        {
            var counter = context.Get<CounterState>(CounterAggregates.Counter);
            context.Set(CounterAggregates.Counter, counter with { Value = counter.Value + request.CounterAdd });
        }

        return ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
    }
}

public sealed record ClaimRequest(string Key, bool RejectAfterClaim);

public sealed class ClaimHandler : ICommandHandler<ClaimRequest, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.claim", touches: [ProbeAggregates.Probe], maxClaimMarkers: 1);

    public async ValueTask<CommandOutcome<Unit>> HandleAsync(ClaimRequest request, ICommandContext context)
    {
        if (!await context.TryClaimAsync("probe_claims", request.Key))
        {
            return CommandOutcome<Unit>.Reject(ProbeErrors.AlreadyClaimed);
        }

        if (request.RejectAfterClaim)
        {
            return CommandOutcome<Unit>.Reject(ProbeErrors.AlreadyClaimed);
        }

        context.Set(ProbeAggregates.Probe, new ProbeState { Blob = "claimed:" + request.Key });
        return CommandOutcome<Unit>.Ok(Unit.Value);
    }
}

/// <summary>
/// GM command adding to the target player's counter (profile Gm, permission <c>probe.edit</c>). A zero amount simulates a
/// module bug that forgets the audit.
/// </summary>
public sealed class GmAddHandler : ICommandHandler<long, Unit>
{
    public const string Permission = "probe.edit";

    public static CommandDescriptor Descriptor { get; } =
        new("probe.gm_add", touches: [CounterAggregates.Counter], profile: GameServer.Actors.SecurityProfile.Gm, permission: Permission);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(long request, ICommandContext context)
    {
        var counter = context.Get<CounterState>(CounterAggregates.Counter);
        context.Set(CounterAggregates.Counter, counter with { Value = counter.Value + request });
        if (request != 0)
        {
            context.AddAudit(Permission, Descriptor.Operation, "probe");
        }

        return ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
    }
}

/// <summary>Player command that tries to write a GM audit entry (must be refused).</summary>
public sealed class ForgeAuditHandler : ICommandHandler<Unit, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.forge_audit", touches: [ProbeAggregates.Probe]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(Unit request, ICommandContext context)
    {
        context.Set(ProbeAggregates.Probe, new ProbeState { Blob = "forged" });
        context.AddAudit("probe.edit", "probe.gm_add", "forged");
        return ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
    }
}

public sealed class ThrowHandler : ICommandHandler<Unit, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.throw", touches: [ProbeAggregates.Probe]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(Unit request, ICommandContext context)
    {
        context.Set(ProbeAggregates.Probe, new ProbeState { Blob = "before-throw" });
        throw new InvalidOperationException("handler bug");
    }
}

public sealed class BadWriteHandler : ICommandHandler<Unit, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.badwrite", touches: [ProbeAggregates.Probe], reads: [IdentityAggregates.Profile]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(Unit request, ICommandContext context)
    {
        context.Set(IdentityAggregates.Profile, new ProfileState { DisplayName = "hacked" });
        return ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
    }
}

public sealed class NoChangeHandler : ICommandHandler<Unit, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.nochange", touches: [CounterAggregates.Counter]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(Unit request, ICommandContext context)
    {
        var counter = context.Get<CounterState>(CounterAggregates.Counter);
        context.Set(CounterAggregates.Counter, counter);
        return ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
    }
}

public sealed class OutboxFloodHandler : ICommandHandler<int, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.outboxflood", touches: [ProbeAggregates.Probe]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(int request, ICommandContext context)
    {
        context.Set(ProbeAggregates.Probe, new ProbeState { Blob = "flood" });
        context.AddOutbox("probe_log", new { text = new string('o', request) });
        return ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
    }
}

public sealed class BigResultHandler : ICommandHandler<int, Unit>
{
    public static CommandDescriptor Descriptor { get; } = new("probe.bigresult", touches: [ProbeAggregates.Probe]);

    public ValueTask<CommandOutcome<Unit>> HandleAsync(int request, ICommandContext context) =>
        ValueTask.FromResult(CommandOutcome<Unit>.Ok(Unit.Value));
}
