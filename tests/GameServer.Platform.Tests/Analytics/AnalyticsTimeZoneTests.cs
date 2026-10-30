using System.Globalization;
using GameServer.Platform.Analytics;
using Shouldly;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>Deployment zone, per-instant offset and formats (T015, US3-1, US3-2, SC-003, [VERIFY] V1).</summary>
public sealed class AnalyticsTimeZoneTests
{
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-30T14:05:09.123Z", "2026-10-30T21:05:09.123+07:00", "+07:00")]
    [InlineData("Asia/Taipei", "2026-10-30T14:05:09.123Z", "2026-10-30T22:05:09.123+08:00", "+08:00")]
    [InlineData("Europe/London", "2026-01-15T12:00:00.000Z", "2026-01-15T12:00:00.000+00:00", "+00:00")]
    [InlineData("Europe/London", "2026-07-15T12:00:00.000Z", "2026-07-15T13:00:00.000+01:00", "+01:00")]
    [InlineData("UTC", "2026-07-15T12:00:00.000Z", "2026-07-15T12:00:00.000+00:00", "+00:00")]
    public void Describe_gives_the_offset_of_the_instant(string zone, string utc, string local, string offset)
    {
        var instant = new AnalyticsTimeZone(zone).Describe(Utc(utc));

        instant.Utc.ShouldBe(utc);
        instant.Local.ShouldBe(local);
        instant.Offset.ShouldBe(offset);
    }

    [Theory]
    // Spring forward 2026-03-29T01:00Z and fall back 2026-10-25T01:00Z.
    [InlineData("2026-03-29T00:59:59.999Z", "2026-03-29T00:59:59.999+00:00", "+00:00")]
    [InlineData("2026-03-29T01:00:00.000Z", "2026-03-29T02:00:00.000+01:00", "+01:00")]
    [InlineData("2026-03-29T01:00:00.001Z", "2026-03-29T02:00:00.001+01:00", "+01:00")]
    [InlineData("2026-10-25T00:59:59.999Z", "2026-10-25T01:59:59.999+01:00", "+01:00")]
    [InlineData("2026-10-25T01:00:00.000Z", "2026-10-25T01:00:00.000+00:00", "+00:00")]
    [InlineData("2026-10-25T01:00:00.001Z", "2026-10-25T01:00:00.001+00:00", "+00:00")]
    public void London_dst_changes_are_exact_to_the_millisecond(string utc, string local, string offset)
    {
        var instant = new AnalyticsTimeZone("Europe/London").Describe(Utc(utc));

        instant.Local.ShouldBe(local);
        instant.Offset.ShouldBe(offset);
        DateTimeOffset.Parse(instant.Local, CultureInfo.InvariantCulture).ShouldBe(Utc(utc));
    }

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-30T16:59:59.999Z", "2026-10-30")]
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-30T17:00:00.000Z", "2026-10-31")]
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-30T23:59:59.999Z", "2026-10-31")]
    [InlineData("Asia/Ho_Chi_Minh", "2026-10-31T00:00:00.000Z", "2026-10-31")]
    [InlineData("UTC", "2026-10-30T23:59:59.999Z", "2026-10-30")]
    [InlineData("UTC", "2026-10-31T00:00:00.000Z", "2026-10-31")]
    public void Local_date_follows_the_zone(string zone, string utc, string date) =>
        new AnalyticsTimeZone(zone).LocalDate(Utc(utc)).ShouldBe(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    [Fact]
    public void Sub_millisecond_ticks_are_truncated_before_formatting()
    {
        var instant = Utc("2026-10-30T16:59:59.999Z").AddTicks(9_999);

        var described = new AnalyticsTimeZone("Asia/Ho_Chi_Minh").Describe(instant);

        described.Utc.ShouldBe("2026-10-30T16:59:59.999Z");
        described.Local.ShouldBe("2026-10-30T23:59:59.999+07:00");
        DateTimeOffset.Parse(described.Local, CultureInfo.InvariantCulture).ShouldBe(DateTimeOffset.Parse(described.Utc, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_missing_name_is_utc_and_defaulted()
    {
        var zone = new AnalyticsTimeZone(null);

        zone.Name.ShouldBe("UTC");
        zone.IsDefaulted.ShouldBeTrue();
        zone.Describe(Utc("2026-10-30T14:05:09.123Z")).Local.ShouldBe("2026-10-30T14:05:09.123+00:00");
        new AnalyticsTimeZone("Asia/Taipei").IsDefaulted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh")]
    [InlineData("Asia/Taipei")]
    [InlineData("Europe/London")]
    [InlineData("UTC")]
    public void Iana_names_are_accepted(string name)
    {
        AnalyticsTimeZone.TryResolve(name, out var zone, out var error).ShouldBeTrue(error);
        zone.HasIanaId.ShouldBeTrue();
    }

    /// <summary>[VERIFY] V1 (2026-10-31, Windows 11 and Linux aspnet:10.0): Windows ids resolve with HasIanaId = false.</summary>
    [Theory]
    [InlineData("SE Asia Standard Time", "Windows zone id")]
    [InlineData("Asia/Hanoi_typo", "not a known time zone")]
    [InlineData("", "is empty")]
    public void Windows_ids_and_unknown_names_are_refused_with_key_value_and_fix(string name, string reason)
    {
        AnalyticsTimeZone.TryResolve(name, out _, out var error).ShouldBeFalse();

        error.ShouldContain("GameServer:Analytics:TimeZone");
        error.ShouldContain(reason);
        error.ShouldContain(name);
        error.ShouldContain("IANA zone name (e.g. Asia/Ho_Chi_Minh)");
        error.ShouldContain("tzdata");
    }

    [Fact]
    public void An_invalid_name_never_throws_in_the_constructor_only_on_use()
    {
        var zone = new AnalyticsTimeZone("Asia/Hanoi_typo");

        Should.Throw<InvalidOperationException>(() => zone.Describe(Utc("2026-10-30T14:05:09.123Z"))).Message.ShouldContain("Asia/Hanoi_typo");
    }
}
