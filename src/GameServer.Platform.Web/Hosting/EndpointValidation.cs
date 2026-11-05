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
    /// <summary>
    /// The endpoint lines, then the option failures met while evaluating hosting conditions that are not in
    /// <paramref name="reported"/> yet (lines the caller's report already holds, e.g. from <c>ValidateModuleOptions</c>).
    /// </summary>
    public static IReadOnlyList<string> Run(IReadOnlyList<Endpoint> endpoints, IServiceProvider services, bool includeUnmapped, IReadOnlyCollection<string> reported)
    {
        var options = services.GetRequiredService<GameServerOptions>();
        var registry = services.GetRequiredService<ModuleRegistry>();
        var catalog = services.GetRequiredService<WireCatalog>();
        var optionFailures = new List<string>();
        var context = CheckContext(services, options, optionFailures);
        var errors = new List<string>();
        errors.AddRange(EndpointSecurityValidator.Validate(
            endpoints,
            options,
            PublicTemplates(registry, catalog),
            registry.Modules.SelectMany(m => m.Descriptor.Permissions)));
        errors.AddRange(CommandRouteValidator.Validate(endpoints, registry.Commands));
        errors.AddRange(EndpointCatalogValidator.Validate(endpoints, catalog, context, includeUnmapped));
        errors.AddRange(GmOptionsCheck.Validate(endpoints, catalog, context.ConditionHolds));
        foreach (var failure in optionFailures)
        {
            if (!reported.Contains(failure) && !errors.Contains(failure))
            {
                errors.Add(failure);
            }
        }

        return errors;
    }

    /// <summary>
    /// Public routes besides the operator allowlist (SEC-09, FR-006): module <c>PublicEndpoints</c> and the templates of Public
    /// declarations owned by modules or the platform. A conditional one stays a public template whether or not its condition
    /// holds: mapping it while the condition does not hold is the one condition line of the catalog check (data-model §2.2).
    /// Host Public declarations still need <c>GameServer:PublicEndpointAllowlist</c> (plan N6).
    /// </summary>
    internal static IEnumerable<string> PublicTemplates(ModuleRegistry registry, WireCatalog catalog) =>
        registry.Modules.SelectMany(m => m.Descriptor.PublicEndpoints)
            .Concat(catalog.Routes
                .Where(r => r.Declaration.Profile == SecurityProfile.Public && r.Owner != RouteEntry.HostOwner)
                .Select(r => r.Declaration.Template));

    /// <summary>
    /// The host facts of FR-011; a condition is evaluated only when a declaration names it (last registration wins), at most once
    /// per check run. Option failures met while evaluating are added to <paramref name="optionFailures"/>.
    /// </summary>
    private static EndpointCheckContext CheckContext(IServiceProvider services, GameServerOptions options, List<string> optionFailures)
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
                    holds = Holds(conditions.LastOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal)), services, optionFailures);
                    evaluated[name] = holds;
                }

                return holds;
            });
    }

    /// <summary>
    /// A condition reading options that fail validation does not hold, and the failure joins the report (FR-009): options with
    /// <c>ValidateOnStart</c> are reported by <c>ValidateModuleOptions</c> too (kept once by <see cref="Run"/>), options with only
    /// <c>Validate()</c> nowhere else, so the host would otherwise start with them invalid.
    /// </summary>
    private static bool Holds(RouteConditionRegistration? condition, IServiceProvider services, List<string> optionFailures)
    {
        try
        {
            return condition?.Holds(services) == true;
        }
        catch (OptionsValidationException ex)
        {
            optionFailures.AddRange(ex.Failures);
            return false;
        }
    }
}
