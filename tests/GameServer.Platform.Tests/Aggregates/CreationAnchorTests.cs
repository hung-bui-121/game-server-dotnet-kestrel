using System.Text.Json.Nodes;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Aggregates;

/// <summary>
/// Slice 010 research R9: an aggregate is only created while its creation anchor (the identity mapping) exists for the
/// same player, and the anchor is rewritten in the creating transaction so a concurrent removal conflicts.
/// </summary>
public sealed class CreationAnchorTests : IDisposable
{
    private const string PlayerId = "p_anchor";
    private static readonly DocumentKey Anchor = new("probe_claims", "guest:anchor");
    private static readonly DocumentKey Profile = new(IdentityAggregates.ProfileDescriptor.Collection, PlayerId);

    private readonly RunnerHarness _h = new();
    private readonly PlayerAggregateWriter _writer;

    public CreationAnchorTests() =>
        _writer = new PlayerAggregateWriter(_h.Registry, _h.Store, _h.Serializer, _h.Clock, _h.Options, _h.Metrics);

    public void Dispose() => _h.Dispose();

    private Task<(ProfileState State, long Rev)> LoginAsync(DocumentKey? anchor) =>
        _writer.UpdateAsync<ProfileState>(PlayerId, IdentityAggregates.Profile, p => p with { SessionEpoch = p.SessionEpoch + 1 }, anchor, CancellationToken.None);

    private static JsonObject AnchorOf(string playerId) => new() { ["mappingKey"] = Anchor.Key, ["playerId"] = playerId, ["linkedAtUtc"] = "2026-10-30T00:00:00.000Z" };

    private async Task<long> VersionAsync(DocumentKey key) => (long)(await _h.Store.GetAsync(key, CancellationToken.None))!.Handle!;

    [Fact]
    public async Task Missing_aggregate_with_missing_anchor_throws_and_writes_nothing()
    {
        var ex = await Should.ThrowAsync<AggregateAnchorMissingException>(() => LoginAsync(Anchor));

        ex.Anchor.ShouldBe(Anchor);
        _h.Store.Peek(Profile).ShouldBeNull();
        _h.Store.Peek(Anchor).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_aggregate_with_an_anchor_of_another_player_throws_and_leaves_the_anchor()
    {
        _h.Store.Seed(Anchor, AnchorOf("p_other"));
        var version = await VersionAsync(Anchor);

        await Should.ThrowAsync<AggregateAnchorMissingException>(() => LoginAsync(Anchor));

        _h.Store.Peek(Profile).ShouldBeNull();
        (await VersionAsync(Anchor)).ShouldBe(version);
    }

    [Fact]
    public async Task Present_anchor_is_rewritten_unchanged_and_the_aggregate_is_created()
    {
        _h.Store.Seed(Anchor, AnchorOf(PlayerId));
        var version = await VersionAsync(Anchor);

        var (state, rev) = await LoginAsync(Anchor);

        state.SessionEpoch.ShouldBe(1);
        rev.ShouldBe(1);
        _h.Store.Peek(Profile).ShouldNotBeNull();
        JsonNode.DeepEquals(_h.Store.Peek(Anchor), AnchorOf(PlayerId)).ShouldBeTrue();
        (await VersionAsync(Anchor)).ShouldBeGreaterThan(version);
    }

    [Fact]
    public async Task Existing_aggregate_does_not_touch_the_anchor()
    {
        _h.Store.Seed(Anchor, AnchorOf(PlayerId));
        await LoginAsync(Anchor);
        var version = await VersionAsync(Anchor);

        var (state, _) = await LoginAsync(Anchor);
        var untouched = await VersionAsync(Anchor);
        await ((IKeyValueStore)_h.Store).RemoveAsync(Anchor, CancellationToken.None);
        var (again, _) = await LoginAsync(Anchor);

        state.SessionEpoch.ShouldBe(2);
        untouched.ShouldBe(version);
        again.SessionEpoch.ShouldBe(3);
    }

    [Fact]
    public async Task Without_an_anchor_the_old_overload_creates_as_before()
    {
        var (state, rev) = await _writer.UpdateAsync<ProfileState>(PlayerId, IdentityAggregates.Profile, p => p with { DisplayName = "Anchorless" }, CancellationToken.None);

        state.DisplayName.ShouldBe("Anchorless");
        rev.ShouldBe(1);
    }
}
