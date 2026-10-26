using System.Collections.Concurrent;
using GameServer.Time;

namespace GameServer.Platform.Limits;

/// <summary>
/// Per-instance fixed-window counter on <see cref="IClock"/> (SEC-13). Budget across instances = limit × instances.
/// </summary>
public sealed class FixedWindowCounter(IClock clock, TimeSpan window, int limit)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public int Limit { get; } = limit;

    /// <summary>True when the key already used the whole budget of the current window.</summary>
    public bool IsOver(string key) => Count(key) >= Limit;

    /// <summary>Adds one hit and returns the count in the current window.</summary>
    public int Increment(string key)
    {
        var now = clock.UtcNow;
        var updated = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, current) => now - current.Start >= window ? new Window(now, 1) : current with { Count = current.Count + 1 });
        if (_windows.Count > 100_000)
        {
            Sweep(now);
        }

        return updated.Count;
    }

    /// <summary>Seconds until the window of <paramref name="key"/> resets (for Retry-After).</summary>
    public int RetryAfterSeconds(string key)
    {
        if (!_windows.TryGetValue(key, out var current))
        {
            return 1;
        }

        var remaining = window - (clock.UtcNow - current.Start);
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private int Count(string key)
    {
        if (!_windows.TryGetValue(key, out var current))
        {
            return 0;
        }

        return clock.UtcNow - current.Start >= window ? 0 : current.Count;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, value) in _windows)
        {
            if (now - value.Start >= window)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
