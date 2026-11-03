using System.Globalization;
using System.Text.Json;
using GameServer.Config;

namespace GameServer.Modules.Economy;

/// <summary>
/// The resources of <c>resources.json</c> as a cross-file rule sees them (IAP grants, game rewards and prices): read leniently,
/// so a reference is checked only against well-formed rows and a fault of <c>resources.json</c> itself is reported once, by the
/// economy rule (spec edge case "a rule reads another module's file"). Nothing is checked when the file is absent.
/// </summary>
public sealed class ResourceReferences
{
    private readonly HashSet<string> _named;
    private readonly Dictionary<string, long> _maxQuantity;

    private ResourceReferences(bool isChecked, HashSet<string> named, Dictionary<string, long> maxQuantity)
    {
        IsChecked = isChecked;
        _named = named;
        _maxQuantity = maxQuantity;
    }

    /// <summary>False when <c>resources.json</c> is not in the config: no reference is checked.</summary>
    public bool IsChecked { get; }

    public static ResourceReferences Read(ConfigContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var maxQuantity = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!context.Config.HasFile(ResourceCatalog.ConfigFile))
        {
            return new ResourceReferences(false, named, maxQuantity);
        }

        var root = context.Json(ResourceCatalog.ConfigFile);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("resources", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = id.GetString()!;
                named.Add(name);
                if (row.TryGetProperty("maxQuantity", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt64(out var value) && value >= 1)
                {
                    // A repeated id is reported by the economy rule; the first row counts here.
                    maxQuantity.TryAdd(name, value);
                }
            }
        }

        return new ResourceReferences(true, named, maxQuantity);
    }

    /// <summary>
    /// Checks <c>resource</c> and <paramref name="quantityProperty"/> of one line (a grant, reward or price): the resource must be
    /// defined; the quantity is an integer of at least 1, and at most the resource's maxQuantity when <paramref name="capAtMaxQuantity"/>.
    /// </summary>
    public void CheckLine(
        JsonElement line, string quantityProperty, ConfigLocation at, ConfigProblems problems, bool capAtMaxQuantity, string fix, string unknownFix)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var resource = ConfigJson.RequiredString(line, "resource", at, problems, fix: fix);
        if (resource is not null && IsChecked && !_named.Contains(resource))
        {
            problems.Error(at, $"resource '{resource}' is not defined in resources.json", unknownFix);
        }

        if (!capAtMaxQuantity || resource is null || !_maxQuantity.TryGetValue(resource, out var max))
        {
            ConfigJson.RequiredInteger(line, quantityProperty, at, problems, min: 1, fix: fix);
            return;
        }

        if (!line.TryGetProperty(quantityProperty, out var quantity) || quantity.ValueKind == JsonValueKind.Null)
        {
            // Missing or null: the shared wording.
            ConfigJson.RequiredInteger(line, quantityProperty, at, problems, min: 1, max: max, fix: fix);
        }
        else if (!(quantity.ValueKind == JsonValueKind.Number && quantity.TryGetInt64(out var value) && value >= 1 && value <= max))
        {
            problems.Error(
                at,
                $"'{quantityProperty}' must be an integer from 1 to {max.ToString(CultureInfo.InvariantCulture)} (maxQuantity of '{resource}'), found {Quote(quantity)}",
                fix);
        }
    }

    private static string Quote(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value.ValueKind.ToString().ToLowerInvariant() : value.GetRawText();
}
