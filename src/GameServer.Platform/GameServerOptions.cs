namespace GameServer.Platform;

public enum StoreKind
{
    InMemory,
    Couchbase,
}

/// <summary>
/// Deployment options bound from the <c>GameServer</c> config section. Defaults are the
/// starting values of Appendix B of the requirements (TBD-19, TBD-13, TBD-07).
/// </summary>
public sealed class GameServerOptions
{
    public const string SectionName = "GameServer";

    /// <summary>Constant of the deployment (ARC-10). Required.</summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>Logical environment id, e.g. <c>prod</c>, <c>dev</c> (ARC-09). Required.</summary>
    public string EnvironmentId { get; set; } = string.Empty;

    /// <summary>Development, Staging or Production (OPS-01).</summary>
    public string DeploymentEnvironment { get; set; } = "Development";

    /// <summary>Root of the game config files, relative to the content root (CFG-01).</summary>
    public string ConfigPath { get; set; } = "config";

    public StoreKind Store { get; set; } = StoreKind.InMemory;

    /// <summary>Public endpoints allowed (SEC-09). Arrays are replaced, not merged, by config binding.</summary>
    public string[] PublicEndpointAllowlist { get; set; } = ["/v1/time"];

    /// <summary>
    /// Configuration keys holding secrets (OPS-08). Outside Development/Test they must not come from a JSON file
    /// (appsettings committed to source); use environment variables or a secret provider.
    /// </summary>
    public string[] SecretKeys { get; set; } =
    [
        "Identity:GuestPepper",
        "Couchbase:Password",
        "Iap:Google:ServiceAccountPrivateKeyPem",
        "DataProtection:MasterKey",
        "Gm:Oidc:ClientSecret",
    ];

    public TransactionSettings Transactions { get; set; } = new();

    public CommandSettings Commands { get; set; } = new();

    public OutboxSettings Outbox { get; set; } = new();

    public LimitSettings Limits { get; set; } = new();

    public HostingSettings Hosting { get; set; } = new();

    public GateSettings Gate { get; set; } = new();

    public bool IsProduction => string.Equals(DeploymentEnvironment, "Production", StringComparison.OrdinalIgnoreCase);

    public bool IsDevelopment => string.Equals(DeploymentEnvironment, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Automated test deployments; like Development, they may use test authentication and the in-memory store.</summary>
    public bool IsTest => string.Equals(DeploymentEnvironment, "Test", StringComparison.OrdinalIgnoreCase);

    public bool AllowsDevelopmentTools => IsDevelopment || IsTest;
}

/// <summary>Which APIs this host maps; lets the GM API run in a separate host later (OPS-10).</summary>
public sealed class HostingSettings
{
    public bool MapPlayerApi { get; set; } = true;

    public bool MapGmApi { get; set; } = true;

    /// <summary>Reverse-proxy headers (OPS-02); trusted only from the listed proxies/networks.</summary>
    public ForwardedHeadersSettings ForwardedHeaders { get; set; } = new();

    /// <summary>Graceful shutdown drain (OPS-03): in-flight requests get this long after SIGTERM.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// After SIGTERM, keep serving (readiness already 503) this long before the server stops accepting, so load
    /// balancers see the 503 and stop routing first. Counted inside <see cref="ShutdownTimeoutSeconds"/>.
    /// </summary>
    public int DrainDelaySeconds { get; set; } = 5;
}

public sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; set; }

    /// <summary>IP addresses of trusted proxies.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>CIDR networks of trusted proxies, e.g. <c>10.0.0.0/8</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Number of proxy hops to honour.</summary>
    public int ForwardLimit { get; set; } = 1;
}

public sealed class TransactionSettings
{
    /// <summary>Transaction expiration (PERF-02, DAT-04): 10 s, overriding the SDK default of 15 s.</summary>
    public int ExpirationSeconds { get; set; } = 10;

    /// <summary>KV durable timeout of the commit step (PERF-02).</summary>
    public int KvDurableTimeoutSeconds { get; set; } = 5;
}

public sealed class CommandSettings
{
    /// <summary>Replay window (CMD-07).</summary>
    public int ReplayWindowHours { get; set; } = 24;

    /// <summary>Per-player queued commands per instance (CMD-06); concurrency is always 1.</summary>
    public int PerPlayerQueueLimit { get; set; } = 2;

    public int ReceiptMaxBytes { get; set; } = 4 * 1024;

    public int ResultCompactMaxBytes { get; set; } = 512;

    public int MaxAggregatesPerGame { get; set; } = 16;
}

public sealed class OutboxSettings
{
    public bool InlineRelayEnabled { get; set; } = true;

    public bool SweeperEnabled { get; set; } = true;

    /// <summary>Sweeper period (OPS-05).</summary>
    public int SweepIntervalSeconds { get; set; } = 30;

    /// <summary>Receipts younger than this are left to the inline relay.</summary>
    public int MinAgeSeconds { get; set; } = 10;

    public int BatchSize { get; set; } = 100;

    /// <summary>Failed relays before a receipt is dead-lettered.</summary>
    public int MaxRelayAttempts { get; set; } = 10;

    /// <summary>First retry delay; doubles per attempt up to <see cref="MaxRetryDelaySeconds"/>.</summary>
    public int RetryDelaySeconds { get; set; } = 30;

    public int MaxRetryDelaySeconds { get; set; } = 3600;
}

public sealed class LimitSettings
{
    public int PlayerBodyBytes { get; set; } = 64 * 1024;

    public int GmBodyBytes { get; set; } = 1024 * 1024;

    public int PlayerTimeoutSeconds { get; set; } = 10;

    public int GmTimeoutSeconds { get; set; } = 30;

    /// <summary>In-flight commands per instance (PERF-04).</summary>
    public int CommandConcurrency { get; set; } = 200;

    /// <summary>In-flight GM requests per instance (PERF-04).</summary>
    public int GmConcurrency { get; set; } = 50;

    /// <summary>Queue length = factor × permits.</summary>
    public int QueueFactor { get; set; } = 2;

    /// <summary>Maximum GetConfig bundle size (CFG-03, API-06).</summary>
    public int ConfigBundleBytes { get; set; } = 1024 * 1024;
}

/// <summary>Section <c>GameServer:Gate</c> (GATE-01, GATE-02, PERF-08).</summary>
public sealed class GateSettings
{
    /// <summary>Per-instance cache of the gate document; at most 10 s (PERF-08), so a toggle applies everywhere within it.</summary>
    public int CacheSeconds { get; set; } = 10;
}
