using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-030..FR-033, SC-003 (slice 015 T032): startup checks on a real host keep one complete report and read config safely.</summary>
public sealed class StartupCheckHostTests
{
    private const string GateLine = "[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).";

    private const string RuleLine = "[module checks] config counter.json: the counter is refused. Fix: accept it.";

    [Fact]
    public async Task Four_checks_and_a_config_that_does_not_load_give_exactly_the_six_lines()
    {
        using var seeded = new SeededConfig().Without("battle.json");

        var ex = await StartupFails(
            settings: s =>
            {
                seeded.Apply(s);
                s["GameServer:Gate:CacheSeconds"] = "99";
            },
            services: s =>
            {
                s.AddSingleton<IStartupCheck, NeedsConfigCheck>();
                s.AddSingleton<IStartupCheck, ThrowingCheck>();
                s.AddSingleton<IStartupCheck, StatusCheck>();
                s.AddSingleton<IStartupCheck, PlainCheck>();
            });

        ex.Violations.ShouldBe([
            $"[config] file '{Path.Combine(seeded.BasePath, "battle.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.",
            GateLine,
            "[NeedsConfigCheck] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).",
            "[ThrowingCheck] the startup check could not be created: InvalidOperationException: boom. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).",
            "status: Failed",
            "plain ran",
        ]);
    }

    [Fact]
    public async Task With_a_valid_config_the_status_is_loaded_with_the_instance_handlers_get()
    {
        var probe = new StatusProbe();
        await using var host = await SampleTestHost.StartAsync(services: s => AddProbe(s, probe));

        probe.State.ShouldBe(GameConfigState.Loaded);
        probe.Config.ShouldBeSameAs(host.Services.GetRequiredService<IGameConfig>());
        probe.DeclarationsPassed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_rule_error_leaves_the_status_loaded_but_not_passed()
    {
        var probe = new StatusProbe();

        var ex = await StartupFails(Modules(new ChecksModule(refuse: true)), services: s => AddProbe(s, probe));

        ex.Violations.ShouldBe([RuleLine]);
        probe.State.ShouldBe(GameConfigState.Loaded);
        probe.Config.ShouldNotBeNull();
        probe.DeclarationsPassed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_warning_does_not_fail_the_declarations()
    {
        var probe = new StatusProbe();
        await using var host = await SampleTestHost.StartAsync(Modules(new ChecksModule(refuse: false, warn: true)), services: s => AddProbe(s, probe));

        probe.DeclarationsPassed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_check_taking_a_catalog_that_was_not_built_did_not_run()
    {
        var ex = await StartupFails(Modules(new ChecksModule(refuse: true)), services: s => s.AddSingleton<IStartupCheck, CatalogCheck>());

        ex.Violations.ShouldBe([
            RuleLine,
            "[CatalogCheck] the startup check did not run: catalog ChecksCatalog was not built (see the lines above). Fix: take GameConfigStatus and read the catalog with TryGetCatalog (FG-023).",
        ]);
    }

    /// <summary>The zh-pvp <c>GameConfigCheck</c> shape: resolves the config inside Check and catches; the report keeps 2 lines.</summary>
    [Fact]
    public async Task A_check_that_resolves_the_config_itself_and_catches_adds_nothing()
    {
        using var seeded = new SeededConfig().Without("battle.json");

        var ex = await StartupFails(
            settings: s =>
            {
                seeded.Apply(s);
                s["GameServer:Gate:CacheSeconds"] = "99";
            },
            services: s => s.AddSingleton<IStartupCheck, CatchingCheck>());

        ex.Violations.ShouldBe([
            $"[config] file '{Path.Combine(seeded.BasePath, "battle.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.",
            GateLine,
        ]);
    }

    [Fact]
    public async Task Checks_run_after_the_catalogs_are_built_and_the_status_is_published()
    {
        var probe = new OrderProbe();
        await using var host = await SampleTestHost.StartAsync(
            Modules(new ChecksModule(refuse: false)),
            services: s => s.AddSingleton<IStartupCheck>(sp => new OrderCheck(sp, probe)));

        probe.Status.ShouldBe(GameConfigState.Loaded);
        probe.Catalog.ShouldBeSameAs(host.Services.GetRequiredService<ChecksCatalog>());
    }

    [Fact]
    public async Task In_provision_mode_a_check_taking_a_declared_catalog_is_a_warning()
    {
        await using var app = SampleTestHost.BuildApp(
            Modules(new ChecksModule(refuse: false)),
            services: s =>
            {
                s.AddSingleton<IStartupCheck, CatalogCheck>();
                s.AddSingleton<IStoreProvisioner>(new NoopProvisioner());
            });
        var error = new StringWriter();

        var code = await ProvisioningRunner.RunAsync(app, new StringWriter(), error, CancellationToken.None);

        code.ShouldBe(HostExitCodes.Ok, error.ToString());
        error.ToString().ShouldBe(
            "warning: [CatalogCheck] the startup check did not run: provision mode does not load the game config. " +
            "Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023)." + Environment.NewLine);
    }

    // ---- slice 015 T071 G1: a check reads a declared catalog with GameConfigStatus.TryGetCatalog ----

    [Fact]
    public async Task A_check_gets_the_built_catalog_handlers_get_and_false_for_an_undeclared_type()
    {
        var probe = new CatalogProbe();
        await using var host = await SampleTestHost.StartAsync(
            Modules(new ChecksModule(refuse: false)), services: s => s.AddSingleton<IStartupCheck>(sp => new TryCatalogCheck(sp.GetRequiredService<GameConfigStatus>(), probe)));

        probe.Found.ShouldBeTrue();
        probe.Catalog.ShouldBeSameAs(host.Services.GetRequiredService<ChecksCatalog>());
        probe.UndeclaredFound.ShouldBeFalse();
    }

    [Fact]
    public async Task A_check_gets_the_instance_a_host_registered_for_a_declared_catalog()
    {
        var own = new ChecksCatalog();
        var probe = new CatalogProbe();
        await using var host = await SampleTestHost.StartAsync(
            Modules(new ChecksModule(refuse: false)),
            services: s =>
            {
                s.AddSingleton(own);
                s.AddSingleton<IStartupCheck>(sp => new TryCatalogCheck(sp.GetRequiredService<GameConfigStatus>(), probe));
            });

        probe.Found.ShouldBeTrue();
        probe.Catalog.ShouldBeSameAs(own);
    }

    [Fact]
    public async Task A_check_reading_a_failed_catalog_through_the_status_adds_no_line()
    {
        var probe = new CatalogProbe();

        var ex = await StartupFails(
            Modules(new ChecksModule(refuse: true)), services: s => s.AddSingleton<IStartupCheck>(sp => new TryCatalogCheck(sp.GetRequiredService<GameConfigStatus>(), probe)));

        ex.Violations.ShouldBe([RuleLine]);
        probe.Found.ShouldBeFalse();
        probe.Catalog.ShouldBeNull();
    }

    [Fact]
    public async Task In_provision_mode_try_get_catalog_is_false()
    {
        var probe = new CatalogProbe();
        await using var app = SampleTestHost.BuildApp(
            Modules(new ChecksModule(refuse: false)),
            services: s =>
            {
                s.AddSingleton<IStartupCheck>(sp => new TryCatalogCheck(sp.GetRequiredService<GameConfigStatus>(), probe));
                s.AddSingleton<IStoreProvisioner>(new NoopProvisioner());
            });

        var code = await ProvisioningRunner.RunAsync(app, new StringWriter(), new StringWriter(), CancellationToken.None);

        code.ShouldBe(HostExitCodes.Ok);
        probe.Ran.ShouldBeTrue();
        probe.Found.ShouldBeFalse();
    }

    private static void AddProbe(IServiceCollection services, StatusProbe probe)
    {
        services.AddSingleton(probe);
        services.AddSingleton<IStartupCheck, ProbingStatusCheck>();
    }

    private static async Task<GameServerStartupException> StartupFails(
        Action<GameServerBuilder>? modules = null, Action<Dictionary<string, string?>>? settings = null, Action<IServiceCollection>? services = null) =>
        await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(modules, settings, services: services);
        });

    // The defaults first: a modules callback replaces them (014 trap).
    private static Action<GameServerBuilder> Modules(IGameModule module) =>
        gs =>
        {
            SampleTestHost.DefaultModules(gs);
            gs.AddModule(module);
        };

    private sealed class ChecksCatalog;

    private sealed class ChecksModule(bool refuse, bool warn = false) : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "checks",
            ConfigFiles = [ConfigFileDeclaration.Required("counter.json", new ConfigRule("checks.counter", Check))],
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(_ => new ChecksCatalog(), "counter.json")],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }

        private void Check(ConfigContext context)
        {
            if (refuse)
            {
                context.Problems.Error(ConfigLocation.WholeFile, "the counter is refused", "accept it");
            }

            if (warn)
            {
                context.Problems.Warning(ConfigLocation.WholeFile, "the counter is odd", "look at it");
            }
        }
    }

    private sealed class CatalogProbe
    {
        public bool Ran { get; set; }

        public bool Found { get; set; }

        public ChecksCatalog? Catalog { get; set; }

        public bool UndeclaredFound { get; set; }
    }

    /// <summary>The recommended shape (G1): the status in the constructor, the catalog read with TryGetCatalog inside Check.</summary>
    private sealed class TryCatalogCheck(GameConfigStatus status, CatalogProbe probe) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            probe.Ran = true;
            probe.Found = status.TryGetCatalog<ChecksCatalog>(out var catalog);
            probe.Catalog = catalog;
            probe.UndeclaredFound = status.TryGetCatalog<StatusProbe>(out _);
        }
    }

    private sealed class StatusProbe
    {
        public GameConfigState? State { get; set; }

        public IGameConfig? Config { get; set; }

        public bool DeclarationsPassed { get; set; }
    }

    private sealed class OrderProbe
    {
        public GameConfigState? Status { get; set; }

        public ChecksCatalog? Catalog { get; set; }
    }

    private sealed class NeedsConfigCheck(IGameConfig config) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => result.Errors.Add("config " + config.ConfigVersion);
    }

    private sealed class ThrowingCheck : IStartupCheck
    {
        public ThrowingCheck() => throw new InvalidOperationException("boom");

        public void Check(StartupCheckResult result)
        {
        }
    }

    private sealed class StatusCheck(GameConfigStatus status) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => result.Errors.Add($"status: {status.State}");
    }

    private sealed class PlainCheck : IStartupCheck
    {
        public void Check(StartupCheckResult result) => result.Errors.Add("plain ran");
    }

    private sealed class ProbingStatusCheck(GameConfigStatus status, StatusProbe probe) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            probe.State = status.State;
            probe.Config = status.Config;
            probe.DeclarationsPassed = status.DeclarationsPassed;
        }
    }

    private sealed class CatalogCheck(ChecksCatalog catalog) : IStartupCheck
    {
        public void Check(StartupCheckResult result) => _ = catalog.ToString();
    }

    private sealed class CatchingCheck(IServiceProvider services) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            try
            {
                _ = services.GetRequiredService<IGameConfig>();
                result.Errors.Add("[catching] unexpected: the config loaded");
            }
            catch (GameServerStartupException)
            {
                // The host already reports the config faults.
            }
        }
    }

    private sealed class OrderCheck(IServiceProvider services, OrderProbe probe) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            probe.Status = services.GetRequiredService<GameConfigStatus>().State;
            probe.Catalog = services.GetRequiredService<ChecksCatalog>();
        }
    }

    private sealed class NoopProvisioner : IStoreProvisioner
    {
        public Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
