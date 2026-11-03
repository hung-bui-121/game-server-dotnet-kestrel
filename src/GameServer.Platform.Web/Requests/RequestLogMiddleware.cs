using System.Diagnostics;
using GameServer.Actors;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Middleware;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Web.Requests;

/// <summary>
/// Outermost framework middleware (research R11): installs the <see cref="IRequestOutcomeFeature"/>, measures the whole
/// pipeline, finalizes the outcome and writes one request event (FR-080..FR-085). It never touches the request or response
/// body, and a failure while writing the event never changes the response.
/// </summary>
internal sealed class RequestLogMiddleware(RequestDelegate next, GameServerOptions options, PlatformMetrics metrics, ILoggerFactory loggerFactory, WireCatalog catalog)
{
    public const int MaxRouteLength = 120;
    public const int MaxActorIdLength = 64;

    private const string Ellipsis = "...";

    private static readonly PathString[] ProbePaths = [new("/health/live"), new("/health/ready")];

    private readonly ILogger _logger = loggerFactory.CreateLogger(RequestLog.Category);

    public async Task InvokeAsync(HttpContext context)
    {
        // A consumer middleware placed before UseGameServer may already hold the feature (context.RequestOutcome() before
        // next()): keep that instance so the reference it holds is the one finalized below. Only another implementation of the
        // interface (which the framework cannot finalize) is replaced.
        if (context.Features.Get<IRequestOutcomeFeature>() is not RequestOutcome outcome)
        {
            outcome = new RequestOutcome();
            context.Features.Set<IRequestOutcomeFeature>(outcome);
        }

        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            outcome.Complete(context, failure);
            if (outcome.Operation is null && context.GetEndpoint()?.Metadata.GetMetadata<CommandOperationMetadata>() is { } command)
            {
                // A command route that failed before the runner (auth, body, header) still names its operation.
                outcome.Operation = command.Operation;
            }

            outcome.RouteName = RouteNameOf(context);

            Write(context, outcome, started);
        }
    }

    /// <summary>Control characters become <c>?</c>; longer values are cut (with <c>...</c> when asked). Unchanged values are not copied.</summary>
    internal static string Clean(string value, int maxLength, bool ellipsis)
    {
        var cut = value.Length > maxLength;
        var length = cut ? maxLength : value.Length;
        if (!cut && !value.AsSpan().ContainsAnyInRange('\0', '\u001f') && !value.AsSpan().ContainsAnyInRange('\u007f', '\u009f'))
        {
            return value;
        }

        return string.Create(length + (cut && ellipsis ? Ellipsis.Length : 0), (value, length, cut && ellipsis), static (span, state) =>
        {
            var (source, count, withEllipsis) = state;
            for (var i = 0; i < count; i++)
            {
                var c = source[i];
                span[i] = char.IsControl(c) ? '?' : c;
            }

            if (withEllipsis)
            {
                Ellipsis.AsSpan().CopyTo(span[count..]);
            }
        });
    }

    /// <summary>
    /// <c>Route</c> of a request without a route template (404, 405): the rest of the path is client-controlled and may carry ids
    /// or tokens (LOG-05), so only its first segment is kept, cleaned and cut: <c>/api/v1/nope</c> → <c>/api/...</c>,
    /// <c>/scan</c> → <c>/scan</c>, <c>//x</c> → <c>/...</c>, the root → <c>/</c>.
    /// </summary>
    internal static string UnmatchedRoute(PathString path)
    {
        var value = path.Value;
        if (value is null || value.Length <= 1)
        {
            return "/";
        }

        var end = value.IndexOf('/', 1);
        return end switch
        {
            < 0 => Clean(value, MaxRouteLength, ellipsis: true),
            1 => "/" + Ellipsis,
            _ => string.Concat(Clean(value[..end], MaxRouteLength, ellipsis: true), "/", Ellipsis),
        };
    }

    /// <summary>FR-015: the declaration the endpoint was mapped from, else the catalog entry of its method and template; null when unmatched.</summary>
    private string? RouteNameOf(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return null;
        }

        return endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()?.Declaration.Name
            ?? catalog.FindRouteName(context.Request.Method, endpoint.RoutePattern.RawText ?? string.Empty);
    }

    private void Write(HttpContext context, RequestOutcome outcome, long started)
    {
        try
        {
            var level = outcome.Aborted ? LogLevel.Warning
                : outcome.StatusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error
                : outcome.StatusCode >= StatusCodes.Status400BadRequest ? LogLevel.Warning
                : LogLevel.Information;
            if (!options.RequestLog.IsEnabled(options) || IsExcluded(context.Request.Path) || !_logger.IsEnabled(level))
            {
                return;
            }

            var endpoint = context.GetEndpoint();
            var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? UnmatchedRoute(context.Request.Path);
            var actorType = ActorClaims.GetActorType(context.User);
            var actorId = actorType is null ? null : context.User.FindFirst(ActorClaims.ActorId)?.Value;
            var entry = new RequestLogEntry(
                context.Request.Method,
                route,
                outcome.RouteName,
                endpoint?.GetProfile()?.ToString(),
                outcome.Operation,
                actorType switch
                {
                    ActorType.Player => ActorClaims.PlayerType,
                    ActorType.Gm => ActorClaims.GmType,
                    _ => null,
                },
                actorId is null ? null : Clean(actorId, MaxActorIdLength, ellipsis: false),
                CorrelationMiddleware.GetCorrelationId(context),
                Activity.Current?.TraceId.ToHexString(),
                outcome.StatusCode,
                outcome.ResultCode!,
                outcome.Replayed,
                outcome.NotModified,
                outcome.Aborted,
                Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
            _logger.Log(level, RequestLog.EventId, entry, null, RequestLogEntry.Format);
        }
        catch (Exception)
        {
            // LOG-06/FR-085: logging never fails or changes a request; the failure is counted instead.
            metrics.RequestLogFailed();
        }
    }

    private bool IsExcluded(PathString path)
    {
        foreach (var probe in ProbePaths)
        {
            if (path.StartsWithSegments(probe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var excluded in options.RequestLog.ExcludedPaths)
        {
            if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
