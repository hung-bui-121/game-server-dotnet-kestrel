using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Platform.Web.Aggregates;
using GameServer.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>State of the probe's client-visible aggregate <see cref="CatalogProbeModule.Card"/>.</summary>
public sealed record ProbeCard
{
    public long Value { get; init; }

    public int Increments { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Catalog probe of slice 014: a module of the test assembly that declares the given routes and, unless told otherwise, one
/// client-visible aggregate <c>probe_card</c> with a typed wire mapper (message <c>sample.counter.v1.CounterState</c>).
/// </summary>
public sealed class CatalogProbeModule(
    string name = CatalogProbeModule.DefaultName,
    IReadOnlyList<RouteDeclaration>? routes = null,
    IReadOnlyList<string>? publicEndpoints = null,
    bool card = true,
    Action<WireMapperOptions>? cardMapper = null) : IGameModule
{
    public const string DefaultName = "probe";
    public const string Card = "probe_card";

    public static PlayerAggregateDescriptor<ProbeCard> CardDescriptor { get; } = new(Card, 1, clientVisible: true, () => new ProbeCard());

    public static Wire.CounterState ToWire(ProbeCard state) => new() { Value = state.Value, Increments = state.Increments };

    public ModuleDescriptor Describe() => new()
    {
        Name = name,
        Routes = routes ?? [],
        PublicEndpoints = publicEndpoints ?? [],
        Aggregates = card ? [CardDescriptor] : [],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        if (card)
        {
            services.AddAggregateWireMapper<ProbeCard, Wire.CounterState>(Card, ToWire, cardMapper ?? (o => o.Hidden(nameof(ProbeCard.Note))));
        }
    }
}
