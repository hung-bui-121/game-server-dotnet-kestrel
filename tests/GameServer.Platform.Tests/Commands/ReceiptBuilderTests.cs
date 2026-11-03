using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T013 (FR-034, SC-005, research R14): the runner builds its receipts with <see cref="ReceiptBuilder"/>; the bytes the
/// store holds equal the builder's output for the same inputs, and the field order is the one of the base.
/// </summary>
public sealed class ReceiptBuilderTests : IDisposable
{
    private const string RequestId = "00000000-0000-4000-8000-0000000000aa";

    /// <summary>
    /// Field order of a receipt document before this slice (ReceiptDocument declaration order; the computed
    /// <c>committedAt</c> is serialized too, last).
    /// </summary>
    private static readonly string[] FieldOrder =
    [
        "receiptId", "gameId", "environmentId", "actorId", "actorType", "playerId", "operation", "requestId", "bodyHash", "resultCode",
        "resultCompact", "configVersion", "schemaVersions", "aggregates", "claimKeys", "committedAtUtc", "outbox", "outboxPending",
        "correlationId", "outboxAttempts", "outboxNextAttemptAtUtc", "outboxDeadLetter", "committedAt",
    ];

    // Inline relay off: the stored receipt keeps outboxPending as committed (the relay would rewrite it after the commit).
    private readonly RunnerHarness _h = new(o => o.Outbox.InlineRelayEnabled = false);

    public static TheoryData<string, string> Commands() => new()
    {
        { "probe.nochange", "{}" },
        { "probe.claim", """{"Key":"gift-1","RejectAfterClaim":false}""" },
        { "probe.outboxflood", "3" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task The_stored_receipt_equals_the_builder_output_byte_for_byte(string operation, string body)
    {
        var stored = await RunAndReadReceiptAsync(operation, body);
        var committed = ReceiptDocument.FromJson(stored);

        var built = ReceiptBuilder.Build(Inputs(operation, body, committed)).ToJson();

        Serialize(built).ShouldBe(Serialize(stored));
        ReceiptDocument.SizeOf(built).ShouldBe(ReceiptDocument.SizeOf(stored));
        ReceiptDocument.SizeOf(stored).ShouldBe(Encoding.UTF8.GetByteCount(Serialize(stored)));
        stored.Select(p => p.Key).ShouldBe(FieldOrder);
    }

    [Fact]
    public async Task The_three_commands_record_what_they_did()
    {
        var nochange = ReceiptDocument.FromJson(await RunAndReadReceiptAsync("probe.nochange", "{}"));
        nochange.Aggregates.ShouldBeEmpty();
        nochange.SchemaVersions.Keys.ShouldBe(["counter"]);

        var claim = ReceiptDocument.FromJson(await RunAndReadReceiptAsync("probe.claim", """{"Key":"gift-1","RejectAfterClaim":false}"""));
        claim.Aggregates.ShouldBe([new ReceiptAggregate("probe", 1)]);
        claim.ClaimKeys.ShouldHaveSingleItem().ShouldStartWith("probe_claims/");

        var outbox = ReceiptDocument.FromJson(await RunAndReadReceiptAsync("probe.outboxflood", "3"));
        outbox.Outbox.ShouldHaveSingleItem().Kind.ShouldBe("probe_log");
        outbox.OutboxPending.ShouldBeTrue();
    }

    public void Dispose() => _h.Dispose();

    private async Task<JsonObject> RunAndReadReceiptAsync(string operation, string body)
    {
        var request = JsonSerializer.Deserialize(body, _h.Registry.GetCommand(operation).RequestType)!;
        var execution = await _h.RunAsync(operation, request, requestId: RequestId, body: body);
        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        return _h.Store.Peek(ReceiptDocument.KeyFor(execution.ReceiptId!))!;
    }

    private ReceiptInputs Inputs(string operation, string body, ReceiptDocument committed)
    {
        var descriptor = _h.Registry.GetCommand(operation).Descriptor;
        var actor = RunnerHarness.Player("p1");
        return new ReceiptInputs(
            ReceiptIds.Compute(actor.ActorId, "p1", operation, RequestId),
            _h.Options.GameId,
            _h.Options.EnvironmentId,
            actor.ActorId,
            actor.ActorType,
            "p1",
            operation,
            RequestId,
            ReceiptIds.BodyHash(Encoding.UTF8.GetBytes(body)),
            CoreErrorCodes.Ok,
            JsonSerializer.SerializeToUtf8Bytes(GameServer.Platform.Tests.Infrastructure.Unit.Value),
            _h.Config.ConfigVersion,
            descriptor.Touches.Concat(descriptor.Reads).Distinct(StringComparer.Ordinal).Select(_h.Registry.GetAggregate),
            committed.Aggregates,
            committed.ClaimKeys.Select(k => new DocumentKey(k[..k.IndexOf('/', StringComparison.Ordinal)], k[(k.IndexOf('/', StringComparison.Ordinal) + 1)..])),
            committed.Outbox,
            actor.CorrelationId,
            _h.Clock.UtcNow);
    }

    private static string Serialize(JsonObject json) => json.ToJsonString(GameServerJson.Options);
}
