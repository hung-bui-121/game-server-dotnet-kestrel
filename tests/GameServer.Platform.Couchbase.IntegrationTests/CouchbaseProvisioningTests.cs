using System.Net;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Observability;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 011 US5 (FR-029..FR-032, SC-005) on the real binary and cluster: <c>--provision</c> on an empty scope, its idempotent
/// re-run, the data-only runtime identity, the startup schema check when a collection is missing, an unknown bucket, and
/// startup provisioning in Development. Production variants need TLS (FR-014) and run only with <c>COUCHBASE_TLS_CONNSTR</c>.
/// </summary>
[Collection(SampleScopeCollection.Name)]
public sealed class CouchbaseProvisioningTests(ProvisionedSampleScope scope)
{
    private const string TlsConnectionStringVariable = "COUCHBASE_TLS_CONNSTR";
    private const string TlsCaPathVariable = "COUCHBASE_CA_PATH";

    private static readonly TimeSpan ServeExitTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task The_first_run_on_an_empty_scope_creates_txn_meta_first_and_waits_until_every_index_is_online()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var run = scope.FirstRun;
        run.ExitCode.ShouldBe(0, run.ToString());
        var lines = run.ProvisionLines;
        lines[0].ShouldBe($"game {scope.GameId}, environment beta (Staging), bucket {IntegrationCluster.Bucket}, scope {scope.ScopeName}");
        lines[1].ShouldBe($"scope {scope.ScopeName} created");
        lines[2].ShouldBe($"collection {scope.ScopeName}.{CouchbaseConnection.TransactionMetadataCollection} created");

        var items = lines.Skip(1).SkipLast(1).ToArray();
        items.ShouldAllBe(l => l.Contains(" created", StringComparison.Ordinal));
        lines[^1].ShouldBe($"done: {items.Length} created, 0 present");

        // Every framework, module and purge reference index exists and is online; each was reported with its build time.
        var created = items.Where(l => l.StartsWith("index ", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]).ToArray();
        created.ShouldContain(CouchbaseProvisioner.PendingOutboxIndex);
        created.ShouldContain(CouchbaseProvisioner.ReceiptsPlayerIndex);
        created.ShouldContain(CouchbaseProvisioner.AccountDeletionsDueIndex);
        created.ShouldContain(n => n.EndsWith("_player_ref", StringComparison.Ordinal));
        items.Where(l => l.StartsWith("index ", StringComparison.Ordinal)).ShouldAllBe(l => l.Contains("(online after ", StringComparison.Ordinal));
        var states = await scope.IndexStatesAsync();
        states.Keys.Order(StringComparer.Ordinal).ShouldBe(created.Order(StringComparer.Ordinal));
        states.Values.ShouldAllBe(s => s == "online");
    }

    [Fact]
    public async Task A_rerun_reports_every_item_present_and_creates_nothing()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var run = await scope.ProvisionAsAdminAsync();

        run.ExitCode.ShouldBe(0, run.ToString());
        var items = run.ProvisionLines.Skip(1).SkipLast(1).ToArray();
        items.ShouldAllBe(l => l.EndsWith(" present", StringComparison.Ordinal));
        items.Length.ShouldBe(scope.FirstRun.ProvisionLines.Count - 2);
        run.ProvisionLines[^1].ShouldBe($"done: 0 created, {items.Length} present");
    }

    [Fact]
    public async Task A_staging_host_on_the_data_only_identity_verifies_the_schema_and_becomes_ready()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var user = await scope.DataOnlyUserAsync();
        var settings = scope.HostSettings(user.Name, user.Password);
        foreach (var (key, value) in StagingSecrets.Create())
        {
            settings[key] = value;
        }

        await using var factory = SampleHostFactory.Create("Beta", settings);
        var ready = await factory.CreateClient().GetAsync(ObservabilityModule.ReadyRoute);

        ready.StatusCode.ShouldBe(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Pins the T040 [VERIFY] on EE 7.6.12: a dropped collection probed by the data-only identity is reported missing (the SDK
    /// times out with retry reason CollectionNotFound), and the host exits 1 before it listens (D2).
    /// </summary>
    [Fact]
    public async Task A_missing_collection_stops_the_host_and_only_an_administrative_provision_restores_it()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var user = await scope.DataOnlyUserAsync();
        var dropped = scope.FirstRun.ProvisionLines
            .Where(l => l.StartsWith("collection ", StringComparison.Ordinal))
            .Select(l => l.Split(' ')[1].Split('.')[1])
            .Last();
        await scope.DropCollectionAsync(dropped);
        try
        {
            var settings = scope.HostSettings(user.Name, user.Password);
            foreach (var (key, value) in StagingSecrets.Create())
            {
                settings[key] = value;
            }

            var serve = await SampleHostProcess.RunAsync("Beta", settings, ["--urls", "http://127.0.0.1:0"], ServeExitTimeout);
            serve.ExitCode.ShouldBe(1, serve.ToString());
            serve.Error.ShouldContain($"scope {scope.ScopeName} in bucket {IntegrationCluster.Bucket} is missing collections {dropped}.");
            serve.Error.ShouldContain("--provision");

            // The runtime identity cannot provision (US5-3): exit 2 naming the item, then the admin re-run continues. Measured on
            // EE 7.6.12: the data-only roles may not even list the scopes (cluster manager 403), so the first item fails.
            var dataOnly = await scope.ProvisionAsync(user.Name, user.Password);
            dataOnly.ExitCode.ShouldBe(2, dataOnly.ToString());
            dataOnly.ProvisionLines.ShouldContain(l => l.StartsWith($"FAILED scope {scope.ScopeName}: ", StringComparison.Ordinal), dataOnly.ToString());
            dataOnly.Output.ShouldContain("(HTTP 403)");
            dataOnly.ProvisionLines[^1].ShouldEndWith("fix the cause and run again (idempotent)");
        }
        finally
        {
            var admin = await scope.ProvisionAsAdminAsync();
            admin.ExitCode.ShouldBe(0, admin.ToString());
            // Dropping a collection drops its indexes: the re-run creates exactly the collection and those indexes.
            admin.ProvisionLines.ShouldContain($"collection {scope.ScopeName}.{dropped} created");
            admin.ProvisionLines.Where(l => l.EndsWith(" created", StringComparison.Ordinal) || l.Contains(" created (", StringComparison.Ordinal))
                .ShouldAllBe(l => l == $"collection {scope.ScopeName}.{dropped} created" || l.Contains($" on {dropped} created (", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_unknown_bucket_fails_with_2_naming_the_bucket()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var bucket = "gs-missing-" + Guid.NewGuid().ToString("N")[..8];
        var settings = scope.HostSettings(IntegrationCluster.AdminUser, IntegrationCluster.AdminPassword);
        settings["Couchbase:BucketName"] = bucket;

        var run = await SampleHostProcess.RunAsync("Beta", settings, ["--provision"]);

        run.ExitCode.ShouldBe(2, run.ToString());
        run.ProvisionLines.ShouldContain(l => l.StartsWith($"FAILED bucket {bucket}: ", StringComparison.Ordinal));
        run.Output.ShouldNotContain(IntegrationCluster.AdminPassword);
    }

    /// <summary>
    /// US5-6: a Development host provisions at startup by default. A collection without indexes is dropped from the shared scope
    /// and the Dev host recreates it before it is ready (a second empty scope would cost minutes of index builds under suite
    /// load).
    /// </summary>
    [Fact]
    public async Task A_development_host_still_provisions_at_startup()
    {
        ProvisionedSampleScope.RequireCouchbase();
        var lines = scope.FirstRun.ProvisionLines;
        var indexed = lines.Where(l => l.StartsWith("index ", StringComparison.Ordinal)).Select(l => l.Split(' ')[3]).ToHashSet(StringComparer.Ordinal);
        var dropped = lines
            .Where(l => l.StartsWith("collection ", StringComparison.Ordinal))
            .Select(l => l.Split(' ')[1].Split('.')[1])
            .First(c => c != CouchbaseConnection.TransactionMetadataCollection && !indexed.Contains(c));
        await scope.DropCollectionAsync(dropped);
        try
        {
            var settings = scope.HostSettings(IntegrationCluster.AdminUser, IntegrationCluster.AdminPassword);
            settings["GameServer:Jobs:Enabled"] = "false";
            await using (var factory = SampleHostFactory.Create("Dev", settings))
            {
                var ready = await factory.CreateClient().GetAsync(ObservabilityModule.ReadyRoute);
                ready.StatusCode.ShouldBe(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
            }

            (await scope.CollectionExistsAsync(dropped)).ShouldBeTrue($"the Development host did not recreate {dropped} at startup.");
        }
        finally
        {
            if (!await scope.CollectionExistsAsync(dropped))
            {
                (await scope.ProvisionAsAdminAsync()).ExitCode.ShouldBe(0);
            }
        }
    }

    /// <summary>
    /// SC-005 on Production (TLS required, FR-014): provisioning with the admin identity needs no GM SSO, IAP, pepper or master key,
    /// and a Live host on the data-only identity becomes ready. Opt-in; US5-4 without TLS is covered by
    /// <c>GameServerCommandLineTests</c>.
    /// </summary>
    [Fact]
    public async Task Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls()
    {
        var tls = Environment.GetEnvironmentVariable(TlsConnectionStringVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(tls),
            $"{TlsConnectionStringVariable} not set: Production requires couchbases:// (FR-014); the Staging tests above cover these paths.");
        var user = await scope.DataOnlyUserAsync();
        var settings = scope.HostSettings(IntegrationCluster.AdminUser, IntegrationCluster.AdminPassword);
        settings["Couchbase:ConnectionString"] = tls;
        settings["Couchbase:TrustCertificatePath"] = Environment.GetEnvironmentVariable(TlsCaPathVariable);

        var provision = await SampleHostProcess.RunAsync("Live", settings, ["--provision"]);
        provision.ExitCode.ShouldBe(0, provision.ToString());

        settings["Couchbase:Username"] = user.Name;
        settings["Couchbase:Password"] = user.Password;
        foreach (var (key, value) in StagingSecrets.Create())
        {
            settings[key] = value;
        }

        await using var factory = SampleHostFactory.Create("Live", settings);
        (await factory.CreateClient().GetAsync(ObservabilityModule.ReadyRoute)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
