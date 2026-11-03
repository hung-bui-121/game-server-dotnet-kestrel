using GameServer.Actors;
using GameServer.Modules.Economy;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sample.Counter;
using Shouldly;
using EconomyWire = GameServer.Modules.Economy.Protocol.V1;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-005: the command helpers map a declaration's template and operation and tag the endpoint (research R4).</summary>
public sealed class DeclaredCommandMappingTests
{
    private static readonly RouteDeclaration Increment = RouteDeclaration.Command(IncrementHandler.Descriptor, "/api/v1/counter/increment");
    private static readonly RouteDeclaration Grant = RouteDeclaration.Command(CheatGrantHandler.Descriptor, "/api/v1/cheat/grant");
    private static readonly RouteDeclaration Adjust = RouteDeclaration.Command(AdjustResourceHandler.Descriptor, "/gm/v1/players/{playerId}/resources/adjust");

    private static WebApplication BareApp() => WebApplication.CreateBuilder().Build();

    private static RouteEndpoint Single(IEndpointRouteBuilder app) =>
        app.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ShouldHaveSingleItem();

    private static void ShouldBeMappedFrom(RouteEndpoint endpoint, RouteDeclaration route, SecurityProfile profile)
    {
        endpoint.RoutePattern.RawText.ShouldBe(route.Template);
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ShouldBe(["POST"]);
        endpoint.Metadata.GetMetadata<CommandOperationMetadata>()!.Operation.ShouldBe(route.Operation);
        endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()!.Declaration.ShouldBeSameAs(route);
        endpoint.Metadata.GetOrderedMetadata<SecurityProfileMetadata>().ShouldHaveSingleItem().Profile.ShouldBe(profile);
    }

    private static IncrementCommand ToDomain(Wire.IncrementRequest request) => new(request.By, null);

    private static Wire.IncrementResult ToWire(IncrementResult result) => new() { Value = result.Value };

    [Fact]
    public async Task Player_command_overload_maps_the_declaration()
    {
        await using var app = BareApp();

        app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(Increment, ToDomain, ToWire);

        ShouldBeMappedFrom(Single(app), Increment, SecurityProfile.Player);
    }

    [Fact]
    public async Task Async_player_command_overload_maps_the_declaration()
    {
        await using var app = BareApp();

        app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            Increment,
            (request, _) => Task.FromResult(ToDomain(request)),
            ToWire);

        ShouldBeMappedFrom(Single(app), Increment, SecurityProfile.Player);
    }

    [Fact]
    public async Task Player_command_overload_with_hook_maps_the_declaration()
    {
        await using var app = BareApp();

        app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(Increment, ToDomain, ToWire, (_, _) => Task.CompletedTask);

        ShouldBeMappedFrom(Single(app), Increment, SecurityProfile.Player);
    }

    [Fact]
    public async Task Dev_command_overload_maps_the_declaration()
    {
        await using var app = BareApp();

        app.MapDevCommand<EconomyWire.CheatGrantRequest, CheatGrantCommand, CheatGrantResult>(
            Grant,
            request => new CheatGrantCommand(request.Resource, request.Quantity),
            result => new EconomyWire.CheatGrantResult { BalanceAfter = result.BalanceAfter });

        ShouldBeMappedFrom(Single(app), Grant, SecurityProfile.Dev);
    }

    [Fact]
    public async Task Gm_command_overloads_map_the_declaration_with_its_permission()
    {
        await using var plain = BareApp();
        await using var hooked = BareApp();

        plain.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(Adjust, body => new AdjustResourceCommand(body.Resource!, body.Delta, body.Reason!));
        hooked.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(
            Adjust,
            body => new AdjustResourceCommand(body.Resource!, body.Delta, body.Reason!),
            (_, _) => Task.CompletedTask);

        foreach (var endpoint in new[] { Single(plain), Single(hooked) })
        {
            ShouldBeMappedFrom(endpoint, Adjust, SecurityProfile.Gm);
            endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()!.Permission.ShouldBe(EconomyPermissions.ResourceAdjust);
        }
    }

    [Fact]
    public async Task A_declaration_without_operation_is_refused_naming_the_route()
    {
        await using var app = BareApp();
        var query = RouteDeclaration.Post("counter.get", "/api/v1/counter/get", SecurityProfile.Player);

        var ex = Should.Throw<ArgumentException>(() =>
            app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(query, ToDomain, ToWire));

        ex.Message.ShouldContain("POST /api/v1/counter/get (counter.get)");
        Should.Throw<ArgumentException>(() => app.MapDevCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(query, ToDomain, ToWire));
        Should.Throw<ArgumentException>(() =>
            app.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(query, body => new AdjustResourceCommand(body.Resource!, body.Delta, body.Reason!)));
    }

    [Fact]
    public async Task A_gm_declaration_without_permission_or_player_id_is_refused()
    {
        await using var app = BareApp();
        var noPermission = new RouteDeclaration(
            "economy.adjust", ["POST"], "/gm/v1/players/{playerId}/resources/adjust", SecurityProfile.Gm, RouteSurface.GmApi, AdjustResourceHandler.Operation);
        var noPlayerId = RouteDeclaration.Command(AdjustResourceHandler.Descriptor, "/gm/v1/resources/adjust");

        Should.Throw<ArgumentException>(() =>
                app.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(noPermission, body => new AdjustResourceCommand(body.Resource!, body.Delta, body.Reason!)))
            .Message.ShouldContain("names no permission");
        Should.Throw<ArgumentException>(() =>
                app.MapGmCommand<AdjustResourceBody, AdjustResourceCommand, AdjustResourceResult>(noPlayerId, body => new AdjustResourceCommand(body.Resource!, body.Delta, body.Reason!)))
            .Message.ShouldContain("{playerId}");
    }

    [Fact]
    public async Task A_profile_conflict_is_not_thrown_while_mapping()
    {
        await using var app = BareApp();

        // A GM command's declaration mapped by the Player helper: the startup check reports it (research R4), mapping does not.
        app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
            RouteDeclaration.Command(AdjustResourceHandler.Descriptor, "/api/v1/economy/adjust"),
            ToDomain,
            ToWire);

        Single(app).Metadata.GetMetadata<SecurityProfileMetadata>()!.Profile.ShouldBe(SecurityProfile.Player);
    }
}
