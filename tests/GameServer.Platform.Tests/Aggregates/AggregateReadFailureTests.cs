using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Tests.Aggregates;

/// <summary>
/// Slice 016 T033/T034 (FR-020, FR-021, FR-026, BC-5, research R5, R6, VERIFY V6): a stored aggregate that cannot be read is one
/// typed failure, reported exactly once (one log line, one metric increment), never with state content; <c>TryLoad</c> never
/// throws for it and <c>Load</c> throws its typed exception.
/// </summary>
public sealed class AggregateReadFailureTests : IDisposable
{
    private const string PlayerId = "p_read";

    private readonly PlatformMetrics _metrics = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly MetricCollector _collector;
    private readonly AggregateDocumentSerializer _serializer;

    public AggregateReadFailureTests()
    {
        _collector = new MetricCollector(_metrics);
        _serializer = new AggregateDocumentSerializer(new GameServerOptions { GameId = "test", EnvironmentId = "dev" }, _metrics, new AggregateReadFailures(_metrics, _logs));
    }

    [Theory]
    [InlineData(AggregateReadFailureReason.VersionAhead, "AGGREGATE_VERSION_AHEAD", "version_ahead")]
    [InlineData(AggregateReadFailureReason.UpgradeFailed, "AGGREGATE_UNREADABLE", "upgrade_failed")]
    [InlineData(AggregateReadFailureReason.Unreadable, "AGGREGATE_UNREADABLE", "unreadable")]
    [InlineData(AggregateReadFailureReason.MapperFailed, "AGGREGATE_UNREADABLE", "mapper_failed")]
    public void Each_reason_has_its_code_and_name(AggregateReadFailureReason reason, string code, string name)
    {
        var failure = new AggregateReadFailure("wallet", PlayerId, reason, "X", 1, 1);

        failure.Code.Name.ShouldBe(code);
        failure.ReasonName.ShouldBe(name);
    }

    [Theory]
    [InlineData(AggregateReadFailureReason.VersionAhead, 503, 1000)]
    [InlineData(AggregateReadFailureReason.Unreadable, 500, null)]
    public void The_exception_carries_the_code_the_aggregate_and_the_retry_delay_but_no_inner_exception(AggregateReadFailureReason reason, int status, int? retryAfterMs)
    {
        var failure = new AggregateReadFailure("wallet", PlayerId, reason, "System.Text.Json.JsonException", 3, 2);

        var ex = failure.ToException();

        ex.Code.ShouldBe(failure.Code);
        ex.Code.HttpStatus.ShouldBe(status);
        ex.Details.ShouldNotBeNull().ShouldBe(new Dictionary<string, string> { ["aggregate"] = "wallet" });
        ex.RetryAfterMs.ShouldBe(retryAfterMs);
        ex.InnerException.ShouldBeNull();
        AggregateReadFailure.TryGet(ex, out var carried).ShouldBeTrue();
        carried.ShouldBe(failure);
    }

    [Fact]
    public void TryGet_is_false_for_any_other_exception()
    {
        AggregateReadFailure.TryGet(new GameServerException(CoreErrorCodes.AggregateUnreadable), out _).ShouldBeFalse();
        AggregateReadFailure.TryGet(new InvalidOperationException(), out _).ShouldBeFalse();
    }

    [Fact]
    public void A_version_ahead_report_is_one_warning_line_and_one_metric()
    {
        new AggregateReadFailures(_metrics, _logs).Report(new AggregateReadFailure("wallet", PlayerId, AggregateReadFailureReason.VersionAhead, string.Empty, 3, 2));

        var line = _logs.Lines.ShouldHaveSingleItem();
        line.Category.ShouldBe("GameServer.Aggregates");
        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldBe(
            $"Aggregate wallet of player {PlayerId} is unavailable: version_ahead (stored schemaVersion 3, code 2). " +
            "Deploy code that reads it before writing it (PCR platform §6).");
        line.Exception.ShouldBeNull();
        _collector.SumTagged("aggregate_read_failures_total", ("aggregateId", "wallet"), ("reason", "version_ahead")).ShouldBe(1);
    }

    [Fact]
    public void Another_reason_is_one_error_line_with_the_exception_type_only()
    {
        new AggregateReadFailures(_metrics, _logs).Report(
            new AggregateReadFailure("wallet", PlayerId, AggregateReadFailureReason.UpgradeFailed, "System.Collections.Generic.KeyNotFoundException", 1, 3));

        var line = _logs.Lines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Error);
        line.Message.ShouldBe($"Aggregate wallet of player {PlayerId} is unavailable: upgrade_failed (System.Collections.Generic.KeyNotFoundException).");
        _collector.SumTagged("aggregate_read_failures_total", ("aggregateId", "wallet"), ("reason", "upgrade_failed")).ShouldBe(1);
    }

    /// <summary>The T004 corruption list (VERIFY V6) and the state-not-an-object rule (BC-5).</summary>
    [Theory]
    [InlineData("gold-object", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("items-object", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("gold-text", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("gold-bool", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("items-numbers", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("unknown-enum", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("gold-huge", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("gold-below-long", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("schema-text", AggregateReadFailureReason.Unreadable, "System.InvalidOperationException", 0)]
    [InlineData("schema-overflow", AggregateReadFailureReason.Unreadable, "System.OverflowException", 0)]
    [InlineData("rev-object", AggregateReadFailureReason.Unreadable, "System.FormatException", 0)]
    [InlineData("rev-text", AggregateReadFailureReason.Unreadable, "System.InvalidOperationException", 0)]
    [InlineData("rev-huge", AggregateReadFailureReason.Unreadable, "System.FormatException", 0)]
    [InlineData("rev-below-long", AggregateReadFailureReason.Unreadable, "System.OverflowException", 0)]
    [InlineData("created-number", AggregateReadFailureReason.Unreadable, "System.InvalidOperationException", 0)]
    [InlineData("state-missing", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("state-null", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("state-array", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("state-text", AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 3)]
    [InlineData("schema-zero", AggregateReadFailureReason.UpgradeFailed, "System.InvalidOperationException", 0)]
    [InlineData("upgrader-throws", AggregateReadFailureReason.UpgradeFailed, "System.Collections.Generic.KeyNotFoundException", 1)]
    [InlineData("upgrader-null", AggregateReadFailureReason.UpgradeFailed, "System.InvalidOperationException", 1)]
    [InlineData("schema-ahead", AggregateReadFailureReason.VersionAhead, "", 4)]
    public void A_damaged_document_is_one_typed_failure_reported_once(string damage, AggregateReadFailureReason reason, string exceptionType, int stored)
    {
        var document = ReadProbeAggregates.Document(ReadProbeAggregates.A, PlayerId, Damage(damage));

        _serializer.TryLoad(ReadProbeAggregates.ADescriptor, PlayerId, document, out var loaded, out var failure).ShouldBeFalse();

        loaded.ShouldBeNull();
        failure.ShouldNotBeNull();
        failure.ShouldBe(new AggregateReadFailure(ReadProbeAggregates.A, PlayerId, reason, exceptionType, stored, ReadProbeAggregates.Schema));
        _logs.Lines.Count.ShouldBe(1);
        _logs.Lines.Single().Message.ShouldNotContain("secret-state-value");
        _collector.SumTagged("aggregate_read_failures_total", ("aggregateId", ReadProbeAggregates.A), ("reason", failure.ReasonName)).ShouldBe(1);

        var ex = Should.Throw<GameServerException>(() => _serializer.Load(ReadProbeAggregates.ADescriptor, PlayerId, document));
        ex.Code.ShouldBe(failure.Code);
        AggregateReadFailure.TryGet(ex, out _).ShouldBeTrue();
        _logs.Lines.Count.ShouldBe(2, "each read reports its own failure once");
    }

    [Fact]
    public void A_missing_upgrade_step_is_an_upgrade_failure()
    {
        var document = ReadProbeAggregates.Document(ReadProbeAggregates.Gap, PlayerId, d => d["schemaVersion"] = 1);

        _serializer.TryLoad(ReadProbeAggregates.GapDescriptor, PlayerId, document, out _, out var failure).ShouldBeFalse();

        failure!.Reason.ShouldBe(AggregateReadFailureReason.UpgradeFailed);
        failure.ExceptionType.ShouldBe("System.InvalidOperationException");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void A_readable_document_reports_nothing(int schemaVersion)
    {
        var document = ReadProbeAggregates.Document(ReadProbeAggregates.A, PlayerId, d => d["schemaVersion"] = schemaVersion);

        _serializer.TryLoad(ReadProbeAggregates.ADescriptor, PlayerId, document, out var loaded, out var failure).ShouldBeTrue();

        failure.ShouldBeNull();
        ((ReadProbeState)loaded!.State).Gold.ShouldBe(5);
        loaded.Rev.ShouldBe(4);
        loaded.Upgraded.ShouldBe(schemaVersion < ReadProbeAggregates.Schema);
        _logs.Lines.ShouldBeEmpty();
        _collector.Sum("aggregate_read_failures_total").ShouldBe(0);
        _serializer.TryLoad(ReadProbeAggregates.ADescriptor, PlayerId, null, out var missing, out _).ShouldBeTrue();
        missing!.Rev.ShouldBe(0);
    }

    [Fact]
    public void Cancellation_inside_an_upgrader_propagates()
    {
        var document = ReadProbeAggregates.Document(ReadProbeAggregates.A, PlayerId, d =>
        {
            d["schemaVersion"] = 1;
            d["state"]!["cancel"] = true;
        });

        Should.Throw<OperationCanceledException>(() => _serializer.TryLoad(ReadProbeAggregates.ADescriptor, PlayerId, document, out _, out _));
        _logs.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void The_two_argument_serializer_counts_without_logging()
    {
        var serializer = new AggregateDocumentSerializer(new GameServerOptions(), _metrics);

        serializer.TryLoad(ReadProbeAggregates.ADescriptor, PlayerId, ReadProbeAggregates.Document(ReadProbeAggregates.A, PlayerId, Damage("state-null")), out _, out _)
            .ShouldBeFalse();

        _logs.Lines.ShouldBeEmpty();
        _collector.SumTagged("aggregate_read_failures_total", ("reason", "unreadable")).ShouldBe(1);
    }

    public void Dispose()
    {
        _collector.Dispose();
        _metrics.Dispose();
    }

    private static Action<JsonObject> Damage(string damage) => damage switch
    {
        "gold-object" => d => d["state"]!["gold"] = new JsonObject { ["x"] = 1 },
        "items-object" => d => d["state"]!["items"] = new JsonObject(),
        "gold-text" => d => d["state"]!["gold"] = "abc",
        "gold-bool" => d => d["state"]!["gold"] = true,
        "items-numbers" => d => d["state"]!["items"] = new JsonArray(1, 2),
        "unknown-enum" => d => d["state"]!["kind"] = "nope",
        "gold-huge" => d => d["state"]!["gold"] = JsonNode.Parse("1e400"),
        "gold-below-long" => d => d["state"]!["gold"] = JsonNode.Parse("-9223372036854775809"),
        "schema-text" => d => d["schemaVersion"] = "3",
        "schema-overflow" => d => d["schemaVersion"] = 3_000_000_000L,
        "rev-object" => d => d["rev"] = new JsonObject(),
        "rev-text" => d => d["rev"] = "x",
        "rev-huge" => d => d["rev"] = JsonNode.Parse("1e400"),
        "rev-below-long" => d => d["rev"] = JsonNode.Parse("-9223372036854775809"),
        "created-number" => d => d["createdAtUtc"] = 5,
        "state-missing" => d => d.Remove("state"),
        "state-null" => d => d["state"] = null,
        "state-array" => d => d["state"] = new JsonArray(1, 2),
        "state-text" => d => d["state"] = "x",
        "schema-zero" => d => d["schemaVersion"] = 0,
        "upgrader-throws" => d =>
        {
            d["schemaVersion"] = 1;
            d["state"]!["boom"] = "secret-state-value";
        },
        "upgrader-null" => d =>
        {
            d["schemaVersion"] = 1;
            d["state"]!["null"] = true;
        },
        "schema-ahead" => d => d["schemaVersion"] = 4,
        _ => throw new ArgumentOutOfRangeException(nameof(damage), damage, null),
    };
}
