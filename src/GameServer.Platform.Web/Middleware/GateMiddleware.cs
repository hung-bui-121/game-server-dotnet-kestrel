using System.Globalization;
using GameServer.Errors;
using GameServer.Platform.Gate;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Routing;
using GameServer.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Headers every Player request carries (GATE-02), sent by the client SDK; compared with <c>minClientVersion</c> of
/// the platform in the <c>gate</c> document.
/// </summary>
public static class GateHeaders
{
    public const string ClientVersion = "X-Client-Version";
    public const string Platform = "X-Platform";
    public const string BuildId = "X-Build-Id";
}

public enum GateDecision
{
    Allow,
    Maintenance,
    UpdateRequired,
}

/// <summary>Outcome of the gate with what the client shows (message, expected end, update URL).</summary>
public sealed record GateResult(GateDecision Decision, int? RetryAfterMs = null, IReadOnlyDictionary<string, string>? Details = null)
{
    public static GateResult Allow { get; } = new(GateDecision.Allow);
}

/// <summary>Maintenance / minimum client version check (GATE-01, GATE-02); replaceable (EXT-04).</summary>
public interface IGateEvaluator
{
    ValueTask<GateResult> EvaluateAsync(HttpContext context);
}

public sealed class AllowAllGateEvaluator : IGateEvaluator
{
    public ValueTask<GateResult> EvaluateAsync(HttpContext context) => ValueTask.FromResult(GateResult.Allow);
}

/// <summary>Drops a cached gate after a GM change so this instance applies it at once (review m6, EXT-04 safe).</summary>
public interface IGateCacheInvalidator
{
    void Invalidate();
}

/// <summary>
/// Default evaluator: the <c>gate</c> document through a per-instance cache of at most
/// <see cref="GateSettings.CacheSeconds"/> (PERF-08, research R2). The cache is one immutable snapshot (parsed minimum
/// versions and maintenance details included) stamped with the time the read <b>started</b>, so staleness never
/// exceeds the TTL plus nothing. One request refreshes an expired snapshot while the others keep using it; a failed
/// read keeps the previous snapshot (fail-open) and, when there is none, retries after a second instead of a full TTL.
/// </summary>
public sealed class StoreGateEvaluator(IKeyValueStore store, IClock clock, GameServerOptions options, ILogger<StoreGateEvaluator> logger)
    : IGateEvaluator, IGateCacheInvalidator, IDisposable
{
    /// <summary>Retry hint without an expected end time (research R6).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FailedFirstReadRetry = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile Snapshot? _snapshot;
    private int _generation;

    private sealed record Snapshot(
        GateDocument Gate,
        IReadOnlyDictionary<string, (ClientVersion Minimum, Dictionary<string, string> Details)> Minimums,
        Dictionary<string, string> MaintenanceDetails,
        DateTimeOffset ReadAt,
        DateTimeOffset ExpiresAt);

    public async ValueTask<GateResult> EvaluateAsync(HttpContext context)
    {
        var snapshot = await CurrentAsync(context.RequestAborted);
        var gate = snapshot.Gate;
        if (gate.Maintenance.Enabled)
        {
            var retryAfter = gate.Maintenance.ExpectedEndUtc is { } end
                ? TimeSpan.FromTicks(Math.Clamp((end - clock.UtcNow).Ticks, TimeSpan.FromSeconds(30).Ticks, TimeSpan.FromHours(1).Ticks))
                : DefaultRetryAfter;
            return new GateResult(GateDecision.Maintenance, (int)retryAfter.TotalMilliseconds, snapshot.MaintenanceDetails);
        }

        // No X-Platform header: no platform minimum applies (the SDK always sends it, GATE-02).
        var platform = context.Request.Headers[GateHeaders.Platform].ToString();
        if (platform.Length > 0
            && snapshot.Minimums.TryGetValue(platform.ToLowerInvariant(), out var min)
            && (!ClientVersion.TryParse(context.Request.Headers[GateHeaders.ClientVersion].ToString(), out var client) || client < min.Minimum))
        {
            return new GateResult(GateDecision.UpdateRequired, Details: min.Details);
        }

        return GateResult.Allow;
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _snapshot = _snapshot is { } current ? current with { ExpiresAt = DateTimeOffset.MinValue } : null;
    }

    public void Dispose() => _refresh.Dispose();

    private static bool IsFresh(Snapshot snapshot, DateTimeOffset now) =>
        // A clock that stepped backwards counts as expired (review m3).
        now >= snapshot.ReadAt && now < snapshot.ExpiresAt;

    private async ValueTask<Snapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        var cached = _snapshot;
        if (cached is not null && IsFresh(cached, clock.UtcNow))
        {
            return cached;
        }

        // First read waits; later refreshes are done by one request while the others use the previous snapshot.
        if (cached is null)
        {
            await _refresh.WaitAsync(cancellationToken);
        }
        else if (!await _refresh.WaitAsync(0, cancellationToken))
        {
            return cached;
        }

        try
        {
            cached = _snapshot;
            if (cached is not null && IsFresh(cached, clock.UtcNow))
            {
                return cached;
            }

            var generation = Volatile.Read(ref _generation);
            var started = clock.UtcNow;
            var ttl = TimeSpan.FromSeconds(options.Gate.CacheSeconds);
            Snapshot next;
            try
            {
                next = Build(GateDocument.Parse(await store.GetAsync(GateDocument.Key, cancellationToken)), started, started + ttl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail open: a store hiccup must not turn into a maintenance page; readiness reports the store.
                logger.LogWarning(ex, "Gate document could not be read; keeping the previous gate state.");
                next = cached is not null
                    ? cached with { ReadAt = started, ExpiresAt = started + ttl }
                    : Build(new GateDocument(), started, started + FailedFirstReadRetry);
            }

            // An invalidation during the read means the document changed: do not keep this copy for a full TTL.
            if (Volatile.Read(ref _generation) != generation)
            {
                next = next with { ExpiresAt = DateTimeOffset.MinValue };
            }

            _snapshot = next;
            return next;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static Snapshot Build(GateDocument gate, DateTimeOffset readAt, DateTimeOffset expiresAt)
    {
        var minimums = new Dictionary<string, (ClientVersion, Dictionary<string, string>)>(StringComparer.Ordinal);
        foreach (var (platform, min) in gate.MinClientVersions)
        {
            if (ClientVersion.TryParse(min.MinVersion, out var minimum))
            {
                var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["minClientVersion"] = min.MinVersion };
                if (min.UpdateUrl is { } url)
                {
                    details["updateUrl"] = url;
                }

                minimums[platform] = (minimum, details);
            }
        }

        var maintenance = new Dictionary<string, string>(StringComparer.Ordinal);
        if (gate.Maintenance.Message is { } message)
        {
            maintenance["message"] = message;
        }

        if (gate.Maintenance.ExpectedEndUtc is { } expected)
        {
            maintenance["expectedEndUtc"] = expected.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        return new Snapshot(gate, minimums, maintenance, readAt, expiresAt);
    }
}

/// <summary>Runs before authentication on gated endpoints (Player API and login); GM, health and Dev keep working.</summary>
public sealed class GateMiddleware(RequestDelegate next, IGateEvaluator evaluator, ErrorResponseWriter writer)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<GateMetadata>() is not null)
        {
            var result = await evaluator.EvaluateAsync(context);
            if (result.Decision != GateDecision.Allow)
            {
                await writer.WriteAsync(
                    context,
                    result.Decision == GateDecision.Maintenance ? CoreErrorCodes.Maintenance : CoreErrorCodes.UpdateRequired,
                    result.RetryAfterMs,
                    result.Details);
                return;
            }
        }

        await next(context);
    }
}
