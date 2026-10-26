using GameServer.Actors;
using GameServer.Errors;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Deny-by-default (SEC-08, ACC-12): an endpoint without a security profile is refused even if the startup
/// validator was disabled. Enforces the actor type per profile, GM permissions (SEC-07) and body limits (API-06).
/// </summary>
public sealed class ProfileEnforcementMiddleware(RequestDelegate next, ErrorResponseWriter writer, GameServerOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            await next(context);
            return;
        }

        var profile = endpoint.GetProfile();
        if (profile is null)
        {
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        SetBodyLimit(context, profile.Value);
        var actorType = ActorClaims.GetActorType(context.User);
        switch (profile.Value)
        {
            case SecurityProfile.Player when actorType != ActorType.Player:
            case SecurityProfile.Gm when actorType != ActorType.Gm:
            case SecurityProfile.Dev when actorType is null:
                await writer.WriteAsync(context, CoreErrorCodes.Unauthenticated);
                return;
        }

        var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
        if (permission is not null && !context.User.HasClaim(ActorClaims.Permission, permission))
        {
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        await next(context);
    }

    private void SetBodyLimit(HttpContext context, SecurityProfile profile)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = profile == SecurityProfile.Gm ? options.Limits.GmBodyBytes : options.Limits.PlayerBodyBytes;
        }
    }
}
