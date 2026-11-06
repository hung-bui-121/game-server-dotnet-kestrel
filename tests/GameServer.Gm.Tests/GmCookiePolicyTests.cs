using GameServer.Modules.Gm;
using GameServer.Platform;
using GameServer.Platform.Web.Security;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 018 US2 (FR-012..FR-016, SC-002): names and attributes of every GM cookie write and deletion per environment, scheme and
/// <c>Gm:Cookies:SameSite</c>; a pre-018 cookie is ignored. Development cases run on the real pipeline; the <c>__Host-</c> names
/// of other environments run on a bare app with Staging options (architect A4: an in-memory Staging GM host cannot start).
/// </summary>
public sealed class GmCookiePolicyTests
{
    private const string Console = "https://gm.example.com";

    /// <summary>A far expiry for the bare app's session cookie (the value is not what these tests check).</summary>
    private static readonly DateTimeOffset FarExpiry = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string[] Attributes(HttpResponseMessage response, string name) =>
        GmTestHost.CookieAttributes(GmTestHost.SetCookie(response, name).ShouldNotBeNull($"Set-Cookie {name}"));

    [Fact]
    public async Task Development_over_http_uses_the_development_names_on_the_site_root_without_secure()
    {
        await using var host = await GmTestHost.StartAsync();
        var browser = host.Browser();

        var csrf = await browser.GetAsync(GmModule.CsrfRoute);
        Attributes(csrf, "gs_gm_dev_csrf").ShouldBe(["path=/", "samesite=lax", "httponly"]);

        var login = await browser.PostLoginAsync("admin");
        login.EnsureSuccessStatusCode();
        var session = Attributes(login, "gs_gm_dev");
        session.Where(a => !a.StartsWith("expires=", StringComparison.Ordinal)).ShouldBe(["path=/", "samesite=lax", "httponly"]);
        session.ShouldContain(a => a.StartsWith("expires=", StringComparison.Ordinal));

        await browser.RefreshCsrfAsync();
        var logout = await browser.PostAsync(GmModule.LogoutRoute, null);
        ((int)logout.StatusCode).ShouldBe(204);
        Attributes(logout, "gs_gm_dev").ShouldBe(["expires=thu, 01 jan 1970 00:00:00 gmt", "path=/", "samesite=lax", "httponly"]);
    }

    [Fact]
    public async Task Development_over_https_makes_the_session_cookie_secure()
    {
        await using var host = await GmTestHost.StartAsync();
        var login = await host.Browser(https: true).PostLoginAsync("admin");
        login.EnsureSuccessStatusCode();
        Attributes(login, "gs_gm_dev").ShouldContain("secure");
    }

    [Fact]
    public async Task Samesite_none_makes_both_cookies_none_and_secure()
    {
        await using var host = await GmTestHost.StartAsync(s =>
        {
            GmTestHost.WithConsole(s, Console);
            s["Gm:Cookies:SameSite"] = "None";
        });
        var browser = host.Browser(https: true);

        var csrf = await browser.GetAsync(GmModule.CsrfRoute);
        Attributes(csrf, "gs_gm_dev_csrf").ShouldBe(["path=/", "secure", "samesite=none", "httponly"]);

        var login = await browser.PostLoginAsync("admin");
        login.EnsureSuccessStatusCode();
        Attributes(login, "gs_gm_dev").Where(a => !a.StartsWith("expires=", StringComparison.Ordinal))
            .ShouldBe(["path=/", "secure", "samesite=none", "httponly"]);
    }

    /// <summary>FR-015: no fallback read of the pre-018 name; the same protected value under the new name proves it is live.</summary>
    [Fact]
    public async Task A_pre_018_cookie_of_a_live_session_is_ignored()
    {
        await using var host = await GmTestHost.StartAsync();
        var staff = await host.Services.GetRequiredService<StaffDirectory>().GetAsync("admin", CancellationToken.None);
        var (value, _) = await host.Services.GetRequiredService<GmSessionService>().CreateAsync(staff.ShouldNotBeNull(), CancellationToken.None);

        var old = host.Client();
        old.DefaultRequestHeaders.Add("Cookie", $"gs_gm={value}");
        (await old.GetAsync(GmModule.MeRoute).ShouldBeAsync(401))["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");

        var current = host.Client();
        current.DefaultRequestHeaders.Add("Cookie", $"{GmCookies.DevelopmentSessionName}={value}");
        await current.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
    }

    [Theory]
    [InlineData("Development", "gs_gm_dev", "gs_gm_dev_csrf")]
    [InlineData("Test", "__Host-gs_gm", "__Host-gs_gm_csrf")]
    [InlineData("Staging", "__Host-gs_gm", "__Host-gs_gm_csrf")]
    [InlineData("Production", "__Host-gs_gm", "__Host-gs_gm_csrf")]
    public void The_names_follow_the_deployment_environment(string environment, string session, string csrf)
    {
        var options = new GameServerOptions { DeploymentEnvironment = environment };
        GmCookies.SessionName(options).ShouldBe(session);
        GmCookies.CsrfName(options).ShouldBe(csrf);
    }

    /// <summary>
    /// Review T024 m3: every site that reads, writes or deletes the session cookie (authentication handler, sign-in ending the
    /// previous session, logout) uses the deployment's name. A <c>Test</c> deployment has development tools (local sign-in, the
    /// in-memory store) but not the Development names, so the whole flow runs on <c>__Host-gs_gm</c> over HTTPS.
    /// </summary>
    [Fact]
    public async Task Outside_development_the_session_cookie_is_read_ended_and_deleted_by_its_host_name()
    {
        await using var host = await GmTestHost.StartAsync(s => s["GameServer:DeploymentEnvironment"] = "Test");
        var jar = new System.Net.CookieContainer();
        var browser = host.Browser(jar, https: true);

        var login = await browser.PostLoginAsync("admin");
        login.EnsureSuccessStatusCode();
        Attributes(login, GmCookies.HostSessionName).Where(a => !a.StartsWith("expires=", StringComparison.Ordinal))
            .ShouldBe(["path=/", "secure", "samesite=lax", "httponly"]);
        GmTestHost.SetCookie(login, GmCookies.DevelopmentSessionName).ShouldBeNull();
        var first = jar.GetCookies(new Uri("https://localhost/"))[GmCookies.HostSessionName].ShouldNotBeNull().Value;

        // The authentication handler reads the __Host- cookie.
        await browser.RefreshCsrfAsync();
        await browser.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        // Signing in again ends the session the __Host- cookie names (GmSessionService.EndCurrentAsync).
        (await browser.PostLoginAsync("admin")).EnsureSuccessStatusCode();
        var copy = host.Browser(https: true);
        copy.DefaultRequestHeaders.Add("Cookie", $"{GmCookies.HostSessionName}={first}");
        await copy.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);

        // Logout deletes the __Host- cookie with the attributes of the write.
        await browser.RefreshCsrfAsync();
        var logout = await browser.PostAsync(GmModule.LogoutRoute, null);
        ((int)logout.StatusCode).ShouldBe(204);
        Attributes(logout, GmCookies.HostSessionName).ShouldBe(["expires=thu, 01 jan 1970 00:00:00 gmt", "path=/", "secure", "samesite=lax", "httponly"]);
    }

    /// <summary>
    /// FR-012 (V5): outside Development the cookies are <c>__Host-</c>, <c>Path=/</c>, <c>Secure</c>, without <c>Domain</c>; the
    /// antiforgery round trip works over HTTPS and the deletion carries the same attributes.
    /// </summary>
    [Fact]
    public async Task Outside_development_the_cookies_are_host_prefixed_and_round_trip_over_https()
    {
        await using var app = await StagingAppAsync();
        var browser = app.GetTestServer().Browser(https: true);

        var csrf = await browser.GetAsync("/csrf");
        csrf.EnsureSuccessStatusCode();
        Attributes(csrf, "__Host-gs_gm_csrf").ShouldBe(["path=/", "secure", "samesite=lax", "httponly"]);
        browser.DefaultRequestHeaders.Add(CsrfOptions.HeaderName, await csrf.Content.ReadAsStringAsync());
        (await browser.PostAsync("/check", null)).StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);

        var session = await browser.PostAsync("/session", null);
        Attributes(session, "__Host-gs_gm").Where(a => !a.StartsWith("expires=", StringComparison.Ordinal))
            .ShouldBe(["path=/", "secure", "samesite=lax", "httponly"]);

        var deletion = await browser.PostAsync("/session/delete", null);
        Attributes(deletion, "__Host-gs_gm").ShouldBe(["expires=thu, 01 jan 1970 00:00:00 gmt", "path=/", "secure", "samesite=lax", "httponly"]);
    }

    /// <summary>
    /// Research R1, pinned: with policy <c>Always</c> ASP.NET Core antiforgery refuses a plain-HTTP request, which is why
    /// Development keeps names without the <c>__Host-</c> prefix and policy <c>SameAsRequest</c>.
    /// </summary>
    [Fact]
    public async Task Outside_development_the_csrf_route_fails_on_plain_http()
    {
        await using var app = await StagingAppAsync();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => app.GetTestServer().Browser().GetAsync("/csrf"));
        ex.Message.ShouldContain("SecurePolicy = Always");
    }

    /// <summary>A bare app with Staging options and only the GM cookie configuration (architect A4).</summary>
    private static async Task<WebApplication> StagingAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Only what the test sets: no appsettings or environment variables of the machine or of a copied host.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        var gameServer = new GameServerOptions { DeploymentEnvironment = "Staging" };
        builder.Services.AddSingleton(gameServer);
        builder.Services.AddOptions<GmOptions>().BindConfiguration(GmOptions.SectionName);
        builder.Services.AddAntiforgery();
        GmCookies.Configure(builder.Services);

        var app = builder.Build();
        app.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(http).RequestToken);
        app.MapPost("/check", async (HttpContext http, IAntiforgery antiforgery) =>
            await antiforgery.IsRequestValidAsync(http) ? Results.NoContent() : Results.BadRequest());
        app.MapPost("/session", (HttpContext http) =>
        {
            http.Response.Cookies.Append(GmCookies.SessionName(gameServer), "session-value", GmCookies.Session(http, FarExpiry));
            return Results.NoContent();
        });
        app.MapPost("/session/delete", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(GmCookies.SessionName(gameServer), GmCookies.Session(http, null));
            return Results.NoContent();
        });
        await app.StartAsync();
        return app;
    }
}
