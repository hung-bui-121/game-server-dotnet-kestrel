extern alias sdk;

using sdk::GameServer.Client;
using Shouldly;
using SdkCore = sdk::GameServer.Protocol.Core.V1;
using SampleWire = SampleGame.Protocol.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T055 (FR-040, FR-041, US5-1, research R10, data-model §4.1): a caller-owned request id is validated before any I/O,
/// normalized to lowercase, sent on every attempt with the same bytes and returned on the result and on SDK-made errors.
/// </summary>
public sealed class RequestIdTests
{
    private const string CallerId = "3f2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d";
    private const string StartRoute = "api/v1/battle/start";

    [Fact]
    public async Task The_caller_id_is_sent_on_every_retry_with_the_same_bytes_and_returned()
    {
        var (client, transport, _, _) = PendingClients.Create(store: null, maxRetries: 3);
        transport.ThenDrop().Then(_ => FakeTransport.Envelope(503, "COMMIT_UNKNOWN_RETRY", retryable: true, codeNumber: 13, retryAfterMs: 10));

        var result = await client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, CallerId);

        result.RequestId.ShouldBe(CallerId);
        transport.Requests.Count.ShouldBe(3);
        transport.Requests.Select(PendingClients.RequestIdOf).ShouldAllBe(id => id == CallerId);
        transport.Requests.Select(r => Convert.ToHexString(r.Body!)).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_null_id_is_generated_and_returned()
    {
        var (client, transport, _, _) = PendingClients.Create(store: null);

        var result = await client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, requestId: null);

        result.RequestId.ShouldBe(PendingClients.RequestIdOf(transport.Requests.ShouldHaveSingleItem()));
        Guid.TryParseExact(result.RequestId, "D", out _).ShouldBeTrue();
        result.RequestId.ShouldBe(result.RequestId.ToLowerInvariant());
    }

    [Fact]
    public async Task The_existing_overload_reports_its_generated_id()
    {
        var (client, transport, _, _) = PendingClients.Create(store: null);

        var result = await client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser);

        result.RequestId.ShouldBe(PendingClients.RequestIdOf(transport.Requests.ShouldHaveSingleItem()));
    }

    [Fact]
    public async Task An_uppercase_id_is_normalized_to_lowercase()
    {
        var (client, transport, _, _) = PendingClients.Create(store: null);

        var result = await client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, CallerId.ToUpperInvariant());

        result.RequestId.ShouldBe(CallerId);
        PendingClients.RequestIdOf(transport.Requests.ShouldHaveSingleItem()).ShouldBe(CallerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3f2a9c1e7b114d2a9c0d5e6f7a8b9c0d")]
    [InlineData("{3f2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d}")]
    [InlineData("3f2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0dd")]
    [InlineData("not-a-uuid")]
    public async Task An_invalid_id_throws_before_any_transport_call_or_record(string requestId)
    {
        var store = new InMemoryPendingCommandStore();
        var (client, transport, _, _) = PendingClients.Create(store);

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, requestId));

        ex.ParamName.ShouldBe("requestId");
        transport.Requests.ShouldBeEmpty();
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_network_error_carries_the_request_id()
    {
        var (client, transport, _, _) = PendingClients.Create(store: null, maxRetries: 1);
        transport.ThenDrop().ThenDrop();

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, CallerId));

        ex.Error.Code.ShouldBe(ErrorCodes.NetworkError);
        ex.Error.RequestId.ShouldBe(CallerId);
    }

    [Fact]
    public async Task An_invalid_response_carries_the_request_id()
    {
        var (client, transport, _, _) = PendingClients.Create(store: null, maxRetries: 0);
        transport.ThenAnswer(new TransportResponse(404, "text/html", [1, 2, 3]));

        var ex = await Should.ThrowAsync<GameServerException>(() =>
            client.SendCommandAsync(PendingClients.Route, PendingClients.Request(), SdkCore.SnapshotResponse.Parser, CallerId));

        ex.Error.Code.ShouldBe(ErrorCodes.InvalidResponse);
        ex.Error.RequestId.ShouldBe(CallerId);
    }

    [Fact]
    public void NewRequestId_is_a_fresh_lowercase_uuid_in_format_d()
    {
        var first = GameServerClient.NewRequestId();
        var second = GameServerClient.NewRequestId();

        Guid.TryParseExact(first, "D", out _).ShouldBeTrue();
        first.ShouldBe(first.ToLowerInvariant());
        first.Length.ShouldBe(36);
        second.ShouldNotBe(first);
    }

    [Fact]
    public void The_four_argument_result_constructor_keeps_an_empty_request_id()
    {
        new CommandResult<SdkCore.SnapshotResponse>(new SdkCore.CommandResponse { Code = "OK" }, null, false, false).RequestId.ShouldBeEmpty();
    }

    [Fact]
    public void The_three_parameter_client_error_keeps_its_binding()
    {
        GameServerError.Client(ErrorCodes.NetworkError, "x").RequestId.ShouldBeNull();
        GameServerError.Client(ErrorCodes.NetworkError, "x", true).Retryable.ShouldBeTrue();
        GameServerError.Client(ErrorCodes.NetworkError, "x", true, CallerId).RequestId.ShouldBe(CallerId);
    }

    /// <summary>CMD-02 against the sample host: the same caller id and bytes in a later call replay the committed command.</summary>
    [Fact]
    public async Task The_same_id_and_bytes_in_a_later_call_replay_against_the_sample_host()
    {
        await using var harness = await SdkHarness.StartAsync();
        var (client, _, _) = harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        var start = new SampleWire.StartBattleRequest { Stage = 1 };

        var first = await client.SendCommandAsync(StartRoute, start, SampleWire.StartBattleResult.Parser, CallerId);
        var again = await client.SendCommandAsync(StartRoute, start, SampleWire.StartBattleResult.Parser, CallerId);

        first.Replayed.ShouldBeFalse();
        again.Replayed.ShouldBeTrue();
        again.ReceiptId.ShouldBe(first.ReceiptId);
        again.Result!.BattleId.ShouldBe(first.Result!.BattleId);
    }
}
