using System.Net;
using GameServer.Actors;
using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T036 (FR-026, research R5 "Middleware", Planning note 7): a reported read failure gets its typed envelope from the
/// error middleware but no second log line; any other 5xx code is still logged once.
/// </summary>
public sealed class ExceptionMiddlewareTests
{
    private static readonly RouteDeclaration Marked = RouteDeclaration.Post("test.marked", "/api/v1/test/marked", SecurityProfile.Player);
    private static readonly RouteDeclaration Unmarked = RouteDeclaration.Post("test.unmarked", "/api/v1/test/unmarked", SecurityProfile.Player);

    private static readonly string MiddlewareCategory = typeof(ExceptionMiddleware).FullName!;

    [Fact]
    public async Task A_marked_read_failure_gets_the_typed_envelope_and_no_middleware_line()
    {
        await using var host = await StartAsync();

        var response = await host.PlayerClient("p1").PostAsync(Marked.Template, null);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe("AGGREGATE_VERSION_AHEAD");
        error.Retryable.ShouldBeTrue();
        error.RetryAfterMs.ShouldBe(1000);
        error.Details.ShouldBe(new Dictionary<string, string> { ["aggregate"] = "wallet" });
        host.LogEntries.Lines(MiddlewareCategory).ShouldBeEmpty();
        host.LogEntries.ShouldNotContain(e => e.Message == "Unhandled exception.");
    }

    [Fact]
    public async Task An_unmarked_5xx_game_server_exception_is_still_logged_once()
    {
        await using var host = await StartAsync();

        var response = await host.PlayerClient("p1").PostAsync(Unmarked.Template, null);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.ReadErrorAsync()).Code.ShouldBe("AGGREGATE_UNREADABLE");
        var line = host.LogEntries.Where(e => e.Category == MiddlewareCategory).ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Error);
        line.Message.ShouldBe("Request failed with AGGREGATE_UNREADABLE: not a reported read failure");
    }

    private static Task<TestHost> StartAsync() =>
        SampleTestHost.StartAsync(
            modules: SampleTestHost.WithRoutes(Marked, Unmarked),
            beforeUseGameServer: app =>
            {
                app.MapDeclared(Marked, (Func<IResult>)(() =>
                    throw new AggregateReadFailure("wallet", "p1", AggregateReadFailureReason.VersionAhead, string.Empty, 3, 2).ToException())).AsPlayerEndpoint();
                app.MapDeclared(Unmarked, (Func<IResult>)(() =>
                    throw new GameServerException(CoreErrorCodes.AggregateUnreadable, "not a reported read failure"))).AsPlayerEndpoint();
            });
}
