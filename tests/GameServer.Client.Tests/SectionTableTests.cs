extern alias sdk;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using sdk::GameServer.Client;
using Shouldly;
using Economy = sdk::GameServer.Modules.Economy.Protocol.V1;
using Identity = sdk::GameServer.Modules.Identity.Protocol.V1;
using SampleWire = SampleGame.Protocol.V1;
using SdkCore = sdk::GameServer.Protocol.Core.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// FG-028: a section key binds a section id to its message parser; a table maps ids to keys; the store reads sections through
/// registered tables (research R14, FR-053, SC-006).
/// </summary>
public sealed class SectionTableTests : IAsyncLifetime
{
    private const string BuyRoute = "api/v1/shop/buy";
    private const string StartRoute = "api/v1/battle/start";

    private static readonly SectionKey<StringValue> Note = new("note", StringValue.Parser);
    private static readonly SectionKey<SampleWire.BattleState> Battle = new("battle", SampleWire.BattleState.Parser);

    private SdkHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await SdkHarness.StartAsync();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

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

    // Host part (T061): typed keys against the real SampleHost composition.
    [Fact]
    public async Task Framework_keys_read_the_same_message_as_the_id_and_parser_after_login_and_a_section_update()
    {
        var (client, _, _) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);

        client.Sections.Get(FrameworkSections.Profile).ShouldNotBeNull();
        client.Sections.Get(FrameworkSections.Profile).ShouldBe(client.Sections.Get(FrameworkSections.ProfileId, Identity.ProfileState.Parser));

        (await client.SendCommandAsync(
                FrameworkRoutes.CheatGrant,
                new Economy.CheatGrantRequest { Resource = "gold", Quantity = 100 },
                Economy.CheatGrantResult.Parser)).Code.ShouldBe("OK");
        (await client.SendCommandAsync(
                BuyRoute,
                new SampleWire.BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 },
                SampleWire.BuyItemResult.Parser)).Code.ShouldBe("OK");

        client.Sections.Get(FrameworkSections.Wallet)!.Balances["gold"].ShouldBe(40);
        client.Sections.Get(FrameworkSections.Wallet).ShouldBe(client.Sections.Get(FrameworkSections.WalletId, Economy.WalletState.Parser));
        client.Sections.Get(FrameworkSections.Inventory).ShouldNotBeNull();
        client.Sections.Get(FrameworkSections.Inventory).ShouldBe(client.Sections.Get(FrameworkSections.InventoryId, Economy.InventoryState.Parser));
        client.Sections.TryRead(FrameworkSections.WalletId, out var wallet).ShouldBe(SectionRead.Parsed);
        wallet.ShouldBeOfType<Economy.WalletState>().ShouldBe(client.Sections.Get(FrameworkSections.Wallet));
    }

    [Fact]
    public async Task A_game_section_is_unknown_and_raw_until_its_table_is_registered()
    {
        var (client, _, _) = _harness.NewClient();
        var (localId, secret) = SdkHarness.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        var loginRev = client.Sections.RevOf(Battle.Id);

        // The login snapshot already carries the game's sections; a command's update keeps them raw too.
        (await client.SendCommandAsync(StartRoute, new SampleWire.StartBattleRequest { Stage = 1 }, SampleWire.StartBattleResult.Parser))
            .Code.ShouldBe("OK");

        client.Sections.RevOf(Battle.Id).ShouldBeGreaterThan(loginRev);
        client.Sections.IsKnown(Battle.Id).ShouldBeFalse();
        client.Sections.TryRead(Battle.Id, out var raw).ShouldBe(SectionRead.Unknown);
        raw.ShouldBeNull();
        client.Sections.TryGet(Battle.Id, out var stored).ShouldBeTrue();
        client.Sections.All.ShouldContain(stored);

        client.Sections.Register(new SectionTable(Battle));

        client.Sections.IsKnown(Battle.Id).ShouldBeTrue();
        client.Sections.TryRead(Battle.Id, out var parsed).ShouldBe(SectionRead.Parsed);
        parsed.ShouldBeOfType<SampleWire.BattleState>().ShouldBe(client.Sections.Get(Battle));
        client.Sections.Get(Battle).ShouldBe(SampleWire.BattleState.Parser.ParseFrom(stored.Payload));
    }

    // Store part (T060 rules), no host needed.
    [Fact]
    public void A_new_store_knows_the_framework_sections()
    {
        var store = new SectionStore();

        store.IsKnown(FrameworkSections.WalletId).ShouldBeTrue();
        store.IsKnown(FrameworkSections.InventoryId).ShouldBeTrue();
        store.IsKnown(FrameworkSections.ProfileId).ShouldBeTrue();
        store.IsKnown(Note.Id).ShouldBeFalse();
        store.Get(FrameworkSections.Wallet).ShouldBeNull();
        store.TryRead(FrameworkSections.WalletId, out var message).ShouldBe(SectionRead.Absent);
        message.ShouldBeNull();
    }

    [Fact]
    public void Registering_a_game_table_parses_its_sections_and_twice_is_idempotent()
    {
        var store = new SectionStore();
        store.Set(new SdkCore.Section { Id = Note.Id, Rev = 3, Payload = new StringValue { Value = "hi" }.ToByteString() });
        store.TryRead(Note.Id, out _).ShouldBe(SectionRead.Unknown);

        store.Register(new SectionTable(Note));
        store.Register(new SectionTable(Note, new SectionKey<StringValue>("note2", StringValue.Parser)));
        store.Register(FrameworkSections.Table);

        store.Get(Note)!.Value.ShouldBe("hi");
        store.TryRead(Note.Id, out var message).ShouldBe(SectionRead.Parsed);
        message.ShouldBe(new StringValue { Value = "hi" });
        store.IsKnown("note2").ShouldBeTrue();
    }

    [Fact]
    public void Registering_another_message_type_for_a_known_id_throws_and_registers_nothing()
    {
        var store = new SectionStore();
        var conflicting = new SectionTable(
            new SectionKey<StringValue>("fresh", StringValue.Parser),
            new SectionKey<StringValue>(FrameworkSections.WalletId, StringValue.Parser));

        Should.Throw<ArgumentException>(() => store.Register(conflicting)).Message.ShouldContain("'wallet'");

        store.IsKnown("fresh").ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => store.Register(null!));
    }

    [Fact]
    public void Clear_drops_the_sections_but_keeps_the_registered_tables()
    {
        var store = new SectionStore();
        store.Register(new SectionTable(Note));
        store.Set(new SdkCore.Section { Id = Note.Id, Rev = 1, Payload = new StringValue { Value = "x" }.ToByteString() });

        store.Clear();

        store.TryRead(Note.Id, out _).ShouldBe(SectionRead.Absent);
        store.IsKnown(Note.Id).ShouldBeTrue();
    }

    [Fact]
    public void Get_with_id_and_parser_is_unchanged_and_ignores_the_tables()
    {
        var store = new SectionStore();
        store.Set(new SdkCore.Section { Id = "custom", Rev = 1, Payload = new StringValue { Value = "raw" }.ToByteString() });

        store.Get("custom", StringValue.Parser)!.Value.ShouldBe("raw");
        store.Get("missing", StringValue.Parser).ShouldBeNull();
        store.TryRead("custom", out _).ShouldBe(SectionRead.Unknown);
    }
}
