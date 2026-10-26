using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Protocol.Core.V1;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Commands;

/// <summary>Maps Player command endpoints onto <see cref="ICommandRunner"/> (API-07, API-08).</summary>
public static class CommandEndpointFactory
{
    /// <summary>
    /// Maps <c>POST {pattern}</c>: raw body (≤ 64 KiB) → bodyHash → wire decode → domain request (ARC-02)
    /// → runner → <see cref="CommandResponse"/>. Requires a client UUID in <c>X-Request-Id</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapPlayerCommand<TWireRequest, TRequest, TResult>(
        this IEndpointRouteBuilder routes,
        string pattern,
        string operation,
        Func<TWireRequest, TRequest> toDomain,
        Func<TResult, IMessage> toWire)
        where TWireRequest : IMessage<TWireRequest>, new()
        where TRequest : notnull =>
        routes.MapPost(pattern, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var body = await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes);
                var requestId = CorrelationMiddleware.GetClientRequestId(http)
                    ?? throw new GameServerException(CoreErrorCodes.BadRequest, $"Header {CorrelationMiddleware.HeaderName} must be a UUID for commands.");
                var request = toDomain(ProtobufBinding.Parse<TWireRequest>(body));
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);

                var invocation = new CommandInvocation(
                    operation,
                    actor,
                    requestId,
                    ReceiptIds.BodyHash(body),
                    request,
                    result => toWire((TResult)result).ToByteArray());
                var execution = await services.GetRequiredService<ICommandRunner>().ExecuteAsync(invocation, http.RequestAborted);

                var response = BuildResponse(execution, services.GetRequiredService<AggregateWireMapperRegistry>(), services.GetRequiredService<IClock>());
                await ProtobufBinding.WriteAsync(http, response);
            })
            .AsPlayerEndpoint();

    internal static CommandResponse BuildResponse(CommandExecution execution, AggregateWireMapperRegistry mappers, IClock clock)
    {
        var response = new CommandResponse
        {
            Code = execution.Code.Name,
            CodeNumber = execution.Code.Number,
            ReceiptId = execution.ReceiptId ?? string.Empty,
            Result = ByteString.CopyFrom(execution.Result),
            ConfigVersion = execution.ConfigVersion,
            ServerTimeUtcMs = clock.UtcNow.ToUnixTimeMilliseconds(),
            Replayed = execution.Replayed,
        };
        foreach (var change in execution.Updates)
        {
            response.Updates.Add(new SectionUpdate
            {
                Id = change.AggregateId,
                BaseRev = change.BaseRev,
                NewRev = change.NewRev,
                Payload = mappers.ToPayload(change.AggregateId, change.State),
                Full = true,
            });
        }

        return response;
    }
}
