using GameServer.Platform.Web.Aggregates;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Modules.Identity;

/// <summary>
/// Framework Identity module. Slice 001 registers the mandatory <c>profile</c> aggregate (DAT-09);
/// guest login and single-session takeover are added in slice 002.
/// </summary>
public sealed class IdentityModule : IGameModule
{
    public const string ModuleName = "identity";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.1.0",
        Aggregates = [IdentityAggregates.ProfileDescriptor],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context) =>
        services.AddAggregateWireMapper<GameServer.Modules.Identity.ProfileState>(
            IdentityAggregates.Profile,
            state => new Protocol.V1.ProfileState { DisplayName = state.DisplayName, SessionEpoch = state.SessionEpoch });
}
