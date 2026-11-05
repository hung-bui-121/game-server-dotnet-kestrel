using GameServer.Actors;
using GameServer.Modules;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// Slice 017 V10 (FR-035, SC-007): a Public GM route declared by a module is allowlisted through its declaration, so a game adds
/// no <c>GameServer:PublicEndpointAllowlist</c> line for it, whatever that setting holds; the same route mapped without a
/// declaration still fails the SEC-09 check (the test sees the check).
/// </summary>
public sealed class PublicDeclarationAllowlistTests
{
    private const string Template = "/gm/v1/probe/public";

    private static readonly RouteDeclaration PublicProbe =
        RouteDeclaration.Get("probe.public", Template, SecurityProfile.Public, RouteSurface.GmApi);

    [Fact]
    public async Task A_declared_public_gm_route_starts_with_the_default_allowlist()
    {
        await using var host = await SampleTestHost.StartAsync(Modules(declared: true));

        var response = await host.Client().GetAsync(Template);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_declared_public_gm_route_starts_with_an_explicit_allowlist_that_does_not_list_it()
    {
        await using var host = await SampleTestHost.StartAsync(
            Modules(declared: true),
            settings: s => s["GameServer:PublicEndpointAllowlist:0"] = "/v1/time");

        var response = await host.Client().GetAsync(Template);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task The_same_route_without_a_declaration_fails_the_allowlist_check()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(Modules(declared: false));
        });

        ex.Violations.ShouldContain(
            $"[endpoint GET {Template}] is Public but not in GameServer:PublicEndpointAllowlist. " +
            "Fix: add it to the allowlist and the PCR (SEC-09) or change its profile.");
    }

    private static Action<GameServerBuilder> Modules(bool declared) =>
        builder =>
        {
            SampleTestHost.DefaultModules(builder);
            builder.AddModule(new PublicGmProbeModule(declared));
        };

    /// <summary>A module of the test assembly with one Public GM route, declared or mapped by template only.</summary>
    private sealed class PublicGmProbeModule(bool declared) : IGameModule, IGmApiModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "probe",
            Routes = declared ? [PublicProbe] : [],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }

        public void MapGmApi(IEndpointRouteBuilder routes)
        {
            var builder = declared
                ? routes.MapDeclared(PublicProbe, () => Results.Ok())
                : routes.MapGet(Template, () => Results.Ok());
            builder.AsPublicEndpoint(protobuf: false);
        }
    }
}
