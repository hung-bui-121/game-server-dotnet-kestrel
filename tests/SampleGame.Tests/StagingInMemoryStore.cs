using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SampleGame.Tests;

/// <summary>
/// The in-memory store without its Development/Test marker, so a Staging or Production composition of the sample host can start
/// in a test without Couchbase (slice 014 T034: the endpoint check over every flavor). Test-only: never register it in a host.
/// </summary>
internal sealed class StagingInMemoryStore(InMemoryDocumentStore inner)
    : ITransactionalDocumentStore, IKeyValueStore, IPagedQueryStore, IMaintenanceQueryStore, ICollectionAwareStore, ITransactionOnlyAwareStore
{
    private readonly IKeyValueStore _keyValue = inner;

    /// <summary>Replaces the store registrations of a composition with this wrapper around one in-memory store.</summary>
    public static void Replace(IServiceCollection services)
    {
        services.RemoveAll<ITransactionalDocumentStore>();
        services.RemoveAll<IKeyValueStore>();
        services.RemoveAll<IPagedQueryStore>();
        services.RemoveAll<IMaintenanceQueryStore>();
        services.AddSingleton(sp => new StagingInMemoryStore(new InMemoryDocumentStore(sp.GetRequiredService<GameServer.Time.IClock>())));
        services.AddSingleton<ITransactionalDocumentStore>(sp => sp.GetRequiredService<StagingInMemoryStore>());
        services.AddSingleton<IKeyValueStore>(sp => sp.GetRequiredService<StagingInMemoryStore>());
        services.AddSingleton<IPagedQueryStore>(sp => sp.GetRequiredService<StagingInMemoryStore>());
        services.AddSingleton<IMaintenanceQueryStore>(sp => sp.GetRequiredService<StagingInMemoryStore>());
    }

    public Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options) => inner.RunTransactionAsync(body, options);

    public Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken) => inner.GetAsync(key, cancellationToken);

    public Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken) =>
        inner.GetManyAsync(keys, cancellationToken);

    public Task<bool> InsertIfAbsentAsync(DocumentKey key, JsonObject content, CancellationToken cancellationToken) =>
        inner.InsertIfAbsentAsync(key, content, cancellationToken);

    public Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken) => inner.MarkOutboxRelayedAsync(receiptKey, cancellationToken);

    public Task MarkOutboxFailedAsync(DocumentKey receiptKey, int attempts, DateTimeOffset nextAttemptAt, bool deadLetter, CancellationToken cancellationToken) =>
        inner.MarkOutboxFailedAsync(receiptKey, attempts, nextAttemptAt, deadLetter, cancellationToken);

    public Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(DateTimeOffset committedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        inner.QueryPendingOutboxAsync(committedBefore, now, limit, cancellationToken);

    Task<StoredDocument?> IKeyValueStore.GetAsync(DocumentKey key, CancellationToken cancellationToken) => _keyValue.GetAsync(key, cancellationToken);

    public Task<bool> InsertAsync(DocumentKey key, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken) =>
        _keyValue.InsertAsync(key, content, expiry, cancellationToken);

    public Task<bool> ReplaceAsync(StoredDocument existing, JsonObject content, TimeSpan? expiry, CancellationToken cancellationToken) =>
        _keyValue.ReplaceAsync(existing, content, expiry, cancellationToken);

    public Task RemoveAsync(DocumentKey key, CancellationToken cancellationToken) => _keyValue.RemoveAsync(key, cancellationToken);

    public Task<Page> QueryPageAsync(PagedQuery query, CancellationToken cancellationToken) => inner.QueryPageAsync(query, cancellationToken);

    public Task<IReadOnlyList<StoredDocument>> QueryDueAsync(DueQuery query, CancellationToken cancellationToken) => inner.QueryDueAsync(query, cancellationToken);

    public Task<IReadOnlyList<DocumentKey>> QueryReferencesAsync(ReferenceQuery query, CancellationToken cancellationToken) =>
        inner.QueryReferencesAsync(query, cancellationToken);

    public void UseCollections(IEnumerable<string> collections) => inner.UseCollections(collections);

    public void UseTransactionOnlyCollections(IReadOnlySet<string> collections) => inner.UseTransactionOnlyCollections(collections);
}
