using System.Collections.Concurrent;
using Couchbase;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.Core.IO.Serializers;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Process-wide cluster connection (PERF-03): one <see cref="ICluster"/> with transactions configured for
/// the framework (expiration 10 s, durability Majority, metadata collection <c>g_&lt;gameId&gt;.txn_meta</c> — DAT-07, OPS-05).
/// </summary>
public sealed class CouchbaseConnection : IAsyncDisposable
{
    public const string TransactionMetadataCollection = "txn_meta";

    private readonly CouchbaseOptions _options;
    private readonly GameServerOptions _gameServer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<Task<ICluster>> _cluster;
    private readonly ConcurrentDictionary<string, Task<ICouchbaseCollection>> _collections = new(StringComparer.Ordinal);

    public CouchbaseConnection(CouchbaseOptions options, GameServerOptions gameServer, ILoggerFactory loggerFactory)
    {
        _options = options;
        _gameServer = gameServer;
        _loggerFactory = loggerFactory;
        _cluster = new Lazy<Task<ICluster>>(ConnectAsync);
    }

    public string BucketName => _options.BucketName;

    public string ScopeName => CouchbaseOptions.ScopeName(_gameServer.GameId);

    public CouchbaseOptions Options => _options;

    public Task<ICluster> GetClusterAsync() => _cluster.Value;

    public async Task<IScope> GetScopeAsync()
    {
        var cluster = await GetClusterAsync();
        var bucket = await cluster.BucketAsync(_options.BucketName);
        return bucket.Scope(ScopeName);
    }

    public Task<ICouchbaseCollection> GetCollectionAsync(string collection) =>
        _collections.GetOrAdd(collection, async name => (await GetScopeAsync()).Collection(name));

    /// <summary>Creates the cluster with the transaction configuration every instance must share.</summary>
    private async Task<ICluster> ConnectAsync()
    {
        var clusterOptions = new ClusterOptions
        {
            ConnectionString = _options.ConnectionString,
            KvDurabilityTimeout = TimeSpan.FromSeconds(_gameServer.Transactions.KvDurableTimeoutSeconds),
        };
        clusterOptions.WithPasswordAuthentication(_options.Username, _options.Password);
        clusterOptions.WithSerializer(SystemTextJsonSerializer.Create(GameServerJson.Options));
        clusterOptions.WithLogging(_loggerFactory);
        clusterOptions.TransactionsConfig = TransactionsConfigBuilder.Create()
            .ExpirationTime(TimeSpan.FromSeconds(_gameServer.Transactions.ExpirationSeconds))
            .DurabilityLevel(DurabilityLevel.Majority)
            .MetadataCollection(new Keyspace(_options.BucketName, ScopeName, TransactionMetadataCollection))
            .LoggerFactory(_loggerFactory)
            .Build();

        return await Cluster.ConnectAsync(clusterOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cluster.IsValueCreated)
        {
            var cluster = await _cluster.Value;
            await cluster.DisposeAsync();
        }
    }
}
