using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Aggregates;
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
/// chain on read, envelope fields on write (DAT-02, DAT-09, DAT-10). A document that cannot be read is a typed read failure
/// (FG-029): <see cref="TryLoad"/> reports it once and returns it, <see cref="Load"/> throws it.
/// </summary>
public sealed class AggregateDocumentSerializer
{
    private readonly GameServerOptions _options;
    private readonly PlatformMetrics _metrics;
    private readonly AggregateReadFailures _failures;

    /// <summary>Counts read failures without logging them (direct constructions in tests); hosts use the three-argument form.</summary>
    public AggregateDocumentSerializer(GameServerOptions options, PlatformMetrics metrics)
        : this(options, metrics, AggregateReadFailures.CountOnly(metrics))
    {
    }

    public AggregateDocumentSerializer(GameServerOptions options, PlatformMetrics metrics, AggregateReadFailures failures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(failures);
        _options = options;
        _metrics = metrics;
        _failures = failures;
    }

    public static DocumentKey KeyFor(PlayerAggregateDescriptor descriptor, string playerId) =>
        new(descriptor.Collection, playerId);

    /// <summary>
    /// The loaded aggregate (default state at rev 0 when <paramref name="document"/> is null). Throws the typed read failure
    /// (<c>AGGREGATE_VERSION_AHEAD</c> or <c>AGGREGATE_UNREADABLE</c>, <see cref="AggregateReadFailure.ToException"/>), reported once
    /// (BC-5: was INTERNAL or the raw exception).
    /// </summary>
    public LoadedAggregate Load(PlayerAggregateDescriptor descriptor, string playerId, StoredDocument? document) =>
        TryLoad(descriptor, playerId, document, out var loaded, out var failure) ? loaded : throw failure.ToException();

    /// <summary>
    /// Loads the aggregate, or classifies why it cannot be read (data-model §2.1, in the loader's order) and reports the failure
    /// once. Never throws for a read failure; cancellation and out-of-memory are never caught.
    /// </summary>
    public bool TryLoad(
        PlayerAggregateDescriptor descriptor,
        string playerId,
        StoredDocument? document,
        [NotNullWhen(true)] out LoadedAggregate? loaded,
        [NotNullWhen(false)] out AggregateReadFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var found = Read(descriptor, playerId, document, out var read);
        if (read is not null)
        {
            loaded = read;
            failure = null;
            return true;
        }

        loaded = null;
        failure = found ?? throw new InvalidOperationException($"Reading '{descriptor.AggregateId}' of {playerId} gave neither a state nor a failure.");
        _failures.Report(failure);
        return false;
    }

    private AggregateReadFailure? Read(PlayerAggregateDescriptor descriptor, string playerId, StoredDocument? document, out LoadedAggregate? loaded)
    {
        loaded = null;
        if (document is null)
        {
            var initial = descriptor.CreateDefault();
            loaded = new LoadedAggregate(descriptor, playerId, null, 0, descriptor.SchemaVersion, false, initial, SerializeState(initial, descriptor.StateType), null);
            return null;
        }

        var content = document.Content;
        int version;
        long rev;
        string? createdAtUtc;
        try
        {
            version = JsonNumbers.ToInt32(content["schemaVersion"]) ?? 1;
            rev = JsonNumbers.ToInt64(content["rev"]) ?? 0;
            createdAtUtc = content["createdAtUtc"]?.GetValue<string>();
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            return Failure(descriptor, playerId, AggregateReadFailureReason.Unreadable, ex.GetType(), storedSchemaVersion: 0);
        }

        if (version > descriptor.SchemaVersion)
        {
            return new AggregateReadFailure(descriptor.AggregateId, playerId, AggregateReadFailureReason.VersionAhead, string.Empty, version, descriptor.SchemaVersion);
        }

        // A present document whose state is missing, null or not an object is damage, never the default state (T004 finding):
        // reading it as the default would let the next write overwrite what is left.
        if (content["state"] is not JsonObject storedState)
        {
            return Failure(descriptor, playerId, AggregateReadFailureReason.Unreadable, typeof(JsonException), version);
        }

        var stateNode = (JsonObject)storedState.DeepClone();
        var upgraded = false;
        try
        {
            for (var v = version; v < descriptor.SchemaVersion; v++)
            {
                var step = v;
                var upgrader = descriptor.Upgraders.FirstOrDefault(u => u.FromVersion == step)
                    ?? throw new InvalidOperationException($"No upgrader from schemaVersion {step} of '{descriptor.AggregateId}'.");
                stateNode = upgrader.Upgrade(stateNode)
                    ?? throw new InvalidOperationException($"The upgrader from schemaVersion {step} of '{descriptor.AggregateId}' returned null.");
                upgraded = true;
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // An upgrader may throw anything (research R5, VERIFY V6): only its type is kept.
            return Failure(descriptor, playerId, AggregateReadFailureReason.UpgradeFailed, ex.GetType(), version);
        }

        if (upgraded)
        {
            _metrics.AggregateUpgraded(descriptor.AggregateId);
        }

        object state;
        byte[] canonical;
        try
        {
            var stateJson = JsonSerializer.SerializeToUtf8Bytes(stateNode, GameServerJson.Options);
            state = DeserializeState(stateJson, descriptor.StateType);

            // Re-serialize through the state type so change detection compares canonical forms.
            canonical = SerializeState(state, descriptor.StateType);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            return Failure(descriptor, playerId, AggregateReadFailureReason.Unreadable, ex.GetType(), version);
        }

        loaded = new LoadedAggregate(descriptor, playerId, document, rev, version, upgraded, state, canonical, createdAtUtc);
        return null;
    }

    /// <summary>What reading a damaged envelope or state throws (VERIFY V6, the explicit list); anything else is a bug and propagates.</summary>
    private static bool IsDamage(Exception exception) =>
        exception is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException;

    private static AggregateReadFailure Failure(
        PlayerAggregateDescriptor descriptor,
        string playerId,
        AggregateReadFailureReason reason,
        Type exceptionType,
        int storedSchemaVersion) =>
        new(descriptor.AggregateId, playerId, reason, exceptionType.FullName ?? exceptionType.Name, storedSchemaVersion, descriptor.SchemaVersion);

    /// <summary>Builds the stored envelope for a new revision and returns it with its serialized size.</summary>
    public (JsonObject Document, int SizeBytes) BuildDocument(LoadedAggregate before, byte[] newStateJson, long newRev, DateTimeOffset now) =>
        BuildDocument(_options.GameId, _options.EnvironmentId, before, newStateJson, newRev, now);

    /// <summary>The envelope for the given game and environment ids (also measured by the test kit's commit check, CommitRules).</summary>
    internal static (JsonObject Document, int SizeBytes) BuildDocument(
        string gameId,
        string environmentId,
        LoadedAggregate before,
        byte[] newStateJson,
        long newRev,
        DateTimeOffset now)
    {
        var timestamp = FormatTime(now);
        var document = new JsonObject
        {
            ["gameId"] = gameId,
            ["environmentId"] = environmentId,
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
