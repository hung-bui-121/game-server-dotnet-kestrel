using GameServer.Config;
using GameServer.Platform.Config;
using Shouldly;

namespace GameServer.Platform.Tests.Config;

/// <summary>FR-011, FR-022, FR-031, FR-053 (slice 015 T012): every report line template, literally (contracts/startup-report.md §4–§7).</summary>
public sealed class ConfigReportLinesTests
{
    [Fact]
    public void A_rule_line_names_module_file_location_problem_and_fix()
    {
        ConfigReportLines.Rule("economy", "resources.json", ConfigLocation.Row(3), "'maxQuantity' must be an integer of at least 1, found \"999\"", "set the largest quantity a player may hold (ECO-07)")
            .ShouldBe("[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found \"999\". Fix: set the largest quantity a player may hold (ECO-07).");
        ConfigReportLines.Rule("iap", "iap.json", ConfigLocation.Key("gems_100"), "the key must be \"google:{productId}\" or \"apple:{productId}\" with a product id of 1 to 128 characters", "correct the key (IAP-01)")
            .ShouldBe("[module iap] config iap.json key 'gems_100': the key must be \"google:{productId}\" or \"apple:{productId}\" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).");
        ConfigReportLines.Rule("iap", "iap.json", ConfigLocation.Path("$.products['google:starter_pack'].grants[1]"), "resource 'shield' is not defined in resources.json", "add it to resources.json or correct the grant (ECO-07, IAP-01)")
            .ShouldBe("[module iap] config iap.json at $.products['google:starter_pack'].grants[1]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01).");
        ConfigReportLines.Rule("economy", "resources.json", ConfigLocation.WholeFile, "'resources' must be an array, found object", "put the resource rows in a \"resources\" array (ECO-07)")
            .ShouldBe("[module economy] config resources.json: 'resources' must be an array, found object. Fix: put the resource rows in a \"resources\" array (ECO-07).");
    }

    [Fact]
    public void One_trailing_period_of_problem_and_fix_is_stripped()
    {
        ConfigReportLines.Rule("m", "a.json", ConfigLocation.WholeFile, "bad.", "fix it.").ShouldBe("[module m] config a.json: bad. Fix: fix it.");
        ConfigReportLines.Catalog("m", typeof(SampleCatalog), null, ConfigLocation.WholeFile, "bad.", "fix it.").ShouldBe("[module m] catalog SampleCatalog: bad. Fix: fix it.");
    }

    [Fact]
    public void An_overlaid_file_names_its_folder()
    {
        var fileRef = ConfigReportLines.FileRef("resources.json", "Staging");

        fileRef.ShouldBe("resources.json (config/env/Staging)");
        ConfigReportLines.FileRef("resources.json", null).ShouldBe("resources.json");
        ConfigReportLines.Rule("economy", fileRef, ConfigLocation.Row(1), "p", "f").ShouldBe("[module economy] config resources.json (config/env/Staging) row 1: p. Fix: f.");
    }

    [Fact]
    public void Rule_and_build_exceptions_have_their_own_lines()
    {
        var boom = new InvalidOperationException("boom");

        ConfigReportLines.RuleThrew("economy", "resources.json", "economy.resources", boom)
            .ShouldBe("[module economy] config resources.json: rule 'economy.resources' threw InvalidOperationException: boom. Fix: report problems through ConfigProblems instead of throwing (FG-018).");
        ConfigReportLines.CatalogThrew("economy", typeof(SampleCatalog), boom)
            .ShouldBe("[module economy] catalog SampleCatalog: the build threw InvalidOperationException: boom. Fix: report problems through ConfigProblems instead of throwing (FG-018).");
    }

    [Fact]
    public void Catalog_lines_have_the_documented_texts()
    {
        ConfigReportLines.Catalog("sample.game", typeof(SampleCatalog), "battle.json", ConfigLocation.Row(2), "no such stage", "add it")
            .ShouldBe("[module sample.game] catalog SampleCatalog (battle.json row 2): no such stage. Fix: add it.");
        ConfigReportLines.Catalog("sample.game", typeof(SampleCatalog), null, ConfigLocation.Row(2), "no such stage", "add it")
            .ShouldBe("[module sample.game] catalog SampleCatalog (row 2): no such stage. Fix: add it.");
        ConfigReportLines.CatalogNull("economy", typeof(SampleCatalog))
            .ShouldBe("[module economy] catalog SampleCatalog: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018).");
        ConfigReportLines.CatalogDeclaredTwice(typeof(SampleCatalog), "economy", "game")
            .ShouldBe("[catalog SampleCatalog] is declared by economy and game. Fix: declare it in one module; the other takes it from DI (FG-018).");
        ConfigReportLines.CatalogHostFailed("economy", typeof(SampleCatalog), new InvalidOperationException("no file"))
            .ShouldBe("[module economy] catalog SampleCatalog: the instance registered by the host could not be created: InvalidOperationException: no file. Fix: correct the registration or remove it to use the declared build (FG-018).");
    }

    [Fact]
    public void Startup_check_lines_have_the_documented_texts()
    {
        ConfigReportLines.CheckNotCreated("ThrowingCheck", new InvalidOperationException("boom"))
            .ShouldBe("[ThrowingCheck] the startup check could not be created: InvalidOperationException: boom. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).");
        ConfigReportLines.CheckNotRunConfig("NeedsConfigCheck")
            .ShouldBe("[NeedsConfigCheck] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).");
        ConfigReportLines.CheckNotRunCatalog("NeedsCatalogCheck", "ResourceCatalog")
            .ShouldBe("[NeedsCatalogCheck] the startup check did not run: catalog ResourceCatalog was not built (see the lines above). Fix: take GameConfigStatus and read the catalog only when DeclarationsPassed (FG-023).");
        ConfigReportLines.CheckNotRunProvision("NeedsConfigCheck")
            .ShouldBe("[NeedsConfigCheck] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).");
        ConfigReportLines.CheckFailed("BrokenCheck", new InvalidOperationException("boom"))
            .ShouldBe("[BrokenCheck] the startup check failed: InvalidOperationException: boom");
    }

    [Fact]
    public void The_bundle_size_line_has_the_documented_text()
    {
        ConfigReportLines.BundleTooLarge(1_048_600, 4, 1_048_576)
            .ShouldBe("[config] the client-visible bundle needs a GetConfig response of up to 1048600 bytes (4 files), over GameServer:Limits:ConfigBundleBytes = 1048576. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).");
    }

    private sealed class SampleCatalog;
}
