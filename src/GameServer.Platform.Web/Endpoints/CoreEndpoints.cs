using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Protocol.Core.V1;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Endpoints;

/// <summary>Framework endpoints: server time (TIME-01), snapshot (PLY-02, API-10), receipt lookup (CMD-09).</summary>
internal static class CoreEndpoints
{
    public const string TimeRoute = "/v1/time";
    public const string SnapshotRoute = "/api/v1/player/snapshot";
    public const string ReceiptRoute = "/api/v1/player/receipts/{receiptId}";

    public static void MapPublic(IEndpointRouteBuilder routes) =>
        routes.MapGet(TimeRoute, async (HttpContext http, IClock clock) =>
                await ProtobufBinding.WriteAsync(http, new TimeResponse { ServerTimeUtcMs = clock.UtcNow.ToUnixTimeMilliseconds() }))
            .AsPublicEndpoint(protobuf: true);

    public static void MapPlayer(IEndpointRouteBuilder routes)
    {
        routes.MapPost(SnapshotRoute, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var request = ProtobufBinding.Parse<SnapshotRequest>(await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes));
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var aggregates = await services.GetRequiredService<PlayerSnapshotReader>().ReadAsync(actor.PlayerId!, request.Sections, http.RequestAborted);
                var mappers = services.GetRequiredService<AggregateWireMapperRegistry>();

                var response = new SnapshotResponse
                {
                    ConfigVersion = services.GetRequiredService<IGameConfig>().ConfigVersion,
                    ServerTimeUtcMs = services.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds(),
                };
                response.Sections.AddRange(aggregates.Select(a => new Section
                {
                    Id = a.Descriptor.AggregateId,
                    Rev = a.Rev,
                    Payload = mappers.ToPayload(a.Descriptor.AggregateId, a.State),
                }));
                await ProtobufBinding.WriteAsync(http, response);
            })
            .AsPlayerEndpoint();

        routes.MapGet(ReceiptRoute, async (HttpContext http, string receiptId) =>
            {
                var services = http.RequestServices;
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var document = await services.GetRequiredService<ITransactionalDocumentStore>()
                    .GetAsync(ReceiptDocument.KeyFor(receiptId), http.RequestAborted);
                var receipt = document is null ? null : ReceiptDocument.FromJson(document.Content);

                // Other actors' receipts are reported as missing, not forbidden (no existence leak).
                if (receipt is null || !string.Equals(receipt.ActorId, actor.ActorId, StringComparison.Ordinal))
                {
                    throw new GameServerException(CoreErrorCodes.NotFound, $"Receipt {receiptId} not found for actor.");
                }

                var view = new ReceiptView
                {
                    ReceiptId = receipt.ReceiptId,
                    Operation = receipt.Operation,
                    RequestId = receipt.RequestId,
                    ResultCode = receipt.ResultCode,
                    Result = ByteString.FromBase64(receipt.ResultCompact),
                    ConfigVersion = receipt.ConfigVersion,
                    CommittedAtUtcMs = receipt.CommittedAt.ToUnixTimeMilliseconds(),
                    ServerTimeUtcMs = services.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds(),
                };
                view.Aggregates.AddRange(receipt.Aggregates.Select(a => new AggregateRev { AggregateId = a.AggregateId, NewRev = a.NewRev }));
                view.Outbox.AddRange(receipt.Outbox.Select(e => new OutboxEntryView
                {
                    EntryId = e.EntryId,
                    Kind = e.Kind,
                    PayloadJson = e.Payload?.ToJsonString(GameServerJson.Options) ?? "null",
                }));
                await ProtobufBinding.WriteAsync(http, view);
            })
            .AsPlayerEndpoint();
    }
}
