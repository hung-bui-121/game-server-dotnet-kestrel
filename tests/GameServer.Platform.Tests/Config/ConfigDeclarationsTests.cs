using System.Text.Json;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>FG-018 (slice 015 T010): config file, rule and catalog declarations and their problem sink.</summary>
public sealed class ConfigDeclarationsTests
{
    private static readonly ConfigRule First = new("test.first", _ => { });
    private static readonly ConfigRule Second = new("test.second", _ => { });

    [Fact]
    public void Required_and_optional_keep_the_name_the_flag_and_the_rule_order()
    {
        var required = ConfigFileDeclaration.Required("resources.json", First, Second);
        var optional = ConfigFileDeclaration.Optional("template.json");

        required.Name.ShouldBe("resources.json");
        required.IsOptional.ShouldBeFalse();
        required.Rules.ShouldBe([First, Second]);
        optional.IsOptional.ShouldBeTrue();
        optional.Rules.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("env/a.json")]
    [InlineData("env\\a.json")]
    [InlineData("..a.json")]
    public void A_file_name_that_is_not_plain_is_refused(string name)
    {
        Should.Throw<ArgumentException>(() => ConfigFileDeclaration.Required(name));
        Should.Throw<ArgumentException>(() => ConfigCatalogDeclaration.Of<object>(_ => null, name));
    }

    [Fact]
    public void A_rule_needs_a_name_and_a_check()
    {
        Should.Throw<ArgumentException>(() => new ConfigRule(" ", _ => { }));
        Should.Throw<ArgumentNullException>(() => new ConfigRule("x", null!));
        Should.Throw<ArgumentException>(() => ConfigFileDeclaration.Required("a.json", null!, First));
    }

    [Fact]
    public void A_catalog_names_its_type_and_files_and_builds_through_its_delegate()
    {
        ConfigContext? seen = null;
        var catalog = new SampleCatalog();
        var declaration = ConfigCatalogDeclaration.Of(c =>
        {
            seen = c;
            return catalog;
        }, "a.json", "b.json", "a.json");
        var context = Context(out _);

        declaration.CatalogType.ShouldBe(typeof(SampleCatalog));
        declaration.Files.ShouldBe(["a.json", "b.json"]);
        declaration.Build(context).ShouldBeSameAs(catalog);
        seen.ShouldBeSameAs(context);
    }

    [Fact]
    public void A_catalog_needs_at_least_one_file()
    {
        Should.Throw<ArgumentException>(() => ConfigCatalogDeclaration.Of<SampleCatalog>(_ => null));
    }

    [Fact]
    public void Row_zero_is_refused_and_locations_compare_by_kind_value_and_file()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ConfigLocation.Row(0));
        Should.Throw<ArgumentException>(() => ConfigLocation.Path("products"));

        ConfigLocation.Row(3).ShouldBe(ConfigLocation.Row(3));
        (ConfigLocation.Row(3) == ConfigLocation.Row(4)).ShouldBeFalse();
        (ConfigLocation.Key("3") != ConfigLocation.Row(3)).ShouldBeTrue();
        ConfigLocation.WholeFile.ShouldBe(default(ConfigLocation));
        ConfigLocation.WholeFile.Kind.ShouldBe(ConfigLocationKind.WholeFile);
        ConfigLocation.Path("$.a").Value.ShouldBe("$.a");

        var inOther = ConfigLocation.Row(2).InFile("resources.json");
        inOther.File.ShouldBe("resources.json");
        inOther.Kind.ShouldBe(ConfigLocationKind.Row);
        inOther.ShouldNotBe(ConfigLocation.Row(2));
        inOther.ShouldBe(ConfigLocation.Row(2).InFile("resources.json"));
        inOther.GetHashCode().ShouldBe(ConfigLocation.Row(2).InFile("resources.json").GetHashCode());
    }

    [Fact]
    public void Error_count_counts_errors_and_not_warnings_and_entries_keep_the_order()
    {
        var problems = new ConfigProblems();

        problems.Warning(ConfigLocation.WholeFile, "w", "f");
        problems.Error(ConfigLocation.Row(1), "e1", "f1");
        problems.Error(ConfigLocation.Key("k"), "e2", "f2");

        problems.ErrorCount.ShouldBe(2);
        problems.Entries.Select(e => (e.Problem, e.IsWarning)).ShouldBe([("w", true), ("e1", false), ("e2", false)]);
        Should.Throw<ArgumentException>(() => problems.Error(ConfigLocation.WholeFile, " ", "f"));
    }

    [Fact]
    public void The_context_exposes_config_file_problems_and_the_parsed_json()
    {
        var context = Context(out var problems);

        context.File.ShouldBe("a.json");
        context.Problems.ShouldBeSameAs(problems);
        context.Config.HasFile("a.json").ShouldBeTrue();
        context.Json("a.json").GetProperty("x").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void A_descriptor_declares_no_file_and_no_catalog_by_default()
    {
        var descriptor = new ModuleDescriptor { Name = "m" };

        descriptor.ConfigFiles.ShouldBeEmpty();
        descriptor.ConfigCatalogs.ShouldBeEmpty();
    }

    private static ConfigContext Context(out ConfigProblems problems)
    {
        var config = new GameConfig("t", new Dictionary<string, byte[]> { ["a.json"] = """{"x":1}"""u8.ToArray() }, []);
        problems = new ConfigProblems();
        return new ConfigContext(config, "a.json", problems, file => JsonDocument.Parse(config.GetFileBytes(file)).RootElement);
    }

    private sealed class SampleCatalog;
}
