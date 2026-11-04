using System.Text.Json.Nodes;
using GameServer.Aggregates;
using GameServer.Modules;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Tests.Infrastructure;

public enum ReadProbeKind
{
    Plain,
    Rare,
}

/// <summary>State of the read-probe aggregates (the T004 shape: a number, a list and an enum).</summary>
public sealed record ReadProbeState
{
    public int Gold { get; init; }

    public IReadOnlyList<string> Items { get; init; } = [];

    public ReadProbeKind Kind { get; init; }
}

/// <summary>
/// Slice 016 (FG-029): client-visible aggregates at schema 3 whose upgraders fail on markers (<c>boom</c> throws
/// <see cref="KeyNotFoundException"/>, <c>null</c> returns null, <c>cancel</c> throws <see cref="OperationCanceledException"/>), and
/// a twin <c>read_gap</c> that misses its 1→2 upgrader. Documents are built raw, as a damaged store would hold them.
/// </summary>
public static class ReadProbeAggregates
{
    public const string A = "read_a";
    public const string B = "read_b";
    public const string C = "read_c";
    public const string Gap = "read_gap";
    public const int Schema = 3;

    public static PlayerAggregateDescriptor<ReadProbeState> ADescriptor { get; } = Descriptor(A);

    public static PlayerAggregateDescriptor<ReadProbeState> BDescriptor { get; } = Descriptor(B);

    public static PlayerAggregateDescriptor<ReadProbeState> CDescriptor { get; } = Descriptor(C);

    /// <summary>Schema 3 with only the 2→3 upgrader: a version-1 document has no upgrade path.</summary>
    public static PlayerAggregateDescriptor<ReadProbeState> GapDescriptor { get; } =
        new(Gap, Schema, clientVisible: false, () => new ReadProbeState(), [new DelegateAggregateUpgrader(2, s => s)]);

    public static DescriptorModule Module() => new(new ModuleDescriptor
    {
        Name = "read",
        Aggregates = [ADescriptor, BDescriptor, CDescriptor, GapDescriptor],
    });

    /// <summary>A stored document: <paramref name="edit"/> damages the valid envelope (gold 5, one item, schema 3, rev 4).</summary>
    public static StoredDocument Document(string aggregateId, string playerId, Action<JsonObject>? edit = null)
    {
        var content = new JsonObject
        {
            ["gameId"] = "test",
            ["environmentId"] = "dev",
            ["playerId"] = playerId,
            ["aggregateId"] = aggregateId,
            ["schemaVersion"] = Schema,
            ["rev"] = 4,
            ["createdAtUtc"] = "2026-10-26T00:00:00.000Z",
            ["updatedAtUtc"] = "2026-10-26T00:00:00.000Z",
            ["state"] = new JsonObject { ["gold"] = 5, ["items"] = new JsonArray("sword"), ["kind"] = "rare" },
        };
        edit?.Invoke(content);

        // Re-parsed so every value is what a store read returns (JSON-element backed), not a constructed node.
        return new StoredDocument(new DocumentKey("pa_" + aggregateId, playerId), JsonNode.Parse(content.ToJsonString())!.AsObject(), handle: null);
    }

    private static PlayerAggregateDescriptor<ReadProbeState> Descriptor(string id) =>
        new(id, Schema, clientVisible: true, () => new ReadProbeState(), [new DelegateAggregateUpgrader(1, UpgradeV1), new DelegateAggregateUpgrader(2, s => s)]);

    private static JsonObject UpgradeV1(JsonObject state)
    {
        if (state["boom"] is not null)
        {
            throw new KeyNotFoundException("upgrade lost key 'secret-state-value'");
        }

        if (state["cancel"] is not null)
        {
            throw new OperationCanceledException();
        }

        return state["null"] is not null ? null! : state;
    }
}
