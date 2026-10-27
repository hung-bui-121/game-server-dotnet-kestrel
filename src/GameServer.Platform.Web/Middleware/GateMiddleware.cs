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

/// <summary>
/// Default evaluator: the <c>gate</c> document through a per-instance cache of at most
/// <see cref="GateSettings.CacheSeconds"/> (PERF-08, research R2). One request refreshes an expired copy while the
/// others keep using it; a failed read keeps the previous copy (fail-open) and logs a warning.
/// </summary>
public sealed class StoreGateEvaluator(IKeyValueStore store, IClock clock, GameServerOptions options, ILogger<StoreGateEvaluator> logger) : IGateEvaluator, IDisposable
{
    /// <summary>Retry hint without an expected end time (research R6).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private GateDocument? _gate;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public async ValueTask<GateResult> EvaluateAsync(HttpContext context)
    {
        var gate = await CurrentAsync(context.RequestAborted);
        var now = clock.UtcNow;
        if (gate.Maintenance.Enabled)
        {
            var retryAfter = gate.Maintenance.ExpectedEndUtc is { } end
                ? TimeSpan.FromTicks(Math.Clamp((end - now).Ticks, TimeSpan.FromSeconds(30).Ticks, TimeSpan.FromHours(1).Ticks))
                : DefaultRetryAfter;
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            if (gate.Maintenance.Message is { } message)
            {
                details["message"] = message;
            }

            if (gate.Maintenance.ExpectedEndUtc is { } expected)
            {
                details["expectedEndUtc"] = expected.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            return new GateResult(GateDecision.Maintenance, (int)retryAfter.TotalMilliseconds, details);
        }

        var platform = context.Request.Headers[GateHeaders.Platform].ToString().ToLowerInvariant();
        if (gate.MinClientVersions.TryGetValue(platform, out var min)
            && ClientVersion.TryParse(min.MinVersion, out var minimum)
            && (!ClientVersion.TryParse(context.Request.Headers[GateHeaders.ClientVersion].ToString(), out var client) || client < minimum))
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["minClientVersion"] = min.MinVersion };
            if (min.UpdateUrl is { } url)
            {
                details["updateUrl"] = url;
            }

            return new GateResult(GateDecision.UpdateRequired, Details: details);
        }

        return GateResult.Allow;
    }

    /// <summary>Drops the cached copy on this instance after a GM change (others follow within the TTL).</summary>
    public void Invalidate() => _readAt = DateTimeOffset.MinValue;

    public void Dispose() => _refresh.Dispose();

    private async ValueTask<GateDocument> CurrentAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cached = _gate;
        if (cached is not null && now - _readAt < TimeSpan.FromSeconds(options.Gate.CacheSeconds))
        {
            return cached;
        }

        // First read waits; later refreshes are done by one request while the others use the previous copy.
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
            if (_gate is not null && clock.UtcNow - _readAt < TimeSpan.FromSeconds(options.Gate.CacheSeconds))
            {
                return _gate;
            }

            try
            {
                _gate = GateDocument.Parse(await store.GetAsync(GateDocument.Key, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail open: a store hiccup must not turn into a maintenance page; readiness reports the store.
                logger.LogWarning(ex, "Gate document could not be read; keeping the previous gate state.");
                _gate ??= new GateDocument();
            }

            _readAt = clock.UtcNow;
            return _gate;
        }
        finally
        {
            _refresh.Release();
        }
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
