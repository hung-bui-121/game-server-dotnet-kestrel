namespace GameServer.Platform.Web.Security;

/// <summary>Section <c>DataProtection</c>: shared key ring of the deployment (OPS-04, research R6/R7).</summary>
public sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";

    /// <summary>Base64 of 32 random bytes; encrypts every key at rest (AES-256-GCM). Secret provider only (OPS-08).</summary>
    public string? MasterKey { get; set; }

    /// <summary>Written next to each encrypted key so a later master-key rotation can keep reading old keys.</summary>
    public string MasterKeyId { get; set; } = "k1";

    /// <summary>Retired master keys still able to decrypt: id → base64 key.</summary>
    public Dictionary<string, string> PreviousMasterKeys { get; set; } = [];

    public int KeyLifetimeDays { get; set; } = 90;

    /// <summary>The next key is created this long before it activates, so every instance has loaded it first.</summary>
    public int ActivationLeadHours { get; set; } = 48;

    /// <summary>How often each instance reloads the shared key ring.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>
    /// Delay before a key created by an on-demand rotation activates; at least two refreshes outside Development/Test
    /// so every instance knows the key before it protects anything with it.
    /// </summary>
    public int RotationDelaySeconds { get; set; } = 900;

    internal byte[]? MasterKeyBytes() => Decode(MasterKey);

    internal static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <summary>Startup violations; secrets are never echoed.</summary>
    public IEnumerable<string> Validate(GameServerOptions gameServer)
    {
        var master = MasterKeyBytes();
        if (master is null)
        {
            if (!gameServer.AllowsDevelopmentTools)
            {
                yield return $"[dataprotection] DataProtection:MasterKey is not set in '{gameServer.DeploymentEnvironment}'. " +
                    "Fix: provide 32 random bytes (base64) through the secret provider (DataProtection__MasterKey); keys must be encrypted at rest (OPS-04).";
            }
        }
        else if (master.Length != 32)
        {
            yield return "[dataprotection] DataProtection:MasterKey must be base64 of exactly 32 bytes (AES-256).";
        }

        if (PreviousMasterKeys.Any(p => Decode(p.Value) is not { Length: 32 }))
        {
            yield return "[dataprotection] every DataProtection:PreviousMasterKeys entry must be base64 of exactly 32 bytes.";
        }

        if (string.IsNullOrWhiteSpace(MasterKeyId) || PreviousMasterKeys.ContainsKey(MasterKeyId))
        {
            yield return "[dataprotection] DataProtection:MasterKeyId must be set and differ from every previous master key id.";
        }

        if (RefreshMinutes is < 1 or > 60)
        {
            yield return "[dataprotection] DataProtection:RefreshMinutes must be 1..60.";
        }

        if (KeyLifetimeDays is < 7 or > 365)
        {
            yield return "[dataprotection] DataProtection:KeyLifetimeDays must be 7..365.";
        }

        if (ActivationLeadHours * 60 < 2 * RefreshMinutes || ActivationLeadHours >= KeyLifetimeDays * 24)
        {
            yield return "[dataprotection] DataProtection:ActivationLeadHours must cover at least two refreshes and be shorter than the key lifetime.";
        }

        // Data Protection may pick a key up to 5 minutes before it activates: two refreshes plus that skew.
        if (RotationDelaySeconds < 0 || (!gameServer.AllowsDevelopmentTools && RotationDelaySeconds < (2 * RefreshMinutes * 60) + 300))
        {
            yield return "[dataprotection] DataProtection:RotationDelaySeconds must be at least two refreshes plus 5 minutes (2 × RefreshMinutes × 60 + 300) outside Development/Test.";
        }
    }
}
