using Couchbase;
using Couchbase.KeyValue;
using GameServer.Modules;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Platform.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// Connects to a real Couchbase (transactions enabled) when <c>COUCHBASE_CONNSTR</c> is set; otherwise every test
/// is reported Skipped with the reason (constitution VI — never a fake pass). Each run uses its own scope.
/// </summary>
public sealed class CouchbaseFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "COUCHBASE_CONNSTR";

    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = $"{ConnectionStringVariable} not set: integration tests need a real Couchbase (≥ 7.6, bucket replicas = 0 on a single node).";

    public CouchbaseConnection Connection { get; private set; } = null!;

    public ITransactionalDocumentStore Store { get; private set; } = null!;

    public ModuleRegistry Registry { get; private set; } = null!;

    public GameServerOptions GameServer { get; private set; } = null!;

    public CouchbaseOptions Options { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        Options = new CouchbaseOptions
        {
            ConnectionString = connectionString,
            Username = Environment.GetEnvironmentVariable("COUCHBASE_USERNAME") ?? "Administrator",
            Password = Environment.GetEnvironmentVariable("COUCHBASE_PASSWORD") ?? "password",
            BucketName = Environment.GetEnvironmentVariable("COUCHBASE_BUCKET") ?? "gameserver-it",
            ProvisionOnStartup = true,
        };
        GameServer = new GameServerOptions
        {
            GameId = "it" + Guid.NewGuid().ToString("N")[..8],
            EnvironmentId = "it",
        };

        Registry = new ModuleRegistry();
        Registry.Add(TestModules.Identity(), serviceOnly: false);
        Registry.Add(TestModules.Counter(), serviceOnly: false);
        Registry.Add(TestModules.Probe(), serviceOnly: false);
        Registry.Add(new DescriptorModule(new ModuleDescriptor
        {
            Name = "conformance",
            Collections =
            [
                new CollectionSpec("conf_docs", [new IndexSpec("ix_conf_docs_group_at", "`group`, `at`")]),
                new CollectionSpec("tst04"),
            ],
        }), serviceOnly: false);

        Connection = new CouchbaseConnection(Options, GameServer, NullLoggerFactory.Instance);
        await new CouchbaseProvisioner(Connection, Registry, GameServer, NullLogger<CouchbaseProvisioner>.Instance)
            .ProvisionAsync(CancellationToken.None);
        Store = new CouchbaseDocumentStore(Connection);
        Available = true;
    }

    /// <summary>Skips the calling test when no Couchbase is configured.</summary>
    public void RequireCouchbase()
    {
        if (!Available)
        {
            Assert.Skip(SkipReason);
        }
    }

    public Task<ICouchbaseCollection> CollectionAsync(string name) => Connection.GetCollectionAsync(name);

    public Task<ICluster> ClusterAsync() => Connection.GetClusterAsync();

    public async ValueTask DisposeAsync()
    {
        if (!Available)
        {
            return;
        }

        try
        {
            var cluster = await Connection.GetClusterAsync();
            var bucket = await cluster.BucketAsync(Options.BucketName);
            await bucket.Collections.DropScopeAsync(Connection.ScopeName);
        }
        finally
        {
            await Connection.DisposeAsync();
        }
    }
}
