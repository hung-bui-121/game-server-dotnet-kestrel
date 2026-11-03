using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;

namespace GameServer.TestKit;

/// <summary>
/// The transaction behind a <see cref="CommandContextDouble"/>: no store. The runner's context only reads claim markers
/// through it, so seeded markers read as present and every other key as missing; writes belong to the runner and are refused.
/// </summary>
internal sealed class DetachedTransaction(IReadOnlySet<DocumentKey> existingClaims) : ITransactionContext
{
    public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var documents = new StoredDocument?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            if (existingClaims.Contains(keys[i]))
            {
                documents[i] = new StoredDocument(keys[i], new JsonObject { ["claimKey"] = keys[i].Key }, handle: null);
            }
        }

        return Task.FromResult<IReadOnlyList<StoredDocument?>>(documents);
    }

    public Task InsertAsync(DocumentKey key, JsonObject content) => throw NotSupported();

    public Task ReplaceAsync(StoredDocument existing, JsonObject content) => throw NotSupported();

    public Task RemoveAsync(StoredDocument existing) => throw NotSupported();

    private static NotSupportedException NotSupported() =>
        new("CommandContextDouble has no store: only the command runner writes documents (transactions are not simulated).");
}
