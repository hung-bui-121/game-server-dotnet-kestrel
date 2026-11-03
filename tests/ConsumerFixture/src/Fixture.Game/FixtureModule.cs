using GameServer.Errors;
using GameServer.Modules;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fixture.Game;

/// <summary>Adapter half of the fixture module: one player command over a well-known Protobuf type (no game proto needed).</summary>
public sealed class FixtureModule : IGameModule, IPlayerApiModule
{
    public const string SaveRoute = FixtureRoutes.SaveTemplate;

    public ModuleDescriptor Describe() => FixtureModuleDefinition.Describe();

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes) =>
        routes.MapPlayerCommand<StringValue, SaveNote, SaveNoteResult>(
            FixtureRoutes.Save,
            ToDomain,
            result => new StringValue { Value = result.Text });

    private static SaveNote ToDomain(StringValue request) =>
        string.IsNullOrWhiteSpace(request.Value)
            ? throw new GameServerException(CoreErrorCodes.BadRequest, "text must not be empty.")
            : new SaveNote(request.Value);
}
