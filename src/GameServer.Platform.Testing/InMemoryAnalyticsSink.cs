using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform.Analytics;

namespace GameServer.Platform.Testing;

/// <summary>
/// In-memory analytics sink for tests (FR-002, US5-3): keeps the raw lines (the bytes a file sink writes, without the
/// newline) and their parsed form. Thread-safe.
/// </summary>
public sealed class InMemoryAnalyticsSink : IAnalyticsSink
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public string Name { get; init; } = "memory";

    /// <summary>Test seam: while true, every write throws (sink error path).</summary>
    public bool FailWrites { get; set; }

    /// <summary>Test seam: each write waits this long first (timeout and drain paths).</summary>
    public TimeSpan WriteDelay { get; set; }

    /// <summary>Raw UTF-8 lines in write order.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Parsed records in write order (a fresh copy per call).</summary>
    public IReadOnlyList<JsonObject> Records => Lines.Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();

    public IReadOnlyList<JsonObject> OfEvent(string eventName) =>
        Records.Where(r => r["event"]?.GetValue<string>() == eventName).ToArray();

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
    }

    public async ValueTask WriteAsync(IReadOnlyList<AnalyticsRecord> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (WriteDelay > TimeSpan.Zero)
        {
            await Task.Delay(WriteDelay, cancellationToken).ConfigureAwait(false);
        }

        if (FailWrites)
        {
            throw new IOException("InMemoryAnalyticsSink: FailWrites is set.");
        }

        lock (_lock)
        {
            foreach (var record in batch)
            {
                _lines.Add(Encoding.UTF8.GetString(record.Json.Span));
            }
        }
    }
}
