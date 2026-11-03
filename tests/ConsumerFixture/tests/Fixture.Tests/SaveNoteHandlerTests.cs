using Fixture.Game;
using GameServer.Config;
using GameServer.Modules.Identity;
using GameServer.TestKit;
using Shouldly;

namespace Fixture.Tests;

/// <summary>A game handler unit test on the kit's command-context double: no host, store or DI (US2-4).</summary>
public sealed class SaveNoteHandlerTests
{
    private static readonly IGameConfig Rules =
        TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = "{\"maxLength\":10}" }, "fixture-test");

    private static CommandContextDouble Context() =>
        CommandContextDouble.Create(SaveNoteHandler.Operation, new IdentityModule(), new FixtureModule()).AsPlayer("p_1").WithConfig(Rules);

    [Fact]
    public async Task Save_replaces_the_note_and_the_runner_would_commit_it()
    {
        var context = Context().Seed(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = "old", Saves = 1 });

        var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("new"), context);

        context.CheckCommit(outcome).ShouldBeEmpty();
        outcome.Result.ShouldBe(new SaveNoteResult("new", 2));
        context.Written<NotesState>(FixtureModuleDefinition.NotesAggregate).ShouldBe(new NotesState { Text = "new", Saves = 2 });
        context.Outbox.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_first_save_starts_from_the_default_note()
    {
        var context = Context();

        var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("hello"), context);

        outcome.Result.ShouldBe(new SaveNoteResult("hello", 1));
    }

    [Theory]
    [InlineData("old", "FIXTURE_UNCHANGED")]
    [InlineData("far too long", "FIXTURE_TOO_LONG")]
    public async Task Rejections_write_nothing(string text, string code)
    {
        var context = Context().Seed(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = "old", Saves = 1 });

        var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote(text), context);

        outcome.IsOk.ShouldBeFalse();
        outcome.Error!.Name.ShouldBe(code);
        context.Writes.ShouldBeEmpty();
        context.CheckCommit(outcome).ShouldBeEmpty();
    }
}
