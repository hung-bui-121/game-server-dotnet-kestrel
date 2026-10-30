using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ScopeNotFoundException = Couchbase.Core.Exceptions.ScopeNotFoundException;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// T055 M2: the provisioning credential of contracts/operations-cli.md §2 is enough. A restricted user with only the documented
/// roles (Bucket Admin, Query Manage Index, Query System Catalog, Data Reader on the bucket) provisions a fresh scope with the
/// real binary (exit 0), and its re-run reads the catalog and reports every item present. Own scope <c>g_prov&lt;8 hex&gt;</c>
/// and user <c>gs-it-*</c>, both removed at the end.
/// </summary>
public sealed class CouchbaseProvisionerRolesTests
{
    [Fact]
    public async Task A_user_with_only_the_documented_provisioning_roles_provisions_a_fresh_scope()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var gameId = "prov" + Guid.NewGuid().ToString("N")[..8];
        await using var provisioner = await ProvisionerUser.CreateAsync(ProvisionerUser.DocumentedRoles(IntegrationCluster.Bucket));
        try
        {
            var settings = IntegrationCluster.HostSettings(gameId, provisioner.Name, provisioner.Password);

            var first = await SampleHostProcess.RunAsync("Beta", settings, ["--provision"]);
            first.ExitCode.ShouldBe(0, first.ToString());
            first.Output.ShouldNotContain(provisioner.Password);
            var items = first.ProvisionLines.Skip(1).SkipLast(1).ToArray();
            items.ShouldAllBe(l => l.Contains(" created", StringComparison.Ordinal));
            items.ShouldContain(l => l.StartsWith("index ", StringComparison.Ordinal));

            // The re-run takes the catalog path (system:indexes) for every index.
            var rerun = await SampleHostProcess.RunAsync("Beta", settings, ["--provision"]);
            rerun.ExitCode.ShouldBe(0, rerun.ToString());
            rerun.ProvisionLines[^1].ShouldBe($"done: 0 created, {items.Length} present");
        }
        finally
        {
            await DropScopeAsync(gameId);
        }
    }

    private static async Task DropScopeAsync(string gameId)
    {
        await using var admin = new CouchbaseConnection(IntegrationCluster.AdminOptions(), new GameServerOptions { GameId = gameId }, NullLoggerFactory.Instance);
        try
        {
            var bucket = await (await admin.GetClusterAsync()).BucketAsync(IntegrationCluster.Bucket);
            await bucket.Collections.DropScopeAsync(admin.ScopeName);
        }
        catch (ScopeNotFoundException)
        {
            // The run failed before it created the scope.
        }
    }
}
