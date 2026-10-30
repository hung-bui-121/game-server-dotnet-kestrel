using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Testing;

/// <summary>
/// JSON shapes of the file store (data-model §6): the document file <c>{key, expiresAtUtc, content}</c> and the journal
/// <c>{version, commitId, writes[]}</c>. UTF-8, indented, hand-editable while the host is stopped.
/// </summary>
internal static class FileDocumentFormat
{
    public const int JournalVersion = 1;

    private const string PutOperation = "put";
    private const string RemoveOperation = "remove";

    /// <summary>Indented, without HTML escaping: the files are read by people and by this loader, never embedded in a page.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Document(string key, DateTimeOffset? expiresAt, JsonObject content) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("key", key);
        WriteTime(writer, expiresAt);
        writer.WritePropertyName("content");
        content.WriteTo(writer);
        writer.WriteEndObject();
    });

    public static byte[] Journal(string commitId, IReadOnlyList<DocumentChange> changes) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", JournalVersion);
        writer.WriteString("commitId", commitId);
        writer.WriteStartArray("writes");
        foreach (var change in changes)
        {
            writer.WriteStartObject();
            writer.WriteString("collection", change.Key.Collection);
            writer.WriteString("key", change.Key.Key);
            writer.WriteString("op", change.Content is null ? RemoveOperation : PutOperation);
            if (change.Content is not null)
            {
                WriteTime(writer, change.ExpiresAt);
                writer.WritePropertyName("content");
                change.Content.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    /// <summary>ISO-8601 UTC, round-trip format (<c>O</c>), e.g. <c>2026-10-30T12:00:00.0000000Z</c>.</summary>
    public static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A document file: null with a reason when it is not one (FR-024).</summary>
    public static (string Key, JsonObject Content, DateTimeOffset? ExpiresAt)? ParseDocument(byte[] bytes, out string reason)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException ex)
        {
            reason = "invalid JSON: " + ex.Message;
            return null;
        }

        if (root is null)
        {
            reason = "not a JSON object";
            return null;
        }

        return TryReadEntry(root, out var key, out var content, out var expiresAt, out reason) ? (key, content!, expiresAt) : null;
    }

    /// <summary>The changes of a journal; throws <see cref="FormatException"/> naming the problem when it cannot be replayed.</summary>
    public static IReadOnlyList<DocumentChange> ParseJournal(byte[] bytes)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new FormatException("invalid JSON: " + ex.Message, ex);
        }

        if (root?["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != JournalVersion)
        {
            throw new FormatException($"version must be {JournalVersion}");
        }

        if (root["writes"] is not JsonArray writes)
        {
            throw new FormatException("'writes' must be an array");
        }

        var changes = new List<DocumentChange>(writes.Count);
        foreach (var node in writes)
        {
            if (node is not JsonObject write || !TryString(write, "collection", out var collection) || !TryString(write, "op", out var op))
            {
                throw new FormatException("every write needs 'collection' and 'op'");
            }

            if (op == RemoveOperation)
            {
                changes.Add(TryString(write, "key", out var removed) && removed.Length > 0
                    ? new DocumentChange(new DocumentKey(collection, removed), null, null)
                    : throw new FormatException("a remove needs 'key'"));
            }
            else if (op == PutOperation && TryReadEntry(write, out var key, out var content, out var expiresAt, out _))
            {
                changes.Add(new DocumentChange(new DocumentKey(collection, key), content, expiresAt));
            }
            else
            {
                throw new FormatException($"write of '{collection}' is not a valid put or remove");
            }
        }

        return changes;
    }

    private static bool TryReadEntry(JsonObject entry, out string key, out JsonObject? content, out DateTimeOffset? expiresAt, out string reason)
    {
        content = null;
        expiresAt = null;
        if (!TryString(entry, "key", out key) || key.Length == 0)
        {
            reason = "'key' must be a non-empty string";
            return false;
        }

        if (entry["content"] is not JsonObject body)
        {
            reason = "'content' must be a JSON object";
            return false;
        }

        switch (entry["expiresAtUtc"])
        {
            case null:
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && TryParseTime(text, out var parsed):
                expiresAt = parsed;
                break;
            default:
                reason = "'expiresAtUtc' must be null or an ISO-8601 UTC time (format O, ending in Z)";
                return false;
        }

        // Detach the content from its parent so the store can own it.
        entry.Remove("content");
        content = body;
        reason = string.Empty;
        return true;
    }

    private static bool TryParseTime(string text, out DateTimeOffset value)
    {
        if (DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) && parsed.Kind == DateTimeKind.Utc)
        {
            value = new DateTimeOffset(parsed);
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryString(JsonObject entry, string property, out string value)
    {
        if (entry[property] is JsonValue node && node.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void WriteTime(Utf8JsonWriter writer, DateTimeOffset? expiresAt)
    {
        if (expiresAt is { } at)
        {
            writer.WriteString("expiresAtUtc", FormatTime(at));
        }
        else
        {
            writer.WriteNull("expiresAtUtc");
        }
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            body(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
