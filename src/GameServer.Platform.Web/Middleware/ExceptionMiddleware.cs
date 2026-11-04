using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Web.Errors;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Outermost error middleware (inside the request event's middleware): maps exceptions to catalog codes (API-04, API-05) and turns unmatched
/// routes into catalog 404s. Unknown exceptions become 500 <c>INTERNAL</c> without details. A reported read failure (FG-029) gets its
/// envelope but no second log line.
/// </summary>
public sealed class ExceptionMiddleware(RequestDelegate next, ErrorResponseWriter writer, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (context.Response.StatusCode == StatusCodes.Status404NotFound && !context.Response.HasStarted && context.GetEndpoint() is null)
            {
                await writer.WriteAsync(context, CoreErrorCodes.NotFound);
            }
        }
        catch (GameServerException ex)
        {
            // A read failure was already logged once, with aggregate, player, reason and exception type, where it happened (FR-026).
            var reported = AggregateReadFailure.TryGet(ex, out _);
            if (!reported && ex.Code.HttpStatus >= 500)
            {
                logger.LogError(ex, "Request failed with {Code}: {Detail}", ex.Code.Name, ex.Detail);
            }
            else if (!reported)
            {
                logger.LogInformation("Request rejected with {Code}: {Detail}", ex.Code.Name, ex.Detail);
            }

            await writer.WriteAsync(context, ex.Code, ex.RetryAfterMs, ex.Details);
        }
        catch (BadHttpRequestException ex)
        {
            var code = ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? CoreErrorCodes.PayloadTooLarge : CoreErrorCodes.BadRequest;
            logger.LogInformation(ex, "Bad HTTP request: {Code}", code.Name);
            await writer.WriteAsync(context, code);
        }
        catch (InvalidProtocolBufferException ex)
        {
            logger.LogInformation(ex, "Invalid Protobuf body.");
            await writer.WriteAsync(context, CoreErrorCodes.BadRequest);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to write.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception.");
            await writer.WriteAsync(context, CoreErrorCodes.Internal);
        }
    }
}
