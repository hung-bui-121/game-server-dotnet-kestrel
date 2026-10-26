using GameServer.Errors;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Protobuf;

/// <summary>Reads a raw request body with a hard size limit (API-06).</summary>
public static class BoundedBody
{
    /// <summary>
    /// Rejects a content type other than <paramref name="expectedContentType"/> with 415 and bodies above
    /// <paramref name="maxBytes"/> with 413.
    /// </summary>
    public static async Task<byte[]> ReadAsync(HttpContext context, int maxBytes, string expectedContentType)
    {
        var contentType = context.Request.ContentType;
        if (contentType is null || !contentType.StartsWith(expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new GameServerException(CoreErrorCodes.UnsupportedMediaType, $"Content-Type '{contentType}' is not {expectedContentType}.");
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
}
