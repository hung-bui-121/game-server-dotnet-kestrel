using System.Globalization;

namespace GameServer.Platform.Analytics;

/// <summary>
/// Deployment zone of the analytics stream (FR-020, FR-021, TIME-01). Configured as an IANA name; the offset is computed
/// per instant from UTC, so DST changes are exact. Resolution is lazy: the constructor never throws, an invalid name is
/// reported by <see cref="AnalyticsOptionsValidator"/> at startup (the zone is only used when analytics is active).
/// </summary>
public sealed class AnalyticsTimeZone
{
    public const string UtcName = "UTC";

    private readonly Lazy<TimeZoneInfo> _zone;

    /// <param name="configuredName">Configured IANA name; null or empty = <c>UTC</c>, defaulted (Development/Test).</param>
    public AnalyticsTimeZone(string? configuredName)
    {
        IsDefaulted = string.IsNullOrWhiteSpace(configuredName);
        Name = IsDefaulted ? UtcName : configuredName!.Trim();
        var name = Name;
        _zone = new Lazy<TimeZoneInfo>(() => TryResolve(name, out var zone, out var error) ? zone : throw new InvalidOperationException(error));
    }

    /// <summary>Configured IANA name, or <c>UTC</c> when defaulted.</summary>
    public string Name { get; }

    /// <summary>True when no zone is configured (allowed in Development/Test only; the pipeline warns once).</summary>
    public bool IsDefaulted { get; }

    /// <summary>The instant truncated to milliseconds, formatted three ways (same instant, invariant culture).</summary>
    public AnalyticsInstant Describe(DateTimeOffset utc)
    {
        // Truncate first so the UTC and local strings always denote the same instant.
        var ticks = utc.UtcTicks;
        var truncated = new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(truncated, _zone.Value);
        var offset = FormatOffset(local.Offset);
        return new AnalyticsInstant(
            truncated.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            local.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + offset,
            offset);
    }

    /// <summary>Local calendar date of the instant in the deployment zone (resume rule, FR-018).</summary>
    public DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _zone.Value).DateTime);

    /// <summary>
    /// Resolves an IANA zone name; never throws. Windows ids (<c>SE Asia Standard Time</c>) are refused: on Windows and on
    /// Linux with ICU they resolve with <see cref="TimeZoneInfo.HasIanaId"/> false ([VERIFY] V1, 2026-10-31).
    /// </summary>
    public static bool TryResolve(string? name, out TimeZoneInfo zone, out string error)
    {
        zone = TimeZoneInfo.Utc;
        const string Fix = "Fix: use an IANA zone name (e.g. Asia/Ho_Chi_Minh); container images need time zone data (tzdata, ICU) " +
                           "and must not run with InvariantGlobalization on Windows.";
        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"[analytics] {AnalyticsOptions.TimeZoneKey} is empty. {Fix}";
            return false;
        }

        try
        {
            var found = TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
            if (!found.HasIanaId)
            {
                error = $"[analytics] {AnalyticsOptions.TimeZoneKey} '{name}' is a Windows zone id, not an IANA name. {Fix}";
                return false;
            }

            zone = found;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            error = $"[analytics] {AnalyticsOptions.TimeZoneKey} '{name}' is not a known time zone ({ex.GetType().Name}). {Fix}";
            return false;
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours:00}:{abs.Minutes:00}");
    }
}

/// <summary>
/// One instant formatted for a record: <c>Utc</c> = <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>; <c>Local</c> =
/// <c>yyyy-MM-ddTHH:mm:ss.fff+hh:mm</c>; <c>Offset</c> = <c>+hh:mm</c> (<c>+00:00</c> for UTC, never <c>Z</c>).
/// </summary>
public readonly record struct AnalyticsInstant(string Utc, string Local, string Offset);
