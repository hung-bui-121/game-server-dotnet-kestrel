using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameServer.Aggregates;

/// <summary>Upgrades the JSON state of an aggregate from <see cref="FromVersion"/> to FromVersion + 1 (DAT-02).</summary>
public interface IAggregateUpgrader
{
    int FromVersion { get; }

    JsonObject Upgrade(JsonObject state);
}

/// <summary>Upgrader built from a delegate.</summary>
public sealed class DelegateAggregateUpgrader(int fromVersion, Func<JsonObject, JsonObject> upgrade) : IAggregateUpgrader
{
    public int FromVersion { get; } = fromVersion;

    public JsonObject Upgrade(JsonObject state) => upgrade(state);
}

/// <summary>Registration of one player aggregate stored in collection <c>pa_&lt;id&gt;</c> (DAT-09).</summary>
public abstract partial class PlayerAggregateDescriptor
{
    public const int DefaultWarnBytes = 32 * 1024;
    public const int DefaultHardBytes = 64 * 1024;

    protected PlayerAggregateDescriptor(
        string aggregateId,
        Type stateType,
        int schemaVersion,
        bool clientVisible,
        IReadOnlyList<IAggregateUpgrader>? upgraders,
        int warnBytes,
        int hardBytes)
    {
        AggregateId = aggregateId;
        StateType = stateType;
        SchemaVersion = schemaVersion;
        ClientVisible = clientVisible;
        Upgraders = upgraders ?? [];
        WarnBytes = warnBytes;
        HardBytes = hardBytes;
    }

    public string AggregateId { get; }

    public Type StateType { get; }

    public int SchemaVersion { get; }

    public bool ClientVisible { get; }

    public IReadOnlyList<IAggregateUpgrader> Upgraders { get; }

    public int WarnBytes { get; }

    public int HardBytes { get; }

    /// <summary>Collection holding this aggregate: <c>pa_&lt;aggregateId&gt;</c>.</summary>
    public string Collection => CollectionFor(AggregateId);

    public static string CollectionFor(string aggregateId) => "pa_" + aggregateId;

    public abstract object CreateDefault();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z][a-z0-9_]{1,31}$")]
    private static partial Regex IdPattern();
}

/// <summary>Typed aggregate registration. <typeparamref name="TState"/> must round-trip through System.Text.Json.</summary>
public sealed class PlayerAggregateDescriptor<TState> : PlayerAggregateDescriptor
    where TState : class
{
    private readonly Func<TState> _createDefault;

    public PlayerAggregateDescriptor(
        string aggregateId,
        int schemaVersion,
        bool clientVisible,
        Func<TState> createDefault,
        IReadOnlyList<IAggregateUpgrader>? upgraders = null,
        int warnBytes = DefaultWarnBytes,
        int hardBytes = DefaultHardBytes)
        : base(aggregateId, typeof(TState), schemaVersion, clientVisible, upgraders, warnBytes, hardBytes)
    {
        _createDefault = createDefault;
    }

    public override object CreateDefault() => _createDefault();
}
