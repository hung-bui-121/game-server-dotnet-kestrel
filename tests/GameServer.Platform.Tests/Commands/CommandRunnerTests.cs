using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

public sealed class CommandRunnerTests : IDisposable
{
    private readonly RunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private long CounterValue(string playerId = "p1") =>
        _h.Store.Peek(new DocumentKey("pa_counter", playerId))?["state"]?["value"]?.GetValue<long>() ?? 0;

    private long CounterRev(string playerId = "p1") =>
        _h.Store.Peek(new DocumentKey("pa_counter", playerId))?["rev"]?.GetValue<long>() ?? 0;

    [Fact]
    public async Task First_commit_writes_aggregate_and_receipt_atomically()
    {
        var execution = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null));

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        execution.Replayed.ShouldBeFalse();
        execution.ReceiptId.ShouldNotBeNull();
        var update = execution.Updates.ShouldHaveSingleItem();
        update.AggregateId.ShouldBe("counter");
        update.BaseRev.ShouldBe(0);
        update.NewRev.ShouldBe(1);
        ((CounterState)update.State).Value.ShouldBe(5);

        CounterValue().ShouldBe(5);
        CounterRev().ShouldBe(1);
        var receipt = _h.Store.Peek(ReceiptDocument.KeyFor(execution.ReceiptId!)).ShouldNotBeNull();
        receipt["operation"]!.GetValue<string>().ShouldBe("counter.increment");
        receipt["configVersion"]!.GetValue<string>().ShouldBe("test+000000000000");
        receipt["aggregates"]!.AsArray().Count.ShouldBe(1);
        receipt["gameId"]!.GetValue<string>().ShouldBe("test");
    }

    [Fact]
    public async Task Retry_with_same_body_returns_committed_result_without_second_effect()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var first = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);
        var second = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);

        second.Replayed.ShouldBeTrue();
        second.ReceiptId.ShouldBe(first.ReceiptId);
        second.Result.ShouldBe(first.Result);
        second.Updates.ShouldHaveSingleItem().NewRev.ShouldBe(first.Updates[0].NewRev);
        CounterValue().ShouldBe(5);
        _h.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task Same_requestId_with_different_body_is_rejected()
    {
        var requestId = Guid.NewGuid().ToString("D");
        await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(6, null), requestId: requestId));

        ex.Code.ShouldBe(CoreErrorCodes.IdempotencyPayloadMismatch);
        CounterValue().ShouldBe(5);
    }

    [Fact]
    public async Task Two_actors_with_same_requestId_are_independent()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var a = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), playerId: "a", requestId: requestId);
        var b = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), playerId: "b", requestId: requestId);

        a.ReceiptId.ShouldNotBe(b.ReceiptId);
        b.Replayed.ShouldBeFalse();
        CounterValue("a").ShouldBe(5);
        CounterValue("b").ShouldBe(5);
    }

    [Fact]
    public async Task Ambiguous_after_commit_then_retry_returns_committed_result_once()
    {
        var requestId = Guid.NewGuid().ToString("D");
        _h.Store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId));
        ex.Code.ShouldBe(CoreErrorCodes.CommitUnknownRetry);
        ex.RetryAfterMs.ShouldNotBeNull();

        var retry = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);
        retry.Replayed.ShouldBeTrue();
        CounterValue().ShouldBe(5);
        _h.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task Ambiguous_before_commit_then_retry_executes_exactly_once()
    {
        var requestId = Guid.NewGuid().ToString("D");
        _h.Store.Faults.Enqueue(StoreFault.AmbiguousBeforeCommit);

        await Should.ThrowAsync<GameServerException>(() =>
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId));
        CounterValue().ShouldBe(0);

        var retry = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);
        retry.Replayed.ShouldBeFalse();
        CounterValue().ShouldBe(5);
    }

    [Fact]
    public async Task Failure_before_commit_writes_nothing()
    {
        _h.Store.Faults.Enqueue(StoreFault.FailBeforeCommit);

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null)));

        ex.Code.ShouldBe(CoreErrorCodes.Internal);
        CounterValue().ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_same_requestId_on_two_instances_has_one_effect()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var instanceA = _h.CreateRunner();
        var instanceB = _h.CreateRunner();
        _h.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(50);
        _h.Store.Faults.Enqueue(StoreFault.DelayCommit);

        var results = await Task.WhenAll(
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId, runner: instanceA),
            _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId, runner: instanceB));

        results.Select(r => r.ReceiptId).Distinct().Count().ShouldBe(1);
        results.Count(r => r.Replayed).ShouldBe(1);
        CounterValue().ShouldBe(5);
        _h.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task Racing_instances_without_delay_still_have_one_effect()
    {
        for (var round = 0; round < 50; round++)
        {
            var requestId = Guid.NewGuid().ToString("D");
            var runners = Enumerable.Range(0, 4).Select(_ => _h.CreateRunner()).ToArray();

            var results = await Task.WhenAll(runners.Select(r => Task.Run(() =>
                _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), playerId: "race", requestId: requestId, runner: r))));

            results.Select(x => x.ReceiptId).Distinct().Count().ShouldBe(1);
            results.Count(x => !x.Replayed).ShouldBe(1);
        }

        CounterValue("race").ShouldBe(50);
    }

    [Fact]
    public async Task Concurrent_different_commands_of_one_player_serialize_without_lost_updates()
    {
        var runners = new[] { _h.CreateRunner(), _h.CreateRunner(), _h.CreateRunner(), _h.CreateRunner() };
        await Task.WhenAll(runners.Select(r => _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null), runner: r)));

        CounterValue().ShouldBe(4);
        CounterRev().ShouldBe(4);
        _h.Store.Count("receipts").ShouldBe(4);
    }

    [Fact]
    public async Task Per_player_queue_overflow_is_rate_limited()
    {
        _h.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(200);
        _h.Store.Faults.Enqueue(StoreFault.DelayCommit);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null)))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks.Select(async t =>
        {
            try
            {
                await t;
                return "ok";
            }
            catch (GameServerException ex)
            {
                return ex.Code.Name;
            }
        }));

        // concurrency 1 + queue 2 per player (CMD-06)
        outcomes.Count(o => o == "ok").ShouldBe(3);
        outcomes.Count(o => o == CoreErrorCodes.RateLimited.Name).ShouldBe(2);
    }

    [Fact]
    public async Task Retry_after_replay_window_is_processed_as_new_request()
    {
        var requestId = Guid.NewGuid().ToString("D");
        await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);
        _h.Clock.Advance(TimeSpan.FromHours(25));

        var again = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(5, null), requestId: requestId);

        again.Replayed.ShouldBeFalse();
        CounterValue().ShouldBe(10);
        _h.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task Business_rejection_writes_nothing_and_creates_no_receipt()
    {
        var execution = await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(11, null));

        execution.Code.ShouldBe(CounterErrors.LimitExceeded);
        execution.ReceiptId.ShouldBeNull();
        execution.Updates.ShouldBeEmpty();
        _h.Store.Count("pa_counter").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Handler_exception_rolls_back()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync("probe.throw", Unit.Value));

        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Writing_an_undeclared_aggregate_fails()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync("probe.badwrite", Unit.Value));

        ex.Message.ShouldContain("not declared in Touches");
        _h.Store.Count("pa_profile").ShouldBe(0);
    }

    [Fact]
    public async Task Unchanged_aggregate_keeps_rev_and_is_not_in_updates()
    {
        var execution = await _h.RunAsync("probe.nochange", Unit.Value);

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        execution.Updates.ShouldBeEmpty();
        _h.Store.Count("pa_counter").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task Hidden_aggregate_changes_are_committed_but_not_sent_to_client()
    {
        var execution = await _h.RunAsync("probe.grow", new GrowRequest(10, 10, 0));

        execution.Updates.Select(u => u.AggregateId).ShouldBe(["probe"]);
        _h.Store.Count("pa_probe_hidden").ShouldBe(1);
    }

    [Fact]
    public async Task Multi_aggregate_command_updates_all_touched_aggregates_in_one_commit()
    {
        var execution = await _h.RunAsync("probe.grow", new GrowRequest(10, 0, 3));

        execution.Updates.Select(u => u.AggregateId).OrderBy(x => x).ShouldBe(["counter", "probe"]);
        var receipt = _h.Store.Peek(ReceiptDocument.KeyFor(execution.ReceiptId!))!;
        receipt["aggregates"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Oversized_receipt_is_a_programming_error_and_rolls_back()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync("probe.outboxflood", 5000));

        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Oversized_result_compact_is_a_programming_error()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _h.RunAsync("probe.bigresult", 513, encode: _ => new byte[513]));

        ex.Message.ShouldContain("resultCompact");
    }

    [Fact]
    public async Task Commit_with_an_uncatalogued_code_is_a_programming_error_and_rolls_back()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync(CommitCodeHandler.Descriptor.Operation, ProbeErrors.NotListed));

        ex.Message.ShouldBe(
            "Command 'probe.commit_code' (module 'probe') committed with error code PROBE_NOT_LISTED(20002), which is not in the error-code catalog; " +
            "the client would receive OK. Fix: list the code in the module's ErrorCodeSet (ModuleDescriptor.ErrorCodes) (API-11).");
        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Commit_with_a_code_differing_from_the_catalogued_one_of_that_name_is_a_programming_error()
    {
        var lookalike = ProbeErrors.AlreadyClaimed with { Number = 20009 };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync(CommitCodeHandler.Descriptor.Operation, lookalike));

        ex.Message.ShouldBe(
            "Command 'probe.commit_code' (module 'probe') committed with error code PROBE_ALREADY_CLAIMED(20009), which has the name of the " +
            "catalogued PROBE_ALREADY_CLAIMED(20001) but differs from it (number, HTTP status, retryable flag or message). " +
            "Fix: commit with the catalogued ErrorCode instance from the module's ErrorCodeSet (API-11).");
        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Commit_with_a_non_200_code_is_a_programming_error()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync(CommitCodeHandler.Descriptor.Operation, CoreErrorCodes.ConcurrentUpdate));

        ex.Message.ShouldBe(
            "Command 'probe.commit_code' (module 'probe') committed with error code CONCURRENT_UPDATE(12), whose HTTP status is 409; " +
            "a commit always answers HTTP 200. Fix: use CommandOutcome.Reject for non-200 codes (nothing is committed) (API-11).");
        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Commit_error_names_the_replacing_handler()
    {
        _h.Registry.ReplaceHandler(CommitCodeHandler.Descriptor.Operation, typeof(GameCommitCodeHandler));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync(CommitCodeHandler.Descriptor.Operation, ProbeErrors.NotListed));

        ex.Message.ShouldStartWith(
            "Command 'probe.commit_code' (module 'probe', handler replaced by GameCommitCodeHandler through ReplaceHandler) " +
            "committed with error code PROBE_NOT_LISTED(20002), which is not in the error-code catalog;");
    }

    [Fact]
    public async Task Commit_with_a_catalogued_code_commits_and_returns_it()
    {
        var execution = await _h.RunAsync(CommitCodeHandler.Descriptor.Operation, ProbeErrors.AlreadyClaimed);

        execution.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        execution.Updates.ShouldHaveSingleItem().AggregateId.ShouldBe(ProbeAggregates.Probe);
        _h.Store.Peek(ReceiptDocument.KeyFor(execution.ReceiptId!))!["resultCode"]!.GetValue<string>().ShouldBe("PROBE_ALREADY_CLAIMED");
    }

    [Fact]
    public async Task Commit_with_a_core_or_other_module_business_code_commits_and_replays_it()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var core = await _h.RunAsync(CommitCodeHandler.Descriptor.Operation, CoreErrorCodes.AggregateTooLarge);
        var other = await _h.RunAsync(CommitCodeHandler.Descriptor.Operation, CounterErrors.LimitExceeded, requestId: requestId);
        var replay = await _h.RunAsync(CommitCodeHandler.Descriptor.Operation, CounterErrors.LimitExceeded, requestId: requestId);

        core.Code.ShouldBe(CoreErrorCodes.AggregateTooLarge);
        other.Code.ShouldBe(CounterErrors.LimitExceeded);
        replay.Replayed.ShouldBeTrue();
        replay.Code.ShouldBe(CounterErrors.LimitExceeded);
        _h.Store.Count("receipts").ShouldBe(2);
    }

    [Fact]
    public async Task Replay_of_a_receipt_with_an_uncatalogued_code_answers_ok_and_logs_a_warning()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var first = await _h.RunAsync("probe.grow", new GrowRequest(10, 0, 0), requestId: requestId);
        var key = ReceiptDocument.KeyFor(first.ReceiptId!);
        var receipt = _h.Store.Peek(key)!;
        receipt["resultCode"] = "PROBE_REMOVED_SINCE";
        _h.Store.Seed(key, receipt);

        var replay = await _h.RunAsync("probe.grow", new GrowRequest(10, 0, 0), requestId: requestId);

        replay.Replayed.ShouldBeTrue();
        replay.Code.ShouldBe(CoreErrorCodes.Ok);
        _h.RunnerLogs.ShouldContain(log =>
            log.StartsWith("Warning: Replay of probe.grow receipt ", StringComparison.Ordinal) &&
            log.Contains("committed error code 'PROBE_REMOVED_SINCE' is not in the error-code catalog; replying OK.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conflicts_until_expiration_return_concurrent_update()
    {
        using var h = new RunnerHarness(o => o.Transactions.ExpirationSeconds = 0);
        h.Store.Faults.Enqueue(StoreFault.ConflictOnce, 5);

        var ex = await Should.ThrowAsync<GameServerException>(() => h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null)));

        ex.Code.ShouldBe(CoreErrorCodes.ConcurrentUpdate);
        ex.Code.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Claim_marker_is_insert_only_and_rolled_back_on_rejection()
    {
        var rejected = await _h.RunAsync("probe.claim", new ClaimRequest("k1", RejectAfterClaim: true));
        rejected.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        _h.Store.Count("probe_claims").ShouldBe(0);

        var first = await _h.RunAsync("probe.claim", new ClaimRequest("k1", RejectAfterClaim: false));
        first.Code.ShouldBe(CoreErrorCodes.Ok);
        _h.Store.Peek(new DocumentKey("probe_claims", "k1")).ShouldNotBeNull()["receiptId"]!.GetValue<string>().ShouldBe(first.ReceiptId);

        var second = await _h.RunAsync("probe.claim", new ClaimRequest("k1", RejectAfterClaim: false));
        second.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
    }

    [Fact]
    public async Task Missing_target_player_is_bad_request()
    {
        var actor = RunnerHarness.Player("p1") with { PlayerId = null };
        var invocation = new CommandInvocation(IncrementHandler.Operation, actor, Guid.NewGuid().ToString("D"), "h", new IncrementCommand(1, null), _ => []);

        var ex = await Should.ThrowAsync<GameServerException>(() => _h.Runner.ExecuteAsync(invocation, CancellationToken.None));
        ex.Code.ShouldBe(CoreErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Profile_aggregate_is_untouched_by_counter_commands()
    {
        await _h.RunAsync(IncrementHandler.Operation, new IncrementCommand(1, null));

        _h.Store.Count(IdentityAggregates.ProfileDescriptor.Collection).ShouldBe(0);
    }
}
