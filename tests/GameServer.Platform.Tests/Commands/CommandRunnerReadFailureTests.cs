using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T041 (FR-024, US3-3, US3-4, research R5 table): a command that touches or reads a broken aggregate is refused with the
/// typed code before its handler and writes nothing; the replay of a command committed before the damage still answers, with the
/// broken aggregate listed instead of sent.
/// </summary>
public sealed class CommandRunnerReadFailureTests : IDisposable
{
    private const string PlayerId = "p1";

    private readonly RunnerHarness _h = new();
    private readonly MetricCollector _metrics;

    public CommandRunnerReadFailureTests() => _metrics = new MetricCollector(_h.Metrics);

    [Fact]
    public async Task A_command_touching_a_broken_aggregate_is_refused_with_the_typed_code_and_writes_nothing()
    {
        var broken = Break(ProbeAggregates.Probe, d => d["schemaVersion"] = 9);

        var ex = await Should.ThrowAsync<GameServerException>(() => _h.RunAsync(GrowHandler.Descriptor.Operation, new GrowRequest(10, 0, 1)));

        ex.Code.ShouldBe(CoreErrorCodes.AggregateVersionAhead);
        ex.Details!["aggregate"].ShouldBe(ProbeAggregates.Probe);
        ex.RetryAfterMs.ShouldBe(1000);
        _h.Store.Peek(broken)!.ToJsonString().ShouldBe(Damaged(ProbeAggregates.Probe, d => d["schemaVersion"] = 9).ToJsonString());
        _h.Store.Count(CounterAggregates.CounterDescriptor.Collection).ShouldBe(0, "the handler never ran: the counter was not written");
        _h.Store.Count(ReceiptDocument.Collection).ShouldBe(0);
        _metrics.SumTagged("command_results_total", ("operation", GrowHandler.Descriptor.Operation), ("code", "AGGREGATE_VERSION_AHEAD")).ShouldBe(1);
        _metrics.SumTagged("command_results_total", ("code", "INTERNAL")).ShouldBe(0);
    }

    [Fact]
    public async Task A_command_reading_a_broken_aggregate_is_refused_before_its_handler()
    {
        // probe.badwrite reads the profile; its handler would throw (INTERNAL) if it ran.
        Break(IdentityAggregates.Profile, d => d["state"] = new JsonArray());

        var ex = await Should.ThrowAsync<GameServerException>(() => _h.RunAsync(BadWriteHandler.Descriptor.Operation, Unit.Value));

        ex.Code.ShouldBe(CoreErrorCodes.AggregateUnreadable);
        ex.Details!["aggregate"].ShouldBe(IdentityAggregates.Profile);
        _h.Store.Count(ReceiptDocument.Collection).ShouldBe(0);
        _h.Store.Count("pa_" + ProbeAggregates.Probe).ShouldBe(0);
    }

    [Fact]
    public async Task A_command_touching_neither_broken_aggregate_commits()
    {
        Break(ProbeAggregates.Probe, d => d["state"] = null);

        var execution = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null));

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        execution.Unavailable.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_replay_of_a_command_committed_before_the_damage_answers_and_lists_the_broken_aggregate()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var first = await _h.RunAsync(GrowHandler.Descriptor.Operation, new GrowRequest(10, 0, 2), requestId: requestId);
        first.Updates.Select(u => u.AggregateId).ShouldBe([ProbeAggregates.Probe, CounterAggregates.Counter], ignoreOrder: true);
        Break(ProbeAggregates.Probe, d => d["schemaVersion"] = 9);

        var replay = await _h.RunAsync(GrowHandler.Descriptor.Operation, new GrowRequest(10, 0, 2), requestId: requestId);

        replay.Replayed.ShouldBeTrue();
        replay.Code.ShouldBe(first.Code);
        replay.ReceiptId.ShouldBe(first.ReceiptId);
        replay.Result.ShouldBe(first.Result);
        replay.Updates.Select(u => u.AggregateId).ShouldBe([CounterAggregates.Counter]);
        var unavailable = replay.Unavailable.ShouldHaveSingleItem();
        unavailable.AggregateId.ShouldBe(ProbeAggregates.Probe);
        unavailable.Code.ShouldBe(CoreErrorCodes.AggregateVersionAhead);
        _h.Store.Count(ReceiptDocument.Collection).ShouldBe(1);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _h.Dispose();
    }

    private DocumentKey Break(string aggregateId, Action<JsonObject> damage)
    {
        var key = new DocumentKey("pa_" + aggregateId, PlayerId);
        _h.Store.Seed(key, Damaged(aggregateId, damage));
        return key;
    }

    private static JsonObject Damaged(string aggregateId, Action<JsonObject> damage)
    {
        var document = new JsonObject
        {
            ["gameId"] = "test",
            ["environmentId"] = "dev",
            ["playerId"] = PlayerId,
            ["aggregateId"] = aggregateId,
            ["schemaVersion"] = 1,
            ["rev"] = 2,
            ["createdAtUtc"] = "2026-10-26T00:00:00.000Z",
            ["updatedAtUtc"] = "2026-10-26T00:00:00.000Z",
            ["state"] = new JsonObject(),
        };
        damage(document);
        return document;
    }
}
