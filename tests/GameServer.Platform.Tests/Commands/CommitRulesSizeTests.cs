using GameServer.Errors;
using GameServer.Platform.Commands;
using Shouldly;

namespace GameServer.Platform.Tests.Commands;

/// <summary>
/// Slice 016 T014 (FR-031, FR-034, research R8, errors-and-report-lines §5): the result- and receipt-size rules are "greater than" their
/// limit (T014, T048) and name the part, the size and the limit in its details and its line.
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

    /// <summary>T082 (T078 finding): a creation's receipt line says what happens to a creation — the login answers INTERNAL (D15).</summary>
    [Fact]
    public void The_creation_line_keeps_the_initializer_label_and_names_the_logins_answer()
    {
        CommitRules.CheckCreationReceiptSize("identity.create_account (initializer sample.starting_gold)", 4096, Settings).ShouldBeNull();
        var violation = CommitRules.CheckCreationReceiptSize("identity.create_account (initializer sample.starting_gold)", 5000, Settings).ShouldNotBeNull();

        violation.Details.ShouldBe(new Dictionary<string, string> { ["part"] = "receipt", ["bytes"] = "5000", ["limit"] = "4096" });
        violation.Line.ShouldBe(
            "Command 'identity.create_account (initializer sample.starting_gold)' receipt would be 5000 bytes; ReceiptMaxBytes is 4096: " +
            "the account writer refuses the creation and the login answers INTERNAL. Fix: fewer ledger/outbox entries or raise " +
            "GameServer:Commands:ReceiptMaxBytes.");
    }

    [Fact]
    public void The_limit_comes_from_the_settings()
    {
        var raised = new CommandSettings { ReceiptMaxBytes = 16384 };

        CommitRules.CheckReceiptSize("sample.battle_end", 16384, raised).ShouldBeNull();
        CommitRules.CheckReceiptSize("sample.battle_end", 16385, raised)!.Details["limit"].ShouldBe("16384");
    }

    [Fact]
    public void A_result_exactly_at_the_limit_commits()
    {
        CommitRules.CheckResultSize("sample.battle_end", 512, Settings).ShouldBeNull();
        CommitRules.CheckResultSize("sample.battle_end", 0, Settings).ShouldBeNull();
    }

    [Fact]
    public void A_result_one_byte_over_the_limit_is_the_typed_rejection_with_part_result()
    {
        var violation = CommitRules.CheckResultSize("sample.battle_end", 513, Settings).ShouldNotBeNull();

        violation.Code.ShouldBe(CoreErrorCodes.CommandOutputTooLarge);
        violation.Details.ShouldBe(new Dictionary<string, string> { ["part"] = "result", ["bytes"] = "513", ["limit"] = "512" });
        violation.Line.ShouldBe(
            "Command 'sample.battle_end' result is 513 bytes; ResultCompactMaxBytes is 512: the runner rejects the command with " +
            "COMMAND_OUTPUT_TOO_LARGE (part result) and nothing commits. Fix: return less or raise " +
            "GameServer:Commands:ResultCompactMaxBytes.");
    }

    [Fact]
    public void Each_part_names_its_setting()
    {
        CommitRules.OutputLimitSetting(CommitRules.OutputPartResult).ShouldBe("ResultCompactMaxBytes");
        CommitRules.OutputLimitSetting(CommitRules.OutputPartReceipt).ShouldBe("ReceiptMaxBytes");
    }
}
