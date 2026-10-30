namespace GameServer.Platform.Couchbase;

/// <summary>
/// Connection settings (section <c>Couchbase</c>). Secrets come from the environment or a secret provider,
/// never from committed appsettings (OPS-08). Ranges are checked at startup by <see cref="CouchbaseSettingsCheck"/>
/// (data-model §4); every value maps 1:1 to the SDK's <c>ClusterOptions</c> (<see cref="CouchbaseClusterOptionsFactory"/>).
/// </summary>
public sealed class CouchbaseOptions
{
    public const string SectionName = "Couchbase";

    public const string Scheme = "couchbase://";

    public const string TlsScheme = "couchbases://";

    /// <summary><c>couchbase://h1[,h2…][?params]</c>, or <c>couchbases://…</c> for TLS. Required: no localhost default (D10).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Bucket of the deployment environment (DAT-01).</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// Use <c>AttemptContext.GetMulti</c> for bundle reads (DAT-11). Off by default until TST-04 (V23-T1) and the store
    /// conformance suite pass on a real cluster: GetMulti returns no write handle in SDK 3.10, so every changed aggregate
    /// is re-read before replace and a concurrent commit aborts the attempt (review M3).
    /// </summary>
    public bool UseGetMulti { get; set; }

    /// <summary>Create scope, collections and indexes at startup. Defaults to true only in Development (DAT-07).</summary>
    public bool? ProvisionOnStartup { get; set; }

    /// <summary>Client timeout of one KV operation (SDK default 2.5 s). The durable commit timeout is <c>GameServer:Transactions:KvDurableTimeoutSeconds</c>.</summary>
    public int KvTimeoutMs { get; set; } = 2500;

    /// <summary>Client timeout of one query (GM pages, jobs); the player API never queries. Below the SDK's 75 s on purpose.</summary>
    public int QueryTimeoutMs { get; set; } = 20000;

    /// <summary>
    /// PEM or DER file of the CA that signs the cluster certificates (relative paths resolve against the content root). With
    /// <c>couchbases://</c> it becomes the only trusted root for KV and HTTP; ignored without TLS.
    /// </summary>
    public string? TrustCertificatePath { get; set; }

    /// <summary>Snappy compression of KV bodies.</summary>
    public bool Compression { get; set; } = true;

    /// <summary>Smallest body considered for compression (SDK default 32).</summary>
    public int CompressionMinSizeBytes { get; set; } = 32;

    /// <summary>Minimum KV connections per node (SDK default 2).</summary>
    public int NumKvConnections { get; set; } = 2;

    /// <summary>Maximum KV connections per node (SDK default 5).</summary>
    public int MaxKvConnections { get; set; } = 5;

    /// <summary>Upper bound of one <c>--provision</c> run (index builds on large collections can be slow).</summary>
    public int ProvisioningTimeoutSeconds { get; set; } = 600;

    /// <summary>True for the <c>couchbases://</c> scheme (the SDK enables TLS from the scheme).</summary>
    public bool UsesTls => ConnectionString.TrimStart().StartsWith(TlsScheme, StringComparison.OrdinalIgnoreCase);

    public static string ScopeName(string gameId) => "g_" + gameId;
}
