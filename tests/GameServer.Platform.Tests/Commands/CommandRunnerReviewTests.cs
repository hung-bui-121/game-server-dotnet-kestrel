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
        var gm = Gm(GmAddHandler.Permission);

        var forA = await _h.Runner.ExecuteAsync(
            new CommandInvocation(GmAddHandler.Descriptor.Operation, gm with { PlayerId = "a" }, requestId, "h", 2L, _ => [1]),
            CancellationToken.None);
        var forB = await _h.Runner.ExecuteAsync(
            new CommandInvocation(GmAddHandler.Descriptor.Operation, gm with { PlayerId = "b" }, requestId, "h", 2L, _ => [1]),
            CancellationToken.None);

        forB.Replayed.ShouldBeFalse();
        forA.ReceiptId.ShouldNotBe(forB.ReceiptId);
        CounterValue("a").ShouldBe(2);
        CounterValue("b").ShouldBe(2);
    }

    [Fact]
    public async Task Runner_rejects_actors_that_do_not_match_the_command_profile()
    {
        Task Run(string operation, ActorContext actor, object request) =>
            _h.Runner.ExecuteAsync(new CommandInvocation(operation, actor, Guid.NewGuid().ToString("D"), "h", request, _ => [1]), CancellationToken.None);

        (await Should.ThrowAsync<GameServerException>(Run(IncrementHandler.Operation, Gm(GmAddHandler.Permission) with { PlayerId = "p1" }, new IncrementCommand(1, null))))
            .Code.ShouldBe(CoreErrorCodes.Forbidden);
        (await Should.ThrowAsync<GameServerException>(Run(GmAddHandler.Descriptor.Operation, RunnerHarness.Player("p1"), 1L)))
            .Code.ShouldBe(CoreErrorCodes.Forbidden);
        (await Should.ThrowAsync<GameServerException>(Run(GmAddHandler.Descriptor.Operation, Gm() with { PlayerId = "p1" }, 1L)))
            .Code.ShouldBe(CoreErrorCodes.Forbidden);
        CounterValue().ShouldBe(0);
    }

    [Fact]
    public async Task Gm_commands_must_audit_once_and_other_commands_never()
    {
        var gm = Gm(GmAddHandler.Permission) with { PlayerId = "p1" };
        await Should.ThrowAsync<Exception>(_h.Runner.ExecuteAsync(
            new CommandInvocation(GmAddHandler.Descriptor.Operation, gm, Guid.NewGuid().ToString("D"), "h", 0L, _ => [1]),
            CancellationToken.None));
        await Should.ThrowAsync<Exception>(_h.RunAsync(ForgeAuditHandler.Descriptor.Operation, Unit.Value));

        _h.Store.Count("receipts").ShouldBe(0);
        _h.Store.Peek(new DocumentKey("pa_probe", "p1")).ShouldBeNull();
    }

    private static ActorContext Gm(params string[] permissions) =>
        new("gm-1", ActorType.Gm, null, "test", "dev", permissions.ToHashSet(), "c");

    private sealed class RejectAllGuard : ICommandGuard
    {
        public ValueTask<ErrorCode?> CheckAsync(ActorContext actor, CommandDescriptor descriptor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ErrorCode?>(CoreErrorCodes.AccountSuspended);
    }
}
