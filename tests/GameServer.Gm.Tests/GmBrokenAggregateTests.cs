using System.Text.Json.Nodes;
using GameServer.Modules.Economy;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 016 T043 (FR-025, US3-5, wire-changes §4): the GM player view lists the aggregates it cannot read in <c>unreadable</c>
/// (always present), a broken profile gives <c>deletion = null</c> and still a player; GM commands and the GM target validator answer
/// the typed code. Also T027's "listed by the GM receipt list" for the creation receipt.
/// </summary>
public sealed class GmBrokenAggregateTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private string _playerId = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GmTestHost.StartAsync(
            services: s => s.AddSingleton(StartingStateBehaviour.Grant(100)),
            extra: b => b.AddModule(new StartingStateModule()));
        _playerId = (await _host.NewPlayerAsync()).PlayerId;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_readable_player_has_an_empty_unreadable_list()
    {
        var view = await PlayerViewAsync();

        view["unreadable"]!.AsArray().ShouldBeEmpty();
        view["aggregates"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).ShouldContain(StartingStateAggregates.Starter);
    }

    [Theory]
    [InlineData(BrokenReason.VersionAhead, "AGGREGATE_VERSION_AHEAD", "version_ahead", 2, 1)]
    [InlineData(BrokenReason.Unreadable, "AGGREGATE_UNREADABLE", "unreadable", 1, 1)]
    [InlineData(BrokenReason.UpgradeFailed, "AGGREGATE_UNREADABLE", "upgrade_failed", 1, 2)]
    public async Task A_broken_aggregate_is_listed_with_its_code_reason_and_versions(BrokenReason reason, string code, string name, int stored, int codeSchema)
    {
        BrokenDocuments.Seed(_host.Store, reason, _playerId);
        var aggregate = BrokenDocuments.AggregateOf(reason);

        var view = await PlayerViewAsync();

        var unreadable = view["unreadable"]!.AsArray().ShouldHaveSingleItem()!;
        unreadable["id"]!.GetValue<string>().ShouldBe(aggregate);
        unreadable["code"]!.GetValue<string>().ShouldBe(code);
        unreadable["reason"]!.GetValue<string>().ShouldBe(name);
        unreadable["storedSchemaVersion"]!.GetValue<int>().ShouldBe(stored);
        unreadable["codeSchemaVersion"]!.GetValue<int>().ShouldBe(codeSchema);
        view["aggregates"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).ShouldNotContain(aggregate);
    }

    [Fact]
    public async Task A_state_only_its_wire_mapper_refuses_is_readable_for_gm()
    {
        BrokenDocuments.Seed(_host.Store, BrokenReason.MapperFailed, _playerId);

        var view = await PlayerViewAsync();

        view["unreadable"]!.AsArray().ShouldBeEmpty();
        view["aggregates"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == StartingStateAggregates.Starter)!["state"]!["padding"]!.GetValue<string>()
            .ShouldBe(StartingStateAggregates.MapperFailMarker);
    }

    [Fact]
    public async Task A_broken_profile_is_listed_gives_no_deletion_and_the_player_still_exists()
    {
        BreakProfile();

        var view = await PlayerViewAsync();

        view["deletion"].ShouldBeNull();
        view["unreadable"]!.AsArray().ShouldHaveSingleItem()!["id"]!.GetValue<string>().ShouldBe("profile");
    }

    [Fact]
    public async Task A_gm_command_on_a_broken_profile_is_refused_by_the_target_validator_with_the_typed_code()
    {
        BreakProfile();
        var gm = await _host.GmClientAsync("econ");

        var problem = await gm.PostCommandAsync(AdjustRoute(), new { resource = "gold", delta = 1, reason = "test" }).ShouldBeAsync(503);

        problem["code"]!.GetValue<string>().ShouldBe("AGGREGATE_VERSION_AHEAD");
        problem["details"]!["aggregate"]!.GetValue<string>().ShouldBe("profile");
    }

    [Fact]
    public async Task A_gm_command_touching_a_broken_aggregate_answers_the_typed_code_and_writes_nothing()
    {
        var wallet = new DocumentKey("pa_" + EconomyAggregates.Wallet, _playerId);
        var damaged = (JsonObject)_host.Store.Peek(wallet)!.DeepClone();
        damaged["state"] = new JsonArray();
        _host.Store.Seed(wallet, damaged);
        var gm = await _host.GmClientAsync("econ");

        var problem = await gm.PostCommandAsync(AdjustRoute(), new { resource = "gold", delta = 1, reason = "test" }).ShouldBeAsync(500);

        problem["code"]!.GetValue<string>().ShouldBe("AGGREGATE_UNREADABLE");
        problem["details"]!["aggregate"]!.GetValue<string>().ShouldBe(EconomyAggregates.Wallet);
        _host.Store.Peek(wallet)!.ToJsonString().ShouldBe(damaged.ToJsonString());
    }

    [Fact]
    public async Task The_creation_receipt_is_listed_by_the_gm_receipt_list()
    {
        var gm = await _host.GmClientAsync("support");

        var page = await gm.GetAsync($"/gm/v1/players/{_playerId}/receipts?pageSize=10").ShouldBeAsync(200);

        var creation = page["items"]!.AsArray().Single(i => i!["operation"]!.GetValue<string>() == AccountCreation.Operation)!;
        creation["actorType"]!.GetValue<string>().ShouldBe("system");
        creation["actorId"]!.GetValue<string>().ShouldBe(_playerId);
    }

    private async Task<JsonNode> PlayerViewAsync()
    {
        var gm = await _host.GmClientAsync("support");
        return await gm.GetAsync($"/gm/v1/players/{_playerId}").ShouldBeAsync(200);
    }

    private string AdjustRoute() => EconomyModule.AdjustRoute.Replace("{playerId}", _playerId, StringComparison.Ordinal);

    private void BreakProfile()
    {
        var key = new DocumentKey("pa_profile", _playerId);
        var damaged = (JsonObject)_host.Store.Peek(key)!.DeepClone();
        damaged["schemaVersion"] = GameServer.Modules.Identity.IdentityAggregates.ProfileDescriptor.SchemaVersion + 1;
        _host.Store.Seed(key, damaged);
    }
}
