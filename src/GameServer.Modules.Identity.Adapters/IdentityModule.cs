using GameServer.Config;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
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

    /// <summary>Ban scope entries: <c>login</c> or a command group (operation prefix).</summary>
    private static readonly System.Text.RegularExpressions.Regex ScopePattern = new("^[a-z][a-z0-9_]{0,31}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
        routes.MapGmCommand<BanBody, BanPlayerCommand, BanResult>(
            BanRoute,
            BanPlayerHandler.Operation,
            IdentityPermissions.PlayerBan,
            body =>
            {
                var scope = body.Scope is { Count: > 0 } requested ? requested.Distinct(StringComparer.Ordinal).ToArray() : [BanInfo.LoginScope];
                if (scope.Length > 10 || scope.Any(s => !ScopePattern.IsMatch(s)))
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "scope holds up to 10 entries: 'login' or command groups ([a-z][a-z0-9_]{0,31}).");
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
