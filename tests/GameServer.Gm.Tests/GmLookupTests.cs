using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>Player lookup, PII gating and paged lists (US2: GM-02, GM-06, ACC-24, ACC-45).</summary>
public sealed class GmLookupTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private string _localId = null!;
    private string _playerId = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GmTestHost.StartAsync();
        _localId = IdentityHttp.NewLocalId();
        var login = await _host.Client().LoginAsync(_localId, IdentityHttp.NewSecret());
        _playerId = login.PlayerId;
        var player = new GamePlayer(_host.BearerClient(login.SessionToken), _playerId);
        for (var i = 1; i <= 5; i++)
        {
            _host.Clock.Advance(TimeSpan.FromSeconds(1));
            await player.CheatAsync("gold", i);
        }
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static JsonNode Aggregate(JsonNode view, string id) =>
        view["aggregates"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public async Task Lookup_by_id_shows_every_aggregate_with_rev_and_size_but_no_pii()
    {
        var gm = await _host.GmClientAsync("support");
        var view = await gm.GetAsync($"/gm/v1/players/{_playerId}").ShouldBeAsync(200);

        view["playerId"]!.GetValue<string>().ShouldBe(_playerId);
        var wallet = Aggregate(view, "wallet");
        wallet["rev"]!.GetValue<long>().ShouldBe(5);
        wallet["sizeBytes"]!.GetValue<int>().ShouldBeGreaterThan(0);
        wallet["state"]!["balances"]!["gold"]!.GetValue<long>().ShouldBe(15);
        Aggregate(view, "progression")["exists"]!.GetValue<bool>().ShouldBeFalse();

        var profile = Aggregate(view, "profile");
        profile["exists"]!.GetValue<bool>().ShouldBeTrue();
        profile["state"]!.AsObject().ContainsKey("identities").ShouldBeFalse();
        profile["state"]!["sessionEpoch"]!.GetValue<long>().ShouldBe(1);
    }

    [Fact]
    public async Task Pii_permission_reveals_identity_mapping_keys()
    {
        var gm = await _host.GmClientAsync("pii");
        var view = await gm.GetAsync($"/gm/v1/players/{_playerId}").ShouldBeAsync(200);
        Aggregate(view, "profile")["state"]!["identities"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe([$"guest:{_localId}"]);
    }

    [Fact]
    public async Task Lookup_by_mapping_key_finds_the_player()
    {
        var gm = await _host.GmClientAsync("support");
        (await gm.GetAsync($"/gm/v1/players/by-mapping/guest:{_localId}").ShouldBeAsync(200))["playerId"]!.GetValue<string>().ShouldBe(_playerId);
        await gm.GetAsync("/gm/v1/players/by-mapping/guest:unknown").ShouldBeAsync(404);
        await gm.GetAsync("/gm/v1/players/no-such-player").ShouldBeAsync(404);
        await gm.GetAsync("/gm/v1/players/bad!id").ShouldBeAsync(400);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=201")]
    [InlineData("?pageSize=ten")]
    public async Task Lists_require_a_page_size_of_1_to_200(string query)
    {
        var gm = await _host.GmClientAsync("support");
        foreach (var list in new[] { "ledger", "receipts", "audit" })
        {
            var problem = await gm.GetAsync($"/gm/v1/players/{_playerId}/{list}{query}").ShouldBeAsync(400);
            problem["code"]!.GetValue<string>().ShouldBe("BAD_REQUEST");
        }

        var separator = query.Length == 0 ? "?" : "&";
        await gm.GetAsync($"/gm/v1/players/search{query}{separator}displayName=x").ShouldBeAsync(400);
    }

    [Fact]
    public async Task Ledger_cursor_walk_returns_every_entry_once_newest_first()
    {
        var gm = await _host.GmClientAsync("support");
        var deltas = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/gm/v1/players/{_playerId}/ledger?pageSize=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await gm.GetAsync(url).ShouldBeAsync(200);
            page["items"]!.AsArray().Count.ShouldBeLessThanOrEqualTo(2);
            deltas.AddRange(page["items"]!.AsArray().Select(i => i!["payload"]!["delta"]!.GetValue<long>()));
            cursor = page["nextCursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        deltas.ShouldBe([5, 4, 3, 2, 1]);
        await gm.GetAsync($"/gm/v1/players/{_playerId}/ledger?pageSize=2&cursor=garbage!").ShouldBeAsync(400);
    }

    [Fact]
    public async Task Receipts_are_listed_and_readable_by_id()
    {
        var gm = await _host.GmClientAsync("support");
        var page = await gm.GetAsync($"/gm/v1/players/{_playerId}/receipts?pageSize=10").ShouldBeAsync(200);
        var items = page["items"]!.AsArray();
        items.Count.ShouldBe(5);
        items.ShouldAllBe(i => i!["operation"]!.GetValue<string>() == "cheat.grant");

        var receiptId = items[0]!["receiptId"]!.GetValue<string>();
        (await gm.GetAsync($"/gm/v1/receipts/{receiptId}").ShouldBeAsync(200))["playerId"]!.GetValue<string>().ShouldBe(_playerId);
        await gm.GetAsync("/gm/v1/receipts/unknown").ShouldBeAsync(404);
    }

    [Fact]
    public async Task Search_by_display_name_pages_through_matches()
    {
        for (var i = 0; i < 5; i++)
        {
            _host.Store.Seed(new DocumentKey("pa_profile", $"named-{i}"), new JsonObject
            {
                ["playerId"] = $"named-{i}",
                ["aggregateId"] = "profile",
                ["schemaVersion"] = 1,
                ["rev"] = 1,
                ["state"] = new JsonObject { ["displayName"] = i < 3 ? "Alice" : "Bob" },
            });
        }

        var gm = await _host.GmClientAsync("support");
        var first = await gm.GetAsync("/gm/v1/players/search?displayName=Alice&pageSize=2").ShouldBeAsync(200);
        first["items"]!.AsArray().Select(i => i!["playerId"]!.GetValue<string>()).ShouldBe(["named-0", "named-1"]);
        var cursor = Uri.EscapeDataString(first["nextCursor"]!.GetValue<string>());
        var second = await gm.GetAsync($"/gm/v1/players/search?displayName=Alice&pageSize=2&cursor={cursor}").ShouldBeAsync(200);
        second["items"]!.AsArray().Select(i => i!["playerId"]!.GetValue<string>()).ShouldBe(["named-2"]);
        second["nextCursor"].ShouldBeNull();

        await gm.GetAsync("/gm/v1/players/search?displayName=&pageSize=2").ShouldBeAsync(400);
    }
}
