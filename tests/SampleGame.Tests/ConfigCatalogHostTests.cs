using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GameServer.Commands;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Platform;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>FR-021, FR-023, SC-002, US1-4 (slice 015 T027): declared catalogs are built once, before traffic, and shared.</summary>
public sealed class ConfigCatalogHostTests
{
    [Fact]
    public async Task Every_declared_catalog_is_built_once_before_the_first_request_and_shared_by_handlers()
    {
        var counting = new CountingCatalogModule();
        var seen = new SeenCatalogs();
        await using var host = await GameHttp.StartGameAsync(extra: gs =>
        {
            gs.AddModule(counting);
            Spy(gs, seen);
        });

        counting.Builds.ShouldBe(1);
        var resources = host.Services.GetRequiredService<ResourceCatalog>();
        var (player, _) = await host.NewPlayerAsync();
        (await player.CheatAsync("gold", 10)).Code.ShouldBe("OK");
        (await player.CheatAsync("gold", 20)).Code.ShouldBe("OK");

        counting.Builds.ShouldBe(1);
        host.Services.GetRequiredService<CountingCatalog>().ShouldBeSameAs(host.Services.GetRequiredService<CountingCatalog>());
        seen.Catalogs.Count.ShouldBe(2);
        seen.Catalogs.ShouldAllBe(c => ReferenceEquals(c, resources));
    }

    [Fact]
    public async Task The_first_command_reads_no_config_file()
    {
        var config = new CountingConfig(TestGameConfig.FromDirectory(Path.Combine(AppContext.BaseDirectory, "config")));
        await using var host = await SampleTestHost.StartAsync(GameHttp.FullGame, config: config);
        var (player, _) = await host.NewPlayerAsync();
        config.Reads.ShouldBeGreaterThan(0);

        config.Reset();
        (await player.CheatAsync("gold", 100)).Code.ShouldBe("OK");
        (await player.BuyAsync("potion_pack", 1)).Code.ShouldBe("OK");

        config.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task A_host_instance_registered_before_the_module_is_the_one_handlers_get()
    {
        var own = OwnCatalog();
        var seen = new SeenCatalogs();
        await using var host = await SampleTestHost.StartAsync(gs =>
        {
            gs.Services.AddSingleton(own);
            GameHttp.FullGame(gs);
            Spy(gs, seen);
        });

        await GrantAsync(host);

        seen.Catalogs.ShouldHaveSingleItem().ShouldBeSameAs(own);
    }

    [Fact]
    public async Task A_host_instance_registered_after_the_module_is_the_one_handlers_get()
    {
        var own = OwnCatalog();
        var seen = new SeenCatalogs();
        await using var host = await SampleTestHost.StartAsync(gs => { GameHttp.FullGame(gs); Spy(gs, seen); }, services: s => s.AddSingleton(own));

        await GrantAsync(host);

        seen.Catalogs.ShouldHaveSingleItem().ShouldBeSameAs(own);
    }

    [Fact]
    public async Task A_throwing_host_factory_gives_the_host_line()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(
                GameHttp.FullGame, services: s => s.AddSingleton<ResourceCatalog>(_ => throw new InvalidOperationException("no file")));
        });

        ex.Violations.ShouldBe(
        [
            "[catalog ResourceCatalog] the registration that replaces the declaration of module economy could not be resolved: InvalidOperationException: no file. " +
            "Fix: correct that registration (host or test), or remove it so the platform builds the declared catalog (FG-018).",
        ]);
    }

    [Fact]
    public async Task A_host_instance_does_not_hide_a_rule_fault_of_its_file()
    {
        using var seeded = new SeededConfig();
        SeededFaults.Edit(seeded, "resources.json", r => r["resources"]![1]!["maxQuantity"] = 0);

        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(GameHttp.FullGame, seeded.Apply, services: s => s.AddSingleton(OwnCatalog()));
        });

        ex.Violations.ShouldBe(
            ["[module economy] config resources.json row 2: 'maxQuantity' must be an integer of at least 1, found 0. Fix: set the largest quantity a player may hold (ECO-07)."]);
    }

    [Fact]
    public async Task A_second_module_declaring_the_catalog_gives_one_line_naming_both()
    {
        var violations = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await GameHttp.StartGameAsync(extra: gs => gs.AddModule(new SecondResourceCatalogModule()));
        });

        violations.Violations.ShouldBe(
            ["[catalog ResourceCatalog] is declared by economy and game. Fix: declare it in one module; the other takes it from DI (FG-018)."]);
    }

    private static ResourceCatalog OwnCatalog() =>
        new([new ResourceDefinition("gold", ResourceKind.Currency, 1_000_000_000, "Gold"), new ResourceDefinition("potion", ResourceKind.Item, 999, "Potion")]);

    private static async Task GrantAsync(GameServer.TestKit.TestHost host)
    {
        var (player, _) = await host.NewPlayerAsync();
        (await player.CheatAsync("gold", 10)).Code.ShouldBe("OK");
    }

    private static void Spy(GameServerBuilder builder, SeenCatalogs seen)
    {
        builder.Services.AddSingleton(seen);
        builder.ConfigureExtensions(x => x.ReplaceHandler<CheatGrantCommand, CheatGrantResult, SpyGrantHandler>(CheatGrantHandler.Operation));
    }

    internal sealed class SeenCatalogs
    {
        public ConcurrentQueue<ResourceCatalog> Catalogs { get; } = new();
    }

    /// <summary>The cheat handler, recording the catalog instance it was created with.</summary>
    internal sealed class SpyGrantHandler(ResourceCatalog catalog, SeenCatalogs seen) : ICommandHandler<CheatGrantCommand, CheatGrantResult>
    {
        public ValueTask<CommandOutcome<CheatGrantResult>> HandleAsync(CheatGrantCommand request, ICommandContext context)
        {
            seen.Catalogs.Enqueue(catalog);
            return new CheatGrantHandler(catalog).HandleAsync(request, context);
        }
    }

    private sealed class CountingCatalog;

    private sealed class CountingCatalogModule : IGameModule
    {
        private int _builds;

        public int Builds => Volatile.Read(ref _builds);

        public ModuleDescriptor Describe() => new()
        {
            Name = "counting",
            ConfigCatalogs =
            [
                ConfigCatalogDeclaration.Of(_ =>
                {
                    Interlocked.Increment(ref _builds);
                    return new CountingCatalog();
                }, "counter.json"),
            ],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed class SecondResourceCatalogModule : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "game",
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => ResourceCatalog.Load(c.Config), ResourceCatalog.ConfigFile)],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    /// <summary>Counts every file read of a real config (bytes, JSON, typed).</summary>
    private sealed class CountingConfig(IGameConfig inner) : IGameConfig
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public string ConfigVersion => inner.ConfigVersion;

        public string ClientConfigVersion => inner.ClientConfigVersion;

        public string Label => inner.Label;

        public IReadOnlyCollection<string> FileNames => inner.FileNames;

        public IReadOnlyCollection<string> ClientVisibleFiles => inner.ClientVisibleFiles;

        public void Reset() => Interlocked.Exchange(ref _reads, 0);

        public bool HasFile(string name) => inner.HasFile(name);

        public ReadOnlyMemory<byte> GetFileBytes(string name)
        {
            Interlocked.Increment(ref _reads);
            return inner.GetFileBytes(name);
        }

        public JsonNode GetFileJson(string name)
        {
            Interlocked.Increment(ref _reads);
            return inner.GetFileJson(name);
        }

        public T GetFile<T>(string name)
        {
            Interlocked.Increment(ref _reads);
            return inner.GetFile<T>(name);
        }
    }
}
