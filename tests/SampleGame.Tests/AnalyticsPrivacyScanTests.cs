using System.Net;
using System.Text.Json.Nodes;
using GameServer.Modules.Identity;
using GameServer.Modules.Identity.Protocol.V1;
using GameServer.Protocol.Core.V1;
using GameServer.Tests.Shared;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// SC-006 / FR-035 (T052, R14): every flow that produces analytics records, then a scan of the raw lines for every credential,
/// token and payload used, and an exact key-set check per record type. The fake verifier derives the order id of a
/// <c>valid:</c> purchase from its reference, so the token-only path is exercised with <c>noorder:</c> (digest).
/// </summary>
public sealed class AnalyticsPrivacyScanTests
{
    private static readonly string[] Envelope = ["event", "schemaVersion", "eventId", "gameId", "environmentId", "userID"];

    private static readonly string[] LoginKeys = [.. Envelope, "activeTime", "activeTimeUtc", "timeZone", "utcOffset", "source", "provider", "isNewUser"];

    private static readonly string[] ResumeKeys = [.. Envelope, "activeTime", "activeTimeUtc", "timeZone", "utcOffset", "source", "isNewUser"];

    private static readonly string[] PaymentKeys =
        [.. Envelope, "chargeTime", "chargeTimeUtc", "timeZone", "utcOffset", "receiptId", "transactionID", "store", "productId", "orderId", "isTest"];

    private static ProviderCredential Pgs(string id) => new() { PlayGames = new GameServer.Modules.Identity.Protocol.V1.PlayGamesCredential { ServerAuthCode = FakeIdentityAdapter.Prefix + id } };

    /// <summary>Every secret found in any line.</summary>
    private static IReadOnlyList<string> Leaks(IEnumerable<string> lines, IReadOnlyCollection<string> secrets) =>
        lines.SelectMany(line => secrets.Where(secret => line.Contains(secret, StringComparison.Ordinal))).Distinct().ToArray();

    [Fact]
    public async Task No_record_carries_a_credential_token_or_payload_and_every_key_set_is_the_documented_one()
    {
        await using var host = await GameHttp.StartGameAsync();
        var secrets = new List<string> { "127.0.0.1", "::1" };

        // Guest login (+ resume on the next day).
        var localId = IdentityHttp.NewLocalId();
        var guestSecret = IdentityHttp.NewSecret();
        var guest = await host.Client().LoginAsync(localId, guestSecret);
        secrets.AddRange([localId, guest.SessionToken, Convert.ToBase64String(guestSecret), Convert.ToHexString(guestSecret), Convert.ToHexStringLower(guestSecret)]);

        // Provider login, then a confirmed switch from the guest session.
        var subject = "g" + Guid.NewGuid().ToString("N")[..20];
        var provider = await (await host.Client().PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = Pgs(subject) }))
            .ReadProtobufAsync<LoginResponse>();
        var switched = await host.BearerClient(guest.SessionToken)
            .PostProtobufAsync(IdentityModule.ProviderLoginRoute, new ProviderLoginRequest { Credential = Pgs(subject), SwitchAccount = true });
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var switchedLogin = await switched.ReadProtobufAsync<LoginResponse>();
        secrets.AddRange([subject, FakeIdentityAdapter.Prefix + subject, provider.SessionToken, switchedLogin.SessionToken]);

        // Purchases: a 300-character Google token without order id, a Google valid: purchase and an Apple purchase.
        var buyer = new GamePlayer(host.BearerClient(switchedLogin.SessionToken), switchedLogin.PlayerId);
        var googleToken = "gtok" + new string('Z', 296);
        (await buyer.IapAsync("google", "gems_100", "noorder:" + googleToken)).Code.ShouldBe("OK");
        (await buyer.IapAsync("google", "gems_100", "valid:google-ref-1")).Code.ShouldBe("OK");
        (await buyer.IapAsync("apple", "gems_100", "valid:2000000748921311")).Code.ShouldBe("OK");
        secrets.AddRange([googleToken, "noorder:" + googleToken, "valid:google-ref-1", "valid:2000000748921311"]);

        // Resume with a stored session on the next day.
        host.Clock.Advance(TimeSpan.FromDays(1));
        (await host.BearerClient(switchedLogin.SessionToken).PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.FlushAnalyticsAsync();

        var lines = host.Analytics.Lines;
        lines.Count.ShouldBe(7);
        Leaks(lines, secrets).ShouldBeEmpty();

        // The scan itself detects an injected token.
        Leaks([lines[0][..^1] + ",\"x\":\"" + googleToken + "\"}"], secrets).ShouldBe([googleToken]);

        foreach (var record in host.Analytics.Records)
        {
            var keys = record.Select(p => p.Key).ToArray();
            var expected = (record["event"]!.GetValue<string>(), record["source"]?.GetValue<string>()) switch
            {
                ("login", "login") => LoginKeys,
                ("login", "resume") => ResumeKeys,
                ("payment", _) when record.ContainsKey("orderId") => PaymentKeys,
                ("payment", _) => PaymentKeys.Where(k => k != "orderId").ToArray(),
                _ => throw new ShouldAssertException("Unexpected record: " + record.ToJsonString()),
            };
            keys.ShouldBe(expected);
        }

        host.Analytics.OfEvent("payment").Select(r => r["transactionID"]!.GetValue<string>()).ShouldContain(t => t.StartsWith("google:h:", StringComparison.Ordinal));
    }
}
