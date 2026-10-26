using GameServer.Errors;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Headers every Player request carries (GATE-02), sent by the client SDK. P0 only defines them; P0.5 compares
/// <see cref="ClientVersion"/> with <c>minClientVersion</c> per platform.
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

/// <summary>
/// Maintenance / minimum client version check (GATE-01, GATE-02). Slice 001 provides the slot and codes;
/// the <c>gate</c> document and GM page come in P0.5.
/// </summary>
public interface IGateEvaluator
{
    ValueTask<GateDecision> EvaluateAsync(HttpContext context);
}

public sealed class AllowAllGateEvaluator : IGateEvaluator
{
    public ValueTask<GateDecision> EvaluateAsync(HttpContext context) => ValueTask.FromResult(GateDecision.Allow);
}

/// <summary>Runs before authentication on gated endpoints (Player API and login); GM, health and Dev keep working.</summary>
public sealed class GateMiddleware(RequestDelegate next, IGateEvaluator evaluator, ErrorResponseWriter writer)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<GateMetadata>() is not null)
        {
            var decision = await evaluator.EvaluateAsync(context);
            if (decision != GateDecision.Allow)
            {
                await writer.WriteAsync(context, decision == GateDecision.Maintenance ? CoreErrorCodes.Maintenance : CoreErrorCodes.UpdateRequired);
                return;
            }
        }

        await next(context);
    }
}
