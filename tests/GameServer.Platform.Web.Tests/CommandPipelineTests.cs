using System.Net;
using GameServer.Platform.Testing;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US1 over HTTP: ACC-04, ACC-05, ACC-07 and the X-Request-Id contract.</summary>
public sealed class CommandPipelineTests : IAsyncLifetime
{
    private const string Increment = "/api/v1/counter/increment";
    private TestHost _host = null!;
    private HttpClient _player = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        _player = _host.PlayerClient("p1");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<long> CounterValueAsync()
    {
        var response = await _player.PostProtobufAsync("/api/v1/counter/get", new GetCounterRequest());
        return (await response.ReadProtobufAsync<GetCounterResponse>()).Value;
    }

    [Fact]
    public async Task Command_returns_section_update_and_replays_identically()
    {
        var requestId = ProtobufHttp.NewRequestId();

        var first = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 5 }, requestId);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.GetValues("X-Request-Id").ShouldContain(requestId);
        var body = await first.ReadProtobufAsync<CommandResponse>();
        body.Code.ShouldBe("OK");
        body.ReceiptId.ShouldNotBeNullOrEmpty();
        body.ConfigVersion.ShouldStartWith("test-1+");
        body.ServerTimeUtcMs.ShouldBeGreaterThan(0);
        IncrementResult.Parser.ParseFrom(body.Result).Value.ShouldBe(5);
        var update = body.Updates.ShouldHaveSingleItem();
        update.Id.ShouldBe("counter");
        update.BaseRev.ShouldBe(0);
        update.NewRev.ShouldBe(1);
        update.Full.ShouldBeTrue();
        CounterState.Parser.ParseFrom(update.Payload).Value.ShouldBe(5);

        var second = await (await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 5 }, requestId)).ReadProtobufAsync<CommandResponse>();
        second.Replayed.ShouldBeTrue();
        second.ReceiptId.ShouldBe(body.ReceiptId);
        second.Result.ShouldBe(body.Result);
        second.Updates.ShouldHaveSingleItem().NewRev.ShouldBe(1);
        (await CounterValueAsync()).ShouldBe(5);
    }

    [Fact]
    public async Task Same_requestId_different_body_is_409()
    {
        var requestId = ProtobufHttp.NewRequestId();
        await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 5 }, requestId);

        var response = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 6 }, requestId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe("IDEMPOTENCY_PAYLOAD_MISMATCH");
        error.RequestId.ShouldBe(requestId);
        error.Retryable.ShouldBeFalse();
        (await CounterValueAsync()).ShouldBe(5);
    }

    [Fact]
    public async Task Concurrent_identical_requests_have_one_effect()
    {
        var requestId = ProtobufHttp.NewRequestId();

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            _player.PostProtobufAsync(Increment, new IncrementRequest { By = 2 }, requestId)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var bodies = await Task.WhenAll(responses.Select(r => r.ReadProtobufAsync<CommandResponse>()));
        bodies.Select(b => b.ReceiptId).Distinct().Count().ShouldBe(1);
        (await CounterValueAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Ambiguous_commit_returns_503_and_retry_returns_committed_result()
    {
        var requestId = ProtobufHttp.NewRequestId();
        _host.Store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        var ambiguous = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 3 }, requestId);
        ambiguous.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ambiguous.Headers.RetryAfter.ShouldNotBeNull();
        var error = await ambiguous.ReadErrorAsync();
        error.Code.ShouldBe("COMMIT_UNKNOWN_RETRY");
        error.Retryable.ShouldBeTrue();
        error.RetryAfterMs.ShouldBeGreaterThan(0);

        var retry = await (await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 3 }, requestId)).ReadProtobufAsync<CommandResponse>();
        retry.Replayed.ShouldBeTrue();
        (await CounterValueAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Timeout_after_commit_returns_503_and_retry_replays_without_second_effect()
    {
        await using var host = await TestHost.StartAsync(settings: s => s["GameServer:Limits:PlayerTimeoutSeconds"] = "1");
        var player = host.PlayerClient("slow");
        var requestId = ProtobufHttp.NewRequestId();
        host.Store.Faults.CommitDelay = TimeSpan.FromMilliseconds(1500);
        host.Store.Faults.Enqueue(StoreFault.DelayCommit);

        var timedOut = await player.PostProtobufAsync(Increment, new IncrementRequest { By = 4 }, requestId);
        timedOut.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await timedOut.ReadErrorAsync()).Code.ShouldBe("REQUEST_TIMEOUT");

        var retry = await (await player.PostProtobufAsync(Increment, new IncrementRequest { By = 4 }, requestId)).ReadProtobufAsync<CommandResponse>();
        retry.Replayed.ShouldBeTrue();
        var value = await (await player.PostProtobufAsync("/api/v1/counter/get", new GetCounterRequest())).ReadProtobufAsync<GetCounterResponse>();
        value.Value.ShouldBe(4);
    }

    [Fact]
    public async Task Business_rejection_is_200_with_code_and_no_receipt()
    {
        var response = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 11 }, ProtobufHttp.NewRequestId());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadProtobufAsync<CommandResponse>();
        body.Code.ShouldBe("COUNTER_LIMIT_EXCEEDED");
        body.CodeNumber.ShouldBe(10001);
        body.ReceiptId.ShouldBeEmpty();
        body.Updates.ShouldBeEmpty();
        _host.Store.Count("receipts").ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task Command_without_valid_requestId_is_400(string? requestId)
    {
        var response = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 1 }, requestId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("BAD_REQUEST");
    }

    [Fact]
    public async Task Invalid_wire_values_are_rejected_by_the_adapter()
    {
        var response = await _player.PostProtobufAsync(Increment, new IncrementRequest { By = 1, Note = new string('n', 65) }, ProtobufHttp.NewRequestId());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Garbage_protobuf_body_is_400()
    {
        var response = await _player.PostBytesAsync(Increment, [0xFF, 0xFF, 0xFF, 0xFF], ProtobufHttp.NewRequestId());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("BAD_REQUEST");
    }
}
