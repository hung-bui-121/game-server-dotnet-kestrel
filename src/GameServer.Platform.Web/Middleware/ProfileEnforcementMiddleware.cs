using GameServer.Actors;
using GameServer.Errors;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Errors;
using GameServer.Platform.Web.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace GameServer.Platform.Web.Middleware;

/// <summary>
/// Deny-by-default (SEC-08, ACC-12): an endpoint without a security profile is refused even if the startup validator
/// was disabled. Authenticates with the schemes of the endpoint's profile only (SEC-02), enforces GM permissions
/// (SEC-07) and the body limit of the profile (API-06).
/// </summary>
public sealed class ProfileEnforcementMiddleware(
    RequestDelegate next,
    ErrorResponseWriter writer,
    GameServerOptions options,
    IOptions<ActorSchemeOptions> schemes)
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

        if (profile.Value == SecurityProfile.Gm && IsCrossSiteMutation(context.Request))
        {
            // P0 part of SEC-05: a cookie-authenticated GM mutation must come from the same site.
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        SetBodyLimit(context, profile.Value);
        if (profile.Value != SecurityProfile.Public)
        {
            var authenticated = profile.Value switch
            {
                SecurityProfile.Player => await AuthenticateAsync(context, schemes.Value.PlayerSchemes, ActorType.Player),
                SecurityProfile.Gm => await AuthenticateAsync(context, schemes.Value.GmSchemes, ActorType.Gm),
                _ => await AuthenticateAsync(context, schemes.Value.PlayerSchemes, ActorType.Player)
                     || await AuthenticateAsync(context, schemes.Value.GmSchemes, ActorType.Gm),
            };
            if (!authenticated)
            {
                var (code, details) = AuthenticationFailures.Get(context);
                await writer.WriteAsync(context, code, details: details);
                return;
            }
        }

        var permission = endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission;
        if (permission is not null && !context.User.HasClaim(ActorClaims.Permission, permission))
        {
            await writer.WriteAsync(context, CoreErrorCodes.Forbidden);
            return;
        }

        await next(context);
    }

    private static async Task<bool> AuthenticateAsync(HttpContext context, IReadOnlyList<string> schemeNames, ActorType expected)
    {
        foreach (var scheme in schemeNames)
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Succeeded && ActorClaims.GetActorType(result.Principal) == expected)
            {
                context.User = result.Principal;
                return true;
            }
        }

        return false;
    }

    private static bool IsCrossSiteMutation(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method)
        && !HttpMethods.IsOptions(request.Method)
        && string.Equals(request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase);

    private void SetBodyLimit(HttpContext context, SecurityProfile profile)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = profile == SecurityProfile.Gm ? options.Limits.GmBodyBytes : options.Limits.PlayerBodyBytes;
        }
    }
}
