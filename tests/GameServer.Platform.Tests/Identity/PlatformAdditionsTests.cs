using System.Text.Json.Nodes;
using GameServer.Modules.Identity;
using GameServer.Platform.Aggregates;
using GameServer.Platform.Caching;
using GameServer.Platform.Limits;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Platform.Tests.Infrastructure;
using Shouldly;

namespace GameServer.Platform.Tests.Identity;

/// <summary>Platform pieces added for slice 002: key-value expiry, aggregate writer, expiring cache, window counter, credentials.</summary>
public sealed class PlatformAdditionsTests
{
    private static readonly DocumentKey SessionKey = new("sessions", "s1");

    [Fact]
    public async Task Key_value_documents_expire_on_the_store_clock()
    {
        var clock = new FakeClock();
        IKeyValueStore store = new InMemoryDocumentStore(clock);

        (await store.InsertAsync(SessionKey, new JsonObject { ["v"] = 1 }, TimeSpan.FromMinutes(5), CancellationToken.None)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(4));
        (await store.GetAsync(SessionKey, CancellationToken.None)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.GetAsync(SessionKey, CancellationToken.None)).ShouldBeNull();
        (await store.InsertAsync(SessionKey, new JsonObject { ["v"] = 2 }, null, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Replace_with_new_expiry_extends_the_document()
    {
        var clock = new FakeClock();
        IKeyValueStore store = new InMemoryDocumentStore(clock);
        await store.InsertAsync(SessionKey, new JsonObject { ["v"] = 1 }, TimeSpan.FromMinutes(5), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(4));
        var current = (await store.GetAsync(SessionKey, CancellationToken.None))!;
        (await store.ReplaceAsync(current, new JsonObject { ["v"] = 2 }, TimeSpan.FromMinutes(5), CancellationToken.None)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(4));

        (await store.GetAsync(SessionKey, CancellationToken.None))!.Content["v"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Aggregate_writer_bumps_rev_and_skips_unchanged_state()
    {
        using var h = new RunnerHarness();
        var writer = new PlayerAggregateWriter(h.Registry, h.Store, h.Serializer, h.Clock, h.Options, h.Metrics);

        var (first, rev1) = await writer.UpdateAsync<ProfileState>("p1", IdentityAggregates.Profile, p => p with { SessionEpoch = p.SessionEpoch + 1 }, CancellationToken.None);
        var (_, rev2) = await writer.UpdateAsync<ProfileState>("p1", IdentityAggregates.Profile, p => p, CancellationToken.None);
        var (second, rev3) = await writer.UpdateAsync<ProfileState>("p1", IdentityAggregates.Profile, p => p with { SessionEpoch = p.SessionEpoch + 1 }, CancellationToken.None);

        first.SessionEpoch.ShouldBe(1);
        rev1.ShouldBe(1);
        rev2.ShouldBe(1);
        second.SessionEpoch.ShouldBe(2);
        rev3.ShouldBe(2);
        h.Store.Count("receipts").ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_first_writes_through_the_writer_lose_nothing()
    {
        using var h = new RunnerHarness();
        var writer = new PlayerAggregateWriter(h.Registry, h.Store, h.Serializer, h.Clock, h.Options, h.Metrics);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            writer.UpdateAsync<ProfileState>("race", IdentityAggregates.Profile, p => p with { SessionEpoch = p.SessionEpoch + 1 }, CancellationToken.None))));

        var stored = h.Store.Peek(new DocumentKey("pa_profile", "race"))!;
        stored["state"]!["sessionEpoch"]!.GetValue<long>().ShouldBe(6);
    }

    [Fact]
    public void Expiring_cache_respects_ttl_and_zero_disables_it()
    {
        var clock = new FakeClock();
        var cache = new ExpiringCache<string, int>(clock, TimeSpan.FromSeconds(60));
        cache.Set("a", 1);
        clock.Advance(TimeSpan.FromSeconds(59));
        cache.TryGet("a", out var hit).ShouldBeTrue();
        hit.ShouldBe(1);
        clock.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet("a", out _).ShouldBeFalse();

        var disabled = new ExpiringCache<string, int>(clock, TimeSpan.Zero);
        disabled.Set("a", 1);
        disabled.TryGet("a", out _).ShouldBeFalse();
    }

    [Fact]
    public void Window_counter_resets_after_the_window()
    {
        var clock = new FakeClock();
        var counter = new FixedWindowCounter(clock, TimeSpan.FromMinutes(1), 2);

        counter.Increment("ip");
        counter.IsOver("ip").ShouldBeFalse();
        counter.Increment("ip");
        counter.IsOver("ip").ShouldBeTrue();
        counter.RetryAfterSeconds("ip").ShouldBe(60);
        clock.Advance(TimeSpan.FromSeconds(61));
        counter.IsOver("ip").ShouldBeFalse();
    }

    [Fact]
    public void Guest_credentials_hash_with_pepper_and_verify_in_constant_time()
    {
        var pepper = "pepper"u8.ToArray();
        var secret = new byte[32];
        secret[0] = 7;

        var hash = GuestCredentials.Hash(pepper, secret);

        GuestCredentials.Verify(hash, pepper, secret).ShouldBeTrue();
        GuestCredentials.Verify(hash, "other"u8.ToArray(), secret).ShouldBeFalse();
        GuestCredentials.Verify("not-hex", pepper, secret).ShouldBeFalse();
        GuestCredentials.IsValidSecret(new byte[31]).ShouldBeFalse();
        GuestCredentials.IsValidLocalId(new string('a', 65)).ShouldBeFalse();
        PlayerIds.New().ShouldMatch("^p_[0-9a-z]{26}$");
        PlayerIds.New().ShouldNotBe(PlayerIds.New());
    }

    [Fact]
    public void Ban_scope_rules()
    {
        var now = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var groupBan = new BanInfo { Scope = ["counter"], UntilUtc = now.AddDays(1) };
        var loginBan = new BanInfo { Scope = [BanInfo.LoginScope], UntilUtc = null };

        groupBan.BlocksLogin(now).ShouldBeFalse();
        groupBan.BlocksGroup("counter", now).ShouldBeTrue();
        groupBan.BlocksGroup("shop", now).ShouldBeFalse();
        groupBan.BlocksGroup("counter", now.AddDays(2)).ShouldBeFalse();
        loginBan.BlocksLogin(now.AddYears(10)).ShouldBeTrue();
        loginBan.BlocksGroup("shop", now).ShouldBeTrue();
    }
}
