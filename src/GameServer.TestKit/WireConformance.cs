using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.TestKit;

/// <summary>
/// Wire-mapper conformance (FG-027, FR-061..FR-066): a client-visible aggregate's mapper carries every persisted member that is not
/// declared hidden and sets every proto field that may not stay default. No unit-test framework, no host, no store: call
/// <see cref="WireConformanceReport.ThrowIfFailed"/> from any test framework.
/// </summary>
public static class WireConformance
{
    /// <summary>Context handed to the modules' <c>ConfigureServices</c> (registrations only; nothing is started).</summary>
    private static readonly ModuleContext Context = new("wire-conformance", "wire-conformance", "Development");

    /// <summary>
    /// One client-visible aggregate of <paramref name="module"/>, with the mapper the module registers. A
    /// <c>ConfigureExtensions(e => e.ReplaceWireMapper(…))</c> override of the host is not seen here: use <see cref="CheckAll"/>.
    /// </summary>
    public static WireConformanceReport Check(IGameModule module, string aggregateId, WireConformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrEmpty(aggregateId);
        var aggregate = module.Describe().Aggregates.FirstOrDefault(a => a.AggregateId == aggregateId && a.ClientVisible)
            ?? throw new ArgumentException($"Module {module.GetType().Name} declares no client-visible aggregate '{aggregateId}'.", nameof(aggregateId));
        return Run([aggregate], MappersOf([module]), options);
    }

    /// <summary>
    /// Every client-visible aggregate declared by <paramref name="modules"/> (their ConfigureServices run against a bare service
    /// collection), with the mappers the modules register. A host's <c>ReplaceWireMapper</c> override is not seen here: use
    /// <see cref="CheckAll"/> on the built host.
    /// </summary>
    public static WireConformanceReport CheckModules(IEnumerable<IGameModule> modules, WireConformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToList();
        var aggregates = list.SelectMany(module => module.Describe().Aggregates).Where(a => a.ClientVisible).DistinctBy(a => a.AggregateId, StringComparer.Ordinal);
        return Run(aggregates, MappersOf(list), options);
    }

    /// <summary>
    /// Every client-visible aggregate of a built host composition (e.g. <c>TestHost.BuildApp(...).Services</c>,
    /// <c>LocalFlavorFactory.Services</c>): the only entry point that checks the mappers the host serves, including
    /// <c>ReplaceWireMapper</c> overrides.
    /// </summary>
    public static WireConformanceReport CheckAll(IServiceProvider services, WireConformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var aggregates = services.GetRequiredService<ModuleRegistry>().Aggregates.Values.Where(a => a.ClientVisible);
        return Run(aggregates, services.GetRequiredService<AggregateWireMapperRegistry>(), options);
    }

    private static WireConformanceReport Run(IEnumerable<PlayerAggregateDescriptor> aggregates, AggregateWireMapperRegistry mappers, WireConformanceOptions? options)
    {
        options ??= new WireConformanceOptions();
        var findings = new List<WireConformanceFinding>();
        var notes = new List<string>();
        var ids = aggregates.Select(a => a.AggregateId).Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids)
        {
            if (!mappers.Has(id))
            {
                findings.Add(new WireConformanceFinding(id, WireConformanceProblem.NoMapper, string.Empty,
                    "the aggregate is client-visible but has no wire mapper. Fix: AddAggregateWireMapper<TState, TWire>(id, map) in the module adapter (API-10)."));
            }
            else if (!mappers.TryGetContract(id, out var contract))
            {
                findings.Add(new WireConformanceFinding(id, WireConformanceProblem.UntypedMapper, string.Empty,
                    "the mapper was registered without its message type, so the kit cannot parse the payload. Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028)."));
            }
            else
            {
                AggregateConformance.Run(id, contract, mappers, options, findings, notes);
            }
        }

        return new WireConformanceReport(ids, findings, notes);
    }

    /// <summary>The modules' registrations in a bare collection; only the <see cref="IAggregateWireMapper"/> services are resolved.</summary>
    private static AggregateWireMapperRegistry MappersOf(IEnumerable<IGameModule> modules)
    {
        var services = new ServiceCollection();
        foreach (var module in modules)
        {
            module.ConfigureServices(services, Context);
        }

        using var provider = services.BuildServiceProvider();
        return new AggregateWireMapperRegistry(provider.GetServices<IAggregateWireMapper>().ToArray());
    }
}
