namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>The local test cluster from the <c>COUCHBASE_*</c> variables (same defaults as <see cref="CouchbaseFixture"/>).</summary>
internal static class IntegrationCluster
{
    /// <summary>
    /// <c>Couchbase:ProvisioningTimeoutSeconds</c> of the test hosts (range 30..7200). Above the 600 s default: under full-suite
    /// load the index service queues the builds of every class, and each index of an empty scope took 23-88 s (measured
    /// 2026-10-30), so a full scope can need more than 10 minutes.
    /// </summary>
    public const int ProvisioningTimeoutSeconds = 1800;

    /// <summary>Port of the cluster manager REST API (RBAC users).</summary>
    private const int ManagementPort = 8091;

    public static string? ConnectionString => Environment.GetEnvironmentVariable(CouchbaseFixture.ConnectionStringVariable);

    public static bool Available => !string.IsNullOrWhiteSpace(ConnectionString);

    public static string SkipReason => $"{CouchbaseFixture.ConnectionStringVariable} not set: provisioning tests need a real Couchbase (EE 7.6).";

    public static string AdminUser => Environment.GetEnvironmentVariable("COUCHBASE_USERNAME") ?? "Administrator";

    public static string AdminPassword => Environment.GetEnvironmentVariable("COUCHBASE_PASSWORD") ?? "password";

    public static string Bucket => Environment.GetEnvironmentVariable("COUCHBASE_BUCKET") ?? "gameserver-it";

    /// <summary><c>http://&lt;first seed host&gt;:8091</c> (plain <c>couchbase://</c> clusters only; TLS variants are opt-in elsewhere).</summary>
    public static Uri ManagementUri
    {
        get
        {
            var seeds = ConnectionString!.Split("://", 2)[1].Split('?', 2)[0];
            var host = seeds.Split(',')[0].Split(':')[0];
            return new UriBuilder(Uri.UriSchemeHttp, host, ManagementPort).Uri;
        }
    }

    /// <summary>Host settings that point a sample host at the test bucket and the scope <c>g_&lt;gameId&gt;</c>.</summary>
    public static Dictionary<string, string?> HostSettings(string gameId, string username, string password) => new()
    {
        ["GameServer:GameId"] = gameId,
        ["Couchbase:ConnectionString"] = ConnectionString,
        ["Couchbase:Username"] = username,
        ["Couchbase:Password"] = password,
        ["Couchbase:BucketName"] = Bucket,
        ["Couchbase:ProvisioningTimeoutSeconds"] = ProvisioningTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static CouchbaseOptions AdminOptions() => new()
    {
        ConnectionString = ConnectionString!,
        Username = AdminUser,
        Password = AdminPassword,
        BucketName = Bucket,
    };
}
