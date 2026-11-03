using Fixture.Game;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.TestKit;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// The game's startup check in a unit test (FG-023; slice 015 T071 G2): the status comes from <see cref="TestGameConfigStatus"/>,
/// in the state the host would publish for that config. Consumer guide §12.5 takes its example from here.
/// </summary>
public sealed class FixtureStartupCheckTests
{
    [Fact]
    public void The_committed_notes_fit_the_result_limit()
    {
        var report = ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(FixtureConfigTests.CommittedConfig()), new IdentityModule(), new FixtureModule());
        var result = new StartupCheckResult();

        new NotesResultSizeCheck(TestGameConfigStatus.From(report), new GameServerOptions()).Check(result);

        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void A_max_length_over_the_result_limit_is_one_line()
    {
        var config = TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = """{"maxLength": 200}""" }, "fixture-test");
        var report = ConfigDeclarationCheck.Check(config, new IdentityModule(), new FixtureModule());
        var result = new StartupCheckResult();

        new NotesResultSizeCheck(TestGameConfigStatus.From(report), new GameServerOptions()).Check(result);

        result.Errors.ShouldBe(
        [
            "[fixture] notes.json maxLength 200 allows a save result of up to 600 bytes, over GameServer:Commands:ResultCompactMaxBytes = 512. " +
            "Fix: set maxLength to at most 170 or raise the limit.",
        ]);
    }

    [Fact]
    public void The_check_skips_when_the_config_is_not_loaded_failed_or_its_rules_failed()
    {
        var badRule = TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = """{"maxLength": 0}""" }, "fixture-test");
        var result = new StartupCheckResult();

        new NotesResultSizeCheck(TestGameConfigStatus.NotLoaded(), new GameServerOptions()).Check(result);
        new NotesResultSizeCheck(TestGameConfigStatus.Failed(), new GameServerOptions()).Check(result);
        new NotesResultSizeCheck(TestGameConfigStatus.From(ConfigDeclarationCheck.Check(badRule, new IdentityModule(), new FixtureModule())), new GameServerOptions())
            .Check(result);

        result.Errors.ShouldBeEmpty();
    }
}
