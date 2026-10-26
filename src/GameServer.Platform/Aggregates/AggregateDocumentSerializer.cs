using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Aggregates;
using GameServer.Errors;
using GameServer.Platform.Diagnostics;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Aggregates;

/// <summary>A player aggregate after load and lazy upgrade.</summary>
public sealed class LoadedAggregate
{
    internal LoadedAggregate(
        PlayerAggregateDescriptor descriptor,
        string playerId,
        StoredDocument? document,
        long rev,
        int storedSchemaVersion,
        bool upgraded,
        object state,
        byte[] stateJson,
        string? createdAtUtc)
    {
        Descriptor = descriptor;
        PlayerId = playerId;
        Document = document;
        Rev = rev;
        StoredSchemaVersion = storedSchemaVersion;
        Upgraded = upgraded;
        State = state;
        StateJson = stateJson;
        CreatedAtUtc = createdAtUtc;
    }

    public PlayerAggregateDescriptor Descriptor { get; }

    public string PlayerId { get; }

    /// <summary>Stored document, or null when the aggregate does not exist yet (default state, rev 0).</summary>
    public StoredDocument? Document { get; }

    public bool Exists => Document is not null;

    public long Rev { get; }

    public int StoredSchemaVersion { get; }

    /// <summary>True when the stored document was older than the current schema (DAT-02).</summary>
    public bool Upgraded { get; }

    public object State { get; }

    /// <summary>Canonical JSON of <see cref="State"/>, used for change detection.</summary>
    public byte[] StateJson { get; }

    public string? CreatedAtUtc { get; }
}

/// <summary>
/// Maps player aggregate documents (data-model.md) to typed state: default when missing, lazy upgrade
/// chain on read, envelope fields on write (DAT-02, DAT-09, DAT-10).
/// </summary>
public sealed class AggregateDocumentSerializer(GameServerOptions options, PlatformMetrics metrics)
{
    public static DocumentKey KeyFor(PlayerAggregateDescriptor descriptor, string playerId) =>
        new(descriptor.Collection, playerId);

    public LoadedAggregate Load(PlayerAggregateDescriptor descriptor, string playerId, StoredDocument? document)
    {
        if (document is null)
        {
            var initial = descriptor.CreateDefault();
            return new LoadedAggregate(descriptor, playerId, null, 0, descriptor.SchemaVersion, false, initial, SerializeState(initial, descriptor.StateType), null);
        }

        var content = document.Content;
        var version = content["schemaVersion"]?.GetValue<int>() ?? 1;
        var rev = content["rev"]?.GetValue<long>() ?? 0;
        var stateNode = content["state"]?.DeepClone() as JsonObject ?? [];

        if (version > descriptor.SchemaVersion)
        {
            throw new GameServerException(
                CoreErrorCodes.Internal,
                $"Aggregate '{descriptor.AggregateId}' of player {playerId} has schemaVersion {version} newer than code ({descriptor.SchemaVersion}).");
        }

        var upgraded = false;
        for (var v = version; v < descriptor.SchemaVersion; v++)
        {
            var upgrader = descriptor.Upgraders.First(u => u.FromVersion == v);
            stateNode = upgrader.Upgrade(stateNode);
            upgraded = true;
        }

        if (upgraded)
        {
            metrics.AggregateUpgraded(descriptor.AggregateId);
        }

        var stateJson = JsonSerializer.SerializeToUtf8Bytes(stateNode, GameServerJson.Options);
        var state = DeserializeState(stateJson, descriptor.StateType);

        // Re-serialize through the state type so change detection compares canonical forms.
        var canonical = SerializeState(state, descriptor.StateType);
        return new LoadedAggregate(
            descriptor,
            playerId,
            document,
            rev,
            version,
            upgraded,
            state,
            canonical,
            content["createdAtUtc"]?.GetValue<string>());
    }

    /// <summary>Builds the stored envelope for a new revision and returns it with its serialized size.</summary>
    public (JsonObject Document, int SizeBytes) BuildDocument(LoadedAggregate before, byte[] newStateJson, long newRev, DateTimeOffset now)
    {
        var timestamp = FormatTime(now);
        var document = new JsonObject
        {
            ["gameId"] = options.GameId,
            ["environmentId"] = options.EnvironmentId,
            ["playerId"] = before.PlayerId,
            ["aggregateId"] = before.Descriptor.AggregateId,
            ["schemaVersion"] = before.Descriptor.SchemaVersion,
            ["rev"] = newRev,
            ["createdAtUtc"] = before.CreatedAtUtc ?? timestamp,
            ["updatedAtUtc"] = timestamp,
            ["state"] = JsonNode.Parse(newStateJson),
        };

        var size = Encoding.UTF8.GetByteCount(document.ToJsonString(GameServerJson.Options));
        return (document, size);
    }

    public static byte[] SerializeState(object state, Type stateType) =>
        JsonSerializer.SerializeToUtf8Bytes(state, stateType, GameServerJson.Options);

    public static object DeserializeState(ReadOnlySpan<byte> json, Type stateType) =>
        JsonSerializer.Deserialize(json, stateType, GameServerJson.Options)
        ?? throw new InvalidOperationException($"State of type {stateType} deserialized to null.");

    public static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
