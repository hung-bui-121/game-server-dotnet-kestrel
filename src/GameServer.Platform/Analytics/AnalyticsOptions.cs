using Microsoft.Extensions.Options;

namespace GameServer.Platform.Analytics;

/// <summary>
/// Section <c>GameServer:Analytics</c> (OPS-01, FR-031): its own options type so it is validated on start (merged into the
/// serve startup report) and never by <c>--provision</c> (FR-030). No key holds a secret (FR-034).
/// </summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "GameServer:Analytics";
    public const string EnabledKey = SectionName + ":Enabled";
    public const string TimeZoneKey = SectionName + ":TimeZone";
    public const string DirectoryKey = SectionName + ":Directory";

    /// <summary>Must be the same on every instance of a deployment (FR-039).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>IANA zone of the deployment; required in Staging/Production when analytics is active.</summary>
    public string? TimeZone { get; set; }

    /// <summary>File sink directory, relative to the content root; separate from the log directory.</summary>
    public string Directory { get; set; } = "analytics";

    /// <summary>Records waiting for the dispatcher (memory bound, PERF-01).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>How long an outbox delivery waits for every sink before the receipt is left to the sweeper (FR-013).</summary>
    public int DeliveryTimeoutMs { get; set; } = 500;

    /// <summary>Players remembered per local day per instance for the resume rule (Identity, FR-018).</summary>
    public int ResumeTrackerCapacity { get; set; } = 200_000;

    /// <summary>Readiness is Degraded when the analytics directory has less free space than this.</summary>
    public long LowDiskBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Readiness is Degraded for this long after a sink error.</summary>
    public int ErrorWindowSeconds { get; set; } = 300;

    /// <summary>Optional: delete this instance's own files older than this many UTC days (FR-029). Null = never.</summary>
    public int? RetainLocalDays { get; set; }
}

/// <summary>
/// Whether analytics is active: <see cref="AnalyticsOptions.Enabled"/> and at least one <see cref="IAnalyticsSink"/>
/// registered (SD5). The one place that decides it, shared by the validator, the pipeline, the consumer and login analytics.
/// The sink count comes from the service registrations, so deciding never constructs a sink.
/// </summary>
public sealed class AnalyticsActivation(int sinkCount)
{
    public int SinkCount { get; } = sinkCount;

    public bool IsActive(AnalyticsOptions options) => options.Enabled && SinkCount > 0;
}

/// <summary>Startup validation of <see cref="AnalyticsOptions"/> (FR-021, FR-031); messages name the key and the allowed range.</summary>
public sealed class AnalyticsOptionsValidator(AnalyticsActivation activation, GameServerOptions gameServer) : IValidateOptions<AnalyticsOptions>
{
    private const string Prefix = "[analytics] ";

    public ValidateOptionsResult Validate(string? name, AnalyticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        Range(errors, "QueueCapacity", options.QueueCapacity, 100, 1_000_000);
        Range(errors, "DeliveryTimeoutMs", options.DeliveryTimeoutMs, 50, 5_000);
        Range(errors, "ResumeTrackerCapacity", options.ResumeTrackerCapacity, 1_000, 10_000_000);
        Range(errors, "ErrorWindowSeconds", options.ErrorWindowSeconds, 10, 86_400);
        if (options.LowDiskBytes < 0)
        {
            errors.Add($"{Prefix}{AnalyticsOptions.SectionName}:LowDiskBytes is {options.LowDiskBytes}; it must be >= 0.");
        }

        if (options.RetainLocalDays is < 1)
        {
            errors.Add($"{Prefix}{AnalyticsOptions.SectionName}:RetainLocalDays is {options.RetainLocalDays}; it must be >= 1 or unset (never delete).");
        }

        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            errors.Add($"{Prefix}{AnalyticsOptions.DirectoryKey} is empty. Fix: set a directory (default 'analytics', relative to the content root).");
        }

        if (activation.IsActive(options))
        {
            ValidateZone(errors, options.TimeZone);
        }
        else if (options.Enabled && !gameServer.AllowsDevelopmentTools)
        {
            // Enabled without a sink would acknowledge every payment outbox entry without writing it (FR-014): never on a
            // shared environment. Development/Test only warn (AnalyticsPipeline).
            errors.Add(
                $"{Prefix}{AnalyticsOptions.EnabledKey} is true in '{gameServer.DeploymentEnvironment}' (deployment {gameServer.EnvironmentId}) " +
                "but no analytics sink is registered, so payment records would be acknowledged without being written. " +
                "Fix: call AddAnalyticsFiles() on the game server builder (or register an IAnalyticsSink), or set GameServer:Analytics:Enabled=false.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateZone(List<string> errors, string? zone)
    {
        if (!string.IsNullOrWhiteSpace(zone))
        {
            if (!AnalyticsTimeZone.TryResolve(zone, out _, out var error))
            {
                errors.Add(error);
            }

            return;
        }

        if (!gameServer.AllowsDevelopmentTools)
        {
            errors.Add(
                $"{Prefix}{AnalyticsOptions.TimeZoneKey} is not set in '{gameServer.DeploymentEnvironment}' (deployment {gameServer.EnvironmentId}). " +
                "Fix: set the IANA zone of the deployment (e.g. Asia/Ho_Chi_Minh), or GameServer:Analytics:Enabled=false.");
        }
    }

    private static void Range(List<string> errors, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{Prefix}{AnalyticsOptions.SectionName}:{key} is {value}; it must be {min}..{max}.");
        }
    }
}
