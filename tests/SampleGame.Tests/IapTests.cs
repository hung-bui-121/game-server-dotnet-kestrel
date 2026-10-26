using System.Net;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using Shouldly;
using IapWire = GameServer.Modules.Iap.Protocol.V1;

namespace SampleGame.Tests;

/// <summary>US3 of 003: IAPurchase with the fake verifier (SMP-04, IAP-02, ECO-08, ACC-46, ACC-47 P0 part).</summary>
public sealed class IapTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await GameHttp.StartGameAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Valid_purchase_grants_once_with_claim_and_iap_ledger()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await player.IapAsync("google", "gems_100", "valid:token-1");

        response.Code.ShouldBe("OK");
        IapWire.IapPurchaseResult.Parser.ParseFrom(response.Result).Granted.ShouldHaveSingleItem().Quantity.ShouldBe(100);
        (await player.WalletAsync()).Balances["gem"].ShouldBe(100);
        var ledger = _host.Ledger(player.PlayerId).ShouldHaveSingleItem();
        ledger["payload"]!["sourceType"]!.GetValue<string>().ShouldBe("iap");
        ledger["payload"]!["externalReference"]!.GetValue<string>().ShouldBe("token-1");
        ledger["payload"]!["orderId"]!.GetValue<string>().ShouldBe("GPA.token-1");
        _host.Store.Peek(new DocumentKey("extref_claims", "extref::iap_google::token-1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Same_receipt_again_is_already_granted_with_one_ledger_entry()
    {
        var (player, _) = await _host.NewPlayerAsync();
        await player.IapAsync("google", "gems_100", "valid:token-2");

        var again = await player.IapAsync("google", "gems_100", "valid:token-2");

        again.Code.ShouldBe("ALREADY_GRANTED");
        (await player.WalletAsync()).Balances["gem"].ShouldBe(100);
        _host.Ledger(player.PlayerId).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Receipt_used_by_another_player_is_already_granted()
    {
        var (first, _) = await _host.NewPlayerAsync();
        var (second, _) = await _host.NewPlayerAsync();
        await first.IapAsync("apple", "gems_100", "valid:shared");

        (await second.IapAsync("apple", "gems_100", "valid:shared")).Code.ShouldBe("ALREADY_GRANTED");
    }

    [Theory]
    [InlineData("google", "gems_100", "forged")]
    [InlineData("google", "not_in_catalog", "valid:x1")]
    [InlineData("apple", "starter_pack", "valid:x2")]
    public async Task Invalid_or_unknown_purchases_are_invalid_receipt(string store, string productId, string payload)
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await player.IapAsync(store, productId, payload);

        response.Code.ShouldBe("INVALID_RECEIPT");
        _host.Ledger(player.PlayerId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_outage_is_a_retryable_503_and_writes_nothing()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await player.PostAsync("/api/v1/iap/purchase", new IapWire.IapPurchaseRequest { Store = "google", ProductId = "gems_100", ReceiptPayload = "unavailable" });

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe("STORE_UNAVAILABLE");
        error.Retryable.ShouldBeTrue();
        _host.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_store_is_400()
    {
        var (player, _) = await _host.NewPlayerAsync();

        var response = await player.PostAsync("/api/v1/iap/purchase", new IapWire.IapPurchaseRequest { Store = "amazon", ProductId = "gems_100", ReceiptPayload = "valid:z" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
