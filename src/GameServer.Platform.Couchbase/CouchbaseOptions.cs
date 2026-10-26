namespace GameServer.Platform.Couchbase;

/// <summary>
/// Connection settings (section <c>Couchbase</c>). Secrets come from the environment or a secret provider,
/// never from committed appsettings (OPS-08).
/// </summary>
public sealed class CouchbaseOptions
{
    public const string SectionName = "Couchbase";

    public string ConnectionString { get; set; } = "couchbase://localhost";

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

    public static string ScopeName(string gameId) => "g_" + gameId;
}
