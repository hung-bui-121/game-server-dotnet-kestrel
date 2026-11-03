using System.Collections.Concurrent;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Requests;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T041 (FR-084, US3-4, SC-007): consumer code reads the request outcome — a middleware placed before
/// <c>UseGameServer</c> after <c>await next()</c>, consumer endpoints and <c>afterExecute</c> hooks. The middleware below never
/// touches <c>Response.Body</c>.
/// </summary>
public sealed class RequestOutcomeFeatureTests
{
    private const string CodedRoute = "/api/v1/consumer/coded";
    private const string TeapotRoute = "/api/v1/consumer/teapot";
    private const string HookedRoute = "/api/v1/consumer/increment-hooked";

    private sealed record Observed(string? ResultCode, int? StatusCode, string? Operation, bool Replayed, bool NotModified, bool Aborted, bool Completed);

    [Fact]
    public async Task A_consumer_middleware_reads_the_same_outcome_as_the_event_for_every_request()
    {
        var observed = new ConcurrentDictionary<string, Observed>();
        await using var session = await RequestLogHost.StartAsync(beforeUseGameServer: app => app.Use(Recorder(observed)));

        var (_, results) = await RequestLogTests.RunSessionAsync(session);

        var replayed = 0;
        foreach (var (expected, entry) in results)
        {
            var requestId = (string)entry.Properties["RequestId"]!;
            var outcome = await ObservedAsync(observed, requestId, expected.Replayed);
            outcome.Completed.ShouldBeTrue(expected.Kind);
            outcome.ResultCode.ShouldNotBeNull(expected.Kind);
            outcome.ResultCode.ShouldBe(entry.Properties["ResultCode"], expected.Kind);
            outcome.StatusCode.ShouldBe(entry.Properties["Status"], expected.Kind);
            outcome.Operation.ShouldBe(entry.Properties["Operation"], expected.Kind);
            outcome.Replayed.ShouldBe(entry.Properties["Replayed"], expected.Kind);
            outcome.NotModified.ShouldBe(entry.Properties["NotModified"], expected.Kind);
            outcome.Aborted.ShouldBe(entry.Properties["Aborted"], expected.Kind);
            (outcome.StatusCode is null).ShouldBe(outcome.Aborted, expected.Kind);
            replayed += expected.Replayed ? 1 : 0;
        }

        replayed.ShouldBe(1);
    }

    [Fact]
    public async Task A_consumer_endpoint_sets_its_own_code_and_a_bare_418_becomes_HTTP_418()
    {
        var observed = new ConcurrentDictionary<string, Observed>();
        await using var session = await RequestLogHost.StartAsync(
            beforeUseGameServer: app => app.Use(Recorder(observed)),
            afterUseGameServer: app =>
            {
                app.MapPost(CodedRoute, (HttpContext http) =>
                    {
                        var outcome = http.RequestOutcome();
                        outcome.ResultCode = "CONSUMER_CODED";
                        outcome.Operation = "consumer.coded";
                        return Results.Ok();
                    })
                    .AsPlayerEndpoint();
                app.MapPost(TeapotRoute, () => Results.StatusCode(StatusCodes.Status418ImATeapot)).AsPlayerEndpoint();
            });
        var player = session.Host.PlayerClient("p_consumer");

        var coded = await session.EventAsync(await player.PostAsync(CodedRoute, null));
        var teapot = await session.EventAsync(await player.PostAsync(TeapotRoute, null));

        coded.Properties["ResultCode"].ShouldBe("CONSUMER_CODED");
        coded.Properties["Operation"].ShouldBe("consumer.coded");
        coded.Properties["Status"].ShouldBe(200);
        (await ObservedAsync(observed, (string)coded.Properties["RequestId"]!)).ResultCode.ShouldBe("CONSUMER_CODED");
        teapot.Properties["ResultCode"].ShouldBe("HTTP_418");
        teapot.Properties["Status"].ShouldBe(418);
        teapot.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Warning);
        (await ObservedAsync(observed, (string)teapot.Properties["RequestId"]!)).ResultCode.ShouldBe("HTTP_418");
    }

    [Fact]
    public async Task An_afterExecute_hook_reads_the_values_set_so_far()
    {
        Observed? inHook = null;
        await using var session = await RequestLogHost.StartAsync(afterUseGameServer: app =>
            app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
                HookedRoute,
                IncrementHandler.Operation,
                request => new IncrementCommand(request.By, null),
                result => new Wire.IncrementResult { Value = result.Value },
                (_, http) =>
                {
                    var outcome = http.RequestOutcome();
                    inHook = new(outcome.ResultCode, outcome.StatusCode, outcome.Operation, outcome.Replayed, outcome.NotModified, outcome.Aborted, outcome.Completed);
                    return Task.CompletedTask;
                }));
        var player = session.Host.PlayerClient("p_hook");
        var requestId = ProtobufHttp.NewRequestId();

        (await player.PostProtobufAsync(HookedRoute, new Wire.IncrementRequest { By = 1 }, requestId)).EnsureSuccessStatusCode();
        await session.EventAsync(requestId);

        inHook.ShouldNotBeNull();
        inHook.ResultCode.ShouldBe("OK");
        inHook.Operation.ShouldBe(IncrementHandler.Operation);
        inHook.Replayed.ShouldBeFalse();
        inHook.Completed.ShouldBeFalse();
        inHook.StatusCode.ShouldBeNull();
    }

    /// <summary>A consumer middleware as a game would place it before <c>UseGameServer</c>: reads the outcome after <c>next</c>.</summary>
    private static Func<HttpContext, RequestDelegate, Task> Recorder(ConcurrentDictionary<string, Observed> observed) =>
        async (context, next) =>
        {
            await next(context);
            var outcome = context.RequestOutcome();
            var value = new Observed(outcome.ResultCode, outcome.StatusCode, outcome.Operation, outcome.Replayed, outcome.NotModified, outcome.Aborted, outcome.Completed);

            // A replay shares its request id with the original: keep both, the second under a suffix.
            var requestId = CorrelationMiddleware.GetCorrelationId(context);
            if (!observed.TryAdd(requestId, value))
            {
                observed[requestId + "#2"] = value;
            }
        };

    private static async Task<Observed> ObservedAsync(ConcurrentDictionary<string, Observed> observed, string requestId, bool second = false)
    {
        var key = second ? requestId + "#2" : requestId;
        for (var i = 0; i < 500; i++)
        {
            if (observed.TryGetValue(key, out var value))
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"The consumer middleware saw no request {key}.");
    }
}
