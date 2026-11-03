using GameServer.Actors;
using GameServer.Modules.Identity;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Aggregates;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Routing;
using GameServer.Routing;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sample.Counter;
using Shouldly;
using Wire = Sample.Counter.Protocol.V1;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// FR-006, FR-007, FR-010, FR-013, FR-054 (US1-3, US1-4, US1-7): every fault class stops the host with exactly one report line,
/// in every deployment environment; <c>--provision</c> checks the declarations too.
/// </summary>
public sealed class EndpointCatalogCheckTests
{
    private const string NotDeclared =
        "is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes " +
        "(or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).";

    private const string KeyedCondition = "probe.keyed";
    private const string KeyMissing = "[probe] Probe:Key is not set. Fix: set it in the configuration.";

    private static async Task<IReadOnlyList<string>> Violations(
        Action<GameServerBuilder>? extra = null,
        Action<WebApplication>? endpoints = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(
                gs =>
                {
                    SampleTestHost.DefaultModules(gs);
                    extra?.Invoke(gs);
                },
                settings,
                endpoints,
                services: services);
        });
        return ex.Violations;
    }

    [Fact]
    public async Task An_undeclared_player_endpoint_is_one_line()
    {
        (await Violations(endpoints: app => app.MapPost("/api/v1/probe/x", () => "x").AsPlayerEndpoint()))
            .ShouldBe([$"[endpoint POST /api/v1/probe/x] {NotDeclared}"]);
    }

    [Fact]
    public async Task An_undeclared_gm_endpoint_is_one_line()
    {
        (await Violations(endpoints: app => app.MapGet("/gm/v1/probe", () => "x").AsGmEndpoint(CounterModuleDefinition.ReadPermission)))
            .ShouldBe([$"[endpoint GET /gm/v1/probe] {NotDeclared}"]);
    }

    [Fact]
    public async Task An_undeclared_host_endpoint_is_one_line()
    {
        (await Violations(
                endpoints: app => app.MapGet("/v1/probe/host", () => "x").AsPublicEndpoint(protobuf: false),
                settings: s => s["GameServer:PublicEndpointAllowlist:1"] = "/v1/probe/host"))
            .ShouldBe([$"[endpoint GET /v1/probe/host] {NotDeclared}"]);
    }

    // T086 B1 (found in T082): mapped from a declaration that no ModuleDescriptor.Routes lists, the endpoint used to pass the check
    // while the export (and so the generated client routes) silently lacked it.
    [Fact]
    public async Task An_endpoint_mapped_from_a_declaration_no_module_lists_is_one_line()
    {
        var route = RouteDeclaration.Post("probe.unlisted", "/api/v1/probe/unlisted", SecurityProfile.Player);

        (await Violations(gs => gs.AddModule(new CatalogProbeModule(routes: [], card: false)), app => app.MapDeclared(route, () => "x").AsPlayerEndpoint()))
            .ShouldBe([$"[endpoint POST /api/v1/probe/unlisted] {NotDeclared}"]);
    }

    [Fact]
    public async Task The_export_of_that_host_does_not_list_the_route()
    {
        var route = RouteDeclaration.Post("probe.unlisted", "/api/v1/probe/unlisted", SecurityProfile.Player);
        var file = Path.Combine(Path.GetTempPath(), "gs-unlisted-" + Guid.NewGuid().ToString("N") + ".json");
        var app = SampleTestHost.BuildApp(
            gs =>
            {
                SampleTestHost.DefaultModules(gs);
                gs.AddModule(new CatalogProbeModule(routes: [], card: false));
            },
            s => s[GameServerCommandLine.ExportPathKey] = file);
        app.MapDeclared(route, () => "x").AsPlayerEndpoint();
        try
        {
            (await GameServerRunExtensions.ExportAsync(app, TextWriter.Null, TextWriter.Null)).ShouldBe(HostExitCodes.Ok);
            (await File.ReadAllTextAsync(file)).ShouldNotContain(route.Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_endpoint_mapped_from_a_copy_of_the_listed_declaration_names_the_owner()
    {
        var listed = RouteDeclaration.Post("probe.save", "/api/v1/probe/save", SecurityProfile.Player);
        var copy = RouteDeclaration.Post("probe.save", "/api/v1/probe/save", SecurityProfile.Player);

        (await Violations(gs => gs.AddModule(new CatalogProbeModule(routes: [listed], card: false)), app => app.MapDeclared(copy, () => "x").AsPlayerEndpoint()))
            .ShouldBe(
            [
                "[endpoint POST /api/v1/probe/save] is mapped from a declaration named probe.save that is not the one probe lists. " +
                "Fix: map it from the declaration instance listed in ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes), not a copy (FG-026).",
                "[route probe.save] (POST /api/v1/probe/save, owner probe, surface PlayerApi) is declared but not mapped. " +
                "Fix: map it in the module's MapPlayerApi method or remove the declaration (FG-026).",
            ]);
    }

    // T086 minor 1 (round 2 m4): a Public route mapped while its condition does not hold is one fault, so one line (data-model
    // §2.2); it stays a public template, so no second "add it to the allowlist" line.
    [Fact]
    public async Task A_public_route_mapped_while_its_condition_does_not_hold_is_one_line()
    {
        var route = RouteDeclaration.Get("probe.open", "/v1/probe/open", SecurityProfile.Public, RouteSurface.Host, condition: "probe.off");

        (await Violations(
                gs => gs.AddModule(new CatalogProbeModule(routes: [route], card: false)),
                app => app.MapDeclared(route, () => "x").AsPublicEndpoint(protobuf: false),
                services: s => s.AddRouteCondition("probe.off", _ => false)))
            .ShouldBe(
            [
                "[route probe.open] is mapped but its condition 'probe.off' does not hold. " +
                "Fix: map it only when the condition holds, or correct the declaration's condition (FG-026).",
            ]);
    }

    // T086 round 2 m1: options with Validate() but no ValidateOnStart are validated nowhere else, so a condition reading them used
    // to be silently false and the host started with the options invalid.
    [Fact]
    public async Task A_condition_reading_options_without_validate_on_start_fails_startup_with_the_options_line()
    {
        var route = RouteDeclaration.Post("probe.keyed", "/api/v1/probe/keyed", SecurityProfile.Player, condition: KeyedCondition);

        (await Violations(
                gs => gs.AddModule(new CatalogProbeModule(routes: [route], card: false)),
                services: s => AddKeyedCondition(s, validateOnStart: false)))
            .ShouldBe([KeyMissing]);
    }

    [Fact]
    public async Task A_condition_reading_options_validated_on_start_reports_the_options_line_once()
    {
        var route = RouteDeclaration.Post("probe.keyed", "/api/v1/probe/keyed", SecurityProfile.Player, condition: KeyedCondition);

        (await Violations(
                gs => gs.AddModule(new CatalogProbeModule(routes: [route], card: false)),
                app => app.MapDeclared(route, () => "x").AsPlayerEndpoint(),
                services: s => AddKeyedCondition(s, validateOnStart: true)))
            .ShouldBe(
            [
                KeyMissing,
                $"[route probe.keyed] is mapped but its condition '{KeyedCondition}' does not hold. " +
                "Fix: map it only when the condition holds, or correct the declaration's condition (FG-026).",
            ]);
    }

    [Fact]
    public async Task A_method_mismatch_is_one_line()
    {
        (await Violations(endpoints: app => app.MapDelete("/api/v1/counter/get", () => "x").AsPlayerEndpoint())).ShouldBe(
        [
            "[endpoint DELETE /api/v1/counter/get] maps method DELETE but route counter.get declares POST. " +
            "Fix: declare one route per method or correct the mapping (FG-026).",
        ]);
    }

    [Fact]
    public async Task A_profile_mismatch_is_one_line()
    {
        var route = RouteDeclaration.Post("probe.player", "/api/v1/probe/player", SecurityProfile.Player);

        (await Violations(gs => gs.DeclareRoutes(route), app => app.MapDeclared(route, () => "x").AsDevEndpoint())).ShouldBe(
        [
            "[endpoint POST /api/v1/probe/player] has profile Dev but route probe.player declares Player. " +
            "Fix: use the As*Endpoint helper of the declared profile or correct the declaration (SEC-08).",
        ]);
    }

    [Fact]
    public async Task An_operation_mismatch_is_one_line()
    {
        var route = RouteDeclaration.Command(IncrementHandler.Descriptor, "/api/v1/probe/increment", name: "probe.increment");

        (await Violations(gs => gs.DeclareRoutes(route), app => app.MapDeclared(route, () => "x").AsPlayerEndpoint())).ShouldBe(
        [
            "[endpoint POST /api/v1/probe/increment] runs command '' but route probe.increment declares 'counter.increment'. " +
            "Fix: map it with MapPlayerCommand/MapDevCommand/MapGmCommand(declaration, …) (ARC-07).",
        ]);
    }

    [Fact]
    public async Task A_permission_mismatch_is_one_line()
    {
        var route = RouteDeclaration.Get("probe.gm_read", "/gm/v1/probe/read", SecurityProfile.Gm, RouteSurface.GmApi, CounterModuleDefinition.ReadPermission);

        (await Violations(gs => gs.DeclareRoutes(route), app => app.MapDeclared(route, () => "x").AsGmEndpoint(IdentityPermissions.PlayerBan))).ShouldBe(
        [
            "[endpoint GET /gm/v1/probe/read] requires permission 'player.ban' but route probe.gm_read declares 'counter.read'. " +
            "Fix: map it with the declaration (SEC-07).",
        ]);
    }

    [Fact]
    public async Task A_duplicate_route_name_is_one_line()
    {
        (await Violations(gs =>
            {
                gs.AddModule(new CatalogProbeModule(routes: [RouteDeclaration.Post("counter.get", "/api/v1/probe/get", SecurityProfile.Player)], card: false));
            }))
            .ShouldBe(["[route counter.get] is declared by sample.counter and probe. Fix: give each route a unique name (FG-026)."]);
    }

    [Fact]
    public async Task A_duplicate_method_and_template_names_both_owners()
    {
        (await Violations(gs => gs.DeclareRoutes(RouteDeclaration.Post("host.counter_get", "/api/v1/counter/get", SecurityProfile.Player))))
            .ShouldBe(
            [
                "[route POST /api/v1/counter/get] is declared twice: counter.get (sample.counter) and host.counter_get (host). " +
                "Fix: declare it once (ARC-07).",
            ]);
    }

    [Fact]
    public async Task A_public_endpoint_list_entry_declared_with_another_profile_is_one_line()
    {
        var route = RouteDeclaration.Post("probe.save", "/api/v1/probe/save", SecurityProfile.Player);

        (await Violations(
                gs => gs.AddModule(new CatalogProbeModule(routes: [route], publicEndpoints: [route.Template], card: false)),
                app => app.MapDeclared(route, () => "x").AsPlayerEndpoint()))
            .ShouldBe(
            [
                "[module probe] lists /api/v1/probe/save in PublicEndpoints but route probe.save declares profile Player. " +
                "Fix: declare it Public or remove it from PublicEndpoints (SEC-09).",
            ]);
    }

    [Fact]
    public async Task An_unknown_hosting_condition_is_one_line()
    {
        var route = RouteDeclaration.Post("probe.flag", "/api/v1/probe/flag", SecurityProfile.Player, condition: "probe.nope");

        (await Violations(gs => gs.AddModule(new CatalogProbeModule(routes: [route], card: false)), app => app.MapDeclared(route, () => "x").AsPlayerEndpoint()))
            .ShouldBe(
            [
                "[route probe.flag] uses hosting condition 'probe.nope' which is not registered. " +
                "Fix: services.AddRouteCondition(\"probe.nope\", …) in the module (FG-026).",
            ]);
    }

    [Fact]
    public async Task Two_wire_mappers_for_one_aggregate_are_one_line()
    {
        (await Violations(services: s => s.AddAggregateWireMapper<CounterState, Wire.CounterState>(CounterAggregates.Counter, state => new Wire.CounterState { Value = state.Value })))
            .ShouldBe(
            [
                "[aggregate counter] has 2 wire mappers. Fix: register one mapper per aggregate; replace a framework mapper with " +
                "ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).",
            ]);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Staging_and_production_fail_the_same_way(string environment)
    {
        var violations = await Violations(
            endpoints: app => app.MapPost("/api/v1/probe/x", () => "x").AsPlayerEndpoint(),
            settings: s => s["GameServer:DeploymentEnvironment"] = environment);

        violations.ShouldContain($"[endpoint POST /api/v1/probe/x] {NotDeclared}");
    }

    [Fact]
    public async Task Provision_mode_checks_the_declarations_and_never_calls_the_provisioner()
    {
        var provisioner = new CountingProvisioner();
        await using var app = SampleTestHost.BuildApp(
            gs =>
            {
                SampleTestHost.DefaultModules(gs);
                gs.DeclareRoutes(RouteDeclaration.Post("counter.get", "/v1/hooks/other", SecurityProfile.Public, RouteSurface.Host));
            },
            testAuthentication: false,
            services: s => s.AddSingleton<IStoreProvisioner>(provisioner));
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProvisioningRunner.RunAsync(app, output, error, CancellationToken.None);

        code.ShouldBe(HostExitCodes.InvalidConfiguration);
        error.ToString().ShouldContain("[route counter.get] is declared by sample.counter and host.");
        provisioner.Calls.ShouldBe(0);
    }

    /// <summary>A hosting condition reading <see cref="ProbeKeyOptions"/>, which fail validation unless <c>Probe:Key</c> is set.</summary>
    private static void AddKeyedCondition(IServiceCollection services, bool validateOnStart)
    {
        var options = services.AddOptions<ProbeKeyOptions>().Validate(o => o.Key is not null, KeyMissing);
        if (validateOnStart)
        {
            options.ValidateOnStart();
        }

        services.AddRouteCondition(KeyedCondition, sp => sp.GetRequiredService<IOptions<ProbeKeyOptions>>().Value.Key is not null);
    }

    private sealed class ProbeKeyOptions
    {
        public string? Key { get; set; }
    }

    private sealed class CountingProvisioner : IStoreProvisioner
    {
        public int Calls { get; private set; }

        public Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
