using System.Security.Cryptography.X509Certificates;
using Couchbase;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.Compression.Snappier;
using Couchbase.Core.IO.Serializers;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase;

/// <summary>
/// Maps <see cref="CouchbaseOptions"/> 1:1 to the SDK's <see cref="ClusterOptions"/> (FR-011, research R5), with the
/// transaction configuration every instance must share (expiration, durability Majority, metadata collection
/// <c>g_&lt;gameId&gt;.txn_meta</c> — DAT-07, OPS-05). Kept apart from the connection so the mapping is testable without a cluster.
/// </summary>
internal static class CouchbaseClusterOptionsFactory
{
    public static ClusterOptions Create(
        CouchbaseOptions options,
        GameServerOptions gameServer,
        ITypeSerializer serializer,
        X509Certificate2Collection? trustedRoots,
        ILoggerFactory loggerFactory)
    {
        var cluster = new ClusterOptions
        {
            ConnectionString = options.ConnectionString,
            KvTimeout = TimeSpan.FromMilliseconds(options.KvTimeoutMs),
            QueryTimeout = TimeSpan.FromMilliseconds(options.QueryTimeoutMs),

            // Durable writes keep their own timeout (GameServer:Transactions:KvDurableTimeoutSeconds), unchanged by this mapping.
            KvDurabilityTimeout = TimeSpan.FromSeconds(gameServer.Transactions.KvDurableTimeoutSeconds),
            Compression = options.Compression,
            CompressionMinSize = options.CompressionMinSizeBytes,

            // Maximum first: the pool never sees a minimum above its maximum while both are assigned.
            MaxKvConnections = options.MaxKvConnections,
            NumKvConnections = options.NumKvConnections,
        };

        if (options.Compression)
        {
            // The SDK's default algorithm compresses nothing: Compression=true alone has no effect ([VERIFY] T033).
            cluster.WithSnappyCompression();
        }

        if (options.UsesTls && trustedRoots is { Count: > 0 })
        {
            var validate = CouchbaseTrustedRoots.Validator(trustedRoots);
            cluster.TlsSettings.KvCertificateValidationCallback = validate;
            cluster.TlsSettings.HttpCertificateValidationCallback = validate;
        }

        cluster.WithPasswordAuthentication(options.Username, options.Password);
        cluster.WithSerializer(serializer);
        cluster.WithLogging(loggerFactory);
        cluster.TransactionsConfig = TransactionsConfigBuilder.Create()
            .ExpirationTime(TimeSpan.FromSeconds(gameServer.Transactions.ExpirationSeconds))
            .DurabilityLevel(DurabilityLevel.Majority)
            .MetadataCollection(new Keyspace(options.BucketName, CouchbaseOptions.ScopeName(gameServer.GameId), CouchbaseConnection.TransactionMetadataCollection))
            .LoggerFactory(loggerFactory)
            .Build();
        return cluster;
    }
}
