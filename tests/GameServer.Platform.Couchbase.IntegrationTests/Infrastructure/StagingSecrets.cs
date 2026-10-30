using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// Believable, freshly generated secrets for a Staging or Production sample host (data-model §3 "required from the
/// environment"): data protection master key, GM SSO with an https authority, guest pepper and IAP key material with a valid
/// RSA PEM and a self-signed stand-in for the Apple root (validated at startup). No real credential; the SSO authority is never
/// contacted because no test signs in to the GM.
/// </summary>
internal static class StagingSecrets
{
    private const int MasterKeyBytes = 32;
    private const int SecretBytes = 24;
    private const int RsaKeyBits = 2048;

    public static Dictionary<string, string?> Create()
    {
        using var rsa = RSA.Create(RsaKeyBits);
        using var appleRootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var appleRoot = new CertificateRequest("CN=Test Apple Root", appleRootKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        return new Dictionary<string, string?>
        {
            ["DataProtection:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(MasterKeyBytes)),
            ["Gm:Oidc:Authority"] = "https://sso.example.invalid/",
            ["Gm:Oidc:ClientId"] = "gameserver-it",
            ["Gm:Oidc:ClientSecret"] = Random(),
            ["Identity:GuestPepper"] = Random(),
            ["Iap:Google:PackageName"] = "com.example.sample",
            ["Iap:Google:ServiceAccountEmail"] = "iap-verifier@example.invalid",
            ["Iap:Google:ServiceAccountPrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["Iap:Apple:BundleId"] = "com.example.sample",
            ["Iap:Apple:RootCertificateBase64"] = Convert.ToBase64String(appleRoot.RawData),
        };
    }

    public static string Random() => Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretBytes));
}
