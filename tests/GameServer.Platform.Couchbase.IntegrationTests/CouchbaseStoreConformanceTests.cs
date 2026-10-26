using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Persistence;
using GameServer.Platform.Tests.Persistence;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>The same behaviour the in-memory store is tested for, on real Couchbase transactions.</summary>
public sealed class CouchbaseStoreConformanceTests(CouchbaseFixture fixture) : StoreConformanceTests, IClassFixture<CouchbaseFixture>
{
    protected override ITransactionalDocumentStore Store
    {
        get
        {
            fixture.RequireCouchbase();
            return fixture.Store;
        }
    }
}
