using GameServer.Errors;
using GameServer.Platform.Web.Errors;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Protobuf;

/// <summary>Bounded Protobuf request/response binding (API-01, API-06).</summary>
public static class ProtobufBinding
{
    private const int RecursionLimit = 32;

    /// <summary>
    /// Reads the raw body with a hard size limit. Rejects non-Protobuf content types with 415
    /// and bodies above <paramref name="maxBytes"/> with 413.
    /// </summary>
    public static Task<byte[]> ReadBodyAsync(HttpContext context, int maxBytes) =>
        BoundedBody.ReadAsync(context, maxBytes, ErrorResponseWriter.ProtobufContentType);

    /// <summary>Parses with recursion and size limits.</summary>
    public static T Parse<T>(byte[] body)
        where T : IMessage<T>, new()
    {
        var message = new T();
        if (body.Length == 0)
        {
            // An empty body is a valid proto3 message with all default values.
            return message;
        }

        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(body), body.Length, RecursionLimit);
        message.MergeFrom(input);
        return message;
    }

    public static async Task WriteAsync(HttpContext context, IMessage message, int statusCode = StatusCodes.Status200OK)
    {
        // A request whose timeout fired (or whose client left) gets the timeout response only: a body written here with the
        // cancelled token may still be buffered, and the timeout writer would append its envelope to it (two messages, one body).
        context.RequestAborted.ThrowIfCancellationRequested();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ErrorResponseWriter.ProtobufContentType;
        await context.Response.Body.WriteAsync(message.ToByteArray(), context.RequestAborted);
    }
}
