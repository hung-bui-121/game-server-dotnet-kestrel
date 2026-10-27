using System.Text.Json.Nodes;
using GameServer.Errors;
using GameServer.Platform.Commands;

namespace GameServer.Platform.Persistence;

/// <summary>
/// Insert-only documents written outside a command but in the key space of command claim markers (e.g. the identity
/// mapping created by a first provider login, IDN-01, next to <c>identity.link</c>'s claim). The insert runs in a
/// transaction, like a claim, so the two never meet as a KV write against a staged transactional insert. Modules use it
/// instead of opening transactions (CMD-05). Never for <c>pa_*</c> or receipts.
/// </summary>
public sealed class ClaimStore(ITransactionalDocumentStore store, GameServerOptions options)
{
    private const int MaxRounds = 3;

    /// <summary>Inserts <paramref name="content"/> unless the key exists; returns the stored document's content and whether this call inserted it.</summary>
    public async Task<(JsonObject Content, bool Inserted)> InsertOrGetAsync(DocumentKey key, JsonObject content, CancellationToken cancellationToken)
    {
        if (key.Collection == ReceiptDocument.Collection || key.Collection.StartsWith("pa_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Claims cannot live in collection '{key.Collection}'.");
        }

        for (var round = 1; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await store.RunTransactionAsync(
                    async tx =>
                    {
                        var existing = await tx.GetManyAsync([key]);
                        if (existing[0] is { } document)
                        {
                            return (document.Content, false);
                        }

                        await tx.InsertAsync(key, (JsonObject)content.DeepClone());
                        return (content, true);
                    },
                    new TransactionRunOptions(TimeSpan.FromSeconds(options.Transactions.ExpirationSeconds)));
            }
            catch (DocumentExistsException) when (round < MaxRounds)
            {
                // Couchbase reports a lost concurrent insert as "exists": the next round reads the winner.
            }
            catch (DocumentExistsException ex)
            {
                throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Insert race on {key}.", ex) { RetryAfterMs = 200 };
            }
            catch (TransactionConflictExpiredException ex)
            {
                throw new GameServerException(CoreErrorCodes.ConcurrentUpdate, $"Insert of {key} expired.", ex) { RetryAfterMs = 200 };
            }
        }
    }
}
