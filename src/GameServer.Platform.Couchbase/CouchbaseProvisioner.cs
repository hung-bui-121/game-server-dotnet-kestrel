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

    /// <summary>Collections the framework itself needs.</summary>
    public static readonly string[] FrameworkCollections = [ReceiptDocument.Collection, CouchbaseConnection.TransactionMetadataCollection];

    public IReadOnlyList<string> RequiredCollections() =>
        FrameworkCollections
            .Concat(registry.Aggregates.Values.Select(a => a.Collection))
            .Concat(registry.Modules.SelectMany(m => m.Descriptor.Collections).Select(c => c.Name))
            .Concat(registry.OutboxProjections.Select(p => p.Collection))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var cluster = await connection.GetClusterAsync();
        var bucket = await cluster.BucketAsync(connection.BucketName);
        var manager = bucket.Collections;

        try
        {
            await manager.CreateScopeAsync(connection.ScopeName, new CreateScopeOptions().CancellationToken(cancellationToken));
            logger.LogInformation("Created scope {Scope}.", connection.ScopeName);
        }
        catch (ScopeExistsException)
        {
        }

        foreach (var collection in RequiredCollections())
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
            try
            {
                await cluster.QueryAsync<object>(statement);
            }
            catch (IndexExistsException)
            {
            }
        }

        logger.LogInformation("Provisioned {Count} collections for game {GameId}.", RequiredCollections().Count, gameServer.GameId);
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
