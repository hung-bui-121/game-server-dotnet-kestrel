using GameServer.Config;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wire = GameServer.Modules.Identity.Protocol.V1;

namespace GameServer.Modules.Identity;

/// <summary>
/// Framework Identity module (slice 002): mandatory <c>profile</c> aggregate, guest login, single-session takeover,
/// <c>PlayerBearer</c> scheme, ban checks. Provider linking (PGS / Game Center) arrives in P0.5.
/// </summary>
public sealed class IdentityModule : IGameModule, IPlayerApiModule
{
    public const string ModuleName = "identity";
    public const string LoginRoute = "/api/v1/identity/login/guest";
    public const string LogoutRoute = "/api/v1/identity/logout";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.2.0",
        Aggregates = [IdentityAggregates.ProfileDescriptor],
        Collections = [new CollectionSpec(GuestLoginService.IdentitiesCollection), new CollectionSpec(SessionService.Collection)],
        PublicEndpoints = [LoginRoute],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddOptions<IdentityOptions>().BindConfiguration(IdentityOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityOptions>, IdentityOptionsValidator>();
        services.AddSingleton<IdentityMetrics>();
        services.AddSingleton<ProfileCache>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<GuestLoginService>();
        services.AddSingleton<ICommandGuard, BanCommandGuard>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PlayerBearerAuthenticationHandler>(PlayerBearerAuthenticationHandler.SchemeName, null);
        services.Configure<ActorSchemeOptions>(o => o.PlayerSchemes.Insert(0, PlayerBearerAuthenticationHandler.SchemeName));

        services.AddAggregateWireMapper<ProfileState>(
            IdentityAggregates.Profile,
            state => new Wire.ProfileState { DisplayName = state.DisplayName, SessionEpoch = state.SessionEpoch });
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes)
    {
        routes.MapPost(LoginRoute, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var request = ProtobufBinding.Parse<Wire.GuestLoginRequest>(await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes));
                var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var result = await services.GetRequiredService<GuestLoginService>()
                    .LoginAsync(request.LocalId, request.Secret.Memory, clientIp, http.RequestAborted);

                var mappers = services.GetRequiredService<AggregateWireMapperRegistry>();
                var response = new Wire.LoginResponse
                {
                    SessionToken = result.Token,
                    PlayerId = result.PlayerId,
                    ConfigVersion = services.GetRequiredService<IGameConfig>().ConfigVersion,
                    ServerTimeUtcMs = services.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds(),
                    SessionExpiresAtUtcMs = result.ExpiresAt.ToUnixTimeMilliseconds(),
                };
                response.Sections.AddRange(result.Sections.Select(s => new GameServer.Protocol.Core.V1.Section
                {
                    Id = s.Descriptor.AggregateId,
                    Rev = s.Rev,
                    Payload = mappers.ToPayload(s.Descriptor.AggregateId, s.State),
                }));
                await ProtobufBinding.WriteAsync(http, response);
            })
            .AsPublicPlayerEndpoint();

        routes.MapPost(LogoutRoute, async (HttpContext http) =>
            {
                var sessionKey = http.User.FindFirst(PlayerBearerAuthenticationHandler.SessionClaim)?.Value;
                if (sessionKey is not null)
                {
                    await http.RequestServices.GetRequiredService<SessionService>().RemoveAsync(sessionKey, http.RequestAborted);
                }

                await ProtobufBinding.WriteAsync(http, new Wire.LogoutResponse
                {
                    ServerTimeUtcMs = http.RequestServices.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds(),
                });
            })
            .AsPlayerEndpoint();
    }
}
