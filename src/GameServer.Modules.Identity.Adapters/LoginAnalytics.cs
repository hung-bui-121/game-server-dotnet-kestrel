using System.Collections.Concurrent;
using GameServer.Analytics;
using GameServer.Platform.Analytics;
using GameServer.Time;
using Microsoft.Extensions.Options;

namespace GameServer.Modules.Identity;

/// <summary>
/// Login analytics (owner task 3, R11): one <c>login</c> record per successful login (<c>source=login</c>) and one
/// <c>source=resume</c> record per player, deployment-local day and instance when a stored session is used. Best effort
/// through the analytics queue (loss accepted and counted, JOB-02). Inactive analytics: both calls return at once.
/// </summary>
public sealed class LoginAnalytics
{
    public const string LoginSource = "login";
    public const string ResumeSource = "resume";

    private readonly IAnalyticsEmitter _emitter;
    private readonly AnalyticsTimeZone _zone;
    private readonly IClock _clock;
    private readonly DailyPlayerSet? _seen;

    public LoginAnalytics(IAnalyticsEmitter emitter, AnalyticsTimeZone zone, IClock clock, IOptions<AnalyticsOptions> options, IdentityMetrics metrics)
    {
        _emitter = emitter;
        _zone = zone;
        _clock = clock;
        _seen = emitter.IsActive ? new DailyPlayerSet(options.Value.ResumeTrackerCapacity, metrics.ResumeTrackerReset) : null;
    }

    /// <summary><c>("login", 1, "activeTime")</c>.</summary>
    public static AnalyticsEventType Login { get; } = new("login", 1, "activeTime");

    /// <summary>Players remembered for the current local day (tests).</summary>
    internal DailyPlayerSet? Seen => _seen;

    /// <summary>End of a successful login (FR-015): emits <c>source=login</c> and marks the player seen today.</summary>
    public void LoggedIn(string playerId, string provider, bool createdPlayer)
    {
        if (_seen is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        _seen.TryMark(playerId, _zone.LocalDate(now));
        _emitter.TryEmit(Login, playerId, now, new LoginFields(LoginSource, provider, createdPlayer));
    }

    /// <summary>
    /// The PlayerBearer scheme authenticated a request (FR-017): emits <c>source=resume</c> the first time the player is seen
    /// on the current local day on this instance.
    /// </summary>
    public void Authenticated(string playerId)
    {
        if (_seen is null)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (_seen.TryMark(playerId, _zone.LocalDate(now)))
        {
            _emitter.TryEmit(Login, playerId, now, new LoginFields(ResumeSource, null, false));
        }
    }
}

/// <summary>Fields of <c>login</c> v1 (data-model §3.2); <c>provider</c> only for <c>source=login</c>.</summary>
public sealed record LoginFields(string Source, string? Provider, bool IsNewUser);

/// <summary>
/// Players seen on one local day (FR-018). A newer day atomically replaces the set; reaching the capacity clears it (each
/// clear may cause one extra resume per active player, counted by <c>login_resume_tracker_resets_total</c>).
/// Hot path (every PlayerBearer request): an already-seen player costs one lock-free lookup; the size is a counter of the
/// day, never <see cref="ConcurrentDictionary{TKey,TValue}.Count"/> (which takes every bucket lock).
/// </summary>
internal sealed class DailyPlayerSet(int capacity, Action onReset)
{
    private Day _current = new(DateOnly.MinValue);

    /// <summary>Players added to the current day's set (may pass the capacity by the number of concurrent adds).</summary>
    public int Count => Volatile.Read(ref _current).Count;

    /// <summary>True when the player was not yet seen on <paramref name="localDay"/> (and is now).</summary>
    public bool TryMark(string playerId, DateOnly localDay)
    {
        while (true)
        {
            var current = Volatile.Read(ref _current);
            if (localDay == current.Date && current.Players.ContainsKey(playerId))
            {
                return false;
            }

            if (localDay < current.Date)
            {
                // A clock read older than the current day: that day is over, never resume it.
                return false;
            }

            if (localDay > current.Date)
            {
                Interlocked.CompareExchange(ref _current, new Day(localDay), current);
                continue;
            }

            if (current.Count >= capacity)
            {
                if (Interlocked.CompareExchange(ref _current, new Day(localDay), current) == current)
                {
                    onReset();
                }

                continue;
            }

            return current.TryAdd(playerId);
        }
    }

    private sealed class Day(DateOnly date)
    {
        private int _count;

        public DateOnly Date { get; } = date;

        public ConcurrentDictionary<string, byte> Players { get; } = new(StringComparer.Ordinal);

        public int Count => Volatile.Read(ref _count);

        public bool TryAdd(string playerId)
        {
            if (!Players.TryAdd(playerId, 0))
            {
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }
    }
}
