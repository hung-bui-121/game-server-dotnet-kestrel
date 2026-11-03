using System.Net;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Web.Requests;
using GameServer.Protocol.Core.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 013 T040 (FR-080, FR-081, FR-083, FR-086, US3-1..3, SC-007): one request event per request with the fields of
/// data-model §1, none for the health probes, exclusions and the per-deployment switch.
/// </summary>
public sealed class RequestLogTests
{
    private const string IncrementRoute = "/api/v1/counter/increment";
    private const string SnapshotRoute = "/api/v1/player/snapshot";
    private const string ConfigRoute = "/api/v1/config/get";
    private const string GmCounterRoute = "/gm/v1/counter/{playerId}";

    /// <summary>Expected fields of one request kind; <c>ActorId</c> "*" = the session's player.</summary>
    internal sealed record Expected(
        string Kind,
        string Method,
        string Route,
        string? Profile,
        string? Operation,
        string? ActorType,
        string? ActorId,
        int? Status,
        string ResultCode,
        LogLevel Level,
        bool Replayed = false,
        bool NotModified = false,
        bool Aborted = false,
        bool ReachesRunner = false);

    [Fact]
    public async Task A_scripted_session_writes_exactly_one_event_per_request_with_the_documented_fields()
    {
        await using var session = await RequestLogHost.StartAsync();

        var (playerId, results) = await RunSessionAsync(session);

        foreach (var (expected, entry) in results)
        {
            AssertEvent(expected, entry, playerId);
        }

        // Exactly one event per non-health request (the replay is the second event of its request id).
        session.Events.Count().ShouldBe(results.Count);
        results.Select(r => r.Expected.Kind).Distinct().Count().ShouldBe(17);

        // FR-083: for every request that reached the runner, command_results_total carries the event's code; requests refused
        // before the runner have an event and no metric.
        foreach (var (expected, entry) in results)
        {
            var traceId = (string)entry.Properties["TraceId"]!;
            var codes = session.CommandResults.Where(m => m.TraceId == traceId).Select(m => m.Code).ToList();
            if (expected.ReachesRunner)
            {
                codes.ShouldHaveSingleItem($"{expected.Kind}: one command_results_total measurement").ShouldBe(expected.ResultCode, expected.Kind);
            }
            else
            {
                codes.ShouldBeEmpty($"{expected.Kind}: no command_results_total measurement");
            }
        }
    }

    [Fact]
    public async Task An_excluded_path_is_not_logged_by_segment_prefix_and_case_insensitively()
    {
        await using var session = await RequestLogHost.StartAsync(settings: s => s["GameServer:RequestLog:ExcludedPaths:0"] = "/API/v1/player");
        var player = session.Host.PlayerClient("p_excl");

        var snapshot = await player.PostProtobufAsync(SnapshotRoute, new SnapshotRequest());
        var counted = await player.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());
        await session.EventAsync(counted);

        snapshot.StatusCode.ShouldBe(HttpStatusCode.OK);
        session.Events.ShouldNotContain(e => Equals(e.Properties["Route"], SnapshotRoute));
        session.Events.ShouldHaveSingleItem().Properties["Route"].ShouldBe(IncrementRoute);
    }

    [Fact]
    public async Task Disabled_writes_no_event_but_still_finalizes_the_outcome()
    {
        var outcomes = new List<IRequestOutcomeFeature>();
        await using var session = await RequestLogHost.StartAsync(
            settings: s => s["GameServer:RequestLog:Enabled"] = "false",
            beforeUseGameServer: app => app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                await next(context);
                lock (outcomes)
                {
                    outcomes.Add(context.RequestOutcome());
                }
            }));
        var player = session.Host.PlayerClient("p_off");

        (await player.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId())).EnsureSuccessStatusCode();
        await player.GetAsync("/api/v1/nope");
        await Task.Delay(100);

        session.Events.ShouldBeEmpty();
        lock (outcomes)
        {
            outcomes.Count.ShouldBe(2);
            outcomes.ShouldAllBe(o => o.Completed);
            outcomes.Select(o => o.ResultCode).ShouldBe(["OK", "NOT_FOUND"]);
            outcomes[0].Operation.ShouldBe("counter.increment");
        }
    }

    [Fact]
    public async Task Staging_is_off_by_default()
    {
        await using var session = await RequestLogHost.StartAsync();
        var client = session.Host.Client();
        await session.EventAsync(await client.GetAsync("/v1/time"));

        // Startup refuses test authentication and the in-memory store in Staging: flip the singleton after startup (as T015).
        session.Host.Services.GetRequiredService<GameServerOptions>().DeploymentEnvironment = "Staging";
        (await client.GetAsync("/v1/time")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Task.Delay(100);

        session.Events.Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_unmatched_scanner_path_is_cleaned_and_cut()
    {
        await using var session = await RequestLogHost.StartAsync();
        var path = "/scan%01%1b" + new string('a', 500) + "?token=query-should-not-appear";

        var response = await session.Host.Client().GetAsync(path);

        var route = (string)(await session.EventAsync(response)).Properties["Route"]!;
        route.ShouldBe("/scan??" + new string('a', 120 - 7) + "...");
        route.ShouldNotContain("query-should-not-appear");
    }

    [Theory]
    [InlineData("/players/p_7f3a9c1e2b4d/receipts/r_secret", "/players/...")]
    [InlineData("/reset-password/token-should-not-appear/", "/reset-password/...")]
    [InlineData("/robots.txt", "/robots.txt")]
    public async Task An_unmatched_path_logs_only_its_first_segment(string path, string expected)
    {
        await using var session = await RequestLogHost.StartAsync();

        var response = await session.Host.Client().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var entry = await session.EventAsync(response);
        entry.Properties["Route"].ShouldBe(expected);
        entry.Message.ShouldNotContain("should-not-appear");
        entry.Message.ShouldNotContain("p_7f3a9c1e2b4d");
    }

    [Theory]
    [InlineData("/api/v1/nope", "/api/...")]
    [InlineData("/a/", "/a/...")]
    [InlineData("//token", "/...")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    public void The_unmatched_route_keeps_the_first_segment_only(string path, string expected) =>
        RequestLogMiddleware.UnmatchedRoute(new PathString(path)).ShouldBe(expected);

    /// <summary>
    /// The 17 request kinds of T040 (+ the health probes, never logged) on <paramref name="session"/>; returns the session's player
    /// id and each request's expectation with its event. Also driven by the outcome-feature tests (T041).
    /// </summary>
    internal static async Task<(string PlayerId, List<(Expected Expected, CapturedLog Event)> Results)> RunSessionAsync(RequestLogHost session)
    {
        var host = session.Host;
        var results = new List<(Expected Expected, CapturedLog Event)>();

        // 1. Guest login (Public, not authenticated).
        var loginResponse = await host.Client().PostLoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = await loginResponse.ReadProtobufAsync<LoginResponse>();
        var player = host.BearerClient(login.SessionToken);
        results.Add((new("guest login", "POST", IdentityModule.LoginRoute, "Public", null, null, null, 200, "OK", LogLevel.Information), await session.EventAsync(loginResponse)));

        // 2. Snapshot.
        var snapshot = await player.PostProtobufAsync(SnapshotRoute, new SnapshotRequest());
        results.Add((new("snapshot", "POST", SnapshotRoute, "Player", null, "player", "*", 200, "OK", LogLevel.Information), await session.EventAsync(snapshot)));

        // 3. Config get, then not-modified with the version just received.
        var config = await player.PostProtobufAsync(ConfigRoute, new GetConfigRequest());
        var version = (await config.ReadProtobufAsync<GetConfigResponse>()).ConfigVersion;
        results.Add((new("config get", "POST", ConfigRoute, "Player", null, "player", "*", 200, "OK", LogLevel.Information), await session.EventAsync(config)));
        var notModified = await player.PostProtobufAsync(ConfigRoute, new GetConfigRequest { ConfigVersion = version });
        (await notModified.ReadProtobufAsync<GetConfigResponse>()).NotModified.ShouldBeTrue();
        results.Add((new("config not modified", "POST", ConfigRoute, "Player", null, "player", "*", 200, "OK", LogLevel.Information, NotModified: true), await session.EventAsync(notModified)));

        // 4. Command OK, 5. business rejection at HTTP 200, 6. replay of 4.
        var commandId = ProtobufHttp.NewRequestId();
        var command = await player.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, commandId);
        command.StatusCode.ShouldBe(HttpStatusCode.OK);
        results.Add((new("command ok", "POST", IncrementRoute, "Player", "counter.increment", "player", "*", 200, "OK", LogLevel.Information, ReachesRunner: true), await session.EventAsync(commandId)));
        var rejectedId = ProtobufHttp.NewRequestId();
        var rejected = await player.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 11 }, rejectedId);
        (await rejected.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("COUNTER_LIMIT_EXCEEDED");
        results.Add((new("business rejection", "POST", IncrementRoute, "Player", "counter.increment", "player", "*", 200, "COUNTER_LIMIT_EXCEEDED", LogLevel.Information, ReachesRunner: true), await session.EventAsync(rejectedId)));
        var replay = await player.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, commandId);
        (await replay.ReadProtobufAsync<CommandResponse>()).Replayed.ShouldBeTrue();
        var replayEvent = await WaitForSecondAsync(session, commandId);
        results.Add((new("replayed command", "POST", IncrementRoute, "Player", "counter.increment", "player", "*", 200, "OK", LogLevel.Information, Replayed: true, ReachesRunner: true), replayEvent));

        // 7. 400 validation (no client UUID), 8. 401, 11. 413, 12. invalid Protobuf.
        var noId = await player.PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 });
        results.Add((new("400 validation", "POST", IncrementRoute, "Player", "counter.increment", "player", "*", 400, "BAD_REQUEST", LogLevel.Warning), await session.EventAsync(noId)));
        var anonymous = await host.Client().PostProtobufAsync(IncrementRoute, new Wire.IncrementRequest { By = 1 }, ProtobufHttp.NewRequestId());
        results.Add((new("401", "POST", IncrementRoute, "Player", "counter.increment", null, null, 401, "UNAUTHENTICATED", LogLevel.Warning), await session.EventAsync(anonymous)));

        // 9. 404 and 10. 405 (no endpoint profile).
        var missing = await player.GetAsync("/api/v1/nope");
        results.Add((new("404", "GET", "/api/...", null, null, null, null, 404, "NOT_FOUND", LogLevel.Warning), await session.EventAsync(missing)));
        var wrongMethod = await player.GetAsync(IncrementRoute);
        results.Add((new("405", "GET", "/api/...", null, null, null, null, 405, "METHOD_NOT_ALLOWED", LogLevel.Warning), await session.EventAsync(wrongMethod)));

        var tooLarge = await player.PostBytesAsync(IncrementRoute, new byte[70 * 1024], ProtobufHttp.NewRequestId());
        results.Add((new("413", "POST", IncrementRoute, "Player", "counter.increment", "player", "*", 413, "PAYLOAD_TOO_LARGE", LogLevel.Warning), await session.EventAsync(tooLarge)));
        var garbage = await player.PostBytesAsync(IncrementRoute, [0xff, 0xff, 0xff, 0xff], ProtobufHttp.NewRequestId());
        results.Add((new("invalid protobuf", "POST", IncrementRoute, "Player", "counter.increment", "player", "*", 400, "BAD_REQUEST", LogLevel.Warning), await session.EventAsync(garbage)));

        // 13. Gate maintenance (before authentication: no actor).
        session.Gate.Maintenance = true;
        var maintenance = await player.PostProtobufAsync(SnapshotRoute, new SnapshotRequest());
        session.Gate.Maintenance = false;
        results.Add((new("gate maintenance", "POST", SnapshotRoute, "Player", null, null, null, 503, "MAINTENANCE", LogLevel.Error), await session.EventAsync(maintenance)));

        // 14. GM request (staff actor).
        var gm = await host.GmClient("staff_1", "counter.read").GetAsync("/gm/v1/counter/" + login.PlayerId);
        gm.StatusCode.ShouldBe(HttpStatusCode.OK);
        results.Add((new("gm request", "GET", GmCounterRoute, "Gm", null, "gm", "staff_1", 200, "OK", LogLevel.Information), await session.EventAsync(gm)));

        // 15. Client abort: no status, ABORTED, never a commit claim.
        var abortId = ProtobufHttp.NewRequestId();
        using (var cancel = new CancellationTokenSource())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RequestLogHost.WaitRoute) { Content = new ByteArrayContent([]) };
            request.Headers.Add("X-Request-Id", abortId);
            var call = player.SendAsync(request, cancel.Token);
            await session.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancel.CancelAsync();
            (await Record.ExceptionAsync(() => call)).ShouldBeAssignableTo<OperationCanceledException>();
        }

        results.Add((new("client abort", "POST", RequestLogHost.WaitRoute, "Player", null, "player", "*", null, RequestResultCodes.Aborted, LogLevel.Warning, Aborted: true), await session.EventAsync(abortId)));

        // 16. Injected 500.
        var failed = await player.PostBytesAsync(RequestLogHost.FailRoute, [], ProtobufHttp.NewRequestId());
        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        results.Add((new("injected 500", "POST", RequestLogHost.FailRoute, "Player", null, "player", "*", 500, "INTERNAL", LogLevel.Error), await session.EventAsync(failed)));

        // 17. Health probes: answered, never logged.
        var before = session.Events.Count();
        (await host.Client().GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client().GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client().GetAsync("/HEALTH/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Task.Delay(100);
        session.Events.Count().ShouldBe(before);

        return (login.PlayerId, results);
    }

    private static async Task<CapturedLog> WaitForSecondAsync(RequestLogHost session, string requestId)
    {
        for (var i = 0; i < 500; i++)
        {
            var events = session.Events.Where(e => Equals(e.Properties["RequestId"], requestId)).ToList();
            if (events.Count >= 2)
            {
                events.Count.ShouldBe(2);
                return events[1];
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No second request event for {requestId}.");
    }

    private static void AssertEvent(Expected expected, CapturedLog entry, string playerId)
    {
        var p = entry.Properties;
        var kind = expected.Kind;
        entry.EventId.Id.ShouldBe(1, kind);
        entry.EventId.Name.ShouldBe("Request", kind);
        entry.Level.ShouldBe(expected.Level, kind);
        p["{OriginalFormat}"].ShouldBe("{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms", kind);
        p.Count.ShouldBe(15, kind);
        p["HttpMethod"].ShouldBe(expected.Method, kind);
        p["Route"].ShouldBe(expected.Route, kind);
        p["Profile"].ShouldBe(expected.Profile, kind);
        p["Operation"].ShouldBe(expected.Operation, kind);
        p["ActorType"].ShouldBe(expected.ActorType, kind);
        p["ActorId"].ShouldBe(expected.ActorId == "*" ? playerId : expected.ActorId, kind);
        p["Status"].ShouldBe(expected.Status, kind);
        p["ResultCode"].ShouldBe(expected.ResultCode, kind);
        p["Replayed"].ShouldBe(expected.Replayed, kind);
        p["NotModified"].ShouldBe(expected.NotModified, kind);
        p["Aborted"].ShouldBe(expected.Aborted, kind);
        Guid.TryParse((string)p["RequestId"]!, out _).ShouldBeTrue(kind);
        ((string)p["TraceId"]!).Length.ShouldBe(32, kind);
        ((double)p["DurationMs"]!).ShouldBeGreaterThanOrEqualTo(0, kind);
        entry.Message.Contains(playerId, StringComparison.Ordinal).ShouldBeFalse(kind);
    }
}
