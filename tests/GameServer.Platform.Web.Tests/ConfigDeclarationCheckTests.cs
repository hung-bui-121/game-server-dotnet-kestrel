using GameServer.Config;
using GameServer.Modules;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-018, BC-8 fix (slice 015 T019): the kit runs a module's config declarations without a host or store.</summary>
public sealed class ConfigDeclarationCheckTests
{
    private static readonly IGameConfig Config = TestGameConfig.FromJson(new Dictionary<string, string>
    {
        ["good.json"] = """{"name":"ok"}""",
        ["bad.json"] = """{"name":5}""",
    });

    private static readonly ConfigRule NameRule = new("test.name", c => ConfigJson.RequiredString(c.Json(c.File!), "name", ConfigLocation.WholeFile, c.Problems));

    private static readonly ConfigRule WarnRule = new("test.warn", c => c.Problems.Warning(ConfigLocation.WholeFile, "looks odd", "check it"));

    [Fact]
    public void Errors_warnings_and_catalogs_are_reported_like_the_host()
    {
        var report = ConfigDeclarationCheck.Check(Config, new TestModule());

        report.Passed.ShouldBeFalse();
        report.Errors.ShouldBe(["[module test] config bad.json: 'name' must be a string, found number. Fix: set 'name' to a string."]);
        report.Warnings.ShouldBe(["[module test] config good.json: looks odd. Fix: check it."]);
        report.Catalog<GoodCatalog>().Name.ShouldBe("ok");
        report.TryGetCatalog<BadCatalog>(out _).ShouldBeFalse();
    }

    [Fact]
    public void ThrowIfFailed_lists_every_error_and_returns_a_passing_report()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ConfigDeclarationCheck.Check(Config, new TestModule()).ThrowIfFailed());

        ex.Message.ShouldContain("[module test] config bad.json: 'name' must be a string, found number.");
        var passing = ConfigDeclarationCheck.Check(Config, new TestModule(withBad: false));
        passing.ThrowIfFailed().ShouldBeSameAs(passing);
    }

    [Fact]
    public void Catalog_of_a_failed_catalog_throws_naming_the_lines()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ConfigDeclarationCheck.Check(Config, new TestModule()).Catalog<BadCatalog>());

        ex.Message.ShouldStartWith("Catalog BadCatalog was not built:");
        ex.Message.ShouldContain("config bad.json: 'name' must be a string");
    }

    [Fact]
    public void Built_catalogs_are_added_to_a_service_collection()
    {
        var services = new ServiceCollection();

        ConfigDeclarationCheck.Check(Config, new TestModule(withBad: false)).ThrowIfFailed().AddCatalogsTo(services);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<GoodCatalog>().Name.ShouldBe("ok");
    }

    [Fact]
    public void A_missing_required_file_gives_the_registry_line_and_no_rule_or_catalog_line()
    {
        var report = ConfigDeclarationCheck.Check(TestGameConfig.Empty, new TestModule(withBad: false));

        report.Errors.ShouldBe(
            ["[module test] required config file 'good.json' is missing. Fix: add it to config/base/manifest.json 'files' and to config/base (CFG-01)."]);
    }

    private sealed class GoodCatalog(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class BadCatalog;

    private sealed class TestModule(bool withBad = true) : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "test",
            ConfigFiles = withBad
                ? [ConfigFileDeclaration.Required("good.json", NameRule, WarnRule), ConfigFileDeclaration.Required("bad.json", NameRule)]
                : [ConfigFileDeclaration.Required("good.json", NameRule)],
            ConfigCatalogs = withBad
                ? [ConfigCatalogDeclaration.Of(c => new GoodCatalog(c.Json("good.json").GetProperty("name").GetString()!), "good.json"), ConfigCatalogDeclaration.Of(_ => new BadCatalog(), "bad.json")]
                : [ConfigCatalogDeclaration.Of(c => new GoodCatalog(c.Json("good.json").GetProperty("name").GetString()!), "good.json")],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
