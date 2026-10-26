using System.Net;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Sample.Counter.Protocol.V1;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>US4: snapshot by rev (ACC-34, PLY-02, API-10) and receipt lookup (CMD-09).</summary>
public sealed class SnapshotTests : IAsyncLifetime
{
    private const string Snapshot = "/api/v1/player/snapshot";
    private TestHost _host = null!;
    private HttpClient _player = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        _player = _host.PlayerClient("p1");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task New_player_gets_default_sections_at_rev_zero()
    {
        var response = await _player.PostProtobufAsync(Snapshot, new SnapshotRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadProtobufAsync<SnapshotResponse>();
        body.Sections.Select(s => s.Id).ShouldBe(["counter", "profile"]);
        body.Sections.ShouldAllBe(s => s.Rev == 0);
        ProfileState.Parser.ParseFrom(body.Sections[1].Payload).SessionEpoch.ShouldBe(0);
        body.ConfigVersion.ShouldStartWith("test-1+");
        body.ServerTimeUtcMs.ShouldBe(_host.Clock.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Filtered_snapshot_reflects_committed_rev()
    {
        await _player.PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 4 }, ProtobufHttp.NewRequestId());

        var request = new SnapshotRequest();
        request.Sections.Add("counter");
        var body = await (await _player.PostProtobufAsync(Snapshot, request)).ReadProtobufAsync<SnapshotResponse>();

        var section = body.Sections.ShouldHaveSingleItem();
        section.Id.ShouldBe("counter");
        section.Rev.ShouldBe(1);
        CounterState.Parser.ParseFrom(section.Payload).ShouldBe(new CounterState { Value = 4, Increments = 1 });
    }

    [Fact]
    public async Task Unknown_section_is_400()
    {
        var request = new SnapshotRequest();
        request.Sections.Add("nope");

        var response = await _player.PostProtobufAsync(Snapshot, request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Owner_can_read_receipt_with_outbox_others_cannot()
    {
        var command = await (await _player.PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 2, Note = "n" }, ProtobufHttp.NewRequestId()))
            .ReadProtobufAsync<CommandResponse>();

        var own = await _player.GetAsync($"/api/v1/player/receipts/{command.ReceiptId}");
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await own.ReadProtobufAsync<ReceiptView>();
        view.Operation.ShouldBe("counter.increment");
        view.ResultCode.ShouldBe("OK");
        view.Aggregates.ShouldHaveSingleItem().NewRev.ShouldBe(1);
        view.Outbox.ShouldHaveSingleItem().Kind.ShouldBe("counter_log");
        view.Outbox[0].PayloadJson.ShouldContain("\"note\":\"n\"");

        var other = await _host.PlayerClient("p2").GetAsync($"/api/v1/player/receipts/{command.ReceiptId}");
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.ReadErrorAsync()).Code.ShouldBe("NOT_FOUND");
    }

    [Fact]
    public async Task Identity_comes_from_authentication_not_payload()
    {
        await _host.PlayerClient("victim").PostProtobufAsync("/api/v1/counter/increment", new IncrementRequest { By = 9 }, ProtobufHttp.NewRequestId());

        // The attacker's snapshot only ever reads their own aggregates (API-12).
        var body = await (await _host.PlayerClient("attacker").PostProtobufAsync(Snapshot, new SnapshotRequest())).ReadProtobufAsync<SnapshotResponse>();
        CounterState.Parser.ParseFrom(body.Sections.Single(s => s.Id == "counter").Payload).Value.ShouldBe(0);
    }
}
