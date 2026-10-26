namespace GameServer.Modules.Iap;

/// <summary>IAP settings (section <c>Iap</c>). Secrets (Google service account key) come from the secret provider (OPS-08).</summary>
public sealed class IapOptions
{
    public const string SectionName = "Iap";

    /// <summary>
    /// Use <see cref="FakeStoreReceiptVerifier"/> for both stores (Development/CI). Allowed only in Development/Test.
    /// Defaults to true there and false elsewhere.
    /// </summary>
    public bool? UseFakeVerifier { get; set; }

    public AppleOptions Apple { get; set; } = new();

    public GoogleOptions Google { get; set; } = new();

    /// <summary>Timeout of one store call (outbound HTTP, PERF-03).</summary>
    public int StoreTimeoutSeconds { get; set; } = 10;
}

public sealed class AppleOptions
{
    public string BundleId { get; set; } = string.Empty;

    /// <summary>Apple Root CA G3 (DER, base64). Pinned in config; never downloaded at runtime.</summary>
    public string RootCertificateBase64 { get; set; } = string.Empty;

    /// <summary>Apple OIDs on the leaf/intermediate certificates (disable only in tests with self-signed chains).</summary>
    public bool RequireAppleCertificateOids { get; set; } = true;
}

public sealed class GoogleOptions
{
    public string PackageName { get; set; } = string.Empty;

    public string ServiceAccountEmail { get; set; } = string.Empty;

    /// <summary>PKCS#8 PEM private key of the service account.</summary>
    public string ServiceAccountPrivateKeyPem { get; set; } = string.Empty;

    public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";

    public string ApiBaseUri { get; set; } = "https://androidpublisher.googleapis.com";
}
