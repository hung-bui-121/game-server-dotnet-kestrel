using System.Text.Json;
using GameServer.Config;

namespace GameServer.Platform.Config;

/// <summary>A property name repeated inside one JSON object: <paramref name="Path"/> is the object's path.</summary>
/// <param name="FirstName">The first spelling of the name in that object; differs from <paramref name="Name"/> only in case for a case variant.</param>
/// <param name="Name">The repeated spelling.</param>
/// <param name="Line">1-based line of the repeated name.</param>
/// <param name="Byte">1-based byte offset of the repeated name's opening quote in that line.</param>
internal readonly record struct DuplicateProperty(string FirstName, string Name, string Path, int Line, int Byte)
{
    /// <summary>True when the two spellings differ (ordinally): the names are equal only when case is ignored.</summary>
    public bool IsCaseVariant => !string.Equals(FirstName, Name, StringComparison.Ordinal);
}

/// <summary>
/// Finds repeated property names at any depth (BC-2, FR-002). <c>JsonDocumentOptions.AllowDuplicateProperties = false</c>
/// detects them but reports neither path nor position on .NET 10.0.14 (slice 015 V1), so the loader walks the tokens itself.
/// Names are compared <see cref="StringComparer.OrdinalIgnoreCase"/> after unescaping, like every framework reader of config
/// (<c>GameServerJson.Options</c> binds ignoring case): <c>"a"</c>/<c>"A"</c> in one object is as ambiguous as <c>"a"</c> twice.
/// </summary>
internal static class JsonDuplicateProperties
{
    /// <summary>Every repeated name of an already well-formed document, in document order.</summary>
    public static IReadOnlyList<DuplicateProperty> Find(ReadOnlySpan<byte> json)
    {
        List<DuplicateProperty>? found = null;
        var frames = new List<Frame>();
        var position = new PositionCounter();
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
                    if (current.TryAdd(name) is { } first)
                    {
                        var (line, column) = position.At(json, reader.TokenStartIndex);
                        (found ??= []).Add(new DuplicateProperty(first, name, PathOf(frames), line, column));
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

    /// <summary>How a container is reached from its parent: a property name or an array index (neither for the root).</summary>
    private readonly record struct Segment(string? Name, int? Index);

    /// <summary>
    /// Line and byte of a token. Duplicates are found in document order, so each call counts line feeds only from the previous
    /// position on: O(n) for the whole document however many duplicates it has.
    /// </summary>
    private sealed class PositionCounter
    {
        private int _scannedTo;
        private int _line = 1;
        private int _lineStart;

        public (int Line, int Byte) At(ReadOnlySpan<byte> json, long tokenStart)
        {
            var index = (int)tokenStart;
            var span = json[_scannedTo..index];
            var lastFeed = span.LastIndexOf((byte)'\n');
            if (lastFeed >= 0)
            {
                _line += span.Count((byte)'\n');
                _lineStart = _scannedTo + lastFeed + 1;
            }

            _scannedTo = index;
            return (_line, index - _lineStart + 1);
        }
    }

    private sealed class Frame(bool isObject, Segment segment)
    {
        // The first name is kept in a field; the set is created at the second property, so arrays and one-property objects
        // allocate none. Values are the first spelling of each name.
        private string? _firstName;
        private Dictionary<string, string>? _names;

        public bool IsObject { get; } = isObject;

        public Segment Segment { get; } = segment;

        public string? Property { get; set; }

        public int NextIndex { get; set; }

        /// <summary>Records <paramref name="name"/>; returns the first spelling when the object already has it ignoring case.</summary>
        public string? TryAdd(string name)
        {
            if (_firstName is null)
            {
                _firstName = name;
                return null;
            }

            if (_names is null)
            {
                _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [_firstName] = _firstName };
            }

            return _names.TryAdd(name, name) ? null : _names[name];
        }
    }
}
