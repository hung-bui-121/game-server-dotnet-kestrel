#if UNITY_5_3_OR_NEWER
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace GameServer.Client
{
    /// <summary>
    /// Unity transport (SDK-02). Must be called from the main thread (UnityWebRequest requirement); the SDK's awaits keep the
    /// Unity synchronization context, so continuations come back to the main thread. Cancellation is marshalled to it too.
    /// </summary>
    public sealed class UnityWebRequestTransport : IHttpTransport
    {
        /// <summary>Per-attempt timeout; the SDK retries timeouts of idempotent requests with the same bytes.</summary>
        public int TimeoutSeconds { get; set; } = 15;

        public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<TransportResponse>();
            var context = SynchronizationContext.Current;
            UnityWebRequest? web = null;
            var registration = default(CancellationTokenRegistration);
            var finished = 0;

            void Finish()
            {
                if (Interlocked.Exchange(ref finished, 1) == 0)
                {
                    registration.Dispose();
                    web?.Dispose();
                }
            }

            try
            {
                var w = new UnityWebRequest(request.Url, request.Method)
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = TimeoutSeconds,
                };
                web = w;
                if (request.Body != null)
                {
                    w.uploadHandler = new UploadHandlerRaw(request.Body) { contentType = GameServerClient.ProtobufContentType };
                }

                foreach (var header in request.Headers)
                {
                    w.SetRequestHeader(header.Key, header.Value);
                }

                var operation = w.SendWebRequest();
                operation.completed += _ =>
                {
                    try
                    {
                        if (w.result == UnityWebRequest.Result.ConnectionError || w.result == UnityWebRequest.Result.DataProcessingError)
                        {
                            completion.TrySetException(new TransportException(w.error ?? "UnityWebRequest failed."));
                        }
                        else
                        {
                            completion.TrySetResult(new TransportResponse(
                                (int)w.responseCode,
                                w.GetResponseHeader("Content-Type"),
                                w.downloadHandler?.data ?? Array.Empty<byte>()));
                        }
                    }
                    finally
                    {
                        Finish();
                    }
                };

                registration = cancellationToken.Register(() =>
                {
                    completion.TrySetCanceled(cancellationToken);

                    // Abort is a Unity API: run it on the main thread, and only while the request is alive.
                    void Abort(object? _)
                    {
                        if (Volatile.Read(ref finished) == 0)
                        {
                            w.Abort();
                        }
                    }

                    if (context != null)
                    {
                        context.Post(Abort, null);
                    }
                    else
                    {
                        Abort(null);
                    }
                });
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Finish();
                completion.TrySetException(new TransportException("Could not start the request: " + ex.Message, ex));
            }

            return completion.Task;
        }
    }
}
#endif
