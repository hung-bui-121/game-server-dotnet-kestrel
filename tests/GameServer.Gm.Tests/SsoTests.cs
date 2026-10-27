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
    private readonly FakeOidcProvider _idp = new();
    private TestHost _host = null!;

    private void Settings(Dictionary<string, string?> values)
    {
        values["Gm:Oidc:Authority"] = FakeOidcProvider.Issuer;
        values["Gm:Oidc:ClientId"] = FakeOidcProvider.ClientId;
        values["Gm:Oidc:ClientSecret"] = FakeOidcProvider.ClientSecret;
        values["Gm:Oidc:RequireMfa"] = "true";
    }

    public async ValueTask InitializeAsync() =>
        _host = await GmTestHost.StartAsync(Settings, services: s => s.PostConfigure<GmOptions>(o => o.Oidc.BackchannelHandler = _idp));

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
    private async Task<(HttpClient Client, string Location)> SignInAsync(Dictionary<string, object> claims, string returnUrl = "/gm/v1/me", string? tamperedState = null)
    {
        var browser = _host.Browser();
        var start = await browser.GetAsync($"{GmSso.StartRoute}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        start.Headers.Location!.ToString().ShouldStartWith(FakeOidcProvider.Issuer + "/authorize");

        var callback = _idp.Authorize(start.Headers.Location!, claims, tamperedState);
        var result = await browser.GetAsync(callback);
        result.StatusCode.ShouldBe(HttpStatusCode.Redirect, await result.Content.ReadAsStringAsync());
        return (browser, result.Headers.Location!.ToString());
    }

    private async Task CreateStaffAsync(object body)
    {
        var admin = await _host.GmClientAsync("admin");
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
        location.ShouldBe(GmSso.DeniedRedirect);
        await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        _host.Logs.ShouldContain(l => l.Contains("GM SSO sign-in denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invitation_binds_the_first_verified_identity_once()
    {
        await CreateStaffAsync(new { staffId = "invitee", email = "invitee@corp.test", roles = new[] { "Support" }, reason = "invite" });

        (await SignInAsync(Claims("sub-unverified", "invitee@corp.test", verified: false))).Location.ShouldBe(GmSso.DeniedRedirect);

        var (client, location) = await SignInAsync(Claims("sub-invitee", "INVITEE@corp.test"));
        location.ShouldBe("/gm/v1/me");
        await client.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);

        // From now on only the bound identity matches: the email no longer grants anything.
        (await SignInAsync(Claims("sub-other", "invitee@corp.test"))).Location.ShouldBe(GmSso.DeniedRedirect);
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
        (await SignInAsync(Claims("sub-nomfa", amr: ["pwd"]))).Location.ShouldBe(GmSso.DeniedRedirect);
        (await SignInAsync(Claims("sub-nomfa", amr: ["pwd", "otp"]))).Location.ShouldBe("/gm/v1/me");
    }

    [Fact]
    public async Task Disabled_staff_cannot_sign_in()
    {
        await CreateStaffAsync(new { staffId = "gone", issuer = FakeOidcProvider.Issuer, subject = "sub-gone", roles = new[] { "Support" }, reason = "sso" });
        var admin = await _host.GmClientAsync("admin");
        await admin.PostCommandAsync("/gm/v1/staff/gone/disable", new { reason = "left" }).ShouldBeAsync(200);
        (await SignInAsync(Claims("sub-gone"))).Location.ShouldBe(GmSso.DeniedRedirect);
    }

    [Fact]
    public async Task A_forged_state_or_an_external_return_url_gets_nowhere()
    {
        await CreateStaffAsync(new { staffId = "redir", issuer = FakeOidcProvider.Issuer, subject = "sub-redir", roles = new[] { "Support" }, reason = "sso" });

        var (forged, location) = await SignInAsync(Claims("sub-redir"), tamperedState: "forged-state");
        location.ShouldBe(GmSso.DeniedRedirect);
        await forged.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);

        (await SignInAsync(Claims("sub-redir"), returnUrl: "https://evil.example/steal")).Location.ShouldBe(GmSso.DefaultReturnUrl);
        (await SignInAsync(Claims("sub-redir"), returnUrl: "//evil.example")).Location.ShouldBe(GmSso.DefaultReturnUrl);
        (await SignInAsync(Claims("sub-redir"), returnUrl: "/gm/v1/é")).Location.ShouldBe(GmSso.DefaultReturnUrl);
        _idp.RejectedTokenRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Bootstrap_managers_are_invited_when_the_deployment_has_none()
    {
        await using var host = await TestHost.StartAsync(
            b => GameHttp.FullGame(b.AddModule<GmModule>()),
            values =>
            {
                Settings(values);
                values["Gm:Oidc:BootstrapManagers:0:StaffId"] = "boss";
                values["Gm:Oidc:BootstrapManagers:0:Email"] = "boss@corp.test";

                // samples/SampleHost/appsettings.json seeds an Administrator: make it a non-manager for this case.
                values["Gm:Bootstrap:Staff:0:Roles:0"] = "Support";
            });
        var boss = host.Services.GetRequiredService<GameServer.Platform.Persistence.IKeyValueStore>();
        var staff = StaffDirectory.Parse(await boss.GetAsync(StaffDirectory.Key("boss"), CancellationToken.None));
        staff.ShouldNotBeNull(string.Join(" | ", host.Logs.Where(l => l.Contains("staff", StringComparison.OrdinalIgnoreCase)).TakeLast(15)));
        staff.Status.ShouldBe(StaffStatus.Invited);
        staff.EffectivePermissions("test", "dev").ShouldContain(GmPermissions.StaffManage);
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
