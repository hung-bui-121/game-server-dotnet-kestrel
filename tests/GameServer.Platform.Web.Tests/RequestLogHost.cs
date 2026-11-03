using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using GameServer.Actors;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Observability;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Requests;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Host of the request-log tests (slice 013 T040–T042): Identity + Counter (or the given modules) + GM + the health probes, a switchable maintenance
/// gate, probe endpoints for an injected 500 and a client abort, and the <c>command_results_total</c> measurements keyed by the
/// trace id of the request that recorded them (the listener runs synchronously inside the request).
/// </summary>
public sealed class RequestLogHost : IAsyncDisposable
{
    public const string FailRoute = "/api/v1/probe/fail";
    public const string WaitRoute = "/api/v1/probe/wait";

    /// <summary>Host declarations of the two probes (BC-1): a failing endpoint and one that waits until the client cancels.</summary>
    public static RouteDeclaration Fail { get; } = RouteDeclaration.Post("probe.fail", FailRoute, SecurityProfile.Player);

    public static RouteDeclaration Wait { get; } = RouteDeclaration.Post("probe.wait", WaitRoute, SecurityProfile.Player);
    public const string StaffPassword = "staff-PASSWORD-5c2e";
    public const string AdminId = "admin";

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly MeterListener _listener;

    private RequestLogHost(
        TestHost host,
        SwitchGate gate,
        TaskCompletionSource waitStarted,
        MeterListener listener,
        ConcurrentQueue<(string? TraceId, string Code)> measurements)
    {
        Host = host;
        Gate = gate;
        WaitStarted = waitStarted;
        _listener = listener;
        CommandResults = measurements;
    }

    public TestHost Host { get; }

    public SwitchGate Gate { get; }

    /// <summary>Signalled when the wait probe is running (the client may cancel from then on).</summary>
    public TaskCompletionSource WaitStarted { get; }

    /// <summary><c>command_results_total</c> measurements of this process: trace id of the recording request and the code tag.</summary>
    public ConcurrentQueue<(string? TraceId, string Code)> CommandResults { get; }

    public IEnumerable<CapturedLog> Events => Host.LogEntries.Where(e => e.Category == RequestLog.Category);

    public static async Task<RequestLogHost> StartAsync(
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null,
        Action<WebApplication>? afterUseGameServer = null,
        Action<GameServerBuilder>? modules = null,
        IReadOnlyList<RouteDeclaration>? routes = null)
    {
        var measurements = new ConcurrentQueue<(string? TraceId, string Code)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName && instrument.Name == "command_results_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "code")
                {
                    measurements.Enqueue((Activity.Current?.TraceId.ToHexString(), (string)tag.Value!));
                }
            }
        });
        listener.Start();

        var gate = new SwitchGate();
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var host = await SampleTestHost.StartAsync(
                b =>
                {
                    if (modules is null)
                    {
                        b.AddModule<IdentityModule>().AddModule<CounterModule>();
                    }
                    else
                    {
                        modules(b);
                    }

                    b.AddModule<GmModule>().AddModule<ObservabilityModule>();
                    b.DeclareRoutes([Fail, Wait, .. routes ?? []]);
                },
                values =>
                {
                    values["Gm:Bootstrap:Staff:0:StaffId"] = AdminId;
                    values["Gm:Bootstrap:Staff:0:Password"] = StaffPassword;
                    values["Gm:Bootstrap:Staff:0:Roles:0"] = "Administrator";
                    settings?.Invoke(values);
                },
                beforeUseGameServer: app =>
                {
                    app.MapDeclared(Fail, (Func<HttpContext, Task<IResult>>)(_ => throw new InvalidOperationException("injected failure")))
                        .AsPlayerEndpoint();
                    app.MapDeclared(Wait, (Func<HttpContext, Task<IResult>>)(async http =>
                        {
                            waitStarted.TrySetResult();
                            await Task.Delay(Timeout.Infinite, http.RequestAborted);
                            return Results.Ok();
                        }))
                        .AsPlayerEndpoint();
                    beforeUseGameServer?.Invoke(app);
                },
                afterUseGameServer: afterUseGameServer,
                services: s => s.AddSingleton<IGateEvaluator>(gate));
            return new RequestLogHost(host, gate, waitStarted, listener, measurements);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>The request event of a request id (the client UUID or the server id echoed in <c>X-Request-Id</c>).</summary>
    public async Task<CapturedLog> EventAsync(string requestId)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var events = Events.Where(e => Equals(e.Properties["RequestId"], requestId)).ToList();
            if (events.Count > 0)
            {
                // The event is written after the response returned to the client: a second one would follow at once.
                await Task.Delay(20);
                return Events.Where(e => Equals(e.Properties["RequestId"], requestId)).ShouldHaveSingleItem($"one request event for {requestId}");
            }

            if (watch.Elapsed > EventTimeout)
            {
                throw new TimeoutException($"No request event for request id {requestId}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>The event of a response: its <c>X-Request-Id</c> is the correlation id of the request.</summary>
    public Task<CapturedLog> EventAsync(HttpResponseMessage response) =>
        EventAsync(response.Headers.GetValues(CorrelationMiddleware.HeaderName).Single());

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await Host.DisposeAsync();
    }

    /// <summary>Maintenance on demand; open otherwise.</summary>
    public sealed class SwitchGate : IGateEvaluator
    {
        public bool Maintenance { get; set; }

        public ValueTask<GateResult> EvaluateAsync(HttpContext context) =>
            ValueTask.FromResult(Maintenance ? new GateResult(GateDecision.Maintenance) : GateResult.Allow);
    }
}
