using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;

namespace GameServer.Platform.Privacy;

/// <summary>
/// Which documents the purge writer may touch and how (FR-019, research R6 refusals), resolved once from the registry's
/// deletion plan: <c>pa_profile</c> only through completion, <c>pa_*</c> only removed by the player's own key, other
/// collections only with their declared treatment, platform collections never through a module step.
/// </summary>
public sealed class PurgeTargets
{
    private readonly Dictionary<string, CollectionDeletion> _plan;

    public PurgeTargets(ModuleRegistry registry)
    {
        _plan = registry.DeletionPlan().ToDictionary(d => d.Collection, StringComparer.Ordinal);
        ProfileCollection = registry.GetAggregate(ModuleRegistry.RequiredProfileAggregate).Collection;
        RemovedCollections = _plan.Values
            .Where(d => d.Treatment == DeletionTreatment.Remove && !d.IsPlayerAggregate)
            .Select(d => d.Collection)
            .ToHashSet(StringComparer.Ordinal);
    }

    public string ProfileCollection { get; }

    /// <summary>Remove-treated collections other than <c>pa_*</c> (receipt claim keys into them are redacted).</summary>
    public IReadOnlySet<string> RemovedCollections { get; }

    public CollectionDeletion? Find(string collection) => _plan.GetValueOrDefault(collection);

    /// <summary>Throws unless <paramref name="action"/> on <paramref name="key"/> matches the collection's treatment.</summary>
    public CollectionDeletion EnsureApplicable(DocumentKey key, PurgeAction action, string playerId)
    {
        if (key.Collection == ProfileCollection)
        {
            throw new InvalidOperationException($"{key} is removed only by the purge completion (the profile goes last, research R9).");
        }

        var entry = Find(key.Collection)
            ?? throw new InvalidOperationException($"Collection '{key.Collection}' has no purge treatment (purge record, NoPlayerData or unknown).");
        if (entry.IsPlayerAggregate)
        {
            if (action != PurgeAction.Remove || key.Key != playerId)
            {
                throw new InvalidOperationException($"Player aggregates are only removed by the purged player's key; refused {action} of {key}.");
            }

            return entry;
        }

        var expected = action == PurgeAction.Anonymize ? DeletionTreatment.Anonymize : DeletionTreatment.Remove;
        if (entry.Treatment != expected)
        {
            throw new InvalidOperationException($"Collection '{key.Collection}' is treated {entry.Treatment}; refused {action} of {key}.");
        }

        return entry;
    }

    /// <summary>Throws unless <paramref name="collection"/> is a module-owned collection (module steps never touch platform data).</summary>
    public CollectionDeletion EnsureModuleOwned(string collection)
    {
        var entry = Find(collection);
        if (entry is null || entry.IsPlayerAggregate || entry.Owner == ModuleRegistry.PlatformOwner || collection == ReceiptDocument.Collection)
        {
            throw new InvalidOperationException(
                $"Collection '{collection}' is not a module collection; deletion steps never rewrite pa_*, receipts, gm_audit or platform collections.");
        }

        return entry;
    }
}
