using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;

namespace GameServer.Tests.Shared;

/// <summary>The four ways a stored aggregate fails to reach a client (data-model §2.1).</summary>
public enum BrokenReason
{
    /// <summary><c>starter</c> stored with <c>schemaVersion</c> one above the code's.</summary>
    VersionAhead,

    /// <summary><c>starter</c> stored with a state of the wrong shape.</summary>
    Unreadable,

    /// <summary><c>broken_probe</c> stored at schema 1 with the marker its 1→2 upgrader throws for.</summary>
    UpgradeFailed,

    /// <summary><c>starter</c> readable, but its wire mapper throws for the marker value.</summary>
    MapperFailed,
}

/// <summary>
/// Slice 016 T015: raw aggregate documents of <see cref="StartingStateModule"/> for one player, one broken document per
/// <see cref="BrokenReason"/> and its undamaged twin (same aggregate and rev, a state every reader accepts).
/// </summary>
public static class BrokenDocuments
{
    public const long Rev = 3;
    public const long TwinValue = 7;
    private const string Stamp = "2026-10-26T00:00:00.000Z";

    /// <summary>The aggregate a seed of <paramref name="reason"/> breaks.</summary>
    public static string AggregateOf(BrokenReason reason) =>
        reason == BrokenReason.UpgradeFailed ? StartingStateAggregates.BrokenProbe : StartingStateAggregates.Starter;

    public static DocumentKey KeyOf(BrokenReason reason, string playerId) => new("pa_" + AggregateOf(reason), playerId);

    /// <summary>The stored document: broken for <paramref name="reason"/>, or its undamaged twin.</summary>
    public static JsonObject Document(BrokenReason reason, string playerId, bool damaged = true, string gameId = "test", string environmentId = "test")
    {
        var aggregate = AggregateOf(reason);
        var schemaVersion = aggregate == StartingStateAggregates.Starter
            ? StartingStateAggregates.StarterDescriptor.SchemaVersion
            : StartingStateAggregates.BrokenProbeDescriptor.SchemaVersion;
        JsonObject state = aggregate == StartingStateAggregates.Starter
            ? new JsonObject { ["value"] = TwinValue, ["increments"] = 1, ["padding"] = string.Empty }
            : new JsonObject { ["value"] = TwinValue, ["fail"] = false };

        if (damaged)
        {
            switch (reason)
            {
                case BrokenReason.VersionAhead:
                    schemaVersion += 1;
                    break;
                case BrokenReason.Unreadable:
                    state = new JsonObject { ["value"] = new JsonObject { ["not"] = "a number" } };
                    break;
                case BrokenReason.UpgradeFailed:
                    schemaVersion = 1;
                    state = new JsonObject { ["value"] = TwinValue, ["fail"] = true };
                    break;
                case BrokenReason.MapperFailed:
                    state["padding"] = StartingStateAggregates.MapperFailMarker;
                    break;
            }
        }

        return new JsonObject
        {
            ["gameId"] = gameId,
            ["environmentId"] = environmentId,
            ["playerId"] = playerId,
            ["aggregateId"] = aggregate,
            ["schemaVersion"] = schemaVersion,
            ["rev"] = Rev,
            ["createdAtUtc"] = Stamp,
            ["updatedAtUtc"] = Stamp,
            ["state"] = state,
        };
    }

    /// <summary>Seeds the document into <paramref name="store"/> (a test host's store) and returns its key.</summary>
    public static DocumentKey Seed(InMemoryDocumentStore store, BrokenReason reason, string playerId, bool damaged = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        var key = KeyOf(reason, playerId);
        store.Seed(key, Document(reason, playerId, damaged));
        return key;
    }
}
