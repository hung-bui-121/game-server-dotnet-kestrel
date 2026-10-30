using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GameServer.Platform;
using GameServer.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Iap;

/// <summary>
/// Offline verification of a StoreKit 2 <c>signedTransactionInfo</c> JWS (IAP-01, [VERIFY] V21): ES256 signature by the
/// leaf of the <c>x5c</c> chain, chain built to the pinned Apple Root CA G3, Apple certificate OIDs, then
/// <c>bundleId</c>, <c>productId</c>, <c>environment</c> (Production deployment accepts only Production) and no revocation.
/// External reference = <c>transactionId</c>.
/// </summary>
public sealed class AppleJwsReceiptVerifier : IStoreReceiptVerifier
{
    public const string StoreName = "apple";
    private const string LeafOid = "1.2.840.113635.100.6.11.1";
    private const string IntermediateOid = "1.2.840.113635.100.6.2.1";

    /// <summary>9999-12-31T23:59:59.999Z, the last instant DateTimeOffset can hold.</summary>
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    private readonly AppleOptions _options;
    private readonly GameServerOptions _gameServer;
    private readonly IClock _clock;
    private readonly ILogger<AppleJwsReceiptVerifier> _logger;
    private readonly X509Certificate2? _root;

    public AppleJwsReceiptVerifier(IOptions<IapOptions> options, GameServerOptions gameServer, IClock clock, ILogger<AppleJwsReceiptVerifier> logger)
    {
        _options = options.Value.Apple;
        _gameServer = gameServer;
        _clock = clock;
        _logger = logger;
        _root = string.IsNullOrWhiteSpace(_options.RootCertificateBase64)
            ? null
            : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(_options.RootCertificateBase64));
    }

    public string Store => StoreName;

    public Task<VerifiedPurchase> VerifyAsync(string productId, string receiptPayload, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(Verify(productId, receiptPayload));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogInformation("Apple receipt rejected: {Error}", ex.GetType().Name);
            return Task.FromResult(VerifiedPurchase.Invalid(StoreName, productId, ex.GetType().Name));
        }
    }

    private VerifiedPurchase Verify(string productId, string jws)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("Iap:Apple:RootCertificateBase64 is not configured.");
        }

        var parts = jws.Split('.');
        if (parts.Length != 3)
        {
            return VerifiedPurchase.Invalid(StoreName, productId, "not a compact JWS");
        }

        using var header = JsonDocument.Parse(Base64Url(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "ES256")
        {
            return VerifiedPurchase.Invalid(StoreName, productId, "alg");
        }

        var chain = header.RootElement.GetProperty("x5c").EnumerateArray()
            .Select(c => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(c.GetString()!)))
            .ToArray();
        try
        {
            if (chain.Length < 2 || !ChainIsTrusted(chain))
            {
                return VerifiedPurchase.Invalid(StoreName, productId, "certificate chain");
            }

            using var key = chain[0].GetECDsaPublicKey() ?? throw new CryptographicException("leaf has no EC key");
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!key.VerifyData(signed, Base64Url(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return VerifiedPurchase.Invalid(StoreName, productId, "signature");
            }
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }

        using var payload = JsonDocument.Parse(Base64Url(parts[1]));
        var transaction = payload.RootElement;
        var environment = transaction.GetProperty("environment").GetString();
        var allowed = _gameServer.IsProduction ? new[] { "Production" } : ["Production", "Sandbox", "Xcode", "LocalTesting"];
        if (transaction.GetProperty("bundleId").GetString() != _options.BundleId
            || transaction.GetProperty("productId").GetString() != productId
            || !allowed.Contains(environment)
            || transaction.TryGetProperty("revocationDate", out _))
        {
            return VerifiedPurchase.Invalid(StoreName, productId, "claims");
        }

        var transactionId = transaction.GetProperty("transactionId").GetString()!;
        // purchaseDate is a timestamp in UNIX milliseconds ([VERIFY] V6); every environment but Production is a test purchase.
        DateTimeOffset? purchasedAt = transaction.TryGetProperty("purchaseDate", out var date) && date.ValueKind == JsonValueKind.Number
            && date.TryGetInt64(out var millis) && millis is >= 0 and <= MaxUnixMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis)
            : null;
        return new VerifiedPurchase(true, StoreName, productId, transactionId, transactionId, null)
        {
            IsTest = environment != "Production",
            PurchasedAt = purchasedAt,
        };
    }

    private bool ChainIsTrusted(X509Certificate2[] certificates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_root!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _clock.UtcNow.UtcDateTime;
        foreach (var intermediate in certificates.Skip(1))
        {
            chain.ChainPolicy.ExtraStore.Add(intermediate);
        }

        if (!chain.Build(certificates[0]))
        {
            return false;
        }

        if (!_options.RequireAppleCertificateOids)
        {
            return true;
        }

        return HasExtension(certificates[0], LeafOid) && HasExtension(certificates[1], IntermediateOid);
    }

    private static bool HasExtension(X509Certificate2 certificate, string oid) =>
        certificate.Extensions.Any(e => e.Oid?.Value == oid);

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }
}
