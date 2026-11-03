using GameServer.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace GameServer.Platform.Web.Routing;

/// <summary>Maps an endpoint from its <see cref="RouteDeclaration"/> (FR-005): template and methods are written once.</summary>
public static class DeclaredRouteExtensions
{
    /// <summary>
    /// Maps <paramref name="route"/>'s methods and template to <paramref name="handler"/> and tags the endpoint. The profile helper
    /// (AsPlayerEndpoint …) is still called by the caller; the startup check compares it with the declaration.
    /// </summary>
    public static RouteHandlerBuilder MapDeclared(this IEndpointRouteBuilder routes, RouteDeclaration route, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(handler);
        var builder = route.Methods.Count == 0
            ? routes.Map(route.Template, handler)
            : routes.MapMethods(route.Template, route.Methods, handler);
        return builder.WithDeclaration(route);
    }

    /// <summary>Tags an endpoint mapped by a library helper (MapHealthChecks, MapOpenApi) with its declaration.</summary>
    public static TBuilder WithDeclaration<TBuilder>(this TBuilder builder, RouteDeclaration route)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(route);
        builder.WithMetadata(new RouteDeclarationMetadata(route));
        return builder;
    }
}
