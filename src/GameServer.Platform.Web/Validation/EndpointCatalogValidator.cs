using GameServer.Actors;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Validation;

/// <summary>What the check needs to know about the host: the mapped surfaces and whether a hosting condition holds.</summary>
public sealed record EndpointCheckContext(bool MapPlayerApi, bool MapGmApi, Func<string, bool> ConditionHolds);

/// <summary>
/// Mapped endpoints ↔ catalog (FR-010, research R7): one report line per fault (data-model §2.2). An endpoint binds to its
/// <see cref="RouteDeclarationMetadata"/> (which must be the very declaration the catalog lists), else each of its (method,
/// template) pairs to the declaration listing that method at that template. Non-route endpoints are ignored.
/// </summary>
public static class EndpointCatalogValidator
{
    private const string AnyMethod = "*";

    public static IReadOnlyList<string> Validate(IEnumerable<Endpoint> endpoints, WireCatalog catalog, EndpointCheckContext context, bool includeUnmapped)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<string>();
        var mapped = new HashSet<(RouteDeclaration Declaration, string Method)>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = RouteDeclaration.CanonicalTemplate(endpoint.RoutePattern.RawText ?? string.Empty);
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [AnyMethod];
            var label = $"{string.Join(",", methods)} {template}";
            if (endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()?.Declaration is { } declared)
            {
                // A declaration no module, platform or host lists is not in the export (nor in the generated client routes).
                if (!catalog.Contains(declared))
                {
                    errors.Add(NotListed(label, declared, catalog));
                    continue;
                }

                CheckBound(endpoint, declared, template, methods, label, errors);
                CheckCondition(declared, catalog, context, errors);
                foreach (var method in Pairs(declared))
                {
                    mapped.Add((declared, method));
                }

                continue;
            }

            var candidates = catalog.RoutesAt(template);
            if (candidates.Count == 0)
            {
                errors.Add(NotDeclared(label));
                continue;
            }

            foreach (var declaration in Bind(methods, candidates, label, errors, mapped))
            {
                CheckSecurity(endpoint, declaration, label, errors);
                CheckCondition(declaration, catalog, context, errors);
            }
        }

        if (includeUnmapped)
        {
            AddUnmapped(catalog, context, mapped, errors);
        }

        return errors;
    }

    private static string NotDeclared(string label) =>
        $"[endpoint {label}] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes " +
        "(or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).";

    /// <summary>An endpoint mapped from a declaration instance the catalog does not list: a copy of a listed one, or an undeclared one.</summary>
    private static string NotListed(string label, RouteDeclaration declared, WireCatalog catalog) =>
        catalog.TryGetRoute(declared.Name, out var listed)
            ? $"[endpoint {label}] is mapped from a declaration named {declared.Name} that is not the one {listed.Owner} lists. " +
              "Fix: map it from the declaration instance listed in ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes), not a copy (FG-026)."
            : NotDeclared(label);

    /// <summary>
    /// FR-011: a route whose condition does not hold must not be mapped. An unregistered condition is reported once by the
    /// declaration rules (<see cref="WireCatalog.Validate"/>), not here.
    /// </summary>
    private static void CheckCondition(RouteDeclaration declared, WireCatalog catalog, EndpointCheckContext context, List<string> errors)
    {
        if (declared.Condition is { } condition && catalog.ConditionNames.Contains(condition) && !context.ConditionHolds(condition))
        {
            errors.Add(
                $"[route {declared.Name}] is mapped but its condition '{condition}' does not hold. " +
                "Fix: map it only when the condition holds, or correct the declaration's condition (FG-026).");
        }
    }

    /// <summary>The declaration of each endpoint pair; a method no declaration at the template lists is one "method differs" line.</summary>
    private static List<RouteDeclaration> Bind(
        IReadOnlyList<string> methods,
        IReadOnlyList<RouteEntry> candidates,
        string label,
        List<string> errors,
        HashSet<(RouteDeclaration Declaration, string Method)> mapped)
    {
        var bound = new List<RouteDeclaration>();
        foreach (var method in methods.Select(m => m.ToUpperInvariant()))
        {
            var declaration = candidates.Select(c => c.Declaration).FirstOrDefault(d => Pairs(d).Contains(method, StringComparer.Ordinal));
            if (declaration is null)
            {
                var other = candidates[0].Declaration;
                errors.Add(
                    $"[endpoint {label}] maps method {method} but route {other.Name} declares {other.MethodLabel}. " +
                    "Fix: declare one route per method or correct the mapping (FG-026).");
                continue;
            }

            mapped.Add((declaration, method));
            if (!bound.Contains(declaration))
            {
                bound.Add(declaration);
            }
        }

        return bound;
    }

    /// <summary>An endpoint mapped from its declaration: template, methods, then the security comparisons.</summary>
    private static void CheckBound(RouteEndpoint endpoint, RouteDeclaration declared, string template, IReadOnlyList<string> methods, string label, List<string> errors)
    {
        if (!string.Equals(template, declared.Template, StringComparison.Ordinal))
        {
            errors.Add(
                $"[route {declared.Name}] declared at {declared.Template} but mapped at {template}. " +
                "Fix: map it with the declaration and no route group prefix, or correct the declaration (FG-026).");
            return;
        }

        var mappedMethods = methods.Select(m => m.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!mappedMethods.SequenceEqual(Pairs(declared), StringComparer.Ordinal))
        {
            errors.Add(
                $"[endpoint {label}] maps method {string.Join(",", mappedMethods)} but route {declared.Name} declares {declared.MethodLabel}. " +
                "Fix: declare one route per method or correct the mapping (FG-026).");
        }

        CheckSecurity(endpoint, declared, label, errors);
    }

    /// <summary>Profile (only with exactly one profile: zero or several are reported by EndpointSecurityValidator), operation, GM permission.</summary>
    private static void CheckSecurity(RouteEndpoint endpoint, RouteDeclaration declared, string label, List<string> errors)
    {
        var profiles = endpoint.Metadata.GetOrderedMetadata<SecurityProfileMetadata>();
        var profileMatches = profiles.Count == 1 && profiles[0].Profile == declared.Profile;
        if (profiles.Count == 1 && !profileMatches)
        {
            errors.Add(
                $"[endpoint {label}] has profile {profiles[0].Profile} but route {declared.Name} declares {declared.Profile}. " +
                "Fix: use the As*Endpoint helper of the declared profile or correct the declaration (SEC-08).");
        }

        var operation = endpoint.Metadata.GetMetadata<CommandOperationMetadata>()?.Operation;
        if (!string.Equals(operation, declared.Operation, StringComparison.Ordinal))
        {
            errors.Add(
                $"[endpoint {label}] runs command '{operation}' but route {declared.Name} declares '{declared.Operation}'. " +
                "Fix: map it with MapPlayerCommand/MapDevCommand/MapGmCommand(declaration, …) (ARC-07).");
        }

        var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
        if (profileMatches && declared.Profile == SecurityProfile.Gm && !string.Equals(permission, declared.Permission, StringComparison.Ordinal))
        {
            errors.Add(
                $"[endpoint {label}] requires permission '{permission}' but route {declared.Name} declares '{declared.Permission}'. " +
                "Fix: map it with the declaration (SEC-07).");
        }
    }

    /// <summary>
    /// FR-010/FR-011: a declaration whose surface is mapped, whose owner is not service-only and whose condition holds must
    /// have an endpoint for each of its methods.
    /// </summary>
    private static void AddUnmapped(WireCatalog catalog, EndpointCheckContext context, HashSet<(RouteDeclaration Declaration, string Method)> mapped, List<string> errors)
    {
        foreach (var entry in catalog.Routes)
        {
            var route = entry.Declaration;
            if (entry.ServiceOnly
                || !SurfaceMapped(route.Surface, context)
                || (route.Condition is { } condition && !context.ConditionHolds(condition))
                || Pairs(route).All(method => mapped.Contains((route, method))))
            {
                continue;
            }

            errors.Add(
                $"[route {route.Name}] ({route.MethodLabel} {route.Template}, owner {entry.Owner}, surface {route.Surface}) is declared but not mapped. " +
                $"Fix: map it in the module's {MapMethodOf(route.Surface)} method or remove the declaration (FG-026).");
        }
    }

    private static bool SurfaceMapped(RouteSurface surface, EndpointCheckContext context) => surface switch
    {
        RouteSurface.PlayerApi => context.MapPlayerApi,
        RouteSurface.GmApi => context.MapGmApi,
        _ => true,
    };

    private static string MapMethodOf(RouteSurface surface) => surface switch
    {
        RouteSurface.PlayerApi => nameof(IPlayerApiModule.MapPlayerApi),
        RouteSurface.GmApi => nameof(IGmApiModule.MapGmApi),
        _ => nameof(IHostEndpointsModule.MapHostEndpoints),
    };

    /// <summary>The declaration's (method) pairs: its methods, or <c>*</c> for any method.</summary>
    private static IReadOnlyList<string> Pairs(RouteDeclaration route) => route.Methods.Count == 0 ? [AnyMethod] : route.Methods;
}
