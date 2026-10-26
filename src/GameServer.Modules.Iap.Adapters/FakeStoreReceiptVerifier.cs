namespace GameServer.Modules.Iap;

/// <summary>
/// Store verifier for Development and CI (IAP-01: "CI dùng verifier giả"). Payload conventions:
/// <c>valid:{reference}</c> → valid purchase with that external reference (order <c>GPA.{reference}</c>);
/// <c>unavailable</c> → transient store failure; anything else → invalid receipt.
/// </summary>
public sealed class FakeStoreReceiptVerifier(string store) : IStoreReceiptVerifier
{
    public const string ValidPrefix = "valid:";
    public const string UnavailablePayload = "unavailable";

    public string Store { get; } = store;

    public Task<VerifiedPurchase> VerifyAsync(string productId, string receiptPayload, CancellationToken cancellationToken)
    {
        if (receiptPayload == UnavailablePayload)
        {
            throw new StoreUnavailableException("Fake store unavailable.");
        }

        if (receiptPayload.StartsWith(ValidPrefix, StringComparison.Ordinal) && receiptPayload.Length > ValidPrefix.Length)
        {
            var reference = receiptPayload[ValidPrefix.Length..];
            return Task.FromResult(new VerifiedPurchase(true, Store, productId, reference, "GPA." + reference, null));
        }

        return Task.FromResult(VerifiedPurchase.Invalid(Store, productId, "fake: invalid payload"));
    }
}
