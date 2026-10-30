using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Analytics;

/// <summary>Event ids (R6): 32 lowercase hex characters in both cases.</summary>
public static class AnalyticsEventIds
{
    private const int Length = 32;

    /// <summary>
    /// Outbox events: <c>hex(SHA-256(UTF-8("{entryId}|{committedAtUtc}")))[0..32]</c>. Stable across redeliveries (both
    /// inputs are immutable receipt fields) and unique when CMD-07 supersedes a receipt with the same id (SD1).
    /// </summary>
    public static string ForOutboxEntry(string entryId, string committedAtUtc) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entryId + "|" + committedAtUtc)))[..Length];

    /// <summary>Emitted events (login, resume): a random GUID in <c>N</c> format.</summary>
    public static string NewRandom() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Formats an envelope as one compact UTF-8 JSON line (R6, data-model §3): envelope keys first in a fixed order, then the
/// type fields in payload order; null type fields are left out; a type field named like an envelope key is ignored (one
/// error log per event type). <c>gameId</c>/<c>environmentId</c> come from <see cref="GameServerOptions"/> only (FR-033).
/// </summary>
public sealed class AnalyticsRecordFormatter(GameServerOptions options, AnalyticsTimeZone zone, ILogger<AnalyticsRecordFormatter> logger)
{
    /// <summary>
    /// Relaxed escaping keeps <c>+</c> in offsets literal (the default encoder writes <c>+</c>); records are JSON lines,
    /// never embedded in HTML.
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private static readonly JsonSerializerOptions FieldOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConcurrentDictionary<string, byte> _collisionsLogged = new(StringComparer.Ordinal);

    public AnalyticsRecord Format(AnalyticsEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var type = envelope.Type;
        var time = zone.Describe(envelope.OccurredAtUtc);
        var timeUtcField = type.TimeField + "Utc";
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("event", type.Name);
            writer.WriteNumber("schemaVersion", type.SchemaVersion);
            writer.WriteString("eventId", envelope.EventId);
            writer.WriteString("gameId", options.GameId);
            writer.WriteString("environmentId", options.EnvironmentId);
            writer.WriteString("userID", envelope.UserId);
            writer.WriteString(type.TimeField, time.Local);
            writer.WriteString(timeUtcField, time.Utc);
            writer.WriteString("timeZone", zone.Name);
            writer.WriteString("utcOffset", time.Offset);
            if (envelope.ReceiptId is { } receiptId)
            {
                writer.WriteString("receiptId", receiptId);
            }

            WriteFields(writer, envelope, timeUtcField);
            writer.WriteEndObject();
        }

        return new AnalyticsRecord(type.Name, envelope.EventId, buffer.WrittenMemory.ToArray());
    }

    private void WriteFields(Utf8JsonWriter writer, AnalyticsEnvelope envelope, string timeUtcField)
    {
        if (envelope.Fields is JsonObject node)
        {
            foreach (var (name, value) in node)
            {
                if (value is null || IsEnvelopeName(envelope, name, timeUtcField))
                {
                    continue;
                }

                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            return;
        }

        // Typed fields (login): serialized once, then copied property by property without building a JSON tree.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Fields, envelope.Fields.GetType(), FieldOptions);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException($"Fields of analytics event '{envelope.Type.Name}' must serialize to a JSON object.", nameof(envelope));
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            var value = bytes.AsSpan(start, (int)reader.BytesConsumed - start);
            if (reader.TokenType == JsonTokenType.Null || IsEnvelopeName(envelope, name, timeUtcField))
            {
                continue;
            }

            writer.WritePropertyName(name);
            writer.WriteRawValue(value, skipInputValidation: true);
        }
    }

    private bool IsEnvelopeName(AnalyticsEnvelope envelope, string name, string timeUtcField)
    {
        var collides = name is "event" or "schemaVersion" or "eventId" or "gameId" or "environmentId" or "userID" or "timeZone" or "utcOffset" or "receiptId"
            || name == envelope.Type.TimeField
            || name == timeUtcField;
        if (collides && _collisionsLogged.TryAdd(envelope.Type.Name, 0))
        {
            logger.LogError(
                "Analytics event {Event} has a field named like an envelope field ({Field}); the field is ignored (envelope wins).",
                envelope.Type.Name,
                name);
        }

        return collides;
    }
}
