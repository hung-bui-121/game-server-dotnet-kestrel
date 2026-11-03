using GameServer.Errors;
using GameServer.Platform.Commands;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T014 (FR-031, FR-034, research R8, errors-and-report-lines §5): the receipt-size rule is "greater than" its limit and
/// names the part, the size and the limit in its details and its line.
/// </summary>
public sealed class CommitRulesSizeTests
{
    private static readonly CommandSettings Settings = new() { ReceiptMaxBytes = 4096 };

    [Fact]
    public void A_receipt_exactly_at_the_limit_commits()
    {
        CommitRules.CheckReceiptSize("sample.battle_end", 4096, Settings).ShouldBeNull();
        CommitRules.CheckReceiptSize("sample.battle_end", 12, Settings).ShouldBeNull();
    }

    [Fact]
    public void One_byte_over_the_limit_is_the_typed_rejection_with_part_bytes_and_limit()
    {
        var violation = CommitRules.CheckReceiptSize("sample.battle_end", 4097, Settings).ShouldNotBeNull();

        violation.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        violation.Details.ShouldBe(new Dictionary<string, string> { ["part"] = "receipt", ["bytes"] = "4097", ["limit"] = "4096" });
        violation.Line.ShouldBe(
            "Command 'sample.battle_end' receipt would be 4097 bytes; ReceiptMaxBytes is 4096: the runner rejects the command with " +
            "COMMAND_OUTPUT_TOO_LARGE (part receipt) and nothing commits. Fix: fewer ledger/outbox entries or raise " +
            "GameServer:Commands:ReceiptMaxBytes.");
    }

    [Fact]
    public void The_initializer_form_of_the_operation_label_is_kept_verbatim()
    {
        var violation = CommitRules.CheckReceiptSize("identity.create_account (initializer sample.starting_gold)", 5000, Settings).ShouldNotBeNull();

        violation.Line.ShouldStartWith("Command 'identity.create_account (initializer sample.starting_gold)' receipt would be 5000 bytes; ReceiptMaxBytes is 4096:");
    }

    [Fact]
    public void The_limit_comes_from_the_settings()
    {
        var raised = new CommandSettings { ReceiptMaxBytes = 16384 };

        CommitRules.CheckReceiptSize("sample.battle_end", 16384, raised).ShouldBeNull();
        CommitRules.CheckReceiptSize("sample.battle_end", 16385, raised)!.Details["limit"].ShouldBe("16384");
    }
}
