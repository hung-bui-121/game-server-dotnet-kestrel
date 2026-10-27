using GameServer.Platform;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// CORS for a GM frontend on another origin (SEC-06): off by default (same-origin, TBD-05a). When origins are
/// configured they must match exactly; credentials are allowed only for them. CORS is not authentication: cookie
/// mutations still need the CSRF token and an allowed Origin.
/// </summary>
public static class GmCors
{
    /// <summary>Registers the platform's GM policy (applied to <c>/gm/**</c>) when origins are configured.</summary>
    public static void Add(IServiceCollection services)
    {
        services.AddOptions<CorsOptions>().Configure<IOptions<GmOptions>>((cors, gm) =>
        {
            var options = gm.Value.Cors;
            if (options.Enabled)
            {
                cors.AddPolicy(GmCorsPolicy.Name, policy => policy
                    .WithOrigins([.. options.AllowedOrigins])
                    .AllowCredentials()
                    .WithMethods("GET", "POST", "PUT", "DELETE")
                    .WithHeaders("Content-Type", CsrfOptions.HeaderName, "X-Request-Id")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
            }
        });
    }
}

public static class GmCorsValidation
{
    public static IEnumerable<string> Validate(GmCorsOptions options, GameServerOptions gameServer)
    {
        foreach (var origin in options.AllowedOrigins)
        {
            if (origin.Contains('*', StringComparison.Ordinal))
            {
                yield return $"Gm:Cors:AllowedOrigins '{origin}' uses a wildcard; list exact origins (SEC-06).";
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.PathAndQuery != "/"
                || origin.EndsWith('/')
                || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                yield return $"Gm:Cors:AllowedOrigins '{origin}' must be an origin (scheme://host[:port]) without path, query or trailing slash.";
                continue;
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !gameServer.IsDevelopment)
            {
                yield return $"Gm:Cors:AllowedOrigins '{origin}' is not https; plain http origins are allowed only in Development.";
            }
        }
    }

    /// <summary>Exact origin comparison (scheme, host, port) used by the CSRF Origin check.</summary>
    public static bool IsAllowed(GmCorsOptions options, string origin) =>
        options.AllowedOrigins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
}
