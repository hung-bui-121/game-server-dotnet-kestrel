using System.Net;
using Couchbase;
using Couchbase.Core.Exceptions;
using Couchbase.Management.Collections;
using GameServer.Modules;
using GameServer.Platform.Commands;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Creates scope <c>g_&lt;gameId&gt;</c>, collections declared by descriptors (<c>pa_*</c>, module collections),
/// <c>receipts</c>, <c>txn_meta</c> and the partial index <c>receipts(outboxPending)</c> (DAT-07).
/// Runs at startup in Development (or when enabled), before any transaction; Production uses the same code
/// from an ops command.
/// </summary>
public sealed class CouchbaseProvisioner(
    CouchbaseConnection connection,
    ModuleRegistry registry,
    GameServerOptions gameServer,
    ILogger<CouchbaseProvisioner> logger) : IHostedService, IStoreInitializer
{
    private readonly object _gate = new();
    private Task? _initialization;

    public const string PendingOutboxIndex = "ix_receipts_outbox_pending";

    /// <summary>Receipts of a player, newest first: GM history and the account purge (research R5).</summary>
    public const string ReceiptsPlayerIndex = "ix_receipts_player";

    /// <summary>Unfinished purges due for an attempt (research R4).</summary>
    public const string AccountDeletionsDueIndex = "ix_account_deletions_due";

    /// <summary>Attempts of a management statement (scope, collection, index) on transient errors.</summary>
    private const int IndexAttempts = 6;

    public IReadOnlyList<string> RequiredCollections() =>
        registry.RequiredCollections().Append(CouchbaseConnection.TransactionMetadataCollection).Distinct(StringComparer.Ordinal).ToArray();

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var cluster = await connection.GetClusterAsync();
        var bucket = await cluster.BucketAsync(connection.BucketName);
        var manager = bucket.Collections;

        // Create only what is missing: the SDK logs every "already exists" at Error level (false alarms on each restart).
        // "Already exists" is still expected from instances provisioning at the same time.
        var scope = (await manager.GetAllScopesAsync(new GetAllScopesOptions().CancellationToken(cancellationToken)))
            .FirstOrDefault(s => s.Name == connection.ScopeName);
        var existing = scope?.Collections.Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        if (scope is null)
        {
            await CreateMissingAsync(
                () => manager.CreateScopeAsync(connection.ScopeName, new CreateScopeOptions().CancellationToken(cancellationToken)),
                $"scope {connection.ScopeName}",
                cancellationToken);
        }

        foreach (var collection in RequiredCollections().Where(c => !existing.Contains(c)))
        {
            await CreateMissingAsync(
                () => manager.CreateCollectionAsync(
                    connection.ScopeName,
                    collection,
                    CreateCollectionSettings.Default,
                    new CreateCollectionOptions().CancellationToken(cancellationToken)),
                $"collection {connection.ScopeName}.{collection}",
                cancellationToken);
        }

        var keyspace = $"`{connection.BucketName}`.`{connection.ScopeName}`";
        var statements = new List<string>
        {
            $"CREATE INDEX `{PendingOutboxIndex}` IF NOT EXISTS ON {keyspace}.`receipts`(committedAtUtc) WHERE outboxPending = true",
            $"CREATE INDEX `{ReceiptsPlayerIndex}` IF NOT EXISTS ON {keyspace}.`receipts`(playerId, committedAtUtc DESC, META().id DESC)",
            // (due, id): the due query's ORDER BY reads index order and stops at its LIMIT (no sort of the whole backlog, PERF-07).
            $"CREATE INDEX `{AccountDeletionsDueIndex}` IF NOT EXISTS ON {keyspace}.`{PlatformCollections.AccountDeletions}`(nextAttemptAtUtc, META().id) " +
            "WHERE purgedAtUtc IS NOT VALUED",
        };
        foreach (var module in registry.Modules)
        {
            foreach (var spec in module.Descriptor.Collections)
            {
                foreach (var index in spec.Indexes ?? [])
                {
                    var where = index.Where is null ? string.Empty : " WHERE " + index.Where;
                    statements.Add($"CREATE INDEX `{index.Name}` IF NOT EXISTS ON {keyspace}.`{spec.Name}`({index.Keys}){where}");
                }
            }
        }

        statements.AddRange(ReferenceIndexes().Select(r => $"CREATE INDEX `{r.Name}` IF NOT EXISTS ON {keyspace}.`{r.Collection}`({r.Keys})"));

        foreach (var statement in statements)
        {
            await CreateIndexAsync(cluster, statement, cancellationToken);
        }

        logger.LogInformation("Provisioned {Count} collections for game {GameId}.", RequiredCollections().Count, gameServer.GameId);
    }

    /// <summary>
    /// Reference indexes of the account purge (research R5): <c>ix_{collection}_player_ref</c> on (player field, document id)
    /// for every Anonymize/Remove collection of the deletion plan, so the key-ordered reference query reads index order and
    /// stops at its LIMIT (review minor 4: a history index (player, time, id) made each batch sort the player's whole
    /// history). Skipped only when a declared index has exactly these keys and no WHERE clause (review minor 3: a partial
    /// index cannot serve every reference). <c>pa_*</c> are removed by key and need none.
    /// </summary>
    public IReadOnlyList<ReferenceIndex> ReferenceIndexes()
    {
        var declared = registry.Modules
            .SelectMany(m => m.Descriptor.Collections)
            .SelectMany(c => (c.Indexes ?? []).Where(i => i.Where is null).Select(i => (Collection: c.Name, i.Keys)))
            .ToArray();
        return registry.DeletionPlan()
            .Where(d => d.Treatment is DeletionTreatment.Anonymize or DeletionTreatment.Remove && !d.IsPlayerAggregate)
            .Where(d => !declared.Any(i => i.Collection == d.Collection && IsReferenceShaped(i.Keys, d.PlayerField)))
            .Select(d => new ReferenceIndex(
                $"ix_{d.Collection}_player_ref",
                d.Collection,
                string.Join('.', d.PlayerField.Split('.').Select(s => $"`{s}`")) + ", META().id"))
            .ToArray();
    }

    /// <summary>Whether index keys are exactly <c>(playerField ASC, META().id ASC)</c>, ignoring backticks, case of META and blanks.</summary>
    private static bool IsReferenceShaped(string keys, string playerField)
    {
        var parts = keys.Split(',').Select(k => k.Replace("`", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)).ToArray();
        return parts.Length == 2
            && (parts[0] == playerField || parts[0] == playerField + "ASC")
            && parts[1].ToUpperInvariant() is "META().ID" or "META().IDASC";
    }

    /// <summary>
    /// Creates a scope or collection. "Already exists" means another instance created it. The cluster manager answers
    /// 503 "Service is temporarily unavailable" while it is busy with other manifest changes (seen with several scopes
    /// provisioned at once, 2026-10-30): retried with backoff like index statements.
    /// </summary>
    private async Task CreateMissingAsync(Func<Task> create, string what, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await create();
                logger.LogInformation("Created {What}.", what);
                return;
            }
            catch (Exception ex) when (ex is ScopeExistsException or CollectionExistsException)
            {
                return;
            }
            catch (CouchbaseException ex) when (attempt < IndexAttempts && ex.Context is ManagementErrorContext { HttpStatus: HttpStatusCode.ServiceUnavailable })
            {
                logger.LogWarning("Creating {What} failed: cluster manager temporarily unavailable (attempt {Attempt}); retrying.", what, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// A freshly created collection may not be visible to the query service yet ("keyspace not found"): retry with
    /// backoff (review minor).
    /// </summary>
    private async Task CreateIndexAsync(global::Couchbase.ICluster cluster, string statement, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cluster.QueryAsync<object>(statement);
                return;
            }
            catch (IndexExistsException)
            {
                return;
            }
            catch (Exception ex) when (attempt < IndexAttempts && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Index statement failed (attempt {Attempt}); retrying.", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => EnsureInitializedAsync(cancellationToken);

    /// <summary>
    /// Provisions once per process when <c>ProvisionOnStartup</c> (default: Development) is on. Startup services that
    /// read the store before traffic (key ring, staff bootstrap) await the same task.
    /// </summary>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!(connection.Options.ProvisionOnStartup ?? gameServer.IsDevelopment))
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            // A failed attempt is not cached: the next caller retries.
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = ProvisionAsync(cancellationToken);
            }

            return _initialization;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An index created by the provisioner for the account purge's reference queries.</summary>
public sealed record ReferenceIndex(string Name, string Collection, string Keys);
