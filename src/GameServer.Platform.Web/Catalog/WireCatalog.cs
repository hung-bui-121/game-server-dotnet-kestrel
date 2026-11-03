using System.Diagnostics.CodeAnalysis;
using GameServer.Platform.Web.Aggregates;
using GameServer.Routing;

namespace GameServer.Platform.Web.Catalog;

/// <summary>A route listed in a module's <c>PublicEndpoints</c> (checked against its declaration, FR-006).</summary>
internal sealed record ModulePublicEndpoint(string Module, string Template);

/// <summary>
/// Read-only route and section catalog of a host (FG-026, FG-028). DI singleton, available after Build() without starting:
/// built from the registered modules (service-only included and flagged), the platform routes, the host's own declarations
/// and the wire mappers.
/// </summary>
public sealed class WireCatalog
{
    private readonly Dictionary<string, RouteEntry> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RouteEntry>> _byTemplate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SectionEntry> _sections = new(StringComparer.Ordinal);

    internal WireCatalog(
        IEnumerable<RouteEntry> routes,
        IEnumerable<SectionEntry> sections,
        IEnumerable<string> conditionNames,
        IEnumerable<ModulePublicEndpoint> modulePublicEndpoints,
        IEnumerable<DuplicateWireMapper> duplicateMappers,
        IReadOnlyDictionary<string, IAggregateWireContract> contracts)
    {
        Routes = routes.OrderBy(r => r.Declaration.Name, StringComparer.Ordinal).ToArray();
        Sections = sections.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        ConditionNames = conditionNames.ToHashSet(StringComparer.Ordinal);
        ModulePublicEndpoints = modulePublicEndpoints.ToArray();
        DuplicateMappers = duplicateMappers.ToArray();
        Contracts = contracts;
        foreach (var route in Routes)
        {
            _byName.TryAdd(route.Declaration.Name, route);
            if (!_byTemplate.TryGetValue(route.Declaration.Template, out var sameTemplate))
            {
                _byTemplate[route.Declaration.Template] = sameTemplate = [];
            }

            sameTemplate.Add(route);
        }

        foreach (var section in Sections)
        {
            _sections.TryAdd(section.Id, section);
        }
    }

    /// <summary>Every declared route, ordinal by name.</summary>
    public IReadOnlyList<RouteEntry> Routes { get; }

    /// <summary>Every client-visible section with a wire mapper, ordinal by id.</summary>
    public IReadOnlyList<SectionEntry> Sections { get; }

    internal IReadOnlySet<string> ConditionNames { get; }

    internal IReadOnlyList<ModulePublicEndpoint> ModulePublicEndpoints { get; }

    internal IReadOnlyList<DuplicateWireMapper> DuplicateMappers { get; }

    /// <summary>Typed mapper contracts by section id (sections of untyped mappers are absent).</summary>
    internal IReadOnlyDictionary<string, IAggregateWireContract> Contracts { get; }

    public bool TryGetRoute(string name, [NotNullWhen(true)] out RouteEntry? entry) => _byName.TryGetValue(name, out entry);

    public bool TryGetSection(string id, [NotNullWhen(true)] out SectionEntry? entry) => _sections.TryGetValue(id, out entry);

    /// <summary>Declaration faults (data-model §2.1) as report lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate() => WireCatalogRules.Validate(this);

    /// <summary>Deterministic export, format <see cref="WireCatalogExport.Format"/> (data-model §3).</summary>
    public string ToExportJson() => WireCatalogExport.Write(this);

    /// <summary>Faults that do not stop the host (untyped wire mappers, FG-028), logged once at startup.</summary>
    internal IReadOnlyList<string> ValidationWarnings => WireCatalogRules.Warnings(this);

    /// <summary>Name of the declaration for a method and template (any-method declarations match every method); null if none.</summary>
    public string? FindRouteName(string method, string template) => FindRoute(method, template)?.Declaration.Name;

    /// <summary>The entries declaring <paramref name="template"/> (canonical form compared ordinally); empty if none.</summary>
    internal IReadOnlyList<RouteEntry> RoutesAt(string template) =>
        _byTemplate.TryGetValue(RouteDeclaration.CanonicalTemplate(template), out var routes) ? routes : [];

    internal RouteEntry? FindRoute(string method, string template)
    {
        ArgumentNullException.ThrowIfNull(method);
        return RoutesAt(template).FirstOrDefault(r =>
            r.Declaration.Methods.Count == 0 || r.Declaration.Methods.Contains(method, StringComparer.OrdinalIgnoreCase));
    }
}
