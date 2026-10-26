#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer.Client
{
    /// <summary>One HTTP request of the SDK. <see cref="Body"/> is sent as is on every retry (SDK-02).</summary>
    public sealed class TransportRequest
    {
        public TransportRequest(string method, Uri url, IReadOnlyDictionary<string, string> headers, byte[]? body)
        {
            Method = method;
            Url = url;
            Headers = headers;
            Body = body;
        }

        public string Method { get; }

        public Uri Url { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        public byte[]? Body { get; }
    }

    public sealed class TransportResponse
    {
        public TransportResponse(int status, string? contentType, byte[] body)
        {
            Status = status;
            ContentType = contentType;
            Body = body;
        }

        public int Status { get; }

        public string? ContentType { get; }

        public byte[] Body { get; }
    }

    /// <summary>The request did not produce an HTTP response (DNS, connection, timeout): safe to retry with the same bytes.</summary>
    public sealed class TransportException : Exception
    {
        public TransportException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>Sends one request. Unity uses <c>UnityWebRequestTransport</c>; tools and tests use <see cref="HttpClientTransport"/>.</summary>
    public interface IHttpTransport
    {
        /// <exception cref="TransportException">No HTTP response was received.</exception>
        Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Transport over <see cref="HttpClient"/> (bots, load tests, server-side tools, tests).</summary>
    public sealed class HttpClientTransport : IHttpTransport
    {
        private readonly HttpClient _http;

        public HttpClientTransport(HttpClient http) => _http = http;

        public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (request.Body != null)
            {
                message.Content = new ByteArrayContent(request.Body);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue(GameServerClient.ProtobufContentType);
            }

            foreach (var header in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            try
            {
                using var response = await _http.SendAsync(message, cancellationToken);
                var body = await response.Content.ReadAsByteArrayAsync();
                return new TransportResponse((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, body);
            }
            catch (HttpRequestException ex)
            {
                throw new TransportException("HTTP request failed.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TransportException("HTTP request timed out.", ex);
            }
        }
    }
}
