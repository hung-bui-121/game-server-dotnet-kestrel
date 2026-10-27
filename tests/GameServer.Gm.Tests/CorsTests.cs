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

    [Fact]
    public async Task Only_configured_origins_get_credentialed_cors_and_cookies_become_samesite_none()
    {
        await using var host = await GmTestHost.StartAsync(s => s["Gm:Cors:AllowedOrigins:0"] = Allowed);
        var client = host.Client();

        var allowed = await client.SendAsync(Preflight(Allowed));
        allowed.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(Allowed);
        allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");

        var evil = await client.SendAsync(Preflight("https://evil.example"));
        evil.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        // SameSite=None cookies are Secure: the cross-origin setup runs on HTTPS.
        var login = await host.Browser(https: true).PostLoginAsync("econ");
        GmTestHost.SessionCookie(login)!.ToLowerInvariant().ShouldSatisfyAllConditions(
            c => c.ShouldContain("samesite=none"),
            c => c.ShouldContain("secure"));
    }

    [Fact]
    public async Task Cors_does_not_replace_the_csrf_check()
    {
        await using var host = await GmTestHost.StartAsync(s => s["Gm:Cors:AllowedOrigins:0"] = Allowed);
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
        await using var host = await GmTestHost.StartAsync(s => s["Gm:Cors:AllowedOrigins:0"] = Allowed);
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

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://gm.example.com/console")]
    [InlineData("https://gm.example.com/")]
    [InlineData("gm.example.com")]
    public async Task Bad_origins_fail_startup(string origin)
    {
        var ex = await Should.ThrowAsync<Exception>(() => GmTestHost.StartAsync(s => s["Gm:Cors:AllowedOrigins:0"] = origin));
        ex.ToString().ShouldContain("Gm:Cors:AllowedOrigins");
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
