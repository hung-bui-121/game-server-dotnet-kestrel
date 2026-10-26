extern alias sdk;

using System.Security.Cryptography;
using GameServer.Platform.Web.Middleware;
using GameServer.Tests.Shared;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using sdk::GameServer.Client;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>Transport that records every exchange and lets a test replace or drop responses.</summary>
public sealed class ScriptedTransport(IHttpTransport inner) : IHttpTransport
{
    public List<(TransportRequest Request, TransportResponse? Response)> Exchanges { get; } = [];

    /// <summary>Called per request with its index; return a response to short-circuit the server, null to forward.</summary>
    public Func<TransportRequest, int, TransportResponse?>? Before { get; set; }

    /// <summary>Called with the server's response; may rewrite it or throw <see cref="TransportException"/> to drop it.</summary>
    public Func<TransportRequest, int, TransportResponse, TransportResponse>? After { get; set; }

    public IEnumerable<TransportRequest> RequestsTo(string route) =>
        Exchanges.Select(e => e.Request).Where(r => r.Url.AbsolutePath.TrimStart('/') == route);

    public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        var index = Exchanges.Count;
        if (Before?.Invoke(request, index) is { } shortCircuit)
        {
            Exchanges.Add((request, shortCircuit));
            return shortCircuit;
        }

        var response = await inner.SendAsync(request, cancellationToken);
        try
        {
            response = After?.Invoke(request, index, response) ?? response;
            Exchanges.Add((request, response));
            return response;
        }
        catch (TransportException)
        {
            Exchanges.Add((request, null));
            throw;
        }
    }

    public static TransportResponse Error(int status, string code, bool retryable, int retryAfterMs = 0) =>
        new(status, GameServerClient.ProtobufContentType, new SdkCore.ErrorEnvelope
        {
            Code = code,
            CodeNumber = 99,
            Message = code,
            Retryable = retryable,
            RetryAfterMs = retryAfterMs,
        }.ToByteArray());
}

/// <summary>Records waits instead of sleeping.</summary>
public sealed class RecordingDelay : IDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>Gate decision switchable from a test (GATE-01/02 slot).</summary>
public sealed class SwitchableGate : IGateEvaluator
{
    public GateDecision Decision { get; set; } = GateDecision.Allow;

    public ValueTask<GateDecision> EvaluateAsync(HttpContext context) => ValueTask.FromResult(Decision);
}

/// <summary>Full SampleGame host + SDK clients talking to it through TestServer.</summary>
public sealed class SdkHarness : IAsyncDisposable
{
    private SdkHarness(TestHost host, SwitchableGate gate)
    {
        Host = host;
        Gate = gate;
    }

    public TestHost Host { get; }

    public SwitchableGate Gate { get; }

    public static async Task<SdkHarness> StartAsync()
    {
        var gate = new SwitchableGate();
        var host = await TestHost.StartAsync(GameHttp.FullGame, services: s => s.AddSingleton<IGateEvaluator>(gate));
        return new SdkHarness(host, gate);
    }

    public (GameServerClient Client, ScriptedTransport Transport, RecordingDelay Delay) NewClient(ISessionStore? session = null)
    {
        var http = Host.Client();
        var transport = new ScriptedTransport(new HttpClientTransport(http));
        var delay = new RecordingDelay();
        var client = new GameServerClient(
            new GameServerClientOptions(http.BaseAddress!, "1.2.3", "editor", "build-42"),
            transport,
            delay: delay,
            session: session,
            random: new Random(7));
        return (client, transport, delay);
    }

    public static (string LocalId, byte[] Secret) NewDevice() => ("dev-" + Guid.NewGuid().ToString("N")[..16], RandomNumberGenerator.GetBytes(32));

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
