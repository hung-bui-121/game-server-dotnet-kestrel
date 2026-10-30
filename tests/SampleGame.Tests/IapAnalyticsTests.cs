using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Modules.Iap;
using GameServer.Platform;
using GameServer.Platform.Analytics;
using GameServer.Platform.Commands;
using GameServer.Platform.Outbox;
using GameServer.Platform.Testing;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using IapWire = GameServer.Modules.Iap.Protocol.V1;

namespace SampleGame.Tests;

/// <summary>Payment records end to end on two instances (T027, US1, SC-001, FR-007..FR-014).</summary>
public sealed class IapAnalyticsTests
{
    /// <summary>A 128-character product id with three grants (tests/Shared/config/base/iap.json).</summary>
    public const string SizeProbeProduct = "analytics_size_probe_" + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    private static readonly string[] PaymentKeys =
    [
        "event", "schemaVersion", "eventId", "gameId", "environmentId", "userID", "chargeTime", "chargeTimeUtc", "timeZone", "utcOffset",
        "receiptId", "transactionID", "store", "productId", "orderId", "isTest",
    ];

    private static JsonObject Receipt(TestHost host, string receiptId) => host.Store.Peek(ReceiptDocument.KeyFor(receiptId)).ShouldNotBeNull();

    private static Task<int> SweepAsync(TestHost host) => host.Services.GetRequiredService<OutboxSweeper>().SweepOnceAsync(CancellationToken.None);

    private static IEnumerable<JsonObject> Payments(params TestHost[] hosts) => hosts.SelectMany(h => h.Analytics.OfEvent("payment"));

    [Fact]
    public async Task Paying_users_and_transactions_are_counted_exactly_across_replays_rejections_and_redelivery()
    {
        await using var a = await GameHttp.StartGameAsync();
        await using var b = await GameHttp.StartGameAsync(store: a.Store, clock: a.Clock);
        var (p1, _) = await a.NewPlayerAsync();
        var (p2, _) = await a.NewPlayerAsync();
        var (p3, _) = await a.NewPlayerAsync();

        // N = 6 purchases for M = 3 players (each buys twice).
        var firstRequest = Guid.NewGuid().ToString("D");
        var first = await p1.IapAsync("google", "gems_100", "valid:t1", firstRequest);
        (await p1.IapAsync("apple", "gems_100", "valid:t2")).Code.ShouldBe("OK");
        (await p2.IapAsync("google", "starter_pack", "valid:t3")).Code.ShouldBe("OK");
        (await p3.IapAsync("google", "gems_100", "noorder:t5")).Code.ShouldBe("OK");

        // Host A's sink fails: the purchase still succeeds and its receipt stays pending.
        a.Analytics.FailWrites = true;
        var pending = await p2.IapAsync("apple", "gems_100", "valid:t4");
        a.Analytics.FailWrites = false;
        pending.Code.ShouldBe("OK");
        Receipt(a, pending.ReceiptId)["outboxPending"]!.GetValue<bool>().ShouldBeTrue();

        // Replay (same request id), resubmission (new request id), rejected and failed purchases add nothing.
        (await p1.IapAsync("google", "gems_100", "valid:t1", firstRequest)).Replayed.ShouldBeTrue();
        (await p1.IapAsync("google", "gems_100", "valid:t1")).Code.ShouldBe("ALREADY_GRANTED");
        (await p3.IapAsync("google", "gems_100", "valid:t2")).Code.ShouldBe("OK");
        (await p3.IapAsync("apple", "gems_100", "valid:t2")).Code.ShouldBe("ALREADY_GRANTED");
        (await p3.IapAsync("google", "gems_100", "forged")).Code.ShouldBe("INVALID_RECEIPT");
        (await p3.IapAsync("google", "not_in_catalog", "valid:x")).Code.ShouldBe("INVALID_RECEIPT");
        (await p3.PostAsync(IapModule.PurchaseRoute, new IapWire.IapPurchaseRequest { Store = "google", ProductId = "gems_100", ReceiptPayload = "unavailable" }))
            .StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        Payments(a, b).Count().ShouldBe(5);

        // Another instance's sweeper delivers the pending receipt after the relay backoff (30 s).
        a.Clock.Advance(TimeSpan.FromSeconds(31));
        (await SweepAsync(b)).ShouldBe(1);
        Receipt(a, pending.ReceiptId)["outboxPending"]!.GetValue<bool>().ShouldBeFalse();

        var records = Payments(a, b).ToArray();
        records.Length.ShouldBe(6);
        records.Select(r => r["transactionID"]!.GetValue<string>()).Distinct().Count().ShouldBe(6);
        records.Select(r => r["userID"]!.GetValue<string>()).Distinct().Order().ShouldBe(new[] { p1.PlayerId, p2.PlayerId, p3.PlayerId }.Order());
        records.Count(r => r["userID"]!.GetValue<string>() == p1.PlayerId).ShouldBe(2);

        // The redelivered record is computed from the receipt.
        var receipt = Receipt(a, pending.ReceiptId);
        var redelivered = b.Analytics.OfEvent("payment").ShouldHaveSingleItem();
        var entry = receipt["outbox"]!.AsArray().Last()!;
        redelivered["eventId"]!.GetValue<string>().ShouldBe(AnalyticsEventIds.ForOutboxEntry(entry["entryId"]!.GetValue<string>(), receipt["committedAtUtc"]!.GetValue<string>()));
        redelivered["chargeTimeUtc"]!.GetValue<string>().ShouldBe(receipt["committedAtUtc"]!.GetValue<string>());
        redelivered["transactionID"]!.GetValue<string>().ShouldBe("apple:t4");
        redelivered["receiptId"]!.GetValue<string>().ShouldBe(pending.ReceiptId);
        redelivered["userID"]!.GetValue<string>().ShouldBe(p2.PlayerId);

        var firstRecord = a.Analytics.OfEvent("payment").First();
        firstRecord["receiptId"]!.GetValue<string>().ShouldBe(first.ReceiptId);
    }

    [Fact]
    public async Task A_payment_record_has_exactly_the_documented_fields()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();

        var response = await player.IapAsync("google", "gems_100", "valid:token-fields");

        var record = host.Analytics.OfEvent("payment").ShouldHaveSingleItem();
        record.Select(p => p.Key).ShouldBe(PaymentKeys);
        var receipt = Receipt(host, response.ReceiptId);
        record["chargeTimeUtc"]!.GetValue<string>().ShouldBe(receipt["committedAtUtc"]!.GetValue<string>());
        record["chargeTime"]!.GetValue<string>().ShouldBe(receipt["committedAtUtc"]!.GetValue<string>().TrimEnd('Z') + "+00:00");
        record["timeZone"]!.GetValue<string>().ShouldBe("UTC");
        record["utcOffset"]!.GetValue<string>().ShouldBe("+00:00");
        record["gameId"]!.GetValue<string>().ShouldBe("test");
        record["environmentId"]!.GetValue<string>().ShouldBe("test");
        record["userID"]!.GetValue<string>().ShouldBe(player.PlayerId);
        record["receiptId"]!.GetValue<string>().ShouldBe(response.ReceiptId);
        record["transactionID"]!.GetValue<string>().ShouldBe("google:GPA.token-fields");
        record["store"]!.GetValue<string>().ShouldBe("google");
        record["productId"]!.GetValue<string>().ShouldBe("gems_100");
        record["orderId"]!.GetValue<string>().ShouldBe("GPA.token-fields");
        record["isTest"]!.GetValue<bool>().ShouldBeTrue();
        record["eventId"]!.GetValue<string>().ShouldMatch("^[0-9a-f]{32}$");
        var entries = receipt["outbox"]!.AsArray();
        entries.Count.ShouldBe(2);
        entries[1]!["kind"]!.GetValue<string>().ShouldBe("analytics");
        entries[1]!["entryId"]!.GetValue<string>().ShouldBe(response.ReceiptId + ":1");
    }

    [Fact]
    public async Task A_google_purchase_without_order_id_gets_a_token_digest_and_the_token_appears_nowhere()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();
        var token = "tok" + new string('Q', 297);

        (await player.IapAsync("google", "gems_100", "noorder:" + token)).Code.ShouldBe("OK");

        var line = host.Analytics.Lines.Single(l => l.Contains("\"event\":\"payment\"", StringComparison.Ordinal));
        var record = JsonNode.Parse(line)!.AsObject();
        record["transactionID"]!.GetValue<string>().ShouldMatch("^google:h:[0-9a-f]{32}$");
        record.ContainsKey("orderId").ShouldBeFalse();
        line.ShouldNotContain("QQQQ");
    }

    [Fact]
    public async Task A_slow_sink_never_holds_the_purchase_beyond_the_bound_and_the_record_arrives_once_per_event_id()
    {
        await using var a = await GameHttp.StartGameAsync(s => s["GameServer:Analytics:DeliveryTimeoutMs"] = "50");
        await using var b = await GameHttp.StartGameAsync(store: a.Store, clock: a.Clock);
        var (player, _) = await a.NewPlayerAsync();

        // The login record must be written first: a slow write holding it would leave the purchase queued (then skipped).
        await a.FlushAnalyticsAsync();
        a.Analytics.WriteDelay = TimeSpan.FromSeconds(2);

        var watch = Stopwatch.StartNew();
        var response = await player.IapAsync("google", "gems_100", "valid:slow-1");
        watch.Stop();

        response.Code.ShouldBe("OK");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1_500));
        Receipt(a, response.ReceiptId)["outboxPending"]!.GetValue<bool>().ShouldBeTrue();

        // The timed-out delivery was already taken by host A's dispatcher: it is still written (partial relay duplicate).
        a.Analytics.WriteDelay = TimeSpan.Zero;
        await a.FlushAnalyticsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        a.Clock.Advance(TimeSpan.FromSeconds(31));
        (await SweepAsync(b)).ShouldBe(1);

        var copies = Payments(a, b).ToArray();
        copies.Length.ShouldBe(2);
        copies.Select(c => c["eventId"]!.GetValue<string>()).Distinct().ShouldHaveSingleItem();
        foreach (var field in new[] { "transactionID", "chargeTime", "chargeTimeUtc", "userID", "receiptId" })
        {
            copies[1][field]!.GetValue<string>().ShouldBe(copies[0][field]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Disabled_analytics_relays_receipts_writes_nothing_and_reports_no_error()
    {
        await using var host = await GameHttp.StartGameAsync(s => s["GameServer:Analytics:Enabled"] = "false");
        using var failures = new CounterListener(host.Services.GetRequiredService<AnalyticsMetrics>().Meter);
        var (player, _) = await host.NewPlayerAsync();

        var response = await player.IapAsync("google", "gems_100", "valid:off-1");

        response.Code.ShouldBe("OK");
        Receipt(host, response.ReceiptId)["outboxPending"]!.GetValue<bool>().ShouldBeFalse();
        host.Analytics.Lines.ShouldBeEmpty();
        failures.Total.ShouldBe(0);
    }

    [Fact]
    public async Task A_three_grant_purchase_with_a_long_token_and_product_stays_inside_the_receipt_budget()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();
        var token = new string('T', 300);

        var response = await player.IapAsync("google", SizeProbeProduct, "noorder:" + token);

        response.Code.ShouldBe("OK");
        var receipt = Receipt(host, response.ReceiptId);
        var receiptBytes = Encoding.UTF8.GetByteCount(receipt.ToJsonString(GameServerJson.Options));
        receiptBytes.ShouldBeLessThan(4096);
        var entry = receipt["outbox"]!.AsArray()[3]!;
        entry["kind"]!.GetValue<string>().ShouldBe("analytics");
        Encoding.UTF8.GetByteCount(entry.ToJsonString(GameServerJson.Options)).ShouldBeLessThanOrEqualTo(512);
        TestContext.Current.SendDiagnosticMessage($"3-grant receipt: {receiptBytes} B; analytics entry {Encoding.UTF8.GetByteCount(entry.ToJsonString(GameServerJson.Options))} B");

        // Worst case of the entry: every optional field present (realistic order id length, category, store time).
        var worst = new VerifiedPurchase(true, "google", SizeProbeProduct, token, "GPA.3391-5521-0937-41862", null)
        {
            IsTest = true,
            PurchaseCategory = "rewarded",
            PurchasedAt = DateTimeOffset.UnixEpoch,
        };
        var worstEntry = new JsonObject
        {
            ["entryId"] = response.ReceiptId + ":3",
            ["kind"] = "analytics",
            ["payload"] = new JsonObject
            {
                ["event"] = "payment",
                ["schemaVersion"] = 1,
                ["timeField"] = "chargeTime",
                ["fields"] = JsonSerializer.SerializeToNode(IapAnalytics.Fields(worst)),
            },
        };
        Encoding.UTF8.GetByteCount(worstEntry.ToJsonString(GameServerJson.Options)).ShouldBeLessThanOrEqualTo(512);
    }

    [Fact]
    public async Task A_commit_unknown_retried_with_the_same_request_id_gives_one_record()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();
        var requestId = Guid.NewGuid().ToString("D");
        host.Store.Faults.OnlyWhenWritingCollection = "extref_claims";
        host.Store.Faults.Enqueue(StoreFault.AmbiguousAfterCommit);

        var ambiguous = await player.PostAsync(
            IapModule.PurchaseRoute,
            new IapWire.IapPurchaseRequest { Store = "google", ProductId = "gems_100", ReceiptPayload = "valid:amb-1" },
            requestId);
        ambiguous.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var retry = await player.IapAsync("google", "gems_100", "valid:amb-1", requestId);
        retry.Replayed.ShouldBeTrue();

        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await SweepAsync(host);

        host.Analytics.OfEvent("payment").Select(r => r["eventId"]!.GetValue<string>()).Distinct().ShouldHaveSingleItem();
        host.Analytics.OfEvent("payment").ShouldAllBe(r => r["transactionID"]!.GetValue<string>() == "google:GPA.amb-1");
    }

    /// <summary>Counts <c>analytics_delivery_failures_total</c> and <c>analytics_sink_errors_total</c> of one host's meter.</summary>
    private sealed class CounterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<long> _values = new();

        public CounterListener(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is "analytics_delivery_failures_total" or "analytics_sink_errors_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => _values.Enqueue(value));
            _listener.Start();
        }

        public long Total => _values.Sum();

        public void Dispose() => _listener.Dispose();
    }
}
