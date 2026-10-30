using System.Diagnostics;
using Couchbase;
using Couchbase.Core.Exceptions;
using Couchbase.Management.Collections;
using Couchbase.Query;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Creates scope <c>g_&lt;gameId&gt;</c>, <c>txn_meta</c> first, the collections declared by descriptors and the framework, module
/// and purge indexes (DAT-07, research R10). Never drops or alters anything. Runs from the provisioning mode of the host
/// (<c>--provision</c>, <see cref="IStoreProvisioner"/>) and at startup when <c>Couchbase:ProvisionOnStartup</c> resolves to true
/// (Development by default); otherwise startup only verifies that every collection exists (research R11).
/// </summary>
public sealed class CouchbaseProvisioner(
    CouchbaseConnection connection,
    ModuleRegistry registry,
    GameServerOptions gameServer,
    ILogger<CouchbaseProvisioner> logger) : IHostedService, IStoreInitializer, IStoreProvisioner
{
    private readonly Lock _gate = new();
    private Task? _initialization;

    public const string PendingOutboxIndex = "ix_receipts_outbox_pending";

    /// <summary>Receipts of a player, newest first: GM history and the account purge (research R5).</summary>
    public const string ReceiptsPlayerIndex = "ix_receipts_player";

    /// <summary>Unfinished purges due for an attempt (research R4).</summary>
    public const string AccountDeletionsDueIndex = "ix_account_deletions_due";

    private const string OnlineState = "online";

    private const string DeferredState = "deferred";

    private const string ErrorState = "error";

    private static readonly TimeSpan IndexPollInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan VisibilityPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary><c>txn_meta</c> first (DAT-07: transactions need it before any other write), then the others in ordinal order.</summary>
    public IReadOnlyList<string> RequiredCollections() => Schema.Collections();

    /// <inheritdoc cref="CouchbaseSchema.ReferenceIndexes"/>
    public IReadOnlyList<ReferenceIndex> ReferenceIndexes() => Schema.ReferenceIndexes();

    /// <summary>Framework, module and purge reference indexes, in creation order.</summary>
    public IReadOnlyList<IndexDefinition> IndexDefinitions() => Schema.Indexes();

    private CouchbaseSchema Schema => new(registry, connection.BucketName, connection.ScopeName);

    /// <summary>Provisions with progress written to the log (startup path, test fixtures).</summary>
    public Task ProvisionAsync(CancellationToken cancellationToken) => ProvisionAsync(new LoggingProgress(logger), cancellationToken);

    /// <inheritdoc />
    public async Task ProvisionAsync(IProgress<ProvisioningItem> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var scopeName = connection.ScopeName;
        var bucketName = connection.BucketName;
        var (cluster, bucket) = await StepAsync("bucket", bucketName, async () =>
        {
            var c = await connection.GetClusterAsync().WaitAsync(cancellationToken);
            return (c, await c.BucketAsync(bucketName).AsTask().WaitAsync(cancellationToken));
        }, cancellationToken);
        var manager = bucket.Collections;

        // Create only what is missing: the SDK logs every "already exists" at Error level (false alarms on each restart).
        var scope = (await StepAsync(
                "scope",
                scopeName,
                () => CouchbaseManagementRetry.RunAsync(
                    () => manager.GetAllScopesAsync(new GetAllScopesOptions().CancellationToken(cancellationToken)),
                    logger,
                    cancellationToken),
                cancellationToken))
            .FirstOrDefault(s => s.Name == scopeName);
        var existing = scope?.Collections.Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        await ReportAsync(progress, "scope", scopeName, scope is not null, () => CreateMissingAsync(
            () => manager.CreateScopeAsync(scopeName, new CreateScopeOptions().CancellationToken(cancellationToken)),
            cancellationToken), cancellationToken);

        foreach (var collection in RequiredCollections())
        {
            await ReportAsync(progress, "collection", $"{scopeName}.{collection}", existing.Contains(collection), () => CreateMissingAsync(
                () => manager.CreateCollectionAsync(
                    scopeName,
                    collection,
                    CreateCollectionSettings.Default,
                    new CreateCollectionOptions().CancellationToken(cancellationToken)),
                cancellationToken), cancellationToken);
        }

        // A new collection reaches the data service through the manifest a moment later (CREATE INDEX retries "keyspace not
        // found" for the query service).
        await Task.WhenAll(RequiredCollections().Select(c => StepAsync(
            "collection",
            $"{scopeName}.{c}",
            () => WaitUntilVisibleAsync(c, cancellationToken),
            cancellationToken)));

        var catalog = await StepAsync("index", $"catalog of {scopeName}", () => ReadIndexStatesAsync(cluster, cancellationToken), cancellationToken);
        foreach (var index in IndexDefinitions())
        {
            var name = $"{index.Name} on {index.Collection}";
            if (catalog.TryGetValue((index.Collection, index.Name), out var state))
            {
                // Existing indexes are never altered; one still building (an interrupted run) is waited for.
                await StepAsync("index", name, () => WaitUntilOnlineAsync(cluster, index, state, cancellationToken), cancellationToken);
                progress.Report(new ProvisioningItem("index", name, ProvisioningOutcome.AlreadyPresent, TimeSpan.Zero));
                continue;
            }

            var watch = Stopwatch.StartNew();
            await StepAsync("index", name, async () =>
            {
                await CreateIndexAsync(cluster, index.Statement, cancellationToken);
                await WaitUntilOnlineAsync(cluster, index, state: null, cancellationToken);
            }, cancellationToken);
            progress.Report(new ProvisioningItem("index", name, ProvisioningOutcome.Created, watch.Elapsed));
        }

        var missing = await StepAsync("scope", scopeName, () => FindMissingCollectionsAsync(cancellationToken), cancellationToken);
        if (missing.Count > 0)
        {
            throw new ProvisioningFailedException("scope", scopeName, "verification found missing collections " + string.Join(", ", missing));
        }
    }

    /// <summary>
    /// Startup check when provisioning at startup is off (FR-032, D2): every required collection answers a key-value probe
    /// with the runtime credential, otherwise the host stops before it listens, naming the collections and the fix.
    /// Indexes are not checked: listing them needs catalog rights the data-only identity does not have.
    /// </summary>
    public async Task VerifySchemaAsync(CancellationToken cancellationToken)
    {
        var missing = await FindMissingCollectionsAsync(cancellationToken);
        if (missing.Count > 0)
        {
            throw new GameServerStartupException(
                $"[couchbase] scope {connection.ScopeName} in bucket {connection.BucketName} is missing collections {string.Join(", ", missing)}. " +
                "Fix: run the provisioning command (<host> --provision) with an administrative credential (DAT-07).");
        }
    }

    private static async Task ReportAsync(
        IProgress<ProvisioningItem> progress,
        string kind,
        string name,
        bool present,
        Func<Task<bool>> create,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var created = !present && await StepAsync(kind, name, create, cancellationToken);
        progress.Report(new ProvisioningItem(kind, name, created ? ProvisioningOutcome.Created : ProvisioningOutcome.AlreadyPresent, watch.Elapsed));
    }

    /// <summary>
    /// Creates a scope or collection through <see cref="CouchbaseManagementRetry"/> (503 retried); false when another instance
    /// created it first ("already exists").
    /// </summary>
    private async Task<bool> CreateMissingAsync(Func<Task> create, CancellationToken cancellationToken)
    {
        try
        {
            return await CouchbaseManagementRetry.RunAsync(
                async () =>
                {
                    await create();
                    return true;
                },
                logger,
                cancellationToken);
        }
        catch (Exception ex) when (ex is ScopeExistsException or CollectionExistsException)
        {
            return false;
        }
    }

    /// <summary>Bounded by <c>Couchbase:ProvisioningTimeoutSeconds</c> also on the startup path, which has no outer timeout.</summary>
    private async Task WaitUntilVisibleAsync(string collection, CancellationToken cancellationToken)
    {
        var handle = await connection.GetCollectionAsync(collection);
        var limit = TimeSpan.FromSeconds(connection.Options.ProvisioningTimeoutSeconds);
        var watch = Stopwatch.StartNew();
        while (!await CouchbaseCollectionProbe.ExistsAsync(handle, cancellationToken))
        {
            if (watch.Elapsed > limit)
            {
                throw new System.TimeoutException($"the collection does not answer key-value requests after {(int)limit.TotalSeconds} s");
            }

            await Task.Delay(VisibilityPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// A freshly created collection may not be visible to the query service yet ("keyspace not found"): retry with
    /// backoff (review minor). <c>CREATE INDEX</c> returns once the index is built (verified on EE 7.6.12, T039), so it is
    /// bounded by <c>Couchbase:ProvisioningTimeoutSeconds</c>, not by the query timeout of GM pages (<c>QueryTimeoutMs</c>).
    /// </summary>
    private async Task CreateIndexAsync(ICluster cluster, string statement, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await cluster.QueryAsync<object>(statement, new QueryOptions()
                    .Timeout(TimeSpan.FromSeconds(connection.Options.ProvisioningTimeoutSeconds))
                    .CancellationToken(cancellationToken));
                return;
            }
            catch (IndexExistsException)
            {
                return;
            }
            catch (Exception ex) when (attempt < CouchbaseManagementRetry.Attempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Index statement failed (attempt {Attempt}); retrying.", attempt);
                await Task.Delay(CouchbaseManagementRetry.Backoff(attempt, CouchbaseManagementRetry.InitialBackoff), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Polls the catalog every 2 s until the index is online (D14: state, not a percentage). A failed build (state <c>error</c>)
    /// fails at once; any other state (pending, building) is waited for, bounded by <c>Couchbase:ProvisioningTimeoutSeconds</c>
    /// like <see cref="WaitUntilVisibleAsync"/> (the startup path has no outer timeout).
    /// </summary>
    private async Task WaitUntilOnlineAsync(ICluster cluster, IndexDefinition index, string? state, CancellationToken cancellationToken)
    {
        var name = $"{index.Name} on {index.Collection}";
        var limit = TimeSpan.FromSeconds(connection.Options.ProvisioningTimeoutSeconds);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            state ??= (await ReadIndexStatesAsync(cluster, cancellationToken)).GetValueOrDefault((index.Collection, index.Name));
            switch (state)
            {
                case OnlineState:
                    return;
                case DeferredState:
                    throw new ProvisioningFailedException(
                        "index",
                        name,
                        "exists with deferred build; the provisioner never alters an existing index: run BUILD INDEX for it, then run again");
                case ErrorState:
                    throw new ProvisioningFailedException(
                        "index",
                        name,
                        "the index service reports state 'error' (build failed); the provisioner never alters an existing index: " +
                        "drop it (DROP INDEX), then run again");
                case null:
                    // Measured (EE 7.6.12, T055): without Query System Catalog the catalog hides the index just created.
                    throw new ProvisioningFailedException(
                        "index",
                        name,
                        "not in system:indexes after CREATE INDEX (the provisioning identity needs the Query System Catalog role to read it)");
            }

            if (watch.Elapsed > limit)
            {
                throw new System.TimeoutException($"the index is still '{state}' after {(int)limit.TotalSeconds} s (Couchbase:ProvisioningTimeoutSeconds)");
            }

            await Task.Delay(IndexPollInterval, cancellationToken);
            state = null;
        }
    }

    private async Task<Dictionary<(string Collection, string Name), string>> ReadIndexStatesAsync(ICluster cluster, CancellationToken cancellationToken)
    {
        var result = await cluster.QueryAsync<IndexRow>(
            "SELECT keyspace_id AS `collection`, name, state FROM system:indexes WHERE bucket_id = $bucket AND scope_id = $scope",
            new QueryOptions()
                .Parameter("bucket", connection.BucketName)
                .Parameter("scope", connection.ScopeName)
                .Readonly(true) // idempotent: the SDK retries it on transient query-service errors
                .CancellationToken(cancellationToken));
        var states = new Dictionary<(string, string), string>();
        await foreach (var row in result.Rows.WithCancellation(cancellationToken))
        {
            states[(row.Collection, row.Name)] = row.State;
        }

        return states;
    }

    private async Task<IReadOnlyList<string>> FindMissingCollectionsAsync(CancellationToken cancellationToken)
    {
        var collections = RequiredCollections();
        var present = await Task.WhenAll(collections.Select(async c =>
            await CouchbaseCollectionProbe.ExistsAsync(await connection.GetCollectionAsync(c), cancellationToken)));
        return collections.Where((_, i) => !present[i]).ToArray();
    }

    /// <summary>Runs one step; any failure but cancellation becomes a <see cref="ProvisioningFailedException"/> naming the item.</summary>
    private static async Task<T> StepAsync<T>(string kind, string name, Func<Task<T>> step, CancellationToken cancellationToken)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not ProvisioningFailedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProvisioningFailedException(kind, name, Cause(ex), ex);
        }
    }

    private static async Task StepAsync(string kind, string name, Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not ProvisioningFailedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ProvisioningFailedException(kind, name, Cause(ex), ex);
        }
    }

    /// <summary>One line: exception type, first message line and the HTTP status of a management error. SDK messages carry no credentials.</summary>
    private static string Cause(Exception exception)
    {
        var message = exception.Message.Split('\n', 2)[0].Trim();
        var status = exception is CouchbaseException { Context: ManagementErrorContext management } ? $" (HTTP {(int)management.HttpStatus})" : string.Empty;
        return $"{exception.GetType().Name}{status}: {message}";
    }

    public Task StartAsync(CancellationToken cancellationToken) => EnsureInitializedAsync(cancellationToken);

    /// <summary>
    /// Once per process: provisions when <c>ProvisionOnStartup</c> resolves to true (default: Development), otherwise verifies
    /// that every collection exists. Startup services that read the store before traffic (key ring, staff bootstrap) await
    /// the same task, so a missing collection stops the host before Kestrel listens (exit 1).
    /// </summary>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        var provision = connection.Options.ProvisionOnStartup ?? gameServer.IsDevelopment;
        lock (_gate)
        {
            // A failed attempt is not cached: the next caller retries.
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = provision ? ProvisionAsync(cancellationToken) : VerifySchemaAsync(cancellationToken);
            }

            return _initialization;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed record IndexRow(string Collection, string Name, string State);

    /// <summary>Startup provisioning: created items at Information, present ones at Debug (every Development start).</summary>
    private sealed class LoggingProgress(ILogger logger) : IProgress<ProvisioningItem>
    {
        public void Report(ProvisioningItem value)
        {
            if (value.Outcome == ProvisioningOutcome.Created)
            {
                logger.LogInformation("Provisioned {Kind} {Name} in {ElapsedMs} ms.", value.Kind, value.Name, (long)value.Elapsed.TotalMilliseconds);
            }
            else
            {
                logger.LogDebug("{Kind} {Name} already present.", value.Kind, value.Name);
            }
        }
    }
}
