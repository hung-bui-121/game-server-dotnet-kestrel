using GameServer.Platform;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// CORS for the GM console (SEC-06, slice 018 case A): the console is a static site on another origin of the same site as the
/// API (<c>https://gm.&lt;env&gt;.&lt;domain&gt;</c> calling <c>https://gm-api.&lt;env&gt;.&lt;domain&gt;</c>). Off when no origin is
/// configured (no console). Configured origins match exactly (one per environment outside Development); credentials are
/// allowed only for them; answers expose <see cref="ExposedHeaders"/> and preflights are cached <see cref="PreflightMaxAge"/>.
/// The platform applies the policy to every <c>/gm/**</c> answer, errors included (FR-002). CORS is not authentication: cookie
/// mutations still need the CSRF token and an allowed Origin.
/// </summary>
public static class GmCors
{
    /// <summary>Answer headers a console reads (FR-001): the request id of every error, the retry delay, the catalog ETag.</summary>
    public static IReadOnlyList<string> ExposedHeaders { get; } = ["X-Request-Id", "Retry-After", "ETag"];

    /// <summary>Preflight cache (FR-001): 7200 s, the most Chromium honours.</summary>
    public static TimeSpan PreflightMaxAge { get; } = TimeSpan.FromSeconds(7200);

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
                    .WithExposedHeaders([.. ExposedHeaders])
                    .SetPreflightMaxAge(PreflightMaxAge));
            }
        });
    }
}

public static class GmCorsValidation
{
    public static IEnumerable<string> Validate(GmCorsOptions options, GameServerOptions gameServer)
    {
        if (!gameServer.IsDevelopment && options.AllowedOrigins.Count > 1)
        {
            // One console per environment (design §6.3, FR-003); Development may run several.
            yield return $"Gm:Cors:AllowedOrigins lists {options.AllowedOrigins.Count} origins in '{gameServer.DeploymentEnvironment}'; " +
                "list the one console origin of this environment.";
        }

        foreach (var origin in options.AllowedOrigins)
        {
            if (origin.Contains('*', StringComparison.Ordinal))
            {
                yield return $"Gm:Cors:AllowedOrigins '{origin}' uses a wildcard; list exact origins (SEC-06).";
                continue;
            }

            // Exactly the serialized origin a browser sends (review T024 m1): CORS and the CSRF Origin check compare strings, so an
            // explicit default port, user info, a path, query, fragment or trailing slash would never match.
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.UserInfo.Length > 0
                || !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                yield return $"Gm:Cors:AllowedOrigins '{origin}' must be an origin (scheme://host[:port]) without default port, user info, path, " +
                    "query or trailing slash.";
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
