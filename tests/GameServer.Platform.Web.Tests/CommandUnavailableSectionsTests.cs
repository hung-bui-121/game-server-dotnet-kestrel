using System.Net;
using GameServer.Platform.Commands;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T042 (FR-024, US3-3, US3-4, research R7 "Post-commit"): a mapper failure after the commit keeps the commit and lists the
/// section; a replay lists an aggregate it cannot read now; a touching command on a broken aggregate is the typed envelope.
/// </summary>
public sealed class CommandUnavailableSectionsTests
{
    [Fact]
    public async Task A_mapper_failure_after_the_commit_answers_200_and_lists_the_section_and_the_retry_replays_it()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var player = host.PlayerClient("p_post_commit");
        var requestId = ProtobufHttp.NewRequestId();

        var first = await CommandAsync(player, BrokenAggregateHost.SetRoute.Template, BrokenAggregateHost.Request(9, StartingStateAggregates.MapperFailMarker), requestId);
        var retry = await CommandAsync(player, BrokenAggregateHost.SetRoute.Template, BrokenAggregateHost.Request(9, StartingStateAggregates.MapperFailMarker), requestId);

        foreach (var response in new[] { first, retry })
        {
            response.Code.ShouldBe("OK");
            response.ReceiptId.ShouldNotBeNullOrEmpty();
            Wire.IncrementResult.Parser.ParseFrom(response.Result).Value.ShouldBe(9);
            response.Updates.ShouldBeEmpty();
            response.UnavailableSections.ShouldBe([new UnavailableSection { Id = StartingStateAggregates.Starter, Code = "AGGREGATE_UNREADABLE", CodeNumber = 25 }]);
        }

        retry.Replayed.ShouldBeTrue();
        retry.ReceiptId.ShouldBe(first.ReceiptId);
        host.Store.Peek(ReceiptDocument.KeyFor(first.ReceiptId)).ShouldNotBeNull();
        host.Store.Peek(BrokenDocuments.KeyOf(BrokenReason.MapperFailed, "p_post_commit"))!["state"]!["padding"]!.GetValue<string>()
            .ShouldBe(StartingStateAggregates.MapperFailMarker);
    }

    [Fact]
    public async Task The_replay_of_a_command_whose_aggregate_broke_since_lists_it()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var player = host.PlayerClient("p_replay");
        var requestId = ProtobufHttp.NewRequestId();
        var first = await CommandAsync(player, BrokenAggregateHost.SetRoute.Template, BrokenAggregateHost.Request(5), requestId);
        first.Updates.Select(u => u.Id).ShouldBe([StartingStateAggregates.Starter]);
        BrokenDocuments.Seed(host.Store, BrokenReason.VersionAhead, "p_replay");

        var replay = await CommandAsync(player, BrokenAggregateHost.SetRoute.Template, BrokenAggregateHost.Request(5), requestId);

        replay.Replayed.ShouldBeTrue();
        replay.Code.ShouldBe("OK");
        replay.Result.ShouldBe(first.Result);
        replay.Updates.ShouldBeEmpty();
        replay.UnavailableSections.ShouldBe([new UnavailableSection { Id = StartingStateAggregates.Starter, Code = "AGGREGATE_VERSION_AHEAD", CodeNumber = 24 }]);
    }

    [Fact]
    public async Task A_touching_command_on_a_broken_aggregate_is_the_typed_envelope_with_the_aggregate()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        BrokenDocuments.Seed(host.Store, BrokenReason.VersionAhead, "p_touch");

        var response = await host.PlayerClient("p_touch").PostProtobufAsync(BrokenAggregateHost.SetRoute.Template, BrokenAggregateHost.Request(1), ProtobufHttp.NewRequestId());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe("AGGREGATE_VERSION_AHEAD");
        error.RetryAfterMs.ShouldBe(1000);
        error.Details.ShouldBe(new Dictionary<string, string> { ["aggregate"] = StartingStateAggregates.Starter });
    }

    private static async Task<CommandResponse> CommandAsync(HttpClient player, string route, Wire.IncrementRequest request, string requestId)
    {
        var response = await player.PostProtobufAsync(route, request, requestId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadProtobufAsync<CommandResponse>();
    }
}
