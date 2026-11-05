using System.Text;
using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Privacy;
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

/// <summary>
/// Account deletion of a player as GM sees it (PRIV-01, FR-012): <c>pending</c> and <c>purging</c> come from
/// <c>profile.deleted</c>, <c>purged</c> from the purge record. Times are UTC strings; the anonymous id is never shown.
/// </summary>
public sealed record GmDeletionView(
    string State,
    string RequestedAtUtc,
    string PurgeAfterUtc,
    string? PurgeStartedAtUtc,
    string? PurgedAtUtc)
{
    public const string Pending = "pending";
    public const string Purging = "purging";
    public const string Purged = "purged";

    public static GmDeletionView? Of(ProfileState profile) => profile.Deleted is not { } deleted
        ? null
        : new GmDeletionView(
            deleted.PurgeStartedAtUtc is null ? Pending : Purging,
            AggregateDocumentSerializer.FormatTime(deleted.RequestedAtUtc),
            AggregateDocumentSerializer.FormatTime(deleted.PurgeAfterUtc),
            deleted.PurgeStartedAtUtc is { } started ? AggregateDocumentSerializer.FormatTime(started) : null,
            null);

    public static GmDeletionView Of(AccountDeletionRecord record) =>
        new(
            record.State == AccountDeletionState.Purged ? Purged : Purging,
            AggregateDocumentSerializer.FormatTime(record.RequestedAtUtc),
            AggregateDocumentSerializer.FormatTime(record.PurgeAfterUtc),
            AggregateDocumentSerializer.FormatTime(record.PurgeStartedAtUtc),
            record.PurgedAtUtc is { } purged ? AggregateDocumentSerializer.FormatTime(purged) : null);
}

/// <summary>
/// A player as GM sees it; <see cref="Deletion"/> is null when the player never requested deletion or when the profile cannot be
/// read. <see cref="Unreadable"/> lists the stored aggregates that cannot be read (FG-029); they are not in <see cref="Aggregates"/>.
/// </summary>
public sealed record GmPlayerView(string PlayerId, IReadOnlyList<GmAggregateView> Aggregates, GmDeletionView? Deletion)
{
    /// <summary>Always present (empty when every aggregate reads), ordered by id (wire-changes §4.1).</summary>
    public IReadOnlyList<GmUnreadableAggregate> Unreadable { get; init; } = [];
}

/// <summary>
/// A stored aggregate GM cannot read (FG-029): the code a reader gets, the reason (<c>version_ahead</c>, <c>upgrade_failed</c>,
/// <c>unreadable</c>), and the stored and code schema versions (0 stored when the envelope cannot be read).
/// </summary>
public sealed record GmUnreadableAggregate(string Id, string Code, string Reason, int StoredSchemaVersion, int CodeSchemaVersion);

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
        routes.MapDeclared(GmRoutes.PlayerByMapping, async (HttpContext http, string mappingKey) =>
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
            .Produces<GmPlayerView>()
            .WithSummary("Find a player by an identity mapping key and show its aggregates.")
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapDeclared(GmRoutes.PlayerSearch, async (HttpContext http) =>
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
            .ProducesGmPage<GmPlayerSearchItem>()
            .WithGmQueryParameter("displayName", required: true, maxLength: 64, minLength: 1)
            .WithSummary("Search players by exact display name (paged).")
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapDeclared(GmRoutes.Player, async (HttpContext http, string playerId) => GmHttp.Json(await LookupAsync(http, ValidPlayerId(playerId))))
            .Produces<GmPlayerView>()
            .WithSummary("Show a player's aggregates and account deletion state.")
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapDeclared(GmRoutes.PlayerReceipts, async (HttpContext http, string playerId) =>
                GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    ReceiptDocument.Collection,
                    "playerId",
                    ValidPlayerId(playerId),
                    "committedAtUtc",
                    descending: true,
                    d => Summarize(ReceiptDocument.FromJson(d.Content)))))
            .ProducesGmPage<GmReceiptSummary>()
            .WithSummary("List a player's command receipts, newest first (paged).")
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapDeclared(GmRoutes.PlayerAudit, async (HttpContext http, string playerId) =>
                GmHttp.Json(await GmHttp.QueryAsync(
                    http,
                    GmModule.AuditCollection,
                    "playerId",
                    ValidPlayerId(playerId),
                    "committedAtUtc",
                    descending: true,
                    d => d.Content)))
            .ProducesGmPage<JsonObject>()
            .WithSummary("List the GM audit entries of a player, newest first (paged).")
            .AsGmEndpoint(GmPermissions.PlayerRead);

        routes.MapDeclared(GmRoutes.Receipt, async (HttpContext http, string receiptId) =>
            {
                if (receiptId.Length is 0 or > 128)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid receiptId.");
                }

                var receipt = await http.RequestServices.GetRequiredService<ReceiptReader>().GetAsync(receiptId, http.RequestAborted)
                    ?? throw new GameServerException(CoreErrorCodes.NotFound, "Receipt not found.");
                return GmHttp.Json(receipt);
            })
            .WithSummary("Show one command receipt.")
            .AsGmEndpoint(GmPermissions.PlayerRead);
    }

    private static async Task<GmPlayerView> LookupAsync(HttpContext http, string playerId)
    {
        var services = http.RequestServices;
        var snapshot = await services.GetRequiredService<PlayerSnapshotReader>().ReadAllAvailableAsync(playerId, http.RequestAborted);
        var aggregates = snapshot.Sections;

        // A broken document still exists: such a player is not purged (wire-changes §4.1).
        if (!aggregates.Any(a => a.Exists) && snapshot.Unavailable.Count == 0)
        {
            // A purged player has no aggregate left but a purge record: 200 without data (PRIV-01, D7).
            var record = await services.GetRequiredService<AccountDeletionRecords>().GetAsync(playerId, http.RequestAborted)
                ?? throw new GameServerException(CoreErrorCodes.NotFound, "Player not found.");
            return new GmPlayerView(playerId, [], GmDeletionView.Of(record));
        }

        var showPii = services.GetRequiredService<ActorContextAccessor>().GetActor(http).HasPermission(GmPermissions.PlayerReadPii);
        var profile = aggregates.SingleOrDefault(a => a.Descriptor.AggregateId == IdentityAggregates.Profile);
        var deletion = profile is { Exists: true } ? GmDeletionView.Of((ProfileState)profile.State) : null;
        return new GmPlayerView(playerId, aggregates.Select(a => View(a, showPii)).ToArray(), deletion)
        {
            Unreadable = snapshot.Unavailable
                .Select(f => new GmUnreadableAggregate(f.AggregateId, f.Code.Name, f.ReasonName, f.StoredSchemaVersion, f.CodeSchemaVersion))
                .ToArray(),
        };
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
