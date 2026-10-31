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
        routes.MapCommand<TWireRequest, TRequest, TResult>(pattern, operation, (wire, _) => Task.FromResult(toDomain(wire)), toWire).AsPlayerEndpoint();

    /// <summary>
    /// Player command whose domain request needs async work before the transaction (e.g. IAP receipt verification,
    /// IAP-01: outbound calls happen before the runner, never inside the transaction). A retry of a committed request is
    /// replayed before <paramref name="toDomainAsync"/> runs, so that work is not repeated (single-use codes, IDN-05).
    /// </summary>
    public static IEndpointConventionBuilder MapPlayerCommand<TWireRequest, TRequest, TResult>(
        this IEndpointRouteBuilder routes,
        string pattern,
        string operation,
        Func<TWireRequest, HttpContext, Task<TRequest>> toDomainAsync,
        Func<TResult, IMessage> toWire)
        where TWireRequest : IMessage<TWireRequest>, new()
        where TRequest : notnull =>
        routes.MapCommand(pattern, operation, toDomainAsync, toWire, replayFirst: true).AsPlayerEndpoint();

    /// <summary>
    /// Player command with a hook run after the command committed or was replayed (never after a rejection), before the
    /// response is written: e.g. refreshing a per-instance cache the command made stale (PRIV-01 revocation, FR-007). A
    /// failing hook is logged and never turns the committed command into an error response.
    /// </summary>
    public static IEndpointConventionBuilder MapPlayerCommand<TWireRequest, TRequest, TResult>(
        this IEndpointRouteBuilder routes,
        string pattern,
        string operation,
        Func<TWireRequest, TRequest> toDomain,
        Func<TResult, IMessage> toWire,
        Func<CommandExecution, HttpContext, Task> afterExecute)
        where TWireRequest : IMessage<TWireRequest>, new()
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(afterExecute);
        return routes.MapCommand<TWireRequest, TRequest, TResult>(
                pattern,
                operation,
                (wire, _) => Task.FromResult(toDomain(wire)),
                toWire,
                afterExecute: afterExecute)
            .AsPlayerEndpoint();
    }

    /// <summary>Development-only command (OPS-09): profile Dev, Protobuf, forbidden in Production by the validator.</summary>
    public static IEndpointConventionBuilder MapDevCommand<TWireRequest, TRequest, TResult>(
        this IEndpointRouteBuilder routes,
        string pattern,
        string operation,
        Func<TWireRequest, TRequest> toDomain,
        Func<TResult, IMessage> toWire)
        where TWireRequest : IMessage<TWireRequest>, new()
        where TRequest : notnull =>
        routes.MapCommand<TWireRequest, TRequest, TResult>(pattern, operation, (wire, _) => Task.FromResult(toDomain(wire)), toWire)
            .AsDevEndpoint()
            .WithMetadata(ProtobufEndpointMetadata.Instance);

    private static IEndpointConventionBuilder MapCommand<TWireRequest, TRequest, TResult>(
        this IEndpointRouteBuilder routes,
        string pattern,
        string operation,
        Func<TWireRequest, HttpContext, Task<TRequest>> toDomainAsync,
        Func<TResult, IMessage> toWire,
        bool replayFirst = false,
        Func<CommandExecution, HttpContext, Task>? afterExecute = null)
        where TWireRequest : IMessage<TWireRequest>, new()
        where TRequest : notnull =>
        routes.MapPost(pattern, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var body = await ProtobufBinding.ReadBodyAsync(http, options.Limits.PlayerBodyBytes);
                var requestId = CorrelationMiddleware.GetClientRequestId(http)
                    ?? throw new GameServerException(CoreErrorCodes.BadRequest, $"Header {CorrelationMiddleware.HeaderName} must be a UUID for commands.");
                var wire = ProtobufBinding.Parse<TWireRequest>(body);
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http);
                var runner = services.GetRequiredService<ICommandRunner>();
                var bodyHash = ReceiptIds.BodyHash(body);

                var execution = replayFirst ? await runner.TryReplayAsync(operation, actor, requestId, bodyHash, http.RequestAborted) : null;
                if (execution is null)
                {
                    var invocation = new CommandInvocation(
                        operation,
                        actor,
                        requestId,
                        bodyHash,
                        await toDomainAsync(wire, http),
                        result => toWire((TResult)result).ToByteArray());
                    execution = await runner.ExecuteAsync(invocation, http.RequestAborted);
                }

                if (afterExecute is not null && execution.ReceiptId is not null)
                {
                    await AfterExecuteHook.RunAsync(afterExecute, execution, http, operation);
                }

                var response = BuildResponse(execution, services.GetRequiredService<AggregateWireMapperRegistry>(), services.GetRequiredService<IClock>());
                await ProtobufBinding.WriteAsync(http, response);
            })
            .WithMetadata(new CommandOperationMetadata(operation));

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
