using GameServer.Modules;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Catalog;

/// <summary>Builds the host's <see cref="WireCatalog"/> from registrations only: no store, configuration file or secret (research R5).</summary>
internal static class WireCatalogBuilder
{
    /// <summary>Assembly name prefix reserved for the framework: modules from such assemblies have origin framework.</summary>
    public const string FrameworkAssemblyPrefix = "GameServer.";

    public static WireCatalog Build(IServiceProvider services) =>
        Build(
            services.GetRequiredService<ModuleRegistry>(),
            services.GetRequiredService<HostRouteDeclarations>(),
            services.GetRequiredService<AggregateWireMapperRegistry>(),
            services.GetServices<RouteConditionRegistration>().Select(c => c.Name));

    public static WireCatalog Build(
        ModuleRegistry registry,
        HostRouteDeclarations host,
        AggregateWireMapperRegistry mappers,
        IEnumerable<string> conditionNames)
    {
        var routes = PlatformRoutes.All
            .Select(route => new RouteEntry(route, RouteEntry.PlatformOwner, CatalogOrigin.Framework, ServiceOnly: false))
            .Concat(registry.Modules.SelectMany(module => module.Descriptor.Routes
                .Select(route => new RouteEntry(route, module.Descriptor.Name, OriginOf(module.Module), module.ServiceOnly))))
            .Concat(host.Routes.Select(route => new RouteEntry(route, RouteEntry.HostOwner, CatalogOrigin.Game, ServiceOnly: false)));

        var contracts = new Dictionary<string, WireContractEntry>(StringComparer.Ordinal);
        var sections = new List<SectionEntry>();
        foreach (var aggregate in registry.Aggregates.Values.Where(a => a.ClientVisible && mappers.Has(a.AggregateId)))
        {
            var owner = registry.Modules.First(m => m.Descriptor.Aggregates.Any(a => a.AggregateId == aggregate.AggregateId));
            mappers.TryGetContract(aggregate.AggregateId, out var contract);
            if (contract is not null)
            {
                contracts[aggregate.AggregateId] = new WireContractEntry(contract, aggregate.StateType, (contract as IWireMapperReplacement)?.ReplacedMessage);
            }

            sections.Add(new SectionEntry(
                aggregate.AggregateId,
                owner.Descriptor.Name,
                OriginOf(owner.Module),
                contract?.Message.FullName,
                contract?.HiddenMembers ?? [],
                contract?.MayStayDefaultFields ?? []));
        }

        var publicEndpoints = registry.Modules
            .SelectMany(module => module.Descriptor.PublicEndpoints.Select(template => new ModulePublicEndpoint(module.Descriptor.Name, template)));
        return new WireCatalog(routes, sections, conditionNames, publicEndpoints, mappers.Duplicates, contracts);
    }

    private static CatalogOrigin OriginOf(IGameModule module) =>
        module.GetType().Assembly.GetName().Name?.StartsWith(FrameworkAssemblyPrefix, StringComparison.Ordinal) == true
            ? CatalogOrigin.Framework
            : CatalogOrigin.Game;
}
