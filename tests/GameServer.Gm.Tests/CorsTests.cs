using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Tests.Shared;
using Microsoft.Extensions.Options;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>SEC-06 / ACC-11: CORS off by default; with an allowlist only exact origins get credentials.</summary>
public sealed class CorsTests
{
    private const string Allowed = "https://gm.example.com";

    private static HttpRequestMessage Preflight(string origin, string route = "/gm/v1/staff")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, route);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-csrf-token,content-type");
        return request;
    }

    [Fact]
    public async Task Same_origin_by_default_sends_no_cors_headers()
    {
        await using var host = await GmTestHost.StartAsync();
        var response = await host.Client().SendAsync(Preflight(Allowed));
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    /// <summary>
    /// Slice 018 FR-010 (BC-3): a console on another origin of the same site keeps <c>SameSite=Lax</c> cookies; only
    /// <c>Gm:Cookies:SameSite=None</c> makes them <c>None</c>, and then always <c>Secure</c>.
    /// </summary>
    [Fact]
    public async Task Configured_origins_get_credentialed_cors_and_cookies_stay_lax()
    {
        await using var host = await GmTestHost.StartAsync(s => GmTestHost.WithConsole(s, Allowed));
        var client = host.Client();

        var allowed = await client.SendAsync(Preflight(Allowed));
        allowed.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(Allowed);
        allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");

        var evil = await client.SendAsync(Preflight("https://evil.example"));
        evil.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        var login = await host.Browser(https: true).PostLoginAsync("econ");
        GmTestHost.CookieAttributes(GmTestHost.SessionCookie(login)!).ShouldSatisfyAllConditions(
            c => c.ShouldContain("samesite=lax"),
            c => c.ShouldContain("secure"));

        await using var none = await GmTestHost.StartAsync(s =>
        {
            GmTestHost.WithConsole(s, Allowed);
            s["Gm:Cookies:SameSite"] = "None";
        });
        var crossSite = await none.Browser(https: true).PostLoginAsync("econ");
        GmTestHost.CookieAttributes(GmTestHost.SessionCookie(crossSite)!).ShouldSatisfyAllConditions(
            c => c.ShouldContain("samesite=none"),
            c => c.ShouldContain("secure"));
    }

    /// <summary>Slice 018 FR-001: preflights are cached 7200 s; answers expose the headers a console reads.</summary>
    [Fact]
    public async Task Preflights_are_cached_two_hours_and_answers_expose_the_console_headers()
    {
        await using var host = await GmTestHost.StartAsync(s => GmTestHost.WithConsole(s, Allowed));
        var client = host.Client();

        var preflight = await client.SendAsync(Preflight(Allowed));
        preflight.Headers.GetValues("Access-Control-Max-Age").ShouldHaveSingleItem().ShouldBe("7200");
        preflight.Headers.GetValues("Access-Control-Allow-Methods").ShouldHaveSingleItem().ShouldBe("GET,POST,PUT,DELETE");
        preflight.Headers.GetValues("Access-Control-Allow-Headers").ShouldHaveSingleItem().ShouldBe("Content-Type,X-CSRF-Token,X-Request-Id");

        var actual = new HttpRequestMessage(HttpMethod.Get, GmModule.AuthMethodsRoute);
        actual.Headers.Add("Origin", Allowed);
        var answer = await client.SendAsync(actual);
        answer.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        answer.Headers.GetValues("Access-Control-Expose-Headers").ShouldHaveSingleItem().ShouldBe("X-Request-Id,Retry-After,ETag");
    }

    [Fact]
    public async Task Cors_does_not_replace_the_csrf_check()
    {
        await using var host = await GmTestHost.StartAsync(s => GmTestHost.WithConsole(s, Allowed));
        var options = host.Services.GetService(typeof(IOptions<GmOptions>)) as IOptions<GmOptions>;
        options!.Value.Cors.Enabled.ShouldBeTrue();
        var player = await host.NewPlayerAsync();
        var gm = host.GmClient("tester", GmPermissions.Access, "resource.adjust");
        var route = EconomyModule.AdjustRoute.Replace("{playerId}", player.PlayerId, StringComparison.Ordinal);

        // A header-authenticated client is not a browser cookie: CORS alone decides nothing about authorization.
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { resource = "gold", delta = 1, reason = "cors" }),
        };
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        request.Headers.Add("Origin", Allowed);
        await gm.SendAsync(request).ShouldBeAsync(200);
    }

    [Fact]
    public async Task The_configured_cross_site_frontend_can_send_cookie_mutations_with_its_token()
    {
        await using var host = await GmTestHost.StartAsync(s => GmTestHost.WithConsole(s, Allowed));
        var gm = host.Browser(https: true);
        await gm.PostLoginAsync("support");
        await gm.RefreshCsrfAsync();

        HttpRequestMessage Revoke(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/gm/v1/staff/support/sessions/revoke")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { reason = "cross-site frontend" }),
            };
            request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            return request;
        }

        (await gm.SendAsync(Revoke("https://evil.example")).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("FORBIDDEN");
        await gm.SendAsync(Revoke(Allowed)).ShouldBeAsync(200);
    }

    /// <summary>
    /// Slice 018 FR-004: a sibling host of the same site (<c>Sec-Fetch-Site: same-site</c>, another origin) holding a valid token is
    /// still refused: the CSRF check compares the Origin with the API's own origin and the configured console origin.
    /// </summary>
    [Fact]
    public async Task A_sibling_origin_of_the_same_site_cannot_send_cookie_mutations()
    {
        await using var host = await GmTestHost.StartAsync(s => GmTestHost.WithConsole(s, Allowed));
        var gm = host.Browser(https: true);
        (await gm.PostLoginAsync("support")).EnsureSuccessStatusCode();
        await gm.RefreshCsrfAsync();

        HttpRequestMessage Revoke(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/gm/v1/staff/support/sessions/revoke")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { reason = "sibling host" }),
            };
            request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Sec-Fetch-Site", "same-site");
            return request;
        }

        var sibling = await gm.SendAsync(Revoke("https://evil.example.com"));
        ((int)sibling.StatusCode).ShouldBe(403);
        (await sibling.JsonAsync())["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");
        sibling.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        var console = await gm.SendAsync(Revoke(Allowed));
        ((int)console.StatusCode).ShouldBe(200, await console.Content.ReadAsStringAsync());
        console.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(Allowed);
        console.Headers.GetValues("Access-Control-Allow-Credentials").ShouldHaveSingleItem().ShouldBe("true");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://gm.example.com/console")]
    [InlineData("https://gm.example.com/")]
    [InlineData("gm.example.com")]
    [InlineData("https://gm.example.com:443")]
    public async Task Bad_origins_fail_startup(string origin)
    {
        var ex = await Should.ThrowAsync<Exception>(() => GmTestHost.StartAsync(s =>
        {
            s["Gm:Cors:AllowedOrigins:0"] = origin;
            s["Gm:Console:BaseUrl"] = Allowed + "/";
        }));

        // The line of the origin itself (since slice 018 other lines name Gm:Cors:AllowedOrigins too).
        ex.ToString().ShouldContain($"Gm:Cors:AllowedOrigins '{origin}'");
    }

    /// <summary>Slice 018 FR-003: the one-origin rule applies outside Development only (a developer may run two consoles).</summary>
    [Fact]
    public async Task A_development_host_accepts_two_console_origins()
    {
        await using var host = await GmTestHost.StartAsync(s =>
        {
            GmTestHost.WithConsole(s, Allowed);
            s["Gm:Cors:AllowedOrigins:1"] = "http://localhost:5173";
        });
        foreach (var origin in new[] { Allowed, "http://localhost:5173" })
        {
            var preflight = await host.Client().SendAsync(Preflight(origin));
            preflight.Headers.GetValues("Access-Control-Allow-Origin").ShouldHaveSingleItem().ShouldBe(origin);
        }
    }

    [Fact]
    public void Plain_http_origins_are_refused_outside_development()
    {
        var errors = GmCorsValidation.Validate(
            new GmCorsOptions { AllowedOrigins = ["http://gm.example.com"] },
            new GameServer.Platform.GameServerOptions { DeploymentEnvironment = "Production" }).ToArray();
        errors.ShouldHaveSingleItem().ShouldContain("not https");
    }
}
