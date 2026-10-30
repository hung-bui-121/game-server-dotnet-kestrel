using System.Text.Json.Nodes;
using GameServer.Analytics;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Analytics;

/// <summary>Core helper <see cref="AnalyticsOutbox.AddAnalyticsEvent"/> (T003, FR-003, FR-011, FR-012).</summary>
public sealed class AnalyticsOutboxTests
{
    [Theory]
    [InlineData("payment", true)]
    [InlineData("login", true)]
    [InlineData("tutorial_done2", true)]
    [InlineData("a", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("Payment", false)]
    [InlineData("1payment", false)]
    [InlineData("pay-ment", false)]
    [InlineData("_payment", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Event_names_follow_the_file_name_rule(string? name, bool valid) => AnalyticsEventType.IsValidName(name).ShouldBe(valid);

    [Theory]
    [InlineData("chargeTime", true)]
    [InlineData("activeTime", true)]
    [InlineData("t", true)]
    [InlineData("ChargeTime", false)]
    [InlineData("charge_time", false)]
    [InlineData("charge-time", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Time_fields_are_camel_case_names(string? name, bool valid) => AnalyticsEventType.IsValidTimeField(name).ShouldBe(valid);

    [Fact]
    public async Task The_staged_entry_has_kind_analytics_the_documented_shape_and_no_null_or_player_id()
    {
        using var h = new RunnerHarness(null, EmitModule());

        var execution = await h.RunAsync(EmitHandler.Operation, new EmitRequest("test_event", 1, "eventTime", Reject: false), playerId: "p_analytics");

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        var receipt = h.Store.Peek(ReceiptDocument.KeyFor(execution.ReceiptId!)).ShouldNotBeNull();
        var entry = receipt["outbox"]!.AsArray().Single()!.AsObject();
        entry["kind"]!.GetValue<string>().ShouldBe(AnalyticsOutbox.Kind);
        entry["entryId"]!.GetValue<string>().ShouldBe(execution.ReceiptId + ":0");
        var payload = entry["payload"]!.AsObject();
        payload.Select(p => p.Key).ShouldBe(["event", "schemaVersion", "timeField", "fields"]);
        payload["event"]!.GetValue<string>().ShouldBe("test_event");
        payload["schemaVersion"]!.GetValue<int>().ShouldBe(1);
        payload["timeField"]!.GetValue<string>().ShouldBe("eventTime");
        var fields = payload["fields"]!.AsObject();
        fields.Select(p => p.Key).ShouldBe(["level", "label"]);
        fields["level"]!.GetValue<int>().ShouldBe(3);
        entry.ToJsonString().ShouldNotContain("p_analytics");
        entry.ToJsonString().ShouldNotContain("null");
    }

    [Fact]
    public async Task A_json_object_keeps_its_order_and_loses_its_null_values()
    {
        using var h = new RunnerHarness(null, EmitModule());

        var execution = await h.RunAsync(EmitHandler.Operation, new EmitRequest("test_event", 1, "eventTime", Reject: false, AsJsonObject: true));

        var entry = h.Store.Peek(ReceiptDocument.KeyFor(execution.ReceiptId!))!["outbox"]!.AsArray().Single()!;
        entry["payload"]!["fields"]!.AsObject().Select(p => p.Key).ShouldBe(["b", "a"]);
    }

    [Theory]
    [InlineData("Bad-Name", 1, "eventTime")]
    [InlineData("test_event", 0, "eventTime")]
    [InlineData("test_event", 1, "event_time")]
    public async Task An_invalid_type_fails_the_command_and_commits_nothing(string name, int version, string timeField)
    {
        using var h = new RunnerHarness(null, EmitModule());

        await Should.ThrowAsync<ArgumentException>(() => h.RunAsync(EmitHandler.Operation, new EmitRequest(name, version, timeField, Reject: false)));

        h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task A_rejection_rolls_the_entry_back()
    {
        using var h = new RunnerHarness(null, EmitModule());

        var execution = await h.RunAsync(EmitHandler.Operation, new EmitRequest("test_event", 1, "eventTime", Reject: true));

        execution.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        h.Store.Count("receipts").ShouldBe(0);
    }

    private static DescriptorModule EmitModule() => new(new ModuleDescriptor
    {
        Name = "analytics_probe",
        DependsOn = ["probe"],
        Commands = [Command.Define<EmitRequest, Unit>(EmitHandler.Descriptor).HandledBy<EmitHandler>()],
        ErrorCodes = new ErrorCodeSet(20100, 20199),
    });

    public sealed record EmitRequest(string Name, int Version, string TimeField, bool Reject, bool AsJsonObject = false);

    public sealed record EmitFields(int Level, string? Missing, string Label);

    public sealed class EmitHandler : ICommandHandler<EmitRequest, Unit>
    {
        public const string Operation = "analytics_probe.emit";

        public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [ProbeAggregates.Probe]);

        public ValueTask<CommandOutcome<Unit>> HandleAsync(EmitRequest request, ICommandContext context)
        {
            object fields = request.AsJsonObject
                ? new JsonObject { ["b"] = 1, ["gone"] = null, ["a"] = "x" }
                : new EmitFields(3, null, "gold");
            context.AddAnalyticsEvent(new AnalyticsEventType(request.Name, request.Version, request.TimeField), fields);
            return ValueTask.FromResult(request.Reject
                ? CommandOutcome<Unit>.Reject(ProbeErrors.AlreadyClaimed)
                : CommandOutcome<Unit>.Ok(Unit.Value));
        }
    }
}
