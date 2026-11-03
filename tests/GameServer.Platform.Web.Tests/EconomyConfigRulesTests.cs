using GameServer.Modules.Economy;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-015, FR-017, BC-1 (slice 015 T022): rule <c>economy.resources</c>, line by line (startup-report §4).</summary>
public sealed class EconomyConfigRulesTests
{
    private const string Prefix = "[module economy] config resources.json";
    private const string MaxFix = "Fix: set the largest quantity a player may hold (ECO-07).";

    [Theory]
    [InlineData("""{"resources": {}}""", ": 'resources' must be an array, found object. Fix: put the resource rows in a \"resources\" array (ECO-07).")]
    [InlineData("""{}""", ": 'resources' is missing. Fix: put the resource rows in a \"resources\" array (ECO-07).")]
    [InlineData("""{"resources": [5]}""", " row 1: the row must be an object, found number. Fix: write each resource as an object (ECO-07).")]
    public void The_rows_must_be_objects_in_an_array(string json, string line)
    {
        Errors(json).ShouldBe([Prefix + line]);
    }

    [Theory]
    [InlineData("\"kind\": \"currency\", \"maxQuantity\": 5, \"displayName\": \"G\"", " row 1: 'id' is missing. Fix: give every resource a non-empty id (ECO-07).")]
    [InlineData("\"id\": null, \"kind\": \"currency\", \"maxQuantity\": 5, \"displayName\": \"G\"", " row 1: 'id' is null. Fix: give every resource a non-empty id (ECO-07).")]
    [InlineData("\"id\": 7, \"kind\": \"currency\", \"maxQuantity\": 5, \"displayName\": \"G\"", " row 1: 'id' must be a string, found number. Fix: give every resource a non-empty id (ECO-07).")]
    [InlineData("\"id\": \"gold\", \"maxQuantity\": 5, \"displayName\": \"G\"", " row 1: 'kind' is missing. Fix: use \"currency\" or \"item\" (ECO-07).")]
    [InlineData("\"id\": \"gold\", \"kind\": \"gem\", \"maxQuantity\": 5, \"displayName\": \"G\"", " row 1: 'kind' is 'gem'. Fix: use \"currency\" or \"item\" (ECO-07).")]
    [InlineData("\"id\": \"gold\", \"kind\": 0, \"maxQuantity\": 5, \"displayName\": \"G\"", " row 1: 'kind' must be a string, found number. Fix: use \"currency\" or \"item\" (ECO-07).")]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"displayName\": \"G\"", " row 1: 'maxQuantity' is missing. " + MaxFix)]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": null, \"displayName\": \"G\"", " row 1: 'maxQuantity' is null. " + MaxFix)]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": 0, \"displayName\": \"G\"", " row 1: 'maxQuantity' must be an integer of at least 1, found 0. " + MaxFix)]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": -1, \"displayName\": \"G\"", " row 1: 'maxQuantity' must be an integer of at least 1, found -1. " + MaxFix)]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": 1.0, \"displayName\": \"G\"", " row 1: 'maxQuantity' must be an integer of at least 1, found 1.0. " + MaxFix)]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": \"5\", \"displayName\": \"G\"", " row 1: 'maxQuantity' must be an integer of at least 1, found \"5\". " + MaxFix)]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": 5", " row 1: 'displayName' is missing. Fix: give every resource a display name (ECO-07).")]
    [InlineData("\"id\": \"gold\", \"kind\": \"currency\", \"maxQuantity\": 5, \"displayName\": 3", " row 1: 'displayName' must be a string, found number. Fix: give every resource a display name (ECO-07).")]
    public void Each_wrong_property_of_a_row_is_one_line(string row, string line)
    {
        Errors($$"""{"resources": [{{{row}}}]}""").ShouldBe([Prefix + line]);
    }

    [Fact]
    public void A_duplicate_id_is_reported_on_the_later_row_naming_the_first()
    {
        Errors("""
            {"resources": [
              {"id": "gold", "kind": "currency", "maxQuantity": 5, "displayName": "G"},
              {"id": "gem", "kind": "currency", "maxQuantity": 5, "displayName": "E"},
              {"id": "gold", "kind": "item", "maxQuantity": 5, "displayName": "G2"}
            ]}
            """).ShouldBe([Prefix + " row 3: id 'gold' is already defined in row 1. Fix: give every resource a unique id (ECO-07)."]);
    }

    [Fact]
    public void Kind_matches_case_insensitively_and_unknown_properties_are_accepted()
    {
        var report = Check("""
            {"resources": [
              {"id": "CASH", "kind": "CURRENCY", "maxQuantity": 1000000000, "displayName": "Cash", "icon": "cash.png"},
              {"id": "GOLD", "kind": "currency", "maxQuantity": 1000000000, "displayName": "Gold"},
              {"id": "potion", "kind": "Item", "maxQuantity": 1, "displayName": ""}
            ]}
            """);

        report.Errors.ShouldBeEmpty();
        report.Catalog<ResourceCatalog>().Find("CASH")!.Kind.ShouldBe(ResourceKind.Currency);
    }

    [Fact]
    public void A_file_with_rule_errors_builds_no_catalog()
    {
        Check("""{"resources": [{"id": "gold"}]}""").TryGetCatalog<ResourceCatalog>(out _).ShouldBeFalse();
    }

    private static IReadOnlyList<string> Errors(string resources) => Check(resources).Errors;

    private static ConfigDeclarationReport Check(string resources) =>
        ConfigDeclarationCheck.Check(TestGameConfig.FromJson(new Dictionary<string, string> { [ResourceCatalog.ConfigFile] = resources }), new EconomyModule());
}
