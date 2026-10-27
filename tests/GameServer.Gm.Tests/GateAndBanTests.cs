using System.Net;
using System.Net.Http.Json;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Middleware;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 008 on two instances sharing a store and clock: maintenance and minimum client version (GATE-01/02, ACC-30),
/// GM ban/unban (GM-07, BAN-01, ACC-31) and display names (PRF-01).
/// </summary>
public sealed class GateAndBanTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private InMemoryDocumentStore _store = null!;
    private TestHost _a = null!;
    private TestHost _b = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new InMemoryDocumentStore(_clock);
        _a = await GmTestHost.StartAsync(store: _store, clock: _clock);
        _b = await GmTestHost.StartAsync(store: _store, clock: _clock);
        _admin = await _a.GmClientAsync("admin");
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object body, string? requestId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Request-Id", requestId ?? Guid.NewGuid().ToString("D"));
        return client.SendAsync(request);
    }

    private static HttpClient Versioned(TestHost host, string platform, string? version)
    {
        var client = host.Client();
        client.DefaultRequestHeaders.Add(GateHeaders.Platform, platform);
        if (version is not null)
        {
            client.DefaultRequestHeaders.Add(GateHeaders.ClientVersion, version);
        }

        return client;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client) => client.PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());

    private int Audits(string operation) =>
        _store.All(GmModule.AuditCollection).Count(d => d.Content["payload"]?["action"]?.GetValue<string>() == operation);

    [Fact]
    public async Task Maintenance_blocks_the_player_api_before_authentication_within_10_seconds_while_gm_keeps_working()
    {
        // Instance B has a cached "open" gate.
        (await LoginAsync(_b.Client())).StatusCode.ShouldBe(HttpStatusCode.OK);

        var expectedEnd = _clock.UtcNow.AddMinutes(30);
        var view = await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = true, message = "Server upgrade", expectedEndUtc = expectedEnd, reason = "release 1.4" }).ShouldBeAsync(200);
        view["maintenance"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        var onA = await LoginAsync(_a.Client());
        onA.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = await onA.ReadErrorAsync();
        error.Code.ShouldBe("MAINTENANCE");
        error.Details["message"].ShouldBe("Server upgrade");
        error.Details.ShouldContainKey("expectedEndUtc");
        error.RetryAfterMs.ShouldBe((int)TimeSpan.FromMinutes(30).TotalMilliseconds);

        // No token at all: still the maintenance answer, not 401.
        (await (await _a.Client().PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest())).ReadErrorAsync()).Code.ShouldBe("MAINTENANCE");
        await _admin.GetAsync(GmModule.MeRoute).ShouldBeAsync(200);
        await _admin.GetAsync(GateEndpoints.GateRoute).ShouldBeAsync(200);

        (await LoginAsync(_b.Client())).StatusCode.ShouldBe(HttpStatusCode.OK);
        _clock.Advance(TimeSpan.FromSeconds(11));
        (await LoginAsync(_b.Client())).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = false, reason = "done" }).ShouldBeAsync(200);
        (await LoginAsync(_a.Client())).StatusCode.ShouldBe(HttpStatusCode.OK);
        Audits("ops.gate.maintenance").ShouldBe(2);
    }

    [Fact]
    public async Task Clients_below_the_platform_minimum_must_update_before_any_business()
    {
        await PutAsync(_admin, GateEndpoints.MinClientVersionRoute, new { platform = "Android", minVersion = "1.3.0", updateUrl = "https://play.example/app", reason = "breaking proto" })
            .ShouldBeAsync(200);

        var old = await LoginAsync(Versioned(_a, "android", "1.2.9"));
        old.StatusCode.ShouldBe((HttpStatusCode)426);
        var error = await old.ReadErrorAsync();
        error.Code.ShouldBe("UPDATE_REQUIRED");
        error.Details["updateUrl"].ShouldBe("https://play.example/app");
        error.Details["minClientVersion"].ShouldBe("1.3.0");

        (await LoginAsync(Versioned(_a, "android", "1.3"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Versioned(_a, "android", "1.10.0"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Versioned(_a, "ios", "1.0.0"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Versioned(_a, "android", null))).StatusCode.ShouldBe((HttpStatusCode)426);
        (await LoginAsync(Versioned(_a, "android", "1.3.0-beta"))).StatusCode.ShouldBe((HttpStatusCode)426);

        // Maintenance wins over the version check.
        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = true, reason = "both" }).ShouldBeAsync(200);
        (await LoginAsync(Versioned(_a, "android", "1.2.9"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // Removing the minimum.
        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = false, reason = "both" }).ShouldBeAsync(200);
        await PutAsync(_admin, GateEndpoints.MinClientVersionRoute, new { platform = "android", minVersion = (string?)null, reason = "rollback" }).ShouldBeAsync(200);
        (await LoginAsync(Versioned(_a, "android", "1.2.9"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gate_changes_are_validated_permissioned_and_idempotent()
    {
        var support = await _a.GmClientAsync("support");
        await PutAsync(support, GateEndpoints.MaintenanceRoute, new { enabled = true, reason = "x" }).ShouldBeAsync(403);
        await PutAsync(_admin, GateEndpoints.MinClientVersionRoute, new { platform = "Android!", minVersion = "1.0", reason = "x" }).ShouldBeAsync(400);
        await PutAsync(_admin, GateEndpoints.MinClientVersionRoute, new { platform = "android", minVersion = "1", reason = "x" }).ShouldBeAsync(400);
        await PutAsync(_admin, GateEndpoints.MinClientVersionRoute, new { platform = "android", minVersion = "1.0", updateUrl = "ftp://x", reason = "x" }).ShouldBeAsync(400);
        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = true, reason = "" }).ShouldBeAsync(400);

        var requestId = Guid.NewGuid().ToString("D");
        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = true, message = "first", reason = "noop" }, requestId).ShouldBeAsync(200);
        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = false, reason = "other" }).ShouldBeAsync(200);

        // A replay returns the original result, not the current gate.
        var replay = await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = true, message = "first", reason = "noop" }, requestId).ShouldBeAsync(200);
        replay["replayed"]!.GetValue<bool>().ShouldBeTrue();
        replay["maintenance"]!["message"]!.GetValue<string>().ShouldBe("first");

        // Same id with another reason, or on another gate endpoint: conflict.
        await PutAsync(_admin, GateEndpoints.MaintenanceRoute, new { enabled = true, message = "first", reason = "changed" }, requestId).ShouldBeAsync(409);
        await PutAsync(_admin, GateEndpoints.MinClientVersionRoute, new { platform = "ios", minVersion = "1.0", reason = "noop" }, requestId).ShouldBeAsync(409);
        Audits("ops.gate.maintenance").ShouldBe(2);
        _store.All(GmModule.AuditCollection).Select(d => d.Content["payload"]!)
            .Where(p => p["action"]?.GetValue<string>() == "ops.gate.maintenance")
            .ShouldAllBe(p => p["permission"]!.GetValue<string>() == GmPermissions.OpsMaintenance);
    }

    [Fact]
    public async Task A_login_ban_suspends_login_and_ends_sessions_and_unban_restores_access()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var login = await _a.Client().LoginAsync(localId, secret);
        var onB = _b.BearerClient(login.SessionToken);
        (await onB.PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);

        var until = _clock.UtcNow.AddDays(3);
        var banned = await _admin.PostCommandAsync($"/gm/v1/players/{login.PlayerId}/ban", new { reason = "cheating", untilUtc = until }).ShouldBeAsync(200);
        banned["code"]!.GetValue<string>().ShouldBe("OK");

        var refused = await _a.Client().PostLoginAsync(localId, secret);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var error = await refused.ReadErrorAsync();
        error.Code.ShouldBe("ACCOUNT_SUSPENDED");
        error.Details["until"].ShouldBe(until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));

        // The other instance's profile cache ends within 60 s; then the session is refused (suspended first).
        _clock.Advance(TimeSpan.FromSeconds(61));
        var stale = await onB.PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest());
        (await stale.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");

        await _admin.PostCommandAsync($"/gm/v1/players/{login.PlayerId}/unban", new { reason = "appeal accepted" }).ShouldBeAsync(200);
        _clock.Advance(TimeSpan.FromSeconds(61));
        (await (await onB.PostProtobufAsync("/api/v1/player/snapshot", new GameServer.Protocol.Core.V1.SnapshotRequest())).ReadErrorAsync()).Code.ShouldBe("SESSION_REPLACED");
        (await _b.Client().PostLoginAsync(localId, secret)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var audits = _store.All(GmModule.AuditCollection).Select(d => d.Content)
            .Where(d => d["payload"]?["action"]?.GetValue<string>() is "identity.ban" or "identity.unban").ToArray();
        audits.Length.ShouldBe(2);
        audits.ShouldAllBe(a => a["payload"]!["permission"]!.GetValue<string>() == IdentityPermissions.PlayerBan);
    }

    [Fact]
    public async Task A_command_group_ban_blocks_that_group_only_and_keeps_the_session()
    {
        var player = await _a.NewPlayerAsync();
        await player.CheatAsync("gold", 500);
        await _admin.PostCommandAsync($"/gm/v1/players/{player.PlayerId}/ban", new { reason = "shop abuse", scope = new[] { "shop" } }).ShouldBeAsync(200);
        _clock.Advance(TimeSpan.FromSeconds(61));

        var buy = await player.PostAsync(SampleGame.Adapters.SampleGameModule.BuyRoute, new SampleGame.Protocol.V1.BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 });
        (await buy.ReadErrorAsync()).Code.ShouldBe("ACCOUNT_SUSPENDED");
        (await player.StartBattleAsync(1)).Code.ShouldBe("OK");
    }

    [Fact]
    public async Task Bans_expire_by_themselves()
    {
        var localId = IdentityHttp.NewLocalId();
        var secret = IdentityHttp.NewSecret();
        var login = await _a.Client().LoginAsync(localId, secret);
        await _admin.PostCommandAsync($"/gm/v1/players/{login.PlayerId}/ban", new { reason = "cool down", untilUtc = _clock.UtcNow.AddHours(1) }).ShouldBeAsync(200);
        (await _a.Client().PostLoginAsync(localId, secret)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _clock.Advance(TimeSpan.FromHours(2));
        (await _a.Client().PostLoginAsync(localId, secret)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ban_needs_permission_an_existing_player_and_valid_input()
    {
        var player = await _a.NewPlayerAsync();
        var support = await _a.GmClientAsync("support");
        await support.PostCommandAsync($"/gm/v1/players/{player.PlayerId}/ban", new { reason = "x" }).ShouldBeAsync(403);
        await _admin.PostCommandAsync("/gm/v1/players/p_nobody/ban", new { reason = "x" }).ShouldBeAsync(404);
        await _admin.PostCommandAsync($"/gm/v1/players/{player.PlayerId}/ban", new { reason = "x", untilUtc = _clock.UtcNow.AddMinutes(-1) }).ShouldBeAsync(400);
        await _admin.PostCommandAsync($"/gm/v1/players/{player.PlayerId}/ban", new { reason = "x", scope = new[] { "Shop!" } }).ShouldBeAsync(400);
        await _admin.PostCommandAsync($"/gm/v1/players/{player.PlayerId}/ban", new { reason = "typo", scope = new[] { "shpo" } }).ShouldBeAsync(400);
        await _admin.PostCommandAsync($"/gm/v1/players/{player.PlayerId}/ban", new { reason = "empty", scope = Array.Empty<string>() }).ShouldBeAsync(400);
    }

    [Theory]
    [InlineData("Nguyễn Văn A", "OK")]
    [InlineData("player_01", "OK")]
    [InlineData("a", "DISPLAY_NAME_INVALID")]
    [InlineData("abcdefghijklmnopqrstu", "DISPLAY_NAME_INVALID")]
    [InlineData(" lead", "DISPLAY_NAME_INVALID")]
    [InlineData("two  spaces", "DISPLAY_NAME_INVALID")]
    [InlineData("smile😀", "DISPLAY_NAME_INVALID")]
    [InlineData("tab\tname", "DISPLAY_NAME_INVALID")]
    [InlineData("\u3164\u3164", "DISPLAY_NAME_INVALID")]
    [InlineData("\uFFA0\uFFA0", "DISPLAY_NAME_INVALID")]
    [InlineData("a\uFE0F\uFE0F\uFE0Fb", "DISPLAY_NAME_INVALID")]
    [InlineData("a\u034Fb", "DISPLAY_NAME_INVALID")]
    [InlineData("ab\u0301\u0301\u0301\u0301\u0301\u0301", "DISPLAY_NAME_INVALID")]
    [InlineData("\u0301abc", "DISPLAY_NAME_INVALID")]
    public async Task Display_names_are_validated_by_the_server(string name, string code)
    {
        var player = await _a.NewPlayerAsync();
        var result = await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = name });
        result.Code.ShouldBe(code);
        if (code == "OK")
        {
            var profile = result.Updates.ShouldHaveSingleItem();
            GameServer.Modules.Identity.Protocol.V1.ProfileState.Parser.ParseFrom(profile.Payload).DisplayName.ShouldBe(name);
        }
        else
        {
            result.Updates.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_game_policy_can_reject_names()
    {
        await using var host = await GmTestHost.StartAsync(services: s => s.AddSingleton<IDisplayNamePolicy>(new BlockList("badword")));
        var player = await host.NewPlayerAsync();
        (await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = "mybadword" })).Code.ShouldBe("DISPLAY_NAME_REJECTED");

        // Fullwidth look-alikes are folded before the policy sees them.
        (await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = "ｂａｄｗｏｒｄ" })).Code.ShouldBe("DISPLAY_NAME_REJECTED");
        (await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = "nice" })).Code.ShouldBe("OK");
    }

    [Fact]
    public async Task Names_are_stored_in_one_normal_form()
    {
        var player = await _a.NewPlayerAsync();
        var decomposed = "Nguye\u0302\u0303n";
        var result = await player.CommandAsync(IdentityModule.DisplayNameRoute, new SetDisplayNameRequest { DisplayName = decomposed });
        result.Code.ShouldBe("OK");
        GameServer.Modules.Identity.Protocol.V1.ProfileState.Parser.ParseFrom(result.Updates.Single().Payload).DisplayName.ShouldBe("Nguyễn");
    }

    private sealed class BlockList(string word) : IDisplayNamePolicy
    {
        public bool IsAllowed(string displayName) => !displayName.Contains(word, StringComparison.OrdinalIgnoreCase);
    }
}
