using GameServer.Time;

namespace GameServer.Platform.Testing;

/// <summary>Settable clock for tests and Development time offset (OPS-09).</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = start;

    public FakeClock()
        : this(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }
    }

    public void Set(DateTimeOffset now)
    {
        lock (_lock)
        {
            _now = now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }
    }
}
