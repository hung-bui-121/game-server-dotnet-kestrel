using System.Net;
using System.Net.Http.Json;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Platform.Web.Security;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>US2: CSRF on every cookie mutation, POST-only logout, protected cookie + server-side ticket (SEC-04, SEC-05, ACC-11).</summary>
public sealed class CsrfAndSessionTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private string _playerId = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GmTestHost.StartAsync();
        var player = await _host.NewPlayerAsync();
        _playerId = player.PlayerId;
        await player.CheatAsync("gold", 10);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private string Adjust => EconomyModule.AdjustRoute.Replace("{playerId}", _playerId, StringComparison.Ordinal);

    private static object AdjustBody => new { resource = "gold", delta = 1, reason = "csrf test" };

    [Fact]
    public async Task Cookie_mutation_without_or_with_a_wrong_token_is_refused()
    {
        var gm = await _host.GmClientAsync("econ");
        gm.DefaultRequestHeaders.Remove(CsrfOptions.HeaderName);
        (await gm.PostCommandAsync(Adjust, AdjustBody).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");

        gm.DefaultRequestHeaders.Add(CsrfOptions.HeaderName, "not-a-token");
        (await gm.PostCommandAsync(Adjust, AdjustBody).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");

        await gm.RefreshCsrfAsync();
        await gm.PostCommandAsync(Adjust, AdjustBody).ShouldBeAsync(200);
    }

    [Fact]
    public async Task A_token_is_bound_to_its_session()
    {
        var support = await _host.GmClientAsync("support");
        var econ = await _host.GmClientAsync("econ");
        var foreign = support.DefaultRequestHeaders.GetValues(CsrfOptions.HeaderName).Single();
        econ.DefaultRequestHeaders.Remove(CsrfOptions.HeaderName);
        econ.DefaultRequestHeaders.Add(CsrfOptions.HeaderName, foreign);
        (await econ.PostCommandAsync(Adjust, AdjustBody).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");
    }

    [Fact]
    public async Task Cross_site_or_foreign_origin_mutations_are_refused_even_with_a_valid_token()
    {
        var gm = await _host.GmClientAsync("econ");

        var crossSite = new HttpRequestMessage(HttpMethod.Post, Adjust) { Content = JsonContent.Create(AdjustBody) };
        crossSite.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        await gm.SendAsync(crossSite).ShouldBeAsync(403);

        var foreign = new HttpRequestMessage(HttpMethod.Post, Adjust) { Content = JsonContent.Create(AdjustBody) };
        foreign.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        foreign.Headers.Add("Origin", "https://evil.example");
        (await gm.SendAsync(foreign).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");

        var sameOrigin = new HttpRequestMessage(HttpMethod.Post, Adjust) { Content = JsonContent.Create(AdjustBody) };
        sameOrigin.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        sameOrigin.Headers.Add("Origin", "http://localhost");
        await gm.SendAsync(sameOrigin).ShouldBeAsync(200);
    }

    [Fact]
    public async Task Local_login_needs_a_token_too()
    {
        var browser = _host.Browser();
        (await browser.PostAsJsonAsync(GmModule.LoginRoute, new { staffId = "admin", password = GmTestHost.Password }).ShouldBeAsync(403))
            ["code"]!.GetValue<string>().ShouldBe("CSRF_FAILED");
    }

    [Fact]
    public async Task Logout_is_post_only_and_a_copied_cookie_dies_with_the_ticket()
    {
        var jar = new CookieContainer();
        var gm = await _host.GmClientAsync("support", jar);
        var session = jar.GetCookies(new Uri("http://localhost/gm/"))[GmCookieAuthenticationHandler.CookieName]!.Value;

        (await gm.GetAsync(GmModule.LogoutRoute)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        await gm.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        await gm.PostAsync(GmModule.LogoutRoute, null).ShouldBeAsync(204);

        // An attacker kept a copy of the cookie: the server-side ticket is gone, so it is useless.
        var copy = _host.Client();
        copy.DefaultRequestHeaders.Add("Cookie", $"{GmCookieAuthenticationHandler.CookieName}={session}");
        await copy.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
    }

    [Fact]
    public async Task Forged_or_tampered_cookies_are_rejected()
    {
        var jar = new CookieContainer();
        await _host.GmClientAsync("support", jar);
        var session = jar.GetCookies(new Uri("http://localhost/gm/"))[GmCookieAuthenticationHandler.CookieName]!.Value;
        var tampered = session[..^2] + (session[^2] == 'A' ? "B" : "A") + session[^1];

        foreach (var value in new[] { tampered, "forged-session-id", new string('x', 600) })
        {
            var client = _host.Client();
            client.DefaultRequestHeaders.Add("Cookie", $"{GmCookieAuthenticationHandler.CookieName}={value}");
            await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        }
    }

    [Fact]
    public async Task Idle_sessions_end_and_activity_keeps_them_alive_until_the_absolute_expiry()
    {
        var active = await _host.GmClientAsync("support");
        var idle = await _host.GmClientAsync("econ");
        for (var i = 0; i < 9; i++)
        {
            _host.Clock.Advance(TimeSpan.FromMinutes(50));
            await active.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
        }

        await idle.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        _host.Clock.Advance(TimeSpan.FromMinutes(50));
        await active.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
    }

    [Fact]
    public async Task Cookies_are_http_only_lax_and_scoped_to_gm()
    {
        var browser = _host.Browser();
        var csrf = await browser.GetAsync(GmModule.CsrfRoute);
        csrf.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(GmCookies.CsrfCookieName, StringComparison.Ordinal)).ToLowerInvariant()
            .ShouldSatisfyAllConditions(c => c.ShouldContain("httponly"), c => c.ShouldContain("path=/gm"), c => c.ShouldContain("samesite=lax"));

        var login = await browser.PostLoginAsync("admin");
        var cookie = GmTestHost.SessionCookie(login)!;
        cookie.ToLowerInvariant().ShouldSatisfyAllConditions(
            c => c.ShouldContain("httponly"),
            c => c.ShouldContain("samesite=lax"),
            c => c.ShouldContain("path=/gm"));

        // Only a protected id: the stored ticket key (hash of the id) is not derivable from it.
        var value = cookie.Split(';')[0].Split('=', 2)[1];
        _host.Store.All(GmSessionService.Collection).ShouldAllBe(s => !value.Contains(s.Key.Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Header_authenticated_test_clients_need_no_token()
    {
        await _host.GmClient("tester", GmPermissions.Access, "resource.adjust").PostCommandAsync(Adjust, AdjustBody).ShouldBeAsync(200);
    }
}
