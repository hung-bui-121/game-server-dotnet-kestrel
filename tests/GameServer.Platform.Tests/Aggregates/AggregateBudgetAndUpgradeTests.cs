using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Aggregates;

/// <summary>DAT-09 size budget (ACC-39) and DAT-02 lazy upgrade (ACC-50).</summary>
public sealed class AggregateBudgetAndUpgradeTests : IDisposable
{
    private readonly MetricCollector _metrics = new();
    private readonly RunnerHarness _h = new();

    public void Dispose()
    {
        _h.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task Aggregate_above_warning_size_commits_and_is_reported()
    {
        var execution = await _h.RunAsync("probe.grow", new GrowRequest(40 * 1024, 0, 0));

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        _metrics.Sum("aggregate_size_warnings_total", "aggregateId", "probe").ShouldBe(1);
        _metrics.Values("aggregate_size_bytes").ShouldContain(v => v > 40 * 1024);
    }

    [Fact]
    public async Task Aggregate_above_hard_limit_rejects_whole_command()
    {
        await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null));

        var execution = await _h.RunAsync("probe.grow", new GrowRequest(70 * 1024, 0, 5));

        execution.Code.ShouldBe(CoreErrorCodes.AggregateTooLarge);
        execution.ReceiptId.ShouldBeNull();
        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.Store.Peek(new DocumentKey("pa_counter", "p1"))!["state"]!["value"]!.GetValue<long>().ShouldBe(1);
        _h.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task Old_schema_is_upgraded_on_read_and_rewritten_on_next_command()
    {
        var key = new DocumentKey("pa_counter", "p1");
        _h.Store.Seed(key, new JsonObject
        {
            ["gameId"] = "test",
            ["environmentId"] = "dev",
            ["playerId"] = "p1",
            ["aggregateId"] = "counter",
            ["schemaVersion"] = 1,
            ["rev"] = 3,
            ["createdAtUtc"] = "2026-01-01T00:00:00.000Z",
            ["updatedAtUtc"] = "2026-01-01T00:00:00.000Z",
            ["state"] = new JsonObject { ["value"] = 7 },
        });

        var snapshot = await _h.Snapshots.ReadAsync("p1", ["counter"], CancellationToken.None);
        var counter = snapshot.ShouldHaveSingleItem();
        counter.Upgraded.ShouldBeTrue();
        counter.Rev.ShouldBe(3);
        ((CounterState)counter.State).ShouldBe(new CounterState { Value = 7, Increments = 0 });
        _h.Store.Peek(key)!["schemaVersion"]!.GetValue<int>().ShouldBe(1);
        _metrics.Sum("aggregate_upgrades_total", "aggregateId", "counter").ShouldBe(1);

        await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null));

        var stored = _h.Store.Peek(key)!;
        stored["schemaVersion"]!.GetValue<int>().ShouldBe(2);
        stored["rev"]!.GetValue<long>().ShouldBe(4);
        stored["state"]!["increments"]!.GetValue<int>().ShouldBe(1);
        stored["createdAtUtc"]!.GetValue<string>().ShouldBe("2026-01-01T00:00:00.000Z");
    }

    [Fact]
    public async Task Missing_aggregate_reads_as_default_rev_zero()
    {
        var snapshot = await _h.Snapshots.ReadAsync("new-player", null, CancellationToken.None);

        snapshot.Select(s => s.Descriptor.AggregateId).ShouldBe(["counter", "probe", "profile"]);
        snapshot.ShouldAllBe(s => s.Rev == 0 && !s.Exists);
    }

    [Fact]
    public async Task Hidden_or_unknown_sections_are_rejected()
    {
        var ex = await Should.ThrowAsync<GameServerException>(() => _h.Snapshots.ReadAsync("p1", ["probe_hidden"], CancellationToken.None));
        ex.Code.ShouldBe(CoreErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Newer_stored_schema_than_code_is_an_error()
    {
        _h.Store.Seed(new DocumentKey("pa_counter", "p1"), new JsonObject { ["schemaVersion"] = 9, ["rev"] = 1, ["state"] = new JsonObject() });

        var ex = await Should.ThrowAsync<GameServerException>(() => _h.Snapshots.ReadAsync("p1", ["counter"], CancellationToken.None));
        ex.Code.ShouldBe(CoreErrorCodes.Internal);
    }
}
