using GameServer.Actors;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Outbox;
using GameServer.Platform.Tests.Infrastructure;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// The commit rules extracted from the runner (research R9, FR-025, FR-026): each message equals the one the runner throws
/// for the same outcome, and a committable outcome has none.
/// </summary>
public sealed class CommitRulesTests : IDisposable
{
    private readonly RunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string? CheckCode(ErrorCode? code) =>
        CommitRules.CheckCode(_h.Registry.GetCommand(CommitCodeHandler.Descriptor.Operation), _h.Registry, code);

    private async Task<string> RunnerMessageAsync(ErrorCode code) =>
        (await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync(CommitCodeHandler.Descriptor.Operation, code))).Message;

    [Fact]
    public async Task Uncatalogued_code_gives_the_runner_message()
    {
        var message = CheckCode(ProbeErrors.NotListed);

        message.ShouldBe(
            "Command 'probe.commit_code' (module 'probe') committed with error code PROBE_NOT_LISTED(20002), which is not in the error-code catalog; " +
            "the client would receive OK. Fix: list the code in the module's ErrorCodeSet (ModuleDescriptor.ErrorCodes) (API-11).");
        message.ShouldBe(await RunnerMessageAsync(ProbeErrors.NotListed));
    }

    [Fact]
    public async Task Catalogued_name_with_another_instance_gives_the_runner_message()
    {
        var lookalike = ProbeErrors.AlreadyClaimed with { Number = 20009 };

        var message = CheckCode(lookalike);

        message.ShouldBe(
            "Command 'probe.commit_code' (module 'probe') committed with error code PROBE_ALREADY_CLAIMED(20009), which has the name of the " +
            "catalogued PROBE_ALREADY_CLAIMED(20001) but differs from it (number, HTTP status, retryable flag or message). " +
            "Fix: commit with the catalogued ErrorCode instance from the module's ErrorCodeSet (API-11).");
        message.ShouldBe(await RunnerMessageAsync(lookalike));
    }

    [Fact]
    public async Task Non_200_code_gives_the_runner_message()
    {
        var message = CheckCode(CoreErrorCodes.ConcurrentUpdate);

        message.ShouldBe(
            "Command 'probe.commit_code' (module 'probe') committed with error code CONCURRENT_UPDATE(12), whose HTTP status is 409; " +
            "a commit always answers HTTP 200. Fix: use CommandOutcome.Reject for non-200 codes (nothing is committed) (API-11).");
        message.ShouldBe(await RunnerMessageAsync(CoreErrorCodes.ConcurrentUpdate));
    }

    [Fact]
    public async Task Replaced_handler_is_named_as_by_the_runner()
    {
        _h.Registry.ReplaceHandler(CommitCodeHandler.Descriptor.Operation, typeof(GameCommitCodeHandler));

        var message = CheckCode(ProbeErrors.NotListed);

        message.ShouldNotBeNull().ShouldStartWith(
            "Command 'probe.commit_code' (module 'probe', handler replaced by GameCommitCodeHandler through ReplaceHandler) ");
        message.ShouldBe(await RunnerMessageAsync(ProbeErrors.NotListed));
    }

    [Fact]
    public void No_code_and_catalogued_200_codes_are_committable()
    {
        CheckCode(null).ShouldBeNull();
        CheckCode(ProbeErrors.AlreadyClaimed).ShouldBeNull();
        CheckCode(CoreErrorCodes.AggregateTooLarge).ShouldBeNull();
        CheckCode(CounterErrors.LimitExceeded).ShouldBeNull();
    }

    [Fact]
    public async Task Gm_command_without_audit_gives_the_runner_message()
    {
        var message = CommitRules.CheckAudit(GmAddHandler.Descriptor, []);

        message.ShouldBe(
            "Command 'probe.gm_add' (Gm) wrote 0 audit entries; expected 1 (SEC-14). " +
            "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.");
        var gm = new ActorContext("gm-1", ActorType.Gm, "p1", "test", "dev", new HashSet<string> { GmAddHandler.Permission }, "c");
        var runner = await Should.ThrowAsync<InvalidOperationException>(() => _h.Runner.ExecuteAsync(
            new CommandInvocation(GmAddHandler.Descriptor.Operation, gm, Guid.NewGuid().ToString("D"), "h", 0L, _ => [1]),
            CancellationToken.None));
        message.ShouldBe(runner.Message);
    }

    [Fact]
    public void Gm_command_with_two_audits_is_refused()
    {
        CommitRules.CheckAudit(GmAddHandler.Descriptor, [Audit(), Audit()]).ShouldBe(
            "Command 'probe.gm_add' (Gm) wrote 2 audit entries; expected 1 (SEC-14). " +
            "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.");
    }

    [Fact]
    public async Task Non_gm_command_with_an_audit_gives_the_runner_message()
    {
        var message = CommitRules.CheckAudit(ForgeAuditHandler.Descriptor, [Audit()]);

        message.ShouldBe(
            "Command 'probe.forge_audit' (Player) wrote 1 audit entries; expected 0 (SEC-14). " +
            "Fix: GM commands call ctx.AddAudit(...) exactly once; other commands never do.");
        var runner = await Should.ThrowAsync<InvalidOperationException>(() => _h.RunAsync(ForgeAuditHandler.Descriptor.Operation, Unit.Value));
        message.ShouldBe(runner.Message);
    }

    [Fact]
    public void Gm_command_with_one_audit_and_other_commands_without_audit_are_committable()
    {
        CommitRules.CheckAudit(GmAddHandler.Descriptor, [Audit(), new OutboxEntry("e2", "counter.changed", null)]).ShouldBeNull();
        CommitRules.CheckAudit(ForgeAuditHandler.Descriptor, [new OutboxEntry("e1", "counter.changed", null)]).ShouldBeNull();
        CommitRules.CheckAudit(ForgeAuditHandler.Descriptor, []).ShouldBeNull();
    }

    private static OutboxEntry Audit() => new(Guid.NewGuid().ToString("N"), AuditExtensions.OutboxKind, null);
}
