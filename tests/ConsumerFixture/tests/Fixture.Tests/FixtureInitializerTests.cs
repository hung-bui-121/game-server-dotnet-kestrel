using Fixture.Game;
using GameServer.Modules.Identity;
using GameServer.TestKit;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// An account initializer tested host-free with the kit (FG-015, FR-017, FR-051; slice 016 T065): the double runs it behind the
/// account writer's view, and <c>CheckAccountCreation</c> returns the lines the writer would log. Consumer guide §13.2 takes its
/// example from here.
/// </summary>
public sealed class FixtureInitializerTests
{
    private static CommandContextDouble Creation(int maxLength) =>
        CommandContextDouble.ForAccountInitializer(FixtureWelcomeInitializer.Name, new IdentityModule(), new FixtureModule())
            .WithConfig(TestGameConfig.FromJson(
                new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = $"{{\"maxLength\":{maxLength}}}" },
                "fixture-test"));

    [Fact]
    public async Task A_new_account_starts_with_the_welcome_note_and_the_creation_would_commit()
    {
        var context = Creation(maxLength: 40);

        (await new FixtureWelcomeInitializer().InitializeAsync(context)).ShouldBeNull();

        context.Written<NotesState>(FixtureModuleDefinition.NotesAggregate)
            .ShouldBe(new NotesState { Text = FixtureWelcomeInitializer.WelcomeText, Saves = 0 });
        context.Outbox.ShouldBeEmpty();
        context.CheckAccountCreation().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_welcome_note_is_cut_to_the_max_length_of_notes_json()
    {
        var context = Creation(maxLength: 1);

        (await new FixtureWelcomeInitializer().InitializeAsync(context)).ShouldBeNull();

        context.Written<NotesState>(FixtureModuleDefinition.NotesAggregate)!.Text.ShouldBe("W");
        context.CheckAccountCreation().ShouldBeEmpty();
    }
}
