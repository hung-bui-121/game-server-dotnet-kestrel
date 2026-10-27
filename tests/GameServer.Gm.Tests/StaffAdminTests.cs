using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameServer.Modules.Gm;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>US5: staff management with one audit per change, no privilege escalation, no lock-out (SEC-14, SEC-15).</summary>
public sealed class StaffAdminTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GmTestHost.StartAsync();
        _admin = await _host.GmClientAsync("admin");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private IReadOnlyList<JsonObject> StaffAudit(string staffId) =>
        _host.Store.All(GmModule.AuditCollection).Select(d => d.Content)
            .Where(d => d["kind"]?.GetValue<string>() == StaffAdminService.AuditKind && d["targetStaffId"]?.GetValue<string>() == staffId)
            .ToArray();

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object body, string? requestId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Request-Id", requestId ?? Guid.NewGuid().ToString("D"));
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Creating_an_invited_staff_expands_the_role_and_writes_one_complete_audit()
    {
        var created = await _admin.PostCommandAsync(StaffEndpoints.ListRoute, new
        {
            staffId = "newbie",
            displayName = "New Bie",
            email = "Newbie@Example.com",
            roles = new[] { "Support" },
            reason = "joined support",
        }).ShouldBeAsync(201);
        created["status"]!.GetValue<string>().ShouldBe(StaffStatus.Invited);
        created["email"]!.GetValue<string>().ShouldBe("newbie@example.com");
        created["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["gm.access", "player.read"]);
        created.ToJsonString().ShouldNotContain("passwordHash");

        var audit = StaffAudit("newbie").ShouldHaveSingleItem();
        var payload = audit["payload"]!;
        audit["actorId"]!.GetValue<string>().ShouldBe("admin");
        payload["action"]!.GetValue<string>().ShouldBe("staff.create");
        payload["permission"]!.GetValue<string>().ShouldBe(GmPermissions.StaffManage);
        payload["reason"]!.GetValue<string>().ShouldBe("joined support");
        payload["after"]!["permissions"]!.AsArray().Count.ShouldBe(2);
        payload["correlationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        audit["committedAtUtc"]!.GetValue<string>().ShouldEndWith("Z");

        var listed = await _admin.GetAsync($"{StaffEndpoints.ListRoute}/newbie/audit?pageSize=10").ShouldBeAsync(200);
        listed["items"]!.AsArray().Count.ShouldBe(1);
        var all = await _admin.GetAsync($"{StaffEndpoints.ListRoute}?pageSize=50").ShouldBeAsync(200);
        all.ToJsonString().Contains("\"staffId\":\"newbie\"", StringComparison.Ordinal).ShouldBeTrue(all.ToJsonString());
    }

    [Fact]
    public async Task A_manager_cannot_grant_permissions_they_do_not_hold()
    {
        var lead = _host.GmClient("lead", GmPermissions.Access, GmPermissions.StaffManage, GmPermissions.PlayerRead);
        (await lead.PostCommandAsync(StaffEndpoints.ListRoute, new { staffId = "econ2", email = "e2@example.com", roles = new[] { "EconomyOperator" }, reason = "x" })
            .ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("PERMISSION_NOT_GRANTABLE");
        await lead.PostCommandAsync(StaffEndpoints.ListRoute, new { staffId = "sup2", email = "s2@example.com", roles = new[] { "Support" }, reason = "x" })
            .ShouldBeAsync(201);
    }

    [Fact]
    public async Task The_last_staff_manager_cannot_be_disabled_or_downgraded()
    {
        (await _admin.PostCommandAsync("/gm/v1/staff/admin/disable", new { reason = "oops" }).ShouldBeAsync(409))
            ["code"]!.GetValue<string>().ShouldBe("LAST_STAFF_MANAGER");
        (await PutAsync(_admin, "/gm/v1/staff/admin/grant", new { roles = new[] { "Support" }, reason = "oops" }).ShouldBeAsync(409))
            ["code"]!.GetValue<string>().ShouldBe("LAST_STAFF_MANAGER");

        // An unused invitation is not a manager: it cannot sign in, so it does not keep the deployment manageable.
        await _admin.PostCommandAsync(StaffEndpoints.ListRoute, new { staffId = "invited-admin", email = "a2@example.com", roles = new[] { "Administrator" }, reason = "backup" })
            .ShouldBeAsync(201);
        await PutAsync(_admin, "/gm/v1/staff/admin/grant", new { roles = new[] { "Support" }, reason = "oops" }).ShouldBeAsync(409);

        // With a second manager who can sign in, the first can step down.
        await _admin.PostCommandAsync(StaffEndpoints.ListRoute, new { staffId = "admin2", issuer = "https://idp.test", subject = "admin2", roles = new[] { "Administrator" }, reason = "backup" })
            .ShouldBeAsync(201);
        await PutAsync(_admin, "/gm/v1/staff/admin/grant", new { roles = new[] { "Support" }, reason = "step down" }).ShouldBeAsync(200);
    }

    [Fact]
    public async Task A_manager_cannot_downgrade_disable_or_revoke_someone_more_privileged()
    {
        var lead = _host.GmClient("lead", GmPermissions.Access, GmPermissions.StaffManage, GmPermissions.PlayerRead);
        foreach (var call in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => PutAsync(lead, "/gm/v1/staff/admin/grant", new { roles = new[] { "Support" }, reason = "x" }),
                     () => lead.PostCommandAsync("/gm/v1/staff/admin/disable", new { reason = "x" }),
                     () => lead.PostCommandAsync("/gm/v1/staff/admin/sessions/revoke", new { reason = "x" }),
                     () => lead.PostCommandAsync("/gm/v1/staff/econ/disable", new { reason = "x" }),
                 })
        {
            (await call().ShouldBeAsync(403))["code"]!.GetValue<string>().ShouldBe("PERMISSION_NOT_GRANTABLE");
        }

        // Someone with a subset of the lead's permissions can be managed.
        await lead.PostCommandAsync("/gm/v1/staff/support/disable", new { reason = "x" }).ShouldBeAsync(200);
    }

    [Fact]
    public async Task An_unchanged_seed_does_not_rewrite_staff_on_restart()
    {
        await using var second = await GmTestHost.StartAsync(store: _host.Store, clock: _host.Clock);
        StaffAudit("support").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Read_only_staff_can_list_but_not_change()
    {
        var reader = _host.GmClient("auditor", GmPermissions.Access, GmPermissions.StaffRead);
        await reader.GetAsync($"{StaffEndpoints.ListRoute}?pageSize=10").ShouldBeAsync(200);
        await reader.GetAsync($"{StaffEndpoints.ListRoute}/support").ShouldBeAsync(200);
        await reader.PostCommandAsync("/gm/v1/staff/support/disable", new { reason = "no" }).ShouldBeAsync(403);
        await reader.PostCommandAsync("/gm/v1/staff/support/sessions/revoke", new { reason = "no" }).ShouldBeAsync(403);
        await reader.PostCommandAsync("/gm/v1/staff/auditor/sessions/revoke", new { reason = "mine" }).ShouldBeAsync(404);
    }

    [Theory]
    [InlineData("Root", null)]
    [InlineData(null, "rogue.permission")]
    public async Task Unknown_roles_or_permissions_are_400(string? role, string? permission)
    {
        await _admin.PostCommandAsync(StaffEndpoints.ListRoute, new
        {
            staffId = "bad",
            email = "bad@example.com",
            roles = role is null ? Array.Empty<string>() : [role],
            permissions = permission is null ? Array.Empty<string>() : [permission],
            reason = "x",
        }).ShouldBeAsync(400);
    }

    [Fact]
    public async Task Retries_replay_and_a_reused_request_id_with_another_body_is_409()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var body = new { staffId = "twice", email = "twice@example.com", roles = new[] { "Support" }, reason = "x" };
        await _admin.PostCommandAsync(StaffEndpoints.ListRoute, body, requestId).ShouldBeAsync(201);
        (await _admin.PostCommandAsync(StaffEndpoints.ListRoute, body, requestId).ShouldBeAsync(200))["replayed"]!.GetValue<bool>().ShouldBeTrue();
        StaffAudit("twice").Count.ShouldBe(1);

        await _admin.PostCommandAsync(StaffEndpoints.ListRoute, body with { email = "other@example.com" }, requestId).ShouldBeAsync(409);
    }

    [Fact]
    public async Task An_email_can_be_invited_once()
    {
        await _admin.PostCommandAsync(StaffEndpoints.ListRoute, new { staffId = "one", email = "same@example.com", roles = new[] { "Support" }, reason = "x" }).ShouldBeAsync(201);
        (await _admin.PostCommandAsync(StaffEndpoints.ListRoute, new { staffId = "two", email = "SAME@example.com", roles = new[] { "Support" }, reason = "x" })
            .ShouldBeAsync(409))["code"]!.GetValue<string>().ShouldBe("STAFF_EXISTS");
    }

    [Fact]
    public async Task A_change_without_reason_or_request_id_is_400()
    {
        await _admin.PostCommandAsync("/gm/v1/staff/support/disable", new { reason = "" }).ShouldBeAsync(400);
        await _admin.PostAsJsonAsync("/gm/v1/staff/support/disable", new { reason = "x" }).ShouldBeAsync(400);
    }
}
