namespace GameServer.Time;

/// <summary>Server clock. Every module reads time through this interface (TIME-01, PLY-07).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Wall clock. The only type allowed to read the system time.</summary>
public sealed class SystemClock : IClock
{
#pragma warning disable RS0030 // Banned API: this is the single sanctioned call site.
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
#pragma warning restore RS0030
}
