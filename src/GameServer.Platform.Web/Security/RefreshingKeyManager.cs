using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace GameServer.Platform.Web.Security;

/// <summary>
/// Wraps the default XML key manager so that Data Protection drops its cached key ring as soon as the shared
/// repository loads keys written by another instance (the default manager only notices keys it created itself).
/// </summary>
internal sealed class RefreshingKeyManager(IKeyManager inner, KeyRingRepository repository) : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate) => inner.CreateNewKey(activationDate, expirationDate);

    public IReadOnlyCollection<IKey> GetAllKeys() => inner.GetAllKeys();

    public CancellationToken GetCacheExpirationToken()
    {
        var own = inner.GetCacheExpirationToken();
        var shared = repository.ChangeToken;
        if (own.IsCancellationRequested || shared.IsCancellationRequested)
        {
            return new CancellationToken(canceled: true);
        }

        // One linked source per key ring refresh (about daily, or when keys change); not disposed because Data
        // Protection keeps registering on the token it was given.
        return CancellationTokenSource.CreateLinkedTokenSource(own, shared).Token;
    }

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason = null) => inner.RevokeAllKeys(revocationDate, reason);

    public void RevokeKey(Guid keyId, string? reason = null) => inner.RevokeKey(keyId, reason);
}
