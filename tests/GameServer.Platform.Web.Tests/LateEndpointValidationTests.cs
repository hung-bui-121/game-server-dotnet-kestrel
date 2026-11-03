using System.Net;
using System.Net.Sockets;
using GameServer.Actors;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Commands;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.TestKit;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// FR-012, FR-013, BC-2 (US1-5, US1-6): endpoints mapped after <c>UseGameServer</c> get every endpoint check before the server
/// listens, with the same lines as endpoints mapped before it; declared routes that are not mapped are reported only here (N7).
/// </summary>
public sealed class LateEndpointValidationTests
{
    private static readonly RouteDeclaration LateOpen = RouteDeclaration.Get("late.open", "/v1/late/open", SecurityProfile.Public, RouteSurface.Host);
    private static readonly RouteDeclaration LateDev = RouteDeclaration.Post("late.dev", "/api/v1/late/dev", SecurityProfile.Dev);
    private static readonly RouteDeclaration LateGhost = new("late.ghost", ["POST"], "/api/v1/late/ghost", SecurityProfile.Player, RouteSurface.PlayerApi, "late.ghost");
    private static readonly RouteDeclaration Unmapped = RouteDeclaration.Post("probe.unmapped", "/api/v1/probe/unmapped", SecurityProfile.Player);

    private static async Task<IReadOnlyList<string>> LateViolations(Action<WebApplication> late, params RouteDeclaration[] routes)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(SampleTestHost.WithRoutes(routes), afterUseGameServer: late);
        });
        return ex.Violations;
    }

    private static async Task<IReadOnlyList<string>> EarlyViolations(Action<WebApplication> early, params RouteDeclaration[] routes)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(SampleTestHost.WithRoutes(routes), beforeUseGameServer: early);
        });
        return ex.Violations;
    }

    public static TheoryData<string> LateFaults() => ["undeclared", "unversioned", "duplicate", "public", "ghost"];

    private static (Action<WebApplication> Map, RouteDeclaration[] Routes) Fault(string fault) => fault switch
    {
        "undeclared" => (app => app.MapPost("/api/v1/late", () => "leak").AsPlayerEndpoint(), []),
        "unversioned" => (app => app.MapGet("/api/late", () => "x").AsPlayerEndpoint(), []),
        "duplicate" => (app => app.MapPost("/api/v1/counter/increment", () => "x").AsPlayerEndpoint(), []),
        "public" => (app => app.MapDeclared(LateOpen, () => "x").AsPublicEndpoint(protobuf: false), [LateOpen]),
        "ghost" => (
            app => app.MapPlayerCommand<Wire.IncrementRequest, IncrementCommand, IncrementResult>(
                LateGhost.Template,
                "late.ghost",
                request => new IncrementCommand(request.By, null),
                result => new Wire.IncrementResult { Value = result.Value }),
            [LateGhost]),
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };

    [Theory]
    [MemberData(nameof(LateFaults))]
    public async Task A_late_endpoint_gets_the_same_lines_as_one_mapped_before_use_game_server(string fault)
    {
        var (map, routes) = Fault(fault);

        var late = await LateViolations(map, routes);
        var early = await EarlyViolations(map, routes);

        late.ShouldNotBeEmpty();
        late.ShouldBe(early);
    }

    [Fact]
    public async Task An_undeclared_late_endpoint_without_profile_is_refused_at_startup()
    {
        // Was ErrorContractTests.Endpoint_added_after_validation_without_profile_is_denied (403 at request time): BC-2.
        (await LateViolations(app => app.MapGet("/api/v1/late", () => "leak"))).ShouldBe(
        [
            "[endpoint GET /api/v1/late] has 0 security profiles. Fix: declare exactly one with AsPlayerEndpoint/AsGmEndpoint/AsPublicEndpoint/AsDevEndpoint (SEC-08).",
            "[endpoint GET /api/v1/late] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes " +
            "(or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).",
        ]);
    }

    [Fact]
    public async Task A_late_dev_endpoint_outside_development_tools_is_refused()
    {
        var violations = await LateViolations(
            app =>
            {
                // UseGameServer ran in Development; the deployment environment seen by the pre-serving run is Staging.
                app.Services.GetRequiredService<GameServerOptions>().DeploymentEnvironment = "Staging";
                app.MapDeclared(LateDev, () => "x").AsDevEndpoint();
            },
            LateDev);

        // The GM OpenAPI document was mapped under Development; in the Staging the check now sees, its condition does not hold, so
        // it is reported once, as mapped against its condition (T086 minor 1; round 2 m4: one line per fault).
        violations.ShouldBe(
        [
            "[endpoint POST /api/v1/late/dev] uses profile Dev in deployment environment 'Staging' (environment id 'test'). " +
            "Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: " +
            "Development/Test, flavors local and dev) (OPS-09, SEC-08).",
            "[route platform.gm_openapi] is mapped but its condition 'development_tools' does not hold. " +
            "Fix: map it only when the condition holds, or correct the declaration's condition (FG-026).",
        ]);
    }

    [Fact]
    public async Task A_declared_route_of_a_mapped_module_that_is_not_mapped_is_reported_only_before_serving()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(gs =>
            {
                SampleTestHost.DefaultModules(gs);
                gs.AddModule(new CatalogProbeModule(routes: [Unmapped], card: false));
            });
        });

        ex.Violations.ShouldBe(
        [
            "[route probe.unmapped] (POST /api/v1/probe/unmapped, owner probe, surface PlayerApi) is declared but not mapped. " +
            "Fix: map it in the module's MapPlayerApi method or remove the declaration (FG-026).",
        ]);
    }

    [Fact]
    public async Task A_declared_late_endpoint_starts()
    {
        var route = RouteDeclaration.Post("late.ok", "/api/v1/late/ok", SecurityProfile.Player);

        await using var host = await SampleTestHost.StartAsync(SampleTestHost.WithRoutes(route), afterUseGameServer: app => app.MapDeclared(route, () => "ok").AsPlayerEndpoint());

        (await host.PlayerClient("p1").PostAsync(new Uri(route.Template, UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_real_host_exits_1_with_the_report_on_stderr_and_never_binds_its_port()
    {
        var port = FreePort();
        await using var app = KestrelApp(port, gs =>
        {
            SampleTestHost.DefaultModules(gs);
            gs.AddModule(new CatalogProbeModule(routes: [Unmapped], card: false));
        });
        var error = new StringWriter();

        var code = await GameServerRunExtensions.ServeAsync(app, error);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain("[route probe.unmapped] (POST /api/v1/probe/unmapped, owner probe, surface PlayerApi) is declared but not mapped.");
        using var listener = new TcpListener(IPAddress.Loopback, port);
        Should.NotThrow(() => listener.Start());
        listener.Stop();
    }

    /// <summary>
    /// T071 round 2 m-5: when <c>UseGameServer</c> throws, <c>RunAsync</c> never runs, so the entry point disposes the app itself:
    /// printed (exit 1) or rethrown under the kit's marker, every singleton the start created is disposed before the caller resumes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_start_disposes_the_app(bool kit)
    {
        var probe = new DisposalProbe();
        // A registry line of UseGameServer itself (the unmapped-route line comes later, from RunAsync, which disposes on its own).
        await using var app = KestrelApp(
            FreePort(),
            SampleTestHost.DefaultModules,
            settings: s => s["GameServer:Gate:CacheSeconds"] = "99",
            services: services =>
            {
                services.AddSingleton(_ => probe);
                if (kit)
                {
                    services.AddSingleton(StartupFailureMode.Throw);
                }
            });
        app.Services.GetRequiredService<DisposalProbe>().ShouldBeSameAs(probe);
        var error = new StringWriter();

        if (kit)
        {
            await Should.ThrowAsync<GameServerStartupException>(() => GameServerRunExtensions.ServeAsync(app, error));
            error.ToString().ShouldBeEmpty();
        }
        else
        {
            (await GameServerRunExtensions.ServeAsync(app, error)).ShouldBe(HostExitCodes.InvalidConfiguration);
            error.ToString().ShouldContain("[gate] GameServer:Gate:CacheSeconds must be 0..10");
        }

        probe.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_web_application_factory_start_fails_on_a_late_fault_and_starts_without_it()
    {
        await using (var healthy = new LocalFlavorFactory<Program>())
        {
            Should.NotThrow(() => healthy.CreateClient().Dispose());
        }

        // The SSO routes are declared with condition gm.sso; forcing it to hold while Gm:Oidc is not configured leaves them unmapped.
        var broken = new LocalFlavorFactory<Program>(new LocalFlavorOptions
        {
            Services = s => s.AddRouteCondition("gm.sso", _ => true),
        });
        GameServerStartupException ex;
        try
        {
            ex = Should.Throw<GameServerStartupException>(() => broken.CreateClient());
        }
        finally
        {
            // The entry point disposed its host after the failed start; the factory's own dispose must still not throw (G3).
            await broken.DisposeAsync();
        }

        ex.Violations.ShouldContain(v => v.StartsWith("[route gm.sso_", StringComparison.Ordinal) && v.Contains("is declared but not mapped", StringComparison.Ordinal));
        Directory.Exists(broken.Root).ShouldBeFalse();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>A host on Kestrel (not TestServer) at a fixed loopback port, composed like the kit's test host.</summary>
    private static WebApplication KestrelApp(
        int port, Action<GameServerBuilder> modules, Action<Dictionary<string, string?>>? settings = null, Action<IServiceCollection>? services = null)
    {
        var url = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = AppContext.BaseDirectory });
        var values = SampleTestHost.DefaultSettings();
        values["Urls"] = url;
        settings?.Invoke(values);
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        var gameServer = builder.AddGameServer();
        builder.Services.AddInMemoryDocumentStore();
        gameServer.UseTestAuthentication();
        modules(gameServer);
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>A singleton the container disposes with the app (it creates it through a factory), recording that it did.</summary>
    private sealed class DisposalProbe : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
