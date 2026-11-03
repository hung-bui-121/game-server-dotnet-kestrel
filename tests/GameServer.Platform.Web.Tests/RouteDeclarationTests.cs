using GameServer.Actors;
using GameServer.Modules.Economy;
using GameServer.Routing;
using Sample.Counter;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FG-026 (1): a route declaration is pure data; constructing canonicalizes and never validates (research R1, R3).</summary>
public sealed class RouteDeclarationTests
{
    [Theory]
    [InlineData("api/v1/x")]
    [InlineData("/api/v1/x")]
    [InlineData("//api/v1/x")]
    public void Template_gets_exactly_one_leading_slash(string template)
    {
        RouteDeclaration.Post("area.x", template, SecurityProfile.Player).Template.ShouldBe("/api/v1/x");
    }

    [Fact]
    public void Methods_are_upper_cased_distinct_and_ordinal_sorted()
    {
        var route = new RouteDeclaration("area.x", ["head", "GET", "get"], "/v1/x", SecurityProfile.Public, RouteSurface.Host);

        route.Methods.ShouldBe(["GET", "HEAD"]);
        route.ToString().ShouldBe("GET,HEAD /v1/x (area.x)");
    }

    [Fact]
    public void Empty_methods_mean_any_method()
    {
        var route = RouteDeclaration.AnyMethod("platform.health_live", "/health/live", SecurityProfile.Public, RouteSurface.Host);

        route.Methods.ShouldBeEmpty();
        route.ToString().ShouldBe("* /health/live (platform.health_live)");
    }

    [Fact]
    public void Constructing_never_validates()
    {
        var route = new RouteDeclaration("Bad Name", ["post"], "{oops", SecurityProfile.Gm, RouteSurface.PlayerApi);

        route.Name.ShouldBe("Bad Name");
        route.Template.ShouldBe("/{oops");
        route.Permission.ShouldBeNull();
    }

    [Fact]
    public void Verb_factories_set_their_method_and_defaults()
    {
        RouteDeclaration.Get("a.b", "/v1/a", SecurityProfile.Player).Methods.ShouldBe(["GET"]);
        RouteDeclaration.Put("a.b", "/v1/a", SecurityProfile.Player).Methods.ShouldBe(["PUT"]);
        RouteDeclaration.Delete("a.b", "/v1/a", SecurityProfile.Player).Methods.ShouldBe(["DELETE"]);

        var post = RouteDeclaration.Post("a.b", "/v1/a", SecurityProfile.Player);
        post.Methods.ShouldBe(["POST"]);
        post.Surface.ShouldBe(RouteSurface.PlayerApi);
        post.Operation.ShouldBeNull();
        post.Permission.ShouldBeNull();
        post.Condition.ShouldBeNull();
    }

    [Fact]
    public void Command_of_a_player_command_takes_name_profile_and_surface_from_the_descriptor()
    {
        var route = RouteDeclaration.Command(IncrementHandler.Descriptor, "/api/v1/counter/increment");

        route.Name.ShouldBe(IncrementHandler.Operation);
        route.Operation.ShouldBe(IncrementHandler.Operation);
        route.Methods.ShouldBe(["POST"]);
        route.Profile.ShouldBe(SecurityProfile.Player);
        route.Surface.ShouldBe(RouteSurface.PlayerApi);
        route.Permission.ShouldBeNull();
        route.ToString().ShouldBe("POST /api/v1/counter/increment (counter.increment)");
    }

    [Fact]
    public void Command_of_a_dev_command_is_a_player_api_route_with_profile_dev()
    {
        var route = RouteDeclaration.Command(CheatGrantHandler.Descriptor, "/api/v1/cheat/grant");

        route.Profile.ShouldBe(SecurityProfile.Dev);
        route.Surface.ShouldBe(RouteSurface.PlayerApi);
        route.Operation.ShouldBe(CheatGrantHandler.Operation);
    }

    [Fact]
    public void Command_of_a_gm_command_is_a_gm_api_route_with_the_command_permission()
    {
        var route = RouteDeclaration.Command(AdjustResourceHandler.Descriptor, "/gm/v1/players/{playerId}/resources/adjust");

        route.Profile.ShouldBe(SecurityProfile.Gm);
        route.Surface.ShouldBe(RouteSurface.GmApi);
        route.Permission.ShouldBe(EconomyPermissions.ResourceAdjust);
        route.Operation.ShouldBe(AdjustResourceHandler.Operation);
    }

    [Fact]
    public void Command_keeps_an_explicit_name_and_condition()
    {
        var route = RouteDeclaration.Command(IncrementHandler.Descriptor, "/api/v1/counter/increment", name: "counter.bump", condition: "feature.x");

        route.Name.ShouldBe("counter.bump");
        route.Operation.ShouldBe(IncrementHandler.Operation);
        route.Condition.ShouldBe("feature.x");
    }

    [Fact]
    public void Two_identical_declarations_are_not_equal()
    {
        var first = RouteDeclaration.Post("a.b", "/v1/a", SecurityProfile.Player);
        var second = RouteDeclaration.Post("a.b", "/v1/a", SecurityProfile.Player);

        first.Equals(second).ShouldBeFalse();
    }
}
