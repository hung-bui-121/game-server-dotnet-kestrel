using GameServer.Platform;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.OpenApi;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace GameServer.Modules.Gm;

/// <summary>
/// Console bootstrap routes (slice 017, design §10): the sign-in methods a login page shows (Public, two booleans only) and the
/// GM document a generic console renders, in every environment, revalidated with its entity tag.
/// </summary>
internal static class GmConsoleEndpoints
{
    /// <summary>The catalog lives behind a GM session: shared caches must not keep it, browsers revalidate it every time (A8).</summary>
    public const string CatalogCacheControl = "private, no-cache";

    private const string WeakPrefix = "W/";

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapDeclared(GmRoutes.AuthMethods, (Func<HttpContext, IResult>)AuthMethodsOf)
            .Produces<GmAuthMethods>()
            .WithSummary("Get which sign-in methods (local account, SSO) this host offers.")
            .AsPublicEndpoint(protobuf: false)
            .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy)
            .RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy);
        routes.MapDeclared(GmRoutes.ConsoleCatalog, (Func<HttpContext, Task<IResult>>)CatalogAsync)
            .ExcludeFromDescription()
            .WithSummary("Get the GM document of this host for a generic console (ETag, 304).")
            .AsGmEndpoint(GmPermissions.Access);
    }

    /// <summary>
    /// <c>local</c> = the local login route is mapped here (development tools, SEC-03); <c>sso</c> = <c>Gm:Oidc</c> is configured
    /// (configuration, not reachability of the identity provider). Nothing else: no version, staff or setting (OBS-03).
    /// </summary>
    private static IResult AuthMethodsOf(HttpContext http)
    {
        var services = http.RequestServices;
        return GmHttp.Json(new GmAuthMethods(
            services.GetRequiredService<GameServerOptions>().AllowsDevelopmentTools,
            services.GetRequiredService<IOptions<GmOptions>>().Value.Oidc.IsConfigured));
    }

    /// <summary>The canonical bytes with their entity tag; 304 without a body when <c>If-None-Match</c> names it.</summary>
    private static async Task<IResult> CatalogAsync(HttpContext http)
    {
        var catalog = await http.RequestServices.GetRequiredService<GmCatalogDocument>().GetAsync(http.RequestAborted);
        http.Response.Headers.ETag = catalog.ETag;
        http.Response.Headers.CacheControl = CatalogCacheControl;
        return Matches(http.Request.Headers.IfNoneMatch, catalog.ETag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(catalog.Bytes, GmHttp.JsonContentType);
    }

    /// <summary><c>If-None-Match</c>: a comma list of tags, weak ones compared by their opaque value, <c>*</c> matching any.</summary>
    internal static bool Matches(StringValues ifNoneMatch, string etag)
    {
        foreach (var header in ifNoneMatch)
        {
            foreach (var part in (header ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = part.StartsWith(WeakPrefix, StringComparison.Ordinal) ? part[WeakPrefix.Length..] : part;
                if (tag == "*" || string.Equals(tag, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
