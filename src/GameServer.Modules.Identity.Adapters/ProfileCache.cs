using GameServer.Platform.Aggregates;
using GameServer.Platform.Caching;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>
/// Per-instance cache of <c>profile</c> (epoch, ban, deleted) ≤ 60 s (IDN-08, PERF-08). A missing <c>pa_profile</c> document
/// reads as null and is never cached (review M1): a first login reads the profile before creating it, so a cached absence
/// would turn into <c>ACCOUNT_DELETED</c> for a brand-new player whose login succeeded on another instance. Every session is
/// created after its profile, so a session whose profile is missing on a fresh read belongs to a purged account (invariant
/// "session ⇒ profile exists", research R9).
/// </summary>
public sealed class ProfileCache(PlayerSnapshotReader reader, IClock clock, IOptions<IdentityOptions> options)
{
    private readonly ExpiringCache<string, ProfileState> _cache = new(clock, TimeSpan.FromSeconds(options.Value.CacheSeconds));

    /// <summary>The cached or freshly read profile; null when the player has no profile document.</summary>
    public async ValueTask<ProfileState?> GetAsync(string playerId, CancellationToken cancellationToken) =>
        _cache.TryGet(playerId, out var cached) ? cached : await ReadFreshAsync(playerId, cancellationToken);

    /// <summary>Reads the profile from the store and caches it; null (and nothing cached) when the player has no profile document.</summary>
    public async Task<ProfileState?> ReadFreshAsync(string playerId, CancellationToken cancellationToken)
    {
        var loaded = await reader.ReadOneAsync(playerId, IdentityAggregates.Profile, cancellationToken);
        if (!loaded.Exists)
        {
            _cache.Remove(playerId);
            return null;
        }

        var profile = (ProfileState)loaded.State;
        _cache.Set(playerId, profile);
        return profile;
    }

    public void Set(string playerId, ProfileState profile) => _cache.Set(playerId, profile);
}
