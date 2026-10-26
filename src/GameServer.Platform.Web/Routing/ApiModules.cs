using GameServer.Actors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Routing;

/// <summary>Module exposing Player API endpoints (Protobuf). Mapped explicitly by the host (ARC-03, ARC-07).</summary>
public interface IPlayerApiModule
{
    void MapPlayerApi(IEndpointRouteBuilder routes);
}

/// <summary>Module exposing GM API endpoints (JSON). Can be hosted separately (OPS-10).</summary>
public interface IGmApiModule
{
    void MapGmApi(IEndpointRouteBuilder routes);
}

/// <summary>
/// Module exposing host-level endpoints (health, metrics) that every host maps, whatever
/// <c>Hosting:MapPlayerApi</c>/<c>MapGmApi</c> say.
/// </summary>
public interface IHostEndpointsModule
{
    void MapHostEndpoints(IEndpointRouteBuilder routes);
}

/// <summary>Helpers that attach the mandatory metadata to endpoints.</summary>
public static class ApiRouteExtensions
{
    public const string PlayerTimeoutPolicy = "gs-player";
    public const string GmTimeoutPolicy = "gs-gm";
    public const string CommandLimiterPolicy = "gs-command";
    public const string GmLimiterPolicy = "gs-gm";

    /// <summary>Player endpoint: Protobuf, profile Player, command limiter, 10 s timeout.</summary>
    public static TBuilder AsPlayerEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new SecurityProfileMetadata(SecurityProfile.Player), ProtobufEndpointMetadata.Instance, GateMetadata.Instance);
        builder.WithRequestTimeout(PlayerTimeoutPolicy);
        builder.RequireRateLimiting(CommandLimiterPolicy);
        return builder;
    }

    /// <summary>GM endpoint: JSON, profile GM, required permission, GM limiter, 30 s timeout.</summary>
    public static TBuilder AsGmEndpoint<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new SecurityProfileMetadata(SecurityProfile.Gm), new RequiredPermissionMetadata(permission));
        builder.WithRequestTimeout(GmTimeoutPolicy);
        builder.RequireRateLimiting(GmLimiterPolicy);
        return builder;
    }

    /// <summary>Public endpoint; must also be in <c>GameServer:PublicEndpointAllowlist</c> (SEC-09).</summary>
    public static TBuilder AsPublicEndpoint<TBuilder>(this TBuilder builder, bool protobuf)
        where TBuilder : IEndpointConventionBuilder =>
        protobuf
            ? builder.WithMetadata(new SecurityProfileMetadata(SecurityProfile.Public), ProtobufEndpointMetadata.Instance)
            : builder.WithMetadata(new SecurityProfileMetadata(SecurityProfile.Public));

    /// <summary>
    /// Public Player-facing endpoint such as login: Protobuf, maintenance/version gate, Player timeout and limiter.
    /// The route must be declared in <c>ModuleDescriptor.PublicEndpoints</c> (SEC-09).
    /// </summary>
    public static TBuilder AsPublicPlayerEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new SecurityProfileMetadata(SecurityProfile.Public), ProtobufEndpointMetadata.Instance, GateMetadata.Instance);
        builder.WithRequestTimeout(PlayerTimeoutPolicy);
        builder.RequireRateLimiting(CommandLimiterPolicy);
        return builder;
    }

    /// <summary>Development-only endpoint (OPS-09); forbidden in Production by the startup validator.</summary>
    public static TBuilder AsDevEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SecurityProfileMetadata(SecurityProfile.Dev));

    internal static SecurityProfile? GetProfile(this Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<SecurityProfileMetadata>()?.Profile;

    internal static bool IsProtobuf(this HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<ProtobufEndpointMetadata>() is not null)
        {
            return true;
        }

        // Endpoints with a declared non-Protobuf profile use JSON; unmatched or unprofiled routes follow the path.
        return metadata?.GetMetadata<SecurityProfileMetadata>() is null
            && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    }
}
