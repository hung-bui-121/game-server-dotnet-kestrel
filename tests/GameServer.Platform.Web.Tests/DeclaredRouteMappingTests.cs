using GameServer.Actors;
using GameServer.Platform.Web.OpenApi;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-005: endpoints mapped from a declaration carry its template, methods and <see cref="RouteDeclarationMetadata"/> (research R4).</summary>
public sealed class DeclaredRouteMappingTests
{
    private static WebApplication BareApp(Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    private static RouteEndpoint Single(IEndpointRouteBuilder app) =>
        app.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ShouldHaveSingleItem();

    [Fact]
    public async Task A_post_declaration_maps_its_template_and_method()
    {
        await using var app = BareApp();
        var route = RouteDeclaration.Post("area.post", "api/v1/area/post", SecurityProfile.Player);

        app.MapDeclared(route, () => "x");

        var endpoint = Single(app);
        endpoint.RoutePattern.RawText.ShouldBe("/api/v1/area/post");
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ShouldBe(["POST"]);
        endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()!.Declaration.ShouldBeSameAs(route);
    }

    [Fact]
    public async Task A_declaration_with_two_methods_maps_both()
    {
        await using var app = BareApp();
        var route = new RouteDeclaration("area.read", ["HEAD", "GET"], "/v1/area/read", SecurityProfile.Public, RouteSurface.Host);

        app.MapDeclared(route, () => "x");

        Single(app).Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ShouldBe(["GET", "HEAD"]);
    }

    [Fact]
    public async Task An_any_method_declaration_maps_without_method_metadata()
    {
        await using var app = BareApp();
        var route = RouteDeclaration.AnyMethod("area.any", "/v1/area/any", SecurityProfile.Public, RouteSurface.Host);

        app.MapDeclared(route, () => "x");

        var endpoint = Single(app);
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>().ShouldBeNull();
        endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()!.Declaration.ShouldBeSameAs(route);
    }

    [Fact]
    public async Task With_declaration_tags_a_health_check_endpoint()
    {
        await using var app = BareApp(services => services.AddHealthChecks());
        var route = RouteDeclaration.AnyMethod("platform.health_live", "/health/live", SecurityProfile.Public, RouteSurface.Host);

        app.MapHealthChecks(route.Template).WithDeclaration(route);

        var endpoint = Single(app);
        endpoint.RoutePattern.RawText.ShouldBe("/health/live");
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>().ShouldBeNull();
        endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()!.Declaration.ShouldBeSameAs(route);
    }

    [Fact]
    public async Task With_declaration_tags_the_openapi_endpoint()
    {
        await using var app = BareApp(GmOpenApi.Add);

        app.MapOpenApi(PlatformRoutes.GmOpenApi.Template).WithDeclaration(PlatformRoutes.GmOpenApi);

        var endpoint = Single(app);
        endpoint.RoutePattern.RawText.ShouldBe(GmOpenApi.Route);
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ShouldBe(["GET"]);
        endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>()!.Declaration.ShouldBeSameAs(PlatformRoutes.GmOpenApi);
    }

    [Fact]
    public async Task Every_core_endpoint_and_the_openapi_document_carry_their_platform_declaration()
    {
        // IClock registered: the time endpoint takes it as a service parameter.
        await using var app = BareApp(services =>
        {
            GmOpenApi.Add(services);
            services.AddSingleton<GameServer.Time.IClock, GameServer.Time.SystemClock>();
        });

        GameServer.Platform.Web.Endpoints.CoreEndpoints.MapPublic(app);
        GameServer.Platform.Web.Endpoints.CoreEndpoints.MapPlayer(app);
        GmOpenApi.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToArray();
        endpoints.Length.ShouldBe(PlatformRoutes.All.Count);
        foreach (var endpoint in endpoints)
        {
            var declaration = endpoint.Metadata.GetMetadata<RouteDeclarationMetadata>().ShouldNotBeNull(endpoint.DisplayName).Declaration;
            PlatformRoutes.All.ShouldContain(declaration);
            endpoint.RoutePattern.RawText.ShouldBe(declaration.Template);
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ShouldBe(declaration.Methods);
            endpoint.Metadata.GetMetadata<SecurityProfileMetadata>()!.Profile.ShouldBe(declaration.Profile);
        }
    }

    [Fact]
    public void Platform_routes_are_the_five_platform_declarations()
    {
        PlatformRoutes.All.Select(r => r.ToString()).ShouldBe(
        [
            "GET /v1/time (platform.time)",
            "POST /api/v1/player/snapshot (platform.snapshot)",
            "POST /api/v1/config/get (platform.config)",
            "GET /api/v1/player/receipts/{receiptId} (platform.receipt)",
            "GET /gm/v1/openapi/{documentName}.json (platform.gm_openapi)",
        ]);
        PlatformRoutes.Time.Surface.ShouldBe(RouteSurface.Host);
        PlatformRoutes.Time.Profile.ShouldBe(SecurityProfile.Public);
        PlatformRoutes.Snapshot.Surface.ShouldBe(RouteSurface.PlayerApi);
        PlatformRoutes.GmOpenApi.Surface.ShouldBe(RouteSurface.GmApi);
        PlatformRoutes.GmOpenApi.Condition.ShouldBe(RouteConditions.DevelopmentTools);
    }
}
