using GameServer.Actors;
using GameServer.Commands;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Hosting;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>The request of the starter test commands: the value and padding to set (<c>starter.set</c>).</summary>
public sealed record StarterSet(long Value, string Padding);

/// <summary><c>starter.set</c> touches <c>starter</c>: sets its value and padding (the mapper-fail marker makes its mapping throw).</summary>
public sealed class StarterSetHandler : ICommandHandler<StarterSet, long>
{
    public static CommandDescriptor Descriptor { get; } = new("starter.set", touches: [StartingStateAggregates.Starter]);

    public ValueTask<CommandOutcome<long>> HandleAsync(StarterSet request, ICommandContext context)
    {
        var state = context.Get<StarterState>(StartingStateAggregates.Starter);
        context.Set(StartingStateAggregates.Starter, state with { Value = request.Value, Padding = request.Padding, Increments = state.Increments + 1 });
        return ValueTask.FromResult(CommandOutcome<long>.Ok(request.Value));
    }
}

/// <summary><c>starter.read</c> touches <c>wallet</c> and only reads <c>starter</c>.</summary>
public sealed class StarterReadHandler : ICommandHandler<StarterSet, long>
{
    public static CommandDescriptor Descriptor { get; } = new("starter.read", touches: [EconomyAggregates.Wallet], reads: [StartingStateAggregates.Starter]);

    public ValueTask<CommandOutcome<long>> HandleAsync(StarterSet request, ICommandContext context) =>
        ValueTask.FromResult(CommandOutcome<long>.Ok(context.Get<StarterState>(StartingStateAggregates.Starter).Value));
}

/// <summary><c>starter.untouched</c> touches only <c>wallet</c> and writes nothing.</summary>
public sealed class StarterUntouchedHandler : ICommandHandler<StarterSet, long>
{
    public static CommandDescriptor Descriptor { get; } = new("starter.untouched", touches: [EconomyAggregates.Wallet]);

    public ValueTask<CommandOutcome<long>> HandleAsync(StarterSet request, ICommandContext context) => ValueTask.FromResult(CommandOutcome<long>.Ok(0));
}

/// <summary><c>probe_state.set</c> touches <c>broken_probe</c>: sets its value.</summary>
public sealed class ProbeSetHandler : ICommandHandler<StarterSet, long>
{
    public static CommandDescriptor Descriptor { get; } = new("probe_state.set", touches: [StartingStateAggregates.BrokenProbe]);

    public ValueTask<CommandOutcome<long>> HandleAsync(StarterSet request, ICommandContext context)
    {
        context.Set(StartingStateAggregates.BrokenProbe, context.Get<BrokenProbeState>(StartingStateAggregates.BrokenProbe) with { Value = request.Value });
        return ValueTask.FromResult(CommandOutcome<long>.Ok(request.Value));
    }
}

/// <summary><c>probe_state.read</c> touches <c>wallet</c> and only reads <c>broken_probe</c>.</summary>
public sealed class ProbeReadHandler : ICommandHandler<StarterSet, long>
{
    public static CommandDescriptor Descriptor { get; } = new("probe_state.read", touches: [EconomyAggregates.Wallet], reads: [StartingStateAggregates.BrokenProbe]);

    public ValueTask<CommandOutcome<long>> HandleAsync(StarterSet request, ICommandContext context) =>
        ValueTask.FromResult(CommandOutcome<long>.Ok(context.Get<BrokenProbeState>(StartingStateAggregates.BrokenProbe).Value));
}

/// <summary>
/// Slice 016 (FG-029): a host with Identity, Economy, <see cref="StartingStateModule"/> and player commands that touch, read or leave
/// alone its <c>starter</c> and <c>broken_probe</c> aggregates, for the degraded-read tests. The broken seeds are
/// <see cref="BrokenDocuments"/>.
/// </summary>
public static class BrokenAggregateHost
{
    public static readonly RouteDeclaration SetRoute = RouteDeclaration.Command(StarterSetHandler.Descriptor, "/api/v1/test/starter/set");
    public static readonly RouteDeclaration ReadRoute = RouteDeclaration.Command(StarterReadHandler.Descriptor, "/api/v1/test/starter/read");
    public static readonly RouteDeclaration UntouchedRoute = RouteDeclaration.Command(StarterUntouchedHandler.Descriptor, "/api/v1/test/starter/untouched");
    public static readonly RouteDeclaration ProbeSetRoute = RouteDeclaration.Command(ProbeSetHandler.Descriptor, "/api/v1/test/probe/set");
    public static readonly RouteDeclaration ProbeReadRoute = RouteDeclaration.Command(ProbeReadHandler.Descriptor, "/api/v1/test/probe/read");

    private static readonly RouteDeclaration[] Routes = [SetRoute, ReadRoute, UntouchedRoute, ProbeSetRoute, ProbeReadRoute];

    /// <summary>The command that touches <paramref name="aggregateId"/> (<c>starter</c> or <c>broken_probe</c>).</summary>
    public static RouteDeclaration TouchingRoute(string aggregateId) => aggregateId == StartingStateAggregates.Starter ? SetRoute : ProbeSetRoute;

    /// <summary>The command that only reads <paramref name="aggregateId"/>.</summary>
    public static RouteDeclaration ReadingRoute(string aggregateId) => aggregateId == StartingStateAggregates.Starter ? ReadRoute : ProbeReadRoute;

    public static Task<TestHost> StartAsync(
        Action<Dictionary<string, string?>>? settings = null,
        Action<IServiceCollection>? services = null,
        Action<GameServerBuilder>? modules = null) =>
        SampleTestHost.StartAsync(
            modules: b =>
            {
                b.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule(new StartingStateModule()).AddModule(new StarterCommandsModule());
                b.DeclareRoutes(Routes);
                modules?.Invoke(b);
            },
            settings: settings,
            afterUseGameServer: app =>
            {
                foreach (var route in Routes)
                {
                    app.MapPlayerCommand<Wire.IncrementRequest, StarterSet, long>(
                        route,
                        request => new StarterSet(request.By, request.Note),
                        value => new Wire.IncrementResult { Value = value });
                }
            },
            services: services);

    /// <summary>The wire request of a starter command: <paramref name="value"/> and <paramref name="padding"/>.</summary>
    public static Wire.IncrementRequest Request(long value, string padding = "") => new() { By = value, Note = padding };

    private sealed class StarterCommandsModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "test.starter_commands",
            DependsOn = [StartingStateModule.ModuleName],
            Commands =
            [
                Command.Define<StarterSet, long>(StarterSetHandler.Descriptor).HandledBy<StarterSetHandler>(),
                Command.Define<StarterSet, long>(StarterReadHandler.Descriptor).HandledBy<StarterReadHandler>(),
                Command.Define<StarterSet, long>(StarterUntouchedHandler.Descriptor).HandledBy<StarterUntouchedHandler>(),
                Command.Define<StarterSet, long>(ProbeSetHandler.Descriptor).HandledBy<ProbeSetHandler>(),
                Command.Define<StarterSet, long>(ProbeReadHandler.Descriptor).HandledBy<ProbeReadHandler>(),
            ],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
