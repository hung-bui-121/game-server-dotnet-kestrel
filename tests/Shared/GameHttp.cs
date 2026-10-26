using GameServer.Modules.Economy;
using GameServer.Modules.Economy.Protocol.V1;
using GameServer.Modules.Iap;
using GameServer.Modules.Iap.Protocol.V1;
using GameServer.Modules.Identity;
using GameServer.Platform.Web.Hosting;
using GameServer.Protocol.Core.V1;
using Google.Protobuf;
using Sample.Counter.Adapters;
using SampleGame.Adapters;
using SampleGame.Protocol.V1;

namespace GameServer.Tests.Shared;

/// <summary>A logged-in SampleGame player talking to one instance (HTTP + Protobuf).</summary>
public sealed class GamePlayer(HttpClient client, string playerId)
{
    public HttpClient Client { get; } = client;

    public string PlayerId { get; } = playerId;

    /// <summary>Same session on another instance (shares the store) — ACC-53 runs on two instances.</summary>
    public GamePlayer On(TestHost host, string token) => new(host.BearerClient(token), PlayerId);

    public Task<HttpResponseMessage> PostAsync(string route, IMessage message, string? requestId = null) =>
        Client.PostProtobufAsync(route, message, requestId ?? ProtobufHttp.NewRequestId());

    public async Task<CommandResponse> CommandAsync(string route, IMessage message, string? requestId = null)
    {
        var response = await PostAsync(route, message, requestId);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.ReadErrorAsync();
            throw new InvalidOperationException($"{route} → {(int)response.StatusCode} {error.Code}");
        }

        return await response.ReadProtobufAsync<CommandResponse>();
    }

    public Task<CommandResponse> CheatAsync(string resource, long quantity) =>
        CommandAsync(CheatModule.GrantRoute, new CheatGrantRequest { Resource = resource, Quantity = quantity });

    public Task<CommandResponse> StartBattleAsync(int stage, string? requestId = null) =>
        CommandAsync(SampleGameModule.StartRoute, new StartBattleRequest { Stage = stage }, requestId);

    public Task<CommandResponse> EndBattleAsync(string battleId, bool won, string? requestId = null) =>
        CommandAsync(SampleGameModule.EndRoute, new EndBattleRequest { BattleId = battleId, Won = won }, requestId);

    public Task<CommandResponse> BuyAsync(string shopItemId, int quantity, string? requestId = null) =>
        CommandAsync(SampleGameModule.BuyRoute, new BuyItemRequest { ShopItemId = shopItemId, Quantity = quantity }, requestId);

    public Task<CommandResponse> IapAsync(string store, string productId, string payload, string? requestId = null) =>
        CommandAsync(IapModule.PurchaseRoute, new IapPurchaseRequest { Store = store, ProductId = productId, ReceiptPayload = payload }, requestId);

    public async Task<SnapshotResponse> SnapshotAsync() =>
        await (await Client.PostProtobufAsync("/api/v1/player/snapshot", new SnapshotRequest())).ReadProtobufAsync<SnapshotResponse>();

    public async Task<GameServer.Modules.Economy.Protocol.V1.WalletState> WalletAsync() =>
        GameServer.Modules.Economy.Protocol.V1.WalletState.Parser.ParseFrom((await SnapshotAsync()).Sections.Single(s => s.Id == EconomyAggregates.Wallet).Payload);

    public async Task<GameServer.Modules.Economy.Protocol.V1.InventoryState> InventoryAsync() =>
        GameServer.Modules.Economy.Protocol.V1.InventoryState.Parser.ParseFrom((await SnapshotAsync()).Sections.Single(s => s.Id == EconomyAggregates.Inventory).Payload);

    public async Task<SampleGame.Protocol.V1.BattleState> BattleAsync() =>
        SampleGame.Protocol.V1.BattleState.Parser.ParseFrom((await SnapshotAsync()).Sections.Single(s => s.Id == "battle").Payload);

    public async Task<SampleGame.Protocol.V1.ProgressionState> ProgressionAsync() =>
        SampleGame.Protocol.V1.ProgressionState.Parser.ParseFrom((await SnapshotAsync()).Sections.Single(s => s.Id == "progression").Payload);
}

public static class GameHttp
{
    /// <summary>All SampleGame modules (Identity, Economy, IAP, SampleGame, Counter, Cheat).</summary>
    public static void FullGame(GameServerBuilder builder) =>
        builder.AddModule<IdentityModule>()
            .AddModule<EconomyModule>()
            .AddModule<IapModule>()
            .AddModule<SampleGameModule>()
            .AddModule<CounterModule>()
            .AddModule<CheatModule>();

    public static Task<TestHost> StartGameAsync(
        Action<Dictionary<string, string?>>? settings = null,
        GameServer.Platform.Testing.InMemoryDocumentStore? store = null,
        GameServer.Platform.Testing.FakeClock? clock = null,
        Action<GameServerBuilder>? extra = null) =>
        TestHost.StartAsync(
            modules: b =>
            {
                FullGame(b);
                extra?.Invoke(b);
            },
            settings: settings,
            store: store,
            clock: clock);

    /// <summary>Logs a new guest in and returns the player plus its token.</summary>
    public static async Task<(GamePlayer Player, string Token)> NewPlayerAsync(this TestHost host)
    {
        var login = await host.Client().LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
        return (new GamePlayer(host.BearerClient(login.SessionToken), login.PlayerId), login.SessionToken);
    }

    /// <summary>Ledger projection documents of a player (ECO-06).</summary>
    public static IReadOnlyList<System.Text.Json.Nodes.JsonObject> Ledger(this TestHost host, string playerId) =>
        host.Store.All(EconomyOperations.LedgerCollection)
            .Select(d => d.Content)
            .Where(d => d["playerId"]?.GetValue<string>() == playerId)
            .ToArray();

    public static long LedgerSum(this TestHost host, string playerId, string resource) =>
        host.Ledger(playerId)
            .Where(d => d["payload"]!["resource"]!.GetValue<string>() == resource)
            .Sum(d => d["payload"]!["delta"]!.GetValue<long>());
}
