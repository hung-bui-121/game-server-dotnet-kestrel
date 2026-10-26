using System.Text.RegularExpressions;
using GameServer.Actors;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Validation;

/// <summary>
/// Inspects every mapped endpoint before traffic (SEC-08, API-02, ARC-07, SEC-09, OPS-09):
/// exactly one security profile, versioned route, no duplicate route+method, Public only from the
/// allowlist, no Dev endpoints in Production.
/// </summary>
public static partial class EndpointSecurityValidator
{
    public static IReadOnlyList<string> Validate(
        IEnumerable<Endpoint> endpoints,
        GameServerOptions options,
        IEnumerable<string>? modulePublicEndpoints = null,
        IEnumerable<string>? permissionCatalog = null)
    {
        var permissions = permissionCatalog?.ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowlist = options.PublicEndpointAllowlist.Concat(modulePublicEndpoints ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            var profiles = endpoint.Metadata.GetOrderedMetadata<SecurityProfileMetadata>();
            var label = $"{string.Join(",", methods)} {route}";

            if (profiles.Count != 1)
            {
                errors.Add(
                    $"[endpoint {label}] has {profiles.Count} security profiles. " +
                    "Fix: declare exactly one with AsPlayerEndpoint/AsGmEndpoint/AsPublicEndpoint/AsDevEndpoint (SEC-08).");
            }

            // Health probes have fixed, unversioned paths for load balancers and orchestrators (OBS-03).
            if (!VersionSegment().IsMatch(route) && !route.StartsWith("/health/", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"[endpoint {label}] route has no version segment. Fix: prefix with /v{{n}}/ (API-02).");
            }

            foreach (var method in methods)
            {
                if (!seen.Add($"{method} {route}"))
                {
                    errors.Add($"[endpoint {method} {route}] is mapped more than once. Fix: remove the duplicate route (ARC-07).");
                }
            }

            if (profiles.Any(p => p.Profile == SecurityProfile.Public) && !allowlist.Contains(route))
            {
                errors.Add(
                    $"[endpoint {label}] is Public but not in GameServer:PublicEndpointAllowlist. " +
                    "Fix: add it to the allowlist and the PCR (SEC-09) or change its profile.");
            }

            if (permissions is not null && profiles.Any(p => p.Profile == SecurityProfile.Gm))
            {
                var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
                if (permission is null || !permissions.Contains(permission))
                {
                    errors.Add(
                        $"[endpoint {label}] is a GM endpoint whose permission '{permission}' is not in any module's permission catalog. " +
                        "Fix: declare it in ModuleDescriptor.Permissions and use AsGmEndpoint(permission) (SEC-08).");
                }
            }

            if (options.IsProduction && profiles.Any(p => p.Profile == SecurityProfile.Dev))
            {
                errors.Add($"[endpoint {label}] uses profile Dev in Production. Fix: do not register cheat/dev modules in Production (OPS-09).");
            }
        }

        return errors;
    }

    [GeneratedRegex("(^|/)v[0-9]+(/|$)")]
    private static partial Regex VersionSegment();
}
