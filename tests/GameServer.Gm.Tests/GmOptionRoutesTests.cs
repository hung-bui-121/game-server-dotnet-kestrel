using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Platform.Registry;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 017 T041 (FR-043, US4-5, US4-6, A9): the two framework option routes answer <c>GmOptionList</c> to staff with the
/// permission of the operation they serve; ban scopes are exactly the set the ban command accepts.
/// </summary>
public sealed class GmOptionRoutesTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await GmTestHost.StartAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Resources_are_the_catalog_sorted_by_id_with_display_names()
    {
        var catalog = _host.Services.GetRequiredService<ResourceCatalog>();
        var support = await _host.GmClientAsync("support");

        var body = await support.GetAsync(EconomyRoutes.ResourceOptionsTemplate).ShouldBeAsync(200);

        body.AsObject().Select(m => m.Key).ShouldBe(["items"]);
        var items = body["items"]!.AsArray().Select(i => (i!["value"]!.GetValue<string>(), i["label"]!.GetValue<string>())).ToArray();
        items.ShouldNotBeEmpty();
        items.ShouldBe(catalog.All.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => (r.Id, r.DisplayName)).ToArray());
        // Review r1 m2: gm.access is enough (resource names are not sensitive); without it 403.
        await _host.GmClient("access-only", GmPermissions.Access).GetAsync(EconomyRoutes.ResourceOptionsTemplate).ShouldBeAsync(200);
        await _host.GmClient("no-access", "player.read").GetAsync(EconomyRoutes.ResourceOptionsTemplate).ShouldBeAsync(403);
    }

    [Fact]
    public async Task Ban_scopes_are_login_and_every_command_group_for_staff_who_may_ban()
    {
        var expected = _host.Services.GetRequiredService<ModuleRegistry>().Commands.Keys
            .Select(operation => operation.Split('.', 2)[0])
            .Append("login")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var admin = await _host.GmClientAsync("admin");

        var body = await admin.GetAsync(IdentityModule.BanScopesRoute).ShouldBeAsync(200);

        var items = body["items"]!.AsArray().Select(i => (i!["value"]!.GetValue<string>(), i["label"]!.GetValue<string>())).ToArray();
        items.ShouldBe(expected.Select(s => (s, s)).ToArray());
        await (await _host.GmClientAsync("support")).GetAsync(IdentityModule.BanScopesRoute).ShouldBeAsync(403);
    }

    [Fact]
    public async Task Every_listed_scope_is_accepted_by_the_ban_and_an_unlisted_one_refused()
    {
        var admin = await _host.GmClientAsync("admin");
        var player = await _host.NewPlayerAsync();
        var ban = IdentityModule.BanRoute.Replace("{playerId}", player.PlayerId, StringComparison.Ordinal);
        var unban = IdentityModule.UnbanRoute.Replace("{playerId}", player.PlayerId, StringComparison.Ordinal);
        var scopes = (await admin.GetAsync(IdentityModule.BanScopesRoute).ShouldBeAsync(200))["items"]!.AsArray()
            .Select(i => i!["value"]!.GetValue<string>())
            .ToArray();

        foreach (var scope in scopes)
        {
            await admin.PostCommandAsync(ban, new { reason = "scope " + scope, scope = new[] { scope } }).ShouldBeAsync(200);
            await admin.PostCommandAsync(unban, new { reason = "lift " + scope }).ShouldBeAsync(200);
        }

        await admin.PostCommandAsync(ban, new { reason = "typo", scope = new[] { "nosuchgroup" } }).ShouldBeAsync(400);
    }
}
