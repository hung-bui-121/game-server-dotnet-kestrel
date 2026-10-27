using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wire = GameServer.Modules.Identity.Protocol.V1;

namespace GameServer.Modules.Identity;

/// <summary>
/// Framework Identity module (slice 002): mandatory <c>profile</c> aggregate, guest login, single-session takeover,
/// <c>PlayerBearer</c> scheme, ban checks. Provider linking (PGS / Game Center) arrives in P0.5.
/// </summary>
public sealed class IdentityModule : IGameModule, IPlayerApiModule, IGmApiModule
{
    public const string ModuleName = "identity";
    public const string LoginRoute = "/api/v1/identity/login/guest";
    public const string LogoutRoute = "/api/v1/identity/logout";
    public const string DisplayNameRoute = "/api/v1/identity/display-name";
    public const string BanRoute = "/gm/v1/players/{playerId}/ban";
    public const string UnbanRoute = "/gm/v1/players/{playerId}/unban";


    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.8.0",
        Aggregates = [IdentityAggregates.ProfileDescriptor],
        Permissions = [IdentityPermissions.PlayerBan],
        ErrorCodes = IdentityErrors.All,
        Commands =
        [
            Command.Define<BanPlayerCommand, BanResult>(BanPlayerHandler.Descriptor).HandledBy<BanPlayerHandler>(),
            Command.Define<UnbanPlayerCommand, BanResult>(UnbanPlayerHandler.Descriptor).HandledBy<UnbanPlayerHandler>(),
            Command.Define<SetDisplayNameCommand, SetDisplayNameResult>(SetDisplayNameHandler.Descriptor).HandledBy<SetDisplayNameHandler>(),
        ],
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
        services.TryAddSingleton<IDisplayNamePolicy, AcceptAllDisplayNamePolicy>();

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

        routes.MapPlayerCommand<Wire.SetDisplayNameRequest, SetDisplayNameCommand, SetDisplayNameResult>(
            DisplayNameRoute,
            SetDisplayNameHandler.Operation,
            wire => new SetDisplayNameCommand(wire.DisplayName),
            result => new Wire.SetDisplayNameResult { DisplayName = result.DisplayName });
    }

    /// <summary>GM-07: ban / unban with reason, end time and scope; audited command on the player's profile.</summary>
    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        // A scope must name something that exists: a typo would be a ban that silently blocks nothing (review M2).
        var groups = routes.ServiceProvider.GetRequiredService<GameServer.Platform.Registry.ModuleRegistry>().Commands.Keys
            .Select(operation => operation.Split('.', 2)[0])
            .Append(BanInfo.LoginScope)
            .ToHashSet(StringComparer.Ordinal);
        routes.MapGmCommand<BanBody, BanPlayerCommand, BanResult>(
            BanRoute,
            BanPlayerHandler.Operation,
            IdentityPermissions.PlayerBan,
            body =>
            {
                // Omitted scope = login ban; an explicit empty list is a client bug, not "ban everything".
                if (body.Scope is { Count: 0 })
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "scope must not be empty; omit it for a login ban.");
                }

                var scope = body.Scope?.Distinct(StringComparer.Ordinal).ToArray() ?? [BanInfo.LoginScope];
                var unknown = scope.Where(s => !groups.Contains(s)).ToArray();
                if (scope.Length > 10 || unknown.Length > 0)
                {
                    throw new GameServerException(
                        CoreErrorCodes.BadRequest,
                        $"scope holds up to 10 entries: 'login' or a command group of this deployment ({string.Join(", ", groups.Order(StringComparer.Ordinal))}); unknown: {string.Join(", ", unknown)}.");
                }

                return new BanPlayerCommand(body.Reason!, body.UntilUtc?.ToUniversalTime(), scope);
            });
        routes.MapGmCommand<UnbanBody, UnbanPlayerCommand, BanResult>(
            UnbanRoute,
            UnbanPlayerHandler.Operation,
            IdentityPermissions.PlayerBan,
            body => new UnbanPlayerCommand(body.Reason!));
    }
}

/// <summary>Body of <c>POST /gm/v1/players/{playerId}/ban</c>; no end time = permanent, no scope = login.</summary>
public sealed record BanBody(string? Reason, DateTimeOffset? UntilUtc, List<string>? Scope) : IGmCommandBody;

public sealed record UnbanBody(string? Reason) : IGmCommandBody;
