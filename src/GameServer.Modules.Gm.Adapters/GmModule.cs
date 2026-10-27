using GameServer.Commands;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Platform.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

public sealed record GmLoginRequest(string? StaffId, string? Password);

public sealed record GmCsrfResponse(string Token);

public sealed record GmHealthEntry(
    string Name,
    string Status,
    string? Description,
    double DurationMs,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string?> Data);

public sealed record GmHealthReport(string Status, double DurationMs, IReadOnlyList<GmHealthEntry> Entries);

public sealed record GmMeResponse(string StaffId, string DisplayName, string GameId, string EnvironmentId, IReadOnlyList<string> Permissions);

public sealed record GmRotateResponse(string? KeyId, string? ActivatesAtUtc, bool Replayed);

/// <summary>
/// Framework GM module: staff and grants, <c>GmCookie</c> scheme (protected cookie + server-side ticket), SSO (OIDC),
/// Development local login, CSRF, staff management, player lookup, audit projection and the static GM page
/// (slices 004 and 007).
/// </summary>
public sealed class GmModule : IGameModule, IGmApiModule
{
    public const string ModuleName = "gm";
    public const string AuditCollection = "gm_audit";
    public const string CsrfRoute = "/gm/v1/auth/csrf";
    public const string LoginRoute = "/gm/v1/auth/login";
    public const string LogoutRoute = "/gm/v1/auth/logout";
    public const string MeRoute = "/gm/v1/me";
    public const string UiRoute = "/gm/v1/ui";
    public const string HealthRoute = "/gm/v1/health";
    public const string RotateKeysRoute = "/gm/v1/ops/dataprotection/rotate";

    public ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.7.0",
        DependsOn = ["identity"],
        Permissions = GmPermissions.All,
        ErrorCodes = GmErrorCodes.All,
        Collections =
        [
            new CollectionSpec(StaffDirectory.Collection, [new IndexSpec("ix_gm_staff_type", "type, staffId, META().id")], TransactionOnly: true),
            new CollectionSpec(StaffAdminService.IdentityCollection, TransactionOnly: true),
            new CollectionSpec(GmSessionService.Collection),
            new CollectionSpec(
                AuditCollection,
                [
                    new IndexSpec("ix_gm_audit_player", "playerId, committedAtUtc DESC, META().id DESC"),
                    new IndexSpec("ix_gm_audit_staff", "targetStaffId, committedAtUtc DESC, META().id DESC"),
                ]),
            new CollectionSpec(ReceiptDocument.Collection, [new IndexSpec("ix_receipts_player", "playerId, committedAtUtc DESC, META().id DESC")]),
            new CollectionSpec(
                IdentityAggregates.ProfileDescriptor.Collection,
                [new IndexSpec("ix_profile_display_name", "`state`.`displayName`, playerId, META().id")]),
        ],
        OutboxProjections = [new OutboxProjection(AuditExtensions.OutboxKind, AuditCollection)],
        PublicEndpoints = [CsrfRoute, LoginRoute, UiRoute, GmSso.StartRoute, GmSso.CallbackRoute],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        services.AddOptions<GmOptions>().BindConfiguration(GmOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GmOptions>, GmOptionsValidator>();
        services.AddSingleton<StaffDirectory>();
        services.AddSingleton<StaffAdminService>();
        services.AddSingleton<GmSessionService>();
        services.AddHostedService<GmBootstrapper>();
        services.AddSingleton<IGmTargetValidator, GmTargetValidator>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, GmCookieAuthenticationHandler>(GmCookieAuthenticationHandler.SchemeName, null);
        services.Configure<ActorSchemeOptions>(o => o.GmSchemes.Insert(0, GmCookieAuthenticationHandler.SchemeName));
        GmCookies.Configure(services);
        GmCors.Add(services);
        GmSso.Add(services);
    }

    public void MapGmApi(IEndpointRouteBuilder routes)
    {
        var services = routes.ServiceProvider;
        if (services.GetRequiredService<GameServerOptions>().AllowsDevelopmentTools)
        {
            // Local accounts exist only in Development/Test; other environments sign in through SSO (SEC-03).
            routes.MapPost(LoginRoute, (Func<HttpContext, Task<IResult>>)LoginAsync)
                .Accepts<GmLoginRequest>(GmHttp.JsonContentType)
                .Produces<GmMeResponse>()
                .AsPublicEndpoint(protobuf: false)
                .RequireCsrf()
                .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy)
                .RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy);
        }

        if (services.GetRequiredService<IOptions<GmOptions>>().Value.Oidc.IsConfigured)
        {
            GmSso.Map(routes);
        }

        routes.MapGet(CsrfRoute, (Func<HttpContext, Task<IResult>>)CsrfAsync)
            .Produces<GmCsrfResponse>()
            .AsPublicEndpoint(protobuf: false)
            .WithRequestTimeout(ApiRouteExtensions.GmTimeoutPolicy)
            .RequireRateLimiting(ApiRouteExtensions.GmLimiterPolicy);
        routes.MapPost(LogoutRoute, (Func<HttpContext, Task<IResult>>)LogoutAsync).Produces(StatusCodes.Status204NoContent).AsGmEndpoint(GmPermissions.Access);
        routes.MapGet(MeRoute, (HttpContext http) => GmHttp.Json(Me(http))).Produces<GmMeResponse>().AsGmEndpoint(GmPermissions.Access);
        routes.MapGet(UiRoute, GmUi.Serve).ExcludeFromDescription().AsPublicEndpoint(protobuf: false);
        routes.MapGet(HealthRoute, (Func<HttpContext, Task<IResult>>)HealthAsync).Produces<GmHealthReport>().AsGmEndpoint(GmPermissions.OpsRead);
        routes.MapPost(RotateKeysRoute, (Func<HttpContext, Task<IResult>>)RotateKeysAsync)
            .Accepts<StaffReasonBody>(GmHttp.JsonContentType)
            .Produces<GmRotateResponse>()
            .AsGmEndpoint(GmPermissions.KeysRotate);
        StaffEndpoints.Map(routes);
        GateEndpoints.Map(routes);
        GmPlayerEndpoints.Map(routes);
    }

    /// <summary>
    /// CSRF token for the caller (SEC-05). Tokens are bound to the GM session, so the client fetches a new one after
    /// login and logout.
    /// </summary>
    private static async Task<IResult> CsrfAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(GmCookieAuthenticationHandler.SchemeName);
        if (result.Succeeded)
        {
            http.User = result.Principal;
        }

        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        return GmHttp.Json(new GmCsrfResponse(tokens.RequestToken!));
    }

    private static async Task<IResult> LoginAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var request = await GmHttp.ReadJsonAsync<GmLoginRequest>(http);
        if (string.IsNullOrEmpty(request.StaffId) || string.IsNullOrEmpty(request.Password) || request.StaffId.Length > 64 || request.Password.Length > 256)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "staffId and password are required.");
        }

        var directory = services.GetRequiredService<StaffDirectory>();
        directory.Invalidate(request.StaffId);
        var record = await directory.GetAsync(request.StaffId, http.RequestAborted);

        // Verify even for unknown staff so response time does not reveal which ids exist.
        var passwordOk = DevPasswordHasher.Verify(request.Password, record?.PasswordHash ?? DummyHash.Value);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<GmModule>();
        if (record is null || !record.CanSignIn || !passwordOk)
        {
            logger.LogWarning("GM local sign-in refused.");
            throw new GameServerException(CoreErrorCodes.Unauthenticated, "Invalid staff credentials.");
        }

        var sessions = services.GetRequiredService<GmSessionService>();
        await sessions.EndCurrentAsync(http);
        var (cookie, expiresAt) = await sessions.CreateAsync(record, http.RequestAborted);
        http.Response.Cookies.Append(GmCookieAuthenticationHandler.CookieName, cookie, GmCookies.Session(http, expiresAt));
        logger.LogInformation("GM local sign-in for staff {StaffId}.", record.StaffId);

        var gameServer = services.GetRequiredService<GameServerOptions>();
        return GmHttp.Json(new GmMeResponse(
            record.StaffId,
            record.DisplayName,
            gameServer.GameId,
            gameServer.EnvironmentId,
            record.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Order(StringComparer.Ordinal).ToArray()));
    }

    /// <summary>POST only (SEC-05): ends the server-side ticket, so a copied cookie stops working on every instance.</summary>
    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        var sessionKey = http.User.FindFirst(GmCookieAuthenticationHandler.SessionClaim)?.Value;
        if (sessionKey is not null)
        {
            await http.RequestServices.GetRequiredService<GmSessionService>().RemoveAsync(sessionKey, http.RequestAborted);
        }

        http.Response.Cookies.Delete(GmCookieAuthenticationHandler.CookieName, GmCookies.Session(http, null));
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<GmModule>()
            .LogInformation("GM sign-out for staff {StaffId}.", http.User.FindFirst(ActorClaims.ActorId)?.Value);
        return Results.NoContent();
    }

    /// <summary>
    /// Schedules a new Data Protection key (OPS-04). It activates after the configured delay so every instance loads
    /// it first; old keys stay in the ring. Audited; a retried request id does not rotate twice.
    /// </summary>
    private static async Task<IResult> RotateKeysAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var body = await GmHttp.ReadJsonAsync<StaffReasonBody>(http);
        var change = StaffEndpoints.Change(http, body.Reason);
        var (auditKey, build) = services.GetRequiredService<StaffAdminService>().SystemAudit(change, "ops.keys.rotate");

        // The key ring and the audit entry commit in one transaction; the audit key makes a retry a replay.
        var rotation = await services.GetRequiredService<KeyRingService>().RotateAsync(new KeyRingAudit(auditKey, build), CancellationToken.None);
        return GmHttp.Json(new GmRotateResponse(rotation.KeyId?.ToString("D"), rotation.ActivatesAt?.ToString("O"), rotation.Replayed));
    }

    /// <summary>Per-check health with descriptions (OBS-03: details only for GM, never on the Public endpoints).</summary>
    private static async Task<IResult> HealthAsync(HttpContext http)
    {
        var health = http.RequestServices.GetService<HealthCheckService>()
            ?? throw new GameServerException(CoreErrorCodes.NotFound, "Health checks are not configured on this host.");
        var report = await health.CheckHealthAsync(http.RequestAborted);
        return GmHttp.Json(new GmHealthReport(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            report.Entries.Select(e => new GmHealthEntry(
                e.Key,
                e.Value.Status.ToString(),
                e.Value.Description,
                e.Value.Duration.TotalMilliseconds,
                e.Value.Tags.ToArray(),
                e.Value.Data.ToDictionary(d => d.Key, d => d.Value?.ToString()))).ToArray()));
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

    private static readonly Lazy<string> DummyHash = new(() => DevPasswordHasher.Hash(Guid.NewGuid().ToString("N")));
}
