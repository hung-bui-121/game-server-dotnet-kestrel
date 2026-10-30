using GameServer.Platform.Persistence;

namespace GameServer.Platform.Testing;

/// <summary>Readiness of the file store (R8): unhealthy once faulted, otherwise the default store probe.</summary>
internal sealed class FileStoreHealthProbe(FileDocumentStore fileStore) : IStoreHealthProbe
{
    private readonly DocumentStoreHealthProbe _store = new(fileStore.Store);

    public Task CheckAsync(CancellationToken cancellationToken)
    {
        if (fileStore.IsFaulted)
        {
            throw new IOException("[filestore] the store is faulted after a write failed past its commit point; restart the host.");
        }

        return _store.CheckAsync(cancellationToken);
    }
}
