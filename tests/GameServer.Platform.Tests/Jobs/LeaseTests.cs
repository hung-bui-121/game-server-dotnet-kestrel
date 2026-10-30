using GameServer.Platform.Jobs;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace GameServer.Platform.Tests.Jobs;

/// <summary>Lease document semantics (JOB-01, research R1, US4-2): CAS acquire/renew/release with a fencing token.</summary>
public sealed class LeaseTests
{
    private const string Job = "test.job";
    private const string A = "host-a:1:aaaaaaaa";
    private const string B = "host-b:2:bbbbbbbb";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly FakeClock _clock = new();
    private readonly InMemoryDocumentStore _store;
    private readonly LeaseStore _leases;

    public LeaseTests()
    {
        _store = new InMemoryDocumentStore(_clock);
        var options = new GameServerOptions { GameId = "test", EnvironmentId = "dev" };
        _leases = new LeaseStore(_store, _clock, options, NullLogger<LeaseStore>.Instance);
    }

    private async Task<JobLease> AcquireAsync(string owner)
    {
        var acquisition = await _leases.TryAcquireAsync(Job, owner, None);
        acquisition.Refusal.ShouldBe(LeaseRefusal.None);
        return acquisition.Lease!;
    }

    private System.Text.Json.Nodes.JsonObject Document() => _store.Peek(LeaseStore.KeyOf(Job))!;

    [Fact]
    public async Task First_acquisition_inserts_token_1_without_expiry()
    {
        var lease = await AcquireAsync(A);

        lease.FencingToken.ShouldBe(1);
        lease.Owner.ShouldBe(A);
        lease.LeaseUntil.ShouldBe(_clock.UtcNow.AddSeconds(60));
        var document = Document();
        document["owner"]!.GetValue<string>().ShouldBe(A);
        document["jobName"]!.GetValue<string>().ShouldBe(Job);
        document["gameId"]!.GetValue<string>().ShouldBe("test");
        LeaseStore.KeyOf(Job).ShouldBe(new DocumentKey("leases", "leases::test.job"));

        // DAT-03: no Couchbase expiry; the document outlives any lease time.
        _clock.Advance(TimeSpan.FromDays(400));
        _store.Peek(LeaseStore.KeyOf(Job)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Second_owner_is_refused_while_the_lease_is_held()
    {
        await AcquireAsync(A);
        _clock.Advance(TimeSpan.FromSeconds(59));

        var refused = await _leases.TryAcquireAsync(Job, B, None);

        refused.Lease.ShouldBeNull();
        refused.Refusal.ShouldBe(LeaseRefusal.HeldElsewhere);
        (await _leases.TryAcquireAsync(Job, A, None)).Refusal.ShouldBe(LeaseRefusal.HeldElsewhere);
    }

    [Fact]
    public async Task Renewal_keeps_the_token_and_extends_the_lease()
    {
        var lease = await AcquireAsync(A);
        _clock.Advance(TimeSpan.FromSeconds(20));

        var renewed = await _leases.TryRenewAsync(lease, None);

        renewed.ShouldNotBeNull();
        renewed.FencingToken.ShouldBe(1);
        renewed.LeaseUntil.ShouldBe(_clock.UtcNow.AddSeconds(60));
        _clock.Advance(TimeSpan.FromSeconds(50));
        (await _leases.TryAcquireAsync(Job, B, None)).Refusal.ShouldBe(LeaseRefusal.HeldElsewhere);
    }

    [Fact]
    public async Task Takeover_happens_only_after_lease_until_with_the_next_token()
    {
        var lease = await AcquireAsync(A);
        _clock.Set(lease.LeaseUntil.AddMilliseconds(-1));
        (await _leases.TryAcquireAsync(Job, B, None)).Refusal.ShouldBe(LeaseRefusal.HeldElsewhere);

        _clock.Set(lease.LeaseUntil);
        var takeover = await AcquireAsync(B);

        takeover.FencingToken.ShouldBe(2);
        takeover.Owner.ShouldBe(B);
        Document()["fencingToken"]!.GetValue<long>().ShouldBe(2);
    }

    [Fact]
    public async Task Renewal_after_a_takeover_is_lost_and_release_writes_nothing()
    {
        var lease = await AcquireAsync(A);
        _clock.Advance(TimeSpan.FromSeconds(61));
        var takeover = await AcquireAsync(B);

        (await _leases.TryRenewAsync(lease, None)).ShouldBeNull();
        (await _leases.ReleaseAsync(lease, _clock.UtcNow, "Completed", None)).ShouldBeFalse();

        Document()["owner"]!.GetValue<string>().ShouldBe(B);
        Document()["fencingToken"]!.GetValue<long>().ShouldBe(takeover.FencingToken);
    }

    [Fact]
    public async Task An_ambiguous_renewal_that_landed_is_recognized()
    {
        var lease = await AcquireAsync(A);
        _clock.Advance(TimeSpan.FromSeconds(20));
        // The write landed but the caller never saw the result: it still holds the old document (stale CAS).
        (await _leases.TryRenewAsync(lease, None)).ShouldNotBeNull();
        _clock.Advance(TimeSpan.FromSeconds(20));

        var renewed = await _leases.TryRenewAsync(lease, None);

        renewed.ShouldNotBeNull();
        renewed.FencingToken.ShouldBe(1);
        renewed.LeaseUntil.ShouldBe(_clock.UtcNow.AddSeconds(60));
    }

    [Fact]
    public async Task A_released_lease_waits_for_next_run_at()
    {
        var lease = await AcquireAsync(A);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var nextRunAt = _clock.UtcNow.AddSeconds(600);

        (await _leases.ReleaseAsync(lease, nextRunAt, "Completed", None)).ShouldBeTrue();

        var document = Document();
        document["owner"].ShouldBeNull();
        document["leaseUntilUtc"]!.GetValue<string>().ShouldBe("2026-10-26T00:00:05.000Z");
        document["nextRunAtUtc"]!.GetValue<string>().ShouldBe("2026-10-26T00:10:05.000Z");
        document["lastRun"]!["result"]!.GetValue<string>().ShouldBe("Completed");
        document["lastRun"]!["startedAtUtc"]!.GetValue<string>().ShouldBe("2026-10-26T00:00:00.000Z");
        (await _leases.TryAcquireAsync(Job, B, None)).Refusal.ShouldBe(LeaseRefusal.NotDue);
        (await _leases.TryAcquireAsync(Job, A, None)).Refusal.ShouldBe(LeaseRefusal.NotDue);

        _clock.Set(nextRunAt);
        (await AcquireAsync(B)).FencingToken.ShouldBe(2);
    }

    [Fact]
    public async Task An_interrupted_run_is_released_for_an_immediate_run_elsewhere()
    {
        var lease = await AcquireAsync(A);

        (await _leases.ReleaseAsync(lease, _clock.UtcNow, "Cancelled", None)).ShouldBeTrue();

        var next = await AcquireAsync(B);
        next.FencingToken.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_acquisitions_have_exactly_one_winner()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => _leases.TryAcquireAsync(Job, $"host-{i}:1:0000000{i}", None))));

        results.Count(r => r.Lease is not null).ShouldBe(1);
        results.Where(r => r.Lease is null).ShouldAllBe(r => r.Refusal == LeaseRefusal.HeldElsewhere);
    }
}
