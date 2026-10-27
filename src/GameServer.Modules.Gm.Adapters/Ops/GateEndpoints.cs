using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform;
using GameServer.Platform.Gate;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameServer.Modules.Gm;

public sealed record MaintenanceBody(bool Enabled, string? Message, DateTimeOffset? ExpectedEndUtc, string? Reason);

public sealed record MinClientVersionBody(string? Platform, string? MinVersion, string? UpdateUrl, string? Reason);

public sealed record GateView(MaintenanceState Maintenance, IReadOnlyDictionary<string, MinClientVersion> MinClientVersions, long Rev, bool Replayed = false);

/// <summary>
/// GM API of the <c>gate</c> document (GATE-01, GATE-02): every change in one audited transaction (research R1),
/// idempotent per <c>X-Request-Id</c>, applied on this instance at once and on the others within the gate cache (≤ 10 s).
/// </summary>
public static class GateEndpoints
{
    public const string GateRoute = "/gm/v1/ops/gate";
    public const string MaintenanceRoute = "/gm/v1/ops/gate/maintenance";
    public const string MinClientVersionRoute = "/gm/v1/ops/gate/min-client-version";

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(GateRoute, (Func<HttpContext, Task<IResult>>)(async http =>
            {
                var gate = GateDocument.Parse(await http.RequestServices.GetRequiredService<IKeyValueStore>().GetAsync(GateDocument.Key, http.RequestAborted));
                return GmHttp.Json(View(gate));
            }))
            .Produces<GateView>()
            .AsGmEndpoint(GmPermissions.OpsRead);

        routes.MapPut(MaintenanceRoute, (Func<HttpContext, Task<IResult>>)(async http =>
            {
                var body = await GmHttp.ReadJsonAsync<MaintenanceBody>(http);
                if (body.Message is { Length: > 500 })
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "message must be at most 500 characters.");
                }

                var maintenance = new MaintenanceState
                {
                    Enabled = body.Enabled,
                    Message = string.IsNullOrWhiteSpace(body.Message) ? null : body.Message.Trim(),
                    ExpectedEndUtc = body.ExpectedEndUtc?.ToUniversalTime(),
                };
                return GmHttp.Json(await ChangeAsync(http, "ops.gate.maintenance", body.Reason, new { maintenance }, gate => gate with { Maintenance = maintenance }));
            }))
            .Accepts<MaintenanceBody>(GmHttp.JsonContentType)
            .Produces<GateView>()
            .AsGmEndpoint(GmPermissions.OpsMaintenance);

        routes.MapPut(MinClientVersionRoute, (Func<HttpContext, Task<IResult>>)(async http =>
            {
                var body = await GmHttp.ReadJsonAsync<MinClientVersionBody>(http);
                var platform = body.Platform?.Trim().ToLowerInvariant();
                if (!GateDocument.IsValidPlatform(platform))
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "platform must be 1..32 of [a-z0-9_-].");
                }

                if (body.MinVersion is not null && !ClientVersion.TryParse(body.MinVersion, out _))
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "minVersion must look like 1.2 or 1.2.3 (numbers only); null removes the minimum.");
                }

                var development = http.RequestServices.GetRequiredService<GameServerOptions>().IsDevelopment;
                if (body.UpdateUrl is not null && (!Uri.TryCreate(body.UpdateUrl, UriKind.Absolute, out var url)
                    || (url.Scheme != Uri.UriSchemeHttps && !(development && url.Scheme == Uri.UriSchemeHttp))
                    || body.UpdateUrl.Length > 512))
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "updateUrl must be an absolute https URL (at most 512 characters).");
                }

                return GmHttp.Json(await ChangeAsync(http, "ops.gate.min_client_version", body.Reason, new { platform, body.MinVersion, body.UpdateUrl }, gate =>
                {
                    var versions = new Dictionary<string, MinClientVersion>(gate.MinClientVersions, StringComparer.Ordinal);
                    if (body.MinVersion is null)
                    {
                        versions.Remove(platform!);
                    }
                    else
                    {
                        versions[platform!] = new MinClientVersion { MinVersion = body.MinVersion, UpdateUrl = body.UpdateUrl };
                    }

                    return gate with { MinClientVersions = versions };
                }));
            }))
            .Accepts<MinClientVersionBody>(GmHttp.JsonContentType)
            .Produces<GateView>()
            .AsGmEndpoint(GmPermissions.OpsMaintenance);
    }

    private static async Task<GateView> ChangeAsync(HttpContext http, string operation, string? reason, object body, Func<GateDocument, GateDocument> mutate)
    {
        var services = http.RequestServices;
        var change = StaffEndpoints.Change(http, reason);
        var (auditKey, build) = services.GetRequiredService<StaffAdminService>().SystemAudit(change, operation, body);
        var bodyHash = StaffAdminService.BodyHash(body);
        var audited = services.GetRequiredService<IAuditedStore>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (gate, replayed) = await audited.RunAsync(async tx =>
                {
                    var read = await tx.GetManyAsync([GateDocument.Key, auditKey]);
                    var current = GateDocument.Parse(read[0]);
                    if (read[1] is { } existing)
                    {
                        if (existing.Content["payload"]?["bodyHash"]?.GetValue<string>() != bodyHash)
                        {
                            throw new GameServerException(CoreErrorCodes.IdempotencyPayloadMismatch, "X-Request-Id was already used with a different body.");
                        }

                        return (current, true);
                    }

                    var next = mutate(current) with { Rev = current.Rev + 1 };
                    if (read[0] is { } document)
                    {
                        await tx.ReplaceAsync(document, next.ToJson());
                    }
                    else
                    {
                        await tx.InsertAsync(GateDocument.Key, next.ToJson());
                    }

                    var audit = build(next.ToJson());
                    audit["payload"]!["before"] = current.ToJson();
                    await tx.InsertAsync(auditKey, audit);
                    return (next, false);
                });

                (services.GetService<IGateEvaluator>() as StoreGateEvaluator)?.Invalidate();
                if (!replayed)
                {
                    services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GateEndpoints))
                        .LogInformation("Gate changed by {ActorId}: {Operation} (rev {Rev}).", change.ActorId, operation, gate.Rev);
                }

                return View(gate, replayed);
            }
            catch (DocumentExistsException ex) when (attempt < 3 && (ex.Key == GateDocument.Key || ex.Key == auditKey))
            {
                // Concurrent first write of the gate, or the same request committed in parallel: read again.
            }
        }
    }

    private static GateView View(GateDocument gate, bool replayed = false) => new(gate.Maintenance, gate.MinClientVersions, gate.Rev, replayed);
}
