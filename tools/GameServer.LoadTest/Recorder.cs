using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GameServer.LoadTest;

/// <summary>One finished call: operation, phase, outcome code (server code, or a client-side code) and latency.</summary>
public sealed record Sample(string Operation, string Phase, double AtSeconds, string Outcome, double LatencyMs);

/// <summary>Thread-safe sample sink with per-window and per-phase summaries.</summary>
public sealed class Recorder
{
    /// <summary>The client had no idle virtual player when an arrival was due (arrival dropped, not a server result).</summary>
    public const string ClientSaturated = "CLIENT_SATURATED";

    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double Elapsed => _clock.Elapsed.TotalSeconds;

    public string Phase { get; set; } = "setup";

    public IReadOnlyCollection<Sample> Samples => _samples;

    /// <summary>
    /// Times <paramref name="call"/> and records its outcome; never throws. Latency is measured from
    /// <paramref name="intendedAt"/> (the scheduled arrival, seconds on this recorder's clock) when given, so time spent
    /// queued in the client (thread pool, busy players) counts — no coordinated omission.
    /// </summary>
    public async Task<string> TimeAsync(string operation, Func<Task<string>> call, double? intendedAt = null)
    {
        var phase = Phase;
        var started = intendedAt is { } at ? TimeSpan.FromSeconds(at) : _clock.Elapsed;
        string outcome;
        try
        {
            outcome = await call();
        }
        catch (Exception ex)
        {
            outcome = Outcomes.Of(ex);
        }

        var latency = (_clock.Elapsed - started).TotalMilliseconds;
        _samples.Enqueue(new Sample(operation, phase, started.TotalSeconds, outcome, latency));
        return outcome;
    }

    /// <summary>
    /// Counts toward latency percentiles: every server answer (any code, including 429/503) and client timeouts (a hung
    /// server is exactly what percentiles must show). Excluded: dropped arrivals and connection failures (NETWORK_ERROR,
    /// e.g. a stopped instance — locally there is no load balancer to take it out).
    /// </summary>
    public static bool Answered(Sample sample) =>
        sample.Outcome != ClientSaturated && !sample.Outcome.StartsWith(Outcomes.NetworkError, StringComparison.Ordinal);

    /// <summary>Operation name of arrivals dropped because every virtual player was busy.</summary>
    public const string DroppedOperation = "dropped";

    public void Drop() => _samples.Enqueue(new Sample(DroppedOperation, Phase, Elapsed, ClientSaturated, 0));

    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];

    /// <summary>One console line per operation for samples that started in [from, to).</summary>
    public string Window(double from, double to)
    {
        var builder = new StringBuilder();
        foreach (var group in _samples.Where(s => s.AtSeconds >= from && s.AtSeconds < to).GroupBy(s => s.Operation).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var served = group.Where(Answered).Select(s => s.LatencyMs).Order().ToArray();
            builder.Append(CultureInfo.InvariantCulture, $"  {group.Key,-8} n={group.Count(),5} p95={Percentile(served, 0.95),7:F1}ms  ");
            builder.AppendLine(Codes(group));
        }

        return builder.ToString();
    }

    public static string Codes(IEnumerable<Sample> samples) =>
        string.Join(' ', samples.GroupBy(s => s.Outcome).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}"));
}

public static class Outcomes
{
    public const string NetworkError = "NETWORK_ERROR";

    public const string ClientTimeout = "CLIENT_TIMEOUT";

    /// <summary>
    /// Outcome code for a failed call: the server's ErrorEnvelope code with HTTP status, or the client error. The SDK
    /// reports both a refused connection and an HttpClient timeout as NETWORK_ERROR; the timeout is told apart by its
    /// inner TaskCanceledException.
    /// </summary>
    public static string Of(Exception ex) => ex switch
    {
        GameServer.Client.GameServerException { InnerException: GameServer.Client.TransportException { InnerException: TaskCanceledException } } => ClientTimeout,
        GameServer.Client.GameServerException g when g.Error.HttpStatus > 0 => $"{g.Error.Code}/{g.Error.HttpStatus}",
        GameServer.Client.GameServerException g => g.Error.Code,
        TaskCanceledException => ClientTimeout,
        _ => ex.GetType().Name,
    };
}
