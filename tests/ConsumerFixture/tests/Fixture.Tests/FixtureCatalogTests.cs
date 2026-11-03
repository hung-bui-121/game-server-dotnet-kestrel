using Fixture.Game;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Routing;
using GameServer.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fixture.Tests;

/// <summary>
/// FG-026 (US1-1, US1-2) from a game repository: framework routes are read through public API instead of copied strings, and the
/// game host's catalog lists the game's own route with owner and origin.
/// </summary>
public sealed class FixtureCatalogTests
{
    [Fact]
    public void Framework_routes_are_public_declarations()
    {
        PlatformRoutes.Config.Template.ShouldBe("/api/v1/config/get");
        PlatformRoutes.Config.Name.ShouldBe("platform.config");
        IdentityRoutes.LoginGuest.Template.ShouldBe("/api/v1/identity/login/guest");
        IdentityRoutes.LoginGuest.Methods.ShouldBe(["POST"]);
    }

    [Fact]
    public async Task The_game_host_catalog_holds_the_game_route_and_the_framework_routes()
    {
        await using var factory = new LocalFlavorFactory<Program>();

        var catalog = factory.Services.GetRequiredService<WireCatalog>();

        catalog.TryGetRoute(FixtureRoutes.Save.Name, out var save).ShouldBeTrue();
        save!.Declaration.Template.ShouldBe(FixtureModule.SaveRoute);
        save.Owner.ShouldBe(FixtureModuleDefinition.ModuleName);
        save.Origin.ShouldBe(CatalogOrigin.Game);
        save.ServiceOnly.ShouldBeFalse();

        catalog.TryGetRoute(PlatformRoutes.Config.Name, out var config).ShouldBeTrue();
        config!.Origin.ShouldBe(CatalogOrigin.Framework);
        catalog.TryGetRoute(IdentityRoutes.LoginGuest.Name, out var login).ShouldBeTrue();
        login!.Owner.ShouldBe(IdentityModule.ModuleName);
        catalog.FindRouteName("POST", FixtureModule.SaveRoute).ShouldBe("fixture.save");
    }
}
