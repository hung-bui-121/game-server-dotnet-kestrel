using System.Net;
using GameServer.Platform.Commands;
using GameServer.Platform.Web.Commands;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 010 review minor 6: an <c>afterExecute</c> hook runs after the commit, so its failure is logged and never turns the
/// committed command into an error response (the client would retry a command that succeeded).
/// </summary>
public sealed class AfterExecuteHookTests
{
    private const string Route = "/api/v1/test/increment-with-failing-hook";

    [Fact]
    public async Task A_failing_hook_is_logged_and_the_committed_command_still_answers_ok()
    {
        var calls = 0;
        await using var host = await TestHost.StartAsync(afterUseGameServer: app => app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            Route,
            IncrementHandler.Operation,
            request => new IncrementCommand(request.By, null),
            result => new Wire.IncrementResult { Value = result.Value },
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("profile cache unavailable");
            }));
        var player = host.PlayerClient("p1");
        var requestId = ProtobufHttp.NewRequestId();

        var first = await player.PostProtobufAsync(Route, new Wire.IncrementRequest { By = 1 }, requestId);
        var replay = await player.PostProtobufAsync(Route, new Wire.IncrementRequest { By = 1 }, requestId);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.ReadProtobufAsync<CommandResponse>()).Replayed.ShouldBeTrue();
        calls.ShouldBe(2);
        host.Store.All(ReceiptDocument.Collection).Count(d => d.Content["playerId"]?.GetValue<string>() == "p1").ShouldBe(1);
        host.Logs.Count(l => l.Contains($"After-execute hook of {IncrementHandler.Operation} failed", StringComparison.Ordinal)).ShouldBe(2);
    }
}
