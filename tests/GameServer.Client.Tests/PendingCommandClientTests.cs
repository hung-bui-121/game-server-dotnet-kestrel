extern alias sdk;

using System.Text;
using Google.Protobuf;
using sdk::GameServer.Client;
using Shouldly;
using Identity = sdk::GameServer.Modules.Identity.Protocol.V1;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T058/T059 (FR-042..FR-045, US5-2..US5-4, US5-6, contracts/sdk-pending-store.md §4): with a store, a command is recorded
/// before its first attempt, removed on a final answer and kept when the outcome is unknown; entries are listed with a status on the
/// server clock and resent with their stored bytes and id; nothing else touches the store.
/// </summary>
public sealed class PendingCommandClientTests : IDisposable
{
    private const string CallerId = "5d1f6b2a-3c4e-4f60-9a7b-8c9d0e1f2a3b";
    private const string Token = "session-token-must-not-be-stored";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-pending-client-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static Task<CommandResult<SdkCore.SnapshotResponse>> SendAsync(GameServerClient client, string? requestId = CallerId, CancellationToken cancellationToken = default) =>
        client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, requestId, cancellationToken);

    public static TheoryData<string> FinalAnswers() => new("ok", "business code", "non-retryable envelope");

    [Theory]
    [MemberData(nameof(FinalAnswers))]
    public async Task A_final_answer_removes_the_entry(string answer)
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        transport.ThenAnswer(answer switch
        {
            "ok" => FakeTransport.Command("OK"),
            "business code" => FakeTransport.Command("SHOP_SOLD_OUT", codeNumber: 30001),
            _ => FakeTransport.Envelope(400, "BAD_REQUEST", retryable: false, codeNumber: 2),
        });

        if (answer == "non-retryable envelope")
        {
            (await Should.ThrowAsync<GameServerException>(() => SendAsync(client))).Error.Code.ShouldBe("BAD_REQUEST");
        }
        else
        {
            (await SendAsync(client)).RequestId.ShouldBe(CallerId);
        }

        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payload_mismatch_removes_the_entry_and_throws()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        transport.ThenAnswer(FakeTransport.Envelope(409, "IDEMPOTENCY_PAYLOAD_MISMATCH", retryable: false, codeNumber: 8));

        var ex = await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        ex.Error.Code.ShouldBe("IDEMPOTENCY_PAYLOAD_MISMATCH");
        store.Entries.ShouldBeEmpty();
    }

    public static TheoryData<string, int, int> AuthenticationAnswers() => new()
    {
        { "UNAUTHENTICATED", 2, 401 },
        { "SESSION_REPLACED", 18, 401 },
        { "ACCOUNT_SUSPENDED", 16, 403 },
        { "ACCOUNT_DELETED", 17, 401 },
    };

    /// <summary>T082 m6: an authentication answer refused the request before the command ran, so the entry stays for a resend.</summary>
    [Theory]
    [MemberData(nameof(AuthenticationAnswers))]
    public async Task An_authentication_answer_keeps_the_entry(string code, int codeNumber, int status)
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        transport.ThenAnswer(FakeTransport.Envelope(status, code, retryable: false, codeNumber: codeNumber));

        (await Should.ThrowAsync<GameServerException>(() => SendAsync(client))).Error.Code.ShouldBe(code);

        store.Entries.ShouldHaveSingleItem().RequestId.ShouldBe(CallerId);
    }

    public static TheoryData<string> UnknownOutcomes() => new("network", "invalid response", "COMMIT_UNKNOWN_RETRY", "AGGREGATE_VERSION_AHEAD");

    [Theory]
    [MemberData(nameof(UnknownOutcomes))]
    public async Task An_unknown_outcome_keeps_the_entry(string outcome)
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store, maxRetries: 1);
        transport.Otherwise = outcome switch
        {
            "network" => _ => throw new TransportException("dropped"),
            "invalid response" => _ => new TransportResponse(404, "text/html", [1]),
            "COMMIT_UNKNOWN_RETRY" => _ => FakeTransport.Envelope(503, "COMMIT_UNKNOWN_RETRY", retryable: true, codeNumber: 13, retryAfterMs: 1000),
            _ => _ => FakeTransport.Envelope(503, "AGGREGATE_VERSION_AHEAD", retryable: true, codeNumber: 24, retryAfterMs: 1000),
        };

        var ex = await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        // SDK-made errors carry the call's id; an envelope carries what the server sent (none from this fake).
        ex.Error.RequestId.ShouldBe(outcome is "network" or "invalid response" ? CallerId : string.Empty);
        var entry = store.Entries.ShouldHaveSingleItem();
        entry.RequestId.ShouldBe(CallerId);
        entry.PlayerId.ShouldBe(PendingClients.PlayerId);
        entry.Route.ShouldBe(PendingClients.Route);
        entry.Body.ShouldBe(PendingClients.Request().ToByteArray());
    }

    [Fact]
    public async Task Cancellation_after_the_record_keeps_the_entry()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        using var cts = new CancellationTokenSource();
        transport.OnSend = _ => cts.Cancel();
        transport.Otherwise = _ => throw new TransportException("dropped");

        await Should.ThrowAsync<OperationCanceledException>(() => SendAsync(client, cancellationToken: cts.Token));

        store.Entries.ShouldHaveSingleItem().RequestId.ShouldBe(CallerId);
    }

    /// <summary>
    /// T082 (T077 item 4, consumer guide §13.5): a token cancelled before the call still records the entry; nothing reached the
    /// server, so its resend runs the command for the first time (not a replay).
    /// </summary>
    [Fact]
    public async Task A_token_cancelled_before_the_call_records_the_entry_and_its_resend_runs_first_time()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => SendAsync(client, cancellationToken: cts.Token));

        transport.Requests.ShouldBeEmpty();
        var entry = store.Entries.ShouldHaveSingleItem();
        var resent = await client.ResendPendingAsync(entry, SdkCore.SnapshotResponse.Parser);
        resent.Replayed.ShouldBeFalse();
        transport.Requests.ShouldHaveSingleItem();
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_record_happens_before_the_first_transport_call()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        var seen = new List<int>();
        transport.OnSend = _ => seen.Add(store.Entries.Count);

        await SendAsync(client);

        seen.ShouldBe([1]);
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_entry_records_the_server_clock()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, clock) = PendingClients.Create(store);
        var server = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        transport.ThenAnswer(FakeTransport.Time(server.ToUnixTimeMilliseconds())).ThenDrop().ThenDrop().ThenDrop();
        await client.SyncTimeAsync();
        clock.UtcNow = clock.UtcNow.AddDays(-300);

        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        store.Entries.ShouldHaveSingleItem().RecordedAtServerUtc.ShouldBe(server);
    }

    [Fact]
    public async Task Calls_other_than_commands_never_touch_the_store()
    {
        var store = new CountingStore();
        var (client, transport, session, _) = PendingClients.Create(store, playerId: null);
        session.Token = null;
        transport.Otherwise = request => request.Url.AbsolutePath.TrimStart('/') switch
        {
            GameServerClient.LoginRoute => Protobuf(new Identity.LoginResponse { SessionToken = Token, PlayerId = PendingClients.PlayerId }),
            GameServerClient.SnapshotRoute => Protobuf(new SdkCore.SnapshotResponse()),
            GameServerClient.ConfigRoute => Protobuf(new SdkCore.GetConfigResponse { NotModified = true }),
            GameServerClient.TimeRoute => FakeTransport.Time(1),
            _ => Protobuf(new Identity.LogoutResponse()),
        };

        await client.LoginGuestAsync("dev-1", [1, 2, 3]);
        await client.RefreshSectionsAsync();
        await client.RefreshConfigAsync();
        await client.SyncTimeAsync();
        await client.LogoutAsync();

        transport.Requests.Count.ShouldBe(5);
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_disk_failure_still_sends_the_command_and_exposes_the_write_problem()
    {
        var store = new PersistentPendingCommandStore(_folder, new FaultyPendingOps { FailAt = PendingStep.Flush });
        var (client, transport, _, _) = PendingClients.Create(store);
        transport.ThenDrop().ThenDrop().ThenDrop();

        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        transport.Requests.Count.ShouldBe(3);
        store.WriteProblem.ShouldBe("not saved: IOException: injected fault at Flush");
        client.PendingCommands.ShouldHaveSingleItem().RequestId.ShouldBe(CallerId);
    }

    [Fact]
    public async Task Without_a_store_the_requests_are_unchanged_and_nothing_is_written()
    {
        var (plain, plainTransport, _, _) = PendingClients.Create(store: null);
        var store = new InMemoryPendingCommandStore();
        var (stored, storedTransport, _, _) = PendingClients.Create(store);

        await SendAsync(plain);
        await SendAsync(stored);

        var a = plainTransport.Requests.ShouldHaveSingleItem();
        var b = storedTransport.Requests.ShouldHaveSingleItem();
        (a.Method, a.Url, Convert.ToHexString(a.Body!)).ShouldBe((b.Method, b.Url, Convert.ToHexString(b.Body!)));
        a.Headers.OrderBy(h => h.Key, StringComparer.Ordinal).ShouldBe(b.Headers.OrderBy(h => h.Key, StringComparer.Ordinal));
        plain.PendingCommands.ShouldBeEmpty();
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact]
    public async Task The_session_token_never_reaches_an_entry_file()
    {
        var store = new PersistentPendingCommandStore(_folder);
        var (client, transport, _, _) = PendingClients.Create(store);
        transport.Otherwise = _ => throw new TransportException("dropped");

        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        var file = Directory.GetFiles(_folder).ShouldHaveSingleItem();
        var bytes = File.ReadAllBytes(file);
        Encoding.UTF8.GetString(bytes).ShouldNotContain(Token);
        Encoding.UTF8.GetString(bytes).ShouldContain(CallerId);
    }

    [Fact]
    public async Task Entries_have_a_status_for_the_session_player_on_the_server_clock()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, session, clock) = PendingClients.Create(store);
        var server = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
        transport.ThenAnswer(FakeTransport.Time(server.ToUnixTimeMilliseconds()));
        await client.SyncTimeAsync();
        var mine = new PendingCommand(CallerId, PendingClients.PlayerId, PendingClients.Route, [1], server);
        var other = new PendingCommand(GameServerClient.NewRequestId(), "p_other", PendingClients.Route, [1], server);

        client.StatusOf(mine).ShouldBe(PendingCommandStatus.Resendable);
        client.StatusOf(other).ShouldBe(PendingCommandStatus.OtherPlayer);

        // A device clock change does not move the server clock; time passing does.
        clock.UtcNow = clock.UtcNow.AddDays(30);
        client.StatusOf(mine).ShouldBe(PendingCommandStatus.Resendable);
        clock.Monotonic += TimeSpan.FromHours(23) - TimeSpan.FromSeconds(1);
        client.StatusOf(mine).ShouldBe(PendingCommandStatus.Resendable);
        clock.Monotonic += TimeSpan.FromSeconds(1);
        client.StatusOf(mine).ShouldBe(PendingCommandStatus.Expired);

        session.Clear();
        client.StatusOf(mine).ShouldBe(PendingCommandStatus.OtherPlayer);
    }

    [Fact]
    public void The_maximum_age_is_validated()
    {
        static GameServerClientOptions Options(TimeSpan age) =>
            new(new Uri("http://localhost/"), "1", "editor", "b") { PendingCommandMaxAge = age };

        Should.Throw<ArgumentException>(() => Options(TimeSpan.Zero).Validate()).ParamName.ShouldBe("PendingCommandMaxAge");
        Should.Throw<ArgumentException>(() => Options(TimeSpan.FromHours(168) + TimeSpan.FromTicks(1)).Validate());
        Should.NotThrow(() => Options(TimeSpan.FromHours(168)).Validate());
        new GameServerClientOptions(new Uri("http://localhost/"), "1", "editor", "b").PendingCommandMaxAge.ShouldBe(TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Resend_sends_the_stored_bytes_with_the_stored_id_and_route_and_removes_on_answer()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);
        transport.ThenDrop().ThenDrop().ThenDrop();
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));
        var entry = client.PendingCommands.ShouldHaveSingleItem();

        var result = await client.ResendPendingAsync(entry, SdkCore.SnapshotResponse.Parser);

        result.RequestId.ShouldBe(CallerId);
        var bodies = transport.Requests.Select(r => Convert.ToHexString(r.Body!)).Distinct().ShouldHaveSingleItem();
        bodies.ShouldBe(Convert.ToHexString(PendingClients.Request().ToByteArray()));
        transport.Requests.Select(PendingClients.RequestIdOf).ShouldAllBe(id => id == CallerId);
        transport.Requests.Last().Url.AbsolutePath.ShouldBe("/" + PendingClients.Route);
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resend_keeps_the_entry_when_the_outcome_is_still_unknown()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store, maxRetries: 0);
        transport.Otherwise = _ => throw new TransportException("dropped");
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        await Should.ThrowAsync<GameServerException>(() => client.ResendPendingAsync(client.PendingCommands[0], SdkCore.SnapshotResponse.Parser));

        store.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Resend_is_refused_for_an_entry_that_is_not_resendable()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, clock) = PendingClients.Create(store);
        var other = new PendingCommand(GameServerClient.NewRequestId(), "p_other", PendingClients.Route, [1], clock.UtcNow);
        var expired = new PendingCommand(GameServerClient.NewRequestId(), PendingClients.PlayerId, PendingClients.Route, [1], clock.UtcNow.AddHours(-24));
        var unknown = new PendingCommand(GameServerClient.NewRequestId(), PendingClients.PlayerId, PendingClients.Route, [1], clock.UtcNow);
        store.Record(other);
        store.Record(expired);

        foreach (var entry in new[] { other, expired, unknown })
        {
            await Should.ThrowAsync<InvalidOperationException>(() => client.ResendPendingAsync(entry, SdkCore.SnapshotResponse.Parser));
        }

        transport.Requests.ShouldBeEmpty();
        store.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Discard_removes_an_entry_and_ignores_unknown_ids()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store, maxRetries: 0);
        transport.Otherwise = _ => throw new TransportException("dropped");
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));

        client.DiscardPendingCommand("unknown");
        client.PendingCommands.Count.ShouldBe(1);
        client.DiscardPendingCommand(CallerId);

        client.PendingCommands.ShouldBeEmpty();
        transport.Requests.Count.ShouldBe(1);
    }

    public static TheoryData<string> StoreKinds() => new("memory", "folder");

    /// <summary>T082 B1: the list is a snapshot, so a resend inside the foreach never throws "Collection was modified".</summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task A_foreach_over_the_entries_can_resend_each_of_them(string kind)
    {
        IPendingCommandStore store = kind == "memory" ? new InMemoryPendingCommandStore() : new PersistentPendingCommandStore(_folder);
        var (client, transport, _, _) = PendingClients.Create(store, maxRetries: 0);
        var secondId = GameServerClient.NewRequestId();
        transport.ThenDrop().ThenDrop();
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client, requestId: secondId));
        var listed = client.PendingCommands;
        listed.Count.ShouldBe(2);

        var resent = new List<string>();
        foreach (var pending in client.PendingCommands)
        {
            resent.Add((await client.ResendPendingAsync(pending, SdkCore.SnapshotResponse.Parser)).RequestId);
        }

        resent.ShouldBe(listed.Select(e => e.RequestId));
        client.PendingCommands.ShouldBeEmpty();
        listed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_foreach_over_the_entries_can_discard_each_of_them()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store, maxRetries: 0);
        transport.ThenDrop().ThenDrop();
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client));
        await Should.ThrowAsync<GameServerException>(() => SendAsync(client, requestId: GameServerClient.NewRequestId()));

        foreach (var pending in client.PendingCommands)
        {
            client.DiscardPendingCommand(pending.RequestId);
        }

        client.PendingCommands.ShouldBeEmpty();
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_caller_id_records_nothing()
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);

        await Should.ThrowAsync<ArgumentException>(() => SendAsync(client, requestId: "bad"));

        transport.Requests.ShouldBeEmpty();
        store.Entries.ShouldBeEmpty();
    }

    private static TransportResponse Protobuf(IMessage message) => new(200, GameServerClient.ProtobufContentType, message.ToByteArray());

    /// <summary>Counts every store call.</summary>
    private sealed class CountingStore : IPendingCommandStore
    {
        public int Calls { get; private set; }

        public IReadOnlyList<PendingCommand> Entries
        {
            get
            {
                Calls++;
                return [];
            }
        }

        public string? LoadProblem => null;

        public string? WriteProblem => null;

        public void Record(PendingCommand command) => Calls++;

        public void Remove(string requestId) => Calls++;
    }
}
