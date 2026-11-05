using GameServer.Commands;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wire = GameServer.Modules.Identity.Protocol.V1;

namespace GameServer.Modules.Identity;

/// <summary>
/// Framework Identity module (slice 002): mandatory <c>profile</c> aggregate, guest login, single-session takeover,
/// <c>PlayerBearer</c> scheme, ban checks; provider login and linking (Play Games, Game Center, game adapters, slice 009).
/// </summary>
public sealed partial class IdentityModule : IGameModule, IPlayerApiModule, IGmApiModule
{
    public const string ModuleName = "identity";
    public const string LoginRoute = "/api/v1/identity/login/guest";
    public const string LogoutRoute = "/api/v1/identity/logout";
    public const string DisplayNameRoute = "/api/v1/identity/display-name";
    public const string ProviderLoginRoute = "/api/v1/identity/login/provider";
    public const string LinkRoute = "/api/v1/identity/link";
    public const string BanRoute = "/gm/v1/players/{playerId}/ban";
    public const string UnbanRoute = "/gm/v1/players/{playerId}/unban";
    public const string DeleteAccountRoute = "/api/v1/identity/delete-account";
    public const string CancelDeletionRoute = "/gm/v1/players/{playerId}/deletion/cancel";

    /// <summary>A literal segment: it wins over <c>/gm/v1/players/{playerId}</c> (player ids are generated, never this word).</summary>
    public const string BanScopesRoute = "/gm/v1/players/ban-scopes";
    public const string ProfileDeletionDueIndex = "ix_profile_deletion_due";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.9.0",
        Aggregates = [IdentityAggregates.ProfileDescriptor],
        Permissions = [IdentityPermissions.PlayerBan],
        ErrorCodes = IdentityErrors.All,
        Commands =
        [
            Command.Define<BanPlayerCommand, BanResult>(BanPlayerHandler.Descriptor).HandledBy<BanPlayerHandler>(),
            Command.Define<UnbanPlayerCommand, BanResult>(UnbanPlayerHandler.Descriptor).HandledBy<UnbanPlayerHandler>(),
            Command.Define<SetDisplayNameCommand, SetDisplayNameResult>(SetDisplayNameHandler.Descriptor).HandledBy<SetDisplayNameHandler>(),
            Command.Define<LinkIdentityCommand, LinkIdentityResult>(LinkIdentityHandler.Descriptor).HandledBy<LinkIdentityHandler>(),
            Command.Define<RequestDeletionCommand, RequestDeletionResult>(RequestDeletionHandler.Descriptor).HandledBy<RequestDeletionHandler>(),
            Command.Define<CancelDeletionCommand, CancelDeletionResult>(CancelDeletionHandler.Descriptor).HandledBy<CancelDeletionHandler>(),
        ],
        Collections =
        [
            new CollectionSpec(GuestLoginService.IdentitiesCollection, Deletion: DeletionTreatment.Remove),
            new CollectionSpec(SessionService.Collection, Deletion: DeletionTreatment.Remove),
            // Index only (pa_* treatment is the platform's): pending deletions due for purge (PERF-07, research R4). META().id is a
            // key so the due query's ORDER BY (due, id) reads index order and stops at its LIMIT instead of sorting the backlog.
            new CollectionSpec(
                IdentityAggregates.ProfileDescriptor.Collection,
                [new IndexSpec(ProfileDeletionDueIndex, "`state`.`deleted`.`purgeAfterUtc`, META().id", "`state`.`deleted`.`purgeStartedAtUtc` IS NOT VALUED")]),
        ],
        Routes = IdentityRoutes.All,

        // PRIV-01: purges accounts whose deletion grace period is over (research R7).
        Jobs = [new JobDescriptor(AccountPurgeJob.JobName, typeof(AccountPurgeJob), AccountPurgeJob.DefaultIntervalSeconds)],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddOptions<IdentityOptions>().BindConfiguration(IdentityOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityOptions>, IdentityOptionsValidator>();
        services.AddSingleton<IdentityMetrics>();
        services.AddSingleton<BanScopes>();
        services.AddSingleton<ProfileCache>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<LoginRateLimits>();
        services.AddSingleton<LoginAnalytics>();
        services.AddSingleton<LoginCompletion>();
        services.AddSingleton<GuestLoginService>();
        services.AddOptions<IdentityProviderOptions>().BindConfiguration(IdentityProviderOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityProviderOptions>, IdentityProviderOptionsValidator>();
        // Never follow redirects: the Game Center key URL comes from the client and must stay on the allowlisted host.
        services.AddHttpClient(IdentityProviders.HttpClientName, (sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<IdentityProviderOptions>>().Value.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddSingleton<IdentityProviders>();
        services.AddSingleton<ProviderLoginService>();
        services.AddSingleton<ICommandGuard, BanCommandGuard>();
        services.TryAddSingleton<IDisplayNamePolicy, AcceptAllDisplayNamePolicy>();
        services.AddSingleton(sp => new AccountDeletionPolicy(sp.GetRequiredService<IOptions<IdentityOptions>>().Value.Deletion.GraceDays));

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PlayerBearerAuthenticationHandler>(PlayerBearerAuthenticationHandler.SchemeName, null);
        services.Configure<ActorSchemeOptions>(o => o.PlayerSchemes.Insert(0, PlayerBearerAuthenticationHandler.SchemeName));

        // Ban details, the deletion request and the linked mapping keys stay on the server (GM and account flows read them; FG-027).
        services.AddAggregateWireMapper<ProfileState, Wire.ProfileState>(
            IdentityAggregates.Profile,
            state => new Wire.ProfileState { DisplayName = state.DisplayName, SessionEpoch = state.SessionEpoch },
            o => o.Hidden(nameof(ProfileState.Ban), nameof(ProfileState.Deleted), nameof(ProfileState.Identities)));
    }

    public void MapPlayerApi(IEndpointRouteBuilder routes)
    {
        // Built now so a duplicate or misnamed provider adapter fails startup (EXT-01).
        routes.ServiceProvider.GetRequiredService<IdentityProviders>();

        routes.MapDeclared(IdentityRoutes.LoginGuest, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var request = ProtobufBinding.Parse<Wire.GuestLoginRequest>(await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes));
                var result = await services.GetRequiredService<GuestLoginService>()
                    .LoginAsync(request.LocalId, request.Secret.Memory, ClientIp(http), http.RequestAborted, CorrelationMiddleware.GetCorrelationId(http));
                await ProtobufBinding.WriteAsync(http, LoginResponse(services, result));
            })
            .AsPublicPlayerEndpoint();

        routes.MapDeclared(IdentityRoutes.LoginProvider, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var request = ProtobufBinding.Parse<Wire.ProviderLoginRequest>(await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes));
                var result = await services.GetRequiredService<ProviderLoginService>().LoginAsync(
                    ToDomain(request.Credential),
                    request.SwitchAccount,
                    BearerToken(http),
                    ClientIp(http),
                    http.RequestAborted,
                    CorrelationMiddleware.GetCorrelationId(http));
                await ProtobufBinding.WriteAsync(http, LoginResponse(services, result));
            })
            .AsPublicPlayerEndpoint();

        routes.MapPlayerCommand<Wire.LinkIdentityRequest, LinkIdentityCommand, LinkIdentityResult>(
            IdentityRoutes.Link,
            VerifyLinkAsync,
            result => new Wire.LinkIdentityResult { Provider = result.Provider, AlreadyLinked = result.AlreadyLinked });

        routes.MapDeclared(IdentityRoutes.Logout, async (HttpContext http) =>
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
            IdentityRoutes.SetDisplayName,
            wire => new SetDisplayNameCommand(wire.DisplayName),
            result => new Wire.SetDisplayNameResult { DisplayName = result.DisplayName });

        routes.MapPlayerCommand<Wire.RequestAccountDeletionRequest, RequestDeletionCommand, RequestDeletionResult>(
            IdentityRoutes.RequestDeletion,
            _ => new RequestDeletionCommand(),
            result => new Wire.RequestAccountDeletionResult
            {
                RequestedAtUtcMs = result.RequestedAtUtc.ToUnixTimeMilliseconds(),
                PurgeAfterUtcMs = result.PurgeAfterUtc.ToUnixTimeMilliseconds(),
            },
            AfterDeletionRequestAsync);
    }

    /// <summary>GM-07: ban / unban with reason, end time and scope; audited command on the player's profile.</summary>
    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        // A scope must name something that exists: a typo would be a ban that silently blocks nothing (review M2). The same set
        // is the option list of identity.ban_scopes (A9).
        var scopes = routes.ServiceProvider.GetRequiredService<BanScopes>();
        routes.MapGmCommand<BanBody, BanPlayerCommand, BanResult>(
            IdentityRoutes.Ban,
            body =>
            {
                // Omitted scope = login ban; an explicit empty list is a client bug, not "ban everything".
                if (body.Scope is { Count: 0 })
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "scope must not be empty; omit it for a login ban.");
                }

                var scope = body.Scope?.Distinct(StringComparer.Ordinal).ToArray() ?? [BanInfo.LoginScope];
                var unknown = scope.Where(s => !scopes.Contains(s)).ToArray();
                if (scope.Length > 10 || unknown.Length > 0)
                {
                    throw new GameServerException(
                        CoreErrorCodes.BadRequest,
                        $"scope holds up to 10 entries: 'login' or a command group of this deployment ({string.Join(", ", scopes.Sorted)}); unknown: {string.Join(", ", unknown)}.");
                }

                return new BanPlayerCommand(body.Reason!, body.UntilUtc?.ToUniversalTime(), scope);
            })
            .WithSummary("Ban a player from login or from command groups, until a time or permanently (GM-07).")
            .WithGmConfirm(GmConfirm.Typed);
        routes.MapDeclared(IdentityRoutes.BanScopes, () => GmHttp.Json(new GmOptionList(scopes.Sorted.Select(s => new GmOption(s, s)).ToArray())))
            .Produces<GmOptionList>()
            .WithSummary("List the scopes a ban may name: login and every command group of this host.")
            .AsGmEndpoint(IdentityPermissions.PlayerBan);
        routes.MapGmCommand<UnbanBody, UnbanPlayerCommand, BanResult>(
            IdentityRoutes.Unban,
            body => new UnbanPlayerCommand(body.Reason!))
            .WithSummary("Lift the ban of a player (GM-07).");

        // PRIV-01, FR-011: GM cancels a pending account deletion while the grace period runs.
        routes.MapGmCommand<CancelDeletionBody, CancelDeletionCommand, CancelDeletionResult>(
            IdentityRoutes.CancelDeletion,
            body => new CancelDeletionCommand(body.Reason!),
            AfterDeletionCancelAsync)
            .WithSummary("Cancel a pending account deletion during its grace period (PRIV-01).")
            .WithGmConfirm(GmConfirm.Typed);
    }
}

public sealed partial class IdentityModule
{
    private const string DeletionLogCategory = "GameServer.Modules.Identity.AccountDeletion";

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Ends the player's sessions on this instance at once (FR-007): the cached profile still has the old epoch and no
    /// deletion. Other instances follow within <c>Identity:CacheSeconds</c>. Runs for a replay too (the cache may be older).
    /// Metric and log first: the commit happened, a failing cache refresh must not lose them (review minor 6).
    /// </summary>
    private static async Task AfterDeletionRequestAsync(CommandExecution execution, HttpContext http)
    {
        var services = http.RequestServices;
        var playerId = services.GetRequiredService<ActorContextAccessor>().GetActor(http).PlayerId!;
        if (!execution.Replayed)
        {
            services.GetRequiredService<IdentityMetrics>().DeletionRequested();
            services.GetRequiredService<ILoggerFactory>().CreateLogger(DeletionLogCategory)
                .LogInformation("Account deletion requested by player {PlayerId}.", playerId);
        }

        await services.GetRequiredService<ProfileCache>().ReadFreshAsync(playerId, CancellationToken.None);
    }

    /// <summary>
    /// The cancelled player can log in at once on any instance (login reads fresh); this keeps the cache true. Metric and
    /// log before the refresh, like <see cref="AfterDeletionRequestAsync"/>.
    /// </summary>
    private static async Task AfterDeletionCancelAsync(CommandExecution execution, HttpContext http)
    {
        var services = http.RequestServices;
        var playerId = (string)http.Request.RouteValues[GmCommandEndpointFactory.PlayerIdRouteValue]!;
        if (!execution.Replayed)
        {
            services.GetRequiredService<IdentityMetrics>().DeletionCancelled();
            services.GetRequiredService<ILoggerFactory>().CreateLogger(DeletionLogCategory).LogInformation(
                "Account deletion of player {PlayerId} cancelled by GM {StaffId}.",
                playerId,
                services.GetRequiredService<ActorContextAccessor>().GetActor(http).ActorId);
        }

        await services.GetRequiredService<ProfileCache>().ReadFreshAsync(playerId, CancellationToken.None);
    }

    /// <summary>
    /// Outbound verification of <c>identity.link</c>, after the replay check and before the runner: limited per player
    /// and per IP like logins (failed proofs count), under the providers' deadline and bulkhead (review M3).
    /// </summary>
    private static async Task<LinkIdentityCommand> VerifyLinkAsync(Wire.LinkIdentityRequest wire, HttpContext http)
    {
        var services = http.RequestServices;
        var providers = services.GetRequiredService<IdentityProviders>();
        var limits = services.GetRequiredService<LoginRateLimits>();
        var playerId = services.GetRequiredService<ActorContextAccessor>().GetActor(http).PlayerId!;
        var clientIp = ClientIp(http);
        var credential = ToDomain(wire.Credential);
        limits.Enter(clientIp);
        providers.EnterLink(playerId);
        try
        {
            return new LinkIdentityCommand(await providers.VerifyAsync(credential, http.RequestAborted));
        }
        catch (GameServerException ex) when (ex.Code == IdentityErrors.IdentityProofInvalid)
        {
            limits.Failed(clientIp);
            throw;
        }
    }

    /// <summary>Token of the session the device already has, if any (only used for the SWITCH_ACCOUNT check).</summary>
    private static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    private static Wire.LoginResponse LoginResponse(IServiceProvider services, LoginResult result)
    {
        var config = services.GetRequiredService<IGameConfig>();
        var response = new Wire.LoginResponse
        {
            SessionToken = result.Token,
            PlayerId = result.PlayerId,
            ConfigVersion = config.ConfigVersion,
            ClientConfigVersion = config.ClientConfigVersion,
            ServerTimeUtcMs = services.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds(),
            SessionExpiresAtUtcMs = result.ExpiresAt.ToUnixTimeMilliseconds(),
            IsNewPlayer = result.IsNewPlayer,
        };
        services.GetRequiredService<SectionWriter>()
            .WriteSections(result.PlayerId, result.Sections, result.Unavailable, response.Sections, response.UnavailableSections);
        return response;
    }

    private static IdentityCredential ToDomain(Wire.ProviderCredential? credential) => credential?.CredentialCase switch
    {
        Wire.ProviderCredential.CredentialOneofCase.PlayGames => new PlayGamesCredential(credential.PlayGames.ServerAuthCode),
        Wire.ProviderCredential.CredentialOneofCase.GameCenter => new GameCenterCredential(
            credential.GameCenter.PublicKeyUrl,
            credential.GameCenter.Signature.ToByteArray(),
            credential.GameCenter.Salt.ToByteArray(),
            credential.GameCenter.Timestamp,
            credential.GameCenter.TeamPlayerId,
            credential.GameCenter.GamePlayerId,
            credential.GameCenter.BundleId),
        Wire.ProviderCredential.CredentialOneofCase.Custom when ProviderIdentities.IsValidProvider(credential.Custom.Provider) =>
            new CustomCredential(credential.Custom.Provider, credential.Custom.Data.ToByteArray()),
        _ => throw new GameServerException(CoreErrorCodes.BadRequest, "credential must be play_games, game_center or custom with a valid provider."),
    };
}

/// <summary>Body of <c>POST /gm/v1/players/{playerId}/ban</c>; no end time = permanent, no scope = login.</summary>
public sealed record BanBody(string? Reason, DateTimeOffset? UntilUtc, [property: GmOptions("identity.ban_scopes")] List<string>? Scope) : IGmCommandBody;

public sealed record UnbanBody(string? Reason) : IGmCommandBody;

/// <summary>Body of <c>POST /gm/v1/players/{playerId}/deletion/cancel</c>.</summary>
public sealed record CancelDeletionBody(string? Reason) : IGmCommandBody;
