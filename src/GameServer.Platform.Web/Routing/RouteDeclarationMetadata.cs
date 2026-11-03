using GameServer.Routing;

namespace GameServer.Platform.Web.Routing;

/// <summary>The declaration an endpoint was mapped from (FG-026): the endpoint check binds the endpoint to it (research R4, R7).</summary>
public sealed record RouteDeclarationMetadata(RouteDeclaration Declaration);
