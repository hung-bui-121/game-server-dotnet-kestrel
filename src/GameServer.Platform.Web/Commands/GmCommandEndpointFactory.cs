using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Endpoints;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Protobuf;
using GameServer.Platform.Web.Routing;
using GameServer.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Platform.Web.Commands;

/// <summary>JSON body of a GM mutation. A non-empty reason is mandatory for the audit (SEC-14).</summary>
public interface IGmCommandBody
{
    string? Reason { get; }
}

/// <summary>JSON response of a GM command (same fields as the Player <c>CommandResponse</c>).</summary>
public sealed record GmCommandResponse(
    string Code,
    int CodeNumber,
    string? ReceiptId,
    bool Replayed,
    string ConfigVersion,
    long ServerTimeUtcMs,
    JsonNode? Result,
    IReadOnlyList<GmAggregateUpdate> Updates);

public sealed record GmAggregateUpdate(string Id, long BaseRev, long NewRev, JsonNode? State);

/// <summary>
/// Maps GM command endpoints onto <see cref="ICommandRunner"/>: same receipt/idempotency/audit pipeline as Player
/// commands, JSON wire format, target player from the route (GM-03, SEC-14).
/// </summary>
public static class GmCommandEndpointFactory
{
    public const string PlayerIdRouteValue = "playerId";
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Maps <c>POST {pattern}</c>; <paramref name="pattern"/> must contain <c>{playerId}</c>. The actor is the
    /// authenticated GM; the receipt is keyed by GM + target player + operation + <c>X-Request-Id</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapGmCommand<TBody, TRequest, TResult>(
        this IEndpointRouteBuilder routes,
        string pattern,
        string operation,
        string permission,
        Func<TBody, TRequest> toDomain)
        where TBody : IGmCommandBody
        where TRequest : notnull
    {
        if (!pattern.Contains("{" + PlayerIdRouteValue + "}", StringComparison.Ordinal))
        {
            throw new ArgumentException($"GM command route '{pattern}' must contain {{{PlayerIdRouteValue}}}.", nameof(pattern));
        }

        return routes.MapPost(pattern, async (HttpContext http) =>
            {
                var services = http.RequestServices;
                var options = services.GetRequiredService<GameServerOptions>();
                var playerId = GmHttp.RequirePlayerId(http.Request.RouteValues[PlayerIdRouteValue] as string);

                var requestId = CorrelationMiddleware.GetClientRequestId(http)
                    ?? throw new GameServerException(CoreErrorCodes.BadRequest, $"Header {CorrelationMiddleware.HeaderName} must be a UUID for commands.");
                var body = await BoundedBody.ReadAsync(http, options.Limits.GmBodyBytes, GmHttp.JsonContentType);
                var wire = Deserialize<TBody>(body);
                if (string.IsNullOrWhiteSpace(wire.Reason) || wire.Reason.Length > MaxReasonLength)
                {
                    throw new GameServerException(CoreErrorCodes.BadRequest, $"reason is required (1..{MaxReasonLength} characters).");
                }

                var request = toDomain(wire);
                var actor = services.GetRequiredService<ActorContextAccessor>().GetActor(http) with { PlayerId = playerId };
                var invocation = new CommandInvocation(
                    operation,
                    actor,
                    requestId,
                    ReceiptIds.BodyHash(body),
                    request,
                    result => JsonSerializer.SerializeToUtf8Bytes((TResult)result, GameServerJson.Options));
                var execution = await services.GetRequiredService<ICommandRunner>().ExecuteAsync(invocation, http.RequestAborted);

                return Results.Json(BuildResponse(execution, services.GetRequiredService<IClock>()), GameServerJson.Options);
            })
            .AsGmEndpoint(permission);
    }

    internal static GmCommandResponse BuildResponse(CommandExecution execution, IClock clock) =>
        new(
            execution.Code.Name,
            execution.Code.Number,
            execution.ReceiptId,
            execution.Replayed,
            execution.ConfigVersion,
            clock.UtcNow.ToUnixTimeMilliseconds(),
            execution.Result.Length == 0 ? null : JsonNode.Parse(execution.Result),
            execution.Updates
                .Select(u => new GmAggregateUpdate(u.AggregateId, u.BaseRev, u.NewRev, JsonSerializer.SerializeToNode(u.State, u.State.GetType(), GameServerJson.Options)))
                .ToArray());

    private static TBody Deserialize<TBody>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<TBody>(body, GameServerJson.Options)
                ?? throw new GameServerException(CoreErrorCodes.BadRequest, "Body is required.");
        }
        catch (JsonException ex)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "Body is not valid JSON for this command.", ex);
        }
    }
}
