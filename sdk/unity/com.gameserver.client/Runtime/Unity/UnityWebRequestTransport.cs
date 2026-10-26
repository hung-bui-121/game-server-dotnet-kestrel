#if UNITY_5_3_OR_NEWER
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace GameServer.Client
{
    /// <summary>
    /// Unity transport (SDK-02). Must be used from the main thread (UnityWebRequest requirement); the SDK's awaits keep the
    /// Unity synchronization context, so continuations come back to the main thread.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        /// <summary>Per-attempt timeout; the SDK retries timeouts with the same bytes.</summary>
        public int TimeoutSeconds { get; set; } = 15;

        public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<TransportResponse>();
            var web = new UnityWebRequest(request.Url, request.Method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            if (request.Body != null)
            {
                web.uploadHandler = new UploadHandlerRaw(request.Body) { contentType = GameServerClient.ProtobufContentType };
            }

            foreach (var header in request.Headers)
            {
                web.SetRequestHeader(header.Key, header.Value);
            }

            var registration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                web.Abort();
            });

            var operation = web.SendWebRequest();
            operation.completed += _ =>
            {
                try
                {
                    if (web.result == UnityWebRequest.Result.ConnectionError || web.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        completion.TrySetException(new TransportException(web.error ?? "UnityWebRequest failed."));
                    }
                    else
                    {
                        completion.TrySetResult(new TransportResponse(
                            (int)web.responseCode,
                            web.GetResponseHeader("Content-Type"),
                            web.downloadHandler?.data ?? Array.Empty<byte>()));
                    }
                }
                finally
                {
                    registration.Dispose();
                    web.Dispose();
                }
            };

            return completion.Task;
        }
    }
}
#endif
