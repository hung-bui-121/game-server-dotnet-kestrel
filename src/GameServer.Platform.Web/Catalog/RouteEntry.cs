using GameServer.Routing;

namespace GameServer.Platform.Web.Catalog;

/// <summary>Who wrote a catalog entry: the framework (platform or a <c>GameServer.*</c> module) or the game.</summary>
public enum CatalogOrigin
{
    Framework,
    Game,
}

/// <summary>A declared route of the host with the module (or <c>platform</c> / <c>host</c>) that declared it (FR-001).</summary>
/// <param name="ServiceOnly">The owner is registered with <c>ServiceOnly()</c>: its routes are never mapped.</param>
public sealed record RouteEntry(RouteDeclaration Declaration, string Owner, CatalogOrigin Origin, bool ServiceOnly)
{
    public const string PlatformOwner = "platform";
    public const string HostOwner = "host";
}
