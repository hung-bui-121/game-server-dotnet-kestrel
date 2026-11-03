using System.Text.Json.Nodes;
using GameServer.Config;
using GameServer.Modules;
using GameServer.Modules.Economy;
using GameServer.Modules.Iap;
using SampleGame.Adapters;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// FR-024 (slice 015 T025): a rule is never weaker than its catalog's <c>Load</c>. For generated configs (valid ones and
/// single-mutation variants, fixed seed), whenever the kit reports no rule line the catalogs load and the kit built them.
/// </summary>
public sealed class CatalogLoadAgreementTests
{
    /// <summary>Recorded seed of the generator; change it only together with a note in the slice tasks.</summary>
    private const int Seed = 15025;

    private const int ConfigsPerFile = 500;

    private static readonly IGameModule[] Modules = [new EconomyModule(), new IapModule(), new SampleGameModule()];

    public static TheoryData<string> Files => [ResourceCatalog.ConfigFile, IapCatalog.ConfigFile, BattleCatalog.ConfigFile, ShopCatalog.ConfigFile];

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_config_the_rules_accept_loads_and_builds_its_catalog(string file)
    {
        var random = new Random(Seed + file.Length);
        var passed = 0;
        var refused = 0;
        for (var i = 0; i < ConfigsPerFile; i++)
        {
            var files = ValidFiles(random);
            if (i % 5 != 0)
            {
                // Four of five configs carry one mutation of the file under test; one of five stays valid.
                files[file] = Mutate(files[file], random);
            }

            var config = TestGameConfig.FromJson(files.ToDictionary(f => f.Key, f => f.Value.ToJsonString()));
            var report = ConfigDeclarationCheck.Check(config, Modules);

            // Judged on the rule lines only: a Load that throws would itself show up as a catalog line.
            if (report.Errors.Any(line => !IsCatalogLine(line)))
            {
                refused++;
                continue;
            }

            passed++;
            var json = files[file].ToJsonString();
            Should.NotThrow(() => Load(file, config), $"rules accepted {file} but Load threw: {json}");
            report.Errors.ShouldBeEmpty($"rules accepted {file} but a catalog failed: {json}");
            Built(file, report).ShouldBeTrue($"rules accepted {file} but the kit built no catalog: {json}");
        }

        passed.ShouldBeGreaterThan(ConfigsPerFile / 5, "the generator must produce accepted configs");
        refused.ShouldBeGreaterThan(ConfigsPerFile / 5, "the generator must produce refused configs");
    }

    private static bool IsCatalogLine(string line) => line.Contains("] catalog ", StringComparison.Ordinal) || line.StartsWith("[catalog ", StringComparison.Ordinal);

    private static void Load(string file, IGameConfig config)
    {
        _ = file switch
        {
            ResourceCatalog.ConfigFile => (object)ResourceCatalog.Load(config),
            IapCatalog.ConfigFile => IapCatalog.Load(config),
            BattleCatalog.ConfigFile => BattleCatalog.Load(config),
            _ => ShopCatalog.Load(config),
        };
    }

    private static bool Built(string file, ConfigDeclarationReport report) => file switch
    {
        ResourceCatalog.ConfigFile => report.TryGetCatalog<ResourceCatalog>(out _),
        IapCatalog.ConfigFile => report.TryGetCatalog<IapCatalog>(out _),
        BattleCatalog.ConfigFile => report.TryGetCatalog<BattleCatalog>(out _),
        _ => report.TryGetCatalog<ShopCatalog>(out _),
    };

    private static Dictionary<string, JsonNode> ValidFiles(Random random)
    {
        var ids = new[] { "gold", "gem", "potion", "sword" };
        var resources = new JsonArray();
        foreach (var id in ids)
        {
            resources.Add(new JsonObject
            {
                ["id"] = id,
                ["kind"] = random.Next(2) == 0 ? "currency" : "item",
                ["maxQuantity"] = random.Next(1, 1_000_000),
                ["displayName"] = id.ToUpperInvariant(),
            });
        }

        var products = new JsonObject();
        var productCount = random.Next(0, 4);
        for (var p = 0; p < productCount; p++)
        {
            products[$"{(random.Next(2) == 0 ? "google" : "apple")}:product_{p}"] = new JsonObject
            {
                ["grants"] = new JsonArray(new JsonObject { ["resource"] = ids[random.Next(ids.Length)], ["quantity"] = 1 }),
            };
        }

        var maxStage = random.Next(1, 4);
        var stages = new JsonArray();
        for (var s = 1; s <= maxStage; s++)
        {
            stages.Add(new JsonObject
            {
                ["stage"] = s,
                ["xp"] = random.Next(0, 500),
                ["rewards"] = new JsonArray(new JsonObject { ["resource"] = ids[random.Next(ids.Length)], ["quantity"] = random.Next(1, 100) }),
                ["drops"] = new JsonArray(),
            });
        }

        var items = new JsonObject();
        var itemCount = random.Next(1, 4);
        for (var n = 0; n < itemCount; n++)
        {
            items[$"item_{n}"] = new JsonObject
            {
                ["price"] = new JsonObject { ["resource"] = ids[random.Next(ids.Length)], ["amount"] = random.Next(1, 1000) },
                ["grant"] = new JsonObject { ["resource"] = ids[random.Next(ids.Length)], ["quantity"] = 1 },
            };
        }

        return new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [ResourceCatalog.ConfigFile] = new JsonObject { ["resources"] = resources },
            [IapCatalog.ConfigFile] = new JsonObject { ["products"] = products },
            [BattleCatalog.ConfigFile] = new JsonObject { ["maxStage"] = maxStage, ["battleHours"] = random.Next(1, 169), ["stages"] = stages },
            [ShopCatalog.ConfigFile] = new JsonObject { ["items"] = items },
        };
    }

    /// <summary>One mutation of a random node: a type swap, a removal, an extreme value, a duplicated row or a renamed property.</summary>
    private static JsonNode Mutate(JsonNode root, Random random)
    {
        var containers = new List<JsonNode>();
        Collect(root, containers);
        var target = containers[random.Next(containers.Count)];
        if (target is JsonArray array)
        {
            if (array.Count > 0 && random.Next(2) == 0)
            {
                array.Add(array[random.Next(array.Count)]!.DeepClone());
            }
            else
            {
                array.Add(Extreme(random));
            }

            return root;
        }

        var obj = (JsonObject)target;
        if (obj.Count == 0)
        {
            obj["x"] = Extreme(random);
            return root;
        }

        var name = obj.ElementAt(random.Next(obj.Count)).Key;
        switch (random.Next(4))
        {
            case 0:
                obj.Remove(name);
                break;
            case 1:
                var value = obj[name];
                obj.Remove(name);
                obj[char.ToUpperInvariant(name[0]) + name[1..]] = value;
                break;
            default:
                obj[name] = Extreme(random);
                break;
        }

        return root;
    }

    private static void Collect(JsonNode node, List<JsonNode> containers)
    {
        switch (node)
        {
            case JsonObject obj:
                containers.Add(obj);
                foreach (var (_, child) in obj.ToArray())
                {
                    if (child is not null)
                    {
                        Collect(child, containers);
                    }
                }

                break;
            case JsonArray array:
                containers.Add(array);
                foreach (var child in array.ToArray())
                {
                    if (child is not null)
                    {
                        Collect(child, containers);
                    }
                }

                break;
        }
    }

    private static JsonNode? Extreme(Random random) => random.Next(16) switch
    {
        0 => null,
        1 => JsonValue.Create(0),
        2 => JsonValue.Create(-1),
        3 => JsonValue.Create(long.MaxValue),
        4 => JsonValue.Create(int.MaxValue + 1L),
        5 => JsonValue.Create(1.5),
        6 => JsonNode.Parse("1.0"),
        7 => JsonNode.Parse("1e3"),
        8 => JsonValue.Create("5"),
        9 => JsonValue.Create(string.Empty),
        10 => JsonValue.Create("unknown_item"),
        11 => JsonValue.Create(true),
        12 => new JsonArray(),
        13 => new JsonObject(),
        14 => JsonValue.Create("CURRENCY"),
        _ => JsonValue.Create(1),
    };
}
