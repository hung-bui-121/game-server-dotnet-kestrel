using System.Diagnostics;
using Couchbase.Core.Exceptions.KeyValue;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// A throw-away local RBAC user with the API deployment's data-only roles on one scope (contracts §2, FR-031): Data Reader,
/// Data Writer and Query Select/Insert/Update/Delete. Created through <see cref="RbacRest"/>; the random password is never
/// printed. Disposing deletes the user.
/// </summary>
internal sealed class DataOnlyUser : IAsyncDisposable
{
    private static readonly string[] Roles = ["data_reader", "data_writer", "query_select", "query_insert", "query_update", "query_delete"];

    private static readonly TimeSpan PropagationLimit = TimeSpan.FromSeconds(30);

    private DataOnlyUser(string name, string password)
    {
        Name = name;
        Password = password;
    }

    public string Name { get; }

    public string Password { get; }

    /// <summary>Creates the user on the scope, then waits until key-value accepts it (role changes propagate asynchronously).</summary>
    public static async Task<DataOnlyUser> CreateAsync(string gameId)
    {
        var user = new DataOnlyUser(RbacRest.NewUserName(), StagingSecrets.Random());
        var scope = CouchbaseOptions.ScopeName(gameId);
        await RbacRest.CreateUserAsync(user.Name, user.Password, Roles.Select(r => $"{r}[{IntegrationCluster.Bucket}:{scope}]"));
        await user.WaitUntilAcceptedAsync(gameId);
        return user;
    }

    public ValueTask DisposeAsync() => new(RbacRest.DeleteUserAsync(Name));

    private async Task WaitUntilAcceptedAsync(string gameId)
    {
        var options = IntegrationCluster.AdminOptions();
        options.Username = Name;
        options.Password = Password;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await using var connection = new CouchbaseConnection(options, new GameServerOptions { GameId = gameId }, NullLoggerFactory.Instance);
            try
            {
                var collection = await connection.GetCollectionAsync(CouchbaseConnection.TransactionMetadataCollection);
                await collection.GetAsync(CouchbaseCollectionProbe.ProbeKey);
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
