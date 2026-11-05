using System.ComponentModel.DataAnnotations;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Validation;

/// <summary>
/// Startup check of GM bodies (slice 017, FR-045, D13; review r1 m2, m4), one line per fault joining the one startup report
/// (data-model §6), over the body contract of every mapped <c>/gm/**</c> endpoint with <c>Accepts</c> metadata:
/// <list type="bullet">
/// <item>every <see cref="GmOptionsAttribute"/> names a catalog route with method <c>GET</c>, surface GmApi, mapped on this host
/// (not service-only, condition holding) — a typo would otherwise point a console at a missing route;</item>
/// <item>that route needs <c>gm.access</c> or the endpoint's own permission — otherwise staff who may run the command cannot load
/// its choices;</item>
/// <item>every validation attribute can run (an invalid regex or an unconvertible <c>[Range]</c> operand would otherwise be a 500
/// on the first request).</item>
/// </list>
/// </summary>
internal static class GmOptionsCheck
{
    /// <summary>The GM sign-in permission (module gm): every staff with a session has it.</summary>
    internal const string GmAccessPermission = "gm.access";

    private const string GmPrefix = "/gm/";

    public static IReadOnlyList<string> Validate(IEnumerable<Endpoint> endpoints, WireCatalog catalog, Func<string, bool> conditionHolds)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(conditionHolds);
        var errors = new List<string>();
        var probedTypes = new HashSet<Type>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = RouteDeclaration.CanonicalTemplate(endpoint.RoutePattern.RawText ?? string.Empty);
            if (!template.StartsWith(GmPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = $"[endpoint {string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {template}]";
            var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
            foreach (var type in endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>().Select(a => a.RequestType).OfType<Type>().Distinct())
            {
                // A body type's attributes are probed once, under the first endpoint that reads it.
                var probe = probedTypes.Add(type);
                foreach (var (path, property) in PropertiesOf(GmBodyContract.For(type), prefix: string.Empty))
                {
                    if (property.OptionsRoute is { } route)
                    {
                        CheckOptions(errors, catalog, conditionHolds, $"{label} body {type.Name} property '{path}'", route, permission);
                    }

                    if (probe)
                    {
                        ProbeAttributes(errors, $"{label} body {type.Name} property '{path}'", property);
                    }
                }
            }
        }

        return errors;
    }

    private static void CheckOptions(
        List<string> errors,
        WireCatalog catalog,
        Func<string, bool> conditionHolds,
        string subject,
        string route,
        string? endpointPermission)
    {
        if (!catalog.TryGetRoute(route, out var entry)
            || !entry.Declaration.Methods.Contains(HttpMethods.Get, StringComparer.Ordinal)
            || entry.Declaration.Surface != RouteSurface.GmApi
            || entry.ServiceOnly
            || (entry.Declaration.Condition is { } condition && !conditionHolds(condition)))
        {
            errors.Add(
                $"{subject} names option route '{route}', which is not a GM GET route of this host. Fix: declare the route " +
                "(RouteDeclaration.Get(..., RouteSurface.GmApi)) or correct the name in [GmOptions] (FR-045).");
            return;
        }

        var optionPermission = entry.Declaration.Permission;
        if (optionPermission != GmAccessPermission && optionPermission != endpointPermission)
        {
            errors.Add(
                $"{subject} names option route '{route}', whose permission '{optionPermission ?? "none"}' is neither {GmAccessPermission} nor " +
                $"the endpoint's permission '{endpointPermission ?? "none"}'. Fix: declare the option route with '{endpointPermission}' or " +
                $"{GmAccessPermission} (FR-045).");
        }
    }

    /// <summary>
    /// Runs each built-in attribute (<c>System.ComponentModel.DataAnnotations</c>) once on <c>null</c>: they build their regex,
    /// conversion or bounds before looking at the value, so a broken argument throws here instead of on the first request. A
    /// game's own attribute is not probed: it may legitimately refuse to run on <c>null</c> (review r2 m1).
    /// </summary>
    private static void ProbeAttributes(List<string> errors, string subject, GmBodyProperty property)
    {
        foreach (var attribute in property.Validations.Where(a => a.GetType().Assembly == typeof(ValidationAttribute).Assembly))
        {
            try
            {
                attribute.GetValidationResult(null, new ValidationContext(new object()) { MemberName = property.ClrName, DisplayName = property.JsonName });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var reason = ex.Message.ReplaceLineEndings(" ");
                errors.Add(
                    $"{subject} has a {attribute.GetType().Name} that cannot run ({ex.GetType().Name}: {reason}). " +
                    "Fix: correct the attribute's arguments (FR-021).");
            }
        }
    }

    /// <summary>(JSON path, property) of every property of the contract, nested ones included (list items as <c>items[].x</c>).</summary>
    private static IEnumerable<(string Path, GmBodyProperty Property)> PropertiesOf(GmBodyContract contract, string prefix)
    {
        foreach (var property in contract.Properties)
        {
            var path = prefix.Length == 0 ? property.JsonName : prefix + "." + property.JsonName;
            yield return (path, property);
            if (property.Nested is { } nested)
            {
                foreach (var inner in PropertiesOf(nested, property.IsCollection ? path + "[]" : path))
                {
                    yield return inner;
                }
            }
        }
    }
}
