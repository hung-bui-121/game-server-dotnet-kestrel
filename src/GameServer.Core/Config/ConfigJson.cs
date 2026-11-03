using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameServer.Config;

/// <summary>
/// Small JSON readers with the framework's wording (FR-017): a missing, null or wrongly typed value reports one problem
/// naming the property and returns null/false. Unknown properties are never reported. Without a <c>fix</c> the line says
/// "set '{property}' to {a type}".
/// </summary>
public static class ConfigJson
{
    /// <summary>Longest raw JSON value quoted in a problem; longer values are cut with "…".</summary>
    private const int MaxQuotedValueLength = 40;

    public static bool TryGetObject(JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, out JsonElement value, string? fix = null) =>
        TryGetKind(owner, property, at, problems, JsonValueKind.Object, "an object", fix, out value);

    public static bool TryGetArray(JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, out JsonElement value, string? fix = null) =>
        TryGetKind(owner, property, at, problems, JsonValueKind.Array, "an array", fix, out value);

    public static string? RequiredString(JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, bool allowEmpty = false, string? fix = null)
    {
        if (!TryGetKind(owner, property, at, problems, JsonValueKind.String, "a string", fix, out var value))
        {
            return null;
        }

        var text = value.GetString()!;
        if (!allowEmpty && text.Length == 0)
        {
            problems.Error(at, $"'{property}' must not be empty", fix ?? DefaultFix(property, "a non-empty string"));
            return null;
        }

        return text;
    }

    /// <summary>A JSON number readable as Int64 within [min, max]; 1.0, 1e3 and strings are problems.</summary>
    public static long? RequiredInteger(
        JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, long min = long.MinValue, long max = long.MaxValue, string? fix = null) =>
        Integer(owner, property, at, problems, min, max, fix, required: true);

    /// <summary>Absent → null without a problem; present but wrong → problem.</summary>
    public static long? OptionalInteger(
        JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, long min = long.MinValue, long max = long.MaxValue, string? fix = null) =>
        Integer(owner, property, at, problems, min, max, fix, required: false);

    /// <summary>Requires <paramref name="element"/> to be an object (row of an array); reports at <paramref name="at"/> otherwise.</summary>
    public static bool RequireObject(JsonElement element, ConfigLocation at, ConfigProblems problems, string? fix = null)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (element.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        problems.Error(at, $"the row must be an object, found {KindOf(element)}", fix ?? "write each row as an object");
        return false;
    }

    /// <summary><c>$.a.b</c>, <c>$.items['key with.dot']</c>, <c>$.rows[2]</c> (0-based index, as JSON paths are).</summary>
    public static string PathOf(params object[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var path = new StringBuilder("$");
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case int index:
                    path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case string name when IsIdentifier(name):
                    path.Append('.').Append(name);
                    break;
                case string name:
                    path.Append("['").Append(name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)).Append("']");
                    break;
                default:
                    throw new ArgumentException($"A JSON path segment must be a property name (string) or an array index (int), found {segment?.GetType().Name ?? "null"}.", nameof(segments));
            }
        }

        return path.ToString();
    }

    /// <summary>The integer range as the lines word it: "an integer of at least 1", "an integer from 1 to 168".</summary>
    internal static string IntegerType(long min, long max) => (min, max) switch
    {
        (long.MinValue, long.MaxValue) => "an integer",
        (_, long.MaxValue) => $"an integer of at least {min.ToString(CultureInfo.InvariantCulture)}",
        (long.MinValue, _) => $"an integer of at most {max.ToString(CultureInfo.InvariantCulture)}",
        _ => $"an integer from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}",
    };

    /// <summary>A short JSON value as the lines quote it: <c>"999"</c>, <c>1.0</c>, <c>true</c>; objects and arrays by kind.</summary>
    internal static string Quote(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return KindOf(value);
        }

        var raw = value.GetRawText();
        return raw.Length <= MaxQuotedValueLength ? raw : raw[..MaxQuotedValueLength] + "…";
    }

    internal static string KindOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    private static long? Integer(JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, long min, long max, string? fix, bool required)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var type = IntegerType(min, max);
        if (!TryGetPresent(owner, property, at, problems, type, fix, required, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= min && number <= max)
        {
            return number;
        }

        problems.Error(at, $"'{property}' must be {type}, found {Quote(value)}", fix ?? DefaultFix(property, type));
        return null;
    }

    private static bool TryGetKind(
        JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, JsonValueKind kind, string type, string? fix, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (!TryGetPresent(owner, property, at, problems, type, fix, required: true, out value))
        {
            return false;
        }

        if (value.ValueKind == kind)
        {
            return true;
        }

        problems.Error(at, $"'{property}' must be {type}, found {KindOf(value)}", fix ?? DefaultFix(property, type));
        value = default;
        return false;
    }

    /// <summary>The property exists and is not null; a missing required or a null value reports one problem.</summary>
    private static bool TryGetPresent(
        JsonElement owner, string property, ConfigLocation at, ConfigProblems problems, string type, string? fix, bool required, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(property);
        value = default;
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out value))
        {
            if (required)
            {
                problems.Error(at, $"'{property}' is missing", fix ?? DefaultFix(property, type));
            }

            value = default;
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            problems.Error(at, $"'{property}' is null", fix ?? DefaultFix(property, type));
            value = default;
            return false;
        }

        return true;
    }

    private static string DefaultFix(string property, string type) => $"set '{property}' to {type}";

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
