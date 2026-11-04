extern alias sdk;

using Google.Protobuf;
using sdk::GameServer.Client;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>A host-free transport: answers each request with the next scripted step and records every request (slice 016 US5).</summary>
internal sealed class FakeTransport : IHttpTransport
{
    private readonly Queue<Func<TransportRequest, TransportResponse>> _steps = new();

    public List<TransportRequest> Requests { get; } = [];

    /// <summary>Called at the start of every send, before the step runs (e.g. to check the store at that moment).</summary>
    public Action<TransportRequest>? OnSend { get; set; }

    /// <summary>Answer used when no step is queued.</summary>
    public Func<TransportRequest, TransportResponse> Otherwise { get; set; } = _ => Command("OK");

    public FakeTransport Then(Func<TransportRequest, TransportResponse> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public FakeTransport ThenAnswer(TransportResponse response) => Then(_ => response);

    public FakeTransport ThenDrop() => Then(_ => throw new TransportException("dropped"));

    public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        OnSend?.Invoke(request);
        var step = _steps.Count > 0 ? _steps.Dequeue() : Otherwise;
        return Task.FromResult(step(request));
    }

    public static TransportResponse Command(string code, int codeNumber = 0, long serverTimeUtcMs = 0) =>
        new(200, GameServerClient.ProtobufContentType, new SdkCore.CommandResponse
        {
            Code = code,
            CodeNumber = codeNumber,
            ReceiptId = code == "OK" ? "rcpt" : string.Empty,
            ServerTimeUtcMs = serverTimeUtcMs,
        }.ToByteArray());

    public static TransportResponse Envelope(int status, string code, bool retryable, int codeNumber = 12, int retryAfterMs = 0) =>
        new(status, GameServerClient.ProtobufContentType, new SdkCore.ErrorEnvelope
        {
            Code = code,
            CodeNumber = codeNumber,
            Message = code,
            Retryable = retryable,
            RetryAfterMs = retryAfterMs,
        }.ToByteArray());

    public static TransportResponse Time(long serverTimeUtcMs) =>
        new(200, GameServerClient.ProtobufContentType, new SdkCore.TimeResponse { ServerTimeUtcMs = serverTimeUtcMs }.ToByteArray());
}

/// <summary>Builds clients over a <see cref="FakeTransport"/> with a logged-in session.</summary>
internal static class PendingClients
{
    public const string Route = "api/v1/test/command";
    public const string PlayerId = "p_pending";

    public static (GameServerClient Client, FakeTransport Transport, InMemorySessionStore Session, ManualClientClock Clock) Create(
        IPendingCommandStore? store,
        int maxRetries = 2,
        string? playerId = PlayerId)
    {
        var transport = new FakeTransport();
        var session = new InMemorySessionStore { Token = "session-token-must-not-be-stored", PlayerId = playerId };
        var clock = new ManualClientClock();
        var options = new GameServerClientOptions(new Uri("http://localhost/"), "1.2.3", "editor", "build-42")
        {
            MaxRetries = maxRetries,
            PendingCommands = store,
        };
        var client = new GameServerClient(options, transport, clock: clock, delay: new RecordingDelay(), session: session, random: new Random(7));
        return (client, transport, session, clock);
    }

    public static SdkCore.SnapshotRequest Request(string section = "wallet") => new() { Sections = { section } };

    public static string RequestIdOf(TransportRequest request) => request.Headers[GameServerClient.RequestIdHeader];
}

/// <summary>The real file system with one step failing (contracts/sdk-pending-store.md §7).</summary>
internal sealed class FaultyPendingOps : CacheFileOps
{
    private int _failures;

    public PendingStep FailAt { get; set; } = PendingStep.None;

    /// <summary>How many times the step fails (then it works).</summary>
    public int Times { get; set; } = int.MaxValue;

    public override void CreateDirectory(string path)
    {
        Fail(PendingStep.CreateDirectory);
        base.CreateDirectory(path);
    }

    public override Stream OpenTemp(string path)
    {
        Fail(PendingStep.OpenTemp);
        return base.OpenTemp(path);
    }

    public override void Write(Stream stream, byte[] buffer)
    {
        Fail(PendingStep.Write);
        base.Write(stream, buffer);
    }

    public override void Flush(Stream stream)
    {
        Fail(PendingStep.Flush);
        base.Flush(stream);
    }

    public override void Move(string source, string destination)
    {
        Fail(PendingStep.Move);
        base.Move(source, destination);
    }

    public override void Replace(string source, string destination)
    {
        Fail(PendingStep.Replace);
        base.Replace(source, destination);
    }

    public override void Delete(string path)
    {
        // The temp cleanup of a failed Record also deletes: only an entry delete counts as the Delete step.
        if (path.EndsWith(PersistentPendingCommandStore.EntrySuffix, StringComparison.Ordinal))
        {
            Fail(PendingStep.Delete);
        }

        base.Delete(path);
    }

    public override IEnumerable<string> EnumerateFiles(string folder, string pattern)
    {
        Fail(PendingStep.Enumerate);
        return base.EnumerateFiles(folder, pattern);
    }

    private void Fail(PendingStep step)
    {
        if (step == FailAt && _failures < Times)
        {
            _failures++;
            throw new IOException($"injected fault at {step}");
        }
    }
}

public enum PendingStep
{
    None,
    CreateDirectory,
    OpenTemp,
    Write,
    Flush,
    Move,
    Replace,
    Delete,
    Enumerate,
}
