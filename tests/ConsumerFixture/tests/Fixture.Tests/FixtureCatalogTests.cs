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

    /// <summary>Slice 014 T051 (FR-003, FR-050): the client-visible card section names its message and its hidden member.</summary>
    [Fact]
    public async Task The_card_section_carries_its_message_and_hidden_member()
    {
        await using var factory = new LocalFlavorFactory<Program>();

        var catalog = factory.Services.GetRequiredService<WireCatalog>();

        catalog.TryGetSection(FixtureModuleDefinition.CardAggregate, out var card).ShouldBeTrue();
        card!.MessageFullName.ShouldBe("fixture.v1.CardState");
        card.HiddenMembers.ShouldBe([nameof(CardState.InternalNote)]);
        card.Owner.ShouldBe(FixtureModuleDefinition.ModuleName);
        card.Origin.ShouldBe(CatalogOrigin.Game);
        catalog.TryGetRoute(FixtureRoutes.Card.Name, out var route).ShouldBeTrue();
        route!.Declaration.Operation.ShouldBeNull();
    }

    [Fact]
    public async Task The_card_route_returns_the_card_section()
    {
        await using var factory = new LocalFlavorFactory<Program>();
        var login = await factory.CreateClient().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        var player = factory.CreateClient();
        player.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.SessionToken);

        var response = await player.PostProtobufAsync(FixtureRoutes.CardTemplate, new Fixture.V1.GetCardRequest(), ProtobufHttp.NewRequestId());
        var card = (await response.ReadProtobufAsync<Fixture.V1.GetCardResponse>()).Card;

        card.Id.ShouldBe(FixtureModuleDefinition.CardAggregate);
        Fixture.V1.CardState.Parser.ParseFrom(card.Payload).Title.ShouldBeEmpty();
    }
}
