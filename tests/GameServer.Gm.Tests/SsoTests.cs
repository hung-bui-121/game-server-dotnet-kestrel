using System.Net;
using GameServer.Gm.Tests.Infrastructure;
using GameServer.Modules.Gm;
using GameServer.Platform;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>US1: SSO with explicit authorization, one-time email invitation, MFA evidence (SEC-03, ACC-10).</summary>
public sealed class SsoTests : IAsyncLifetime
{
    /// <summary>The host without console (slice 018 FR-025): Development defaults, the GM page exists.</summary>
    private const string LegacyDefault = "/gm/v1/ui";
    private const string LegacyDenied = "/gm/v1/ui?error=sso_denied";

    private const string ConsoleOrigin = "https://gm.example.com";
    private const string ConsoleRoot = "https://gm.example.com/";
    private const string ConsoleDenied = "https://gm.example.com/#/login?error=sso_denied";

    private readonly FakeOidcProvider _idp = new();
    private TestHost _host = null!;

    private void Settings(Dictionary<string, string?> values)
    {
        values["Gm:Oidc:Authority"] = FakeOidcProvider.Issuer;
        values["Gm:Oidc:ClientId"] = FakeOidcProvider.ClientId;
        values["Gm:Oidc:ClientSecret"] = FakeOidcProvider.ClientSecret;
        values["Gm:Oidc:RequireMfa"] = "true";
    }

    public async ValueTask InitializeAsync() => _host = await StartAsync();

    private Task<TestHost> StartAsync(Action<Dictionary<string, string?>>? more = null) =>
        GmTestHost.StartAsync(
            values =>
            {
                Settings(values);
                more?.Invoke(values);
            },
            services: s => s.PostConfigure<GmOptions>(o => o.Oidc.BackchannelHandler = _idp));

    /// <summary>A deployment with a console (slice 018): its origin and its URL.</summary>
    private Task<TestHost> StartWithConsoleAsync() => StartAsync(values => GmTestHost.WithConsole(values, ConsoleOrigin));

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _idp.Dispose();
    }

    private static Dictionary<string, object> Claims(string subject, string? email = null, bool verified = true, string[]? amr = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["amr"] = amr ?? ["pwd", "mfa"] };
        if (email is not null)
        {
            claims["email"] = email;
            claims["email_verified"] = verified;
        }

        return claims;
    }

    /// <summary>Drives the browser: start → provider sign-in → callback. Returns the final redirect and the client.</summary>
    private async Task<(HttpClient Client, string Location)> SignInAsync(Dictionary<string, object> claims, string? returnUrl = "/gm/v1/me", string? tamperedState = null)
    {
        var (client, callback) = await CallbackAsync(_host, claims, returnUrl, tamperedState);
        return (client, callback.Headers.Location!.ToString());
    }

    /// <summary>The callback answer itself (status, Location, Set-Cookie) on <paramref name="host"/>; no <c>returnUrl</c> when null.</summary>
    private async Task<(HttpClient Client, HttpResponseMessage Callback)> CallbackAsync(
        TestHost host, Dictionary<string, object> claims, string? returnUrl, string? tamperedState = null)
    {
        var browser = host.Browser();
        var start = await browser.GetAsync(returnUrl is null ? GmSso.StartRoute : $"{GmSso.StartRoute}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        start.Headers.Location!.ToString().ShouldStartWith(FakeOidcProvider.Issuer + "/authorize");

        var callback = _idp.Authorize(start.Headers.Location!, claims, tamperedState);
        var result = await browser.GetAsync(callback);
        result.StatusCode.ShouldBe(HttpStatusCode.Redirect, await result.Content.ReadAsStringAsync());
        return (browser, result);
    }

    private Task CreateStaffAsync(object body) => CreateStaffAsync(_host, body);

    private static async Task CreateStaffAsync(TestHost host, object body)
    {
        var admin = await host.GmClientAsync("admin");
        await admin.PostCommandAsync(StaffEndpoints.ListRoute, body).ShouldBeAsync(201);
    }

    [Fact]
    public async Task A_staff_bound_to_the_identity_gets_a_session_with_its_grant()
    {
        await CreateStaffAsync(new { staffId = "sso-support", issuer = FakeOidcProvider.Issuer, subject = "sub-1", roles = new[] { "Support" }, reason = "sso" });

        var (client, location) = await SignInAsync(Claims("sub-1"));
        location.ShouldBe("/gm/v1/me");
        await client.RefreshCsrfAsync();
        var me = await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
        me["staffId"]!.GetValue<string>().ShouldBe("sso-support");
        me["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["gm.access", "player.read"]);
        _host.Logs.ShouldNotContain(l => l.Contains("sub-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Company_identities_without_a_grant_are_refused()
    {
        var (client, location) = await SignInAsync(Claims("stranger", "stranger@corp.test"));
        location.ShouldBe(LegacyDenied);
        await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        _host.Logs.ShouldContain(l => l.Contains("GM SSO sign-in denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invitation_binds_the_first_verified_identity_once()
    {
        await CreateStaffAsync(new { staffId = "invitee", email = "invitee@corp.test", roles = new[] { "Support" }, reason = "invite" });

        (await SignInAsync(Claims("sub-unverified", "invitee@corp.test", verified: false))).Location.ShouldBe(LegacyDenied);

        var (client, location) = await SignInAsync(Claims("sub-invitee", "INVITEE@corp.test"));
        location.ShouldBe("/gm/v1/me");
        await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        // From now on only the bound identity matches: the email no longer grants anything.
        (await SignInAsync(Claims("sub-other", "invitee@corp.test"))).Location.ShouldBe(LegacyDenied);
        (await SignInAsync(Claims("sub-invitee", "changed@corp.test"))).Location.ShouldBe("/gm/v1/me");

        var admin = await _host.GmClientAsync("admin");
        var view = await admin.GetAsync("/gm/v1/staff/invitee").ShouldBeAsync(200);
        view["status"]!.GetValue<string>().ShouldBe(StaffStatus.Active);
        view["oidcBound"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_mfa_evidence_is_refused()
    {
        await CreateStaffAsync(new { staffId = "nomfa", issuer = FakeOidcProvider.Issuer, subject = "sub-nomfa", roles = new[] { "Support" }, reason = "sso" });
        (await SignInAsync(Claims("sub-nomfa", amr: ["pwd"]))).Location.ShouldBe(LegacyDenied);
        (await SignInAsync(Claims("sub-nomfa", amr: ["pwd", "otp"]))).Location.ShouldBe("/gm/v1/me");
    }

    [Fact]
    public async Task Disabled_staff_cannot_sign_in()
    {
        await CreateStaffAsync(new { staffId = "gone", issuer = FakeOidcProvider.Issuer, subject = "sub-gone", roles = new[] { "Support" }, reason = "sso" });
        var admin = await _host.GmClientAsync("admin");
        await admin.PostCommandAsync("/gm/v1/staff/gone/disable", new { reason = "left" }).ShouldBeAsync(200);
        (await SignInAsync(Claims("sub-gone"))).Location.ShouldBe(LegacyDenied);
    }

    [Fact]
    public async Task A_forged_state_or_an_external_return_url_gets_nowhere()
    {
        await CreateStaffAsync(new { staffId = "redir", issuer = FakeOidcProvider.Issuer, subject = "sub-redir", roles = new[] { "Support" }, reason = "sso" });

        var (forged, location) = await SignInAsync(Claims("sub-redir"), tamperedState: "forged-state");
        location.ShouldBe(LegacyDenied);
        await forged.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);

        // Without a console (slice 018 FR-025): only /gm/ paths; anything else lands on the Development page.
        (await SignInAsync(Claims("sub-redir"), returnUrl: "https://evil.example/steal")).Location.ShouldBe(LegacyDefault);
        (await SignInAsync(Claims("sub-redir"), returnUrl: "//evil.example")).Location.ShouldBe(LegacyDefault);
        (await SignInAsync(Claims("sub-redir"), returnUrl: "/gm/v1/é")).Location.ShouldBe(LegacyDefault);
        (await SignInAsync(Claims("sub-redir"), returnUrl: "/#/players/abc")).Location.ShouldBe(LegacyDefault);

        // Review T024 M1: browsers resolve %2e%2e as "..", so /gm/%2e%2e/x would land on /x.
        (await SignInAsync(Claims("sub-redir"), returnUrl: "/gm/%2e%2e/x")).Location.ShouldBe(LegacyDefault);
        (await SignInAsync(Claims("sub-redir"), returnUrl: "/gm/.%2E/x")).Location.ShouldBe(LegacyDefault);
        (await SignInAsync(Claims("sub-redir"), returnUrl: null)).Location.ShouldBe(LegacyDefault);
        _idp.RejectedTokenRequests.ShouldBe(0);
    }

    /// <summary>
    /// Slice 018 US3-1 (FR-022, FR-023): with a console the sign-in returns to the console path the console asked for, and the
    /// session cookie is set on that callback answer.
    /// </summary>
    [Fact]
    public async Task With_a_console_the_sign_in_returns_to_the_console_path()
    {
        await using var host = await StartWithConsoleAsync();
        await CreateStaffAsync(host, new { staffId = "console", issuer = FakeOidcProvider.Issuer, subject = "sub-console", roles = new[] { "Support" }, reason = "sso" });

        var (client, callback) = await CallbackAsync(host, Claims("sub-console"), "/#/players/abc");
        callback.Headers.Location!.ToString().ShouldBe("https://gm.example.com/#/players/abc");
        GmTestHost.SessionCookie(callback).ShouldNotBeNull();
        (await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(200))["staffId"]!.GetValue<string>().ShouldBe("console");

        (await CallbackAsync(host, Claims("sub-console"), null)).Callback.Headers.Location!.ToString().ShouldBe(ConsoleRoot);
        _idp.RejectedTokenRequests.ShouldBe(0);
    }

    /// <summary>Slice 018 US3-2 (SC-003): no hostile return value is followed; each lands on the console root.</summary>
    [Fact]
    public async Task With_a_console_hostile_return_values_land_on_the_console_root()
    {
        await using var host = await StartWithConsoleAsync();
        await CreateStaffAsync(host, new { staffId = "console", issuer = FakeOidcProvider.Issuer, subject = "sub-console", roles = new[] { "Support" }, reason = "sso" });

        foreach (var hostile in new[]
        {
            "https://evil.example/steal", "//evil.example", "/\\evil.example", "/a/../b", "/é", "/" + new string('a', 512), "javascript:alert(1)",
            "/#/x y",
        })
        {
            var (_, callback) = await CallbackAsync(host, Claims("sub-console"), hostile);
            callback.Headers.Location!.ToString().ShouldBe(ConsoleRoot, hostile);
        }

        _idp.RejectedTokenRequests.ShouldBe(0);
    }

    /// <summary>Slice 018 US3-3 (FR-024): every denial lands on the console's login route, without a reason and without a session.</summary>
    [Fact]
    public async Task With_a_console_every_denial_lands_on_the_console_login()
    {
        await using var host = await StartWithConsoleAsync();
        await CreateStaffAsync(host, new { staffId = "nomfa", issuer = FakeOidcProvider.Issuer, subject = "sub-nomfa", roles = new[] { "Support" }, reason = "sso" });
        await CreateStaffAsync(host, new { staffId = "gone", issuer = FakeOidcProvider.Issuer, subject = "sub-gone", roles = new[] { "Support" }, reason = "sso" });
        var admin = await host.GmClientAsync("admin");
        await admin.PostCommandAsync("/gm/v1/staff/gone/disable", new { reason = "left" }).ShouldBeAsync(200);

        var denials = new (string Case, Dictionary<string, object> Claims, string? State)[]
        {
            ("no MFA", Claims("sub-nomfa", amr: ["pwd"]), null),
            ("disabled staff", Claims("sub-gone"), null),
            ("unknown identity", Claims("stranger", "stranger@corp.test"), null),
            ("forged state", Claims("sub-nomfa"), "forged-state"),
        };
        foreach (var (name, claims, state) in denials)
        {
            var (client, callback) = await CallbackAsync(host, claims, "/#/players/abc", state);
            callback.Headers.Location!.ToString().ShouldBe(ConsoleDenied, name);
            GmTestHost.SessionCookie(callback).ShouldBeNull(name);
            await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        }

        _idp.RejectedTokenRequests.ShouldBe(0);
    }

    /// <summary>
    /// Review T024 M1: a console under a sub-path never leaves it. Percent-encoded dots (<c>%2e%2e</c>, which browsers resolve as
    /// <c>..</c>), other path segments and a query land on the console root; the hash route is kept under the sub-path.
    /// </summary>
    [Fact]
    public async Task A_sub_path_console_is_never_left_through_the_return_url()
    {
        const string SubPathConsole = "https://gm.example.com/console/";
        await using var host = await StartAsync(values =>
        {
            values["Gm:Cors:AllowedOrigins:0"] = ConsoleOrigin;
            values["Gm:Console:BaseUrl"] = SubPathConsole;
        });
        await CreateStaffAsync(host, new { staffId = "console", issuer = FakeOidcProvider.Issuer, subject = "sub-console", roles = new[] { "Support" }, reason = "sso" });

        (await CallbackAsync(host, Claims("sub-console"), "/#/players/abc")).Callback.Headers.Location!.ToString()
            .ShouldBe("https://gm.example.com/console/#/players/abc");
        foreach (var hostile in new[] { "/%2e%2e/other/", "/.%2e/other/", "/%2E./other/", "/other/", "/?x=1" })
        {
            (await CallbackAsync(host, Claims("sub-console"), hostile)).Callback.Headers.Location!.ToString().ShouldBe(SubPathConsole, hostile);
        }

        _idp.RejectedTokenRequests.ShouldBe(0);
    }

    /// <summary>FR-024: a callback the OIDC handler does not take (no state at all) is a denial too.</summary>
    [Fact]
    public async Task An_unhandled_callback_is_a_denial()
    {
        (await _host.Client().GetAsync(GmSso.CallbackRoute)).Headers.Location!.ToString().ShouldBe(LegacyDenied);

        await using var host = await StartWithConsoleAsync();
        var callback = await host.Client().GetAsync(GmSso.CallbackRoute);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().ShouldBe(ConsoleDenied);
    }

    [Fact]
    public async Task Bootstrap_managers_are_invited_when_the_deployment_has_none()
    {
        await using var host = await SampleTestHost.StartAsync(
            b => GameHttp.FullGame(b.AddModule<GmModule>()),
            values =>
            {
                Settings(values);
                values["Gm:Oidc:BootstrapManagers:0:StaffId"] = "boss";
                values["Gm:Oidc:BootstrapManagers:0:Email"] = "boss@corp.test";
            });
        var boss = host.Services.GetRequiredService<GameServer.Platform.Persistence.IKeyValueStore>();
        var staff = StaffDirectory.Parse(await boss.GetAsync(StaffDirectory.Key("boss"), CancellationToken.None));
        staff.ShouldNotBeNull(string.Join(" | ", host.Logs.Where(l => l.Contains("staff", StringComparison.OrdinalIgnoreCase)).TakeLast(15)));
        staff.Status.ShouldBe(StaffStatus.Invited);
        staff.EffectivePermissions("test", "test").ShouldContain(GmPermissions.StaffManage);
        (await host.Services.GetRequiredService<StaffAdminService>().CountManagersAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public void Outside_development_sso_settings_are_mandatory()
    {
        var production = new GameServerOptions { DeploymentEnvironment = "Production" };
        new GmOptionsValidator(production).Validate(null, new GmOptions()).Failures!.ShouldContain(f => f.Contains("Gm:Oidc:Authority", StringComparison.Ordinal));

        var configured = new GmOptions();
        configured.Oidc.Authority = "https://login.corp.test";
        configured.Oidc.ClientId = "gm";
        configured.Oidc.ClientSecret = "from-secret-provider";
        new GmOptionsValidator(production).Validate(null, configured).Succeeded.ShouldBeTrue();

        configured.Oidc.Authority = "http://login.corp.test";
        new GmOptionsValidator(production).Validate(null, configured).Failed.ShouldBeTrue();
    }
}
