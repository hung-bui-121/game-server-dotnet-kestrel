extern alias sdk;

using GameServer.Modules.Economy;
using GameServer.Platform.Commands;
using GameServer.Tests.Shared;
using sdk::GameServer.Client;
using SampleGame.Adapters;
using Shouldly;
using EconomyWire = GameServer.Modules.Economy.Protocol.V1;
using SampleWire = SampleGame.Protocol.V1;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T061 (FR-042, FR-043, FR-047, SC-007, US5-3, US5-5, contracts/sdk-pending-store.md §5): the app dies at each row of the
/// crash table; a new client on the same folder logs in, lists the entry and resends it, and the purchase runs exactly once (a commit
/// answers replayed with the original receipt).
/// </summary>
public sealed class PendingCommandEndToEndTests : IAsyncLifetime
{
    private const string Item = "potion_pack";
    private const long StartingGold = 1000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-pending-e2e-" + Guid.NewGuid().ToString("N"));
    private SdkHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await SdkHarness.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Dies_before_the_record_finished_lists_nothing_and_nothing_ran()
    {
        var device = SdkHarness.NewDevice();
        var playerId = await FundedPlayerAsync(device);
        var dying = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder, new FaultyPendingOps { FailAt = PendingStep.Move }));
        dying.Transport.Before = (_, _) => throw new TransportException("the app died before sending");
        await Should.ThrowAsync<GameServerException>(() => BuyAsync(dying.Client));
        File.WriteAllBytes(Path.Combine(_folder, dying.Client.PendingCommands[0].RequestId + ".pending.tmp"), [1, 2, 3]);

        var restarted = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));

        restarted.Client.PendingCommands.ShouldBeEmpty();
        Directory.GetFiles(_folder).ShouldBeEmpty();
        Purchases(playerId).ShouldBe(0);
    }

    [Fact]
    public async Task Dies_after_the_record_before_the_request_left_executes_once_on_resend()
    {
        var device = SdkHarness.NewDevice();
        var playerId = await FundedPlayerAsync(device);
        var dying = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));
        dying.Transport.Before = (_, _) => throw new TransportException("no network");
        await Should.ThrowAsync<GameServerException>(() => BuyAsync(dying.Client));
        Purchases(playerId).ShouldBe(0);

        var result = await ResendAfterRestartAsync(device);

        result.IsOk.ShouldBeTrue();
        result.Replayed.ShouldBeFalse();
        Purchases(playerId).ShouldBe(1);
        Gold(playerId).ShouldBe(StartingGold - result.Result!.PricePaid);
    }

    [Fact]
    public async Task Dies_after_the_commit_before_the_response_replays_the_original_receipt()
    {
        var device = SdkHarness.NewDevice();
        var playerId = await FundedPlayerAsync(device);
        var dying = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));
        dying.Transport.After = (_, _, _) => throw new TransportException("the app died before the response");
        await Should.ThrowAsync<GameServerException>(() => BuyAsync(dying.Client));
        Purchases(playerId).ShouldBe(1);
        var committed = ReceiptOf(playerId);

        var result = await ResendAfterRestartAsync(device);

        result.IsOk.ShouldBeTrue();
        result.Replayed.ShouldBeTrue();
        result.ReceiptId.ShouldBe(committed);
        Purchases(playerId).ShouldBe(1);
        Gold(playerId).ShouldBe(StartingGold - result.Result!.PricePaid);
    }

    [Fact]
    public async Task Dies_after_the_response_before_the_remove_replays_on_resend()
    {
        var device = SdkHarness.NewDevice();
        var playerId = await FundedPlayerAsync(device);
        var dying = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder, new FaultyPendingOps { FailAt = PendingStep.Delete }));
        var first = await BuyAsync(dying.Client);
        first.IsOk.ShouldBeTrue();
        dying.Client.PendingCommands.ShouldBeEmpty("memory first: the entry is gone from this client");

        var result = await ResendAfterRestartAsync(device);

        result.Replayed.ShouldBeTrue();
        result.ReceiptId.ShouldBe(first.ReceiptId);
        Purchases(playerId).ShouldBe(1);
    }

    [Fact]
    public async Task Dies_after_the_remove_lists_nothing()
    {
        var device = SdkHarness.NewDevice();
        var playerId = await FundedPlayerAsync(device);
        var alive = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));
        (await BuyAsync(alive.Client)).IsOk.ShouldBeTrue();

        var restarted = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));

        restarted.Client.PendingCommands.ShouldBeEmpty();
        Purchases(playerId).ShouldBe(1);
    }

    [Fact]
    public async Task An_entry_of_another_player_is_refused()
    {
        var device = SdkHarness.NewDevice();
        await FundedPlayerAsync(device);
        var dying = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));
        dying.Transport.Before = (_, _) => throw new TransportException("no network");
        await Should.ThrowAsync<GameServerException>(() => BuyAsync(dying.Client));

        var other = await LoggedInAsync(SdkHarness.NewDevice(), new PersistentPendingCommandStore(_folder));
        var entry = other.Client.PendingCommands.ShouldHaveSingleItem();

        other.Client.StatusOf(entry).ShouldBe(PendingCommandStatus.OtherPlayer);
        await Should.ThrowAsync<InvalidOperationException>(() => other.Client.ResendPendingAsync(entry, SampleWire.BuyItemResult.Parser));
    }

    private async Task<CommandResult<SampleWire.BuyItemResult>> ResendAfterRestartAsync((string LocalId, byte[] Secret) device)
    {
        var restarted = await LoggedInAsync(device, new PersistentPendingCommandStore(_folder));
        restarted.Client.PendingCommands.ShouldHaveSingleItem();
        var entry = restarted.Client.PendingCommands[0];
        restarted.Client.StatusOf(entry).ShouldBe(PendingCommandStatus.Resendable);

        var result = await restarted.Client.ResendPendingAsync(entry, SampleWire.BuyItemResult.Parser);

        restarted.Client.PendingCommands.ShouldBeEmpty();
        return result;
    }

    private static Task<CommandResult<SampleWire.BuyItemResult>> BuyAsync(GameServerClient client) =>
        client.SendCommandAsync(SampleGameModule.BuyRoute, new SampleWire.BuyItemRequest { ShopItemId = Item, Quantity = 1 }, SampleWire.BuyItemResult.Parser, GameServerClient.NewRequestId());

    /// <summary>A player with gold, funded by a client without a store (so the grant is no pending entry).</summary>
    private async Task<string> FundedPlayerAsync((string LocalId, byte[] Secret) device)
    {
        var (client, _, _) = _harness.NewClient();
        var login = await client.LoginGuestAsync(device.LocalId, device.Secret);
        (await client.SendCommandAsync(CheatModule.GrantRoute, new EconomyWire.CheatGrantRequest { Resource = "gold", Quantity = StartingGold }, EconomyWire.CheatGrantResult.Parser))
            .IsOk.ShouldBeTrue();
        return login.PlayerId;
    }

    /// <summary>A new client (as after an app start) on the folder's store, logged in as the device's player.</summary>
    private async Task<(GameServerClient Client, ScriptedTransport Transport)> LoggedInAsync((string LocalId, byte[] Secret) device, IPendingCommandStore store)
    {
        var (client, transport, _) = _harness.NewClient(pending: store);
        await client.LoginGuestAsync(device.LocalId, device.Secret);
        return (client, transport);
    }

    private int Purchases(string playerId) =>
        _harness.Host.Store.All(ReceiptDocument.Collection).Select(d => d.Content)
            .Count(r => r["playerId"]!.GetValue<string>() == playerId && r["operation"]!.GetValue<string>() == "shop.buy");

    private string ReceiptOf(string playerId) =>
        _harness.Host.Store.All(ReceiptDocument.Collection).Select(d => d.Content)
            .Single(r => r["playerId"]!.GetValue<string>() == playerId && r["operation"]!.GetValue<string>() == "shop.buy")["receiptId"]!.GetValue<string>();

    private long Gold(string playerId) => _harness.Host.LedgerSum(playerId, "gold");
}
