extern alias sdk;

using Google.Protobuf;
using sdk::GameServer.Client;
using Shouldly;
using Economy = sdk::GameServer.Modules.Economy.Protocol.V1;
using Identity = sdk::GameServer.Modules.Identity.Protocol.V1;

namespace GameServer.Client.Tests;

/// <summary>FG-028: a section key binds a section id to its message parser; a table maps ids to keys (research R14).</summary>
public sealed class SectionTableTests
{
    private static readonly SectionKey<Economy.WalletState> Wallet = new("wallet", Economy.WalletState.Parser);
    private static readonly SectionKey<Identity.ProfileState> Profile = new("profile", Identity.ProfileState.Parser);

    [Fact]
    public void A_table_finds_its_keys_by_id_in_order()
    {
        var table = new SectionTable(Wallet, Profile);

        table.Keys.ShouldBe([Wallet, Profile]);
        table.TryGet("wallet", out var wallet).ShouldBeTrue();
        wallet.ShouldBeSameAs(Wallet);
        table.TryGet("Wallet", out _).ShouldBeFalse();
        table.TryGet("unknown", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_duplicate_id_throws()
    {
        var other = new SectionKey<Identity.ProfileState>("wallet", Identity.ProfileState.Parser);

        Should.Throw<ArgumentException>(() => new SectionTable(Wallet, other)).Message.ShouldContain("'wallet'");
    }

    [Fact]
    public void A_key_needs_an_id_and_a_parser()
    {
        Should.Throw<ArgumentException>(() => new SectionKey<Economy.WalletState>(string.Empty, Economy.WalletState.Parser));
        Should.Throw<ArgumentNullException>(() => new SectionKey<Economy.WalletState>("wallet", null!));
    }

    [Fact]
    public void The_untyped_parser_parses_the_same_bytes_as_the_typed_one()
    {
        var message = new Economy.WalletState();
        message.Balances.Add("gold", 42);
        var bytes = message.ToByteString();

        SectionKey key = Wallet;

        key.Id.ShouldBe("wallet");
        key.Parser.ParseFrom(bytes).ShouldBe(Wallet.TypedParser.ParseFrom(bytes));
        Wallet.TypedParser.ParseFrom(bytes).Balances["gold"].ShouldBe(42);
    }
}
