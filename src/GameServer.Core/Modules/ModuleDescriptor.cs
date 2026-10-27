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
}

/// <summary>A collection to provision, with optional secondary indexes.</summary>
/// <param name="TransactionOnly">
/// Written only inside transactions (with its audit entry or aggregate change): plain key-value writes are refused,
/// because a plain insert racing a transactional insert of the same key silently wins (TST-04 V15-T2).
/// </param>
public sealed record CollectionSpec(string Name, IReadOnlyList<IndexSpec>? Indexes = null, bool TransactionOnly = false);

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
