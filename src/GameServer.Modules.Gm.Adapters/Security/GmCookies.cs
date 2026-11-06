using GameServer.Platform;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// Cookie policy of the GM API (SEC-04, slice 018): the one place that names the GM session and CSRF cookies of a deployment
/// (FR-017) and writes their attributes. The console is a static site on another origin of the same site as the API (case A),
/// so the cookies stay <c>SameSite=Lax</c> unless <c>Gm:Cookies:SameSite=None</c>; CORS no longer changes them.
/// <list type="bullet">
/// <item>Outside Development: <c>__Host-gs_gm</c> / <c>__Host-gs_gm_csrf</c>, <c>Path=/</c>, <c>Secure</c>, no <c>Domain</c>
/// (the prefix makes the browser refuse a cookie tossed by a sibling host of the site); antiforgery policy <c>Always</c>.</item>
/// <item>Development: <c>gs_gm_dev</c> / <c>gs_gm_dev_csrf</c>, <c>Path=/</c>; the session cookie is <c>Secure</c> on HTTPS or
/// with <c>SameSite=None</c>, the antiforgery policy <c>SameAsRequest</c> (<c>Always</c> with <c>None</c>). A <c>__Host-</c>
/// cookie must be <c>Secure</c> and ASP.NET Core antiforgery throws on plain HTTP with policy <c>Always</c>, while run-local
/// (also <c>-Lan</c>) and the in-process test clients use plain HTTP (research R1).</item>
/// </list>
/// The names differ from the pre-018 <c>gs_gm</c> / <c>gs_gm_csrf</c> (<c>Path=/gm</c>): a leftover cookie is never read and
/// never sent ahead of the new one (FR-015). All cookies are <c>HttpOnly</c>; writes and deletions use the same attributes.
/// </summary>
public static class GmCookies
{
    public const string HostSessionName = "__Host-gs_gm";
    public const string HostCsrfName = "__Host-gs_gm_csrf";
    public const string DevelopmentSessionName = "gs_gm_dev";
    public const string DevelopmentCsrfName = "gs_gm_dev_csrf";

    /// <summary>Session cookie name of this deployment: the <c>__Host-</c> name outside Development.</summary>
    public static string SessionName(GameServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsDevelopment ? DevelopmentSessionName : HostSessionName;
    }

    /// <summary>Antiforgery cookie name of this deployment: the <c>__Host-</c> name outside Development.</summary>
    public static string CsrfName(GameServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsDevelopment ? DevelopmentCsrfName : HostCsrfName;
    }

    /// <summary>
    /// Options for writing (<paramref name="expiresAt"/> set) or deleting (null) the session cookie: <c>Path=/</c>,
    /// <c>HttpOnly</c>, <c>SameSite</c> from <c>Gm:Cookies</c>, <c>Secure</c> outside Development, on HTTPS or with
    /// <c>SameSite=None</c> (FR-012, FR-013).
    /// </summary>
    public static CookieOptions Session(HttpContext http, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var cookies = services.GetRequiredService<IOptions<GmOptions>>().Value.Cookies;
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteOf(cookies),
            // Behind a TLS-terminating proxy IsHttps is false: only Development may drop Secure, and never with SameSite=None.
            Secure = !services.GetRequiredService<GameServerOptions>().IsDevelopment || http.Request.IsHttps || cookies.IsNone,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true,
        };
    }

    /// <summary>Antiforgery cookie with the same policy; cookie schemes and trusted origins for the CSRF check.</summary>
    public static void Configure(IServiceCollection services)
    {
        services.AddOptions<AntiforgeryOptions>().Configure<IOptions<GmOptions>, GameServerOptions>((antiforgery, gm, gameServer) =>
        {
            var cookies = gm.Value.Cookies;
            antiforgery.HeaderName = CsrfOptions.HeaderName;
            antiforgery.Cookie.Name = CsrfName(gameServer);
            antiforgery.Cookie.Path = "/";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteOf(cookies);
            antiforgery.Cookie.SecurePolicy = !gameServer.IsDevelopment || cookies.IsNone ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.AddOptions<CsrfOptions>().Configure<IOptions<GmOptions>>((csrf, gm) =>
        {
            csrf.CookieSchemes.Add(GmCookieAuthenticationHandler.SchemeName);
            csrf.TrustedOrigins.AddRange(gm.Value.Cors.AllowedOrigins);
        });
    }

    private static SameSiteMode SameSiteOf(GmCookieOptions cookies) => cookies.IsNone ? SameSiteMode.None : SameSiteMode.Lax;
}
