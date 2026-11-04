using GameServer.Platform.Registry;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests;

/// <summary>
/// Slice 016 T050 (FR-032, SC-006, US4-4, BC-2, research R9, errors-and-report-lines §4.2): every <c>GameServer:Commands</c> value
/// outside its documented range is one startup line, the result must fit its own receipt, and an out-of-range aggregate maximum is
/// reported once while the count check uses 16.
/// </summary>
public sealed class CommandSettingsValidationTests
{
    public static TheoryData<string, int, string> OutOfRange() => new()
    {
        { "ReplayWindowHours", 0, "[deployment] GameServer:Commands:ReplayWindowHours is 0; allowed 1..168 (CMD-07)." },
        { "ReplayWindowHours", 169, "[deployment] GameServer:Commands:ReplayWindowHours is 169; allowed 1..168 (CMD-07)." },
        { "PerPlayerQueueLimit", -1, "[deployment] GameServer:Commands:PerPlayerQueueLimit is -1; allowed 0..16 (CMD-06)." },
        { "PerPlayerQueueLimit", 17, "[deployment] GameServer:Commands:PerPlayerQueueLimit is 17; allowed 0..16 (CMD-06)." },
        { "ReceiptMaxBytes", 65537, "[deployment] GameServer:Commands:ReceiptMaxBytes is 65537; allowed 1024..65536 (CMD-03)." },
        { "ResultCompactMaxBytes", 63, "[deployment] GameServer:Commands:ResultCompactMaxBytes is 63; allowed 64..16384 (CMD-03)." },
        { "MaxAggregatesPerGame", 0, "[deployment] GameServer:Commands:MaxAggregatesPerGame is 0; allowed 1..16 (DAT-09)." },
        { "MaxAggregatesPerGame", 17, "[deployment] GameServer:Commands:MaxAggregatesPerGame is 17; allowed 1..16 (DAT-09)." },
    };

    public static TheoryData<string, int> AtTheBounds() => new()
    {
        { "ReplayWindowHours", 1 },
        { "ReplayWindowHours", 168 },
        { "PerPlayerQueueLimit", 0 },
        { "PerPlayerQueueLimit", 16 },
        { "ReceiptMaxBytes", 65536 },
        { "ResultCompactMaxBytes", 64 },
        { "MaxAggregatesPerGame", 1 },
        { "MaxAggregatesPerGame", 16 },
    };

    [Fact]
    public void The_defaults_are_valid()
    {
        new CommandSettings().Validate().ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_value_one_past_its_bound_is_one_line(string setting, int value, string line)
    {
        With(setting, value).Validate().ShouldBe([line]);
    }

    [Theory]
    [MemberData(nameof(AtTheBounds))]
    public void A_value_at_its_bound_is_valid(string setting, int value)
    {
        With(setting, value).Validate().ShouldBeEmpty();
    }

    /// <summary>The lower bound of the receipt and the upper bound of the result are valid only with a matching other value.</summary>
    [Fact]
    public void The_receipt_and_result_bounds_are_valid_with_a_matching_other_value()
    {
        // At its lower bound the receipt is in range, but even the smallest result (64 bytes) needs 1112 receipt bytes.
        new CommandSettings { ReceiptMaxBytes = 1024, ResultCompactMaxBytes = 64 }.Validate().ShouldBe(
        [
            "[deployment] GameServer:Commands:ResultCompactMaxBytes 64 needs 1112 receipt bytes (base64 + 1024) but ReceiptMaxBytes is 1024. " +
            "Fix: raise ReceiptMaxBytes or lower ResultCompactMaxBytes.",
        ]);
        new CommandSettings { ReceiptMaxBytes = 1112, ResultCompactMaxBytes = 64 }.Validate().ShouldBeEmpty();
        new CommandSettings { ReceiptMaxBytes = 1023, ResultCompactMaxBytes = 64 }.Validate().First()
            .ShouldBe("[deployment] GameServer:Commands:ReceiptMaxBytes is 1023; allowed 1024..65536 (CMD-03).");
        new CommandSettings { ResultCompactMaxBytes = 16384, ReceiptMaxBytes = 65536 }.Validate().ShouldBeEmpty();
        new CommandSettings { ResultCompactMaxBytes = 16385, ReceiptMaxBytes = 65536 }.Validate()
            .ShouldBe(["[deployment] GameServer:Commands:ResultCompactMaxBytes is 16385; allowed 64..16384 (CMD-03)."]);
    }

    /// <summary>4·⌈512/3⌉ + 1024 = 1708: equality commits, one byte less is the cross line.</summary>
    [Fact]
    public void The_result_must_fit_its_own_receipt()
    {
        new CommandSettings { ResultCompactMaxBytes = 512, ReceiptMaxBytes = 1708 }.Validate().ShouldBeEmpty();
        new CommandSettings { ResultCompactMaxBytes = 512, ReceiptMaxBytes = 1707 }.Validate().ShouldBe(
        [
            "[deployment] GameServer:Commands:ResultCompactMaxBytes 512 needs 1708 receipt bytes (base64 + 1024) but ReceiptMaxBytes is 1707. " +
            "Fix: raise ReceiptMaxBytes or lower ResultCompactMaxBytes.",
        ]);
    }

    [Fact]
    public void Every_violation_is_its_own_line_in_the_documented_order()
    {
        var settings = new CommandSettings
        {
            ReplayWindowHours = 0,
            PerPlayerQueueLimit = 99,
            ReceiptMaxBytes = 2048,
            ResultCompactMaxBytes = 4096,
            MaxAggregatesPerGame = 40,
        };

        settings.Validate().Select(l => l[..l.IndexOf(" is ", StringComparison.Ordinal)]).ShouldBe(
        [
            "[deployment] GameServer:Commands:ReplayWindowHours",
            "[deployment] GameServer:Commands:PerPlayerQueueLimit",
            "[deployment] GameServer:Commands:ResultCompactMaxBytes 4096 needs 6488 receipt bytes (base64 + 1024) but ReceiptMaxBytes",
            "[deployment] GameServer:Commands:MaxAggregatesPerGame",
        ]);
    }

    [Fact]
    public void The_registry_reports_the_settings_lines_after_the_initializer_block()
    {
        var registry = Registry();
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        options.Commands.ReplayWindowHours = 200;

        registry.Validate(options, config: null).ShouldBe(["[deployment] GameServer:Commands:ReplayWindowHours is 200; allowed 1..168 (CMD-07)."]);
    }

    /// <summary>An out-of-range maximum gets only its range line; the count is checked against 16, not against the bad value.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void An_out_of_range_aggregate_maximum_is_only_its_range_line(int maximum)
    {
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        options.Commands.MaxAggregatesPerGame = maximum;

        Registry().Validate(options, config: null).ShouldBe(
            [$"[deployment] GameServer:Commands:MaxAggregatesPerGame is {maximum}; allowed 1..16 (DAT-09)."]);
    }

    [Fact]
    public void An_in_range_aggregate_maximum_still_limits_the_count()
    {
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        options.Commands.MaxAggregatesPerGame = 2;

        Registry().Validate(options, config: null).ShouldHaveSingleItem().ShouldContain(" player aggregates registered (max 2).");
    }

    private static ModuleRegistry Registry()
    {
        var registry = new ModuleRegistry();
        foreach (var module in new[] { TestModules.Identity(), TestModules.Counter(), TestModules.Probe() })
        {
            registry.Add(module, serviceOnly: false);
        }

        return registry;
    }

    private static CommandSettings With(string setting, int value)
    {
        var settings = new CommandSettings();
        typeof(CommandSettings).GetProperty(setting)!.SetValue(settings, value);
        return settings;
    }
}
