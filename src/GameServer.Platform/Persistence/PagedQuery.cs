using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServer.Errors;

namespace GameServer.Platform.Persistence;

/// <summary>
/// One-field equality filter, one-field ordering, keyset pagination (GM-06): every list is bounded by
/// <see cref="PageSize"/> (1..200) and continues with an opaque <see cref="Cursor"/>. Filter and order fields must hold
/// JSON strings; documents where they are missing or not strings do not match. The collection needs an index on
/// (filter field, order field, document id) in the query direction so pages are read from the index.
/// </summary>
public sealed partial record PagedQuery(
    string Collection,
    string FilterField,
    string FilterValue,
    string OrderByField,
    bool Descending,
    int PageSize,
    string? Cursor)
{
    public const int MaxPageSize = 200;

    /// <summary>Field paths are dotted identifiers only (no query injection).</summary>
    public static bool IsValidField(string field) => FieldPattern().IsMatch(field);

    public void Validate()
    {
        if (PageSize is < 1 or > MaxPageSize)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, $"pageSize must be 1..{MaxPageSize}.");
        }

        if (!IsValidField(FilterField) || !IsValidField(OrderByField) || !CollectionPattern().IsMatch(Collection))
        {
            throw new ArgumentException("Invalid collection or field path.");
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_-]{0,250}$")]
    private static partial Regex CollectionPattern();
}

public sealed record Page(IReadOnlyList<StoredDocument> Items, string? NextCursor);

/// <summary>Bounded list queries for GM and reports (PERF-07: N1QL only outside the hot path).</summary>
public interface IPagedQueryStore
{
    Task<Page> QueryPageAsync(PagedQuery query, CancellationToken cancellationToken);
}

/// <summary>Keyset cursor: last order value + last key.</summary>
public sealed record PageCursor(string Value, string Key)
{
    public string Encode() =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { v = Value, k = Key })).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static PageCursor? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)))!;
            return new PageCursor(json["v"]!.GetValue<string>(), json["k"]!.GetValue<string>());
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NullReferenceException or InvalidOperationException)
        {
            throw new GameServerException(CoreErrorCodes.BadRequest, "Invalid cursor.");
        }
    }

    /// <summary>Reads a dotted path holding a JSON string (null when missing or not a string).</summary>
    public static string? StringAt(JsonObject document, string path)
    {
        JsonNode? node = document;
        foreach (var segment in path.Split('.'))
        {
            node = (node as JsonObject)?[segment];
            if (node is null)
            {
                return null;
            }
        }

        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }
}
