using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using GameServer.Analytics;

namespace GameServer.Modules.Iap;

/// <summary>
/// The <c>payment</c> analytics event of <c>iap.purchase</c> (ANA-01, owner task 3): committed with the grant as an outbox
/// entry, so it is delivered at least once. Core API only (ARC-01).
/// </summary>
public static class IapAnalytics
{
    private const string GoogleStore = "google";
    private const string AppleStore = "apple";
    private const int DigestHexLength = 32;

    /// <summary><c>("payment", 1, "chargeTime")</c>.</summary>
    public static AnalyticsEventType Payment { get; } = new("payment", 1, "chargeTime");

    /// <summary>
    /// Store transaction id for the record (R3, FR-008): <c>apple:{transactionId}</c>, <c>google:{orderId}</c>, or
    /// <c>google:h:{32 hex}</c> (SHA-256 of the purchase token) when Google gives no order id. Never the token (LOG-05).
    /// </summary>
    public static string TransactionId(VerifiedPurchase purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        return purchase.Store switch
        {
            AppleStore => "apple:" + purchase.ExternalReference,
            GoogleStore when !string.IsNullOrEmpty(purchase.OrderId) => "google:" + purchase.OrderId,
            GoogleStore => "google:h:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(purchase.ExternalReference)))[..DigestHexLength],
            _ => throw new ArgumentException($"Unknown store '{purchase.Store}' for the payment record.", nameof(purchase)),
        };
    }

    /// <summary>Type fields of the payment record (data-model §3.1), in contract order.</summary>
    public static PaymentFields Fields(VerifiedPurchase purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        return new PaymentFields(
            TransactionId(purchase),
            purchase.Store,
            purchase.ProductId,
            string.IsNullOrEmpty(purchase.OrderId) ? null : purchase.OrderId,
            purchase.IsTest,
            purchase.PurchaseCategory,
            purchase.PurchasedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}

/// <summary>Fields of <c>payment</c> v1; absent optional values are not stored (receipt size, FR-012).</summary>
public sealed record PaymentFields(
    [property: JsonPropertyName("transactionID")] string TransactionId,
    [property: JsonPropertyName("store")] string Store,
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("orderId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OrderId,
    [property: JsonPropertyName("isTest")] bool IsTest,
    [property: JsonPropertyName("purchaseCategory"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PurchaseCategory,
    [property: JsonPropertyName("storePurchaseTimeUtc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StorePurchaseTimeUtc);
