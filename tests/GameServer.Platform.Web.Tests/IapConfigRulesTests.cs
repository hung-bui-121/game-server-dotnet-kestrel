using GameServer.Modules.Economy;
using GameServer.Modules.Iap;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>FR-016, FR-017, US1-2, BC-1 (slice 015 T023): rule <c>iap.products</c> with the economy module.</summary>
public sealed class IapConfigRulesTests
{
    private const string Resources = """
        {"resources": [
          {"id": "gem", "kind": "currency", "maxQuantity": 1000000, "displayName": "Gem"},
          {"id": "sword", "kind": "item", "maxQuantity": 10, "displayName": "Sword"}
        ]}
        """;

    private const string Prefix = "[module iap] config iap.json";
    private const string KeyLine = ": the key must be \"google:{productId}\" or \"apple:{productId}\" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).";

    [Theory]
    [InlineData("gems_100")]
    [InlineData("steam:gems_100")]
    [InlineData("google:")]
    [InlineData("Google:gems_100")]
    public void A_key_without_a_known_store_or_product_id_is_one_line(string key)
    {
        Errors(Products(key, Grant("gem", 1))).ShouldBe([$"{Prefix} key '{key}'{KeyLine}"]);
    }

    [Fact]
    public void A_product_id_of_128_characters_passes_and_129_does_not()
    {
        var at = "google:" + new string('x', 128);
        var over = "google:" + new string('x', 129);

        Errors(Products(at, Grant("gem", 1))).ShouldBeEmpty();
        Errors(Products(over, Grant("gem", 1))).ShouldBe([$"{Prefix} key '{over}'{KeyLine}"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"grants": []}""")]
    [InlineData("""{"grants": {}}""")]
    public void Grants_must_be_a_non_empty_array(string product)
    {
        Errors("{\"products\": {\"google:p\": " + product + "}}")
            .ShouldBe([$"{Prefix} key 'google:p': 'grants' must be a non-empty array. Fix: list what the product grants (IAP-01)."]);
    }

    [Fact]
    public void An_unknown_resource_is_a_path_line()
    {
        Errors(Products("google:starter_pack", Grant("gem", 1), Grant("shield", 1))).ShouldBe(
            [$"{Prefix} at $.products['google:starter_pack'].grants[1]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01)."]);
    }

    [Theory]
    [InlineData("0", "found 0")]
    [InlineData("1.0", "found 1.0")]
    [InlineData("1000001", "found 1000001")]
    [InlineData("\"5\"", "found \"5\"")]
    public void A_quantity_outside_1_to_max_quantity_is_one_line(string quantity, string found)
    {
        Errors("{\"products\": {\"google:gems_100\": {\"grants\": [{\"resource\": \"gem\", \"quantity\": " + quantity + "}]}}}").ShouldBe(
            [$"{Prefix} at $.products['google:gems_100'].grants[0]: 'quantity' must be an integer from 1 to 1000000 (maxQuantity of 'gem'), {found}. Fix: correct the grant (IAP-01)."]);
    }

    [Fact]
    public void A_missing_quantity_and_an_empty_product_id_use_the_shared_wording()
    {
        Errors("""{"products": {"apple:p": {"grants": [{"resource": "gem"}]}}}""")
            .ShouldBe([$"{Prefix} at $.products['apple:p'].grants[0]: 'quantity' is missing. Fix: correct the grant (IAP-01)."]);
        Errors(Products("apple:", Grant("gem", 1))).ShouldBe([$"{Prefix} key 'apple:'{KeyLine}"]);
    }

    [Fact]
    public void A_malformed_resources_row_gives_no_reference_line_for_that_resource()
    {
        var malformed = """{"resources": [{"id": "gem", "kind": "currency", "maxQuantity": "lots", "displayName": "Gem"}]}""";

        var report = Check(malformed, Products("google:p", Grant("gem", 5)));

        report.Errors.ShouldBe(["[module economy] config resources.json row 1: 'maxQuantity' must be an integer of at least 1, found \"lots\". Fix: set the largest quantity a player may hold (ECO-07)."]);
    }

    [Fact]
    public void An_empty_products_object_passes_and_builds_the_catalog()
    {
        var report = Check(Resources, """{"products": {}}""");

        report.Errors.ShouldBeEmpty();
        report.Catalog<IapCatalog>().Products.ShouldBeEmpty();
    }

    [Fact]
    public void Without_resources_json_no_reference_is_checked()
    {
        var report = ConfigDeclarationCheck.Check(
            TestGameConfig.FromJson(new Dictionary<string, string> { [IapCatalog.ConfigFile] = Products("google:p", Grant("anything", 5)) }), new IapModule());

        report.Errors.ShouldBeEmpty();
    }

    private static string Grant(string resource, long quantity) => $$"""{"resource": "{{resource}}", "quantity": {{quantity}}}""";

    private static string Products(string key, params string[] grants) => "{\"products\": {\"" + key + "\": {\"grants\": [" + string.Join(", ", grants) + "]}}}";

    private static IReadOnlyList<string> Errors(string iap) => Check(Resources, iap).Errors;

    private static ConfigDeclarationReport Check(string resources, string iap) =>
        ConfigDeclarationCheck.Check(
            TestGameConfig.FromJson(new Dictionary<string, string> { [ResourceCatalog.ConfigFile] = resources, [IapCatalog.ConfigFile] = iap }),
            new EconomyModule(),
            new IapModule());
}
