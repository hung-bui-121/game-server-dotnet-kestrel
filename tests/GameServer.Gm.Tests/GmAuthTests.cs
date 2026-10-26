using System.Net.Http.Json;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Platform;
using GameServer.Platform.Web.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>GM authentication, permission and scope (US1: SEC-02, SEC-07, SEC-15, ACC-09, ACC-10).</summary>
public sealed class GmAuthTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private string _playerId = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GmTestHost.StartAsync();

        // A player with data so lookups have something to return.
        var player = await _host.NewPlayerAsync();
        _playerId = player.PlayerId;
        await player.CheatAsync("gold", 10);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static string Adjust(string playerId) => EconomyModule.AdjustRoute.Replace("{playerId}", playerId, StringComparison.Ordinal);

    [Fact]
    public async Task Login_sets_a_restricted_cookie_and_me_reflects_the_grant()
    {
        var response = await _host.PostLoginAsync("admin");
        response.EnsureSuccessStatusCode();
        var cookie = GmTestHost.SessionCookie(response)!.ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("samesite=lax");
        cookie.ShouldContain("path=/gm");

        var gm = await _host.GmClientAsync("admin");
        var me = await gm.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
        me["staffId"]!.GetValue<string>().ShouldBe("admin");
        me["displayName"]!.GetValue<string>().ShouldBe("Staff admin");
        me["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(
            ["counter.read", "gm.access", "ops.read", "player.read", "player.read.pii", "progression.edit", "resource.adjust"]);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var gm = await _host.GmClientAsync("support");
        (await gm.GetAsync(GmModule.MeRoute)).Headers.CacheControl!.NoStore.ShouldBeTrue();
        await gm.PostAsync(GmModule.LogoutRoute, null).ShouldBeAsync(204);
        (await gm.GetAsync(GmModule.MeRoute).ShouldBeAsync(401))["code"]!.GetValue<string>().ShouldBe("UNAUTHENTICATED");
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("nobody", GmTestHost.Password)]
    [InlineData("disabled", GmTestHost.Password)]
    public async Task Bad_credentials_are_401_without_a_cookie(string staffId, string password)
    {
        var response = await _host.PostLoginAsync(staffId, password);
        ((int)response.StatusCode).ShouldBe(401);
        GmTestHost.SessionCookie(response).ShouldBeNull();
    }

    [Fact]
    public async Task Role_templates_expand_to_permissions_checked_per_endpoint()
    {
        var support = await _host.GmClientAsync("support");
        await support.GetAsync($"/gm/v1/players/{_playerId}").ShouldBeAsync(200);
        (await support.PostCommandAsync(Adjust(_playerId), new { resource = "gold", delta = 1, reason = "test" }).ShouldBeAsync(403))
            ["code"]!.GetValue<string>().ShouldBe("FORBIDDEN");

        var econ = await _host.GmClientAsync("econ");
        await econ.PostCommandAsync(Adjust(_playerId), new { resource = "gold", delta = 1, reason = "test" }).ShouldBeAsync(200);
        await econ.PostCommandAsync($"/gm/v1/players/{_playerId}/progression", new { stage = 2, reason = "test" }).ShouldBeAsync(403);
    }

    [Theory]
    [InlineData("other-game")]
    [InlineData("other-env")]
    public async Task Grants_for_another_game_or_environment_give_no_permission(string staffId)
    {
        var gm = await _host.GmClientAsync(staffId);
        await gm.GetAsync(GmModule.MeRoute).ShouldBeAsync(403);
        await gm.GetAsync($"/gm/v1/players/{_playerId}").ShouldBeAsync(403);
        await gm.PostCommandAsync(Adjust(_playerId), new { resource = "gold", delta = 1, reason = "test" }).ShouldBeAsync(403);
    }

    [Fact]
    public async Task Disabled_staff_loses_access_within_the_cache_window()
    {
        var gm = await _host.GmClientAsync("support");
        var directory = _host.Services.GetRequiredService<StaffDirectory>();
        var record = (await directory.GetAsync("support", CancellationToken.None))!;
        await directory.SaveAsync(record with { Disabled = true }, CancellationToken.None);

        _host.Clock.Advance(TimeSpan.FromSeconds(61));
        await gm.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
    }

    [Fact]
    public async Task Session_expires_after_eight_hours()
    {
        var gm = await _host.GmClientAsync("support");
        _host.Clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1));
        await gm.GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
    }

    [Fact]
    public async Task Player_and_gm_credentials_do_not_cross()
    {
        var gm = await _host.GmClientAsync("admin");
        await gm.PostAsync("/api/v1/player/snapshot", new ByteArrayContent([])).ShouldBeAsync(401);

        var login = await _host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        await _host.BearerClient(login.SessionToken).GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
        await _host.Player("p1").GetAsync(GmModule.MeRoute).ShouldBeAsync(401);
    }

    [Fact]
    public async Task Cross_site_mutations_are_refused()
    {
        var gm = await _host.GmClientAsync("econ");
        var request = new HttpRequestMessage(HttpMethod.Post, Adjust(_playerId))
        {
            Content = JsonContent.Create(new { resource = "gold", delta = 1, reason = "test" }),
        };
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        (await gm.SendAsync(request).ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("FORBIDDEN");

        var read = new HttpRequestMessage(HttpMethod.Get, $"/gm/v1/players/{_playerId}");
        read.Headers.Add("Sec-Fetch-Site", "cross-site");
        await gm.SendAsync(read).ShouldBeAsync(200);
    }

    [Fact]
    public async Task Ui_page_is_served_with_a_strict_policy()
    {
        var response = await _host.Client().GetAsync(GmModule.UiRoute);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        (await response.Content.ReadAsStringAsync()).ShouldContain("GM Console");
    }

    [Fact]
    public async Task Gm_endpoint_with_a_permission_outside_the_catalog_fails_startup()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(() => TestHost.StartAsync(
            b => GameHttp.FullGame(b.AddModule<GmModule>()),
            beforeUseGameServer: app => app.MapGet("/gm/v1/rogue", () => Results.Ok()).AsGmEndpoint("rogue.permission")));
        ex.Message.ShouldContain("rogue.permission");
    }
}

/// <summary>Pure rules: role templates, password hashing, bootstrap validation.</summary>
public sealed class GmRulesTests
{
    private static readonly string[] Catalog = ["gm.access", "ops.read", "player.read", "player.read.pii", "progression.edit", "resource.adjust"];

    [Fact]
    public void Role_templates_expand_within_the_catalog()
    {
        GmRoles.Expand(GmRoles.Support, Catalog).ShouldBe(["gm.access", "player.read"]);
        GmRoles.Expand(GmRoles.Operator, Catalog).ShouldBe(["gm.access", "player.read", "progression.edit"]);
        GmRoles.Expand(GmRoles.EconomyOperator, Catalog).ShouldBe(["gm.access", "player.read", "resource.adjust"]);
        GmRoles.Expand(GmRoles.Administrator, Catalog).ShouldBe(Catalog);

        // A template never grants a permission whose module is not registered.
        GmRoles.Expand(GmRoles.Operator, ["gm.access", "player.read"]).ShouldBe(["gm.access", "player.read"]);
        Should.Throw<ArgumentException>(() => GmRoles.Expand("Root", Catalog));
    }

    [Fact]
    public void Dev_password_hash_verifies_only_the_right_password()
    {
        var hash = DevPasswordHasher.Hash("s3cret");
        hash.ShouldStartWith("pbkdf2-sha256$100000$");
        hash.ShouldNotContain("s3cret");
        DevPasswordHasher.Verify("s3cret", hash).ShouldBeTrue();
        DevPasswordHasher.Verify("s3cret!", hash).ShouldBeFalse();
        DevPasswordHasher.Verify("s3cret", null).ShouldBeFalse();
        DevPasswordHasher.Verify("s3cret", "pbkdf2-sha256$1$AAAA$AAAA").ShouldBeFalse();
    }

    [Fact]
    public void Staff_permissions_only_come_from_the_matching_scope()
    {
        var staff = new StaffRecord
        {
            StaffId = "s",
            Grants =
            [
                new StaffGrant { GameId = "g", EnvironmentId = "dev", Permissions = ["player.read"] },
                new StaffGrant { GameId = "g", EnvironmentId = "prod", Permissions = ["resource.adjust"] },
            ],
        };
        staff.EffectivePermissions("g", "dev").ShouldBe(["player.read"]);
        staff.EffectivePermissions("other", "dev").ShouldBeEmpty();
    }

    [Fact]
    public void Bootstrap_staff_is_refused_outside_development()
    {
        var options = new GmOptions();
        options.Bootstrap.Staff.Add(new GmBootstrapStaff { StaffId = "admin", Password = "x", Roles = ["Administrator"] });

        new GmOptionsValidator(new GameServerOptions { DeploymentEnvironment = "Production" }).Validate(null, options).Failed.ShouldBeTrue();
        new GmOptionsValidator(new GameServerOptions { DeploymentEnvironment = "Development" }).Validate(null, options).Succeeded.ShouldBeTrue();

        options.Bootstrap.Staff[0].Roles = ["Root"];
        new GmOptionsValidator(new GameServerOptions { DeploymentEnvironment = "Development" }).Validate(null, options).Failed.ShouldBeTrue();
    }
}
