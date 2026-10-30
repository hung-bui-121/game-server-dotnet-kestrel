namespace GameServer.Platform.Persistence;

/// <summary>Creates the store schema (scope, collections, indexes) for the provisioning run of the host (DAT-07, FR-029).</summary>
public interface IStoreProvisioner
{
    /// <summary>Creates what is missing, in order; reports each item; throws <see cref="ProvisioningFailedException"/> on the first failure.</summary>
    Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken);
}

public enum ProvisioningOutcome
{
    Created,
    AlreadyPresent,
}

/// <summary>One provisioned item, e.g. kind <c>collection</c>, name <c>g_sample.txn_meta</c>.</summary>
public sealed record ProvisioningItem(string Kind, string Name, ProvisioningOutcome Outcome, TimeSpan Elapsed);

/// <summary>Provisioning stopped at one item; <see cref="Cause"/> never contains credentials.</summary>
public sealed class ProvisioningFailedException(string kind, string name, string cause, Exception? inner = null)
    : Exception($"{kind} {name}: {cause}", inner)
{
    public string Kind { get; } = kind;

    public string Name { get; } = name;

    public string Cause { get; } = cause;
}
