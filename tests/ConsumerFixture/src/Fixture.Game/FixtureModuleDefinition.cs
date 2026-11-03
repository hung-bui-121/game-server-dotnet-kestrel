using GameServer.Actors;
using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules;
using GameServer.Routing;

namespace Fixture.Game;

/// <summary>State of the player's note.</summary>
public sealed record NotesState
{
    public string Text { get; init; } = string.Empty;

    public int Saves { get; init; }
}

/// <summary>
/// Client-visible state of the player's card (section <c>card</c>, FG-028): a nested record list, a dictionary, a nullable member,
/// a <see cref="DateTimeOffset"/> sent as UTC milliseconds, and <see cref="InternalNote"/>, which stays on the server (hidden).
/// </summary>
public sealed record CardState
{
    public string Title { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<CardSlot> Slots { get; init; } = [];

    public string? Nickname { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Server-only note: never sent to the client (declared hidden on the wire mapper).</summary>
    public string InternalNote { get; init; } = string.Empty;
}

/// <summary>A slot of the card.</summary>
public sealed record CardSlot(string Item, DateTimeOffset UnlockEndUtc);

/// <summary>Game config file <c>notes.json</c>.</summary>
public sealed record NotesRules
{
    public int MaxLength { get; init; }
}

public sealed record SaveNote(string Text);

public sealed record SaveNoteResult(string Text, int Saves);

/// <summary>Game error codes: the first block of the game band (50000–89999).</summary>
public static class FixtureErrors
{
    public static readonly ErrorCode Unchanged = new(50001, "FIXTURE_UNCHANGED", 200, false, "The note is unchanged.");
    public static readonly ErrorCode TooLong = new(50002, "FIXTURE_TOO_LONG", 200, false, "The note is too long.");

    public static ErrorCodeSet All { get; } = new(50000, 50099, Unchanged, TooLong);
}

public sealed class SaveNoteHandler : ICommandHandler<SaveNote, SaveNoteResult>
{
    public const string Operation = "fixture.save";

    public static CommandDescriptor Descriptor { get; } = new(Operation, touches: [FixtureModuleDefinition.NotesAggregate]);

    public ValueTask<CommandOutcome<SaveNoteResult>> HandleAsync(SaveNote request, ICommandContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var rules = context.Config.GetFile<NotesRules>(FixtureModuleDefinition.RulesFile);
        if (request.Text.Length > rules.MaxLength)
        {
            return ValueTask.FromResult(CommandOutcome<SaveNoteResult>.Reject(FixtureErrors.TooLong));
        }

        var notes = context.Get<NotesState>(FixtureModuleDefinition.NotesAggregate);
        if (notes.Text == request.Text)
        {
            return ValueTask.FromResult(CommandOutcome<SaveNoteResult>.Reject(FixtureErrors.Unchanged));
        }

        var next = notes with { Text = request.Text, Saves = notes.Saves + 1 };
        context.Set(FixtureModuleDefinition.NotesAggregate, next);
        return ValueTask.FromResult(CommandOutcome<SaveNoteResult>.Ok(new SaveNoteResult(next.Text, next.Saves)));
    }
}

/// <summary>HTTP routes of the game module (FG-026), declared in the business code next to the commands.</summary>
public static class FixtureRoutes
{
    public const string SaveTemplate = "/api/v1/fixture/save";

    public const string CardTemplate = "/api/v1/fixture/card";

    public static RouteDeclaration Save { get; } = RouteDeclaration.Command(SaveNoteHandler.Descriptor, SaveTemplate);

    /// <summary>The player's card (a query: no command, returns the <c>card</c> section).</summary>
    public static RouteDeclaration Card { get; } = RouteDeclaration.Post("fixture.card", CardTemplate, SecurityProfile.Player);
}

public static class FixtureModuleDefinition
{
    public const string ModuleName = "fixture";
    public const string NotesAggregate = "notes";
    public const string CardAggregate = "card";
    public const string RulesFile = "notes.json";

    public static PlayerAggregateDescriptor<NotesState> Aggregate { get; } =
        new(NotesAggregate, schemaVersion: 1, clientVisible: false, createDefault: () => new NotesState());

    public static PlayerAggregateDescriptor<CardState> Card { get; } =
        new(CardAggregate, schemaVersion: 1, clientVisible: true, createDefault: () => new CardState());

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        DependsOn = ["identity"],
        RequiredConfigFiles = [RulesFile],
        Aggregates = [Aggregate, Card],
        Commands = [Command.Define<SaveNote, SaveNoteResult>(SaveNoteHandler.Descriptor).HandledBy<SaveNoteHandler>()],
        ErrorCodes = FixtureErrors.All,
        Routes = [FixtureRoutes.Save, FixtureRoutes.Card],
    };
}
