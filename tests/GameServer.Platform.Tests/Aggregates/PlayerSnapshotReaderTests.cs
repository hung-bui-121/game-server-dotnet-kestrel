using GameServer.Errors;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Aggregates;

/// <summary>
/// Slice 016 T035 (FR-021, FR-022): the degrading reads list a broken aggregate instead of failing, in one store read; the
/// throwing reads keep their signatures and throw the typed failure.
/// </summary>
public sealed class PlayerSnapshotReaderTests : IDisposable
{
    private const string PlayerId = "p_snapshot";

    private readonly RunnerHarness _h = new(null, ReadProbeAggregates.Module());
    private readonly RecordingStore _store;
    private readonly PlayerSnapshotReader _reader;

    public PlayerSnapshotReaderTests()
    {
        _store = new RecordingStore(_h.Store);
        _reader = new PlayerSnapshotReader(_h.Registry, _store, _h.Serializer);
        foreach (var id in new[] { ReadProbeAggregates.A, ReadProbeAggregates.B, ReadProbeAggregates.C })
        {
            Seed(id, broken: false);
        }
    }

    [Fact]
    public async Task One_broken_aggregate_of_three_is_listed_and_the_others_are_read()
    {
        Seed(ReadProbeAggregates.B, broken: true);

        var snapshot = await _reader.ReadAvailableAsync(PlayerId, [ReadProbeAggregates.A, ReadProbeAggregates.B, ReadProbeAggregates.C], CancellationToken.None);

        snapshot.Sections.Select(s => s.Descriptor.AggregateId).ShouldBe([ReadProbeAggregates.A, ReadProbeAggregates.C]);
        snapshot.Sections.ShouldAllBe(s => s.Rev == 4);
        var failure = snapshot.Unavailable.ShouldHaveSingleItem();
        failure.AggregateId.ShouldBe(ReadProbeAggregates.B);
        failure.Reason.ShouldBe(AggregateReadFailureReason.Unreadable);
        _store.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task Every_aggregate_broken_gives_no_section_and_lists_all_ordered_by_id()
    {
        foreach (var id in new[] { ReadProbeAggregates.C, ReadProbeAggregates.A, ReadProbeAggregates.B })
        {
            Seed(id, broken: true);
        }

        var snapshot = await _reader.ReadAvailableAsync(PlayerId, [ReadProbeAggregates.C, ReadProbeAggregates.A, ReadProbeAggregates.B], CancellationToken.None);

        snapshot.Sections.ShouldBeEmpty();
        snapshot.Unavailable.Select(f => f.AggregateId).ShouldBe([ReadProbeAggregates.A, ReadProbeAggregates.B, ReadProbeAggregates.C]);
    }

    [Fact]
    public async Task All_client_visible_sections_list_the_broken_one()
    {
        Seed(ReadProbeAggregates.A, broken: true);

        var snapshot = await _reader.ReadAvailableAsync(PlayerId, null, CancellationToken.None);

        snapshot.Unavailable.Select(f => f.AggregateId).ShouldBe([ReadProbeAggregates.A]);
        snapshot.Sections.Select(s => s.Descriptor.AggregateId).ShouldNotContain(ReadProbeAggregates.A);
        snapshot.Sections.Select(s => s.Descriptor.AggregateId).ShouldContain(ReadProbeAggregates.B);
        _store.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task The_gm_read_lists_hidden_broken_aggregates_too()
    {
        _h.Store.Seed(new DocumentKey("pa_" + ReadProbeAggregates.Gap, PlayerId), ReadProbeAggregates.Document(ReadProbeAggregates.Gap, PlayerId, d => d["schemaVersion"] = 1).Content);

        var snapshot = await _reader.ReadAllAvailableAsync(PlayerId, CancellationToken.None);

        snapshot.Unavailable.ShouldHaveSingleItem().AggregateId.ShouldBe(ReadProbeAggregates.Gap);
        snapshot.Sections.Count.ShouldBe(_h.Registry.Aggregates.Count - 1);
        _store.Reads.ShouldBe(1);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData(ProbeAggregates.Hidden)]
    public async Task An_unknown_or_hidden_id_is_still_bad_request(string id)
    {
        var ex = await Should.ThrowAsync<GameServerException>(() => _reader.ReadAvailableAsync(PlayerId, [ReadProbeAggregates.A, id], CancellationToken.None));

        ex.Code.ShouldBe(CoreErrorCodes.BadRequest);
        _store.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task The_throwing_reads_throw_the_typed_failure()
    {
        Seed(ReadProbeAggregates.B, broken: true, damage: d => d["schemaVersion"] = 9);

        foreach (var read in new Func<Task>[]
                 {
                     () => _reader.ReadAsync(PlayerId, null, CancellationToken.None),
                     () => _reader.ReadAllAsync(PlayerId, CancellationToken.None),
                     () => _reader.ReadOneAsync(PlayerId, ReadProbeAggregates.B, CancellationToken.None),
                 })
        {
            var ex = await Should.ThrowAsync<GameServerException>(read);
            ex.Code.ShouldBe(CoreErrorCodes.AggregateVersionAhead);
            ex.Details!["aggregate"].ShouldBe(ReadProbeAggregates.B);
        }
    }

    public void Dispose() => _h.Dispose();

    private void Seed(string aggregateId, bool broken, Action<System.Text.Json.Nodes.JsonObject>? damage = null) =>
        _h.Store.Seed(
            new DocumentKey("pa_" + aggregateId, PlayerId),
            ReadProbeAggregates.Document(aggregateId, PlayerId, broken ? damage ?? (d => d["state"] = null) : null).Content);
}
