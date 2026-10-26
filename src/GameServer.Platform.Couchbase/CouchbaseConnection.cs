using System.Collections.Concurrent;
using Couchbase;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.Core.IO.Serializers;
using Couchbase.Core.IO.Transcoders;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Process-wide cluster connection (PERF-03): one <see cref="ICluster"/> with transactions configured for
/// the framework (expiration 10 s, durability Majority, metadata collection <c>g_&lt;gameId&gt;.txn_meta</c> — DAT-07, OPS-05).
/// A failed connection attempt is not cached: the next caller retries (review M5).
/// </summary>
public sealed class CouchbaseConnection : IAsyncDisposable
{
    public const string TransactionMetadataCollection = "txn_meta";

    private readonly CouchbaseOptions _options;
    private readonly GameServerOptions _gameServer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ICouchbaseCollection> _collections = new(StringComparer.Ordinal);
    private ICluster? _cluster;

    public CouchbaseConnection(CouchbaseOptions options, GameServerOptions gameServer, ILoggerFactory loggerFactory)
    {
        _options = options;
        _gameServer = gameServer;
        _loggerFactory = loggerFactory;
        Serializer = SystemTextJsonSerializer.Create(GameServerJson.Options);
        Transcoder = new JsonTranscoder(Serializer);
    }

    public string BucketName => _options.BucketName;

    public string ScopeName => CouchbaseOptions.ScopeName(_gameServer.GameId);

    public CouchbaseOptions Options => _options;

    /// <summary>Serializer shared by KV, query and transactions (System.Text.Json, same options as the platform).</summary>
    public ITypeSerializer Serializer { get; }

    public ITypeTranscoder Transcoder { get; }

    public async Task<ICluster> GetClusterAsync()
    {
        if (_cluster is { } connected)
        {
            return connected;
        }

        await _connectLock.WaitAsync();
        try
        {
            return _cluster ??= await ConnectAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<IScope> GetScopeAsync()
    {
        var cluster = await GetClusterAsync();
        var bucket = await cluster.BucketAsync(_options.BucketName);
        return bucket.Scope(ScopeName);
    }

    public async Task<ICouchbaseCollection> GetCollectionAsync(string collection)
    {
        if (_collections.TryGetValue(collection, out var cached))
        {
            return cached;
        }

        var resolved = (await GetScopeAsync()).Collection(collection);
        return _collections.GetOrAdd(collection, resolved);
    }

    /// <summary>Creates the cluster with the transaction configuration every instance must share.</summary>
    private async Task<ICluster> ConnectAsync()
    {
        var clusterOptions = new ClusterOptions
        {
            ConnectionString = _options.ConnectionString,
            KvDurabilityTimeout = TimeSpan.FromSeconds(_gameServer.Transactions.KvDurableTimeoutSeconds),
        };
        clusterOptions.WithPasswordAuthentication(_options.Username, _options.Password);
        clusterOptions.WithSerializer(Serializer);
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
        if (_cluster is { } cluster)
        {
            await cluster.DisposeAsync();
        }

        _connectLock.Dispose();
    }
}
