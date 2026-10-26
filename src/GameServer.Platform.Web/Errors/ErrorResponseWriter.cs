using System.Globalization;
using System.Text.Json.Nodes;
using GameServer.Config;
using GameServer.Errors;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Protocol.Core.V1;
using GameServer.Time;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Errors;

/// <summary>
/// Writes errors in the format of the endpoint (API-04): Protobuf <c>ErrorEnvelope</c> for Player/Protobuf
/// endpoints, Problem Details JSON otherwise. Never includes exception details.
/// </summary>
public sealed class ErrorResponseWriter(IClock clock, IGameConfig config)
{
    public const string ProtobufContentType = "application/x-protobuf";

    public async Task WriteAsync(HttpContext context, ErrorCode code, int? retryAfterMs = null, IReadOnlyDictionary<string, string>? details = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var requestId = CorrelationMiddleware.GetCorrelationId(context);
        var retryAfter = code.Retryable ? retryAfterMs ?? 1000 : 0;

        context.Response.Clear();
        context.Response.StatusCode = code.HttpStatus;
        context.Response.Headers[CorrelationMiddleware.HeaderName] = requestId;
        if (code.Retryable)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter / 1000.0)).ToString(CultureInfo.InvariantCulture);
        }

        if (context.IsProtobuf())
        {
            var envelope = new ErrorEnvelope
            {
                Code = code.Name,
                CodeNumber = code.Number,
                Message = code.SafeMessage,
                RequestId = requestId,
                Retryable = code.Retryable,
                RetryAfterMs = retryAfter,
                ServerTimeUtcMs = clock.UtcNow.ToUnixTimeMilliseconds(),
                ConfigVersion = config.ConfigVersion,
            };
            foreach (var (key, value) in details ?? new Dictionary<string, string>())
            {
                envelope.Details[key] = value;
            }

            context.Response.ContentType = ProtobufContentType;
            await context.Response.Body.WriteAsync(envelope.ToByteArray());
            return;
        }

        var problem = new JsonObject
        {
            ["type"] = "https://errors.gameserver/" + code.Name.ToLowerInvariant(),
            ["title"] = code.SafeMessage,
            ["status"] = code.HttpStatus,
            ["code"] = code.Name,
            ["codeNumber"] = code.Number,
            ["requestId"] = requestId,
            ["retryable"] = code.Retryable,
        };
        if (code.Retryable)
        {
            problem["retryAfterMs"] = retryAfter;
        }

        if (details is not null)
        {
            problem["details"] = new JsonObject(details.Select(d => KeyValuePair.Create(d.Key, (JsonNode?)d.Value)));
        }

        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(problem.ToJsonString(GameServerJson.Options));
    }
}
