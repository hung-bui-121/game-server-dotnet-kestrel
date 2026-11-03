using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform;
using GameServer.Platform.Persistence;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// FR-001..FR-003, FR-013, FR-014, SC-001 (slice 015 T026): each seeded config fault of the sample game composition gives
/// exactly one startup line, asserted literally; the combined examples of contracts/startup-report.md §2 and §4.
/// </summary>
[Collection(ConsoleErrorCollection.Name)]
public sealed class ConfigStartupReportTests
{
    internal const string GateLine = "[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).";

    public static TheoryData<string> Faults => [.. SeededFaults.Names];

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task Each_seeded_fault_gives_exactly_one_line(string fault)
    {
        using var seeded = new SeededConfig();
        var expected = SeededFaults.Apply(fault, seeded);

        var violations = await StartupFailsAsync(seeded, SeededFaults.Modules(fault));

        violations.ShouldBe([expected]);
    }

    [Fact]
    public async Task Six_faults_in_three_files_and_an_unrelated_violation_give_the_seven_lines_of_the_contract()
    {
        using var seeded = new SeededConfig()
            .With("resources.json", """
                {"resources": [
                  {"id": "gold", "kind": "currency", "maxQuantity": 1000000000, "displayName": "Gold"},
                  {"id": "gem", "kind": "currency", "maxQuantity": 1000000, "displayName": "Gem"},
                  {"id": "potion", "kind": "item", "maxQuantity": "999", "displayName": "Potion"},
                  {"id": "gold", "kind": "currency", "maxQuantity": 10, "displayName": "Gold again"},
                  {"id": "sword", "kind": "item", "maxQuantity": 10, "displayName": "Sword"}
                ]}
                """)
            .With("iap.json", """
                {"products": {
                  "gems_100": {"grants": [{"resource": "gem", "quantity": 100}]},
                  "google:gems_100": {"grants": [{"resource": "gem", "quantity": 0}]},
                  "google:starter_pack": {"grants": [{"resource": "gold", "quantity": 500}, {"resource": "shield", "quantity": 1}]}
                }}
                """);
        SeededFaults.Edit(seeded, "battle.json", battle => battle["stages"]![1]!["xp"] = "70");

        var violations = await StartupFailsAsync(seeded, GameHttp.FullGame, s => s["GameServer:Gate:CacheSeconds"] = "99");

        violations.ShouldBe(
        [
            "[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found \"999\". Fix: set the largest quantity a player may hold (ECO-07).",
            "[module economy] config resources.json row 4: id 'gold' is already defined in row 1. Fix: give every resource a unique id (ECO-07).",
            "[module iap] config iap.json key 'gems_100': the key must be \"google:{productId}\" or \"apple:{productId}\" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).",
            "[module iap] config iap.json at $.products['google:gems_100'].grants[0]: 'quantity' must be an integer from 1 to 1000000 (maxQuantity of 'gem'), found 0. Fix: correct the grant (IAP-01).",
            "[module iap] config iap.json at $.products['google:starter_pack'].grants[1]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01).",
            "[module sample.game] config battle.json row 2: 'xp' must be an integer of at least 0, found \"70\". Fix: give every stage an integer xp of at least 0.",
            GateLine,
        ]);
    }

    [Fact]
    public async Task Two_missing_files_and_one_invalid_json_file_give_the_three_loader_lines_and_nothing_else()
    {
        const string invalid = "{\n  \"products\": {\n    \"google:x\" }\n}";
        using var seeded = new SeededConfig().Without("battle.json").Without("shop.json").With("iap.json", invalid);

        var violations = await StartupFailsAsync(seeded, GameHttp.FullGame);

        violations.ShouldBe(
        [
            $"[config] file '{Path.Combine(seeded.BasePath, "shop.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.",
            $"[config] file '{Path.Combine(seeded.BasePath, "battle.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.",
            $"[config] '{Path.Combine(seeded.BasePath, "iap.json")}' is not valid JSON: {ParseMessage(invalid)}. Fix: correct the file (CFG-01).",
        ]);
    }

    [Fact]
    public async Task A_game_rule_and_the_economy_rule_on_one_file_both_report_naming_their_module()
    {
        using var seeded = new SeededConfig();
        SeededFaults.Edit(seeded, "resources.json", r =>
        {
            r["resources"]![0]!["kind"] = "item";
            r["resources"]![1]!["maxQuantity"] = 0;
        });

        var violations = await StartupFailsAsync(seeded, SeededFaults.WithGame(new GameRulesModule()));

        violations.ShouldBe(
        [
            "[module economy] config resources.json row 2: 'maxQuantity' must be an integer of at least 1, found 0. Fix: set the largest quantity a player may hold (ECO-07).",
            "[module game] config resources.json row 1: 'gold' must be a currency. Fix: set its kind to \"currency\" (game rule).",
        ]);
    }

    [Fact]
    public async Task Provision_mode_with_a_seeded_rule_fault_reports_no_rule_line()
    {
        using var seeded = new SeededConfig();
        SeededFaults.Edit(seeded, "resources.json", r => r["resources"]![1]!["maxQuantity"] = 0);
        await using var app = SampleTestHost.BuildApp(
            GameHttp.FullGame, seeded.Apply, services: s => s.AddSingleton<IStoreProvisioner>(new NoopProvisioner()));

        var code = 0;
        var error = await ConsoleErrorCapture.RunAsync(async () => code = await app.RunGameServerAsync(HostMode.Provision));

        code.ShouldBe(HostExitCodes.Ok);
        error.ShouldNotContain("config resources.json");
    }

    [Fact]
    public async Task Export_mode_with_a_seeded_rule_fault_reports_no_rule_line()
    {
        using var seeded = new SeededConfig();
        SeededFaults.Edit(seeded, "resources.json", r => r["resources"]![1]!["maxQuantity"] = 0);
        var export = Path.Combine(seeded.Path, "wire.json");
        var app = SampleTestHost.BuildApp(GameHttp.FullGame, s =>
        {
            seeded.Apply(s);
            s["GameServerCommandLine:ExportWireContract"] = export; // what GameServerCommandLine.Parse passes for --export-wire-contract
        });

        var code = 0;
        var error = await ConsoleErrorCapture.RunAsync(async () => code = await app.RunGameServerAsync(HostMode.ExportWireContract));

        code.ShouldBe(HostExitCodes.Ok);
        error.ShouldNotContain("config resources.json");
        File.Exists(export).ShouldBeTrue();
    }

    internal static async Task<IReadOnlyList<string>> StartupFailsAsync(
        SeededConfig seeded, Action<GameServerBuilder> modules, Action<Dictionary<string, string?>>? settings = null)
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await SampleTestHost.StartAsync(modules, s =>
            {
                seeded.Apply(s);
                settings?.Invoke(s);
            });
        });
        return ex.Violations;
    }

    private static string ParseMessage(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }

        throw new InvalidOperationException("expected a parse error");
    }

    private sealed class NoopProvisioner : IStoreProvisioner
    {
        public Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>The seeded faults of T026/T028: one change of the shared test config and the one line it must give.</summary>
internal static class SeededFaults
{
    public const string GameRule = "game rule problem";
    public const string ThrowingBuild = "catalog build that throws";

    private static readonly Dictionary<string, Func<SeededConfig, string>> Seeds = new(StringComparer.Ordinal)
    {
        ["resources duplicate id"] = s =>
        {
            Edit(s, "resources.json", r => r["resources"]!.AsArray().Add(JsonNode.Parse("""{"id": "gold", "kind": "currency", "maxQuantity": 5, "displayName": "G"}""")));
            return "[module economy] config resources.json row 5: id 'gold' is already defined in row 1. Fix: give every resource a unique id (ECO-07).";
        },
        ["resources missing maxQuantity"] = s =>
        {
            Edit(s, "resources.json", r => r["resources"]![2]!.AsObject().Remove("maxQuantity"));
            return "[module economy] config resources.json row 3: 'maxQuantity' is missing. Fix: set the largest quantity a player may hold (ECO-07).";
        },
        ["resources maxQuantity string"] = s =>
        {
            Edit(s, "resources.json", r => r["resources"]![2]!["maxQuantity"] = "999");
            return "[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found \"999\". Fix: set the largest quantity a player may hold (ECO-07).";
        },
        ["resources unknown kind"] = s =>
        {
            Edit(s, "resources.json", r => r["resources"]![1]!["kind"] = "gems");
            return "[module economy] config resources.json row 2: 'kind' is 'gems'. Fix: use \"currency\" or \"item\" (ECO-07).";
        },
        ["iap unknown resource"] = s =>
        {
            Edit(s, "iap.json", r => r["products"]!["google:starter_pack"]!["grants"]![1]!["resource"] = "shield");
            return "[module iap] config iap.json at $.products['google:starter_pack'].grants[1]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01).";
        },
        ["iap quantity 0"] = s =>
        {
            Edit(s, "iap.json", r => r["products"]!["google:gems_100"]!["grants"]![0]!["quantity"] = 0);
            return "[module iap] config iap.json at $.products['google:gems_100'].grants[0]: 'quantity' must be an integer from 1 to 1000000 (maxQuantity of 'gem'), found 0. Fix: correct the grant (IAP-01).";
        },
        ["iap key without store"] = s =>
        {
            Edit(s, "iap.json", r => r["products"]!["gems_100"] = r["products"]!["google:gems_100"]!.DeepClone());
            return "[module iap] config iap.json key 'gems_100': the key must be \"google:{productId}\" or \"apple:{productId}\" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).";
        },
        ["iap duplicate product key"] = s =>
        {
            s.With("iap.json", "{\n  \"products\": {\n    \"google:gems_100\": {\"grants\": [{\"resource\": \"gem\", \"quantity\": 100}]},\n    \"google:gems_100\": {\"grants\": [{\"resource\": \"gem\", \"quantity\": 200}]}\n  }\n}");
            return $"[config] '{Path.Combine(s.BasePath, "iap.json")}' has property 'google:gems_100' twice at $.products (line 4, byte 5). Fix: keep one property; the last one used to win silently (CFG-01).";
        },
        ["battle wrong type"] = s =>
        {
            Edit(s, "battle.json", r => r["stages"]![1]!["xp"] = "70");
            return "[module sample.game] config battle.json row 2: 'xp' must be an integer of at least 0, found \"70\". Fix: give every stage an integer xp of at least 0.";
        },
        [GameRule] = s =>
        {
            Edit(s, "resources.json", r => r["resources"]![0]!["kind"] = "item");
            return "[module game] config resources.json row 1: 'gold' must be a currency. Fix: set its kind to \"currency\" (game rule).";
        },
        [ThrowingBuild] = _ =>
            "[module game] catalog ThrowingCatalog: the build threw InvalidOperationException: boom. Fix: report problems through ConfigProblems instead of throwing (FG-018).",
        ["duplicate property"] = s =>
        {
            s.With("battle.json", "{\n  \"maxStage\": 3,\n  \"maxStage\": 3,\n  \"stages\": []\n}");
            return $"[config] '{Path.Combine(s.BasePath, "battle.json")}' has property 'maxStage' twice at $ (line 3, byte 3). Fix: keep one property; the last one used to win silently (CFG-01).";
        },
        ["missing file"] = s =>
        {
            s.Without("shop.json");
            return $"[config] file '{Path.Combine(s.BasePath, "shop.json")}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.";
        },
        ["invalid json"] = s =>
        {
            s.With("battle.json", "{ \"maxStage\": 3,");
            string message;
            try
            {
                using var _ = JsonDocument.Parse("{ \"maxStage\": 3,");
                throw new InvalidOperationException("expected a parse error");
            }
            catch (JsonException ex)
            {
                message = ex.Message;
            }

            return $"[config] '{Path.Combine(s.BasePath, "battle.json")}' is not valid JSON: {message}. Fix: correct the file (CFG-01).";
        },
    };

    public static IEnumerable<string> Names => Seeds.Keys;

    /// <summary>Loader faults have no kit equivalent (the kit starts from a loaded config).</summary>
    public static IEnumerable<string> RuleAndCatalogNames => Names.Where(n => !IsLoaderFault(n));

    public static string Apply(string fault, SeededConfig seeded) => Seeds[fault](seeded);

    public static bool IsLoaderFault(string fault) => fault is "iap duplicate product key" or "duplicate property" or "missing file" or "invalid json";

    /// <summary>The sample game composition, plus the game test module for the faults that need it.</summary>
    public static Action<GameServerBuilder> Modules(string fault) => fault switch
    {
        GameRule => WithGame(new GameRulesModule()),
        ThrowingBuild => WithGame(new ThrowingCatalogModule()),
        _ => GameHttp.FullGame,
    };

    public static IGameModule[] KitModules(string fault)
    {
        IGameModule[] game = [new GameServer.Modules.Economy.EconomyModule(), new GameServer.Modules.Iap.IapModule(), new SampleGame.Adapters.SampleGameModule()];
        return fault switch
        {
            GameRule => [.. game, new GameRulesModule()],
            ThrowingBuild => [.. game, new ThrowingCatalogModule()],
            _ => game,
        };
    }

    public static Action<GameServerBuilder> WithGame(IGameModule module) =>
        builder =>
        {
            GameHttp.FullGame(builder);
            builder.AddModule(module);
        };

    public static void Edit(SeededConfig seeded, string file, Action<JsonNode> edit)
    {
        var path = Path.Combine(seeded.BasePath, file);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        edit(node);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>A game module adding its own rule to the framework's <c>resources.json</c> (the zh-pvp "CASH and GOLD" shape).</summary>
internal sealed class GameRulesModule : IGameModule
{
    private static readonly ConfigRule GoldIsCurrency = new("game.currencies", c =>
    {
        var row = 0;
        foreach (var resource in c.Json("resources.json").GetProperty("resources").EnumerateArray())
        {
            row++;
            if (resource.TryGetProperty("id", out var id) && id.GetString() == "gold"
                && !(resource.TryGetProperty("kind", out var kind) && string.Equals(kind.GetString(), "currency", StringComparison.OrdinalIgnoreCase)))
            {
                c.Problems.Error(ConfigLocation.Row(row), "'gold' must be a currency", "set its kind to \"currency\" (game rule)");
            }
        }
    });

    public ModuleDescriptor Describe() => new()
    {
        Name = "game",
        ConfigFiles = [ConfigFileDeclaration.Required("resources.json", GoldIsCurrency)],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }
}

/// <summary>A game module whose catalog build throws instead of reporting.</summary>
internal sealed class ThrowingCatalogModule : IGameModule
{
    public ModuleDescriptor Describe() => new()
    {
        Name = "game",
        ConfigCatalogs = [ConfigCatalogDeclaration.Of<ThrowingCatalog>(_ => throw new InvalidOperationException("boom"), "counter.json")],
    };

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
    }
}

internal sealed class ThrowingCatalog;
