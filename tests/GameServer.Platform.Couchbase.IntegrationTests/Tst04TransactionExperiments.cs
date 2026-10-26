using System.Diagnostics;
using System.Text.Json.Nodes;
using Couchbase;
using Couchbase.Client.Transactions;
using Couchbase.Client.Transactions.Config;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.KeyValue;
using Couchbase.Query;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// TST-04: experiments on behaviour the Couchbase docs call "undefined" (06_verify_couchbase_txn.md).
/// Results are printed to the test output and copied into docs/pcr/platform.md. Assertions are only made where
/// the requirements depend on a documented guarantee (V15-T3, V4-T1 replicas = 0, V16-T1).
/// </summary>
public sealed class Tst04TransactionExperiments(CouchbaseFixture fixture) : IClassFixture<CouchbaseFixture>
{
    private const string Collection = "tst04";

    private static void Log(string experiment, string message) =>
        TestContext.Current.TestOutputHelper?.WriteLine($"[TST-04 {experiment}] {message}");

    private static string NewKey() => "tst04-" + Guid.NewGuid().ToString("N");

    private async Task<(ICluster Cluster, ICouchbaseCollection Collection)> SetupAsync()
    {
        fixture.RequireCouchbase();
        return (await fixture.ClusterAsync(), await fixture.CollectionAsync(Collection));
    }

    /// <summary>Starts a transaction that stages <paramref name="stage"/>, then waits for <paramref name="release"/> before committing.</summary>
    private static Task<string> StageAndHoldAsync(ICluster cluster, Func<AttemptContext, Task> stage, TaskCompletionSource staged, Task release) =>
        Task.Run(async () =>
        {
            try
            {
                await cluster.Transactions.RunAsync(async ctx =>
                {
                    await stage(ctx);
                    staged.TrySetResult();
                    await release;
                });
                return "committed";
            }
            catch (Exception ex)
            {
                staged.TrySetResult();
                return ex.GetType().Name + ": " + ex.InnerException?.GetType().Name;
            }
        });

    [Fact]
    public async Task V15_T1_non_transactional_replace_on_staged_document()
    {
        var (cluster, collection) = await SetupAsync();
        var key = NewKey();
        await collection.InsertAsync(key, new JsonObject { ["v"] = "initial" });
        var cas = (await collection.GetAsync(key)).Cas;
        var staged = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var txn = StageAndHoldAsync(cluster, async ctx => await ctx.ReplaceAsync(await ctx.GetAsync(collection, key), new JsonObject { ["v"] = "txn" }), staged, release.Task);
        await staged.Task;

        string kv;
        try
        {
            await collection.ReplaceAsync(key, new JsonObject { ["v"] = "kv" }, new ReplaceOptions().Cas(cas));
            kv = "replace(cas) succeeded";
        }
        catch (Exception ex)
        {
            kv = ex.GetType().Name;
        }

        release.SetResult();
        var txnOutcome = await txn;
        var final = (await collection.GetAsync(key)).ContentAs<JsonObject>();
        Log("V15-T1", $"non-txn: {kv}; txn: {txnOutcome}; final: {final?.ToJsonString()}");
    }

    [Fact]
    public async Task V15_T2_non_transactional_insert_on_staged_insert()
    {
        var (cluster, collection) = await SetupAsync();
        var key = NewKey();
        var staged = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var txn = StageAndHoldAsync(cluster, ctx => ctx.InsertAsync(collection, key, new JsonObject { ["v"] = "txn" }), staged, release.Task);
        await staged.Task;

        string kv;
        try
        {
            await collection.InsertAsync(key, new JsonObject { ["v"] = "kv" });
            kv = "insert succeeded";
        }
        catch (Exception ex)
        {
            kv = ex.GetType().Name;
        }

        release.SetResult();
        var txnOutcome = await txn;
        var final = (await collection.GetAsync(key)).ContentAs<JsonObject>();
        Log("V15-T2", $"non-txn insert: {kv}; txn: {txnOutcome}; final: {final?.ToJsonString()}");
    }

    [Fact]
    public async Task V15_T3_staged_insert_is_invisible_to_reads_and_query()
    {
        var (cluster, collection) = await SetupAsync();
        var key = NewKey();
        var staged = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var txn = StageAndHoldAsync(cluster, ctx => ctx.InsertAsync(collection, key, new JsonObject { ["v"] = "txn" }), staged, release.Task);
        await staged.Task;

        await Should.ThrowAsync<DocumentNotFoundException>(() => collection.GetAsync(key));
        (await collection.ExistsAsync(key)).Exists.ShouldBeFalse();
        var scope = await fixture.Connection.GetScopeAsync();
        var query = await scope.QueryAsync<JsonObject>(
            $"SELECT META(d).id FROM `{Collection}` AS d USE KEYS $id",
            new QueryOptions().Parameter("id", key).ScanConsistency(QueryScanConsistency.RequestPlus));
        var rows = await query.Rows.CountAsync();

        release.SetResult();
        (await txn).ShouldBe("committed");
        rows.ShouldBe(0);
        Log("V15-T3", "staged insert invisible to Get, Exists and N1QL request_plus (as documented).");
    }

    [Fact]
    public async Task V23_T1_parallel_get_and_get_multi_timing()
    {
        var (cluster, collection) = await SetupAsync();
        var keys = Enumerable.Range(0, 4).Select(_ => NewKey()).ToArray();
        foreach (var key in keys)
        {
            await collection.InsertAsync(key, new JsonObject { ["v"] = key });
        }

        string parallel = "ok";
        TimeSpan sequential = default;
        TimeSpan multi = default;
        await cluster.Transactions.RunAsync(async ctx =>
        {
            try
            {
                await Task.WhenAll(keys.Select(k => ctx.GetAsync(collection, k)));
            }
            catch (Exception ex)
            {
                parallel = ex.GetType().Name;
            }
        });

        await cluster.Transactions.RunAsync(async ctx =>
        {
            var watch = Stopwatch.StartNew();
            foreach (var key in keys)
            {
                await ctx.GetAsync(collection, key);
            }

            sequential = watch.Elapsed;
        });

        await cluster.Transactions.RunAsync(async ctx =>
        {
            var watch = Stopwatch.StartNew();
            var specs = keys.Select(k => new TransactionGetMultiSpec(collection, k, null!)).ToList();
            var result = await ctx.GetMulti(specs, (TransactionGetMultiOptionsBuilder)TransactionGetMultiOptionsBuilder.Create().Mode(TransactionGetMultiMode.PrioritizeLatency));
            multi = watch.Elapsed;
            Enumerable.Range(0, keys.Length).ShouldAllBe(i => result.Exists(i));
        });

        Log("V23-T1", $"Task.WhenAll(4 x ctx.GetAsync): {parallel}; sequential 4 gets: {sequential.TotalMilliseconds:F1} ms; GetMulti 4 specs: {multi.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task V17_T1_atr_count_after_many_transactions()
    {
        var (cluster, collection) = await SetupAsync();
        var count = int.TryParse(Environment.GetEnvironmentVariable("TST04_TXN_COUNT"), out var n) ? n : 200;
        var key = NewKey();
        await collection.InsertAsync(key, new JsonObject { ["v"] = 0 });
        for (var i = 0; i < count; i++)
        {
            await cluster.Transactions.RunAsync(async ctx =>
            {
                var doc = await ctx.GetAsync(collection, key);
                await ctx.ReplaceAsync(doc, new JsonObject { ["v"] = i });
            });
        }

        var atrs = await CountAtrsAsync(cluster, CouchbaseConnection.TransactionMetadataCollection);
        Log("V17-T1", $"{count} transactions on one key -> {atrs} ATR documents in txn_meta. " +
            "Lost-attempt cleanup timing (process killed mid-commit) is not automated yet: run the manual drill in docs/pcr/platform.md.");
        atrs.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task V17_T2_metadata_collection_is_where_atrs_are_written()
    {
        var (cluster, collection) = await SetupAsync();
        var key = NewKey();
        await collection.InsertAsync(key, new JsonObject { ["v"] = 0 });
        var before = await CountAtrsAsync(cluster, CouchbaseConnection.TransactionMetadataCollection);

        await cluster.Transactions.RunAsync(async ctx => await ctx.ReplaceAsync(await ctx.GetAsync(collection, key), new JsonObject { ["v"] = 1 }));

        var after = await CountAtrsAsync(cluster, CouchbaseConnection.TransactionMetadataCollection);
        var inDefault = await CountAtrsAsync(cluster, Collection);
        Log("V17-T2", $"ATRs in txn_meta before/after: {before}/{after}; ATRs in the data collection: {inDefault}. " +
            "Cross-instance cleanup with different MetadataCollection needs two processes: manual drill.");
        after.ShouldBeGreaterThanOrEqualTo(before);
        inDefault.ShouldBe(0);
    }

    [Fact]
    public async Task V4_T1_majority_durability_commits_on_this_bucket()
    {
        var (cluster, collection) = await SetupAsync();
        var bucket = await cluster.Buckets.GetBucketAsync(fixture.Options.BucketName);
        var key = NewKey();

        await cluster.Transactions.RunAsync(ctx => ctx.InsertAsync(collection, key, new JsonObject { ["v"] = 1 }));

        (await collection.ExistsAsync(key)).Exists.ShouldBeTrue();
        Log("V4-T1", $"bucket replicas = {bucket.NumReplicas}: transaction with DurabilityLevel.Majority committed. " +
            "replicas = 1 on a single node (DurabilityImpossible) needs a dedicated bucket: manual drill.");
    }

    [Fact]
    public async Task V16_T1_counter_outside_transaction_is_not_rolled_back()
    {
        var (cluster, collection) = await SetupAsync();
        var counterKey = NewKey();
        var attempts = 0;

        var reserved = await collection.Binary.IncrementAsync(counterKey, new IncrementOptions().Initial(0).Delta(1));
        var ex = await Should.ThrowAsync<Exception>(() => cluster.Transactions.RunAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException("lambda failure after reserve");
        }));

        var value = (await collection.GetAsync(counterKey)).ContentAs<ulong>();
        value.ShouldBe(reserved.Content);
        attempts.ShouldBe(1);
        Log("V16-T1", $"counter reserved = {reserved.Content}, after failed transaction = {value} (not rolled back); lambda attempts = {attempts}; error = {ex.GetType().Name}.");
    }

    private async Task<long> CountAtrsAsync(ICluster cluster, string collectionName)
    {
        var keyspace = $"`{fixture.Options.BucketName}`.`{fixture.Connection.ScopeName}`.`{collectionName}`";
        await cluster.QueryAsync<object>($"CREATE PRIMARY INDEX IF NOT EXISTS ON {keyspace}");
        var result = await cluster.QueryAsync<long>(
            $"SELECT RAW COUNT(*) FROM {keyspace} AS d WHERE META(d).id LIKE '_txn:atr-%'",
            new QueryOptions().ScanConsistency(QueryScanConsistency.RequestPlus));
        return await result.Rows.FirstAsync();
    }
}
