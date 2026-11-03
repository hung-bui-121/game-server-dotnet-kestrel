using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Outbox;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-003, FR-013, FR-014, FR-021, FR-022 (slice 015 T021): the config phase is the first block of UseGameServer.</summary>
public sealed class ConfigStartupPhaseTests
{
    private const string GateLine = "[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).";

    private const string RuleLine = "[module phase] config counter.json: 'displayName' must be an integer, found \"Test counter\". Fix: set 'displayName' to an integer.";

    [Fact]
    public async Task A_rule_error_and_an_unrelated_violation_are_in_one_report()
    {
        var ex = await StartupFails(new PhaseModule(PhaseModule.IntegerDisplayName), s => s["GameServer:Gate:CacheSeconds"] = "99");

        ex.Violations.ShouldBe([RuleLine, GateLine]);
    }

    [Fact]
    public async Task A_warning_is_logged_once_and_the_host_starts()
    {
        await using var host = await SampleTestHost.StartAsync(Modules(new PhaseModule(PhaseModule.Warn)));

        host.LogEntries.Where(e => e.Category == GameServerApplicationExtensions.StartupLogCategory && e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .ShouldBe(["[module phase] config counter.json: the display name is long. Fix: shorten it."]);
        host.Services.GetRequiredService<PhaseCatalog>().ShouldNotBeNull();
    }

    [Fact]
    public async Task A_config_that_does_not_load_gives_the_loader_lines_only()
    {
        using var seeded = new SeededConfig().Without("counter.json");

        var ex = await StartupFails(new PhaseModule(PhaseModule.IntegerDisplayName), seeded.Apply);

        ex.Violations.ShouldBe(
            [$"[config] file '{Path.Combine(seeded.BasePath, "counter.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest."]);
    }

    [Fact]
    public async Task An_outbox_consumer_taking_a_failed_catalog_adds_no_line()
    {
        var ex = await StartupFails(
            new PhaseModule(PhaseModule.IntegerDisplayName),
            services: s => s.AddSingleton<IOutboxConsumer>(sp => new CatalogConsumer(sp.GetRequiredService<PhaseCatalog>())));

        ex.Violations.ShouldBe([RuleLine]);
    }

    [Fact]
    public async Task An_outbox_consumer_taking_a_built_catalog_gets_the_instance_of_the_phase()
    {
        CatalogConsumer? consumer = null;
        await using var host = await SampleTestHost.StartAsync(
            Modules(new PhaseModule(null)),
            services: s => s.AddSingleton<IOutboxConsumer>(sp => consumer = new CatalogConsumer(sp.GetRequiredService<PhaseCatalog>())));

        consumer.ShouldNotBeNull();
        consumer.Catalog.ShouldBeSameAs(host.Services.GetRequiredService<PhaseCatalog>());
    }

    private static async Task<GameServerStartupException> StartupFails(
        IGameModule module, Action<Dictionary<string, string?>>? settings = null, Action<IServiceCollection>? services = null) =>
        await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(Modules(module), settings, services: services);
        });

    // The defaults first: a modules callback replaces them (014 trap).
    private static Action<GameServerBuilder> Modules(IGameModule module) =>
        gs =>
        {
            SampleTestHost.DefaultModules(gs);
            gs.AddModule(module);
        };

    private sealed class PhaseCatalog;

    private sealed class CatalogConsumer(PhaseCatalog catalog) : IOutboxConsumer
    {
        public PhaseCatalog Catalog { get; } = catalog;

        public string Kind => "phase.test";

        public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PhaseModule(ConfigRule? rule) : IGameModule
    {
        public static readonly ConfigRule IntegerDisplayName =
            new("phase.integer", c => ConfigJson.RequiredInteger(c.Json(c.File!), "displayName", ConfigLocation.WholeFile, c.Problems));

        public static readonly ConfigRule Warn =
            new("phase.warn", c => c.Problems.Warning(ConfigLocation.WholeFile, "the display name is long", "shorten it"));

        public ModuleDescriptor Describe() => new()
        {
            Name = "phase",
            ConfigFiles = rule is null ? [] : [ConfigFileDeclaration.Required("counter.json", rule)],
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(_ => new PhaseCatalog(), "counter.json")],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
