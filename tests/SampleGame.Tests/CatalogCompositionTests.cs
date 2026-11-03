using System.Globalization;
using System.Reflection;
using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Web.Catalog;
using GameServer.Platform.Web.OpenApi;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sample.Counter.Adapters;
using SampleGame.Adapters;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// FR-001, FR-002, FR-011, FR-013, FR-014 (US1-1, US1-2, US1-5): the endpoint check over the real sample host composition in every
/// flavor and hosting variant: the host starts, every mapped route endpoint is in the catalog, nothing declared is left unmapped.
/// </summary>
public sealed class CatalogCompositionTests
{
    /// <summary>data-model §1.3: every route the framework can map.</summary>
    private static readonly string[] FrameworkRouteNames =
    [
        "cheat.grant", "economy.adjust", "economy.ledger",
        "gm.auth_csrf", "gm.auth_login", "gm.auth_logout", "gm.health", "gm.me", "gm.ops_gate", "gm.ops_gate_maintenance",
        "gm.ops_gate_min_client_version", "gm.ops_rotate_keys", "gm.ops_version", "gm.player", "gm.player_audit", "gm.player_by_mapping",
        "gm.player_receipts", "gm.player_search", "gm.receipt", "gm.sso_callback", "gm.sso_start", "gm.staff", "gm.staff_audit",
        "gm.staff_create", "gm.staff_disable", "gm.staff_enable", "gm.staff_grant", "gm.staff_list", "gm.staff_revoke_sessions", "gm.ui",
        "iap.purchase",
        "identity.ban", "identity.cancel_deletion", "identity.link", "identity.login_guest", "identity.login_provider", "identity.logout",
        "identity.request_deletion", "identity.set_display_name", "identity.unban",
        "platform.config", "platform.gm_openapi", "platform.health_live", "platform.health_ready", "platform.receipt", "platform.snapshot",
        "platform.time",
    ];

    public static TheoryData<string, bool, bool> Matrix()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var flavor in new[] { "Local", "Dev", "Beta", "Uat", "Live" })
        {
            foreach (var player in new[] { true, false })
            {
                foreach (var gm in new[] { true, false })
                {
                    data.Add(flavor, player, gm);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Every_flavor_and_hosting_variant_starts_with_every_mapped_route_declared(string flavor, bool player, bool gm)
    {
        await using var factory = Factory(flavor, player, gm);
        factory.CreateClient().Dispose();

        var catalog = factory.Services.GetRequiredService<WireCatalog>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        foreach (var endpoint in endpoints)
        {
            var template = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            methods.ShouldAllBe(method => catalog.FindRouteName(method, template) != null, $"{string.Join(",", methods)} {template} in {flavor}");
        }

        var developmentTools = factory.Services.GetRequiredService<GameServerOptions>().AllowsDevelopmentTools;
        endpoints.Any(e => e.RoutePattern.RawText == GmOpenApi.Route).ShouldBe(developmentTools && gm);
    }

    [Fact]
    public async Task The_framework_entries_of_the_local_composition_are_every_framework_route()
    {
        await using var factory = Factory("Local", player: true, gm: true);

        var names = factory.Services.GetRequiredService<WireCatalog>().Routes
            .Where(r => r.Origin == CatalogOrigin.Framework)
            .Select(r => r.Declaration.Name)
            .ToArray();

        names.Except(FrameworkRouteNames).ShouldBeEmpty("framework routes missing from data-model §1.3");
        FrameworkRouteNames.Except(names).ShouldBeEmpty("framework routes not declared by the SampleHost local composition");
        names.Length.ShouldBe(FrameworkRouteNames.Length);
    }

    [Fact]
    public async Task A_service_only_module_keeps_its_declarations_without_mapping_them()
    {
        await using var host = await SampleTestHost.StartAsync(gs =>
        {
            gs.AddModule<IdentityModule>().AddModule<EconomyModule>().AddModule<IapModule>().AddModule<CounterModule>().AddModule<GmModule>();
            gs.AddModule<SampleGameModule>(o => o.ServiceOnly());
        });

        var catalog = host.Services.GetRequiredService<WireCatalog>();
        catalog.TryGetRoute(SampleGameRoutes.BattleStart.Name, out var start).ShouldBeTrue();
        start!.ServiceOnly.ShouldBeTrue();
        host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .ShouldNotContain(e => e.RoutePattern.RawText == SampleGameRoutes.StartTemplate);
    }

    [Fact]
    public void No_configuration_key_turns_the_check_off()
    {
        static IEnumerable<string> Keys(Type type, string prefix, int depth) =>
            depth > 4
                ? []
                : type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite || (p.PropertyType.IsClass && p.PropertyType != typeof(string)))
                    .SelectMany(p => new[] { prefix + p.Name }.Concat(
                        p.PropertyType.IsClass && p.PropertyType.Namespace?.StartsWith("GameServer", StringComparison.Ordinal) == true
                            ? Keys(p.PropertyType, prefix + p.Name + ":", depth + 1)
                            : []));

        Keys(typeof(GameServerOptions), "GameServer:", 0)
            .Where(key => key.Contains("Catalog", StringComparison.OrdinalIgnoreCase) || key.Contains("EndpointCheck", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The sample host's own entry point in <paramref name="flavor"/>. Couchbase flavors run on the in-memory store; Staging and
    /// Production use <see cref="StagingInMemoryStore"/> and generated secrets (no real credential is read).
    /// </summary>
    private static LocalFlavorFactory<Program> Factory(string flavor, bool player, bool gm)
    {
        var options = new LocalFlavorOptions { Environment = flavor };
        options.Settings["GameServer:Hosting:MapPlayerApi"] = player.ToString(CultureInfo.InvariantCulture);
        options.Settings["GameServer:Hosting:MapGmApi"] = gm.ToString(CultureInfo.InvariantCulture);
        options.Settings["Observability:Logging:Console:Enabled"] = "false";
        if (flavor != "Local")
        {
            options.Settings["GameServer:Store"] = "InMemory";
        }

        if (flavor is "Beta" or "Uat" or "Live")
        {
            foreach (var (key, value) in StagingSecrets.Create())
            {
                options.Settings[key] = value;
            }

            options.Services = StagingInMemoryStore.Replace;
        }

        return new LocalFlavorFactory<Program>(options);
    }
}
