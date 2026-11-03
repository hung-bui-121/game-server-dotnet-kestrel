using GameServer.Routing;

namespace GameServer.Platform.Web.Catalog;

/// <summary>Routes a host maps itself outside any module (<c>GameServerBuilder.DeclareRoutes</c>, owner <c>host</c>).</summary>
internal sealed class HostRouteDeclarations
{
    private readonly List<RouteDeclaration> _routes = [];

    public IReadOnlyList<RouteDeclaration> Routes => _routes;

    public void Add(IEnumerable<RouteDeclaration> routes) => _routes.AddRange(routes);
}
