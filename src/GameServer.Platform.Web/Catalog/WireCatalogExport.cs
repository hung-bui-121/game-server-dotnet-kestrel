using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GameServer.Routing;
using Google.Protobuf;

namespace GameServer.Platform.Web.Catalog;

/// <summary>
/// The deterministic catalog export (FR-030, data-model §3): UTF-8 without BOM, LF, 2-space indentation, string arrays on one
/// line, properties in a fixed order, nulls and empty arrays omitted. No timestamp, path, machine, user or environment value.
/// </summary>
public static class WireCatalogExport
{
    public const string Format = "gameserver.wire-contract/1";

    private const string Indent = "  ";

    /// <summary>Depth of the properties of a route or section object: root → array → object → property.</summary>
    private const int EntryDepth = 3;

    /// <summary>Escapes only what JSON requires (a '+' in a version stays readable).</summary>
    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Write(WireCatalog catalog)
    {
        var json = new StringBuilder();
        json.Append('{').Append('\n');
        var top = new List<string>
        {
            Property(1, "format", Format),
            Property(1, "frameworkVersion", GameServerVersion.Framework),
            Property(1, "protobufVersion", GameServerVersion.Of(typeof(IMessage).Assembly)),
        };
        if (catalog.Routes.Count > 0)
        {
            top.Add(Array(1, "routes", catalog.Routes.Select(Route)));
        }

        if (catalog.Sections.Count > 0)
        {
            top.Add(Array(1, "sections", catalog.Sections.Select(Section)));
        }

        json.Append(string.Join(",\n", top)).Append('\n').Append('}').Append('\n');
        return json.ToString();
    }

    private static List<string> Route(RouteEntry entry)
    {
        var route = entry.Declaration;
        var properties = new List<string> { Property(EntryDepth, "name", route.Name) };
        if (route.Methods.Count > 0)
        {
            properties.Add(Strings(EntryDepth, "methods", route.Methods));
        }

        properties.Add(Property(EntryDepth, "template", route.Template));
        properties.Add(Property(EntryDepth, "profile", route.Profile.ToString()));
        properties.Add(Property(EntryDepth, "surface", route.Surface.ToString()));
        properties.Add(Property(EntryDepth, "owner", entry.Owner));
        properties.Add(Property(EntryDepth, "origin", OriginName(entry.Origin)));
        AddOptional(properties, "operation", route.Operation);
        AddOptional(properties, "permission", route.Permission);
        AddOptional(properties, "condition", route.Condition);
        if (entry.ServiceOnly)
        {
            properties.Add($"{Pad(EntryDepth)}\"serviceOnly\": true");
        }

        return properties;
    }

    private static List<string> Section(SectionEntry entry)
    {
        var properties = new List<string>
        {
            Property(EntryDepth, "id", entry.Id),
            Property(EntryDepth, "owner", entry.Owner),
            Property(EntryDepth, "origin", OriginName(entry.Origin)),
        };
        AddOptional(properties, "message", entry.MessageFullName);
        if (entry.HiddenMembers.Count > 0)
        {
            properties.Add(Strings(EntryDepth, "hiddenMembers", entry.HiddenMembers));
        }

        if (entry.MayStayDefaultFields.Count > 0)
        {
            properties.Add(Strings(EntryDepth, "mayStayDefault", entry.MayStayDefaultFields));
        }

        return properties;
    }

    /// <summary>The export spelling of an origin (<c>framework</c>, <c>game</c>).</summary>
    internal static string OriginName(CatalogOrigin origin) => origin == CatalogOrigin.Framework ? "framework" : "game";

    private static void AddOptional(List<string> properties, string name, string? value)
    {
        if (value is not null)
        {
            properties.Add(Property(EntryDepth, name, value));
        }
    }

    private static string Array(int depth, string name, IEnumerable<List<string>> objects)
    {
        var pad = Pad(depth);
        var inner = Pad(depth + 1);
        var items = objects.Select(properties => $"{inner}{{\n{string.Join(",\n", properties)}\n{inner}}}");
        return $"{pad}{Quote(name)}: [\n{string.Join(",\n", items)}\n{pad}]";
    }

    private static string Strings(int depth, string name, IEnumerable<string> values) =>
        $"{Pad(depth)}{Quote(name)}: [{string.Join(", ", values.Select(Quote))}]";

    private static string Property(int depth, string name, string value) => $"{Pad(depth)}{Quote(name)}: {Quote(value)}";

    private static string Pad(int depth) => string.Concat(Enumerable.Repeat(Indent, depth));

    private static string Quote(string value) => JsonSerializer.Serialize(value, QuoteOptions);
}
