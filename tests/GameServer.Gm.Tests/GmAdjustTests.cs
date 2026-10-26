using System.Text.Json.Nodes;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Platform.Outbox;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using SampleGame.Adapters;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>Resource adjustment and progression edit with audit (US3: GM-03, SEC-14, ACC-27, ACC-45).</summary>
public sealed class GmAdjustTests : IAsyncLifetime
{
    private const string PlayerId = "p-adjust";
    private TestHost _host = null!;
    private GamePlayer _player = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await GmTestHost.StartAsync();
        _player = new GamePlayer(_host.Player(PlayerId), PlayerId);
        await _player.CheatAsync("gold", 50);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static string AdjustRoute => EconomyModule.AdjustRoute.Replace("{playerId}", PlayerId, StringComparison.Ordinal);

    private static string ProgressionRoute => SampleGameModule.GmProgressionRoute.Replace("{playerId}", PlayerId, StringComparison.Ordinal);

    private IReadOnlyList<JsonObject> Audits() => _host.Store.All(GmModule.AuditCollection).Select(d => d.Content).ToArray();

    private IReadOnlyList<JsonObject> Ledger(string sourceType) =>
        _host.Store.All(EconomyOperations.LedgerCollection).Select(d => d.Content)
            .Where(d => d["payload"]!["sourceType"]!.GetValue<string>() == sourceType).ToArray();

    [Fact]
    public async Task Adjust_changes_the_wallet_with_receipt_ledger_and_a_complete_audit()
    {
        var gm = await _host.GmClientAsync("econ");
        var response = await gm.PostCommandAsync(AdjustRoute, new { resource = "gold", delta = 100, reason = "compensation" }).ShouldBeAsync(200);

        response["code"]!.GetValue<string>().ShouldBe("OK");
        response["result"]!["balanceBefore"]!.GetValue<long>().ShouldBe(50);
        response["result"]!["balanceAfter"]!.GetValue<long>().ShouldBe(150);
        response["updates"]!.AsArray().Select(u => u!["id"]!.GetValue<string>()).ShouldBe(["wallet"]);
        (await _player.WalletAsync()).Balances["gold"].ShouldBe(150);

        var receiptId = response["receiptId"]!.GetValue<string>();
        var receipt = await gm.GetAsync($"/gm/v1/receipts/{receiptId}").ShouldBeAsync(200);
        receipt["actorType"]!.GetValue<string>().ShouldBe("gm");
        receipt["actorId"]!.GetValue<string>().ShouldBe("econ");
        receipt["playerId"]!.GetValue<string>().ShouldBe(PlayerId);

        var ledger = Ledger(AdjustResourceHandler.SourceType).ShouldHaveSingleItem();
        ledger["receiptId"]!.GetValue<string>().ShouldBe(receiptId);
        ledger["payload"]!["delta"]!.GetValue<long>().ShouldBe(100);

        // SEC-14: actor, permission/action, target, reason, receipt reference, UTC time, correlation.
        var audit = Audits().ShouldHaveSingleItem();
        audit["actorId"]!.GetValue<string>().ShouldBe("econ");
        audit["payload"]!["actorId"]!.GetValue<string>().ShouldBe("econ");
        audit["payload"]!["permission"]!.GetValue<string>().ShouldBe("resource.adjust");
        audit["payload"]!["action"]!.GetValue<string>().ShouldBe("economy.adjust");
        audit["payload"]!["targetPlayerId"]!.GetValue<string>().ShouldBe(PlayerId);
        audit["playerId"]!.GetValue<string>().ShouldBe(PlayerId);
        audit["payload"]!["reason"]!.GetValue<string>().ShouldBe("compensation");
        audit["receiptId"]!.GetValue<string>().ShouldBe(receiptId);
        audit["payload"]!["before"].ShouldBeNull();
        audit["payload"]!["after"].ShouldBeNull();
        DateTimeOffset.Parse(audit["committedAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(_host.Clock.UtcNow);
        audit["payload"]!["correlationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        var listed = await gm.GetAsync($"/gm/v1/players/{PlayerId}/audit?pageSize=10").ShouldBeAsync(200);
        listed["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "gold", 10)]
    [InlineData("  ", "gold", 10)]
    [InlineData("ok", null, 10)]
    public async Task Missing_reason_or_resource_is_400_without_changes(string? reason, string? resource, long delta)
    {
        var gm = await _host.GmClientAsync("econ");
        await gm.PostCommandAsync(AdjustRoute, new { resource, delta, reason }).ShouldBeAsync(400);
        (await _player.WalletAsync()).Balances["gold"].ShouldBe(50);
        Audits().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("diamond", 10, "UNKNOWN_ITEM")]
    [InlineData("gold", -51, "INSUFFICIENT_FUNDS")]
    [InlineData("gold", 0, "INVALID_QUANTITY")]
    public async Task Business_errors_change_nothing(string resource, long delta, string code)
    {
        var gm = await _host.GmClientAsync("econ");
        var response = await gm.PostCommandAsync(AdjustRoute, new { resource, delta, reason = "test" }).ShouldBeAsync(200);
        response["code"]!.GetValue<string>().ShouldBe(code);
        response["receiptId"].ShouldBeNull();
        (await _player.WalletAsync()).Balances["gold"].ShouldBe(50);
        Audits().ShouldBeEmpty();
        Ledger(AdjustResourceHandler.SourceType).ShouldBeEmpty();
    }

    [Fact]
    public async Task Negative_delta_debits()
    {
        var gm = await _host.GmClientAsync("econ");
        var response = await gm.PostCommandAsync(AdjustRoute, new { resource = "gold", delta = -20, reason = "chargeback" }).ShouldBeAsync(200);
        response["result"]!["balanceAfter"]!.GetValue<long>().ShouldBe(30);
        Ledger(AdjustResourceHandler.SourceType).ShouldHaveSingleItem()["payload"]!["delta"]!.GetValue<long>().ShouldBe(-20);
    }

    [Fact]
    public async Task Retry_with_the_same_request_id_replays()
    {
        var gm = await _host.GmClientAsync("econ");
        var requestId = Guid.NewGuid().ToString("D");
        var first = await gm.PostCommandAsync(AdjustRoute, new { resource = "gold", delta = 100, reason = "compensation" }, requestId).ShouldBeAsync(200);
        var second = await gm.PostCommandAsync(AdjustRoute, new { resource = "gold", delta = 100, reason = "compensation" }, requestId).ShouldBeAsync(200);

        second["replayed"]!.GetValue<bool>().ShouldBeTrue();
        second["receiptId"]!.GetValue<string>().ShouldBe(first["receiptId"]!.GetValue<string>());
        second["result"]!["balanceAfter"]!.GetValue<long>().ShouldBe(150);
        (await _player.WalletAsync()).Balances["gold"].ShouldBe(150);
        Audits().Count.ShouldBe(1);

        await gm.PostCommandAsync(AdjustRoute, new { resource = "gold", delta = 999, reason = "compensation" }, requestId).ShouldBeAsync(409);
    }

    [Fact]
    public async Task Command_without_request_id_is_400()
    {
        var gm = await _host.GmClientAsync("econ");
        var response = await gm.PostAsync(AdjustRoute, System.Net.Http.Json.JsonContent.Create(new { resource = "gold", delta = 1, reason = "x" }));
        ((int)response.StatusCode).ShouldBe(400);
    }

    [Fact]
    public async Task Progression_edit_records_before_and_after()
    {
        var gm = await _host.GmClientAsync("operator");
        var response = await gm.PostCommandAsync(ProgressionRoute, new { stage = 3, reason = "support ticket 42" }).ShouldBeAsync(200);
        response["code"]!.GetValue<string>().ShouldBe("OK");
        (await _player.ProgressionAsync()).Stage.ShouldBe(3);

        var audit = Audits().ShouldHaveSingleItem();
        audit["payload"]!["permission"]!.GetValue<string>().ShouldBe("progression.edit");
        audit["payload"]!["action"]!.GetValue<string>().ShouldBe("progression.set");
        audit["payload"]!["before"]!["stage"]!.GetValue<int>().ShouldBe(1);
        audit["payload"]!["after"]!["stage"]!.GetValue<int>().ShouldBe(3);
        audit["payload"]!["reason"]!.GetValue<string>().ShouldBe("support ticket 42");

        await gm.PostCommandAsync(ProgressionRoute, new { reason = "nothing" }).ShouldBeAsync(400);
        await gm.PostCommandAsync(ProgressionRoute, new { level = 0, reason = "bad" }).ShouldBeAsync(400);
        (await gm.PostCommandAsync(ProgressionRoute, new { stage = 999, reason = "too far" }).ShouldBeAsync(200))
            ["code"]!.GetValue<string>().ShouldBe("STAGE_LOCKED");
    }

    [Fact]
    public async Task Audit_is_delivered_by_the_sweeper_when_inline_relay_is_off()
    {
        await using var host = await GmTestHost.StartAsync(s => s["GameServer:Outbox:InlineRelayEnabled"] = "false");
        var gm = await host.GmClientAsync("econ");
        await gm.PostCommandAsync(AdjustRoute, new { resource = "gold", delta = 5, reason = "late audit" }).ShouldBeAsync(200);
        host.Store.All(GmModule.AuditCollection).ShouldBeEmpty();

        host.Clock.Advance(TimeSpan.FromSeconds(11));
        (await host.Services.GetRequiredService<OutboxSweeper>().SweepOnceAsync(CancellationToken.None)).ShouldBe(1);
        host.Store.All(GmModule.AuditCollection).ShouldHaveSingleItem().Content["payload"]!["reason"]!.GetValue<string>().ShouldBe("late audit");
    }
}
