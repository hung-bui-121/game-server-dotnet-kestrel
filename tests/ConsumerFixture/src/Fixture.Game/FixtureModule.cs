using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Protocol.Core.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wire = Fixture.V1;

namespace Fixture.Game;

/// <summary>
/// Adapter half of the fixture module: one player command over a well-known Protobuf type, the client-visible <c>card</c>
/// section with its typed wire mapper and a query route returning it (slice 014), and a GM command with an annotated body and
/// its option route for the generic console (slice 017).
/// </summary>
public sealed class FixtureModule : IGameModule, IPlayerApiModule, IGmApiModule
{
    public const string SaveRoute = FixtureRoutes.SaveTemplate;

    public ModuleDescriptor Describe() => FixtureModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddAggregateWireMapper<CardState, Wire.CardState>(FixtureModuleDefinition.CardAggregate, ToWire, o => o.Hidden(nameof(CardState.InternalNote)));

        // A startup check is registered in IServiceCollection; the platform creates and runs it after the config phase (FG-023).
        services.AddSingleton<IStartupCheck, NotesResultSizeCheck>();
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes)
    {
        routes.MapPlayerCommand<StringValue, SaveNote, SaveNoteResult>(
            FixtureRoutes.Save,
            ToDomain,
            result => new StringValue { Value = result.Text });

        routes.MapDeclared(FixtureRoutes.Card, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var loaded = await services.GetRequiredService<PlayerSnapshotReader>()
                    .ReadOneAsync(actor.PlayerId!, FixtureModuleDefinition.CardAggregate, http.RequestAborted);
                await ProtobufBinding.WriteAsync(http, new Wire.GetCardResponse
                {
                    Card = new Section
                    {
                        Id = FixtureModuleDefinition.CardAggregate,
                        Rev = loaded.Rev,
                        Payload = ToWire((CardState)loaded.State).ToByteString(),
                    },
                });
            })
            .AsPlayerEndpoint();
    }

    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        // Body rules (required, length) are annotations; the preset value is checked against the same list the option route answers.
        routes.MapGmCommand<SetNoteBody, SetNoteCommand, SetNoteResult>(
                FixtureRoutes.NoteSet,
                body => body.Preset is { } preset && !FixtureNotePresets.Contains(preset)
                    ? throw new GameServerException(CoreErrorCodes.BadRequest, "preset is not one of fixture.note_presets.")
                    : new SetNoteCommand(body.Text!, body.Preset, body.Reason!))
            .WithSummary("Set the note of the player (fixture GM command).");

        routes.MapDeclared(FixtureRoutes.NotePresets, () => GmHttp.Json(FixtureNotePresets.All))
            .Produces<GmOptionList>()
            .WithSummary("List the note presets a GM may pick.")
            .AsGmEndpoint(FixturePermissions.NoteEdit);
    }

    /// <summary>State → wire: dates as UTC milliseconds; <see cref="CardState.InternalNote"/> stays on the server.</summary>
    internal static Wire.CardState ToWire(CardState state)
    {
        var wire = new Wire.CardState { Title = state.Title, UpdatedUtcMs = state.UpdatedUtc.ToUnixTimeMilliseconds() };
        foreach (var (key, count) in state.Counts)
        {
            wire.Counts[key] = count;
        }

        foreach (var slot in state.Slots)
        {
            wire.Slots.Add(new Wire.CardState.Types.Slot { Item = slot.Item, UnlockEndUtcMs = slot.UnlockEndUtc.ToUnixTimeMilliseconds() });
        }

        if (state.Nickname is not null)
        {
            wire.Nickname = state.Nickname;
        }

        return wire;
    }

    private static SaveNote ToDomain(StringValue request) =>
        string.IsNullOrWhiteSpace(request.Value)
            ? throw new GameServerException(CoreErrorCodes.BadRequest, "text must not be empty.")
            : new SaveNote(request.Value);
}
