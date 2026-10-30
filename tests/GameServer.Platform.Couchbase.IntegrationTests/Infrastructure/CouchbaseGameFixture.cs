namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// A Couchbase scope provisioned only by the full sample game hosts of the test class (<see cref="CouchbaseGameHost"/>): the
/// scope holds the production collections and indexes and nothing else, so query plans match a deployment (PERF-07).
/// </summary>
public sealed class CouchbaseGameFixture : CouchbaseFixture
{
    protected override bool ProvisionsTestModules => false;
}
