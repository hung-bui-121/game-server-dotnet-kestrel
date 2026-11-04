using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T044 (SC-004, FR-020..FR-024, FR-026): every reader against each of the four broken-aggregate reasons. No response is
/// <c>INTERNAL</c>; the degrading readers answer 200 with every other section equal to an undamaged twin's; touching and reading
/// commands answer the typed code and write nothing; each failed read is exactly one log entry and one metric increment; a
/// successful login moves the epoch once; "Unhandled exception." is never logged.
/// </summary>
public sealed class BrokenAggregateMatrixTests
{
    private const string Snapshot = "/api/v1/player/snapshot";

    [Theory]
    [InlineData(BrokenReason.VersionAhead)]
    [InlineData(BrokenReason.Unreadable)]
    [InlineData(BrokenReason.UpgradeFailed)]
    [InlineData(BrokenReason.MapperFailed)]
    public async Task Every_reader_handles_a_broken_aggregate_without_internal_and_reports_each_failed_read_once(BrokenReason reason)
    {
        await using var host = await BrokenAggregateHost.StartAsync(
            settings: s => s["Identity:LoginPerLocalIdPerMinute"] = "100",
            modules: b => b.AddModule<GmModule>());
        using var failedReads = new FailedReads(host);
        var aggregate = BrokenDocuments.AggregateOf(reason);
        var mapper = reason == BrokenReason.MapperFailed;
        var code = reason == BrokenReason.VersionAhead ? "AGGREGATE_VERSION_AHEAD" : "AGGREGATE_UNREADABLE";
        var marker = mapper ? StartingStateAggregates.MapperFailMarker : string.Empty;
        var touching = BrokenAggregateHost.TouchingRoute(aggregate).Template;

        // Two players with the same history: one committed command on the aggregate, then one is damaged and one gets the twin.
        var broken = await PlayerAsync(host);
        var twin = await PlayerAsync(host);
        var replayId = ProtobufHttp.NewRequestId();
        await CommandOkAsync(broken.Bearer, touching, BrokenAggregateHost.Request(1), replayId);
        await CommandOkAsync(twin.Bearer, touching, BrokenAggregateHost.Request(1), ProtobufHttp.NewRequestId());
        BrokenDocuments.Seed(host.Store, reason, broken.PlayerId);
        BrokenDocuments.Seed(host.Store, reason, twin.PlayerId, damaged: false);
        var expected = new UnavailableSection { Id = aggregate, Code = code, CodeNumber = reason == BrokenReason.VersionAhead ? 24 : 25 };

        // Snapshot, all sections and an explicit list: 200, the section listed, the others equal to the twin's.
        foreach (var sections in new[] { Array.Empty<string>(), [aggregate, "wallet"] })
        {
            var reads = failedReads.Count;
            var damaged = await SnapshotAsync(broken.Bearer, sections);
            failedReads.Count.ShouldBe(reads + 1);
            var undamaged = await SnapshotAsync(twin.Bearer, sections);
            damaged.UnavailableSections.ShouldBe([expected]);
            damaged.Sections.ShouldBe(undamaged.Sections.Where(s => s.Id != aggregate).ToArray());
        }

        // Login: 200, session, epoch + 1 exactly once, the section listed.
        var beforeLogin = failedReads.Count;
        var login = await host.Client().LoginAsync(broken.LocalId, broken.Secret);
        failedReads.Count.ShouldBe(beforeLogin + 1);
        var twinLogin = await host.Client().LoginAsync(twin.LocalId, twin.Secret);
        Epoch(host, broken.PlayerId).ShouldBe(2);
        login.UnavailableSections.ShouldBe([expected]);
        login.Sections.ShouldBe(twinLogin.Sections.Where(s => s.Id != aggregate).ToArray());
        var bearer = host.BearerClient(login.SessionToken);

        // A touching command: the typed code and no write (a mapper failure commits and lists the section, T042).
        var receipts = Receipts(host, broken.PlayerId);
        var stored = host.Store.Peek(BrokenDocuments.KeyOf(reason, broken.PlayerId))!.ToJsonString();
        var reads0 = failedReads.Count;
        var touched = await bearer.PostProtobufAsync(touching, BrokenAggregateHost.Request(2, marker), ProtobufHttp.NewRequestId());
        failedReads.Count.ShouldBe(reads0 + 1);
        if (mapper)
        {
            touched.StatusCode.ShouldBe(HttpStatusCode.OK);
            var response = await touched.ReadProtobufAsync<CommandResponse>();
            response.Code.ShouldBe("OK");
            response.UnavailableSections.ShouldBe([expected]);
            Receipts(host, broken.PlayerId).ShouldBe(receipts + 1);
        }
        else
        {
            await ShouldBeTypedAsync(touched, code, aggregate);
            host.Store.Peek(BrokenDocuments.KeyOf(reason, broken.PlayerId))!.ToJsonString().ShouldBe(stored);
            Receipts(host, broken.PlayerId).ShouldBe(receipts);
        }

        // A command that only reads it: the typed code and no write (a readable state reads fine).
        receipts = Receipts(host, broken.PlayerId);
        var reads1 = failedReads.Count;
        var read = await bearer.PostProtobufAsync(BrokenAggregateHost.ReadingRoute(aggregate).Template, BrokenAggregateHost.Request(0), ProtobufHttp.NewRequestId());
        if (mapper)
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await read.ReadProtobufAsync<CommandResponse>()).Code.ShouldBe("OK");
            failedReads.Count.ShouldBe(reads1);
        }
        else
        {
            await ShouldBeTypedAsync(read, code, aggregate);
            failedReads.Count.ShouldBe(reads1 + 1);
            Receipts(host, broken.PlayerId).ShouldBe(receipts);
        }

        // A command that neither touches nor reads it commits and reads nothing broken.
        var reads2 = failedReads.Count;
        var untouched = await CommandOkAsync(bearer, BrokenAggregateHost.UntouchedRoute.Template, BrokenAggregateHost.Request(0), ProtobufHttp.NewRequestId());
        untouched.UnavailableSections.ShouldBeEmpty();
        failedReads.Count.ShouldBe(reads2);

        // The replay of the command committed before the damage: 200, same result, the section listed.
        var reads3 = failedReads.Count;
        var replay = await CommandOkAsync(bearer, touching, BrokenAggregateHost.Request(1), replayId);
        replay.Replayed.ShouldBeTrue();
        replay.UnavailableSections.ShouldBe([expected]);
        replay.Updates.ShouldBeEmpty();
        failedReads.Count.ShouldBe(reads3 + 1);

        // GM view: listed in unreadable (a state only its wire mapper refuses is readable JSON for GM).
        var reads4 = failedReads.Count;
        var gm = await host.GmClient("admin", GmPermissions.PlayerRead).GetAsync($"/gm/v1/players/{broken.PlayerId}");
        gm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = JsonNode.Parse(await gm.Content.ReadAsStringAsync())!;
        view["unreadable"]!.AsArray().Select(u => u!["id"]!.GetValue<string>()).ShouldBe(mapper ? [] : [aggregate]);
        failedReads.Count.ShouldBe(reads4 + (mapper ? 0 : 1));

        // A service read of the one aggregate: the typed exception.
        var reads5 = failedReads.Count;
        var reader = host.Services.GetRequiredService<PlayerSnapshotReader>();
        if (mapper)
        {
            (await reader.ReadOneAsync(broken.PlayerId, aggregate, CancellationToken.None)).Exists.ShouldBeTrue();
            failedReads.Count.ShouldBe(reads5);
        }
        else
        {
            (await Should.ThrowAsync<GameServerException>(() => reader.ReadOneAsync(broken.PlayerId, aggregate, CancellationToken.None))).Code.Name.ShouldBe(code);
            failedReads.Count.ShouldBe(reads5 + 1);
        }

        failedReads.Metric.ShouldBe(failedReads.Count, "one metric increment per failed read");
        host.LogEntries.ShouldNotContain(e => e.Message == "Unhandled exception.");
        host.LogEntries.ShouldNotContain(e => e.Message.Contains("INTERNAL", StringComparison.Ordinal));
    }

    private static async Task<(string PlayerId, string LocalId, byte[] Secret, HttpClient Bearer)> PlayerAsync(TestHost host)
    {
        var (localId, secret) = (IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var login = await host.Client().LoginAsync(localId, secret);
        return (login.PlayerId, localId, secret, host.BearerClient(login.SessionToken));
    }

    private static async Task<SnapshotResponse> SnapshotAsync(HttpClient player, string[] sections)
    {
        var request = new SnapshotRequest();
        request.Sections.Add(sections);
        var response = await player.PostProtobufAsync(Snapshot, request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadProtobufAsync<SnapshotResponse>();
    }

    private static async Task<CommandResponse> CommandOkAsync(HttpClient player, string route, Sample.Counter.Protocol.V1.IncrementRequest request, string requestId)
    {
        var response = await player.PostProtobufAsync(route, request, requestId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.ReadProtobufAsync<CommandResponse>();
        body.Code.ShouldBe("OK");
        return body;
    }

    private static async Task ShouldBeTypedAsync(HttpResponseMessage response, string code, string aggregate)
    {
        response.StatusCode.ShouldBe(code == "AGGREGATE_VERSION_AHEAD" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError);
        var error = await response.ReadErrorAsync();
        error.Code.ShouldBe(code);
        error.Details.ShouldBe(new Dictionary<string, string> { ["aggregate"] = aggregate });
    }

    private static long Epoch(TestHost host, string playerId) =>
        host.Store.Peek(new DocumentKey("pa_profile", playerId))!["state"]!["sessionEpoch"]!.GetValue<long>();

    private static int Receipts(TestHost host, string playerId) =>
        host.Store.All(GameServer.Platform.Commands.ReceiptDocument.Collection).Count(d => d.Content["playerId"]?.GetValue<string>() == playerId);

    /// <summary>Failed reads of one host: its read-failure log entries and its <c>aggregate_read_failures_total</c> increments.</summary>
    private sealed class FailedReads : IDisposable
    {
        private readonly TestHost _host;
        private readonly MeterListener _listener = new();
        private long _metric;

        public FailedReads(TestHost host)
        {
            _host = host;
            var meter = host.Services.GetRequiredService<PlatformMetrics>().Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "aggregate_read_failures_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _metric, value));
            _listener.Start();
        }

        public int Count => _host.LogEntries.Count(e => e.Category == AggregateReadFailures.LogCategory);

        public long Metric => Interlocked.Read(ref _metric);

        public void Dispose() => _listener.Dispose();
    }
}
