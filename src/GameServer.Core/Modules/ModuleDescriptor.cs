using GameServer.Aggregates;
using GameServer.Commands;
using GameServer.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace GameServer.Modules;

/// <summary>
/// Single registration point of a module (ARC-03, ARC-06): everything the host needs to know
/// to provision, validate and wire the module.
/// </summary>
public sealed class ModuleDescriptor
{
    public required string Name { get; init; }

    public string Version { get; init; } = "1.0.0";

    /// <summary>Names of modules that must also be registered.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Config files (CFG-01) that must exist in the effective config set.</summary>
    public IReadOnlyList<string> RequiredConfigFiles { get; init; } = [];

    /// <summary>GM permissions this module checks (SEC-07 catalog).</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>Extra collections (besides <c>pa_*</c>) to provision.</summary>
    public IReadOnlyList<CollectionSpec> Collections { get; init; } = [];

    public IReadOnlyList<PlayerAggregateDescriptor> Aggregates { get; init; } = [];

    public IReadOnlyList<CommandRegistration> Commands { get; init; } = [];

    public ErrorCodeSet ErrorCodes { get; init; } = ErrorCodeSet.None;

    /// <summary>Public routes of the module (SEC-09); added to the allowlist and listed in the module PCR.</summary>
    public IReadOnlyList<string> PublicEndpoints { get; init; } = [];

    /// <summary>Outbox kinds projected into a collection by the default projection consumer.</summary>
    public IReadOnlyList<OutboxProjection> OutboxProjections { get; init; } = [];

    /// <summary>Background jobs coordinated by a lease document, run on one instance at a time (JOB-01, OPS-05).</summary>
    public IReadOnlyList<JobDescriptor> Jobs { get; init; } = [];

    /// <summary>
    /// Account deletion steps of this module (PRIV-01, FR-020), run by the purge in registration order before the player's
    /// identities and profile are removed. Required when a collection declares <see cref="DeletionTreatment.ModuleStep"/>.
    /// </summary>
    public IReadOnlyList<DeletionStepRegistration> DeletionSteps { get; init; } = [];
}

/// <summary>What the account purge does with a collection's documents that reference the deleted player (PRIV-01, FR-019).</summary>
public enum DeletionTreatment
{
    /// <summary>No treatment declared. Allowed only on a re-declaration of a collection whose treatment is declared elsewhere (index-only).</summary>
    Unspecified = 0,

    /// <summary>The collection never holds player data.</summary>
    NoPlayerData,

    /// <summary>Every value equal to the player id is overwritten with the anonymous id; the record is kept.</summary>
    Anonymize,

    /// <summary>Every document whose player field equals the player id is removed.</summary>
    Remove,

    /// <summary>The declaring module's <see cref="DeletionStepRegistration"/> handles the collection.</summary>
    ModuleStep,
}

/// <summary>A collection to provision, with optional secondary indexes.</summary>
/// <param name="TransactionOnly">
/// Written only inside transactions (with its audit entry or aggregate change): plain key-value writes are refused,
/// because a plain insert racing a transactional insert of the same key silently wins (TST-04 V15-T2).
/// </param>
/// <param name="Deletion">Account deletion treatment (FR-019); every collection must state one in exactly one declaration.</param>
/// <param name="PlayerField">Dotted path of the field holding the player id; used by <see cref="DeletionTreatment.Anonymize"/> and <see cref="DeletionTreatment.Remove"/>.</param>
public sealed record CollectionSpec(
    string Name,
    IReadOnlyList<IndexSpec>? Indexes = null,
    bool TransactionOnly = false,
    DeletionTreatment Deletion = DeletionTreatment.Unspecified,
    string PlayerField = CollectionSpec.DefaultPlayerField)
{
    public const string DefaultPlayerField = "playerId";
}

/// <summary>
/// A lease job (JOB-01): <paramref name="JobType"/> implements <c>GameServer.Jobs.ILeaseJob</c> and is resolved from DI
/// per run. <paramref name="Name"/> matches <c>^[a-z][a-z0-9._-]{1,63}$</c> and is unique per deployment.
/// </summary>
/// <param name="DefaultIntervalSeconds">Cluster-wide time between two finished runs; overridable per job in configuration.</param>
public sealed record JobDescriptor(string Name, Type JobType, int DefaultIntervalSeconds);

/// <summary>
/// An account deletion step (FR-020): <paramref name="StepType"/> implements <c>GameServer.Privacy.IAccountDeletionStep</c>
/// and is resolved from DI per purge. <paramref name="Name"/> is unique across modules.
/// </summary>
public sealed record DeletionStepRegistration(string Name, Type StepType);

/// <summary>N1QL index: <c>CREATE INDEX {Name} ON {collection}({Keys}) WHERE {Where}</c>.</summary>
public sealed record IndexSpec(string Name, string Keys, string? Where = null);

/// <summary>Projects outbox entries of <paramref name="Kind"/> into <paramref name="Collection"/>, keyed by entry id.</summary>
public sealed record OutboxProjection(string Kind, string Collection);

/// <summary>Deployment facts available to modules while registering services.</summary>
public sealed record ModuleContext(string GameId, string EnvironmentId, string DeploymentEnvironment);

/// <summary>A framework or game module. The host registers it with one call (ARC-03).</summary>
public interface IGameModule
{
    ModuleDescriptor Describe();

    void ConfigureServices(IServiceCollection services, ModuleContext context);
}
