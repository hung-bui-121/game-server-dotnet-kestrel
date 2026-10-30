using System.Security.Cryptography;
using System.Text;
using GameServer.Modules.Economy;
using GameServer.Platform.Persistence;
using GameServer.Tests.Shared;
using Shouldly;

namespace SampleGame.Tests;

/// <summary>
/// Claim keys of long external references (review T066 M4, ECO-08): a key within the Couchbase limit keeps its form, a longer
/// one is hashed, on every store alike.
/// </summary>
public sealed class ExternalClaimKeyTests
{
    private const string GooglePrefix = "extref::iap_google::";

    private static string Hashed(string reference) =>
        "extrefh::iap_google::" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));

    [Fact]
    public void A_key_up_to_the_limit_keeps_its_form_and_one_byte_more_is_hashed()
    {
        var fits = new string('t', EconomyOperations.MaxClaimKeyBytes - GooglePrefix.Length);
        var over = fits + "t";

        EconomyOperations.ExternalClaimKey("iap_google", fits).ShouldBe(GooglePrefix + fits);
        Encoding.UTF8.GetByteCount(GooglePrefix + fits).ShouldBe(246);
        EconomyOperations.ExternalClaimKey("iap_google", over).ShouldBe(Hashed(over));
        EconomyOperations.ExternalClaimKey("iap_google", "token-1").ShouldBe(GooglePrefix + "token-1");
    }

    [Fact]
    public void The_limit_counts_utf8_bytes_not_characters()
    {
        // 112 two-byte characters = 224 bytes + 20 prefix bytes = 244 (fits); 114 = 248 (hashed) although only 134 characters.
        var fits = new string('é', 112);
        var over = new string('é', 114);

        EconomyOperations.ExternalClaimKey("iap_google", fits).ShouldBe(GooglePrefix + fits);
        EconomyOperations.ExternalClaimKey("iap_google", over).ShouldBe(Hashed(over));
    }

    [Fact]
    public void A_hashed_key_is_stable_short_and_distinct_per_reference_and_source()
    {
        var token = new string('x', 300);

        var key = EconomyOperations.ExternalClaimKey("iap_google", token);

        key.ShouldBe(EconomyOperations.ExternalClaimKey("iap_google", token));
        key.ShouldNotBe(EconomyOperations.ExternalClaimKey("iap_google", token + "y"));
        key.ShouldNotBe(EconomyOperations.ExternalClaimKey("iap_apple", token));
        Encoding.UTF8.GetByteCount(key).ShouldBeLessThanOrEqualTo(EconomyOperations.MaxClaimKeyBytes);
    }

    [Fact]
    public async Task A_300_character_google_token_is_granted_once_under_its_hashed_claim_and_the_ledger_keeps_the_token()
    {
        await using var host = await GameHttp.StartGameAsync();
        var (player, _) = await host.NewPlayerAsync();
        var token = "noorder:" + new string('T', 300);
        var reference = token["noorder:".Length..];

        (await player.IapAsync("google", "gems_100", token)).Code.ShouldBe("OK");
        (await player.IapAsync("google", "gems_100", token)).Code.ShouldBe("ALREADY_GRANTED");

        host.Store.Peek(new DocumentKey(EconomyOperations.ClaimCollection, Hashed(reference))).ShouldNotBeNull();
        host.Store.Peek(new DocumentKey(EconomyOperations.ClaimCollection, GooglePrefix + reference)).ShouldBeNull();
        host.Ledger(player.PlayerId).ShouldHaveSingleItem()["payload"]!["externalReference"]!.GetValue<string>().ShouldBe(reference);
        (await player.WalletAsync()).Balances["gem"].ShouldBe(100);
    }
}
