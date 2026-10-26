using System.Text;
using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Modules.Gm;

/// <summary>One aggregate as GM sees it: revision, size, schema and state (GM-02).</summary>
public sealed record GmAggregateView(
    string Id,
    bool Exists,
    bool ClientVisible,
    long Rev,
    int SchemaVersion,
    int StoredSchemaVersion,
    int SizeBytes,
    string? UpdatedAtUtc,
    JsonNode? State);

public sealed record GmPlayerView(string PlayerId, IReadOnlyList<GmAggregateView> Aggregates);

public sealed record GmPlayerSearchItem(string PlayerId, string DisplayName);

public sealed record GmReceiptSummary(
    string ReceiptId,
    string Operation,
    string ActorType,
    string ActorId,
    string ResultCode,
    string CommittedAtUtc,
    IReadOnlyList<ReceiptAggregate> Aggregates,
    bool OutboxPending,
    bool OutboxDeadLetter,
    string CorrelationId);

/// <summary>Player lookup, search, receipts and audit (FR-006, FR-007). Lists are keyset-paged (GM-06).</summary>
public static class GmPlayerEndpoints
{
    public const string PlayerRoute = "/gm/v1/players/{playerId}";
    public const string MappingRoute = "/gm/v1/players/by-mapping/{mappingKey}";
    public const string SearchRoute = "/gm/v1/players/search";
    public const string ReceiptsRoute = "/gm/v1/players/{playerId}/receipts";
    public const string AuditRoute = "/gm/v1/players/{playerId}/audit";
    public const string ReceiptRoute = "/gm/v1/receipts/{receiptId}";

    private const string IdentitiesField = "identities";

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(MappingRoute, async (HttpContext http, string mappingKey) =>
            {
                if (mappingKey.Length is 0 or > 256)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid mapping key.");
                }

                var mapping = await http.RequestServices.GetRequiredService<IKeyValueStore>()
                    .GetAsync(new DocumentKey(IdentityCollections.Identities, mappingKey), http.RequestAborted);
                var playerId = mapping?.Content["playerId"]?.GetValue<string>()
                    ?? throw new GameServerException(CoreErrorCodes.NotFound, "No player for this mapping key.");
                return GmHttp.Json(await LookupAsync(http, playerId));
            })
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapGet(SearchRoute, async (HttpContext http) =>
            {
                var displayName = http.Request.Query["displayName"].ToString();
                if (displayName.Length is 0 or > 64)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "displayName is required (1..64 characters).");
                }

                return GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    IdentityAggregates.ProfileDescriptor.Collection,
                    "state.displayName",
                    displayName,
                    "playerId",
                    descending: false,
                    d => new GmPlayerSearchItem(
                        d.Content["playerId"]?.GetValue<string>() ?? d.Key.Key,
                        d.Content["state"]?["displayName"]?.GetValue<string>() ?? string.Empty)));
            })
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapGet(PlayerRoute, async (HttpContext http, string playerId) => GmHttp.Json(await LookupAsync(http, ValidPlayerId(playerId))))
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapGet(ReceiptsRoute, async (HttpContext http, string playerId) =>
                GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    ReceiptDocument.Collection,
                    "playerId",
                    ValidPlayerId(playerId),
                    "committedAtUtc",
                    descending: true,
                    d => Summarize(ReceiptDocument.FromJson(d.Content)))))
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapGet(AuditRoute, async (HttpContext http, string playerId) =>
                GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    GmModule.AuditCollection,
                    "playerId",
                    ValidPlayerId(playerId),
                    "committedAtUtc",
                    descending: true,
                    d => d.Content)))
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapGet(ReceiptRoute, async (HttpContext http, string receiptId) =>
            {
                if (receiptId.Length is 0 or > 128)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid receiptId.");
                }

                var receipt = await http.RequestServices.GetRequiredService<ReceiptReader>().GetAsync(receiptId, http.RequestAborted)
                    ?? throw new GameServerException(CoreErrorCodes.NotFound, "Receipt not found.");
                return GmHttp.Json(receipt);
            })
            .AsGmEndpoint(GmPermissions.PlayerRead);
    }

    private static async Task<GmPlayerView> LookupAsync(HttpContext http, string playerId)
    {
        var services = http.RequestServices;
        var aggregates = await services.GetRequiredService<PlayerSnapshotReader>().ReadAllAsync(playerId, http.RequestAborted);
        if (!aggregates.Any(a => a.Exists))
        {
            throw new GameServerException(CoreErrorCodes.NotFound, "Player not found.");
        }

        var showPii = services.GetRequiredService<ActorContextAccessor>().GetActor(http).HasPermission(GmPermissions.PlayerReadPii);
        return new GmPlayerView(playerId, aggregates.Select(a => View(a, showPii)).ToArray());
    }

    private static GmAggregateView View(LoadedAggregate aggregate, bool showPii)
    {
        var state = JsonNode.Parse(aggregate.StateJson);
        if (!showPii && aggregate.Descriptor.AggregateId == IdentityAggregates.Profile && state is JsonObject profile)
        {
            // Identity mapping keys carry provider/local ids: PII, shown only with player.read.pii (GM-02).
            profile.Remove(IdentitiesField);
        }

        var content = aggregate.Document?.Content;
        return new GmAggregateView(
            aggregate.Descriptor.AggregateId,
            aggregate.Exists,
            aggregate.Descriptor.ClientVisible,
            aggregate.Rev,
            aggregate.Descriptor.SchemaVersion,
            aggregate.StoredSchemaVersion,
            content is null ? 0 : Encoding.UTF8.GetByteCount(content.ToJsonString(GameServerJson.Options)),
            content?["updatedAtUtc"]?.GetValue<string>(),
            state);
    }

    private static GmReceiptSummary Summarize(ReceiptDocument receipt) =>
        new(
            receipt.ReceiptId,
            receipt.Operation,
            receipt.ActorType,
            receipt.ActorId,
            receipt.ResultCode,
            receipt.CommittedAtUtc,
            receipt.Aggregates,
            receipt.OutboxPending,
            receipt.OutboxDeadLetter,
            receipt.CorrelationId);

    private static string ValidPlayerId(string playerId) => GmHttp.RequirePlayerId(playerId);
}
