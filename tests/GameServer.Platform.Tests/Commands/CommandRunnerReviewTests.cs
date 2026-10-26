using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>Cases added after the 001 tech-lead review (PERF-02, ACC-42, CMD-04, receipt key per target player).</summary>
public sealed class CommandRunnerReviewTests : IDisposable
{
    private readonly RunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private long CounterValue(string playerId = "p1") =>
        _h.Store.Peek(new DocumentKey("pa_counter", playerId))?["state"]?["value"]?.GetValue<long>() ?? 0;

    [Fact]
    public async Task Client_disconnect_during_commit_does_not_abort_the_commit()
    {
        _h.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(300);
        _h.Store.Faults.Enqueue(StoreFault.DelayCommit);
        using var disconnect = new CancellationTokenSource();
        var invocation = new CommandInvocation(
            IncrementHandler.Operation,
            RunnerHarness.Player("p1"),
            Guid.NewGuid().ToString("D"),
            "hash",
            new IncrementCommand(3, null),
            _ => [1]);

        var running = _h.Runner.ExecuteAsync(invocation, disconnect.Token);
        await Task.Delay(50);
        await disconnect.CancelAsync();
        var execution = await running;

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        CounterValue().ShouldBe(3);
    }

    [Fact]
    public async Task One_time_effect_retried_after_replay_window_is_rejected_by_its_claim()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var first = await _h.RunAsync("probe.claim", new ClaimRequest("gift-1", false), requestId: requestId);
        first.Code.ShouldBe(CoreErrorCodes.Ok);

        _h.Clock.Advance(TimeSpan.FromHours(25));
        var late = await _h.RunAsync("probe.claim", new ClaimRequest("gift-1", false), requestId: requestId);

        late.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        late.Replayed.ShouldBeFalse();
        _h.Store.Count("probe_claims").ShouldBe(1);
    }

    [Fact]
    public async Task Guards_reject_new_executions_but_never_block_a_committed_replay()
    {
        var requestId = Guid.NewGuid().ToString("D");
        await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), requestId: requestId);
        _h.Guards.Add(new RejectAllGuard());
        var runner = _h.CreateRunner();

        var replay = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), requestId: requestId, runner: runner);
        replay.Replayed.ShouldBeTrue();

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), runner: runner));
        ex.Code.ShouldBe(CoreErrorCodes.AccountSuspended);
        CounterValue().ShouldBe(1);
    }

    [Fact]
    public async Task Same_actor_and_requestId_for_two_target_players_are_two_commands()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var gm = new ActorContext("gm-1", ActorType.Gm, null, "test", "dev", new HashSet<string>(), "c");

        var forA = await _h.Runner.ExecuteAsync(
            new CommandInvocation(IncrementHandler.Operation, gm with { PlayerId = "a" }, requestId, "h", new IncrementCommand(2, null), _ => [1]),
            CancellationToken.None);
        var forB = await _h.Runner.ExecuteAsync(
            new CommandInvocation(IncrementHandler.Operation, gm with { PlayerId = "b" }, requestId, "h", new IncrementCommand(2, null), _ => [1]),
            CancellationToken.None);

        forB.Replayed.ShouldBeFalse();
        forA.ReceiptId.ShouldNotBe(forB.ReceiptId);
        CounterValue("a").ShouldBe(2);
        CounterValue("b").ShouldBe(2);
    }

    private sealed class RejectAllGuard : ICommandGuard
    {
        public ValueTask<ErrorCode?> CheckAsync(ActorContext actor, CommandDescriptor descriptor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ErrorCode?>(CoreErrorCodes.AccountSuspended);
    }
}
