using System.Text.Json;
using GameServer.Config;
using GameServer.Modules.Economy;

namespace GameServer.Modules.Iap;

/// <summary>Content rule of <c>iap.json</c> (FR-016, data-model §2.2): checked at every start, before traffic (FG-018).</summary>
public static class IapConfigRules
{
    /// <summary>Longest store product id accepted in a key (Google Play and App Store ids are shorter).</summary>
    public const int MaxProductIdLength = 128;

    private const string GrantFix = "correct the grant (IAP-01)";

    private static readonly string[] Stores = ["google", "apple"];

    /// <summary>
    /// Rule <c>iap.products</c>: <c>"{store}:{productId}"</c> keys, non-empty grants, each naming a resource of
    /// <c>resources.json</c> (well-formed rows only) with a quantity from 1 to that resource's maxQuantity.
    /// </summary>
    public static ConfigRule Products { get; } = new("iap.products", CheckProducts);

    private static void CheckProducts(ConfigContext context)
    {
        var problems = context.Problems;
        if (!ConfigJson.TryGetObject(context.Json(IapCatalog.ConfigFile), "products", ConfigLocation.WholeFile, problems, out var products, "map \"{store}:{productId}\" keys to products (IAP-01)"))
        {
            return;
        }

        var resources = ResourceReferences.Read(context);
        foreach (var product in products.EnumerateObject())
        {
            var at = ConfigLocation.Key(product.Name);
            if (!IsValidKey(product.Name))
            {
                problems.Error(
                    at,
                    "the key must be \"google:{productId}\" or \"apple:{productId}\" with a product id of 1 to 128 characters",
                    "correct the key (IAP-01)");
            }

            CheckGrants(product, at, problems, resources);
        }
    }

    private static void CheckGrants(JsonProperty product, ConfigLocation at, ConfigProblems problems, ResourceReferences resources)
    {
        if (product.Value.ValueKind != JsonValueKind.Object)
        {
            problems.Error(at, $"the product must be an object, found {Kind(product.Value)}", "write each product as an object with \"grants\" (IAP-01)");
            return;
        }

        if (!product.Value.TryGetProperty("grants", out var grants) || grants.ValueKind != JsonValueKind.Array || grants.GetArrayLength() == 0)
        {
            problems.Error(at, "'grants' must be a non-empty array", "list what the product grants (IAP-01)");
            return;
        }

        var index = 0;
        foreach (var grant in grants.EnumerateArray())
        {
            var grantAt = ConfigLocation.Path(ConfigJson.PathOf("products", product.Name, "grants", index++));
            if (grant.ValueKind != JsonValueKind.Object)
            {
                problems.Error(grantAt, $"the grant must be an object, found {Kind(grant)}", GrantFix);
                continue;
            }

            resources.CheckLine(grant, "quantity", grantAt, problems, capAtMaxQuantity: true, GrantFix, "add it to resources.json or correct the grant (ECO-07, IAP-01)");
        }
    }

    private static bool IsValidKey(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        var productId = key.Length - colon - 1;
        return Stores.Contains(key[..colon], StringComparer.Ordinal) && productId is >= 1 and <= MaxProductIdLength;
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };
}
