using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Modules.Iap;
using Shouldly;

namespace SampleGame.Tests;

/// <summary><c>transactionID</c> and payment fields (T021, R3, FR-008, LOG-05).</summary>
public sealed class IapAnalyticsUnitTests
{
    [Fact]
    public void Apple_uses_the_transaction_id() =>
        IapAnalytics.TransactionId(new VerifiedPurchase(true, "apple", "gems_100", "2000000748921311", "2000000748921311", null)).ShouldBe("apple:2000000748921311");

    [Fact]
    public void Google_uses_the_order_id() =>
        IapAnalytics.TransactionId(new VerifiedPurchase(true, "google", "gems_100", "token-abc", "GPA.3391-5521-0937-41862", null)).ShouldBe("google:GPA.3391-5521-0937-41862");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Google_without_order_id_uses_a_digest_of_the_token_never_the_token(string? orderId)
    {
        var token = new string('k', 300);

        var id = IapAnalytics.TransactionId(new VerifiedPurchase(true, "google", "gems_100", token, orderId, null));

        id.ShouldBe("google:h:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32]);
        id.ShouldMatch("^google:h:[0-9a-f]{32}$");
        id.ShouldNotContain("kkkk");
        IapAnalytics.TransactionId(new VerifiedPurchase(true, "google", "gems_100", token, orderId, null)).ShouldBe(id);
        IapAnalytics.TransactionId(new VerifiedPurchase(true, "google", "gems_100", token + "x", orderId, null)).ShouldNotBe(id);
    }

    [Fact]
    public void An_unknown_store_is_refused() =>
        Should.Throw<ArgumentException>(() => IapAnalytics.TransactionId(new VerifiedPurchase(true, "amazon", "gems_100", "r", "o", null)));

    [Fact]
    public void Fields_follow_the_contract_names_and_order_and_omit_unknown_values()
    {
        var full = new VerifiedPurchase(true, "google", "gems_100", "token", "GPA.1", null)
        {
            IsTest = true,
            PurchaseCategory = "promo",
            PurchasedAt = new DateTimeOffset(2026, 10, 30, 14, 5, 1, TimeSpan.Zero).AddTicks(12_345),
        };
        var bare = new VerifiedPurchase(true, "google", "gems_100", "token", null, null);

        JsonSerializer.Serialize(IapAnalytics.Fields(full)).ShouldBe(
            """{"transactionID":"google:GPA.1","store":"google","productId":"gems_100","orderId":"GPA.1","isTest":true,"purchaseCategory":"promo","storePurchaseTimeUtc":"2026-10-30T14:05:01.001Z"}""");
        JsonSerializer.Serialize(IapAnalytics.Fields(bare)).ShouldStartWith("""{"transactionID":"google:h:""");
        JsonSerializer.Serialize(IapAnalytics.Fields(bare)).ShouldEndWith(""","store":"google","productId":"gems_100","isTest":false}""");
        IapAnalytics.Payment.ShouldBe(new GameServer.Analytics.AnalyticsEventType("payment", 1, "chargeTime"));
    }
}
