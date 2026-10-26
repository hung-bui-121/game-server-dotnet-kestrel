using System.Text.Json.Nodes;

namespace GameServer.Platform.Persistence;

/// <summary>Key of a document inside the game scope <c>g_&lt;gameId&gt;</c>.</summary>
public readonly record struct DocumentKey(string Collection, string Key)
{
    public override string ToString() => $"{Collection}/{Key}";
}

/// <summary>A document as read from a store. <see cref="Handle"/> is store-specific (CAS, transaction get result).</summary>
public sealed class StoredDocument(DocumentKey key, JsonObject content, object? handle)
{
    public DocumentKey Key { get; } = key;

    /// <summary>Content; treat as read-only.</summary>
    public JsonObject Content { get; } = content;

    public object? Handle { get; } = handle;
}

public sealed record TransactionRunOptions(TimeSpan Expiration);

/// <summary>
/// Minimal transactional document store used by the command runner (research R3).
/// Only <c>GameServer.Platform</c> may write <c>pa_*</c> and <c>receipts</c> through it (CMD-05).
/// </summary>
public interface ITransactionalDocumentStore
{
    /// <summary>
    /// Runs <paramref name="body"/> in one ACID transaction. The body may run several times.
    /// Exceptions thrown by the body roll the transaction back and are rethrown unchanged.
    /// Store failures surface as <see cref="TransactionCommitAmbiguousException"/>,
    /// <see cref="TransactionConflictExpiredException"/> or <see cref="TransactionFailedException"/>.
    /// </summary>
    Task<T> RunTransactionAsync<T>(Func<ITransactionContext, Task<T>> body, TransactionRunOptions options);

    /// <summary>Reads committed data outside a transaction (Read Committed).</summary>
    Task<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken);

    /// <summary>Reads several committed documents concurrently; result is aligned with <paramref name="keys"/>.</summary>
    Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys, CancellationToken cancellationToken);

    /// <summary>Inserts a projection document if absent. Returns false if it already existed.</summary>
    Task<bool> InsertIfAbsentAsync(DocumentKey key, JsonObject content, CancellationToken cancellationToken);

    /// <summary>Sets <c>outboxPending = false</c> on a committed receipt.</summary>
    Task MarkOutboxRelayedAsync(DocumentKey receiptKey, CancellationToken cancellationToken);

    /// <summary>Committed receipts with <c>outboxPending = true</c> committed before <paramref name="committedBefore"/>.</summary>
    Task<IReadOnlyList<StoredDocument>> QueryPendingOutboxAsync(DateTimeOffset committedBefore, int limit, CancellationToken cancellationToken);
}

/// <summary>Operations available inside a transaction attempt. Call them sequentially (V23).</summary>
public interface ITransactionContext
{
    /// <summary>Bundle read (DAT-11); result is aligned with <paramref name="keys"/>, null when missing.</summary>
    Task<IReadOnlyList<StoredDocument?>> GetManyAsync(IReadOnlyList<DocumentKey> keys);

    Task InsertAsync(DocumentKey key, JsonObject content);

    Task ReplaceAsync(StoredDocument existing, JsonObject content);
}

/// <summary>The commit may or may not have happened (CMD-04).</summary>
public sealed class TransactionCommitAmbiguousException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Retries on conflicts ran until the expiration without committing (CMD-05).</summary>
public sealed class TransactionConflictExpiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The transaction definitely did not commit for a store reason.</summary>
public sealed class TransactionFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Insert of an existing key.</summary>
public sealed class DocumentExistsException(DocumentKey key) : Exception($"Document {key} already exists.")
{
    public DocumentKey Key { get; } = key;
}

/// <summary>
/// A document changed between the bundle read and the write of the same attempt (stores whose bundle read
/// returns no write handle). The command runner re-runs the command.
/// </summary>
public sealed class StaleReadException(DocumentKey key) : Exception($"Document {key} changed during the transaction.")
{
    public DocumentKey Key { get; } = key;
}
