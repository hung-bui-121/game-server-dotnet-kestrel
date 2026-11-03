using System.Text;
using System.Text.Json.Nodes;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Config;
using GameServer.Platform.Registry;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>FR-011..FR-014, FR-020..FR-023 (slice 015 T018): the declaration engine shared by the host and the test kit.</summary>
public sealed class ConfigDeclarationRunnerTests
{
    private static readonly CountingConfig Config = new(new Dictionary<string, string>
    {
        ["a.json"] = """{"rows":[1,2]}""",
        ["b.json"] = """{"x":1}""",
        ["notes.txt"] = "plain text",
    });

    [Fact]
    public void Rules_run_in_module_then_declaration_order_and_report_in_their_order()
    {
        var order = new List<string>();
        var first = Module("m1", files: [ConfigFileDeclaration.Required("a.json", Rule("r1", order, c => c.Problems.Error(ConfigLocation.Row(2), "p1", "f1")), Rule("r2", order))]);
        var second = Module("m2", files: [ConfigFileDeclaration.Required("b.json", Rule("r3", order, c => c.Problems.Error(ConfigLocation.Key("k"), "p2.", "f2.")))]);

        var result = Run([first, second]);

        order.ShouldBe(["r1", "r2", "r3"]);
        result.Errors.ShouldBe(["[module m1] config a.json row 2: p1. Fix: f1.", "[module m2] config b.json key 'k': p2. Fix: f2."]);
        result.RuleCount.ShouldBe(3);
    }

    [Fact]
    public void An_absent_file_runs_no_rule_and_gives_no_line_here()
    {
        var order = new List<string>();
        var module = Module("m", files: [ConfigFileDeclaration.Optional("absent.json", Rule("opt", order)), ConfigFileDeclaration.Required("gone.json", Rule("req", order))]);

        var result = Run([module]);

        order.ShouldBeEmpty();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void A_throwing_rule_gives_one_line_and_the_other_rules_still_run()
    {
        var order = new List<string>();
        var module = Module("m", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("m.boom", _ => throw new InvalidOperationException("boom")), Rule("after", order))]);

        var result = Run([module]);

        order.ShouldBe(["after"]);
        result.Errors.ShouldBe(["[module m] config a.json: rule 'm.boom' threw InvalidOperationException: boom. Fix: report problems through ConfigProblems instead of throwing (FG-018)."]);
    }

    [Fact]
    public void A_warning_is_not_an_error_and_does_not_stop_a_catalog()
    {
        var module = Module(
            "m",
            files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("m.warn", c => c.Problems.Warning(ConfigLocation.WholeFile, "odd", "check it")))],
            catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")]);

        var result = Run([module]);

        result.Errors.ShouldBeEmpty();
        result.Warnings.ShouldBe(["[module m] config a.json: odd. Fix: check it."]);
        result.Catalogs[typeof(CatalogA)].ShouldBeOfType<CatalogA>();
    }

    [Fact]
    public void Two_modules_with_rules_on_one_file_both_report_naming_their_module()
    {
        var economy = Module("economy", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("e", c => c.Problems.Error(ConfigLocation.Row(1), "e", "f")))]);
        var game = Module("game", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("g", c => c.Problems.Error(ConfigLocation.Row(1), "g", "f")))]);

        Run([economy, game]).Errors.ShouldBe(["[module economy] config a.json row 1: e. Fix: f.", "[module game] config a.json row 1: g. Fix: f."]);
    }

    [Fact]
    public void A_catalog_whose_file_has_a_rule_error_from_any_module_is_skipped_without_a_line()
    {
        var built = false;
        var owner = Module("owner", catalogs: [ConfigCatalogDeclaration.Of(_ => { built = true; return new CatalogA(); }, "a.json")]);
        var other = Module("other", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("o", c => c.Problems.Error(ConfigLocation.Row(1), "bad", "fix")))]);

        var result = Run([owner, other]);

        built.ShouldBeFalse();
        result.Errors.ShouldBe(["[module other] config a.json row 1: bad. Fix: fix."]);
        result.FailedCatalogs.ShouldBe([typeof(CatalogA)]);
    }

    [Fact]
    public void A_build_problem_a_throw_and_a_null_each_give_one_line_and_other_catalogs_are_built()
    {
        var module = Module("m", catalogs:
        [
            ConfigCatalogDeclaration.Of<CatalogA>(c => { c.Problems.Error(ConfigLocation.Row(2).InFile("a.json"), "no stage", "add it"); return null; }, "a.json"),
            ConfigCatalogDeclaration.Of<CatalogB>(_ => throw new FormatException("bad"), "a.json"),
            ConfigCatalogDeclaration.Of<CatalogC>(_ => null, "b.json"),
            ConfigCatalogDeclaration.Of(_ => new CatalogD(), "b.json"),
        ]);

        var result = Run([module]);

        result.Errors.ShouldBe(
        [
            "[module m] catalog CatalogA (a.json row 2): no stage. Fix: add it.",
            "[module m] catalog CatalogB: the build threw FormatException: bad. Fix: report problems through ConfigProblems instead of throwing (FG-018).",
            "[module m] catalog CatalogC: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018).",
        ]);
        result.Catalogs.Keys.ShouldBe([typeof(CatalogD)]);
        result.FailedCatalogs.ShouldBe([typeof(CatalogA), typeof(CatalogB), typeof(CatalogC)], ignoreOrder: true);
        result.CatalogCount.ShouldBe(4);
    }

    [Fact]
    public void A_type_declared_twice_gives_one_line_naming_both_modules_and_the_first_is_kept()
    {
        var first = new CatalogA();
        var economy = Module("economy", catalogs: [ConfigCatalogDeclaration.Of(_ => first, "a.json")]);
        var game = Module("game", catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")]);

        var result = Run([economy, game]);

        result.Errors.ShouldBe(["[catalog CatalogA] is declared by economy and game. Fix: declare it in one module; the other takes it from DI (FG-018)."]);
        result.Catalogs[typeof(CatalogA)].ShouldBeSameAs(first);
    }

    [Fact]
    public void A_provided_catalog_is_resolved_instead_of_built_and_its_failure_has_its_own_line()
    {
        var host = new CatalogA();
        var built = false;
        var module = Module("m", catalogs:
        [
            ConfigCatalogDeclaration.Of(_ => { built = true; return new CatalogA(); }, "a.json"),
            ConfigCatalogDeclaration.Of(_ => new CatalogB(), "a.json"),
        ]);

        var result = ConfigDeclarationRunner.Run(
            Registry(module).Modules,
            Config,
            type => type == typeof(CatalogA) ? CatalogBinding.Provided(() => host) : CatalogBinding.Provided(() => throw new InvalidOperationException("no file")));

        built.ShouldBeFalse();
        result.Catalogs[typeof(CatalogA)].ShouldBeSameAs(host);
        result.Errors.ShouldBe(
            ["[module m] catalog CatalogB: the instance registered by the host could not be created: InvalidOperationException: no file. Fix: correct the registration or remove it to use the declared build (FG-018)."]);
    }

    [Fact]
    public void Each_file_is_parsed_once_per_run_and_shared_by_rules_and_builds()
    {
        var config = new CountingConfig(new Dictionary<string, string> { ["a.json"] = """{"x":1}""" });
        var reads = new List<int>();
        var rule = new ConfigRule("read", c => reads.Add(c.Json("a.json").GetProperty("x").GetInt32()));
        var module = Module(
            "m",
            files: [ConfigFileDeclaration.Required("a.json", rule, rule)],
            catalogs: [ConfigCatalogDeclaration.Of(c => new CatalogA { X = c.Json("a.json").GetProperty("x").GetInt32() }, "a.json")]);

        var result = ConfigDeclarationRunner.Run(Registry(module).Modules, config, _ => CatalogBinding.Declared);

        reads.ShouldBe([1, 1]);
        ((CatalogA)result.Catalogs[typeof(CatalogA)]).X.ShouldBe(1);
        config.ByteReads.ShouldBe(1);
    }

    [Fact]
    public void Json_of_an_absent_file_is_a_rule_throw_line()
    {
        var module = Module("m", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("m.cross", c => c.Json("absent.json")))]);

        Run([module]).Errors.Single().ShouldStartWith("[module m] config a.json: rule 'm.cross' threw KeyNotFoundException: Config file 'absent.json' is not in the effective config.");
    }

    [Fact]
    public void A_problem_in_another_file_names_that_file_and_an_overlaid_file_names_its_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "gs-runner-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "base"));
            Directory.CreateDirectory(Path.Combine(root, "env", "Staging"));
            File.WriteAllText(Path.Combine(root, "base", "manifest.json"), """{"label":"v1","files":["a.json","b.json"],"clientVisible":[]}""");
            File.WriteAllText(Path.Combine(root, "base", "a.json"), "{}");
            File.WriteAllText(Path.Combine(root, "base", "b.json"), "{}");
            File.WriteAllText(Path.Combine(root, "env", "Staging", "manifest.json"), """{"label":"","files":["b.json"],"clientVisible":[]}""");
            File.WriteAllText(Path.Combine(root, "env", "Staging", "b.json"), "{}");
            var config = FileGameConfigLoader.Load(root, "Staging");
            var module = Module("m", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("x", c => c.Problems.Error(ConfigLocation.Row(1).InFile("b.json"), "p", "f")))]);

            ConfigDeclarationRunner.Run(Registry(module).Modules, config, _ => CatalogBinding.Declared).Errors
                .ShouldBe(["[module m] config b.json (config/env/Staging) row 1: p. Fix: f."]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConfigDeclarationResult Run(IGameModule[] modules) =>
        ConfigDeclarationRunner.Run(Registry(modules).Modules, Config, _ => CatalogBinding.Declared);

    private static ModuleRegistry Registry(params IGameModule[] modules)
    {
        var registry = new ModuleRegistry();
        foreach (var module in modules)
        {
            registry.Add(module, serviceOnly: false);
        }

        return registry;
    }

    private static ConfigRule Rule(string name, List<string> order, Action<ConfigContext>? body = null) =>
        new(name, c =>
        {
            order.Add(name);
            body?.Invoke(c);
        });

    private static TestModule Module(string name, ConfigFileDeclaration[]? files = null, ConfigCatalogDeclaration[]? catalogs = null) =>
        new(new ModuleDescriptor { Name = name, ConfigFiles = files ?? [], ConfigCatalogs = catalogs ?? [] });

    private sealed class TestModule(ModuleDescriptor descriptor) : IGameModule
    {
        public ModuleDescriptor Describe() => descriptor;

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }

    private sealed class CatalogA
    {
        public int X { get; init; }
    }

    private sealed class CatalogB;

    private sealed class CatalogC;

    private sealed class CatalogD;

    /// <summary>An <see cref="IGameConfig"/> that counts byte reads, to prove each file is parsed once.</summary>
    private sealed class CountingConfig(Dictionary<string, string> files) : IGameConfig
    {
        private int _byteReads;

        public int ByteReads => _byteReads;

        public string ConfigVersion => "test+000000000000";

        public string ClientConfigVersion => "test+111111111111";

        public string Label => "test";

        public IReadOnlyCollection<string> FileNames => files.Keys;

        public IReadOnlyCollection<string> ClientVisibleFiles => [];

        public bool HasFile(string name) => files.ContainsKey(name);

        public ReadOnlyMemory<byte> GetFileBytes(string name)
        {
            Interlocked.Increment(ref _byteReads);
            return Encoding.UTF8.GetBytes(files[name]);
        }

        public JsonNode GetFileJson(string name) => JsonNode.Parse(GetFileBytes(name).Span)!;

        public T GetFile<T>(string name) => throw new NotSupportedException();
    }
}
