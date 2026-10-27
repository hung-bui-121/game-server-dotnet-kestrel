using System.Text.Json;
using GameServer.Platform;
using GameServer.Platform.Caching;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>
/// Reads <c>gm_staff</c>, cached per instance for at most <see cref="GmOptions.CacheSeconds"/> (SEC-10: a change made on
/// another instance takes effect within that window). Writes go through <see cref="StaffAdminService"/> only.
/// </summary>
public sealed class StaffDirectory(IKeyValueStore store, IClock clock, IOptions<GmOptions> options)
{
    public const string Collection = "gm_staff";

    private readonly ExpiringCache<string, StaffRecord> _cache = new(clock, TimeSpan.FromSeconds(options.Value.CacheSeconds));

    public async Task<StaffRecord?> GetAsync(string staffId, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(staffId, out var cached))
        {
            return cached;
        }

        var record = Parse(await store.GetAsync(Key(staffId), cancellationToken));
        if (record is not null)
        {
            // Unknown ids are not cached: login attempts with random ids must not grow the cache.
            _cache.Set(staffId, record);
        }

        return record;
    }

    /// <summary>Drops the cached copy on this instance after a change (other instances follow within the TTL).</summary>
    public void Invalidate(string staffId) => _cache.Remove(staffId);

    public static DocumentKey Key(string staffId) => new(Collection, staffId);

    public static StaffRecord? Parse(StoredDocument? document) => document?.Content.Deserialize<StaffRecord>(GameServerJson.Options);
}
