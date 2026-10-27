namespace GameServer.Platform.Persistence;

/// <summary>
/// Makes the store usable (collections and indexes provisioned) before a startup service touches it. Idempotent and
/// shared: every caller awaits the same one-time initialization, whatever the order in which hosted services start.
/// Stores that need no initialization register nothing; callers treat a missing service as "ready".
/// </summary>
public interface IStoreInitializer
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken);
}
