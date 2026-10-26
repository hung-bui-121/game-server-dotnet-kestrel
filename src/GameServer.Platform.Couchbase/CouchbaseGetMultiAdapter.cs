using System.Text.Json.Nodes;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.Core.IO.Transcoders;
using Couchbase.KeyValue;
using GameServer.Platform.Persistence;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Isolates the volatile <c>AttemptContext.GetMulti</c> API (DAT-11, V23) so an SDK change only touches this file.
/// Never issue concurrent <c>GetAsync</c> calls on one attempt (docs: operations must be in order).
/// </summary>
internal static class CouchbaseGetMultiAdapter
{
    public static async Task<IReadOnlyList<StoredDocument?>> GetMultiAsync(
        AttemptContext attempt,
        IReadOnlyList<DocumentKey> keys,
        IReadOnlyList<ICouchbaseCollection> collections,
        ITypeTranscoder transcoder)
    {
        var specs = keys.Select((k, i) => new TransactionGetMultiSpec(collections[i], k.Key, transcoder)).ToList();

        // Read-skew detection: the bundle must be a consistent view because the handler decides on it (review M3).
        var options = (TransactionGetMultiOptionsBuilder)TransactionGetMultiOptionsBuilder.Create()
            .Mode(TransactionGetMultiMode.PrioritizeReadSkewDetection);

        var result = await attempt.GetMulti(specs, options);
        var documents = new StoredDocument?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            // Handle is null: GetMulti returns no TransactionGetResult, see CouchbaseTransactionContext.ReplaceAsync.
            documents[i] = result.Exists(i) ? new StoredDocument(keys[i], result.ContentAs<JsonObject>(i)!, null) : null;
        }

        return documents;
    }

    public static async Task<IReadOnlyList<StoredDocument?>> GetSequentialAsync(
        AttemptContext attempt,
        IReadOnlyList<DocumentKey> keys,
        IReadOnlyList<ICouchbaseCollection> collections)
    {
        var documents = new StoredDocument?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var result = await attempt.GetOptionalAsync(collections[i], keys[i].Key);
            documents[i] = result is null ? null : new StoredDocument(keys[i], result.ContentAs<JsonObject>()!, result);
        }

        return documents;
    }
}
