using System.Net;
using System.Text.Json;
using GameServer.Platform.Web.Routing;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US5: ErrorEnvelope / Problem Details, limits, time (ACC-09, ACC-40, ACC-43, ACC-52).</summary>
public sealed class ErrorContractTests
{
    private static async Task AssertEnvelope(HttpResponseMessage response, HttpStatusCode status, string code, bool retryable = false)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-protobuf");
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe(code);
        error.Retryable.ShouldBe(retryable);
        error.RequestId.ShouldNotBeNullOrEmpty();
        error.ServerTimeUtcMs.ShouldBeGreaterThan(0);
        error.Message.ShouldNotContain("Exception");
    }

    [Fact]
    public async Task Player_middleware_errors_are_error_envelopes()
    {
        await using var host = await TestHost.StartAsync();
        var player = host.PlayerClient("p1");

        await AssertEnvelope(
            await player.PostBytesAsync("/api/v1/counter/increment", new byte[65 * 1024], ProtobufHttp.NewRequestId()),
            HttpStatusCode.RequestEntityTooLarge,
            "PAYLOAD_TOO_LARGE");
        await AssertEnvelope(
            await player.PostBytesAsync("/api/v1/counter/increment", "{}"u8.ToArray(), ProtobufHttp.NewRequestId(), "application/json"),
            HttpStatusCode.UnsupportedMediaType,
            "UNSUPPORTED_MEDIA_TYPE");
        await AssertEnvelope(
            await host.Client().PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId()),
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED");
        await AssertEnvelope(await player.GetAsync("/api/v1/nope"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Concurrency_limit_returns_429_with_retry_after()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        await using var host = await TestHost.StartAsync(
            settings: s =>
            {
                s["GameServer:Limits:CommandConcurrency"] = "1";
                s["GameServer:Limits:QueueFactor"] = "0";
            },
            beforeUseGameServer: app => app.MapPost("/api/v1/test/block", async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return Results.Ok();
            }).AsPlayerEndpoint());
        var player = host.PlayerClient("p1");

        var blocked = player.PostAsync("/api/v1/test/block", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = await player.PostAsync("/api/v1/test/block", null);
        release.SetResult();
        await blocked;

        await AssertEnvelope(rejected, HttpStatusCode.TooManyRequests, "RATE_LIMITED", retryable: true);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Unhandled_exception_is_500_without_details()
    {
        await using var host = await TestHost.StartAsync(
            beforeUseGameServer: app => app.MapPost("/api/v1/test/boom", (Func<IResult>)(() => throw new InvalidOperationException("secret detail")))
                .AsPlayerEndpoint());

        var response = await host.PlayerClient("p1").PostAsync("/api/v1/test/boom", null);

        await AssertEnvelope(response, HttpStatusCode.InternalServerError, "INTERNAL");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("secret detail");
    }

    [Fact]
    public async Task Request_timeout_is_503_request_timeout()
    {
        await using var host = await TestHost.StartAsync(
            settings: s => s["GameServer:Limits:PlayerTimeoutSeconds"] = "1",
            beforeUseGameServer: app => app.MapPost("/api/v1/test/slow", async (HttpContext http) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), http.RequestAborted);
                return Results.Ok();
            }).AsPlayerEndpoint());

        var response = await host.PlayerClient("p1").PostAsync("/api/v1/test/slow", null);

        await AssertEnvelope(response, HttpStatusCode.ServiceUnavailable, "REQUEST_TIMEOUT", retryable: true);
    }

    [Fact]
    public async Task Time_endpoint_is_public()
    {
        await using var host = await TestHost.StartAsync();

        var response = await host.Client().GetAsync("/v1/time");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadProtobufAsync<TimeResponse>()).ServerTimeUtcMs.ShouldBe(host.Clock.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Gm_api_uses_problem_details_and_checks_actor_and_permission()
    {
        await using var host = await TestHost.StartAsync();
        await host.PlayerClient("p9").PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 3 }, ProtobufHttp.NewRequestId());

        var asPlayer = await host.PlayerClient("p9").GetAsync("/gm/v1/counter/p9");
        asPlayer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        asPlayer.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonDocument.Parse(await asPlayer.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString().ShouldBe("UNAUTHENTICATED");

        var noPermission = await host.GmClient("staff", "other.perm").GetAsync("/gm/v1/counter/p9");
        noPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var ok = await host.GmClient("staff", "counter.read").GetAsync("/gm/v1/counter/p9");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("value").GetInt64().ShouldBe(3);
        json.GetProperty("rev").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Gm_token_cannot_call_player_api()
    {
        await using var host = await TestHost.StartAsync();

        var response = await host.GmClient("staff", "counter.read").PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest());

        await AssertEnvelope(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Endpoint_added_after_validation_without_profile_is_denied()
    {
        await using var host = await TestHost.StartAsync(afterUseGameServer: app => app.MapGet("/api/v1/late", () => "leak"));

        var response = await host.PlayerClient("p1").GetAsync("/api/v1/late");

        await AssertEnvelope(response, HttpStatusCode.Forbidden, "FORBIDDEN");
    }
}
