using System.Text.RegularExpressions;
using GameServer.Jobs;
using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Privacy;

namespace GameServer.Platform.Registry;

/// <summary>Resolved deletion treatment of one collection (FR-019). <paramref name="Owner"/> is a module name or <c>platform</c>.</summary>
public sealed record CollectionDeletion(string Collection, DeletionTreatment Treatment, string PlayerField, string Owner)
{
    /// <summary>Player aggregate collection (<c>pa_*</c>): removed by key (<c>{playerId}</c>), not found through <see cref="PlayerField"/>.</summary>
    public bool IsPlayerAggregate => Collection.StartsWith(ModuleRegistry.AggregateCollectionPrefix, StringComparison.Ordinal);
}

/// <summary>Deletion treatments (PRIV-01, FR-019, research R5) and lease jobs (JOB-01, research R1) of the registered modules.</summary>
public sealed partial class ModuleRegistry
{
    public const string PlatformOwner = "platform";
    public const string AggregateCollectionPrefix = "pa_";

    private IReadOnlyList<CollectionDeletion>? _deletionPlan;

    /// <summary>
    /// Treatment of every provisioned collection that holds or may hold player data: platform collections first, then
    /// <c>pa_*</c> and module collections in registration order. <c>account_deletions</c> (the purge record) is excluded.
    /// Collections with a registration error are left out; <see cref="Validate"/> reports them.
    /// </summary>
    public IReadOnlyList<CollectionDeletion> DeletionPlan() => _deletionPlan ??= ResolveDeletion(null);

    private void ValidateDeletion(List<string> errors) => ResolveDeletion(errors);

    /// <summary>Fixed treatments of platform-owned collections (rule 1); null = excluded from the purge (purge record).</summary>
    private static IReadOnlyList<(string Collection, DeletionTreatment? Treatment)> PlatformTreatments { get; } =
    [
        (ReceiptDocument.Collection, DeletionTreatment.Anonymize),
        (PlatformCollections.Audit, DeletionTreatment.Anonymize),
        (PlatformCollections.DataProtection, DeletionTreatment.NoPlayerData),
        (PlatformCollections.Gate, DeletionTreatment.NoPlayerData),
        (PlatformCollections.Leases, DeletionTreatment.NoPlayerData),
        (PlatformCollections.AccountDeletions, null),
    ];

    private List<CollectionDeletion> ResolveDeletion(List<string>? errors)
    {
        var plan = new List<CollectionDeletion>();
        var platform = PlatformTreatments.ToDictionary(p => p.Collection, p => p.Treatment, StringComparer.Ordinal);
        foreach (var (collection, treatment) in PlatformTreatments.Where(p => p.Treatment is not null))
        {
            plan.Add(new CollectionDeletion(collection, treatment!.Value, CollectionSpec.DefaultPlayerField, PlatformOwner));
        }

        foreach (var aggregate in Aggregates.Values)
        {
            plan.Add(new CollectionDeletion(aggregate.Collection, DeletionTreatment.Remove, CollectionSpec.DefaultPlayerField, PlatformOwner));
        }

        // Declarations per collection in registration order; a projection target counts as a declaration without a spec.
        var declarations = new Dictionary<string, List<(string Module, CollectionSpec Spec)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var module in _modules)
        {
            foreach (var spec in module.Descriptor.Collections)
            {
                if (!declarations.TryGetValue(spec.Name, out var list))
                {
                    declarations[spec.Name] = list = [];
                    order.Add(spec.Name);
                }

                list.Add((module.Descriptor.Name, spec));
            }
        }

        foreach (var module in _modules)
        {
            foreach (var projection in module.Descriptor.OutboxProjections)
            {
                if (!declarations.ContainsKey(projection.Collection) && !platform.ContainsKey(projection.Collection))
                {
                    errors?.Add(
                        $"[module {module.Descriptor.Name}] outbox projection target '{projection.Collection}' (kind '{projection.Kind}') has no CollectionSpec. " +
                        "Fix: declare it in ModuleDescriptor.Collections with a Deletion treatment; the platform cannot guess what a payload holds (FR-019).");
                }
            }
        }

        foreach (var collection in order)
        {
            var list = declarations[collection];
            var fixedTreatment = platform.TryGetValue(collection, out var platformTreatment) || collection.StartsWith(AggregateCollectionPrefix, StringComparison.Ordinal);
            if (fixedTreatment)
            {
                var expected = collection.StartsWith(AggregateCollectionPrefix, StringComparison.Ordinal) ? DeletionTreatment.Remove : platformTreatment;
                foreach (var (module, spec) in list.Where(d => d.Spec.Deletion != DeletionTreatment.Unspecified && d.Spec.Deletion != expected))
                {
                    errors?.Add(
                        $"[module {module}] collection '{collection}' is owned by the platform (deletion treatment {expected?.ToString() ?? "purge record"}) " +
                        $"but is re-declared with {spec.Deletion}. Fix: re-declare it with Deletion Unspecified (index only).");
                }

                continue;
            }

            if (ResolveModuleCollection(collection, list, errors) is { } resolved)
            {
                plan.Add(resolved);
            }
        }

        ValidateDeletionSteps(errors);
        return plan;
    }

    private CollectionDeletion? ResolveModuleCollection(string collection, List<(string Module, CollectionSpec Spec)> list, List<string>? errors)
    {
        var explicitDeclarations = list.Where(d => d.Spec.Deletion != DeletionTreatment.Unspecified).ToArray();
        if (explicitDeclarations.Length == 0)
        {
            errors?.Add(
                $"[module {list[0].Module}] collection '{collection}' declares no deletion treatment. " +
                "Fix: CollectionSpec(..., Deletion: DeletionTreatment.NoPlayerData | Anonymize | Remove | ModuleStep) (PRIV-01, FR-019).");
            return null;
        }

        var (owner, spec) = explicitDeclarations[0];
        var conflicting = explicitDeclarations.Where(d => d.Spec.Deletion != spec.Deletion || d.Spec.PlayerField != spec.PlayerField).ToArray();
        if (conflicting.Length > 0)
        {
            errors?.Add(
                $"[module {conflicting[0].Module}] collection '{collection}' declares deletion treatment {conflicting[0].Spec.Deletion} " +
                $"(player field '{conflicting[0].Spec.PlayerField}') but module '{owner}' declares {spec.Deletion} (player field '{spec.PlayerField}'). " +
                "Fix: state the treatment in one declaration only; others use Unspecified.");
            return null;
        }

        if (spec.Deletion is DeletionTreatment.Anonymize or DeletionTreatment.Remove && !PagedQuery.IsValidField(spec.PlayerField))
        {
            errors?.Add(
                $"[module {owner}] collection '{collection}' has an invalid player field '{spec.PlayerField}'. " +
                "Fix: a dotted path of identifiers, e.g. 'playerId' or 'payload.playerId'.");
            return null;
        }

        if (spec.Deletion == DeletionTreatment.ModuleStep && _modules.First(m => m.Descriptor.Name == owner).Descriptor.DeletionSteps.Count == 0)
        {
            errors?.Add(
                $"[module {owner}] collection '{collection}' uses deletion treatment ModuleStep but module '{owner}' declares no deletion step. " +
                "Fix: add a DeletionStepRegistration implementing IAccountDeletionStep to ModuleDescriptor.DeletionSteps (FR-020).");
            return null;
        }

        return new CollectionDeletion(collection, spec.Deletion, spec.PlayerField, owner);
    }

    private void ValidateDeletionSteps(List<string>? errors)
    {
        if (errors is null)
        {
            return;
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in _modules)
        {
            var owner = module.Descriptor.Name;
            foreach (var step in module.Descriptor.DeletionSteps)
            {
                if (!JobNamePattern().IsMatch(step.Name))
                {
                    errors.Add($"[module {owner}] deletion step name '{step.Name}' is invalid. Fix: use ^[a-z][a-z0-9._-]{{1,63}}$.");
                }

                if (!seen.TryAdd(step.Name, owner))
                {
                    errors.Add(
                        $"[module {owner}] deletion step '{step.Name}' is already registered by module '{seen[step.Name]}'. " +
                        "Fix: deletion step names must be unique per deployment (FR-020).");
                }

                if (!IsConcrete(step.StepType, typeof(IAccountDeletionStep)))
                {
                    errors.Add(
                        $"[module {owner}] deletion step '{step.Name}' type '{step.StepType.FullName}' does not implement IAccountDeletionStep. " +
                        "Fix: use a non-abstract class implementing GameServer.Privacy.IAccountDeletionStep.");
                }
            }
        }
    }

    private void ValidateJobs(GameServerOptions options, List<string> errors)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        ValidateJobDeclarations(seen, errors);

        // A misspelled override would silently leave the job enabled with its default interval (review minor 7).
        foreach (var name in options.Jobs.Settings.Keys.Where(k => !seen.ContainsKey(k)).Order(StringComparer.Ordinal))
        {
            errors.Add(
                $"[jobs] GameServer:Jobs:Settings:{name} names no registered job. " +
                $"Fix: use one of: {(seen.Count == 0 ? "(none)" : string.Join(", ", seen.Keys.Order(StringComparer.Ordinal)))}.");
        }
    }

    private void ValidateJobDeclarations(Dictionary<string, string> seen, List<string> errors)
    {
        foreach (var module in _modules)
        {
            var owner = module.Descriptor.Name;
            foreach (var job in module.Descriptor.Jobs)
            {
                if (!JobNamePattern().IsMatch(job.Name))
                {
                    errors.Add($"[module {owner}] job name '{job.Name}' is invalid. Fix: use ^[a-z][a-z0-9._-]{{1,63}}$ (JOB-01).");
                }

                if (!seen.TryAdd(job.Name, owner))
                {
                    errors.Add(
                        $"[module {owner}] job '{job.Name}' is already registered by module '{seen[job.Name]}'. " +
                        "Fix: job names must be unique per deployment; they name the lease document (JOB-01).");
                }

                if (!IsConcrete(job.JobType, typeof(ILeaseJob)))
                {
                    errors.Add(
                        $"[module {owner}] job '{job.Name}' type '{job.JobType.FullName}' does not implement ILeaseJob. " +
                        "Fix: use a non-abstract class implementing GameServer.Jobs.ILeaseJob.");
                }

                if (job.DefaultIntervalSeconds is < GameServer.Platform.Jobs.JobSettings.MinIntervalSeconds or > GameServer.Platform.Jobs.JobSettings.MaxIntervalSeconds)
                {
                    errors.Add(
                        $"[module {owner}] job '{job.Name}' default interval {job.DefaultIntervalSeconds} s is outside " +
                        $"{GameServer.Platform.Jobs.JobSettings.MinIntervalSeconds}..{GameServer.Platform.Jobs.JobSettings.MaxIntervalSeconds} s.");
                }
            }
        }
    }

    private static bool IsConcrete(Type type, Type contract) => contract.IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false };

    [GeneratedRegex("^[a-z][a-z0-9._-]{1,63}$")]
    private static partial Regex JobNamePattern();
}
