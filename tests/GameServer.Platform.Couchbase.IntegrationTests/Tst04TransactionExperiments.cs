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

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("TST04_TIMING_ROUNDS"), out var r) ? r : 50;
        var specs = keys.Select(k => new TransactionGetMultiSpec(collection, k, null!)).ToList();
        var multiOptions = (TransactionGetMultiOptionsBuilder)TransactionGetMultiOptionsBuilder.Create().Mode(TransactionGetMultiMode.PrioritizeLatency);
        var readers = new (string Name, Func<AttemptContext, Task> Read)[]
        {
            ("sequential 4 x GetAsync", async ctx =>
            {
                foreach (var key in keys)
                {
                    await ctx.GetAsync(collection, key);
                }
            }),
            ("Task.WhenAll 4 x GetAsync", ctx => Task.WhenAll(keys.Select(k => ctx.GetAsync(collection, k)))),
            ("GetMulti 4 specs", async ctx =>
            {
                var result = await ctx.GetMulti(specs, multiOptions);
                Enumerable.Range(0, keys.Length).ShouldAllBe(i => result.Exists(i));
            }),
        };

        var samples = readers.ToDictionary(x => x.Name, _ => new List<double>());
        var failures = readers.ToDictionary(x => x.Name, _ => new List<string>());
        for (var round = -3; round < rounds; round++)
        {
            // Rotate the order each round so no reader always pays for a cold connection; rounds < 0 are warm-up.
            for (var i = 0; i < readers.Length; i++)
            {
                var (name, read) = readers[(i + Math.Max(round, 0)) % readers.Length];
                var elapsed = TimeSpan.Zero;
                try
                {
                    await cluster.Transactions.RunAsync(async ctx =>
                    {
                        var watch = Stopwatch.StartNew();
                        await read(ctx);
                        elapsed = watch.Elapsed;
                    });
                }
                catch (Exception ex)
                {
                    failures[name].Add(ex.GetType().Name + ": " + ex.InnerException?.GetType().Name);
                    continue;
                }

                if (round >= 0)
                {
                    samples[name].Add(elapsed.TotalMilliseconds);
                }
            }
        }

        static double Percentile(List<double> values, double p)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length == 0 ? double.NaN : sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
        }

        var summary = string.Join("; ", readers.Select(x => x.Name).Select(name => samples[name].Count == 0
            ? $"{name}: failed ({string.Join(", ", failures[name].Distinct())})"
            : $"{name}: median {Percentile(samples[name], 0.5):F2} ms, p95 {Percentile(samples[name], 0.95):F2} ms"));
        Log("V23-T1", $"{rounds} rounds (after 3 warm-up) in one transaction each — {summary}");
        // A transient failure is tolerated (local single node under other test load); a systematic one is not.
        foreach (var (name, failed) in failures)
        {
            failed.Count.ShouldBeLessThanOrEqualTo(Math.Max(1, (rounds + 3) / 20), $"{name}: {string.Join(", ", failed.Distinct())}");
        }

        // DAT-11 candidate: parallel gets, then replace each document with the result of its own get (no extra read).
        // SDK rules for concurrent operations: wait for every concurrent op to finish before the next step; mutations run
        // one after another here.
        await cluster.Transactions.RunAsync(async ctx =>
        {
            var docs = await Task.WhenAll(keys.Select(k => ctx.GetAsync(collection, k)));
            foreach (var doc in docs)
            {
                await ctx.ReplaceAsync(doc!, new JsonObject { ["v"] = "replaced" });
            }
        });
        foreach (var key in keys)
        {
            (await collection.GetAsync(key)).ContentAs<JsonObject>()!["v"]!.GetValue<string>().ShouldBe("replaced");
        }

        Log("V23-T1", "Task.WhenAll gets followed by Replace of each TransactionGetResult: committed.");
    }

    [Fact]
    public async Task V17_T1_atr_count_after_many_transactions()
    {
        var (cluster, collection) = await SetupAsync();
        var count = int.TryParse(Environment.GetEnvironmentVariable("TST04_TXN_COUNT"), out var n) ? n : 200;
        var key = NewKey();
        await collection.InsertAsync(key, new JsonObject { ["v"] = 0 });
        var before = await CountAtrsAsync(CouchbaseConnection.TransactionMetadataCollection);
        for (var i = 0; i < count; i++)
        {
            await cluster.Transactions.RunAsync(async ctx =>
            {
                var doc = await ctx.GetAsync(collection, key);
                await ctx.ReplaceAsync(doc, new JsonObject { ["v"] = i });
            });
        }

        var atrs = await CountAtrsAsync(CouchbaseConnection.TransactionMetadataCollection);
        Log("V17-T1", $"{count} transactions on one key -> ATR documents in txn_meta {before} before, {atrs} after (+{atrs - before}; " +
            "the scope is shared with the other experiments of this class). " +
            "Lost-attempt cleanup timing (process killed mid-commit) is not automated yet: run the manual drill in docs/pcr/platform.md.");
        atrs.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task V17_T2_metadata_collection_is_where_atrs_are_written()
    {
        var (cluster, collection) = await SetupAsync();
        var key = NewKey();
        await collection.InsertAsync(key, new JsonObject { ["v"] = 0 });
        var before = await CountAtrsAsync(CouchbaseConnection.TransactionMetadataCollection);

        await cluster.Transactions.RunAsync(async ctx => await ctx.ReplaceAsync(await ctx.GetAsync(collection, key), new JsonObject { ["v"] = 1 }));

        var after = await CountAtrsAsync(CouchbaseConnection.TransactionMetadataCollection);
        var inDefault = await CountAtrsAsync(Collection);
        var meta = await fixture.CollectionAsync(CouchbaseConnection.TransactionMetadataCollection);
        var clientRecord = (await meta.ExistsAsync("_txn:client-record")).Exists;
        Log("V17-T2", $"ATRs in txn_meta before/after: {before}/{after}; ATRs in the data collection: {inDefault}; " +
            $"cleanup client record in txn_meta: {clientRecord}. " +
            "Cross-instance cleanup with different MetadataCollection needs two processes: manual drill.");
        after.ShouldBeGreaterThan(0);
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

    /// <summary>
    /// Counts ATR documents with KV <c>Exists</c> over the SDK's fixed ATR id list. N1QL cannot be used: the query/index
    /// services do not return <c>_txn:</c> system documents (first run on 7.6.12 counted 0 ATRs after 200 commits).
    /// The id list is internal to the SDK (<c>AtrIds.AllAtrIds</c>), read by reflection — experiment code only.
    /// </summary>
    private async Task<long> CountAtrsAsync(string collectionName)
    {
        var atrIds = typeof(AttemptContext).Assembly
            .GetType("Couchbase.Client.Transactions.ActiveTransactionRecords.AtrIds", throwOnError: true)!
            .GetField("AllAtrIds", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .GetValue(null) as IEnumerable<string>
            ?? throw new InvalidOperationException("Couchbase SDK no longer exposes AtrIds.AllAtrIds; update the TST-04 helper.");
        var collection = await fixture.CollectionAsync(collectionName);
        var exists = await Task.WhenAll(atrIds.Select(async id => (await collection.ExistsAsync(id)).Exists));
        return exists.LongCount(e => e);
    }
}
