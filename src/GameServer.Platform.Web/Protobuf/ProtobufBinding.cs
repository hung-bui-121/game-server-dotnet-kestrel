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
    public static async Task<byte[]> ReadBodyAsync(HttpContext context, int maxBytes)
    {
        var contentType = context.Request.ContentType;
        if (contentType is null || !contentType.StartsWith(ErrorResponseWriter.ProtobufContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new GameServerException(CoreErrorCodes.UnsupportedMediaType, $"Content-Type '{contentType}' is not {ErrorResponseWriter.ProtobufContentType}.");
        }

        if (context.Request.ContentLength > maxBytes)
        {
            throw new GameServerException(CoreErrorCodes.PayloadTooLarge, $"Content-Length {context.Request.ContentLength} > {maxBytes}.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new GameServerException(CoreErrorCodes.PayloadTooLarge, $"Body exceeds {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Parses with recursion and size limits.</summary>
    public static T Parse<T>(byte[] body)
        where T : IMessage<T>, new()
    {
        using var input = CodedInputStream.CreateWithLimits(new MemoryStream(body), body.Length, RecursionLimit);
        var message = new T();
        message.MergeFrom(input);
        return message;
    }

    public static async Task WriteAsync(HttpContext context, IMessage message, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ErrorResponseWriter.ProtobufContentType;
        await context.Response.Body.WriteAsync(message.ToByteArray(), context.RequestAborted);
    }
}
