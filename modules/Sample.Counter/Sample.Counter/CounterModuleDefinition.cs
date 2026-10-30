using GameServer.Commands;
using GameServer.Modules;

namespace Sample.Counter;

/// <summary>Service other modules may use without the counter endpoints (ARC-04, ARC-05).</summary>
public interface ICounterService
{
    Task<CounterReading> GetAsync(string playerId, CancellationToken cancellationToken);
}

public sealed record CounterReading(long Value, long Rev);

/// <summary>Business half of the module descriptor; the adapter adds endpoints and wire mapping.</summary>
public static class CounterModuleDefinition
{
    public const string ModuleName = "sample.counter";
    public const string ReadPermission = "counter.read";
    public const string LogCollection = "counter_log";

    public static ModuleDescriptor Describe() => new()
    {
        Name = ModuleName,
        Version = "0.1.0",
        DependsOn = ["identity"],
        Permissions = [ReadPermission],
        // Log entries keep their history with the player id anonymized (PRIV-01, FR-019).
        Collections = [new CollectionSpec(LogCollection, Deletion: DeletionTreatment.Anonymize)],
        Aggregates = [CounterAggregates.CounterDescriptor],
        Commands = [Command.Define<IncrementCommand, IncrementResult>(IncrementHandler.Descriptor).HandledBy<IncrementHandler>()],
        ErrorCodes = CounterErrors.All,
        OutboxProjections = [new OutboxProjection(IncrementHandler.LogKind, LogCollection)],
    };
}
