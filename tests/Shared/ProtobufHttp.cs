using System.Net.Http.Headers;
using GameServer.Protocol.Core.V1;
using Google.Protobuf;

namespace GameServer.Tests.Shared;

/// <summary>Protobuf helpers for HTTP tests.</summary>
public static class ProtobufHttp
{
    public const string ContentType = "application/x-protobuf";

    public static Task<HttpResponseMessage> PostProtobufAsync(this HttpClient client, string path, IMessage message, string? requestId = null) =>
        client.PostBytesAsync(path, message.ToByteArray(), requestId);

    public static async Task<HttpResponseMessage> PostBytesAsync(this HttpClient client, string path, byte[] body, string? requestId = null, string contentType = ContentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (requestId is not null)
        {
            request.Headers.Add("X-Request-Id", requestId);
        }

        return await client.SendAsync(request);
    }

    public static async Task<T> ReadProtobufAsync<T>(this HttpResponseMessage response)
        where T : IMessage<T>, new()
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var message = new T();
        message.MergeFrom(bytes);
        return message;
    }

    public static Task<ErrorEnvelope> ReadErrorAsync(this HttpResponseMessage response) => response.ReadProtobufAsync<ErrorEnvelope>();

    public static string NewRequestId() => Guid.NewGuid().ToString("D");
}
