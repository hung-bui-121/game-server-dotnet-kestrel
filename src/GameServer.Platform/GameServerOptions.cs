using GameServer.Platform.Jobs;
using GameServer.Platform.Privacy;

namespace GameServer.Platform;

public enum StoreKind
{
    InMemory,
    Couchbase,

    /// <summary>Development-only store backed by a folder of JSON files (local flavor, slice 011).</summary>
    File,
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

    /// <summary>
    /// <c>GameServer:ConfigCompression</c> (FG-019, FR-050..FR-052): compress <c>GetConfig</c> responses with Brotli or gzip for
    /// clients whose <c>Accept-Encoding</c> asks for it (no other route is ever compressed). On by default; <c>false</c> is the
    /// rollback switch and sends today's bytes and headers (no <c>Content-Encoding</c>, no <c>Vary</c>).
    /// <see cref="LimitSettings.ConfigBundleBytes"/> limits the uncompressed response either way.
    /// </summary>
    public bool ConfigCompression { get; set; } = true;

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
        "Identity:Providers:PlayGames:ClientSecret",
    ];

    public TransactionSettings Transactions { get; set; } = new();

    public CommandSettings Commands { get; set; } = new();

    public OutboxSettings Outbox { get; set; } = new();

    public LimitSettings Limits { get; set; } = new();

    public HostingSettings Hosting { get; set; } = new();

    public GateSettings Gate { get; set; } = new();

    /// <summary>Lease job runner (JOB-01, OPS-05).</summary>
    public JobSettings Jobs { get; set; } = new();

    /// <summary>Account purge limits (PRIV-01).</summary>
    public PurgeSettings Purge { get; set; } = new();

    /// <summary>Per-request event (FR-080..FR-086), section <c>GameServer:RequestLog</c>.</summary>
    public RequestLogSettings RequestLog { get; set; } = new();

    public bool IsProduction => string.Equals(DeploymentEnvironment, "Production", StringComparison.OrdinalIgnoreCase);

    public bool IsDevelopment => string.Equals(DeploymentEnvironment, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Automated test deployments; like Development, they may use test authentication and the in-memory store.</summary>
    public bool IsTest => string.Equals(DeploymentEnvironment, "Test", StringComparison.OrdinalIgnoreCase);

    public bool AllowsDevelopmentTools => IsDevelopment || IsTest;

    /// <summary>
    /// The single decision for the Dev profile (FR-006): startup validation, request pipeline and command runner allow
    /// Dev endpoints and commands only when true. Today equals <see cref="AllowsDevelopmentTools"/> (Development/Test); a
    /// future Staging switch (PCR platform §17 R13) would extend this property only, never for Production.
    /// </summary>
    public bool AllowsDevProfile => AllowsDevelopmentTools;

    /// <summary>Bounds of the background work settings (data-model.md); checked at startup with the module registry.</summary>
    public IEnumerable<string> ValidateBackgroundWork() => Jobs.Validate(Transactions).Concat(Purge.Validate());
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
    /// <summary>The documented ranges (FR-032, research R9); every default is inside them.</summary>
    internal const int MinReplayWindowHours = 1;
    internal const int MaxReplayWindowHours = 168;
    internal const int MinPerPlayerQueueLimit = 0;
    internal const int MaxPerPlayerQueueLimit = 16;
    internal const int MinReceiptMaxBytes = 1024;
    internal const int MaxReceiptMaxBytes = 64 * 1024;
    internal const int MinResultCompactMaxBytes = 64;
    internal const int MaxResultCompactMaxBytes = 16 * 1024;
    internal const int MinMaxAggregatesPerGame = 1;
    internal const int MaxMaxAggregatesPerGame = 16;

    /// <summary>Receipt bytes besides the base64 result that the largest allowed result must leave room for.</summary>
    internal const int ReceiptOverheadBytes = 1024;

    /// <summary>Replay window (CMD-07).</summary>
    public int ReplayWindowHours { get; set; } = 24;

    /// <summary>Per-player queued commands per instance (CMD-06); concurrency is always 1.</summary>
    public int PerPlayerQueueLimit { get; set; } = 2;

    public int ReceiptMaxBytes { get; set; } = 4 * 1024;

    public int ResultCompactMaxBytes { get; set; } = 512;

    public int MaxAggregatesPerGame { get; set; } = 16;

    /// <summary>
    /// <see cref="MaxAggregatesPerGame"/> when it is in range, else the DAT-09 maximum: an out-of-range value is reported by
    /// <see cref="Validate"/> once and never used for the count check.
    /// </summary>
    internal int EffectiveMaxAggregatesPerGame => IsInRange(MaxAggregatesPerGame, MinMaxAggregatesPerGame, MaxMaxAggregatesPerGame)
        ? MaxAggregatesPerGame
        : MaxMaxAggregatesPerGame;

    /// <summary>One line per value outside its documented range (FR-032); empty when valid.</summary>
    public IEnumerable<string> Validate()
    {
        var lines = new List<string>();
        AddRange(lines, nameof(ReplayWindowHours), ReplayWindowHours, MinReplayWindowHours, MaxReplayWindowHours, "CMD-07");
        AddRange(lines, nameof(PerPlayerQueueLimit), PerPlayerQueueLimit, MinPerPlayerQueueLimit, MaxPerPlayerQueueLimit, "CMD-06");
        AddRange(lines, nameof(ReceiptMaxBytes), ReceiptMaxBytes, MinReceiptMaxBytes, MaxReceiptMaxBytes, "CMD-03");
        AddRange(lines, nameof(ResultCompactMaxBytes), ResultCompactMaxBytes, MinResultCompactMaxBytes, MaxResultCompactMaxBytes, "CMD-03");

        // The largest allowed result must fit its own receipt (stored as base64).
        var needed = (4L * ((ResultCompactMaxBytes + 2L) / 3)) + ReceiptOverheadBytes;
        if (needed > ReceiptMaxBytes)
        {
            lines.Add(
                $"[deployment] GameServer:Commands:ResultCompactMaxBytes {ResultCompactMaxBytes} needs {needed} receipt bytes (base64 + {ReceiptOverheadBytes}) " +
                $"but ReceiptMaxBytes is {ReceiptMaxBytes}. Fix: raise ReceiptMaxBytes or lower ResultCompactMaxBytes.");
        }

        AddRange(lines, nameof(MaxAggregatesPerGame), MaxAggregatesPerGame, MinMaxAggregatesPerGame, MaxMaxAggregatesPerGame, "DAT-09");
        return lines;
    }

    private static bool IsInRange(int value, int min, int max) => value >= min && value <= max;

    private static void AddRange(List<string> lines, string name, int value, int min, int max, string rule)
    {
        if (!IsInRange(value, min, max))
        {
            lines.Add($"[deployment] GameServer:Commands:{name} is {value}; allowed {min}..{max} ({rule}).");
        }
    }
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

/// <summary>
/// Section <c>GameServer:RequestLog</c> (FR-080..FR-086): one event per HTTP request, category <c>GameServer.Requests</c>.
/// The level per deployment comes from <c>Observability:Logging:LevelOverrides:GameServer.Requests</c>.
/// </summary>
public sealed class RequestLogSettings
{
    public const int MaxExcludedPaths = 50;
    public const int MaxExcludedPathLength = 200;

    /// <summary>
    /// Null = on in Development/Test (local, dev, test hosts), off in Staging/Production (beta, uat, live). An explicit
    /// <c>true</c> outside Development/Test is allowed and logged once at startup with the volume estimate.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Paths never logged in addition to <c>/health/live</c> and <c>/health/ready</c> (segment prefix, case-insensitive).
    /// Arrays are replaced, not merged, by config binding.
    /// </summary>
    public string[] ExcludedPaths { get; set; } = [];

    public bool IsEnabled(GameServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Enabled ?? options.AllowsDevelopmentTools;
    }

    /// <summary>Startup-report lines for invalid values; empty when valid.</summary>
    public IEnumerable<string> Validate()
    {
        var paths = ExcludedPaths;
        if (paths.Length > MaxExcludedPaths)
        {
            yield return $"[request-log] GameServer:RequestLog:ExcludedPaths has {paths.Length} entries; at most {MaxExcludedPaths} are allowed.";
        }

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            if (string.IsNullOrEmpty(path) || path[0] != '/')
            {
                yield return $"[request-log] GameServer:RequestLog:ExcludedPaths:{i} '{path}' must start with '/'.";
            }
            else if (path.Length > MaxExcludedPathLength)
            {
                yield return $"[request-log] GameServer:RequestLog:ExcludedPaths:{i} has {path.Length} characters; at most {MaxExcludedPathLength} are allowed.";
            }
            else if (path.Contains('?', StringComparison.Ordinal))
            {
                yield return $"[request-log] GameServer:RequestLog:ExcludedPaths:{i} '{path}' must be a path without a query string ('?').";
            }
        }
    }
}

/// <summary>Section <c>GameServer:Gate</c> (GATE-01, GATE-02, PERF-08).</summary>
public sealed class GateSettings
{
    /// <summary>Per-instance cache of the gate document; at most 10 s (PERF-08), so a toggle applies everywhere within it.</summary>
    public int CacheSeconds { get; set; } = 10;
}
