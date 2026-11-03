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

    public static Task WriteAsync(HttpContext context, IMessage message, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(message);
        return WriteBytesAsync(context, statusCode, message.ToByteArray(), ReadOnlyMemory<byte>.Empty, contentEncoding: null, contentLength: null);
    }

    /// <summary>
    /// Writes an already serialized (and possibly compressed) Protobuf body in up to two parts, so the <c>GetConfig</c> prefix and
    /// tail are sent without joining them (research R11).
    /// </summary>
    /// <param name="contentEncoding">The <c>Content-Encoding</c> of the body; null for identity.</param>
    /// <param name="contentLength">Bytes on the wire; null leaves the header unset.</param>
    internal static async Task WriteBytesAsync(
        HttpContext context,
        int statusCode,
        ReadOnlyMemory<byte> first,
        ReadOnlyMemory<byte> second,
        string? contentEncoding,
        long? contentLength)
    {
        // The token is cancelled when the request timeout fired or the client left. Write nothing then: on a timeout the timeout
        // policy writes the error envelope, and a body started here first would share the response with it (seen under
        // TestServer, T035). A cancellation during the writes below still stops them through the same token.
        context.RequestAborted.ThrowIfCancellationRequested();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ErrorResponseWriter.ProtobufContentType;
        if (contentEncoding is not null)
        {
            context.Response.Headers.ContentEncoding = contentEncoding;
        }

        context.Response.ContentLength = contentLength;
        await context.Response.Body.WriteAsync(first, context.RequestAborted);
        if (!second.IsEmpty)
        {
            await context.Response.Body.WriteAsync(second, context.RequestAborted);
        }
    }
}
