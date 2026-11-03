using System.Text.Json;
using GameServer.Config;

namespace GameServer.Platform.Config;

/// <summary>A property name repeated inside one JSON object: <paramref name="Path"/> is the object's path.</summary>
/// <param name="Line">1-based line of the repeated name.</param>
/// <param name="Byte">1-based byte offset of the repeated name's opening quote in that line.</param>
internal readonly record struct DuplicateProperty(string Name, string Path, int Line, int Byte);

/// <summary>
/// Finds repeated property names at any depth (BC-2, FR-002). <c>JsonDocumentOptions.AllowDuplicateProperties = false</c>
/// detects them but reports neither path nor position on .NET 10.0.14 (slice 015 V1), so the loader walks the tokens itself:
/// names are compared ordinally after unescaping (<c>"a"</c> = <c>"a"</c>; <c>"a"</c> ≠ <c>"A"</c>).
/// </summary>
internal static class JsonDuplicateProperties
{
    /// <summary>Every repeated name of an already well-formed document, in document order.</summary>
    public static IReadOnlyList<DuplicateProperty> Find(ReadOnlySpan<byte> json)
    {
        List<DuplicateProperty>? found = null;
        var frames = new List<Frame>();
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    var segment = NextSegment(frames);
                    frames.Add(new Frame(reader.TokenType == JsonTokenType.StartObject, segment));
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.RemoveAt(frames.Count - 1);
                    break;
                case JsonTokenType.PropertyName:
                    var current = frames[^1];
                    var name = reader.GetString()!;
                    if (!current.Names!.Add(name))
                    {
                        var (line, column) = Position(json, reader.TokenStartIndex);
                        (found ??= []).Add(new DuplicateProperty(name, PathOf(frames), line, column));
                    }

                    current.Property = name;
                    break;
                default:
                    // A primitive value: as an array element it takes the next index.
                    NextSegment(frames);
                    break;
            }
        }

        return found ?? (IReadOnlyList<DuplicateProperty>)[];
    }

    /// <summary>The segment the next value is reached by: the pending property name, or the next array index (consumed).</summary>
    private static Segment NextSegment(List<Frame> frames)
    {
        if (frames.Count == 0)
        {
            return default;
        }

        var parent = frames[^1];
        return parent.IsObject ? new Segment(parent.Property, null) : new Segment(null, parent.NextIndex++);
    }

    private static string PathOf(List<Frame> frames) =>
        ConfigJson.PathOf(frames.Skip(1).Select(f => f.Segment.Index is { } index ? (object)index : f.Segment.Name!).ToArray());

    private static (int Line, int Byte) Position(ReadOnlySpan<byte> json, long index)
    {
        var before = json[..(int)index];
        var line = before.Count((byte)'\n') + 1;
        var lineStart = before.LastIndexOf((byte)'\n') + 1;
        return (line, (int)index - lineStart + 1);
    }

    /// <summary>How a container is reached from its parent: a property name or an array index (neither for the root).</summary>
    private readonly record struct Segment(string? Name, int? Index);

    private sealed class Frame(bool isObject, Segment segment)
    {
        public bool IsObject { get; } = isObject;

        public Segment Segment { get; } = segment;

        public HashSet<string>? Names { get; } = isObject ? new HashSet<string>(StringComparer.Ordinal) : null;

        public string? Property { get; set; }

        public int NextIndex { get; set; }
    }
}
