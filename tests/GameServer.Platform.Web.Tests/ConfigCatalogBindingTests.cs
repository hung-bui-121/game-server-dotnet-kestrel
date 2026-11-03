using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Config;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-021, BC-8 (slice 015 T020): who provides a declared catalog, decided from the final registrations (no UseGameServer).</summary>
public sealed class ConfigCatalogBindingTests
{
    [Fact]
    public async Task Only_the_module_declares_the_catalog_so_the_platform_builds_it()
    {
        await using var app = Build(gs => gs.AddModule(new CatalogModule()));

        Binding(app).IsDeclared.ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_instance_registered_before_the_module_is_provided_and_resolved()
    {
        var own = new SampleCatalog("host");
        await using var app = Build(gs =>
        {
            gs.Services.AddSingleton(own);
            gs.AddModule(new CatalogModule());
        });

        Binding(app).IsDeclared.ShouldBeFalse();
        Binding(app).Resolve!().ShouldBeSameAs(own);
        app.Services.GetRequiredService<SampleCatalog>().ShouldBeSameAs(own);
    }

    [Fact]
    public async Task A_host_instance_registered_after_the_module_is_provided_and_resolved()
    {
        var own = new SampleCatalog("host");
        await using var app = Build(gs => gs.AddModule(new CatalogModule()), services => services.AddSingleton(own));

        Binding(app).IsDeclared.ShouldBeFalse();
        app.Services.GetRequiredService<SampleCatalog>().ShouldBeSameAs(own);
    }

    [Fact]
    public async Task Resolving_a_declared_catalog_before_the_config_phase_names_the_fix()
    {
        await using var app = Build(gs => gs.AddModule(new CatalogModule()));

        Should.Throw<InvalidOperationException>(() => app.Services.GetRequiredService<SampleCatalog>()).Message.ShouldBe(
            "SampleCatalog is a declared config catalog: the platform builds it in UseGameServer. Fix: start the host with " +
            "UseGameServer/RunGameServerAsync/TestHost, or in a test without a host build it with ConfigDeclarationCheck.Check(config, modules) " +
            "and call report.AddCatalogsTo(services) (FG-018).");
    }

    [Fact]
    public void Services_built_from_the_module_alone_do_not_register_the_catalog()
    {
        var services = new ServiceCollection();
        new CatalogModule().ConfigureServices(services, new ModuleContext("test", "test", "Development"));
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<SampleCatalog>())
            .Message.ShouldContain($"No service for type '{typeof(SampleCatalog).FullName}' has been registered");
    }

    private static WebApplication Build(Action<GameServerBuilder> modules, Action<IServiceCollection>? services = null) =>
        SampleTestHost.BuildApp(
            gs =>
            {
                SampleTestHost.DefaultModules(gs);
                modules(gs);
            },
            services: services);

    private static CatalogBinding Binding(WebApplication app) =>
        app.Services.GetRequiredService<DeclaredCatalogBindings>().For(typeof(SampleCatalog), app.Services);

    private sealed record SampleCatalog(string Name);

    private sealed class CatalogModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "catalogs",
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(_ => new SampleCatalog("built"), "counter.json")],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
