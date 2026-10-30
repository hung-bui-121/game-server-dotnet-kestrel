using System.Text;
using System.Text.Json.Nodes;
using Couchbase.Query;
using GameServer.Modules.Economy;
using GameServer.Platform.Analytics;
using GameServer.Platform.Commands;
using GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;
using GameServer.Platform.Outbox;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Couchbase.IntegrationTests;

/// <summary>
/// Slice 012 T053 on real transactions: a payment record whose inline delivery failed is delivered by another instance's
/// sweeper with the identifying fields of the receipt (US1-4), the documented manual re-drive of a dead-lettered receipt
/// (contracts/analytics-operations §7, SD4) and the receipt size of a large purchase (FR-012).
/// </summary>
/// <remarks>The tests share one scope and one forward-only clock; sweeps may relay other tests' receipts, assertions look at their own.</remarks>
public sealed class CouchbaseAnalyticsTests(CouchbaseGameFixture fixture) : IClassFixture<CouchbaseGameFixture>
{
    private const string SizeProbeProduct = "analytics_size_probe_" + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>Past the first relay backoff (30 s) and the sweeper's minimum age (10 s).</summary>
    private static readonly TimeSpan PastBackoff = TimeSpan.FromSeconds(31);

    private static readonly FakeClock Clock = new();

    /// <summary>Longest Google reference whose claim key keeps the raw form (the 20-byte "extref::iap_google::" prefix included).</summary>
    private const int MaxRawTokenLength = EconomyOperations.MaxClaimKeyBytes - 20;

    private async Task<(CouchbaseGameHost Host, InMemoryAnalyticsSink Sink)> StartAsync(Action<Dictionary<string, string?>>? settings = null)
    {
        fixture.RequireCouchbase();
        var sink = new InMemoryAnalyticsSink();
        var host = await CouchbaseGameHost.StartAsync(fixture, Clock, settings, services: s => s.AddSingleton<IAnalyticsSink>(sink));
        return (host, sink);
    }

    private static async Task<GamePlayer> NewPlayerAsync(CouchbaseGameHost host)
    {
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        return new GamePlayer(host.BearerClient(login.SessionToken), login.PlayerId);
    }

    private async Task<JsonObject> ReceiptAsync(string receiptId) =>
        (await fixture.Store.GetAsync(ReceiptDocument.KeyFor(receiptId), CancellationToken.None)).ShouldNotBeNull().Content;

    private static Task<int> SweepAsync(CouchbaseGameHost host) =>
        host.Services.GetRequiredService<OutboxSweeper>().SweepOnceAsync(CancellationToken.None);

    private static JsonObject RecordOf(InMemoryAnalyticsSink sink, string receiptId) =>
        sink.OfEvent("payment").Single(r => r["receiptId"]!.GetValue<string>() == receiptId);

    [Fact]
    public async Task A_failed_inline_delivery_is_written_by_another_instances_sweeper_with_the_receipts_identity()
    {
        var (a, sinkA) = await StartAsync();
        await using var hostA = a;
        var (b, sinkB) = await StartAsync();
        await using var hostB = b;
        var player = await NewPlayerAsync(a);

        sinkA.FailWrites = true;
        var purchase = await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));
        sinkA.FailWrites = false;

        purchase.Code.ShouldBe("OK");
        var pending = await ReceiptAsync(purchase.ReceiptId);
        pending["outboxPending"]!.GetValue<bool>().ShouldBeTrue();
        Clock.Advance(PastBackoff);
        (await SweepAsync(b)).ShouldBeGreaterThanOrEqualTo(1);

        var record = RecordOf(sinkB, purchase.ReceiptId);
        var entry = pending["outbox"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == "analytics")!;
        record["eventId"]!.GetValue<string>().ShouldBe(AnalyticsEventIds.ForOutboxEntry(entry["entryId"]!.GetValue<string>(), pending["committedAtUtc"]!.GetValue<string>()));
        record["chargeTimeUtc"]!.GetValue<string>().ShouldBe(pending["committedAtUtc"]!.GetValue<string>());
        record["transactionID"]!.GetValue<string>().ShouldBe(entry["payload"]!["fields"]!["transactionID"]!.GetValue<string>());
        record["userID"]!.GetValue<string>().ShouldBe(player.PlayerId);
        (await ReceiptAsync(purchase.ReceiptId))["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task A_dead_lettered_payment_is_re_driven_by_the_documented_statement()
    {
        var (host, sink) = await StartAsync(s => s["GameServer:Outbox:MaxRelayAttempts"] = "1");
        await using var _ = host;
        var player = await NewPlayerAsync(host);

        sink.FailWrites = true;
        var purchase = await player.IapAsync("google", "gems_100", "valid:" + Guid.NewGuid().ToString("N"));
        sink.FailWrites = false;
        purchase.Code.ShouldBe("OK");
        (await ReceiptAsync(purchase.ReceiptId))["outboxDeadLetter"]!.GetValue<bool>().ShouldBeTrue();
        Clock.Advance(PastBackoff);
        await SweepAsync(host);
        sink.OfEvent("payment").ShouldNotContain(r => r["receiptId"]!.GetValue<string>() == purchase.ReceiptId);

        // contracts/analytics-operations.md §7, verbatim except the keyspace of this test.
        var statement =
            $"UPDATE `{fixture.Options.BucketName}`.`{fixture.Connection.ScopeName}`.`receipts` r\n" +
            "SET r.outboxDeadLetter = false, r.outboxAttempts = 0\n" +
            "UNSET r.outboxNextAttemptAtUtc\n" +
            "WHERE r.outboxPending = true AND r.outboxDeadLetter = true\n" +
            "  AND ANY e IN r.outbox SATISFIES e.kind = \"analytics\" END;";
        var cluster = await fixture.ClusterAsync();
        // A scan of the receipts collection with request_plus: under full-suite index load it can pass the 20 s client
        // default (CouchbaseOptions.QueryTimeoutMs); the test checks the statement, not its latency.
        var options = new QueryOptions().ScanConsistency(QueryScanConsistency.RequestPlus).Metrics(true).Timeout(TimeSpan.FromMinutes(2));
        var result = await cluster.QueryAsync<JsonObject>(statement, options);
        await foreach (var row in result.Rows)
        {
            row.ShouldNotBeNull();
        }

        result.MetaData!.Status.ShouldBe(QueryStatus.Success);
        result.MetaData.Metrics.MutationCount.ShouldBeGreaterThanOrEqualTo(1u);

        (await SweepAsync(host)).ShouldBeGreaterThanOrEqualTo(1);
        RecordOf(sink, purchase.ReceiptId)["transactionID"]!.GetValue<string>().ShouldStartWith("google:GPA.");
        var relayed = await ReceiptAsync(purchase.ReceiptId);
        relayed["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
        relayed["outboxDeadLetter"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task A_three_grant_purchase_with_a_long_token_stays_below_the_receipt_limit()
    {
        var (host, sink) = await StartAsync();
        await using var _ = host;
        var player = await NewPlayerAsync(host);

        // The same 300-character token as the in-memory test: its claim key is hashed (longer than the Couchbase key limit).
        var purchase = await player.IapAsync("google", SizeProbeProduct, "noorder:" + new string('T', 300));

        purchase.Code.ShouldBe("OK");
        var receipt = await ReceiptAsync(purchase.ReceiptId);
        Encoding.UTF8.GetByteCount(receipt.ToJsonString(GameServerJson.Options)).ShouldBeLessThan(4096);
        RecordOf(sink, purchase.ReceiptId)["productId"]!.GetValue<string>().ShouldBe(SizeProbeProduct);
    }

    [Theory]
    [InlineData(MaxRawTokenLength, false)]
    [InlineData(MaxRawTokenLength + 1, true)]
    [InlineData(300, true)]
    public async Task A_long_google_token_is_granted_once_on_couchbase_with_a_raw_or_hashed_claim(int length, bool hashed)
    {
        var (host, _) = await StartAsync();
        await using var __ = host;
        var player = await NewPlayerAsync(host);
        var reference = new string('L', length - 32) + Guid.NewGuid().ToString("N");

        (await player.IapAsync("google", "gems_100", "noorder:" + reference)).Code.ShouldBe("OK");
        (await player.IapAsync("google", "gems_100", "noorder:" + reference)).Code.ShouldBe("ALREADY_GRANTED");

        var key = EconomyOperations.ExternalClaimKey("iap_google", reference);
        key.StartsWith("extrefh::", StringComparison.Ordinal).ShouldBe(hashed);
        (await fixture.Store.GetAsync(new DocumentKey(EconomyOperations.ClaimCollection, key), CancellationToken.None)).ShouldNotBeNull();
        (await player.WalletAsync()).Balances["gem"].ShouldBe(100);
    }
}
