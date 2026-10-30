using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameServer.Platform;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Iap;

/// <summary>
/// Startup validation of the <c>Iap</c> section (IAP-01, FR-009), merged into the one startup report of serve mode (not run by
/// <c>--provision</c>, which needs no IAP credentials). The fake verifier is refused outside Development/Test. Whenever the real
/// verifiers are used (default outside Development/Test, or <c>Iap:UseFakeVerifier=false</c>), their keys must be set and the key
/// material must parse, so a deployment never starts with purchases that can only fail. Messages name the key, never its value.
/// </summary>
internal sealed class IapOptionsValidator(GameServerOptions gameServer) : IValidateOptions<IapOptions>
{
    private const string Prefix = "[module iap] ";

    public ValidateOptionsResult Validate(string? name, IapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var useFake = options.UseFakeVerifier ?? gameServer.AllowsDevelopmentTools;
        if (useFake)
        {
            if (!gameServer.AllowsDevelopmentTools)
            {
                errors.Add(
                    $"{Prefix}Iap:UseFakeVerifier is true in '{gameServer.DeploymentEnvironment}'. Fix: remove it and configure the Apple/Google " +
                    "verifiers (IAP-01); the fake verifier is for Development/Test only.");
            }

            return Result(errors);
        }

        Required(errors, "Iap:Google:PackageName", options.Google.PackageName);
        Required(errors, "Iap:Google:ServiceAccountEmail", options.Google.ServiceAccountEmail);
        if (Required(errors, "Iap:Google:ServiceAccountPrivateKeyPem", options.Google.ServiceAccountPrivateKeyPem)
            && !IsRsaPrivateKey(options.Google.ServiceAccountPrivateKeyPem))
        {
            errors.Add(
                $"{Prefix}Iap:Google:ServiceAccountPrivateKeyPem is not a PEM RSA private key. Fix: set the private key of the service " +
                "account (PKCS#8 PEM, -----BEGIN PRIVATE KEY-----) through the secret provider (OPS-08).");
        }

        Required(errors, "Iap:Apple:BundleId", options.Apple.BundleId);
        if (Required(errors, "Iap:Apple:RootCertificateBase64", options.Apple.RootCertificateBase64)
            && !IsCertificate(options.Apple.RootCertificateBase64))
        {
            errors.Add(
                $"{Prefix}Iap:Apple:RootCertificateBase64 is not a base64 DER X.509 certificate. Fix: set the Apple Root CA - G3 certificate " +
                "(DER, base64).");
        }

        return Result(errors);
    }

    private static ValidateOptionsResult Result(List<string> errors) =>
        errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

    /// <summary>True when set; otherwise adds the error naming the key and its environment variable.</summary>
    private static bool Required(List<string> errors, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        errors.Add($"{Prefix}{key} is not set. Fix: set the environment variable {key.Replace(":", "__", StringComparison.Ordinal)} (IAP-01).");
        return false;
    }

    /// <summary>
    /// Same import and signature as <c>GooglePlayReceiptVerifier</c> (RS256). A public key imports too: signing one byte proves the
    /// private part is there.
    /// </summary>
    private static bool IsRsaPrivateKey(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            _ = rsa.SignData([0], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Same load as <c>AppleJwsReceiptVerifier</c>.</summary>
    private static bool IsCertificate(string base64)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
