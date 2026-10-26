using GameServer.Platform.Aggregates;
using GameServer.Platform.Caching;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>Per-instance cache of <c>profile</c> (epoch, ban, deleted) ≤ 60 s (IDN-08, PERF-08).</summary>
public sealed class ProfileCache(PlayerSnapshotReader reader, IClock clock, IOptions<IdentityOptions> options)
{
    private readonly ExpiringCache<string, ProfileState> _cache = new(clock, TimeSpan.FromSeconds(options.Value.CacheSeconds));

    public async ValueTask<ProfileState> GetAsync(string playerId, CancellationToken cancellationToken) =>
        await _cache.GetOrAddAsync(playerId, id => ReadFreshAsync(id, cancellationToken));

    public async Task<ProfileState> ReadFreshAsync(string playerId, CancellationToken cancellationToken)
    {
        var loaded = await reader.ReadOneAsync(playerId, IdentityAggregates.Profile, cancellationToken);
        var profile = (ProfileState)loaded.State;
        _cache.Set(playerId, profile);
        return profile;
    }

    public void Set(string playerId, ProfileState profile) => _cache.Set(playerId, profile);
}
