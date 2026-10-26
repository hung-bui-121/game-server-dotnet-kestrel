using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform;
using GameServer.Platform.Caching;
using GameServer.Platform.Persistence;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Gm;

/// <summary>Reads and writes <c>gm_staff</c>; cached per instance for at most <see cref="GmOptions.CacheSeconds"/>.</summary>
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

        var document = await store.GetAsync(Key(staffId), cancellationToken);
        var record = document?.Content.Deserialize<StaffRecord>(GameServerJson.Options);
        if (record is not null)
        {
            // Unknown ids are not cached: login attempts with random ids must not grow the cache.
            _cache.Set(staffId, record);
        }

        return record;
    }

    /// <summary>Creates or replaces the staff document (bootstrap and ops scripts).</summary>
    public async Task SaveAsync(StaffRecord record, CancellationToken cancellationToken)
    {
        var content = (JsonObject)JsonSerializer.SerializeToNode(record, GameServerJson.Options)!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await store.GetAsync(Key(record.StaffId), cancellationToken);
            var saved = existing is null
                ? await store.InsertAsync(Key(record.StaffId), content, null, cancellationToken)
                : await store.ReplaceAsync(existing, content, null, cancellationToken);
            if (saved)
            {
                _cache.Remove(record.StaffId);
                return;
            }
        }

        throw new InvalidOperationException($"Staff '{record.StaffId}' changed concurrently; save not applied.");
    }

    private static DocumentKey Key(string staffId) => new(Collection, staffId);
}
