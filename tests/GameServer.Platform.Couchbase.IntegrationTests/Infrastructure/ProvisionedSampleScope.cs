using Microsoft.Extensions.Logging.Abstractions;
using ScopeNotFoundException = Couchbase.Core.Exceptions.ScopeNotFoundException;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// A throw-away scope <c>g_prov&lt;8 hex&gt;</c> provisioned once by the real sample host binary (<c>--provision</c>, Beta flavor,
/// admin credential), shared by the provisioning and flavor readiness tests (T042, T043). <see cref="FirstRun"/> is the run on
/// the empty scope. Tests that change the scope restore it. The scope and the data-only user are removed at the end, also
/// after a failure.
/// </summary>
public sealed class ProvisionedSampleScope : IAsyncLifetime
{
    private CouchbaseConnection? _admin;
    private DataOnlyUser? _dataOnly;

    public string GameId { get; } = "prov" + Guid.NewGuid().ToString("N")[..8];

    public string ScopeName => CouchbaseOptions.ScopeName(GameId);

    /// <summary>The first provisioning run, on the empty scope.</summary>
    internal HostRun FirstRun { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!IntegrationCluster.Available)
        {
            return;
        }

        _admin = new CouchbaseConnection(IntegrationCluster.AdminOptions(), new GameServerOptions { GameId = GameId }, NullLoggerFactory.Instance);
        FirstRun = await ProvisionAsync(IntegrationCluster.AdminUser, IntegrationCluster.AdminPassword);
    }

    /// <summary>Skips the calling test when no Couchbase is configured.</summary>
    public static void RequireCouchbase() => Assert.SkipUnless(IntegrationCluster.Available, IntegrationCluster.SkipReason);

    /// <summary>Settings of a sample host on this scope.</summary>
    internal Dictionary<string, string?> HostSettings(string username, string password) => IntegrationCluster.HostSettings(GameId, username, password);

    /// <summary><c>SampleHost --provision</c> on this scope (Beta flavor by default: no SSO, IAP, pepper or master key needed).</summary>
    internal Task<HostRun> ProvisionAsync(string username, string password, string flavor = "Beta") =>
        SampleHostProcess.RunAsync(flavor, HostSettings(username, password), ["--provision"]);

    internal Task<HostRun> ProvisionAsAdminAsync() => ProvisionAsync(IntegrationCluster.AdminUser, IntegrationCluster.AdminPassword);

    /// <summary>One data-only user for the whole run (creating users is slow to propagate).</summary>
    internal async Task<DataOnlyUser> DataOnlyUserAsync() => _dataOnly ??= await DataOnlyUser.CreateAsync(GameId);

    public async Task DropCollectionAsync(string collection)
    {
        var cluster = await _admin!.GetClusterAsync();
        var bucket = await cluster.BucketAsync(IntegrationCluster.Bucket);
        await bucket.Collections.DropCollectionAsync(ScopeName, collection);
    }

    public async Task<bool> CollectionExistsAsync(string collection) =>
        await CouchbaseCollectionProbe.ExistsAsync(await _admin!.GetCollectionAsync(collection), CancellationToken.None);

    /// <summary>Index name → state of every index in the scope (<c>system:indexes</c>, admin credential).</summary>
    public async Task<Dictionary<string, string>> IndexStatesAsync()
    {
        var cluster = await _admin!.GetClusterAsync();
        var result = await cluster.QueryAsync<IndexState>(
            "SELECT name, state FROM system:indexes WHERE bucket_id = $bucket AND scope_id = $scope",
            new global::Couchbase.Query.QueryOptions().Parameter("bucket", IntegrationCluster.Bucket).Parameter("scope", ScopeName));
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var row in result.Rows)
        {
            states[row.Name] = row.State;
        }

        return states;
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        try
        {
            if (_dataOnly is not null)
            {
                await _dataOnly.DisposeAsync();
            }

            var cluster = await _admin.GetClusterAsync();
            var bucket = await cluster.BucketAsync(IntegrationCluster.Bucket);
            await bucket.Collections.DropScopeAsync(ScopeName);
        }
        catch (ScopeNotFoundException)
        {
            // The first run failed before it created the scope.
        }
        finally
        {
            await _admin.DisposeAsync();
        }
    }

    private sealed record IndexState(string Name, string State);
}

/// <summary>The provisioning (T042) and flavor readiness (T043) tests share one provisioned scope and run one after another.</summary>
[CollectionDefinition(Name)]
public sealed class SampleScopeCollection : ICollectionFixture<ProvisionedSampleScope>
{
    public const string Name = "Provisioned sample scope";
}
