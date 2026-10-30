using System.Net;
using System.Security.Cryptography;
using GameServer.Client;
using GameServer.Modules.Economy.Protocol.V1;
using GameServer.Modules.Iap.Protocol.V1;
using SampleGame.Protocol.V1;

namespace GameServer.LoadTest;

/// <summary>Creates SDK clients that share one connection pool. Retries are off: every server answer is measured as is.</summary>
public sealed class ClientFactory(int maxRetries) : IDisposable
{
    public const string GrantRoute = "api/v1/cheat/grant";
    public const string StartBattleRoute = "api/v1/battle/start";
    public const string EndBattleRoute = "api/v1/battle/end";
    public const string BuyRoute = "api/v1/shop/buy";
    public const string PurchaseRoute = "api/v1/iap/purchase";

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 1024,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public GameServerClient Create(Uri baseUrl, ISessionStore? session = null) =>
        new(new GameServerClientOptions(baseUrl, "1.0.0", "loadtest", "loadtest") { MaxRetries = maxRetries },
            new HttpClientTransport(_http),
            session: session);

    public static (string LocalId, byte[] Secret) NewDevice() => ("lt-" + Guid.NewGuid().ToString("N")[..20], RandomNumberGenerator.GetBytes(32));

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// A logged-in player that plays StartBattle → EndBattle, buying a potion pack every third cycle; with
/// <see cref="PurchaseEvery"/> = n it also sends an IAP purchase (fake verifier, unique <c>valid:</c> receipt) every n-th action.
/// </summary>
public sealed class VirtualPlayer(GameServerClient client, InMemorySessionStore? session = null)
{
    private string? _battleId;
    private int _cycle;
    private int _actions;

    public GameServerClient Client => client;

    /// <summary>0 = never; n = every n-th action is an IAP purchase (slice 012, SC-004 measurement).</summary>
    public int PurchaseEvery { get; init; }

    /// <summary>The session, so another client (another instance) can act for the same player.</summary>
    public InMemorySessionStore Session { get; } = session ?? new InMemorySessionStore();

    public static async Task<VirtualPlayer> LoginAsync(ClientFactory factory, Uri baseUrl, long gold, int purchaseEvery = 0)
    {
        var session = new InMemorySessionStore();
        var client = factory.Create(baseUrl, session);
        var (localId, secret) = ClientFactory.NewDevice();
        await client.LoginGuestAsync(localId, secret);
        if (gold > 0)
        {
            await GrantAsync(client, "gold", gold);
        }

        return new VirtualPlayer(client, session) { PurchaseEvery = purchaseEvery };
    }

    /// <summary>Dev cheat grant (setup only); anything but OK aborts, or the run would be a stream of INSUFFICIENT_FUNDS.</summary>
    public static async Task GrantAsync(GameServerClient client, string resource, long quantity)
    {
        var code = (await client.SendCommandAsync(ClientFactory.GrantRoute, new CheatGrantRequest { Resource = resource, Quantity = quantity }, CheatGrantResult.Parser)).Code;
        if (code != "OK")
        {
            throw new InvalidOperationException($"cheat grant {quantity} {resource} failed: {code}");
        }
    }

    /// <summary>Google purchase through the fake verifier (Development hosts only); every receipt is new, so every one grants.</summary>
    public static async Task<string> PurchaseAsync(GameServerClient client) =>
        (await client.SendCommandAsync(
            ClientFactory.PurchaseRoute,
            new IapPurchaseRequest { Store = "google", ProductId = "gems_100", ReceiptPayload = "valid:lt-" + Guid.NewGuid().ToString("N") },
            IapPurchaseResult.Parser)).Code;

    public static async Task<string> BuyAsync(GameServerClient client) =>
        (await client.SendCommandAsync(ClientFactory.BuyRoute, new BuyItemRequest { ShopItemId = "potion_pack", Quantity = 1 }, BuyItemResult.Parser)).Code;

    /// <summary>Runs the next step; latency counts from <paramref name="intendedAt"/>, the scheduled arrival.</summary>
    public async Task StepAsync(Recorder recorder, double intendedAt)
    {
        if (PurchaseEvery > 0 && ++_actions % PurchaseEvery == 0)
        {
            await recorder.TimeAsync("purchase", () => PurchaseAsync(client), intendedAt);
            return;
        }

        if (_battleId is { } battleId)
        {
            _battleId = null;
            await recorder.TimeAsync("end", async () =>
                (await client.SendCommandAsync(ClientFactory.EndBattleRoute, new EndBattleRequest { BattleId = battleId, Won = true }, EndBattleResult.Parser)).Code,
                intendedAt);
            return;
        }

        if (_cycle++ % 3 == 2)
        {
            await recorder.TimeAsync("buy", () => BuyAsync(client), intendedAt);
            return;
        }

        await recorder.TimeAsync("start", async () =>
        {
            var start = await client.SendCommandAsync(ClientFactory.StartBattleRoute, new StartBattleRequest { Stage = 1 }, StartBattleResult.Parser);
            _battleId = start.IsOk ? start.Result?.BattleId : null;
            return start.Code;
        }, intendedAt);
    }

    public long Balance(string resource)
    {
        var wallet = client.Sections.Get("wallet", WalletState.Parser);
        if (wallet != null && wallet.Balances.TryGetValue(resource, out var gold))
        {
            return gold;
        }

        var inventory = client.Sections.Get("inventory", InventoryState.Parser);
        return inventory != null && inventory.Items.TryGetValue(resource, out var items) ? items : 0;
    }
}

/// <summary>Polls <c>/health/ready</c> of every instance and prints each status change (outage drills, ACC-16).</summary>
public sealed class HealthWatcher(IReadOnlyList<Uri> urls, Recorder recorder) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<Uri, string> _last = [];

    public List<string> Transitions { get; } = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            foreach (var url in urls)
            {
                string status;
                try
                {
                    using var response = await _http.GetAsync(new Uri(url, "health/ready"), cancellationToken);
                    status = response.StatusCode == HttpStatusCode.OK ? "ready" : $"not-ready/{(int)response.StatusCode}";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    status = "unreachable/" + ex.GetType().Name;
                }

                if (!_last.TryGetValue(url, out var previous) || previous != status)
                {
                    _last[url] = status;
                    var line = $"[{recorder.Elapsed,6:F1}s] health {url.Authority}: {status}";
                    Transitions.Add(line);
                    Console.WriteLine(line);
                }
            }
        }
        while (await WaitAsync(timer, cancellationToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
