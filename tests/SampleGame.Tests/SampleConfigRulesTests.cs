using GameServer.Modules.Economy;
using SampleGame.Adapters;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// FR-070, US1-3, US6-1 (slice 015 T024): rules <c>sample.battle</c> and <c>sample.shop</c> with the economy module; rule
/// <c>sample.account</c> of the optional <c>account.json</c> (slice 016 T063, errors-and-report-lines §4.3).
/// </summary>
public sealed class SampleConfigRulesTests
{
    private const string Resources = """
        {"resources": [
          {"id": "gold", "kind": "currency", "maxQuantity": 1000000000, "displayName": "Gold"},
          {"id": "potion", "kind": "item", "maxQuantity": 999, "displayName": "Potion"}
        ]}
        """;

    private const string Shop = """{"items": {"potion_pack": {"price": {"resource": "gold", "amount": 60}, "grant": {"resource": "potion", "quantity": 1}}}}""";

    private const string Battle = "[module sample.game] config battle.json";
    private const string ShopLine = "[module sample.game] config shop.json";
    private const string AccountLine = "[module sample.game] config account.json";

    [Theory]
    [InlineData("\"maxStage\": 0,", ": 'maxStage' must be an integer from 1 to 2147483647, found 0. Fix: set the highest stage, at least 1.")]
    [InlineData("\"maxStage\": 1, \"battleHours\": 169,", ": 'battleHours' must be an integer from 1 to 168, found 169. Fix: set how long a battle may last, from 1 to 168 hours.")]
    public void Battle_settings_are_checked(string settings, string line)
    {
        Errors(battle: $$"""{{{settings}} "stages": [{"stage": 1, "xp": 0, "rewards": [], "drops": []}]}""").ShouldBe([Battle + line]);
    }

    [Fact]
    public void Battle_hours_may_be_absent()
    {
        Errors(battle: """{"maxStage": 1, "stages": [{"stage": 1, "xp": 0, "rewards": [], "drops": []}]}""").ShouldBeEmpty();
    }

    [Fact]
    public void A_stage_row_reports_duplicate_out_of_range_string_xp_and_missing_stages()
    {
        Errors(battle: """
            {"maxStage": 3, "stages": [
              {"stage": 1, "xp": 40, "rewards": [], "drops": []},
              {"stage": 2, "xp": "70", "rewards": [], "drops": []},
              {"stage": 1, "xp": 0, "rewards": [], "drops": []},
              {"stage": 4, "xp": 0, "rewards": [], "drops": []}
            ]}
            """).ShouldBe(
        [
            Battle + " row 2: 'xp' must be an integer of at least 0, found \"70\". Fix: give every stage an integer xp of at least 0.",
            Battle + " row 3: stage 1 is already defined in row 1. Fix: give every stage one row.",
            Battle + " row 4: 'stage' must be an integer from 1 to 3, found 4. Fix: number the stages from 1 to maxStage.",
            Battle + ": stage 3 has no row. Fix: add a row for every stage from 1 to maxStage.",
        ]);
    }

    [Fact]
    public void Rewards_and_drops_name_resources_with_a_positive_quantity()
    {
        Errors(battle: """
            {"maxStage": 1, "stages": [
              {"stage": 1, "xp": 0, "rewards": [{"resource": "shield", "quantity": 1}], "drops": [{"resource": "potion", "quantity": 0}]}
            ]}
            """).ShouldBe(
        [
            Battle + " at $.stages[0].rewards[0]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the line (ECO-07).",
            Battle + " at $.stages[0].drops[0]: 'quantity' must be an integer of at least 1, found 0. Fix: name a resource of resources.json with a quantity of at least 1.",
        ]);
    }

    [Fact]
    public void A_mistyped_max_stage_gives_one_summary_line()
    {
        Errors(battle: """{"maxStage": 1000000, "stages": [{"stage": 1, "xp": 0, "rewards": [], "drops": []}]}""")
            .ShouldBe([Battle + ": 999999 stages from 1 to 1000000 have no row, the first is stage 2. Fix: add a row for every stage from 1 to maxStage."]);
    }

    [Theory]
    [InlineData("""{"items": []}""", ": 'items' must be an object, found array. Fix: map item ids to shop items in an \"items\" object.")]
    [InlineData("""{"items": {"p": {"price": {"resource": "gold", "amount": 0}, "grant": {"resource": "potion", "quantity": 1}}}}""",
        " at $.items.p.price: 'amount' must be an integer of at least 1, found 0. Fix: set the price to a resource of resources.json and an amount of at least 1.")]
    [InlineData("""{"items": {"broken": {"price": {"resource": "gold", "amount": 1}, "grant": {"resource": "unknown_item", "quantity": 1}}}}""",
        " at $.items.broken.grant: resource 'unknown_item' is not defined in resources.json. Fix: add it to resources.json or correct the line (ECO-07).")]
    [InlineData("""{"items": {"p": {"price": {"resource": "gold", "amount": 1}, "grant": {"resource": "potion", "quantity": 1000}}}}""",
        " at $.items.p.grant: 'quantity' must be an integer from 1 to 999 (maxQuantity of 'potion'), found 1000. Fix: correct the grant.")]
    [InlineData("""{"items": {"p": {"grant": {"resource": "potion", "quantity": 1}}}}""",
        " key 'p': 'price' is missing. Fix: give the item a price {\"resource\": …, \"amount\": …}.")]
    public void Shop_items_are_checked(string shop, string line)
    {
        Errors(shop: shop).ShouldBe([ShopLine + line]);
    }

    [Theory]
    [InlineData("""{"startingGold": -1}""", "-1")]
    [InlineData("""{"startingGold": "100"}""", "\"100\"")]
    [InlineData("""{"startingGold": 1000000001}""", "1000000001")]
    [InlineData("""{"startingGold": 1.5}""", "1.5")]
    [InlineData("""{"startingGold": null}""", "null")]
    [InlineData("""{"gold": 100}""", "nothing")]
    public void Starting_gold_is_an_integer_from_0_to_the_gold_max_quantity(string account, string found)
    {
        Errors(account: account).ShouldBe(
        [
            AccountLine + $": startingGold must be an integer from 0 to 1000000000 (the maxQuantity of gold in resources.json); found {found}. " +
            "Fix: set the gold a new account starts with.",
        ]);
    }

    [Theory]
    [InlineData("""{"startingGold": 0}""")]
    [InlineData("""{"startingGold": 1000000000}""")]
    public void Starting_gold_at_both_ends_of_the_range_passes(string account)
    {
        Errors(account: account).ShouldBeEmpty();
    }

    [Fact]
    public void An_account_file_that_is_not_an_object_or_grants_an_undefined_gold_is_reported()
    {
        Errors(account: "[100]").ShouldBe([AccountLine + ": the file must be an object, found array. Fix: write {\"startingGold\": …}."]);
        Errors(account: """{"startingGold": 100}""", resources: """{"resources": [{"id": "potion", "kind": "item", "maxQuantity": 999, "displayName": "Potion"}]}""")
            .ShouldContain(AccountLine + ": startingGold grants 'gold', which is not defined in resources.json. Fix: add it to resources.json or remove account.json.");
    }

    [Fact]
    public void A_gold_row_without_a_valid_max_quantity_is_reported_once_by_the_economy_rule()
    {
        var errors = Errors(
            account: """{"startingGold": -5}""",
            resources: """{"resources": [{"id": "gold", "kind": "currency", "maxQuantity": 0, "displayName": "Gold"}, {"id": "potion", "kind": "item", "maxQuantity": 999, "displayName": "Potion"}]}""");

        errors.ShouldNotContain(e => e.StartsWith(AccountLine, StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("[module economy] config resources.json row 1:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("samples/SampleHost/config")]
    [InlineData("tests/Shared/config")]
    public void The_committed_sample_and_test_configs_pass(string folder)
    {
        var report = ConfigDeclarationCheck.Check(
            TestGameConfig.FromDirectory(Path.Combine(RepositoryRoot(), folder)), new EconomyModule(), new GameServer.Modules.Iap.IapModule(), new SampleGameModule());

        report.Errors.ShouldBeEmpty();
        report.Catalog<BattleCatalog>().MaxStage.ShouldBe(3);
        report.Catalog<ShopCatalog>().Items.ShouldNotBeEmpty();
    }

    private static IReadOnlyList<string> Errors(string? battle = null, string? shop = null, string? account = null, string? resources = null)
    {
        var files = new Dictionary<string, string>
        {
            [ResourceCatalog.ConfigFile] = resources ?? Resources,
            [BattleCatalog.ConfigFile] = battle ?? """{"maxStage": 1, "stages": [{"stage": 1, "xp": 0, "rewards": [], "drops": []}]}""",
            [ShopCatalog.ConfigFile] = shop ?? Shop,
        };
        if (account is not null)
        {
            files[SampleAccountInitializer.ConfigFile] = account;
        }

        return ConfigDeclarationCheck.Check(TestGameConfig.FromJson(files), new EconomyModule(), new SampleGameModule()).Errors;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("GameServer.slnx not found above the test output.");
    }
}
