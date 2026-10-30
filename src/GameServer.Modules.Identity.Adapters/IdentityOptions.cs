using System.Diagnostics.Metrics;
using System.Text;
using GameServer.Platform;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>Identity settings (section <c>Identity</c>). Defaults follow Appendix B (TBD-14, TBD-19, TBD-04a).</summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>Development/Test-only pepper used when none is configured.</summary>
    public const string DevelopmentPepper = "development-only-pepper-do-not-use-in-production";

    /// <summary>Server pepper for guest secret hashes; from the secret provider, never committed (OPS-08, SEC-11).</summary>
    public string GuestPepper { get; set; } = string.Empty;

    /// <summary>Per-instance cache of sessions and profiles; bounds takeover/revocation latency (IDN-08).</summary>
    public int CacheSeconds { get; set; } = 60;

    public int SessionSlidingDays { get; set; } = 30;

    public int SessionAbsoluteDays { get; set; } = 90;

    public int TouchAfterHours { get; set; } = 24;

    /// <summary>Login attempts per minute per localId (SEC-13).</summary>
    public int LoginPerLocalIdPerMinute { get; set; } = 10;

    /// <summary>Failed logins per minute per IP (SEC-13).</summary>
    public int FailedLoginPerIpPerMinute { get; set; } = 60;

    /// <summary>Login requests per minute per IP above which a warning metric is raised (not blocked).</summary>
    public int LoginPerIpWarnPerMinute { get; set; } = 600;

    /// <summary>Account deletion (PRIV-01, section <c>Identity:Deletion</c>).</summary>
    public DeletionSettings Deletion { get; set; } = new();

    internal byte[] PepperBytes(GameServerOptions gameServer) =>
        Encoding.UTF8.GetBytes(string.IsNullOrEmpty(GuestPepper) && gameServer.AllowsDevelopmentTools ? DevelopmentPepper : GuestPepper);
}

/// <summary>Account deletion settings (FR-010, TBD-04c).</summary>
public sealed class DeletionSettings
{
    public const int MinGraceDays = 1;
    public const int MaxGraceDays = 90;

    /// <summary>Days between the request and the purge; GM can cancel during this time.</summary>
    public int GraceDays { get; set; } = 30;
}

/// <summary>
/// Fails startup when no pepper is configured outside Development/Test, the cache TTL exceeds 60 s (PERF-08), or the
/// deletion grace period is outside 1..90 days.
/// </summary>
internal sealed class IdentityOptionsValidator(GameServerOptions gameServer) : IValidateOptions<IdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(options.GuestPepper) && !gameServer.AllowsDevelopmentTools)
        {
            errors.Add("[module identity] Identity:GuestPepper is not set. Fix: provide it through the secret provider (environment variable Identity__GuestPepper) (OPS-08).");
        }

        if (options.CacheSeconds is < 0 or > 60)
        {
            errors.Add("[module identity] Identity:CacheSeconds must be 0..60: revocation, takeover and bans apply within it (IDN-08, PERF-08).");
        }

        if (options.Deletion.GraceDays is < DeletionSettings.MinGraceDays or > DeletionSettings.MaxGraceDays)
        {
            errors.Add($"[module identity] Identity:Deletion:GraceDays must be {DeletionSettings.MinGraceDays}..{DeletionSettings.MaxGraceDays} (PRIV-01, FR-010).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Identity instruments. Player ids and tokens are never tags (OBS-02, LOG-05).</summary>
public sealed class IdentityMetrics : IDisposable
{
    public const string MeterName = "GameServer.Identity";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _logins;
    private readonly Counter<long> _burstWarnings;
    private readonly Counter<long> _sessionReplaced;
    private readonly Counter<long> _deletionRequests;
    private readonly Counter<long> _deletionCancellations;
    private readonly Counter<long> _resumeTrackerResets;

    public IdentityMetrics()
    {
        _logins = _meter.CreateCounter<long>("login_total");
        _burstWarnings = _meter.CreateCounter<long>("login_ip_burst_warnings_total");
        _sessionReplaced = _meter.CreateCounter<long>("session_replaced_total");
        _deletionRequests = _meter.CreateCounter<long>("account_deletion_requests_total");
        _deletionCancellations = _meter.CreateCounter<long>("account_deletion_cancellations_total");
        _resumeTrackerResets = _meter.CreateCounter<long>("login_resume_tracker_resets_total", description: "Resume trackers cleared at GameServer:Analytics:ResumeTrackerCapacity");
    }

    public void Login(string result) => _logins.Add(1, new KeyValuePair<string, object?>("result", result));

    public void IpBurstWarning() => _burstWarnings.Add(1);

    public void SessionReplaced() => _sessionReplaced.Add(1);

    public void DeletionRequested() => _deletionRequests.Add(1);

    public void DeletionCancelled() => _deletionCancellations.Add(1);

    public void ResumeTrackerReset() => _resumeTrackerResets.Add(1);

    public void Dispose() => _meter.Dispose();
}
