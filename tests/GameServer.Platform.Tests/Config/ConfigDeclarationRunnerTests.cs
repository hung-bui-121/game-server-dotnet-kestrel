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
            ["[catalog CatalogB] the registration that replaces the declaration of module m could not be resolved: InvalidOperationException: no file. Fix: correct that registration (host or test), or remove it so the platform builds the declared catalog (FG-018)."]);
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

    // ---- slice 015 T071 M1: a catalog over an absent optional file is still built ----

    private const string AbsentJsonMessage = "Config file 'absent.json' is not in the effective config. Fix: check Config.HasFile(\"absent.json\") first";

    [Fact]
    public void An_absent_optional_file_whose_build_checks_has_file_gives_the_empty_catalog_and_no_line()
    {
        var empty = new CatalogA();
        var module = Module(
            "m",
            files: [ConfigFileDeclaration.Optional("absent.json")],
            catalogs: [ConfigCatalogDeclaration.Of(c => c.Config.HasFile("absent.json") ? new CatalogA { X = 1 } : empty, "absent.json", "a.json")]);

        var result = Run([module]);

        result.Errors.ShouldBeEmpty();
        result.FailedCatalogs.ShouldBeEmpty();
        result.Catalogs[typeof(CatalogA)].ShouldBeSameAs(empty);
    }

    [Fact]
    public void An_absent_optional_file_whose_build_reads_it_gives_the_optional_absent_line()
    {
        var module = Module(
            "m",
            files: [ConfigFileDeclaration.Optional("absent.json")],
            catalogs: [ConfigCatalogDeclaration.Of(c => new CatalogA { X = c.Json("absent.json").GetArrayLength() }, "a.json", "absent.json")]);

        var result = Run([module]);

        result.Errors.ShouldBe(
        [
            $"[module m] catalog CatalogA: the build threw KeyNotFoundException: {AbsentJsonMessage}; optional config file 'absent.json' is absent. " +
            "Fix: build an empty catalog when Config.HasFile(\"absent.json\") is false, or declare the file Required (FG-018).",
        ]);
        result.FailedCatalogs.ShouldBe([typeof(CatalogA)]);
    }

    [Fact]
    public void An_absent_optional_file_whose_build_returns_null_gives_the_null_line()
    {
        var module = Module("m", files: [ConfigFileDeclaration.Optional("absent.json")], catalogs: [ConfigCatalogDeclaration.Of<CatalogA>(_ => null, "absent.json")]);

        Run([module]).Errors.ShouldBe(
            ["[module m] catalog CatalogA: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018)."]);
    }

    [Fact]
    public void An_absent_required_catalog_file_gives_the_registry_line_only()
    {
        var built = false;
        var module = Module("m", catalogs: [ConfigCatalogDeclaration.Of(_ => { built = true; return new CatalogA(); }, "absent.json")]);

        var result = Run([module]);

        built.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();
        result.FailedCatalogs.ShouldBe([typeof(CatalogA)]);
        Registry(module).ValidateConfig(Config).ShouldBe(
            ["[module m] required config file 'absent.json' is missing. Fix: add it to config/base/manifest.json 'files' and to config/base (CFG-01)."]);
    }

    [Fact]
    public void A_present_optional_file_with_a_rule_error_skips_the_catalog_without_a_line()
    {
        var built = false;
        var module = Module(
            "m",
            files: [ConfigFileDeclaration.Optional("a.json", new ConfigRule("r", c => c.Problems.Error(ConfigLocation.WholeFile, "bad", "fix")))],
            catalogs: [ConfigCatalogDeclaration.Of(_ => { built = true; return new CatalogA(); }, "a.json")]);

        var result = Run([module]);

        built.ShouldBeFalse();
        result.Errors.ShouldBe(["[module m] config a.json: bad. Fix: fix."]);
        result.FailedCatalogs.ShouldBe([typeof(CatalogA)]);
    }

    [Fact]
    public void A_provided_catalog_over_an_absent_optional_file_is_resolved()
    {
        var host = new CatalogA();
        var module = Module("m", files: [ConfigFileDeclaration.Optional("absent.json")], catalogs: [ConfigCatalogDeclaration.Of<CatalogA>(_ => null, "absent.json")]);

        var result = ConfigDeclarationRunner.Run(Registry(module).Modules, Config, _ => CatalogBinding.Provided(() => host));

        result.Errors.ShouldBeEmpty();
        result.Catalogs[typeof(CatalogA)].ShouldBeSameAs(host);
    }

    public static TheoryData<string> Compositions => [.. InvariantCompositions.Keys];

    /// <summary>The M1 invariant: a catalog that was not built always has a line that explains it (runner or registry).</summary>
    [Theory]
    [MemberData(nameof(Compositions))]
    public void A_catalog_that_is_not_built_is_always_explained_by_a_line(string composition)
    {
        var modules = InvariantCompositions[composition]();
        var registry = Registry(modules);

        var result = ConfigDeclarationRunner.Run(registry.Modules, Config, _ => CatalogBinding.Declared);

        if (result.FailedCatalogs.Count > 0)
        {
            (result.Errors.Count + registry.ValidateConfig(Config).Count).ShouldBeGreaterThan(0);
        }

        ConfigDeclarationRunner.UnexplainedFailures(result, registry.ValidateConfig(Config)).ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_catalog_that_no_line_explains_gives_the_backstop_line()
    {
        var result = new ConfigDeclarationResult();
        result.FailedCatalogs.Add(typeof(CatalogB));
        result.FailedCatalogs.Add(typeof(CatalogA));

        ConfigDeclarationRunner.UnexplainedFailures(result, []).ShouldBe(
        [
            "[catalog CatalogA] was not built and no other line explains why (framework fault). Fix: report it with this config; the catalog would fail at its first use (FG-018).",
            "[catalog CatalogB] was not built and no other line explains why (framework fault). Fix: report it with this config; the catalog would fail at its first use (FG-018).",
        ]);
        ConfigDeclarationRunner.UnexplainedFailures(result, ["[module m] required config file 'x.json' is missing. …"]).ShouldBeEmpty();
        result.Errors.Add("[module m] config a.json: bad. Fix: fix.");
        ConfigDeclarationRunner.UnexplainedFailures(result, []).ShouldBeEmpty();
    }

    private static readonly Dictionary<string, Func<IGameModule[]>> InvariantCompositions = new(StringComparer.Ordinal)
    {
        ["built"] = () => [Module("m", catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")])],
        ["required absent"] = () => [Module("m", catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "absent.json")])],
        ["optional absent, empty"] = () =>
            [Module("m", files: [ConfigFileDeclaration.Optional("absent.json")], catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "absent.json")])],
        ["optional absent, reads"] = () =>
            [Module("m", files: [ConfigFileDeclaration.Optional("absent.json")], catalogs: [ConfigCatalogDeclaration.Of(c => new CatalogA { X = c.Json("absent.json").GetArrayLength() }, "absent.json")])],
        ["optional absent, null"] = () =>
            [Module("m", files: [ConfigFileDeclaration.Optional("absent.json")], catalogs: [ConfigCatalogDeclaration.Of<CatalogA>(_ => null, "absent.json")])],
        ["rule error in another module"] = () =>
        [
            Module("owner", catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")]),
            Module("other", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("o", c => c.Problems.Error(ConfigLocation.Row(1), "bad", "fix")))]),
        ],
        ["build problem, throw, null"] = () =>
        [
            Module("m", catalogs:
            [
                ConfigCatalogDeclaration.Of<CatalogA>(c => { c.Problems.Error(ConfigLocation.Row(2).InFile("a.json"), "no stage", "add it"); return null; }, "a.json"),
                ConfigCatalogDeclaration.Of<CatalogB>(_ => throw new FormatException("bad"), "a.json"),
                ConfigCatalogDeclaration.Of<CatalogC>(_ => null, "b.json"),
            ]),
        ],
        ["declared twice"] = () =>
        [
            Module("economy", catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")]),
            Module("game", catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")]),
        ],
        ["throwing rule"] = () =>
            [Module("m", files: [ConfigFileDeclaration.Required("a.json", new ConfigRule("t", _ => throw new InvalidOperationException("x")))], catalogs: [ConfigCatalogDeclaration.Of(_ => new CatalogA(), "a.json")])],
    };

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
