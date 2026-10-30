using System.Collections.Concurrent;
using GameServer.Jobs;
using GameServer.Modules;
using GameServer.Platform.Jobs;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>Lease job runner on real hosts sharing one store and clock (US4, FR-001, FR-002, FR-004, JOB-01).</summary>
public sealed class LeaseJobHostTests
{
    private const string TestJob = "test.lease-job";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly FakeClock _clock = new();
    private readonly InMemoryDocumentStore _store;
    private readonly JobProbe _probe = new();

    public LeaseJobHostTests() => _store = new InMemoryDocumentStore(_clock);

    private Task<TestHost> StartHostAsync(string name, bool loop, Action<Dictionary<string, string?>>? settings = null) =>
        TestHost.StartAsync(
            modules: b =>
            {
                TestHost.DefaultModules(b);
                b.AddModule(new JobModule("jobs", TestJob));
            },
            settings: s =>
            {
                s["GameServer:Jobs:Enabled"] = loop ? "true" : "false";
                s["GameServer:Jobs:LeaseSeconds"] = "30";
                s["GameServer:Jobs:RenewSeconds"] = "1";
                s["GameServer:Jobs:PollSeconds"] = "5";
                s[$"GameServer:Jobs:Settings:{TestJob}:IntervalSeconds"] = "10";
                settings?.Invoke(s);
            },
            store: _store,
            clock: _clock,
            services: s =>
            {
                s.AddSingleton(_probe);
                s.AddSingleton(new HostTag(name));
            });

    private static LeaseJobRunner Runner(TestHost host) => host.Services.GetRequiredService<LeaseJobRunner>();

    private System.Text.Json.Nodes.JsonObject? Lease() => _store.Peek(LeaseStore.KeyOf(TestJob));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Two_instances_never_run_the_job_concurrently_and_only_the_holder_runs_it()
    {
        _probe.Behavior = (_, context) => Task.Delay(300, context.Cancellation);
        await using var a = await StartHostAsync("a", loop: true);
        await using var b = await StartHostAsync("b", loop: true);

        await WaitUntilAsync(() => _probe.Runs.Count >= 1, "the first run");
        await WaitUntilAsync(() => Lease()?["owner"] is null, "the first release");
        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _probe.Runs.Count >= 2, "the second run");
        await WaitUntilAsync(() => Lease()?["owner"] is null, "the second release");

        _probe.MaxConcurrent.ShouldBe(1);
        _probe.Runs.ShouldAllBe(r => r.RunnerOwnsLease);
        _probe.Runs.Select(r => r.Token).ShouldBe([1L, 2L]);
        _probe.Runs.Count.ShouldBe(2);
        Lease()!["lastRun"]!["result"]!.GetValue<string>().ShouldBe(nameof(JobRunResult.Completed));
    }

    [Fact]
    public async Task Run_once_reports_not_due_held_elsewhere_completed_and_failed()
    {
        await using var host = await StartHostAsync("a", loop: false);
        var runner = Runner(host);

        (await runner.RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.Completed);
        (await runner.RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.NotDue);

        _clock.Advance(TimeSpan.FromSeconds(10));
        var other = await host.Services.GetRequiredService<LeaseStore>().TryAcquireAsync(TestJob, "other:1:00000000", CancellationToken.None);
        other.Lease.ShouldNotBeNull();
        (await runner.RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.HeldElsewhere);

        // The other holder crashed: after its lease ends this instance takes over; the job fails.
        _clock.Advance(TimeSpan.FromSeconds(30));
        _probe.Behavior = (_, _) => throw new InvalidOperationException("job bug");
        (await runner.RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.Failed);
        Lease()!["fencingToken"]!.GetValue<long>().ShouldBe(3);
        Lease()!["lastRun"]!["result"]!.GetValue<string>().ShouldBe(nameof(JobRunResult.Failed));
        // A failed run is finished: the next one waits for the interval.
        (await runner.RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.NotDue);
        host.Logs.ShouldContain(l => l.Contains($"Job {TestJob} failed"));
    }

    [Fact]
    public async Task Losing_the_lease_during_a_run_cancels_the_job_token()
    {
        var started = new TaskCompletionSource();
        _probe.Behavior = async (_, context) =>
        {
            started.TrySetResult();
            await Task.Delay(WaitLimit, context.Cancellation);
        };
        await using var host = await StartHostAsync("a", loop: false);
        var run = Runner(host).RunOnceAsync(TestJob, CancellationToken.None);
        await started.Task.WaitAsync(WaitLimit);

        // Another instance overwrites the lease (as after a takeover): the next renewal's compare-and-swap fails.
        var kv = (IKeyValueStore)_store;
        var current = (await kv.GetAsync(LeaseStore.KeyOf(TestJob), CancellationToken.None))!;
        var stolen = (System.Text.Json.Nodes.JsonObject)current.Content.DeepClone();
        stolen["owner"] = "thief:1:00000000";
        stolen["fencingToken"] = 2L;
        (await kv.ReplaceAsync(current, stolen, null, CancellationToken.None)).ShouldBeTrue();

        (await run.WaitAsync(WaitLimit)).ShouldBe(JobRunResult.LeaseLost);
        _probe.CancelledRuns.ShouldBe(1);
        Lease()!["owner"]!.GetValue<string>().ShouldBe("thief:1:00000000");
        host.Logs.ShouldContain(l => l.Contains($"Lost the lease of job {TestJob}"));
    }

    [Fact]
    public async Task Graceful_stop_of_the_holder_releases_for_an_immediate_run_elsewhere()
    {
        var started = new TaskCompletionSource();
        _probe.Behavior = async (host, context) =>
        {
            if (host == "a")
            {
                started.TrySetResult();
                await Task.Delay(WaitLimit, context.Cancellation);
            }
        };
        var a = await StartHostAsync("a", loop: true);
        await using var b = await StartHostAsync("b", loop: false);
        await started.Task.WaitAsync(WaitLimit);
        var stoppedAt = _clock.UtcNow;

        await a.DisposeAsync();

        var lease = Lease()!;
        lease["owner"].ShouldBeNull();
        lease["lastRun"]!["result"]!.GetValue<string>().ShouldBe(nameof(JobRunResult.Cancelled));
        lease["nextRunAtUtc"]!.GetValue<string>().ShouldBe(GameServer.Platform.Aggregates.AggregateDocumentSerializer.FormatTime(stoppedAt));
        (await Runner(b).RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.Completed);
        _probe.Runs.Select(r => r.Host).ShouldBe(["a", "b"]);
        _probe.Runs.Select(r => r.Token).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task A_disabled_job_never_runs()
    {
        await using var host = await StartHostAsync("a", loop: true, s => s[$"GameServer:Jobs:Settings:{TestJob}:Enabled"] = "false");
        await Task.Delay(500);

        (await Runner(host).RunOnceAsync(TestJob, CancellationToken.None)).ShouldBe(JobRunResult.Disabled);
        _probe.Runs.ShouldBeEmpty();
        Lease().ShouldBeNull();
    }

    [Fact]
    public async Task Duplicate_job_name_fails_host_start()
    {
        var ex = await Should.ThrowAsync<GameServerStartupException>(async () =>
        {
            await using var host = await TestHost.StartAsync(
                modules: b =>
                {
                    TestHost.DefaultModules(b);
                    b.AddModule(new JobModule("jobs", TestJob));
                    b.AddModule(new JobModule("jobs2", TestJob));
                },
                services: s =>
                {
                    s.AddSingleton(_probe);
                    s.AddSingleton(new HostTag("a"));
                });
        });

        ex.Message.ShouldContain($"[module jobs2] job '{TestJob}' is already registered by module 'jobs'");
    }

    public sealed record HostTag(string Name);

    public sealed record JobRun(string Host, long Token, bool RunnerOwnsLease);

    /// <summary>Shared by every host of a test: records runs and concurrency.</summary>
    public sealed class JobProbe
    {
        private int _running;
        private int _maxConcurrent;
        private int _cancelled;

        public ConcurrentQueue<JobRun> Runs { get; } = new();

        public Func<string, IJobContext, Task> Behavior { get; set; } = (_, _) => Task.CompletedTask;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public int CancelledRuns => Volatile.Read(ref _cancelled);

        public async Task RunAsync(string host, IJobContext context, bool runnerOwnsLease)
        {
            Runs.Enqueue(new JobRun(host, context.FencingToken, runnerOwnsLease));
            var running = Interlocked.Increment(ref _running);
            int max;
            while (running > (max = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, running, max) != max)
            {
            }

            try
            {
                await Behavior(host, context);
            }
            catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    public sealed class ProbeJob(JobProbe probe, HostTag host, LeaseJobRunner runner, IKeyValueStore store) : ILeaseJob
    {
        public async Task RunAsync(IJobContext context)
        {
            var lease = (await store.GetAsync(LeaseStore.KeyOf(context.JobName), context.Cancellation))!.Content;
            var owns = lease["owner"]?.GetValue<string>() == runner.Owner && lease["fencingToken"]!.GetValue<long>() == context.FencingToken;
            await probe.RunAsync(host.Name, context, owns);
        }
    }

    private sealed class JobModule(string name, string jobName) : IGameModule
    {
        public ModuleDescriptor Describe() => new() { Name = name, Jobs = [new JobDescriptor(jobName, typeof(ProbeJob), 10)] };

        public void ConfigureServices(IServiceCollection services, ModuleContext context)
        {
        }
    }
}
