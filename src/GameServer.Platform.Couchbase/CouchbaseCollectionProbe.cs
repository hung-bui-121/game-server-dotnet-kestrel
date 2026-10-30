using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Core.Retry;
using Couchbase.KeyValue;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Tells whether a collection exists by reading a key that never exists (research R11). Needs only key-value read rights,
/// so the runtime (data-only) credential can run it.
/// </summary>
internal static class CouchbaseCollectionProbe
{
    public const string ProbeKey = "_schema_probe";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True when the collection answers (document not found), false when the cluster does not know it. Verified on SDK 3.10 +
    /// EE 7.6.12 (T040): an unknown collection or scope is an <see cref="UnambiguousTimeoutException"/> whose retry reasons are
    /// <c>CollectionNotFound</c> / <c>ScopeNotFound</c>, never a <see cref="CollectionNotFoundException"/>; both forms are
    /// classified as missing. Any other error (cluster unreachable, authentication) propagates unchanged.
    /// </summary>
    public static async Task<bool> ExistsAsync(ICouchbaseCollection collection, CancellationToken cancellationToken)
    {
        try
        {
            await collection.GetAsync(ProbeKey, new GetOptions().Timeout(Timeout).CancellationToken(cancellationToken));
            return true;
        }
        catch (DocumentNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (IsUnknownCollection(ex))
        {
            return false;
        }
    }

    internal static bool IsUnknownCollection(Exception exception) => exception switch
    {
        CollectionNotFoundException or ScopeNotFoundException => true,
        global::Couchbase.Core.Exceptions.TimeoutException { Context: KeyValueErrorContext context } =>
            (context.RetryReasons ?? []).Any(r => r is RetryReason.CollectionNotFound or RetryReason.ScopeNotFound),
        _ => false,
    };
}
