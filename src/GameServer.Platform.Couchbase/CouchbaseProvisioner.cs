using Couchbase.Core.Exceptions;
using Couchbase.Management.Collections;
using GameServer.Platform.Commands;
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
    ILogger<CouchbaseProvisioner> logger) : IHostedService
{
    public const string PendingOutboxIndex = "ix_receipts_outbox_pending";

    private const int IndexAttempts = 6;

    public IReadOnlyList<string> RequiredCollections() =>
        registry.RequiredCollections().Append(CouchbaseConnection.TransactionMetadataCollection).Distinct(StringComparer.Ordinal).ToArray();

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var cluster = await connection.GetClusterAsync();
        var bucket = await cluster.BucketAsync(connection.BucketName);
        var manager = bucket.Collections;

        // Create only what is missing: the SDK logs every "already exists" at Error level (false alarms on each restart).
        // The catches remain for instances provisioning at the same time.
        var scope = (await manager.GetAllScopesAsync(new GetAllScopesOptions().CancellationToken(cancellationToken)))
            .FirstOrDefault(s => s.Name == connection.ScopeName);
        var existing = scope?.Collections.Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        try
        {
            if (scope is null)
            {
                await manager.CreateScopeAsync(connection.ScopeName, new CreateScopeOptions().CancellationToken(cancellationToken));
                logger.LogInformation("Created scope {Scope}.", connection.ScopeName);
            }
        }
        catch (ScopeExistsException)
        {
        }

        foreach (var collection in RequiredCollections().Where(c => !existing.Contains(c)))
        {
            try
            {
                await manager.CreateCollectionAsync(
                    connection.ScopeName,
                    collection,
                    CreateCollectionSettings.Default,
                    new CreateCollectionOptions().CancellationToken(cancellationToken));
                logger.LogInformation("Created collection {Scope}.{Collection}.", connection.ScopeName, collection);
            }
            catch (CollectionExistsException)
            {
            }
        }

        var keyspace = $"`{connection.BucketName}`.`{connection.ScopeName}`";
        var statements = new List<string>
        {
            $"CREATE INDEX `{PendingOutboxIndex}` IF NOT EXISTS ON {keyspace}.`receipts`(committedAtUtc) WHERE outboxPending = true",
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

        foreach (var statement in statements)
        {
            await CreateIndexAsync(cluster, statement, cancellationToken);
        }

        logger.LogInformation("Provisioned {Count} collections for game {GameId}.", RequiredCollections().Count, gameServer.GameId);
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (connection.Options.ProvisionOnStartup ?? gameServer.IsDevelopment)
        {
            await ProvisionAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
