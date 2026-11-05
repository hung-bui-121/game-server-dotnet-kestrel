using GameServer.Actors;
using GameServer.Routing;

namespace GameServer.Modules.Identity;

/// <summary>Routes of the identity module (FG-026): login, session, profile, linking, deletion and GM moderation.</summary>
public static class IdentityRoutes
{
    /// <summary>Guest login (Public, gated).</summary>
    public static RouteDeclaration LoginGuest { get; } = RouteDeclaration.Post("identity.login_guest", IdentityModule.LoginRoute, SecurityProfile.Public);

    /// <summary>Login with a platform identity provider (Public, gated).</summary>
    public static RouteDeclaration LoginProvider { get; } =
        RouteDeclaration.Post("identity.login_provider", IdentityModule.ProviderLoginRoute, SecurityProfile.Public);

    public static RouteDeclaration Logout { get; } = RouteDeclaration.Post("identity.logout", IdentityModule.LogoutRoute, SecurityProfile.Player);

    public static RouteDeclaration SetDisplayName { get; } = RouteDeclaration.Command(SetDisplayNameHandler.Descriptor, IdentityModule.DisplayNameRoute);

    public static RouteDeclaration Link { get; } = RouteDeclaration.Command(LinkIdentityHandler.Descriptor, IdentityModule.LinkRoute);

    public static RouteDeclaration RequestDeletion { get; } = RouteDeclaration.Command(RequestDeletionHandler.Descriptor, IdentityModule.DeleteAccountRoute);

    public static RouteDeclaration Ban { get; } = RouteDeclaration.Command(BanPlayerHandler.Descriptor, IdentityModule.BanRoute);

    public static RouteDeclaration Unban { get; } = RouteDeclaration.Command(UnbanPlayerHandler.Descriptor, IdentityModule.UnbanRoute);

    public static RouteDeclaration CancelDeletion { get; } = RouteDeclaration.Command(CancelDeletionHandler.Descriptor, IdentityModule.CancelDeletionRoute);

    /// <summary>The scopes a ban may name, as GM options (slice 017): <c>login</c> plus every command group of the host.</summary>
    public static RouteDeclaration BanScopes { get; } = RouteDeclaration.Get(
        "identity.ban_scopes",
        IdentityModule.BanScopesRoute,
        SecurityProfile.Gm,
        RouteSurface.GmApi,
        IdentityPermissions.PlayerBan);

    public static IReadOnlyList<RouteDeclaration> All { get; } =
        [LoginGuest, LoginProvider, Logout, SetDisplayName, Link, RequestDeletion, Ban, Unban, CancelDeletion, BanScopes];
}
