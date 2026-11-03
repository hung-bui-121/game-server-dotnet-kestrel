using GameServer.Actors;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-006, SEC-09: Public declarations of modules count as public endpoints; host Public declarations do not (plan N6).</summary>
public sealed class EndpointValidationTests
{
    private static readonly RouteDeclaration OpenRoute = RouteDeclaration.Get("probe.open", "/v1/probe/open", SecurityProfile.Public, RouteSurface.Host);

    private static void MapOpen(WebApplication app) => app.MapDeclared(OpenRoute, () => "open").AsPublicEndpoint(protobuf: false);

    [Fact]
    public async Task A_module_public_declaration_allowlists_its_template()
    {
        await using var host = await SampleTestHost.StartAsync(
            gs =>
            {
                SampleTestHost.DefaultModules(gs);
                gs.AddModule(new CatalogProbeModule(routes: [OpenRoute], card: false));
            },
            beforeUseGameServer: MapOpen);

        var response = await host.Client().GetAsync(new Uri(OpenRoute.Template, UriKind.Relative));

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_public_declaration_still_needs_the_operator_allowlist()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(
                gs =>
                {
                    SampleTestHost.DefaultModules(gs);
                    gs.DeclareRoutes(OpenRoute);
                },
                beforeUseGameServer: MapOpen);
        });

        ex.Violations.ShouldBe(
        [
            "[endpoint GET /v1/probe/open] is Public but not in GameServer:PublicEndpointAllowlist. " +
            "Fix: add it to the allowlist and the PCR (SEC-09) or change its profile.",
        ]);
    }

    [Fact]
    public async Task A_host_public_declaration_on_the_operator_allowlist_starts()
    {
        await using var host = await SampleTestHost.StartAsync(
            gs =>
            {
                SampleTestHost.DefaultModules(gs);
                gs.DeclareRoutes(OpenRoute);
            },
            settings: s => s["GameServer:PublicEndpointAllowlist:1"] = OpenRoute.Template,
            beforeUseGameServer: MapOpen);

        (await host.Client().GetAsync(new Uri(OpenRoute.Template, UriKind.Relative))).IsSuccessStatusCode.ShouldBeTrue();
    }
}
