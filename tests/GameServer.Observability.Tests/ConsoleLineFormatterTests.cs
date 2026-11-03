using GameServer.Platform.Observability;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>Slice 013 T044 (FR-087, data-model §1 console shape): one compact line per request event, other events with category.</summary>
public sealed class ConsoleLineFormatterTests
{
    private const string RequestTemplate = "{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms";
    private static readonly DateTimeOffset At = new(2026, 11, 3, 8, 15, 2, 123, TimeSpan.Zero);

    [Fact]
    public void A_player_command_is_one_line_with_operation_player_and_short_request_id()
    {
        var line = Format(Request(LogEventLevel.Information, "POST", "/api/v1/counter/increment", "counter.increment", "player", "p_7f3a9c1e2b4d", 200, "OK", durationMs: 12.4));

        line.ShouldBe("08:15:02.123 INF POST /api/v1/counter/increment (counter.increment) player=p_7f3a9c1e2b4d req=1a2b3c4d -> 200 OK 12.4 ms" + Environment.NewLine);
    }

    [Fact]
    public void A_replayed_command_says_replayed()
    {
        var line = Format(Request(LogEventLevel.Information, "POST", "/api/v1/iap/purchase", "iap.purchase", "player", "p_7f3a9c1e2b4d", 200, "ALREADY_GRANTED", replayed: true, durationMs: 3.1, requestId: "9f8e7d6c-0000-4000-8000-000000000000"));

        line.ShouldBe("08:15:02.123 INF POST /api/v1/iap/purchase (iap.purchase) player=p_7f3a9c1e2b4d req=9f8e7d6c -> 200 ALREADY_GRANTED replayed 3.1 ms" + Environment.NewLine);
    }

    [Fact]
    public void A_not_modified_config_get_says_not_modified()
    {
        var line = Format(Request(LogEventLevel.Information, "POST", "/api/v1/config/get", null, "player", "p_1", 200, "OK", notModified: true, durationMs: 1));

        line.ShouldBe("08:15:02.123 INF POST /api/v1/config/get player=p_1 req=1a2b3c4d -> 200 OK not_modified 1.0 ms" + Environment.NewLine);
    }

    [Fact]
    public void A_staff_request_names_the_staff_id()
    {
        var line = Format(Request(LogEventLevel.Information, "POST", "/gm/v1/players/{playerId}/ban", null, "gm", "admin", 200, "OK", durationMs: 8, requestId: "77aa88bb-0000-4000-8000-000000000000"));

        line.ShouldBe("08:15:02.123 INF POST /gm/v1/players/{playerId}/ban staff=admin req=77aa88bb -> 200 OK 8.0 ms" + Environment.NewLine);
    }

    [Fact]
    public void An_unmatched_request_without_actor_omits_the_optional_parts()
    {
        var line = Format(Request(LogEventLevel.Warning, "GET", "/api/...", null, null, null, 404, "NOT_FOUND", durationMs: 0.4, requestId: "0c1d2e3f-0000-4000-8000-000000000000"));

        line.ShouldBe("08:15:02.123 WRN GET /api/... req=0c1d2e3f -> 404 NOT_FOUND 0.4 ms" + Environment.NewLine);
    }

    [Fact]
    public void An_aborted_request_has_a_dash_for_the_status()
    {
        var line = Format(Request(LogEventLevel.Warning, "POST", "/api/v1/counter/increment", "counter.increment", "player", "p_1", null, "ABORTED", aborted: true, durationMs: 5));

        line.ShouldBe("08:15:02.123 WRN POST /api/v1/counter/increment (counter.increment) player=p_1 req=1a2b3c4d -> - ABORTED 5.0 ms" + Environment.NewLine);
    }

    [Fact]
    public void Other_events_render_the_message_with_category_and_the_exception_below()
    {
        var exception = new InvalidOperationException("boom");
        var logEvent = new LogEvent(
            At,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Relay of {Kind} failed after {Attempts} attempts"),
            [
                new LogEventProperty("Kind", new ScalarValue("counter_log")),
                new LogEventProperty("Attempts", new ScalarValue(3)),
                new LogEventProperty("SourceContext", new ScalarValue("GameServer.Platform.Outbox.OutboxRelay")),
            ]);

        var text = Format(logEvent);

        text.ShouldStartWith("08:15:02.123 ERR Relay of counter_log failed after 3 attempts [GameServer.Platform.Outbox.OutboxRelay]" + Environment.NewLine);
        text.ShouldContain("System.InvalidOperationException: boom");
    }

    [Fact]
    public void Control_characters_in_rendered_values_become_question_marks_and_the_exception_keeps_its_line_breaks()
    {
        const string Escape = "\u001b[2J\u001b[31m";
        var exception = new InvalidOperationException("bad name 'x" + Escape + "\rFAKE'" + Environment.NewLine + "second line");
        var logEvent = new LogEvent(
            At,
            LogEventLevel.Warning,
            exception,
            new MessageTemplateParser().Parse("Player {Name} sent {Payload}\u0007 and {Count}"),
            [
                new LogEventProperty("Name", new ScalarValue("eve" + Escape + "\r\n08:15:02.124 INF forged line")),
                new LogEventProperty("Payload", new StructureValue([new LogEventProperty("Text", new ScalarValue("a\u009bb\tc"))])),
                new LogEventProperty("Count", new ScalarValue(2)),
                new LogEventProperty("SourceContext", new ScalarValue("Game.Chat\u001b]0;title\u0007")),
            ]);

        var text = Format(logEvent);

        var lines = text.Split(Environment.NewLine);
        lines[0].ShouldBe("08:15:02.123 WRN Player eve?[2J?[31m??08:15:02.124 INF forged line sent { Text: \"a?b?c\" }? and 2 [Game.Chat?]0;title?]");
        text.ShouldNotContain("\u001b");
        text.ShouldNotContain("\u009b");
        text.ShouldNotContain("\u0007");
        lines[1].ShouldBe("System.InvalidOperationException: bad name 'x?[2J?[31m?FAKE'");
        lines[2].ShouldBe("second line");
    }

    [Fact]
    public void A_request_line_is_cleaned_too()
    {
        var line = Format(Request(LogEventLevel.Information, "POST", "/api/v1/game/act", "game.act\u001b[8m", "player", "p_1", 200, "GAME_CODE\r\nX", durationMs: 1));

        line.ShouldBe("08:15:02.123 INF POST /api/v1/game/act (game.act?[8m) player=p_1 req=1a2b3c4d -> 200 GAME_CODE??X 1.0 ms" + Environment.NewLine);
    }

    [Fact]
    public void Colours_only_when_asked()
    {
        var logEvent = Request(LogEventLevel.Error, "POST", "/api/v1/probe/fail", null, "player", "p_1", 500, "INTERNAL", durationMs: 1);

        Format(logEvent, colours: false).ShouldNotContain("\u001b[");
        var coloured = Format(logEvent, colours: true);
        coloured.ShouldContain("\u001b[31mERR\u001b[0m");
    }

    private static string Format(LogEvent logEvent, bool colours = false)
    {
        using var writer = new StringWriter();
        new ConsoleLineFormatter(colours).Format(logEvent, writer);
        return writer.ToString();
    }

    private static LogEvent Request(
        LogEventLevel level,
        string method,
        string route,
        string? operation,
        string? actorType,
        string? actorId,
        int? status,
        string resultCode,
        bool replayed = false,
        bool notModified = false,
        bool aborted = false,
        double durationMs = 0,
        string requestId = "1a2b3c4d-5e6f-4000-8000-000000000000") =>
        new(
            At,
            level,
            null,
            new MessageTemplateParser().Parse(RequestTemplate),
            [
                Property("HttpMethod", method),
                Property("Route", route),
                Property("Profile", "Player"),
                Property("Operation", operation),
                Property("ActorType", actorType),
                Property("ActorId", actorId),
                Property("RequestId", requestId),
                Property("TraceId", "4bf92f3577b34da6a3ce929d0e0e4736"),
                Property("Status", status),
                Property("ResultCode", resultCode),
                Property("Replayed", replayed),
                Property("NotModified", notModified),
                Property("Aborted", aborted),
                Property("DurationMs", durationMs),
                Property("SourceContext", "GameServer.Requests"),
            ]);

    private static LogEventProperty Property(string name, object? value) => new(name, new ScalarValue(value));
}
