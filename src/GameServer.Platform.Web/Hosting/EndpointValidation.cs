using GameServer.Actors;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Hosting;

/// <summary>
/// One entry point running every endpoint validator over a given endpoint set with the host's inputs (research R8): security
/// profile rules, command routes and the route catalog. <c>UseGameServer</c> runs it over the endpoints mapped so far; the
/// pre-serving run over the final set also reports declared routes that are not mapped (plan N7).
/// </summary>
internal static class EndpointValidation
{
    public static IReadOnlyList<string> Run(IReadOnlyList<Endpoint> endpoints, IServiceProvider services, bool includeUnmapped)
    {
        var options = services.GetRequiredService<GameServerOptions>();
        var registry = services.GetRequiredService<ModuleRegistry>();
        var catalog = services.GetRequiredService<WireCatalog>();
        var errors = new List<string>();
        errors.AddRange(EndpointSecurityValidator.Validate(
            endpoints,
            options,
            PublicTemplates(registry, catalog),
            registry.Modules.SelectMany(m => m.Descriptor.Permissions)));
        errors.AddRange(CommandRouteValidator.Validate(endpoints, registry.Commands));
        errors.AddRange(EndpointCatalogValidator.Validate(endpoints, catalog, CheckContext(services, options, registry), includeUnmapped));
        return errors;
    }

    /// <summary>
    /// Public routes besides the operator allowlist (SEC-09, FR-006): module <c>PublicEndpoints</c> and the templates of Public
    /// declarations owned by modules or the platform. Host Public declarations still need <c>GameServer:PublicEndpointAllowlist</c>
    /// (plan N6).
    /// </summary>
    internal static IEnumerable<string> PublicTemplates(ModuleRegistry registry, WireCatalog catalog) =>
        registry.Modules.SelectMany(m => m.Descriptor.PublicEndpoints)
            .Concat(catalog.Routes
                .Where(r => r.Declaration.Profile == SecurityProfile.Public && r.Owner != RouteEntry.HostOwner)
                .Select(r => r.Declaration.Template));

    /// <summary>The host facts of FR-011; a condition is evaluated only when an unmapped declaration names it (last registration wins).</summary>
    private static EndpointCheckContext CheckContext(IServiceProvider services, GameServerOptions options, ModuleRegistry registry)
    {
        var conditions = services.GetServices<RouteConditionRegistration>().ToArray();
        return new EndpointCheckContext(
            options.Hosting.MapPlayerApi,
            options.Hosting.MapGmApi,
            registry.Modules.Where(m => m.ServiceOnly).Select(m => m.Descriptor.Name).ToHashSet(StringComparer.Ordinal),
            name => conditions.LastOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.Holds(services) == true);
    }
}
