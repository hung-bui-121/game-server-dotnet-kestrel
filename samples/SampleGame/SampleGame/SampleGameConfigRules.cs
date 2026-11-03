using System.Text.Json;
using GameServer.Config;
using GameServer.Modules.Economy;

namespace SampleGame;

/// <summary>Content rules of <c>battle.json</c> and <c>shop.json</c> (FR-070, data-model §2.2), checked at every start (FG-018).</summary>
public static class SampleGameConfigRules
{
    /// <summary>Longest battle a stage may declare (one week).</summary>
    public const int MaxBattleHours = 168;

    /// <summary>Missing stages listed one per line up to this count; more give one summary line (a typo in maxStage).</summary>
    private const int MaxListedMissingStages = 10;

    private const string LineFix = "name a resource of resources.json with a quantity of at least 1";
    private const string UnknownFix = "add it to resources.json or correct the line (ECO-07)";

    /// <summary>Rule <c>sample.battle</c>: maxStage, battleHours, one row per stage with xp, rewards and drops.</summary>
    public static ConfigRule Battle { get; } = new("sample.battle", CheckBattle);

    /// <summary>Rule <c>sample.shop</c>: every item has a price and a grant naming resources of <c>resources.json</c>.</summary>
    public static ConfigRule Shop { get; } = new("sample.shop", CheckShop);

    private static void CheckBattle(ConfigContext context)
    {
        var problems = context.Problems;
        var root = context.Json(BattleCatalog.ConfigFile);
        var maxStage = ConfigJson.RequiredInteger(root, "maxStage", ConfigLocation.WholeFile, problems, 1, int.MaxValue, "set the highest stage, at least 1");
        ConfigJson.OptionalInteger(root, "battleHours", ConfigLocation.WholeFile, problems, 1, MaxBattleHours, $"set how long a battle may last, from 1 to {MaxBattleHours} hours");
        if (!ConfigJson.TryGetArray(root, "stages", ConfigLocation.WholeFile, problems, out var stages, "list the stages in a \"stages\" array"))
        {
            return;
        }

        var resources = ResourceReferences.Read(context);
        var firstRow = new Dictionary<long, int>();
        var number = 0;
        foreach (var row in stages.EnumerateArray())
        {
            var at = ConfigLocation.Row(++number);
            if (!ConfigJson.RequireObject(row, at, problems, "write each stage as an object"))
            {
                continue;
            }

            var stage = ConfigJson.RequiredInteger(row, "stage", at, problems, 1, maxStage ?? int.MaxValue, "number the stages from 1 to maxStage");
            if (stage is { } value && !firstRow.TryAdd(value, number))
            {
                problems.Error(at, $"stage {value} is already defined in row {firstRow[value]}", "give every stage one row");
            }

            ConfigJson.RequiredInteger(row, "xp", at, problems, min: 0, fix: "give every stage an integer xp of at least 0");
            CheckLines(row, "rewards", number - 1, problems, resources);
            CheckLines(row, "drops", number - 1, problems, resources);
        }

        if (maxStage is { } max)
        {
            CheckEveryStageHasARow(max, firstRow, problems);
        }
    }

    /// <summary>One line per missing stage; a single line when more than <see cref="MaxListedMissingStages"/> are missing.</summary>
    private static void CheckEveryStageHasARow(long maxStage, Dictionary<long, int> rows, ConfigProblems problems)
    {
        const string fix = "add a row for every stage from 1 to maxStage";
        var missing = maxStage - rows.Count;
        if (missing <= 0)
        {
            return;
        }

        if (missing > MaxListedMissingStages)
        {
            var first = 1L;
            while (rows.ContainsKey(first))
            {
                first++;
            }

            problems.Error(ConfigLocation.WholeFile, $"{missing} stages from 1 to {maxStage} have no row, the first is stage {first}", fix);
            return;
        }

        for (var stage = 1L; stage <= maxStage; stage++)
        {
            if (!rows.ContainsKey(stage))
            {
                problems.Error(ConfigLocation.WholeFile, $"stage {stage} has no row", fix);
            }
        }
    }

    private static void CheckLines(JsonElement row, string property, int index, ConfigProblems problems, ResourceReferences resources)
    {
        if (!ConfigJson.TryGetArray(row, property, ConfigLocation.Row(index + 1), problems, out var lines, $"list the stage {property} in an array (may be empty)"))
        {
            return;
        }

        var position = 0;
        foreach (var line in lines.EnumerateArray())
        {
            var at = ConfigLocation.Path(ConfigJson.PathOf("stages", index, property, position++));
            if (ConfigJson.RequireObject(line, at, problems, "write each line as {\"resource\": …, \"quantity\": …}"))
            {
                resources.CheckLine(line, "quantity", at, problems, capAtMaxQuantity: false, LineFix, UnknownFix);
            }
        }
    }

    private static void CheckShop(ConfigContext context)
    {
        var problems = context.Problems;
        if (!ConfigJson.TryGetObject(context.Json(ShopCatalog.ConfigFile), "items", ConfigLocation.WholeFile, problems, out var items, "map item ids to shop items in an \"items\" object"))
        {
            return;
        }

        var resources = ResourceReferences.Read(context);
        foreach (var item in items.EnumerateObject())
        {
            var at = ConfigLocation.Key(item.Name);
            if (item.Name.Length == 0)
            {
                problems.Error(at, "the item id is empty", "give every shop item an id");
            }

            if (!ConfigJson.RequireObject(item.Value, at, problems, "write each shop item as an object with \"price\" and \"grant\""))
            {
                continue;
            }

            if (ConfigJson.TryGetObject(item.Value, "price", at, problems, out var price, "give the item a price {\"resource\": …, \"amount\": …}"))
            {
                var priceAt = ConfigLocation.Path(ConfigJson.PathOf("items", item.Name, "price"));
                resources.CheckLine(price, "amount", priceAt, problems, capAtMaxQuantity: false, "set the price to a resource of resources.json and an amount of at least 1", UnknownFix);
            }

            if (ConfigJson.TryGetObject(item.Value, "grant", at, problems, out var grant, "give the item a grant {\"resource\": …, \"quantity\": …}"))
            {
                var grantAt = ConfigLocation.Path(ConfigJson.PathOf("items", item.Name, "grant"));
                resources.CheckLine(grant, "quantity", grantAt, problems, capAtMaxQuantity: true, "correct the grant", UnknownFix);
            }
        }
    }
}
