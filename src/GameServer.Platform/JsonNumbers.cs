using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameServer.Platform;

/// <summary>
/// Reads numbers from <see cref="JsonNode"/> regardless of how the node was created (parsed JSON or CLR value).
/// </summary>
public static class JsonNumbers
{
    public static long? ToInt64(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<long>(out var l) => l,
        JsonValue value when value.TryGetValue<int>(out var i) => i,
        JsonValue value when value.TryGetValue<JsonElement>(out var e) && e.TryGetInt64(out var el) => el,
        _ => long.Parse(node.ToJsonString(), CultureInfo.InvariantCulture),
    };

    public static int? ToInt32(JsonNode? node) => ToInt64(node) is { } l ? checked((int)l) : null;
}
