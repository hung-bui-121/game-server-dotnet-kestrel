using GameServer.Config;
using GameServer.Modules;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>Slice 015 T071 G2: <see cref="TestGameConfigStatus"/> gives a startup check's unit test the status the host would publish.</summary>
public sealed class TestGameConfigStatusTests
{
    private static readonly IGameConfig Config = TestGameConfig.FromJson(new Dictionary<string, string> { ["rows.json"] = """{"rows":[1,2,3]}""" });

    [Fact]
    public void Not_loaded_is_the_provision_state()
    {
        var status = TestGameConfigStatus.NotLoaded();

        status.State.ShouldBe(GameConfigState.NotLoaded);
        status.Config.ShouldBeNull();
        status.DeclarationsPassed.ShouldBeFalse();
        status.TryGetCatalog<RowsCatalog>(out _).ShouldBeFalse();
    }

    [Fact]
    public void Failed_is_the_state_of_a_config_that_did_not_load()
    {
        var status = TestGameConfigStatus.Failed();

        status.State.ShouldBe(GameConfigState.Failed);
        status.Config.ShouldBeNull();
        status.DeclarationsPassed.ShouldBeFalse();
        status.TryGetConfig(out _).ShouldBeFalse();
    }

    [Fact]
    public void From_a_passing_report_is_loaded_with_its_config_and_catalogs()
    {
        var report = ConfigDeclarationCheck.Check(Config, new RowsModule(refuse: false));

        var status = TestGameConfigStatus.From(report);

        status.State.ShouldBe(GameConfigState.Loaded);
        status.Config.ShouldBeSameAs(Config);
        status.DeclarationsPassed.ShouldBeTrue();
        status.TryGetCatalog<RowsCatalog>(out var catalog).ShouldBeTrue();
        catalog.ShouldBeSameAs(report.Catalog<RowsCatalog>());
        catalog.Rows.ShouldBe(3);
    }

    [Fact]
    public void From_a_failing_report_is_loaded_but_not_passed_and_has_no_failed_catalog()
    {
        var report = ConfigDeclarationCheck.Check(Config, new RowsModule(refuse: true));

        var status = TestGameConfigStatus.From(report);

        status.State.ShouldBe(GameConfigState.Loaded);
        status.Config.ShouldBeSameAs(Config);
        status.DeclarationsPassed.ShouldBeFalse();
        status.TryGetCatalog<RowsCatalog>(out _).ShouldBe(report.TryGetCatalog<RowsCatalog>(out _));
        status.TryGetCatalog<RowsCatalog>(out _).ShouldBeFalse();
    }

    [Fact]
    public void From_a_report_without_modules_passes_with_no_catalog()
    {
        var status = TestGameConfigStatus.From(ConfigDeclarationCheck.Check(Config));

        status.DeclarationsPassed.ShouldBeTrue();
        status.TryGetConfig(out var config).ShouldBeTrue();
        config.ShouldBeSameAs(Config);
        status.TryGetCatalog<RowsCatalog>(out _).ShouldBeFalse();
    }

    [Fact]
    public void A_check_unit_test_uses_the_status_like_the_host()
    {
        var result = new StartupCheckResult();

        new RowsCheck(TestGameConfigStatus.From(ConfigDeclarationCheck.Check(Config, new RowsModule(refuse: false)))).Check(result);
        new RowsCheck(TestGameConfigStatus.NotLoaded()).Check(result);

        result.Errors.ShouldBe(["[rows] 3 rows, at most 2"]);
    }

    private sealed class RowsCatalog(int rows)
    {
        public int Rows { get; } = rows;
    }

    private sealed class RowsCheck(GameConfigStatus status) : IStartupCheck
    {
        public void Check(StartupCheckResult result)
        {
            if (status.TryGetCatalog<RowsCatalog>(out var catalog) && catalog.Rows > 2)
            {
                result.Errors.Add($"[rows] {catalog.Rows} rows, at most 2");
            }
        }
    }

    private sealed class RowsModule(bool refuse) : IGameModule
    {
        public ModuleDescriptor Describe() => new()
        {
            Name = "rows",
            ConfigFiles = [ConfigFileDeclaration.Required("rows.json", new ConfigRule("rows.refuse", Refuse))],
            ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => new RowsCatalog(c.Json("rows.json").GetProperty("rows").GetArrayLength()), "rows.json")],
        };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }

        private void Refuse(ConfigContext context)
        {
            if (refuse)
            {
                context.Problems.Error(ConfigLocation.WholeFile, "refused", "accept it");
            }
        }
    }
}
