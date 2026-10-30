using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Platform.Commands;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Every record states its time unambiguously (T035, US3-1, US3-2, US3-5, SC-003): purchases and logins at local and UTC
/// midnights and around both 2026 London changes, in three deployment zones. Expected values are computed from UTC only.
/// </summary>
public sealed partial class AnalyticsTimeTests
{
    public static TheoryData<string, string> Instants() => new()
    {
        { "Asia/Ho_Chi_Minh", "2026-10-30T16:59:59.999Z" },
        { "Asia/Ho_Chi_Minh", "2026-10-30T17:00:00.000Z" },
        { "Asia/Ho_Chi_Minh", "2026-10-30T23:59:59.999Z" },
        { "Asia/Ho_Chi_Minh", "2026-10-31T00:00:00.000Z" },
        { "Asia/Taipei", "2026-10-30T15:59:59.999Z" },
        { "Asia/Taipei", "2026-10-30T16:00:00.000Z" },
        { "Asia/Taipei", "2026-10-31T00:00:00.000Z" },
        { "Europe/London", "2026-03-29T00:59:59.999Z" },
        { "Europe/London", "2026-03-29T01:00:00.000Z" },
        { "Europe/London", "2026-10-25T00:59:59.999Z" },
        { "Europe/London", "2026-10-25T01:00:00.001Z" },
        { "Europe/London", "2026-10-30T23:59:59.999Z" },
        { "Europe/London", "2026-10-31T00:00:00.000Z" },
    };

    [Theory]
    [MemberData(nameof(Instants))]
    public async Task Local_and_utc_times_denote_the_same_instant_with_the_offset_of_that_instant(string zone, string utc)
    {
        var instant = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        await using var host = await GameHttp.StartGameAsync(s => s["GameServer:Analytics:TimeZone"] = zone, clock: new FakeClock(instant));
        var (player, _) = await host.NewPlayerAsync();

        var purchase = await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));
        await host.FlushAnalyticsAsync();

        var login = host.Analytics.OfEvent("login").ShouldHaveSingleItem();
        var payment = host.Analytics.OfEvent("payment").ShouldHaveSingleItem();
        ShouldStateOneInstant(login, "activeTime", zone, instant);
        var committed = host.Store.Peek(ReceiptDocument.KeyFor(purchase.ReceiptId))!["committedAtUtc"]!.GetValue<string>();
        ShouldStateOneInstant(payment, "chargeTime", zone, DateTimeOffset.Parse(committed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        committed.ShouldBe(utc);
    }

    private static void ShouldStateOneInstant(JsonObject record, string field, string zone, DateTimeOffset expected)
    {
        var local = record[field]!.GetValue<string>();
        var utc = record[field + "Utc"]!.GetValue<string>();
        var offset = record["utcOffset"]!.GetValue<string>();

        LocalFormat().IsMatch(local).ShouldBeTrue(local);
        UtcFormat().IsMatch(utc).ShouldBeTrue(utc);
        record["timeZone"]!.GetValue<string>().ShouldBe(zone);
        local[^6..].ShouldBe(offset);
        DateTimeOffset.Parse(local, CultureInfo.InvariantCulture).ShouldBe(expected);
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture).ShouldBe(expected);

        // The expected local wall time is derived from UTC and the zone rules only, never from the machine zone.
        var rules = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var wall = TimeZoneInfo.ConvertTime(expected, rules);
        local.ShouldBe(wall.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + offset);
        offset.ShouldBe((rules.GetUtcOffset(expected) < TimeSpan.Zero ? "-" : "+") + rules.GetUtcOffset(expected).ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        utc.ShouldBe(expected.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}$")]
    private static partial Regex LocalFormat();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex UtcFormat();
}
