using Fixture.Game;
using GameServer.Config;
using GameServer.Modules.Identity;
using GameServer.TestKit;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// The game's config rules through the kit call (FR-018, FR-070; slice 015 T053): the lines a host would print at startup, without a
/// host. Consumer guide §12.4 takes its example from here.
/// </summary>
public sealed class FixtureConfigTests
{
    [Fact]
    public void The_committed_config_passes_the_rules()
    {
        var config = TestGameConfig.FromDirectory(CommittedConfig());

        var report = ConfigDeclarationCheck.Check(config, new IdentityModule(), new FixtureModule()).ThrowIfFailed();

        report.Passed.ShouldBeTrue();
        report.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"maxLength\": 0}", "0")]
    [InlineData("{\"maxLength\": \"40\"}", "\"40\"")]
    public void A_bad_max_length_gives_the_startup_line(string notes, string found)
    {
        var config = TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = notes }, "fixture-test");

        var report = ConfigDeclarationCheck.Check(config, new IdentityModule(), new FixtureModule());

        report.Passed.ShouldBeFalse();
        report.Errors.ShouldBe(
        [
            $"[module fixture] config notes.json: 'maxLength' must be an integer of at least 1, found {found}. " +
            "Fix: set the longest note a player may save, at least 1.",
        ]);
    }

    /// <summary><c>src/Fixture.Host/config</c> of the fixture repository (the config the host ships).</summary>
    internal static string CommittedConfig()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var config = Path.Combine(directory.FullName, "src", "Fixture.Host", "config");
            if (Directory.Exists(Path.Combine(config, "base")))
            {
                return config;
            }
        }

        throw new InvalidOperationException("src/Fixture.Host/config was not found above " + AppContext.BaseDirectory);
    }
}
