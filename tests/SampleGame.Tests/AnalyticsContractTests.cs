using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GameServer.Modules.Iap;
using GameServer.Platform.Testing;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// T062 (SC-009): the data team's rules of <c>docs/guides/analytics.md</c>, applied literally to the raw lines, give the expected
/// UL per local month and active days per player. Purchases go through the real Google verifier (stubbed store HTTP), so test
/// and promo purchases are told apart from real ones; one purchase is redelivered (duplicate copy).
/// </summary>
public sealed class AnalyticsContractTests
{
    private const string Zone = "Asia/Ho_Chi_Minh";

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public async Task The_guides_rules_give_ul_per_local_month_and_active_days_per_player()
    {
        using var rsa = RSA.Create(2048);
        using var appleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var appleRoot = new CertificateRequest("CN=Test Apple Root", appleKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100));
        using var store = new PlayStub();
        var clock = new FakeClock(Utc(10, 30, 10));
        await using var host = await GameHttp.StartGameAsync(
            s =>
            {
                s["GameServer:Analytics:TimeZone"] = Zone;
                s["GameServer:Analytics:DeliveryTimeoutMs"] = "50";
                s["Iap:UseFakeVerifier"] = "false";
                s["Iap:Google:PackageName"] = "com.example.sample";
                s["Iap:Google:ServiceAccountEmail"] = "verifier@example.iam.gserviceaccount.com";
                s["Iap:Google:ServiceAccountPrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem();
                s["Iap:Google:TokenUri"] = "https://oauth.test/token";
                s["Iap:Google:ApiBaseUri"] = "https://play.test";
                s["Iap:Apple:BundleId"] = "com.example.sample";
                s["Iap:Apple:RootCertificateBase64"] = Convert.ToBase64String(appleRoot.RawData);
            },
            clock: clock,
            extra: b => b.Services.AddHttpClient(GooglePlayReceiptVerifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => store));

        // Oct 30 17:00 local: A logs in and buys.
        var (a, aToken) = await host.NewPlayerAsync();
        (await a.IapAsync("google", "gems_100", "real-a1")).Code.ShouldBe("OK");

        // Oct 31 23:00 local: B logs in; 23:59:59 local B buys (October).
        clock.Set(Utc(10, 31, 16));
        var (b, _) = await host.NewPlayerAsync();
        clock.Set(Utc(10, 31, 16, 59, 59));
        (await b.IapAsync("google", "gems_100", "real-b1")).Code.ShouldBe("OK");

        // Nov 1 00:00:01 local: C logs in and buys (November); A comes back with its stored session (resume).
        clock.Set(Utc(10, 31, 17, 0, 1));
        var (c, _) = await host.NewPlayerAsync();
        (await c.IapAsync("google", "gems_100", "real-c1")).Code.ShouldBe("OK");
        (await host.BearerClient(aToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Nov 1 10:00 local: a promo purchase by B (its first request of the day: a resume) and a license-test purchase by C
        // (no revenue, filtered out by the rules).
        clock.Set(Utc(11, 1, 3));
        (await b.IapAsync("google", "gems_100", "promo-b2")).Code.ShouldBe("OK");
        (await c.IapAsync("google", "gems_100", "test-c2")).Code.ShouldBe("OK");

        // Nov 2 10:00 local: A buys while the sink is slow: the inline delivery times out, the sweeper writes a second copy.
        clock.Set(Utc(11, 2, 3));
        (await host.BearerClient(aToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The resume record must be written first, or the slow write would hold it and the purchase would be skipped, not duplicated.
        await host.FlushAnalyticsAsync();
        host.Analytics.WriteDelay = TimeSpan.FromMilliseconds(400);
        (await a.IapAsync("google", "gems_100", "real-a2")).Code.ShouldBe("OK");
        host.Analytics.WriteDelay = TimeSpan.Zero;
        await host.FlushAnalyticsAsync();
        clock.Advance(TimeSpan.FromSeconds(31));
        await host.Services.GetRequiredService<GameServer.Platform.Outbox.OutboxSweeper>().SweepOnceAsync(CancellationToken.None);
        await host.FlushAnalyticsAsync();

        // The guide, literally: parse every line (skip invalid ones), filter, dedupe by transactionID, take 7 / 10 characters.
        var records = new List<JsonElement>();
        foreach (var line in host.Analytics.Lines.Append("{\"event\":\"payment\",\"torn"))
        {
            try
            {
                records.Add(JsonDocument.Parse(line).RootElement);
            }
            catch (JsonException)
            {
                // A torn line: skipped (contract §2).
            }
        }

        var payments = records.Where(r => r.GetProperty("event").GetString() == "payment").ToList();
        payments.Count(p => p.GetProperty("transactionID").GetString() == "google:GPA.real-a2").ShouldBe(2);
        var ul = payments
            .Where(p => p.GetProperty("environmentId").GetString() == "test" && !p.GetProperty("isTest").GetBoolean()
                        && (p.TryGetProperty("purchaseCategory", out var category) ? category.GetString() : string.Empty) != "promo")
            .DistinctBy(p => p.GetProperty("transactionID").GetString())
            .GroupBy(p => p.GetProperty("chargeTime").GetString()![..7])
            .ToDictionary(g => g.Key, g => g.Select(p => p.GetProperty("userID").GetString()).Distinct().Count());
        ul.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} {m.Value}").ShouldBe(["2026-10 2", "2026-11 2"]);

        var activeDays = records
            .Where(r => r.GetProperty("event").GetString() == "login")
            .GroupBy(r => (User: r.GetProperty("userID").GetString()!, Month: r.GetProperty("activeTime").GetString()![..7]))
            .ToDictionary(g => g.Key, g => g.Select(r => r.GetProperty("activeTime").GetString()![..10]).Distinct().Count());
        activeDays.Select(d => $"{d.Key.User} {d.Key.Month} {d.Value}").Order(StringComparer.Ordinal).ShouldBe(
            new[] { $"{a.PlayerId} 2026-10 1", $"{a.PlayerId} 2026-11 2", $"{b.PlayerId} 2026-10 1", $"{b.PlayerId} 2026-11 1", $"{c.PlayerId} 2026-11 1" }
                .Order(StringComparer.Ordinal));
    }

    /// <summary>Google OAuth + <c>purchases.products.get</c>: token prefix <c>promo-</c> → purchaseType 1, <c>test-</c> → 0 without order id.</summary>
    private sealed class PlayStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "oauth.test")
            {
                return Task.FromResult(Json(new { access_token = "at", expires_in = 3600 }));
            }

            var token = Uri.UnescapeDataString(request.RequestUri.Segments[^1]);
            object purchase = token.StartsWith("test-", StringComparison.Ordinal)
                ? new { purchaseState = 0, purchaseType = 0, purchaseTimeMillis = "1793500000000" }
                : token.StartsWith("promo-", StringComparison.Ordinal)
                    ? new { purchaseState = 0, purchaseType = 1, orderId = "GPA." + token, purchaseTimeMillis = "1793500000000" }
                    : new { purchaseState = 0, orderId = "GPA." + token, purchaseTimeMillis = "1793500000000" };
            return Task.FromResult(Json(purchase));
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
