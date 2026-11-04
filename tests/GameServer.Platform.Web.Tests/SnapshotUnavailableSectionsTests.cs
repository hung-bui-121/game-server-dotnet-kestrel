using System.Net;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T038 (FR-022, US3-1, US3-2, US3-8, wire-changes §2): a broken section is left out of the snapshot and listed with its
/// code; every other section is the same as for an undamaged twin player. (Next to <see cref="SnapshotTests"/>, on the broken
/// aggregate host.)
/// </summary>
public sealed class SnapshotUnavailableSectionsTests
{
    private const string Snapshot = "/api/v1/player/snapshot";

    [Theory]
    [InlineData(BrokenReason.VersionAhead, "AGGREGATE_VERSION_AHEAD", 24)]
    [InlineData(BrokenReason.Unreadable, "AGGREGATE_UNREADABLE", 25)]
    [InlineData(BrokenReason.UpgradeFailed, "AGGREGATE_UNREADABLE", 25)]
    [InlineData(BrokenReason.MapperFailed, "AGGREGATE_UNREADABLE", 25)]
    public async Task A_broken_section_is_listed_and_the_others_equal_the_twin_players(BrokenReason reason, string code, int number)
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        var (broken, twin) = ("p_broken_" + reason, "p_twin_" + reason);
        BrokenDocuments.Seed(host.Store, reason, broken);
        BrokenDocuments.Seed(host.Store, reason, twin, damaged: false);
        var aggregate = BrokenDocuments.AggregateOf(reason);

        foreach (var sections in new[] { Array.Empty<string>(), [aggregate, "wallet"] })
        {
            var damaged = await SnapshotAsync(host, broken, sections);
            var undamaged = await SnapshotAsync(host, twin, sections);

            damaged.Sections.Select(s => s.Id).ShouldNotContain(aggregate);
            damaged.UnavailableSections.ShouldBe([new UnavailableSection { Id = aggregate, Code = code, CodeNumber = number }]);
            undamaged.UnavailableSections.ShouldBeEmpty();
            damaged.Sections.ShouldBe(undamaged.Sections.Where(s => s.Id != aggregate).ToArray());
        }
    }

    [Fact]
    public async Task Every_requested_section_broken_is_still_200_with_no_section()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        BrokenDocuments.Seed(host.Store, BrokenReason.VersionAhead, "p_all");
        BrokenDocuments.Seed(host.Store, BrokenReason.UpgradeFailed, "p_all");

        var snapshot = await SnapshotAsync(host, "p_all", ["starter", "broken_probe"]);

        snapshot.Sections.ShouldBeEmpty();
        snapshot.UnavailableSections.Select(u => u.Id).ShouldBe(["broken_probe", "starter"]);
    }

    [Fact]
    public async Task An_unknown_section_is_still_bad_request()
    {
        await using var host = await BrokenAggregateHost.StartAsync();
        BrokenDocuments.Seed(host.Store, BrokenReason.Unreadable, "p_unknown");
        var request = new SnapshotRequest();
        request.Sections.Add(["starter", "nope"]);

        var response = await host.PlayerClient("p_unknown").PostProtobufAsync(Snapshot, request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<SnapshotResponse> SnapshotAsync(TestHost host, string playerId, string[] sections)
    {
        var request = new SnapshotRequest();
        request.Sections.Add(sections);
        var response = await host.PlayerClient(playerId).PostProtobufAsync(Snapshot, request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadProtobufAsync<SnapshotResponse>();
    }
}
