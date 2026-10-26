using System.Collections.Concurrent;
using GameServer.Time;

namespace GameServer.Platform.Caching;

/// <summary>
/// Per-instance cache with a fixed TTL measured on <see cref="IClock"/> (PERF-08: session, profile, permission,
/// gate caches ≤ 60 s). Expired entries are evicted on access and on a size threshold.
/// </summary>
public sealed class ExpiringCache<TKey, TValue>(IClock clock, TimeSpan ttl, int sweepThreshold = 100_000)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();

    public TimeSpan Ttl { get; } = ttl;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > clock.UtcNow)
        {
            value = entry.Value;
            return true;
        }

        _entries.TryRemove(key, out _);
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (Ttl <= TimeSpan.Zero)
        {
            return;
        }

        if (_entries.Count >= sweepThreshold)
        {
            Sweep();
        }

        _entries[key] = new Entry(value, clock.UtcNow + Ttl);
    }

    public async ValueTask<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        if (TryGet(key, out var cached))
        {
            return cached;
        }

        var value = await factory(key);
        Set(key, value);
        return value;
    }

    public void Remove(TKey key) => _entries.TryRemove(key, out _);

    private void Sweep()
    {
        var now = clock.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(TValue Value, DateTimeOffset ExpiresAt);
}
