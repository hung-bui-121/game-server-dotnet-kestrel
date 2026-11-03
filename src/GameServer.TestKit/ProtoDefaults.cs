using System.Collections;
using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GameServer.TestKit;

/// <summary>
/// Proto fields still at their default (data-model §7.3, FR-061): paths of proto field names joined by '.', repeated and map
/// elements transparent. Repeated/map: empty, or every element default (message elements: a sub-field default in every element);
/// presence fields: not present; oneof: no case set (reported by the oneof name, other cases never reported).
/// </summary>
internal static class ProtoDefaults
{
    /// <summary>Field number of the value in a map entry message (key = 1, value = 2).</summary>
    private const int MapValueField = 2;

    public static IReadOnlyList<string> Of(IMessage message)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Collect(message, string.Empty, paths);
        return [.. paths];
    }

    private static void Collect(IMessage message, string prefix, ISet<string> paths)
    {
        var descriptor = message.Descriptor;
        foreach (var oneof in descriptor.Oneofs.Where(o => !o.IsSynthetic && o.Accessor.GetCaseFieldDescriptor(message) is null))
        {
            paths.Add(prefix + oneof.Name);
        }

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (field.RealContainingOneof is { } oneof && oneof.Accessor.GetCaseFieldDescriptor(message) != field)
            {
                continue;
            }

            var path = prefix + field.Name;
            var value = field.Accessor.GetValue(message);
            if (field.IsMap)
            {
                var values = ((IDictionary)value).Values.Cast<object?>().ToList();
                Elements(field.MessageType.FindFieldByNumber(MapValueField), values, path, paths);
            }
            else if (field.IsRepeated)
            {
                Elements(field, ((IList)value).Cast<object?>().ToList(), path, paths);
            }
            else if (field.FieldType == FieldType.Message)
            {
                if (value is IMessage nested)
                {
                    Collect(nested, path + ".", paths);
                }
                else
                {
                    paths.Add(path);
                }
            }
            else if (field.HasPresence ? !field.Accessor.HasValue(message) : IsDefault(value))
            {
                paths.Add(path);
            }
        }
    }

    /// <summary>Repeated values or map values of <paramref name="element"/>'s kind.</summary>
    private static void Elements(FieldDescriptor element, List<object?> values, string path, ISet<string> paths)
    {
        if (values.Count == 0)
        {
            paths.Add(path);
            return;
        }

        if (element.FieldType != FieldType.Message)
        {
            if (values.All(IsDefault))
            {
                paths.Add(path);
            }

            return;
        }

        // A sub-field is reported when it is default in every element.
        HashSet<string>? common = null;
        foreach (var value in values)
        {
            var own = new HashSet<string>(StringComparer.Ordinal);
            if (value is IMessage nested)
            {
                Collect(nested, path + ".", own);
            }
            else
            {
                own.Add(path);
            }

            if (common is null)
            {
                common = own;
            }
            else
            {
                common.IntersectWith(own);
            }
        }

        paths.UnionWith(common!);
    }

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        ByteString bytes => bytes.IsEmpty,
        bool flag => !flag,
        Enum number => Convert.ToInt64(number, CultureInfo.InvariantCulture) == 0,
        float number => number == 0,
        double number => number == 0,
        IConvertible number => Convert.ToDecimal(number, CultureInfo.InvariantCulture) == 0,
        _ => false,
    };
}
