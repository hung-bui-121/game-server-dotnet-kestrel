using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Analytics;
using GameServer.Platform.Analytics;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>Record envelope, field order, nulls, collisions and event ids (T017, data-model §3, FR-006, FR-033).</summary>
public sealed class AnalyticsRecordFormatterTests
{
    private static readonly AnalyticsEventType Payment = new("payment", 1, "chargeTime");
    private static readonly AnalyticsEventType Login = new("login", 1, "activeTime");

    private readonly ConcurrentQueue<string> _errors = new();

    private AnalyticsRecordFormatter Formatter(string zone = "Asia/Ho_Chi_Minh") =>
        new(new GameServerOptions { GameId = "sample", EnvironmentId = "live" }, new AnalyticsTimeZone(zone), new ListLogger(_errors));

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string Text(AnalyticsRecord record) => Encoding.UTF8.GetString(record.Json.Span);

    [Fact]
    public void Payment_reproduces_the_data_model_example_byte_for_byte()
    {
        var fields = new JsonObject
        {
            ["transactionID"] = "google:GPA.3391-5521-0937-41862",
            ["store"] = "google",
            ["productId"] = "gems_100",
            ["orderId"] = "GPA.3391-5521-0937-41862",
            ["isTest"] = false,
            ["storePurchaseTimeUtc"] = "2026-10-30T14:05:01.000Z",
        };
        var envelope = new AnalyticsEnvelope(
            Payment,
            "5b1f0c9e2a7d4c8e9f0a1b2c3d4e5f60",
            "p_01JB7Q2M8XKZ4N6P0R3S5T7V9W",
            Utc("2026-10-30T14:05:09.123Z"),
            "q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e",
            fields);

        var record = Formatter().Format(envelope);

        Text(record).ShouldBe(
            """{"event":"payment","schemaVersion":1,"eventId":"5b1f0c9e2a7d4c8e9f0a1b2c3d4e5f60","gameId":"sample","environmentId":"live","userID":"p_01JB7Q2M8XKZ4N6P0R3S5T7V9W","chargeTime":"2026-10-30T21:05:09.123+07:00","chargeTimeUtc":"2026-10-30T14:05:09.123Z","timeZone":"Asia/Ho_Chi_Minh","utcOffset":"+07:00","receiptId":"q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e","transactionID":"google:GPA.3391-5521-0937-41862","store":"google","productId":"gems_100","orderId":"GPA.3391-5521-0937-41862","isTest":false,"storePurchaseTimeUtc":"2026-10-30T14:05:01.000Z"}""");
        record.Event.ShouldBe("payment");
        record.EventId.ShouldBe("5b1f0c9e2a7d4c8e9f0a1b2c3d4e5f60");
        record.Json.Span[^1].ShouldBe((byte)'}');
    }

    [Fact]
    public void Login_reproduces_the_data_model_example_byte_for_byte_from_typed_fields()
    {
        var envelope = new AnalyticsEnvelope(
            Login,
            "0f9e8d7c6b5a49382716a5b4c3d2e1f0",
            "p_01JB7Q2M8XKZ4N6P0R3S5T7V9W",
            Utc("2026-10-31T01:00:01.002Z"),
            null,
            new LoginLike("resume", null, false));

        Text(Formatter().Format(envelope)).ShouldBe(
            """{"event":"login","schemaVersion":1,"eventId":"0f9e8d7c6b5a49382716a5b4c3d2e1f0","gameId":"sample","environmentId":"live","userID":"p_01JB7Q2M8XKZ4N6P0R3S5T7V9W","activeTime":"2026-10-31T08:00:01.002+07:00","activeTimeUtc":"2026-10-31T01:00:01.002Z","timeZone":"Asia/Ho_Chi_Minh","utcOffset":"+07:00","source":"resume","isNewUser":false}""");
    }

    [Fact]
    public void Null_fields_are_omitted_in_both_shapes()
    {
        var fromNode = Text(Formatter().Format(new AnalyticsEnvelope(Payment, "e", "p", Utc("2026-10-30T14:05:09.123Z"), "r", new JsonObject { ["a"] = null, ["b"] = 1 })));
        var fromType = Text(Formatter().Format(new AnalyticsEnvelope(Login, "e", "p", Utc("2026-10-30T14:05:09.123Z"), null, new LoginLike("login", null, true))));

        fromNode.ShouldEndWith("\"receiptId\":\"r\",\"b\":1}");
        fromType.ShouldEndWith("\"source\":\"login\",\"isNewUser\":true}");
        fromType.ShouldNotContain("provider");
    }

    [Fact]
    public void Nested_values_are_copied_verbatim()
    {
        var line = Text(Formatter().Format(new AnalyticsEnvelope(Login, "e", "p", Utc("2026-10-30T14:05:09.123Z"), null, new Nested("x", [1, 2], new Inner(true)))));

        line.ShouldEndWith("\"name\":\"x\",\"values\":[1,2],\"inner\":{\"flag\":true}}");
    }

    [Fact]
    public void A_field_named_like_the_envelope_is_ignored_and_logged_once_per_type()
    {
        var formatter = Formatter();
        var fields = new JsonObject { ["userID"] = "forged", ["chargeTime"] = "x", ["ok"] = 1 };

        var line = Text(formatter.Format(new AnalyticsEnvelope(Payment, "e", "p_real", Utc("2026-10-30T14:05:09.123Z"), "r", fields)));
        formatter.Format(new AnalyticsEnvelope(Payment, "e", "p_real", Utc("2026-10-30T14:05:09.123Z"), "r", fields));

        var record = JsonNode.Parse(line)!.AsObject();
        record["userID"]!.GetValue<string>().ShouldBe("p_real");
        record["chargeTime"]!.GetValue<string>().ShouldBe("2026-10-30T21:05:09.123+07:00");
        line.ShouldNotContain("forged");
        line.ShouldEndWith("\"ok\":1}");
        _errors.Count.ShouldBe(1);
    }

    [Fact]
    public void Payment_event_ids_are_stable_per_entry_and_commit_time()
    {
        var first = AnalyticsEventIds.ForOutboxEntry("q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e:2", "2026-10-30T14:05:09.123Z");

        AnalyticsEventIds.ForOutboxEntry("q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e:2", "2026-10-30T14:05:09.123Z").ShouldBe(first);

        // CMD-07 supersede: same receipt id and entry index, another commit time → another event (SD1).
        AnalyticsEventIds.ForOutboxEntry("q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e:2", "2026-10-31T15:00:00.000Z").ShouldNotBe(first);
        first.ShouldMatch("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Login_event_ids_are_unique_lowercase_hex()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => AnalyticsEventIds.NewRandom()).ToArray();

        ids.Distinct().Count().ShouldBe(1000);
        ids.ShouldAllBe(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{32}$"));
    }

    [Fact]
    public void Game_and_environment_come_from_the_options_and_utc_zone_has_a_numeric_offset()
    {
        var formatter = new AnalyticsRecordFormatter(new GameServerOptions { GameId = "g1", EnvironmentId = "beta" }, new AnalyticsTimeZone(null), new ListLogger(_errors));

        var record = JsonNode.Parse(Text(formatter.Format(new AnalyticsEnvelope(Login, "e", "p", Utc("2026-10-30T14:05:09.123Z"), null, new LoginLike("login", "guest", false)))))!;

        record["gameId"]!.GetValue<string>().ShouldBe("g1");
        record["environmentId"]!.GetValue<string>().ShouldBe("beta");
        record["timeZone"]!.GetValue<string>().ShouldBe("UTC");
        record["utcOffset"]!.GetValue<string>().ShouldBe("+00:00");
        record["activeTime"]!.GetValue<string>().ShouldBe("2026-10-30T14:05:09.123+00:00");
    }

    public sealed record LoginLike(string Source, string? Provider, bool IsNewUser);

    public sealed record Nested(string Name, int[] Values, Inner Inner);

    public sealed record Inner(bool Flag);

    private sealed class ListLogger(ConcurrentQueue<string> errors) : ILogger<AnalyticsRecordFormatter>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                errors.Enqueue(formatter(state, exception));
            }
        }
    }
}
