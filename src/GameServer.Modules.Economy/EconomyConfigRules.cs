using System.Text.Json;
using GameServer.Config;

namespace GameServer.Modules.Economy;

/// <summary>Content rule of <c>resources.json</c> (FR-015, data-model §2.2): checked at every start, before traffic (FG-018).</summary>
public static class EconomyConfigRules
{
    internal const string RowFix = "write each resource as an object (ECO-07)";
    internal const string MaxQuantityFix = "set the largest quantity a player may hold (ECO-07)";

    private static readonly string[] Kinds = ["currency", "item"];

    /// <summary>Rule <c>economy.resources</c>: rows, unique ids, kind, maxQuantity ≥ 1 and displayName.</summary>
    public static ConfigRule Resources { get; } = new("economy.resources", CheckResources);

    private static void CheckResources(ConfigContext context)
    {
        var problems = context.Problems;
        if (!ConfigJson.TryGetArray(context.Json(ResourceCatalog.ConfigFile), "resources", ConfigLocation.WholeFile, problems, out var rows, "put the resource rows in a \"resources\" array (ECO-07)"))
        {
            return;
        }

        var firstRow = new Dictionary<string, int>(StringComparer.Ordinal);
        var number = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var at = ConfigLocation.Row(++number);
            if (!ConfigJson.RequireObject(row, at, problems, RowFix))
            {
                continue;
            }

            if (ConfigJson.RequiredString(row, "id", at, problems, fix: "give every resource a non-empty id (ECO-07)") is { } id && !firstRow.TryAdd(id, number))
            {
                problems.Error(at, $"id '{id}' is already defined in row {firstRow[id]}", "give every resource a unique id (ECO-07)");
            }

            CheckKind(row, at, problems);
            ConfigJson.RequiredInteger(row, "maxQuantity", at, problems, min: 1, fix: MaxQuantityFix);
            ConfigJson.RequiredString(row, "displayName", at, problems, allowEmpty: true, fix: "give every resource a display name (ECO-07)");
        }
    }

    /// <summary>A string matching "currency" or "item" case-insensitively, as the catalog reads it; an integer kind is refused (D3).</summary>
    private static void CheckKind(JsonElement row, ConfigLocation at, ConfigProblems problems)
    {
        const string fix = "use \"currency\" or \"item\" (ECO-07)";
        if (ConfigJson.RequiredString(row, "kind", at, problems, allowEmpty: true, fix: fix) is { } kind
            && !Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            problems.Error(at, $"'kind' is '{kind}'", fix);
        }
    }
}
