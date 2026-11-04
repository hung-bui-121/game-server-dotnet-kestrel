using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T047/T048 (FR-030, FR-031, research R8): every commit rule of the runner runs before the first staged write, and a
/// result or receipt over its limit is the business rejection <c>COMMAND_OUTPUT_TOO_LARGE</c> (HTTP 200) with its details.
/// </summary>
public sealed class CommandRunnerRulesBeforeStagingTests : IDisposable
{
    private readonly RunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    public static TheoryData<string> RefusedRuns() => new("result", "receipt", "audit", "aggregate");

    /// <summary>A refused run reads, but never inserts or replaces anything in its attempt: the refusal needs no rollback.</summary>
    [Theory]
    [MemberData(nameof(RefusedRuns))]
    public async Task A_refused_run_stages_nothing(string refusal)
    {
        var spy = new RecordingStore(_h.Store);
        var runner = _h.CreateRunner(spy);

        var run = refusal switch
        {
            "result" => _h.RunAsync("probe.bigresult", 513, runner: runner, encode: _ => new byte[513]),
            "receipt" => _h.RunAsync("probe.outboxflood", 5000, runner: runner),
            "audit" => _h.RunAsync("probe.forge_audit", Unit.Value, runner: runner),
            _ => _h.RunAsync("probe.grow", new GrowRequest(70_000, 0, 1), runner: runner),
        };

        if (refusal == "audit")
        {
            await Should.ThrowAsync<InvalidOperationException>(() => run);
        }
        else
        {
            (await run).Code.ShouldBe(refusal == "aggregate" ? CoreErrorCodes.AggregateTooLarge : CoreErrorCodes.CommandOutputTooLarge);
        }

        spy.BodyRuns.ShouldBe(1);
        spy.Operations.ShouldNotBeEmpty();
        spy.Operations.ShouldAllBe(o => o.StartsWith("read ", StringComparison.Ordinal), string.Join("; ", spy.Operations));
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task A_committing_run_stages_aggregates_claims_then_the_receipt()
    {
        var spy = new RecordingStore(_h.Store);

        var execution = await _h.RunAsync("probe.claim", new ClaimRequest("k1", RejectAfterClaim: false), runner: _h.CreateRunner(spy));

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        spy.Operations.ShouldBe(["read pa_probe,receipts", "read probe_claims", "insert pa_probe", "insert probe_claims", "insert receipts"]);
    }

    [Fact]
    public async Task A_retry_of_an_output_too_large_command_executes_again_with_the_same_code()
    {
        using var metrics = new MetricCollector(_h.Metrics);
        var requestId = Guid.NewGuid().ToString("D");

        var first = await _h.RunAsync("probe.outboxflood", 5000, requestId: requestId);
        var second = await _h.RunAsync("probe.outboxflood", 5000, requestId: requestId);

        first.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        second.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        second.Replayed.ShouldBeFalse("a rejection has no receipt to replay");
        second.Details.ShouldBe(first.Details);
        metrics.SumTagged("command_output_too_large_total", ("operation", "probe.outboxflood")).ShouldBe(2);
        _h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task A_raised_receipt_limit_commits_the_same_command()
    {
        using var raised = new RunnerHarness(o => o.Commands.ReceiptMaxBytes = 16 * 1024);

        var execution = await raised.RunAsync("probe.outboxflood", 5000);

        execution.Code.ShouldBe(CoreErrorCodes.Ok);
        execution.Details.ShouldBeEmpty();
        raised.Store.Count("receipts").ShouldBe(1);
    }

    [Fact]
    public async Task A_module_rejection_keeps_its_details()
    {
        var execution = await _h.RunAsync(RejectDetailsHandler.Descriptor.Operation, "sold_out");

        execution.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        execution.Details.ShouldBe(new Dictionary<string, string> { ["reason"] = "sold_out" });
        _h.Store.Count("pa_probe").ShouldBe(0);
        _h.RunnerLogs.ShouldNotContain(l => l.StartsWith("Error: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejection_without_details_and_a_commit_have_empty_details()
    {
        var rejected = await _h.RunAsync("probe.claim", new ClaimRequest("k2", RejectAfterClaim: true));
        var committed = await _h.RunAsync("probe.nochange", Unit.Value);

        rejected.Code.ShouldBe(ProbeErrors.AlreadyClaimed);
        rejected.Details.ShouldBeEmpty();
        committed.Details.ShouldBeEmpty();
    }
}
