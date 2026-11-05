using System.ComponentModel.DataAnnotations;
using GameServer.Actors;
using GameServer.Commands;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Routing;

namespace Fixture.Game;

/// <summary>GM permissions owned by the fixture module.</summary>
public static class FixturePermissions
{
    public const string NoteEdit = "fixture.note.edit";
}

/// <summary>GM edit of the player's note (slice 017 dogfood: a game's own GM command for the generic console).</summary>
public sealed record SetNoteCommand(string Text, string? Preset, string Reason);

public sealed record SetNoteResult(string Text, int Saves);

/// <summary>
/// JSON body of <c>POST /gm/v1/players/{playerId}/note</c>. The rules are declared once: the server enforces them before
/// <c>toDomain</c> and the GM document shows them (<c>required</c>, <c>maxLength</c>, <c>x-gm-options</c>).
/// </summary>
public sealed record SetNoteBody(
    [property: Required, StringLength(SetNoteHandler.MaxTextLength)] string? Text,
    [property: GmOptions("fixture.note_presets")] string? Preset,
    string? Reason) : IGmCommandBody;

/// <summary><c>fixture.note_set</c>: GM sets the note of the target player; one audit entry with before and after.</summary>
public sealed class SetNoteHandler : ICommandHandler<SetNoteCommand, SetNoteResult>
{
    public const string Operation = "fixture.note_set";
    public const int MaxTextLength = 140;

    public static CommandDescriptor Descriptor { get; } = new(
        Operation,
        touches: [FixtureModuleDefinition.NotesAggregate],
        profile: SecurityProfile.Gm,
        permission: FixturePermissions.NoteEdit);

    public ValueTask<CommandOutcome<SetNoteResult>> HandleAsync(SetNoteCommand request, ICommandContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var before = context.Get<NotesState>(FixtureModuleDefinition.NotesAggregate);
        var after = before with { Text = request.Text, Saves = before.Saves + 1 };
        context.Set(FixtureModuleDefinition.NotesAggregate, after);
        context.AddAudit(FixturePermissions.NoteEdit, Operation, request.Reason, before, after);
        return ValueTask.FromResult(CommandOutcome<SetNoteResult>.Ok(new SetNoteResult(after.Text, after.Saves)));
    }
}

/// <summary>The presets a GM may pick for a note (the option route's answer; the command checks the value against it).</summary>
public static class FixtureNotePresets
{
    public static GmOptionList All { get; } = new(
    [
        new GmOption("apology", "Apology for an incident"),
        new GmOption("welcome", "Welcome back"),
    ]);

    public static bool Contains(string value) => All.Items.Any(o => string.Equals(o.Value, value, StringComparison.Ordinal));
}

public static partial class FixtureRoutes
{
    public const string NoteSetTemplate = "/gm/v1/players/{playerId}/note";

    public const string NotePresetsTemplate = "/gm/v1/fixture/note-presets";

    /// <summary>GM command of the game (surface GmApi: never generated for Unity).</summary>
    public static RouteDeclaration NoteSet { get; } = RouteDeclaration.Command(SetNoteHandler.Descriptor, NoteSetTemplate);

    /// <summary>The option route of <see cref="SetNoteBody.Preset"/> (<c>[GmOptions]</c>, checked at startup).</summary>
    public static RouteDeclaration NotePresets { get; } =
        RouteDeclaration.Get("fixture.note_presets", NotePresetsTemplate, SecurityProfile.Gm, RouteSurface.GmApi, FixturePermissions.NoteEdit);
}
