using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Modules.Gm;

/// <summary>Staff as returned by the GM API: no password hash, session epoch or OIDC subject.</summary>
public sealed record StaffView(
    string StaffId,
    string DisplayName,
    string? Email,
    string Status,
    bool Disabled,
    bool OidcBound,
    IReadOnlyList<string> Permissions,
    string? UpdatedAtUtc,
    bool Replayed = false);

public sealed record CreateStaffBody(
    string? StaffId,
    string? DisplayName,
    string? Email,
    string? Issuer,
    string? Subject,
    List<string>? Roles,
    List<string>? Permissions,
    string? Reason);

public sealed record StaffGrantBody(List<string>? Roles, List<string>? Permissions, string? Reason);

public sealed record StaffReasonBody(string? Reason);

/// <summary>Staff management API (SEC-15, SEC-10, SEC-14 — contract gm-security-api.md).</summary>
public static class StaffEndpoints
{
    public const string ListRoute = "/gm/v1/staff";
    public const string StaffRoute = "/gm/v1/staff/{staffId}";
    public const string AuditRoute = "/gm/v1/staff/{staffId}/audit";
    public const string GrantRoute = "/gm/v1/staff/{staffId}/grant";
    public const string DisableRoute = "/gm/v1/staff/{staffId}/disable";
    public const string EnableRoute = "/gm/v1/staff/{staffId}/enable";
    public const string RevokeSessionsRoute = "/gm/v1/staff/{staffId}/sessions/revoke";

    public static void Map(IEndpointRouteBuilder routes)
    {
        // Explicit delegate types: a lambda taking only HttpContext would bind to RequestDelegate and drop its result.
        routes.MapGet(ListRoute, (Func<HttpContext, Task<IResult>>)(async http =>
                GmHttp.Json(await GmHttp.QueryAsync(http, StaffDirectory.Collection, "type", StaffRecord.DocumentType, "staffId", descending: false, d => View(http, StaffDirectory.Parse(d)!)))))
            .Produces<GmPage<StaffView>>()
            .AsGmEndpoint(GmPermissions.StaffRead);

        routes.MapGet(StaffRoute, async (HttpContext http, string staffId) =>
                GmHttp.Json(View(http, await GetAsync(http, RequireStaffId(staffId)))))
            .Produces<StaffView>()
            .AsGmEndpoint(GmPermissions.StaffRead);

        routes.MapGet(AuditRoute, async (HttpContext http, string staffId) =>
                GmHttp.Json(await GmHttp.QueryAsync(http, GmModule.AuditCollection, "targetStaffId", RequireStaffId(staffId), "committedAtUtc", descending: true, d => d.Content)))
            .AsGmEndpoint(GmPermissions.StaffRead);

        routes.MapPost(ListRoute, (Func<HttpContext, Task<IResult>>)(async http =>
            {
                var body = await GmHttp.ReadJsonAsync<CreateStaffBody>(http);
                var staffId = RequireStaffId(body.StaffId);
                if ((body.Issuer is null) != (body.Subject is null))
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "issuer and subject go together.");
                }

                if (string.IsNullOrWhiteSpace(body.Email) && body.Issuer is null)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "An email invitation or an issuer + subject is required.");
                }

                if (body.Email is { } email && (email.Length > 254 || !email.Contains('@', StringComparison.Ordinal)))
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "email is not valid.");
                }

                var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? staffId : body.DisplayName.Trim();
                if (displayName.Length > 100)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "displayName must be 1..100 characters.");
                }

                var oidc = body.Issuer is null ? null : new StaffOidcIdentity { Issuer = body.Issuer, Subject = body.Subject! };
                var result = await Service(http).CreateAsync(
                    Change(http, body.Reason),
                    new NewStaff(staffId, displayName, body.Email, oidc, body.Roles ?? [], body.Permissions ?? []),
                    http.RequestAborted);
                return Results.Json(View(http, result.Staff, result.Replayed), GameServerJson.Options, statusCode: result.Replayed ? 200 : 201);
            }))
            .Accepts<CreateStaffBody>(GmHttp.JsonContentType)
            .Produces<StaffView>(StatusCodes.Status201Created)
            .AsGmEndpoint(GmPermissions.StaffManage);

        routes.MapPut(GrantRoute, async (HttpContext http, string staffId) =>
            {
                var body = await GmHttp.ReadJsonAsync<StaffGrantBody>(http);
                var result = await Service(http).SetGrantAsync(Change(http, body.Reason), RequireStaffId(staffId), body.Roles ?? [], body.Permissions ?? [], http.RequestAborted);
                return GmHttp.Json(View(http, result.Staff, result.Replayed));
            })
            .Accepts<StaffGrantBody>(GmHttp.JsonContentType)
            .Produces<StaffView>()
            .AsGmEndpoint(GmPermissions.StaffManage);

        routes.MapPost(DisableRoute, (HttpContext http, string staffId) => SetDisabledAsync(http, staffId, disabled: true))
            .Accepts<StaffReasonBody>(GmHttp.JsonContentType).Produces<StaffView>().AsGmEndpoint(GmPermissions.StaffManage);
        routes.MapPost(EnableRoute, (HttpContext http, string staffId) => SetDisabledAsync(http, staffId, disabled: false))
            .Accepts<StaffReasonBody>(GmHttp.JsonContentType).Produces<StaffView>().AsGmEndpoint(GmPermissions.StaffManage);

        // gm.access: revoking one's own sessions needs no management permission; anyone else's does (checked below).
        routes.MapPost(RevokeSessionsRoute, async (HttpContext http, string staffId) =>
            {
                var target = RequireStaffId(staffId);
                var actor = http.RequestServices.GetRequiredService<ActorContextAccessor>().GetActor(http);
                if (target != actor.ActorId && !actor.HasPermission(GmPermissions.StaffManage))
                {
                    throw new GameServerException(CoreErrorCodes.Forbidden, $"Revoking sessions of another staff member requires {GmPermissions.StaffManage}.");
                }

                var body = await GmHttp.ReadJsonAsync<StaffReasonBody>(http);
                var result = await Service(http).RevokeSessionsAsync(Change(http, body.Reason), target, http.RequestAborted);
                return GmHttp.Json(View(http, result.Staff, result.Replayed));
            })
            .Accepts<StaffReasonBody>(GmHttp.JsonContentType)
            .Produces<StaffView>()
            .AsGmEndpoint(GmPermissions.Access);
    }

    private static async Task<IResult> SetDisabledAsync(HttpContext http, string staffId, bool disabled)
    {
        var body = await GmHttp.ReadJsonAsync<StaffReasonBody>(http);
        var result = await Service(http).SetDisabledAsync(Change(http, body.Reason), RequireStaffId(staffId), disabled, http.RequestAborted);
        return GmHttp.Json(View(http, result.Staff, result.Replayed));
    }

    /// <summary>The acting staff, its effective permissions (escalation check) and the mandatory request id.</summary>
    public static StaffChange Change(HttpContext http, string? reason)
    {
        var actor = http.RequestServices.GetRequiredService<ActorContextAccessor>().GetActor(http);
        var requestId = CorrelationMiddleware.GetClientRequestId(http)
            ?? throw new GameServerException(CoreErrorCodes.BadRequest, $"Header {CorrelationMiddleware.HeaderName} must be a UUID for GM mutations.");
        return new StaffChange(actor.ActorId, actor.Permissions, requestId, reason?.Trim() ?? string.Empty, actor.CorrelationId);
    }

    private static StaffAdminService Service(HttpContext http) => http.RequestServices.GetRequiredService<StaffAdminService>();

    private static async Task<StaffRecord> GetAsync(HttpContext http, string staffId) =>
        StaffDirectory.Parse(await http.RequestServices.GetRequiredService<GameServer.Platform.Persistence.IKeyValueStore>()
            .GetAsync(StaffDirectory.Key(staffId), http.RequestAborted))
        ?? throw new GameServerException(CoreErrorCodes.NotFound, $"Staff '{staffId}' does not exist.");

    private static string RequireStaffId(string? staffId) =>
        staffId is not null && GmOptionsValidator.StaffIdPattern().IsMatch(staffId)
            ? staffId
            : throw new GameServerException(CoreErrorCodes.BadRequest, "staffId must be 1..64 of [a-z0-9._-].");

    private static StaffView View(HttpContext http, StaffRecord staff, bool replayed = false)
    {
        var gameServer = http.RequestServices.GetRequiredService<GameServerOptions>();
        return new StaffView(
            staff.StaffId,
            staff.DisplayName,
            staff.Email,
            staff.Status,
            staff.Disabled,
            staff.Oidc is not null,
            staff.EffectivePermissions(gameServer.GameId, gameServer.EnvironmentId).Order(StringComparer.Ordinal).ToArray(),
            staff.UpdatedAtUtc,
            replayed);
    }
}
