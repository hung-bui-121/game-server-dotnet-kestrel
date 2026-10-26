using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Correlation id = <c>X-Request-Id</c> (research R7, OBS-04). A valid client UUID is kept and is the
/// command requestId; otherwise the server generates one for correlation only.
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    private const string ItemKey = "gs.correlation";
    private const string ClientItemKey = "gs.client-request-id";

    public async Task InvokeAsync(HttpContext context)
    {
        var header = context.Request.Headers[HeaderName].ToString();
        string correlationId;
        if (Guid.TryParse(header, out var parsed))
        {
            correlationId = parsed.ToString("D");
            context.Items[ClientItemKey] = correlationId;
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("D");
        }

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("gs.request_id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>Correlation id of the request (always set by this middleware).</summary>
    public static string GetCorrelationId(HttpContext context) =>
        context.Items[ItemKey] as string ?? context.TraceIdentifier;

    /// <summary>The client-supplied UUID, or null if missing/invalid.</summary>
    public static string? GetClientRequestId(HttpContext context) => context.Items[ClientItemKey] as string;
}
