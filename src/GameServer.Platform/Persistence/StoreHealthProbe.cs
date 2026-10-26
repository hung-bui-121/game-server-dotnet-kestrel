using GameServer.Platform.Commands;

namespace GameServer.Platform.Persistence;

/// <summary>Cheap reachability check of the document store for readiness (OBS-03).</summary>
public interface IStoreHealthProbe
{
    /// <summary>Completes when the store answered; throws otherwise.</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Default probe: a key-value get of a key that never exists (one round trip, no N1QL).</summary>
public sealed class DocumentStoreHealthProbe(ITransactionalDocumentStore store) : IStoreHealthProbe
{
    private static readonly DocumentKey ProbeKey = new(ReceiptDocument.Collection, ReceiptDocument.KeyPrefix + "health-probe");

    public Task CheckAsync(CancellationToken cancellationToken) => store.GetAsync(ProbeKey, cancellationToken);
}
