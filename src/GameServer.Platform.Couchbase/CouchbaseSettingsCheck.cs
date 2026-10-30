using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Startup check of the <c>Couchbase</c> section (FR-013..FR-015, data-model §4): required keys, scheme and hosts, ranges, the
/// CA file, the TLS rule (Production error, Staging warning) and warnings for the legacy keys of the owner's
/// <c>database_settings.json</c>. Every message names the key and the allowed range or the fix; none echoes the connection string.
/// </summary>
internal sealed class CouchbaseSettingsCheck(CouchbaseOptions options, GameServerOptions gameServer, IConfiguration configuration, string contentRoot)
    : IStartupCheck
{
    public const int MinTimeoutMs = 100;
    public const int MaxCompressionMinSizeBytes = 20 * 1024 * 1024;
    public const int MaxNumKvConnections = 32;
    public const int MaxKvConnectionsLimit = 64;
    public const int MinProvisioningTimeoutSeconds = 30;
    public const int MaxProvisioningTimeoutSeconds = 7200;

    private const string Section = CouchbaseOptions.SectionName + ":";
    private const string MoxiAdvice = "Moxi no longer exists; the SDK bootstraps from Couchbase:ConnectionString on the standard ports";
    private const string BucketTypeAdvice = "not an application setting; transactions need a Couchbase bucket, created by ops";
    private const string DbCacheAdvice = "dropped: nothing reads it, and memcached buckets cannot run transactions";
    private const string ServerIpAdvice = "renamed: use Couchbase:ConnectionString (couchbase://ip1,ip2; couchbases:// for TLS)";
    private const string OperationTimeoutAdvice = "renamed: use Couchbase:KvTimeoutMs";
    private const string CompressThresholdAdvice = "renamed: use Couchbase:CompressionMinSizeBytes";

    /// <summary>Reference keys found in configuration are warned about, never refused (research R5, D12). Lookup is case-insensitive.</summary>
    internal static readonly IReadOnlyList<(string Key, string Advice)> LegacyKeys =
    [
        ("Couchbase:MoxiPort", MoxiAdvice),
        ("dbbase:moxiPort", MoxiAdvice),
        ("Couchbase:BucketType", BucketTypeAdvice),
        ("dbbase:bucketType", BucketTypeAdvice),
        ("Couchbase:DbCache", DbCacheAdvice),
        ("dbcache", DbCacheAdvice),
        ("Couchbase:ServerIp", ServerIpAdvice),
        ("dbbase:serverIp", ServerIpAdvice),
        ("Couchbase:OperationTimeOut", OperationTimeoutAdvice),
        ("dbbase:operationTimeOut", OperationTimeoutAdvice),
        ("Couchbase:CompressThreshold", CompressThresholdAdvice),
        ("dbbase:compressThreshold", CompressThresholdAdvice),
        ("dbbase:bucketName", "renamed: use Couchbase:BucketName"),
        ("dbbase:localFolder", "renamed: use FileStore:Folder (local flavor only)"),
    ];

    public void Check(StartupCheckResult result)
    {
        Required(result, nameof(CouchbaseOptions.ConnectionString), options.ConnectionString, "couchbase://host[,host…] (couchbases:// for TLS)");
        Required(result, nameof(CouchbaseOptions.Username), options.Username, "the user of this deployment");
        Required(result, nameof(CouchbaseOptions.BucketName), options.BucketName, "the bucket of this environment (DAT-01)");
        // Required in every environment: no cluster accepts an empty password, and an SDK authentication timeout is a worse
        // message than this one (Development reads it from the user environment variable of the dev-couchbase profile).
        if (string.IsNullOrEmpty(options.Password))
        {
            result.Errors.Add(
                $"[couchbase] Couchbase:Password is not set in '{gameServer.DeploymentEnvironment}'. Fix: set the environment variable " +
                "Couchbase__Password (never in a committed file, OPS-08).");
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString) && CheckConnectionString(result))
        {
            CheckTls(result);
        }

        CheckRanges(result);
        CheckTrustCertificate(result);
        foreach (var (key, advice) in LegacyKeys.Where(legacy => configuration.GetSection(legacy.Key).Exists()))
        {
            result.Warnings.Add($"[couchbase] legacy setting {key} is ignored: {advice} (FR-015).");
        }
    }

    private static void Required(StartupCheckResult result, string name, string value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Errors.Add($"[couchbase] {Section}{name} is not set. Fix: set {expected}, e.g. through the environment variable Couchbase__{name}.");
        }
    }

    /// <summary>True when the scheme is valid (the TLS rule only makes sense then).</summary>
    private bool CheckConnectionString(StartupCheckResult result)
    {
        var value = options.ConnectionString.Trim();
        var scheme = value.StartsWith(CouchbaseOptions.TlsScheme, StringComparison.OrdinalIgnoreCase) ? CouchbaseOptions.TlsScheme
            : value.StartsWith(CouchbaseOptions.Scheme, StringComparison.OrdinalIgnoreCase) ? CouchbaseOptions.Scheme
            : null;
        if (scheme is null)
        {
            var separator = value.IndexOf("://", StringComparison.Ordinal);
            var found = separator > 0 ? $"'{value[..separator]}://'" : "none";
            result.Errors.Add(
                $"[couchbase] Couchbase:ConnectionString must start with {CouchbaseOptions.Scheme} or {CouchbaseOptions.TlsScheme} (TLS) " +
                $"followed by at least one host, e.g. couchbase://cb1,cb2 (scheme found: {found}).");
            return false;
        }

        var hosts = value[scheme.Length..].Split('?')[0].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Length == 0)
        {
            result.Errors.Add($"[couchbase] Couchbase:ConnectionString has no host after {scheme}. Fix: list at least one node, e.g. {scheme}cb1,cb2.");
        }

        return true;
    }

    /// <summary>FR-014, SEC-01: TLS is required in Production and expected in Staging; Development and Test may use plain connections.</summary>
    private void CheckTls(StartupCheckResult result)
    {
        if (options.UsesTls)
        {
            return;
        }

        var message =
            $"[couchbase] Couchbase:ConnectionString uses {CouchbaseOptions.Scheme} (no TLS) in '{gameServer.DeploymentEnvironment}'. " +
            $"Fix: use {CouchbaseOptions.TlsScheme}, with Couchbase:TrustCertificatePath for a private CA (TLS is required in Production, SEC-01).";
        if (gameServer.IsProduction)
        {
            result.Errors.Add(message);
        }
        else if (string.Equals(gameServer.DeploymentEnvironment, DeploymentEnvironments.Staging, StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add(message);
        }
    }

    private void CheckRanges(StartupCheckResult result)
    {
        var limits = gameServer.Limits;
        var maxKv = 1000 * Math.Min(limits.PlayerTimeoutSeconds, limits.GmTimeoutSeconds);
        Range(result, nameof(CouchbaseOptions.KvTimeoutMs), options.KvTimeoutMs, MinTimeoutMs, maxKv,
            " = 1000 x min(GameServer:Limits:PlayerTimeoutSeconds, GameServer:Limits:GmTimeoutSeconds)");
        Range(result, nameof(CouchbaseOptions.QueryTimeoutMs), options.QueryTimeoutMs, MinTimeoutMs, 1000 * limits.GmTimeoutSeconds,
            " = 1000 x GameServer:Limits:GmTimeoutSeconds");
        Range(result, nameof(CouchbaseOptions.CompressionMinSizeBytes), options.CompressionMinSizeBytes, 0, MaxCompressionMinSizeBytes);
        Range(result, nameof(CouchbaseOptions.NumKvConnections), options.NumKvConnections, 1, MaxNumKvConnections);

        // The maximum is bounded by the minimum only when the minimum is valid itself (one error per cause).
        var numValid = options.NumKvConnections is >= 1 and <= MaxNumKvConnections;
        Range(result, nameof(CouchbaseOptions.MaxKvConnections), options.MaxKvConnections, numValid ? options.NumKvConnections : 1, MaxKvConnectionsLimit,
            string.Empty, numValid ? " (lower bound = Couchbase:NumKvConnections)" : string.Empty);
        Range(result, nameof(CouchbaseOptions.ProvisioningTimeoutSeconds), options.ProvisioningTimeoutSeconds,
            MinProvisioningTimeoutSeconds, MaxProvisioningTimeoutSeconds);
    }

    private static void Range(StartupCheckResult result, string name, int value, int min, int max, string maxNote = "", string minNote = "")
    {
        if (value < min || value > max)
        {
            result.Errors.Add(
                $"[couchbase] {Section}{name} {value.ToString(CultureInfo.InvariantCulture)} is out of range " +
                $"(allowed {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}{maxNote}){minNote}.");
        }
    }

    private void CheckTrustCertificate(StartupCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(options.TrustCertificatePath))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString) && !options.UsesTls)
        {
            result.Warnings.Add(
                $"[couchbase] Couchbase:TrustCertificatePath is ignored: the connection string does not use {CouchbaseOptions.TlsScheme} (FR-013).");
        }

        // The path is resolved inside the try: an invalid path (illegal characters, too long) is reported, not thrown (FR-009).
        var path = options.TrustCertificatePath;
        try
        {
            path = CouchbaseTrustedRoots.ResolvePath(contentRoot, path);
            CouchbaseTrustedRoots.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result.Errors.Add($"[couchbase] Couchbase:TrustCertificatePath '{path}' is not readable ({ex.Message}). Fix: point it to the root CA file (PEM or DER).");
        }
        catch (CryptographicException)
        {
            result.Errors.Add($"[couchbase] Couchbase:TrustCertificatePath '{path}' is not an X.509 certificate. Fix: use the root CA certificate as PEM or DER.");
        }
    }
}
