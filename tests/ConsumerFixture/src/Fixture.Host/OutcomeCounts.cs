using System.Collections.Concurrent;

namespace Fixture.Host;

/// <summary>
/// Request outcomes counted by result code: a game's own per-request behaviour built on <c>HttpContext.RequestOutcome()</c> from a
/// middleware placed before <c>UseGameServer</c> (FR-084), never by reading response bodies.
/// </summary>
public sealed class OutcomeCounts
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public int this[string resultCode] => _counts.GetValueOrDefault(resultCode);

    public void Add(string resultCode) => _counts.AddOrUpdate(resultCode, 1, static (_, count) => count + 1);
}
