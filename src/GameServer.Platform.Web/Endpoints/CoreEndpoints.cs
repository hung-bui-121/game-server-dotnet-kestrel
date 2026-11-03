using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Config;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Requests;
using GameServer.Platform.Web.Routing;
using GameServer.Protocol.Core.V1;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Endpoints;

/// <summary>
/// Framework endpoints: server time (TIME-01), snapshot (PLY-02, API-10), config bundle (CFG-02, CFG-03; the only compressed
/// response, FG-019), receipt lookup (CMD-09).
/// </summary>
internal static class CoreEndpoints
{
    public const string SnapshotRoute = PlatformRoutes.SnapshotTemplate;

    public static void MapPublic(IEndpointRouteBuilder routes) =>
        routes.MapDeclared(PlatformRoutes.Time, async (HttpContext http, IClock clock) =>
                await ProtobufBinding.WriteAsync(http, new TimeResponse { ServerTimeUtcMs = clock.UtcNow.ToUnixTimeMilliseconds() }))
            .AsPublicEndpoint(protobuf: true);

    public static void MapPlayer(IEndpointRouteBuilder routes)
    {
        routes.MapDeclared(PlatformRoutes.Snapshot, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var request = ProtobufBinding.Parse<SnapshotRequest>(await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes));
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var aggregates = await services.GetRequiredService<PlayerSnapshotReader>().ReadAsync(actor.PlayerId!, request.Sections, http.RequestAborted);
                var mappers = services.GetRequiredService<AggregateWireMapperRegistry>();
                var config = services.GetRequiredService<IGameConfig>();

                var response = new SnapshotResponse
                {
                    ConfigVersion = config.ConfigVersion,
                    ClientConfigVersion = config.ClientConfigVersion,
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

        routes.MapDeclared(PlatformRoutes.Config, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var request = ProtobufBinding.Parse<GetConfigRequest>(await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes));
                var bundle = services.GetRequiredService<ConfigPhaseSummary>().Bundle;
                var now = services.GetRequiredService<IClock>().UtcNow.ToUnixTimeMilliseconds();

                // Either version names exactly the current client-visible files; SDKs before 0.7.0 hold the server version (R1).
                var notModified = bundle.IsCurrent(request.ConfigVersion);
                http.RequestOutcome().NotModified = notModified;
                if (notModified)
                {
                    await ConfigResponseCompression.WriteNotModifiedAsync(http, options.ConfigCompression, bundle.NotModified(now));
                    return;
                }

                // The bundle is serialized once (R11): only the server time and the client version are written per request.
                var tail = bundle.Tail(now);
                if (bundle.Prefix.Length + tail.Length > options.Limits.ConfigBundleBytes)
                {
                    // The startup size check (FR-053) keeps a started host below the limit; the request-time check stays (CFG-03).
                    throw new GameServerException(CoreErrorCodes.PayloadTooLarge, $"Config bundle exceeds {options.Limits.ConfigBundleBytes} bytes.");
                }

                await ConfigResponseCompression.WriteFullAsync(http, options.ConfigCompression, bundle, tail);
            })
            .AsPlayerEndpoint();

        routes.MapDeclared(PlatformRoutes.Receipt, async (HttpContext http, string receiptId) =>
            {
                var services = http.RequestServices;
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var receipt = await services.GetRequiredService<ReceiptReader>().GetAsync(receiptId, http.RequestAborted);

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
