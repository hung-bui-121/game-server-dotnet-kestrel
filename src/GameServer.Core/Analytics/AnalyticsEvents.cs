using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GameServer.Commands;

namespace GameServer.Analytics;

/// <summary>
/// Analytics event type (ANA-01): stream and file name, schema version of its fields, and the owner-chosen name of the
/// time field (<c>chargeTime</c>, <c>activeTime</c>; the record also carries <c>{TimeField}Utc</c>).
/// </summary>
public sealed record AnalyticsEventType(string Name, int SchemaVersion, string TimeField)
{
    private const int MaxNameLength = 32;

    /// <summary><c>^[a-z][a-z0-9_]{0,31}$</c> (the name is part of file names, so it never contains <c>-</c>).</summary>
    public static bool IsValidName(string? name)
    {
        if (name is not { Length: > 0 and <= MaxNameLength } || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>^[a-z][A-Za-z0-9]{0,31}$</c> (camelCase JSON name).</summary>
    public static bool IsValidTimeField(string? name)
    {
        if (name is not { Length: > 0 and <= MaxNameLength } || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> naming the invalid part (a module bug, never a player input).</summary>
    public static void ThrowIfInvalid(AnalyticsEventType? type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!IsValidName(type.Name))
        {
            throw new ArgumentException($"Analytics event name '{type.Name}' must match ^[a-z][a-z0-9_]{{0,31}}$.", nameof(type));
        }

        if (type.SchemaVersion < 1)
        {
            throw new ArgumentException($"Analytics event '{type.Name}' has schema version {type.SchemaVersion}; it must be >= 1.", nameof(type));
        }

        if (!IsValidTimeField(type.TimeField))
        {
            throw new ArgumentException($"Analytics event '{type.Name}' time field '{type.TimeField}' must match ^[a-z][A-Za-z0-9]{{0,31}}$.", nameof(type));
        }
    }
}

/// <summary>
/// Payload of an outbox entry of kind <see cref="AnalyticsOutbox.Kind"/>: self-describing, so one platform consumer serves
/// every event type. It holds no player id; the envelope (player, time, ids) is derived from the receipt at delivery.
/// </summary>
public sealed record AnalyticsOutboxEvent(string Event, int SchemaVersion, string TimeField, object Fields);

/// <summary>At-least-once analytics events committed with a command (ANA-01, JOB-02).</summary>
public static class AnalyticsOutbox
{
    /// <summary>Outbox kind consumed by the platform analytics consumer; reserved (a second consumer fails startup).</summary>
    public const string Kind = "analytics";

    /// <summary>Type-specific fields: camelCase unless named explicitly, null values left out (receipt size, FR-012).</summary>
    private static readonly JsonSerializerOptions FieldOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Records an analytics event inside the current command (at-least-once, committed with it). At delivery the platform
    /// adds the envelope: userID = the receipt's player, time = the receipt commit time, event id, game, environment, zone.
    /// <paramref name="fields"/> must serialize to a JSON object; null fields are not stored. Throws
    /// <see cref="ArgumentException"/> for an invalid type or fields (a module bug: the command fails INTERNAL, nothing commits).
    /// </summary>
    public static void AddAnalyticsEvent(this ICommandContext context, AnalyticsEventType type, object fields)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);
        AnalyticsEventType.ThrowIfInvalid(type);
        if (JsonSerializer.SerializeToNode(fields, fields.GetType(), FieldOptions) is not JsonObject node)
        {
            throw new ArgumentException($"Fields of analytics event '{type.Name}' must serialize to a JSON object.", nameof(fields));
        }

        // A JsonObject passed as is keeps its null values through serialization: drop them here.
        foreach (var name in node.Where(p => p.Value is null).Select(p => p.Key).ToArray())
        {
            node.Remove(name);
        }

        context.AddOutbox(Kind, new AnalyticsOutboxEvent(type.Name, type.SchemaVersion, type.TimeField, node));
    }
}
