using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

public sealed record GmLoginRequest(string? StaffId, string? Password);

public sealed record GmMeResponse(string StaffId, string DisplayName, string GameId, string EnvironmentId, IReadOnlyList<string> Permissions);

/// <summary>
/// Framework GM module (slice 004): staff and grants, <c>GmCookie</c> scheme, Development local login, player lookup,
/// audit projection and the static GM page. SSO, staff management UI and maintenance arrive in P0.5.
/// </summary>
public sealed class GmModule : IGameModule, IGmApiModule
{
    public const string ModuleName = "gm";
    public const string AuditCollection = "gm_audit";
    public const string LoginRoute = "/gm/v1/auth/login";
    public const string LogoutRoute = "/gm/v1/auth/logout";
    public const string MeRoute = "/gm/v1/me";
    public const string UiRoute = "/gm/v1/ui";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.4.0",
        DependsOn = ["identity"],
        Permissions = GmPermissions.All,
        Collections =
        [
            new CollectionSpec(StaffDirectory.Collection),
            new CollectionSpec(GmSessionService.Collection),
            new CollectionSpec(AuditCollection, [new IndexSpec("ix_gm_audit_player", "playerId, committedAtUtc")]),
            new CollectionSpec(ReceiptDocument.Collection, [new IndexSpec("ix_receipts_player", "playerId, committedAtUtc")]),
            new CollectionSpec(IdentityAggregates.ProfileDescriptor.Collection, [new IndexSpec("ix_profile_display_name", "`state`.`displayName`, playerId")]),
        ],
        OutboxProjections = [new OutboxProjection(AuditExtensions.OutboxKind, AuditCollection)],
        PublicEndpoints = [LoginRoute, UiRoute],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddOptions<GmOptions>().BindConfiguration(GmOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GmOptions>, GmOptionsValidator>();
        services.AddSingleton<StaffDirectory>();
        services.AddSingleton<GmSessionService>();
        services.AddHostedService<GmBootstrapper>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, GmCookieAuthenticationHandler>(GmCookieAuthenticationHandler.SchemeName, null);
        services.Configure<ActorSchemeOptions>(o => o.GmSchemes.Insert(0, GmCookieAuthenticationHandler.SchemeName));
    }

    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetRequiredService<GameServerOptions>().AllowsDevelopmentTools)
        {
            // Local accounts exist only in Development/Test; Production signs in through SSO (P0.5, SEC-03).
            routes.MapPost(LoginRoute, (Func<HttpContext, Task<IResult>>)LoginAsync)
                .AsPublicEndpoint(protobuf: false)
                .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy)
                .RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy);
        }

        routes.MapPost(LogoutRoute, (Func<HttpContext, Task<IResult>>)LogoutAsync).AsGmEndpoint(GmPermissions.Access);
        routes.MapGet(MeRoute, (HttpContext http) => GmHttp.Json(Me(http))).AsGmEndpoint(GmPermissions.Access);
        routes.MapGet(UiRoute, GmUi.Serve).AsPublicEndpoint(protobuf: false);
        GmPlayerEndpoints.Map(routes);
    }

    private static async Task<IResult> LoginAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var request = await GmHttp.ReadJsonAsync<GmLoginRequest>(http);
        if (string.IsNullOrEmpty(request.StaffId) || string.IsNullOrEmpty(request.Password) || request.StaffId.Length > 64 || request.Password.Length > 256)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "staffId and password are required.");
        }

        var record = await services.GetRequiredService<StaffDirectory>().GetAsync(request.StaffId, http.RequestAborted);

        // Verify even for unknown staff so response time does not reveal which ids exist.
        var passwordOk = DevPasswordHasher.Verify(request.Password, record?.PasswordHash ?? DummyHash.Value);
        if (record is null || record.Disabled || !passwordOk)
        {
            throw new GameServerException(CoreErrorCodes.Unauthenticated, "Invalid staff credentials.");
        }

        var (token, expiresAt) = await services.GetRequiredService<GmSessionService>().CreateAsync(record.StaffId, http.RequestAborted);
        http.Response.Cookies.Append(GmCookieAuthenticationHandler.CookieName, token, CookieOptions(http, expiresAt));

        var gameServer = services.GetRequiredService<GameServerOptions>();
        return GmHttp.Json(new GmMeResponse(
            record.StaffId,
            record.DisplayName,
            gameServer.GameId,
            gameServer.EnvironmentId,
            record.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Order(StringComparer.Ordinal).ToArray()));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        var sessionKey = http.User.FindFirst(GmCookieAuthenticationHandler.SessionClaim)?.Value;
        if (sessionKey is not null)
        {
            await http.RequestServices.GetRequiredService<GmSessionService>().RemoveAsync(sessionKey, http.RequestAborted);
        }

        http.Response.Cookies.Delete(GmCookieAuthenticationHandler.CookieName, CookieOptions(http, null));
        return Results.NoContent();
    }

    private static GmMeResponse Me(HttpContext http)
    {
        var actor = http.RequestServices.GetRequiredService<ActorContextAccessor>().GetActor(http);
        return new GmMeResponse(
            actor.ActorId,
            http.User.FindFirst(GmCookieAuthenticationHandler.DisplayNameClaim)?.Value ?? actor.ActorId,
            actor.GameId,
            actor.EnvironmentId,
            actor.Permissions.Order(StringComparer.Ordinal).ToArray());
    }

    private static CookieOptions CookieOptions(HttpContext http, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = http.Request.IsHttps,
        Path = "/gm",
        Expires = expiresAt,
        IsEssential = true,
    };

    private static readonly Lazy<string> DummyHash = new(() => DevPasswordHasher.Hash(Guid.NewGuid().ToString("N")));
}
