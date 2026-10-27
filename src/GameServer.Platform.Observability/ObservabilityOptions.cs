namespace GameServer.Platform.Observability;

/// <summary>Section <c>Observability</c> (LOG-01..06, OBS-04).</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// Instance id in log file names and telemetry (LOG-03). Defaults to <c>HOSTNAME</c> (stable pod name) or the machine
    /// name; set it explicitly when several processes share one machine and log directory.
    /// </summary>
    public string? InstanceId { get; set; }

    public LoggingSettings Logging { get; set; } = new();

    public OtlpSettings Otlp { get; set; } = new();

    /// <summary>Readiness results are shared for this long (bounds store probes from the Public endpoint).</summary>
    public int ReadyCacheMilliseconds { get; set; } = 1000;

    public string ResolveInstanceId() =>
        !string.IsNullOrWhiteSpace(InstanceId) ? InstanceId
        : Environment.GetEnvironmentVariable("HOSTNAME") is { Length: > 0 } host ? host
        : Environment.MachineName;
}

public sealed class LoggingSettings
{
    public bool FileEnabled { get; set; } = true;

    /// <summary>Log directory (persistent volume or shipped by a sidecar, LOG-03); relative to the content root.</summary>
    public string Directory { get; set; } = "logs";

    public string MinimumLevel { get; set; } = "Information";

    /// <summary>
    /// Minimum level per source-context prefix (keys case-insensitive); configuration entries are merged over these
    /// defaults. The Couchbase SDK transaction code logs ~6 Information lines per transaction (attempt, ATR, cleanup —
    /// measured 2026-10-27), ~95 % of all lines under load, so it starts at Warning; SDK bootstrap/topology lines stay.
    /// <c>ExecuteApplicationLambda</c> is an SDK transaction category without a namespace. A level below the
    /// Microsoft <c>Logging:LogLevel</c> filter is still cut by that filter first.
    /// </summary>
    public Dictionary<string, string> LevelOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.AspNetCore"] = "Warning",
        ["Couchbase.Client.Transactions"] = "Warning",
        ["ExecuteApplicationLambda"] = "Warning",
    };

    /// <summary>LOG-02: 100 MiB per file.</summary>
    public long FileSizeLimitBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>LOG-02: at most 30 files.</summary>
    public int RetainedFileCountLimit { get; set; } = 30;

    /// <summary>Optional age limit of retained files (TBD-08 retention policy).</summary>
    public int? RetainedFileTimeLimitDays { get; set; }

    /// <summary>LOG-06: bounded async queue; entries beyond it are dropped and counted, never blocking requests.</summary>
    public int BufferSize { get; set; } = 10_000;

    /// <summary>Readiness is Degraded when the log directory has less free space than this.</summary>
    public long LowDiskBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Readiness is Degraded for this long after a sink error.</summary>
    public int SinkErrorWindowSeconds { get; set; } = 300;
}

public sealed class OtlpSettings
{
    /// <summary>OTLP collector endpoint; unset = no exporter (instrumentation stays cheap).</summary>
    public string? Endpoint { get; set; }

    /// <summary><c>grpc</c> or <c>http/protobuf</c>.</summary>
    public string Protocol { get; set; } = "grpc";
}
