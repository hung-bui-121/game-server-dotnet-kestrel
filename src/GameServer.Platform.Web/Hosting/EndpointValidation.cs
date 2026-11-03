using GameServer.Actors;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var context = CheckContext(services, options);
        var errors = new List<string>();
        errors.AddRange(EndpointSecurityValidator.Validate(
            endpoints,
            options,
            PublicTemplates(registry, catalog, context.ConditionHolds),
            registry.Modules.SelectMany(m => m.Descriptor.Permissions)));
        errors.AddRange(CommandRouteValidator.Validate(endpoints, registry.Commands));
        errors.AddRange(EndpointCatalogValidator.Validate(endpoints, catalog, context, includeUnmapped));
        return errors;
    }

    /// <summary>
    /// Public routes besides the operator allowlist (SEC-09, FR-006): module <c>PublicEndpoints</c> and the templates of Public
    /// declarations owned by modules or the platform whose hosting condition, if any, holds (a route that must not be mapped
    /// opens no template). Host Public declarations still need <c>GameServer:PublicEndpointAllowlist</c> (plan N6).
    /// </summary>
    internal static IEnumerable<string> PublicTemplates(ModuleRegistry registry, WireCatalog catalog, Func<string, bool> conditionHolds) =>
        registry.Modules.SelectMany(m => m.Descriptor.PublicEndpoints)
            .Concat(catalog.Routes
                .Where(r => r.Declaration.Profile == SecurityProfile.Public && r.Owner != RouteEntry.HostOwner)
                .Where(r => r.Declaration.Condition is not { } condition || conditionHolds(condition))
                .Select(r => r.Declaration.Template));

    /// <summary>
    /// The host facts of FR-011; a condition is evaluated only when a declaration names it (last registration wins), at most once
    /// per check run.
    /// </summary>
    private static EndpointCheckContext CheckContext(IServiceProvider services, GameServerOptions options)
    {
        var conditions = services.GetServices<RouteConditionRegistration>().ToArray();
        var evaluated = new Dictionary<string, bool>(StringComparer.Ordinal);
        return new EndpointCheckContext(
            options.Hosting.MapPlayerApi,
            options.Hosting.MapGmApi,
            name =>
            {
                if (!evaluated.TryGetValue(name, out var holds))
                {
                    holds = Holds(conditions.LastOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal)), services);
                    evaluated[name] = holds;
                }

                return holds;
            });
    }

    /// <summary>
    /// A condition reading module options that fail validation (GM SSO in a host with nothing configured) does not hold: that failure
    /// is already one line of the report (<c>ValidateModuleOptions</c>), and the module could not map the route either.
    /// </summary>
    private static bool Holds(RouteConditionRegistration? condition, IServiceProvider services)
    {
        try
        {
            return condition?.Holds(services) == true;
        }
        catch (OptionsValidationException)
        {
            return false;
        }
    }
}
