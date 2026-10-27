using GameServer.Platform;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// Cookie policy of the GM API (SEC-04): <c>HttpOnly</c>, <c>Path=/gm</c>, <c>SameSite=Lax</c> (same-origin, TBD-05a);
/// <c>SameSite=None; Secure</c> only when a cross-origin frontend is configured (SEC-06). <c>Secure</c> is dropped only
/// on plain-HTTP Development.
/// </summary>
public static class GmCookies
{
    public const string CsrfCookieName = "gs_gm_csrf";

    public static CookieOptions Session(HttpContext http, DateTimeOffset? expiresAt)
    {
        var services = http.RequestServices;
        var crossOrigin = services.GetRequiredService<IOptions<GmOptions>>().Value.Cors.Enabled;
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = crossOrigin ? SameSiteMode.None : SameSiteMode.Lax,
            // Behind a TLS-terminating proxy IsHttps is false: only plain-HTTP Development may drop Secure.
            Secure = crossOrigin || http.Request.IsHttps || !services.GetRequiredService<GameServerOptions>().IsDevelopment,
            Path = "/gm",
            Expires = expiresAt,
            IsEssential = true,
        };
    }

    /// <summary>Antiforgery cookie with the same policy; cookie schemes and trusted origins for the CSRF check.</summary>
    public static void Configure(IServiceCollection services)
    {
        services.AddOptions<AntiforgeryOptions>().Configure<IOptions<GmOptions>, GameServerOptions>((antiforgery, gm, gameServer) =>
        {
            var crossOrigin = gm.Value.Cors.Enabled;
            antiforgery.HeaderName = CsrfOptions.HeaderName;
            antiforgery.Cookie.Name = CsrfCookieName;
            antiforgery.Cookie.Path = "/gm";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = crossOrigin ? SameSiteMode.None : SameSiteMode.Lax;
            antiforgery.Cookie.SecurePolicy = crossOrigin || !gameServer.IsDevelopment ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.AddOptions<CsrfOptions>().Configure<IOptions<GmOptions>>((csrf, gm) =>
        {
            csrf.CookieSchemes.Add(GmCookieAuthenticationHandler.SchemeName);
            csrf.TrustedOrigins.AddRange(gm.Value.Cors.AllowedOrigins);
        });
    }
}
