using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// The CA of <c>Couchbase:TrustCertificatePath</c> as the only trusted root of the cluster's TLS certificates (SEC-01, research R5).
/// The SDK's own <c>WithTrustedServerCertificates</c> is not used: it checks the OS trust store first ([VERIFY] T033).
/// </summary>
internal static class CouchbaseTrustedRoots
{
    private const string PemMarker = "-----BEGIN";

    /// <summary>Absolute path of the configured file: relative paths resolve against the content root.</summary>
    public static string ResolvePath(string contentRoot, string path) => Path.GetFullPath(Path.Combine(contentRoot, path));

    /// <summary>
    /// Every certificate of a PEM file, or the certificate of a DER file. Throws <see cref="IOException"/> (or
    /// <see cref="UnauthorizedAccessException"/>) when the file cannot be read and <see cref="CryptographicException"/>
    /// when it holds no X.509 certificate.
    /// </summary>
    public static X509Certificate2Collection Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var certificates = new X509Certificate2Collection();
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains(PemMarker, StringComparison.Ordinal))
        {
            certificates.ImportFromPem(text);
        }
        else
        {
            certificates.Add(X509CertificateLoader.LoadCertificate(bytes));
        }

        return certificates.Count > 0
            ? certificates
            : throw new CryptographicException("The PEM file contains no CERTIFICATE block.");
    }

    /// <summary>Server certificate validation for KV and HTTP connections against <paramref name="roots"/> only.</summary>
    public static RemoteCertificateValidationCallback Validator(X509Certificate2Collection roots) =>
        (_, certificate, chain, errors) => IsTrusted(roots, certificate, chain, errors);

    /// <summary>
    /// Trusted when the host name matches (the platform's own check, kept) and the chain ends in one of <paramref name="roots"/>;
    /// the OS trust store is ignored. Revocation is not checked, like the SDK default (a private CA rarely publishes a CRL).
    /// <paramref name="roots"/> must hold the self-signed root CA: an intermediate alone ends the chain as <c>PartialChain</c> and is
    /// refused. Missing intermediates are never downloaded (AIA) inside the TLS handshake: the server must send them.
    /// </summary>
    internal static bool IsTrusted(X509Certificate2Collection roots, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null
            || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != SslPolicyErrors.None)
        {
            return false;
        }

        var owned = certificate is X509Certificate2 ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var custom = new X509Chain();
        try
        {
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(roots);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.DisableCertificateDownloads = true;

            // Intermediates sent by the server (owned by the caller's chain: not disposed here).
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                {
                    custom.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            return custom.Build(owned ?? (X509Certificate2)certificate);
        }
        finally
        {
            // Build creates a certificate object per chain element; X509Chain.Dispose does not release them.
            foreach (var element in custom.ChainElements)
            {
                element.Certificate.Dispose();
            }

            owned?.Dispose();
        }
    }
}
