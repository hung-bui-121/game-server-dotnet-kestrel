using GameServer.Platform.Aggregates;
using GameServer.Platform.Commands;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Aggregates;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 016 T037 (FR-022, FR-024, FR-026, research R7): the one mapping site leaves out and lists a section whose mapper throws
/// (one report), merges the loader's failures ordered by id, and never sends a section in both lists or with an empty payload.
/// </summary>
public sealed class SectionWriterTests : IDisposable
{
    private const string PlayerId = "p_sections";

    private readonly PlatformMetrics _metrics = new();
    private readonly CapturedLogSink _sink = new();
    private readonly ILoggerFactory _loggers;
    private readonly AggregateDocumentSerializer _serializer;
    private readonly AggregateWireMapperRegistry _mappers;
    private readonly SectionWriter _writer;

    public SectionWriterTests()
    {
        _loggers = LoggerFactory.Create(b => b.AddProvider(_sink));
        var failures = new AggregateReadFailures(_metrics, _loggers);
        _serializer = new AggregateDocumentSerializer(new GameServerOptions { GameId = "test", EnvironmentId = "test" }, _metrics, failures);
        _mappers = new AggregateWireMapperRegistry(
        [
            new AggregateWireMapper<StarterState, Wire.CounterState>(StartingStateAggregates.Starter, StartingStateModule.StarterToWire),
            new AggregateWireMapper<BrokenProbeState, Wire.CounterState>(StartingStateAggregates.BrokenProbe, s => new Wire.CounterState { Value = s.Value }),
        ]);
        _writer = new SectionWriter(_mappers, failures);
    }

    [Fact]
    public void A_throwing_mapper_leaves_its_section_out_and_lists_it_once()
    {
        var starter = Load(BrokenReason.MapperFailed, damaged: true);
        var probe = Load(BrokenReason.UpgradeFailed, damaged: false);
        var sections = new RepeatedField<Section>();
        var unavailable = new RepeatedField<UnavailableSection>();

        _writer.WriteSections(PlayerId, [starter, probe], [], sections, unavailable);

        sections.Select(s => s.Id).ShouldBe([StartingStateAggregates.BrokenProbe]);
        sections[0].Payload.ShouldBe(_mappers.ToPayload(probe.Descriptor.AggregateId, probe.State));
        sections[0].Rev.ShouldBe(BrokenDocuments.Rev);
        unavailable.ShouldBe([new UnavailableSection { Id = StartingStateAggregates.Starter, Code = "AGGREGATE_UNREADABLE", CodeNumber = 25 }]);
        var line = _sink.Entries.Where(e => e.Category == AggregateReadFailures.LogCategory).ShouldHaveSingleItem();
        line.Message.ShouldBe($"Aggregate starter of player {PlayerId} is unavailable: mapper_failed (System.InvalidOperationException).");
    }

    [Fact]
    public void Loader_failures_and_mapper_failures_are_merged_and_ordered_by_id()
    {
        var sections = new RepeatedField<Section>();
        var unavailable = new RepeatedField<UnavailableSection>();
        var zeta = new AggregateReadFailure("zeta", PlayerId, AggregateReadFailureReason.VersionAhead, string.Empty, 2, 1);
        var alpha = new AggregateReadFailure("alpha", PlayerId, AggregateReadFailureReason.Unreadable, "System.Text.Json.JsonException", 1, 1);

        _writer.WriteSections(PlayerId, [Load(BrokenReason.MapperFailed, damaged: true)], [zeta, alpha], sections, unavailable);

        sections.ShouldBeEmpty();
        unavailable.Select(u => (u.Id, u.Code)).ShouldBe([("alpha", "AGGREGATE_UNREADABLE"), ("starter", "AGGREGATE_UNREADABLE"), ("zeta", "AGGREGATE_VERSION_AHEAD")]);
        _sink.Entries.Count(e => e.Category == AggregateReadFailures.LogCategory).ShouldBe(1, "the loader's failures were reported where they happened");
    }

    [Fact]
    public void A_section_listed_by_the_loader_is_never_written_too()
    {
        var starter = Load(BrokenReason.MapperFailed, damaged: false);
        var sections = new RepeatedField<Section>();
        var unavailable = new RepeatedField<UnavailableSection>();

        _writer.WriteSections(
            PlayerId,
            [starter],
            [new AggregateReadFailure(StartingStateAggregates.Starter, PlayerId, AggregateReadFailureReason.Unreadable, "X", 1, 1)],
            sections,
            unavailable);

        sections.ShouldBeEmpty();
        unavailable.Select(u => u.Id).ShouldBe([StartingStateAggregates.Starter]);
    }

    [Fact]
    public void Updates_follow_the_same_rule_after_a_commit()
    {
        var good = new AggregateChange(StartingStateAggregates.BrokenProbe, 1, 2, new BrokenProbeState { Value = 4 });
        var bad = new AggregateChange(StartingStateAggregates.Starter, 1, 2, new StarterState { Padding = StartingStateAggregates.MapperFailMarker });
        var updates = new RepeatedField<SectionUpdate>();
        var unavailable = new RepeatedField<UnavailableSection>();

        _writer.WriteUpdates(PlayerId, [bad, good], [], updates, unavailable);

        var update = updates.ShouldHaveSingleItem();
        update.Id.ShouldBe(StartingStateAggregates.BrokenProbe);
        (update.BaseRev, update.NewRev, update.Full).ShouldBe((1L, 2L, true));
        update.Payload.ShouldBe(_mappers.ToPayload(good.AggregateId, good.State));
        unavailable.Select(u => u.Id).ShouldBe([StartingStateAggregates.Starter]);
    }

    public void Dispose()
    {
        _loggers.Dispose();
        _metrics.Dispose();
    }

    private LoadedAggregate Load(BrokenReason reason, bool damaged)
    {
        var descriptor = BrokenDocuments.AggregateOf(reason) == StartingStateAggregates.Starter
            ? (GameServer.Aggregates.PlayerAggregateDescriptor)StartingStateAggregates.StarterDescriptor
            : StartingStateAggregates.BrokenProbeDescriptor;
        var document = new StoredDocument(BrokenDocuments.KeyOf(reason, PlayerId), BrokenDocuments.Document(reason, PlayerId, damaged), handle: null);
        return _serializer.Load(descriptor, PlayerId, document);
    }
}
