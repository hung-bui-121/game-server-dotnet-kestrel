using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform;

namespace GameServer.TestKit;

/// <summary>
/// Sample and perturbation values per leaf kind (data-model §7.2). Each leaf gets a distinct ordinal <c>n</c>, so a mapper that
/// copies one member into another field is caught; every perturbation moves the wire value by at least one unit. Values are written
/// through <see cref="GameServerJson.Options"/>, so the document converters (camelCase enums, dates) apply.
/// </summary>
internal static class SampleStates
{
    /// <summary>Base instant of date samples: 2026-01-02T03:04:05.006Z (+ n minutes).</summary>
    private static readonly DateTimeOffset BaseInstant = new(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero);

    /// <summary>Small integer types wrap their ordinal into 1..SmallRange so the +1 perturbation stays in range.</summary>
    private const int SmallRange = 100;

    public static bool IsSupported(Type type) => Sample(type, 1) is not null;

    public static bool IsSupportedKey(Type type) => type == typeof(string) || type == typeof(Guid) || type.IsEnum || IsInteger(type);

    /// <summary>The sample of leaf type <paramref name="type"/> for ordinal <paramref name="n"/>; null when the type is not supported.</summary>
    public static object? Sample(Type type, int n)
    {
        if (type.IsEnum)
        {
            return NonZeroValues(type).FirstOrDefault();
        }

        return type switch
        {
            _ when IsInteger(type) => Convert.ChangeType(type == typeof(byte) || type == typeof(sbyte) ? ((n - 1) % SmallRange) + 1 : n, type, CultureInfo.InvariantCulture),
            _ when type == typeof(float) => n + 0.5f,
            _ when type == typeof(double) => n + 0.5d,
            _ when type == typeof(decimal) => n + 0.5m,
            _ when type == typeof(bool) => true,
            _ when type == typeof(string) => "s" + n.ToString(CultureInfo.InvariantCulture),
            _ when type == typeof(DateTimeOffset) => BaseInstant.AddMinutes(n),
            _ when type == typeof(DateTime) => BaseInstant.AddMinutes(n).UtcDateTime,
            _ when type == typeof(TimeSpan) => TimeSpan.FromSeconds(n),
            _ when type == typeof(Guid) => new Guid(n, 0x5157, 0x4e47, 1, 2, 3, 4, 5, 6, 7, 8),
            _ when type == typeof(byte[]) => new[] { (byte)(((n - 1) % SmallRange) + 1) },
            _ => null,
        };
    }

    /// <summary>A value at least one wire unit away from <paramref name="value"/> (same type).</summary>
    public static object Perturb(Type type, object value)
    {
        if (type.IsEnum)
        {
            var values = NonZeroValues(type).ToList();
            var others = values.Where(v => !v.Equals(value)).ToList();
            return others.Count > 0 ? others[0] : Enum.ToObject(type, 0);
        }

        return value switch
        {
            byte v => (byte)(v + 1),
            sbyte v => (sbyte)(v + 1),
            short v => (short)(v + 1),
            ushort v => (ushort)(v + 1),
            int v => v + 1,
            uint v => v + 1,
            long v => v + 1,
            ulong v => v + 1,
            float v => v + 1,
            double v => v + 1,
            decimal v => v + 1,
            bool v => !v,
            string v => v + "x",
            DateTimeOffset v => v.AddSeconds(1),
            DateTime v => v.AddSeconds(1),
            TimeSpan v => v.Add(TimeSpan.FromSeconds(1)),
            Guid v => PerturbGuid(v),
            byte[] v => (byte[])[.. v, 1],
            _ => throw new InvalidOperationException($"No perturbation for {type}."),
        };
    }

    /// <summary>The JSON of a leaf value under the document options.</summary>
    public static JsonNode? ToJson(object? value, Type type) => JsonSerializer.SerializeToNode(value, type, GameServerJson.Options);

    public static object? FromJson(JsonNode? json, Type type) => json.Deserialize(type, GameServerJson.Options);

    /// <summary>The property name the serializer writes for dictionary key <paramref name="key"/>.</summary>
    public static string KeyToJson(object key, Type keyType)
    {
        var dictionary = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, typeof(int)))!;
        dictionary.Add(key, 0);
        return JsonSerializer.SerializeToNode(dictionary, dictionary.GetType(), GameServerJson.Options)!.AsObject().Single().Key;
    }

    public static object KeyFromJson(string name, Type keyType)
    {
        var json = new JsonObject { [name] = 0 };
        var dictionary = (System.Collections.IDictionary)json.Deserialize(typeof(Dictionary<,>).MakeGenericType(keyType, typeof(int)), GameServerJson.Options)!;
        return dictionary.Keys.Cast<object>().Single();
    }

    /// <summary>Dictionary key sample: string <c>k{n}</c>, integer <c>n</c>, enum, Guid (data-model §7.2).</summary>
    public static object? KeySample(Type keyType, int n) =>
        keyType == typeof(string) ? "k" + n.ToString(CultureInfo.InvariantCulture) : IsSupportedKey(keyType) ? Sample(keyType, n) : null;

    private static bool IsInteger(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

    private static IEnumerable<object> NonZeroValues(Type enumType) =>
        Enum.GetValues(enumType).Cast<object>().Where(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0).Distinct();

    private static Guid PerturbGuid(Guid value)
    {
        var bytes = value.ToByteArray();
        bytes[^1]++;
        return new Guid(bytes);
    }
}
