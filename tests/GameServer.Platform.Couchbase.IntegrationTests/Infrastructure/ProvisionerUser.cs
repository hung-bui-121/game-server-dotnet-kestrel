using System.Diagnostics;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Management.Collections;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// A throw-away local RBAC user with the provisioning roles of contracts/operations-cli.md §2 on the whole bucket (the scope does
/// not exist before the first run): Bucket Admin, Query Manage Index, Query System Catalog and Data Reader. Disposing deletes it.
/// </summary>
internal sealed class ProvisionerUser : IAsyncDisposable
{
    private static readonly TimeSpan PropagationLimit = TimeSpan.FromSeconds(30);

    private ProvisionerUser(string name, string password)
    {
        Name = name;
        Password = password;
    }

    public string Name { get; }

    public string Password { get; }

    /// <summary>The documented minimal set, in 7.6 role syntax.</summary>
    public static IReadOnlyList<string> DocumentedRoles(string bucket) =>
        [$"bucket_admin[{bucket}]", $"query_manage_index[{bucket}]", "query_system_catalog", $"data_reader[{bucket}]"];

    /// <summary>Creates the user, then waits until the cluster manager and key-value accept it (roles propagate asynchronously).</summary>
    public static async Task<ProvisionerUser> CreateAsync(IReadOnlyList<string> roles)
    {
        var user = new ProvisionerUser(RbacRest.NewUserName(), StagingSecrets.Random());
        await RbacRest.CreateUserAsync(user.Name, user.Password, roles);
        try
        {
            await user.WaitUntilAcceptedAsync();
            return user;
        }
        catch
        {
            await user.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(RbacRest.DeleteUserAsync(Name));

    /// <summary>Lists the bucket's scopes (Bucket Admin) and reads a missing key of its default collection (Data Reader).</summary>
    private async Task WaitUntilAcceptedAsync()
    {
        var options = IntegrationCluster.AdminOptions();
        options.Username = Name;
        options.Password = Password;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await using var connection = new CouchbaseConnection(options, new GameServerOptions { GameId = "probe" }, NullLoggerFactory.Instance);
            try
            {
                var cluster = await connection.GetClusterAsync();
                var bucket = await cluster.BucketAsync(IntegrationCluster.Bucket);
                await bucket.Collections.GetAllScopesAsync(new GetAllScopesOptions());
                await (await bucket.DefaultCollectionAsync()).GetAsync(CouchbaseCollectionProbe.ProbeKey);
                return;
            }
            catch (DocumentNotFoundException)
            {
                return;
            }
            catch (Exception) when (watch.Elapsed < PropagationLimit)
            {
                await Task.Delay(500);
            }
        }
    }
}
