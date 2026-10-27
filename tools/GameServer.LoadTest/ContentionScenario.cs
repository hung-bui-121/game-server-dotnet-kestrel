using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameServer.Client;

namespace GameServer.LoadTest;

/// <summary>
/// ACC-06: two BuyVirtualItem for the same player sent at the same moment to two instances, with gold for exactly one.
/// Expect one OK and one INSUFFICIENT_FUNDS, final wallet/inventory consistent, and the ledger projection (read through
/// the GM API) summing to the same balances. Rounds run <c>parallel</c> players at once (different players must not
/// block each other).
/// </summary>
public sealed class ContentionScenario(Options options)
{
    private const long Price = 60;

    public async Task<int> RunAsync()
    {
        var urls = options.Urls;
        var first = urls[0];
        var second = urls.Count > 1 ? urls[1] : urls[0];
        if (urls.Count < 2)
        {
            Console.WriteLine("warning: one URL given — both requests go to the same instance (per-player limiter serialises them).");
        }

        var rounds = options.Count("rounds", 20);
        var parallel = options.Count("parallel", 5);
        using var factory = new ClientFactory(options.Count("retries", 0));
        using var gm = await GmClient.LoginAsync(first, options.Get("gm-staff") ?? "admin", GmClient.DevPassword(), TimeSpan.FromSeconds(10));

        var failures = 0;
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var batch = 0; batch < rounds; batch += parallel)
        {
            var results = await Task.WhenAll(Enumerable.Range(batch, Math.Min(parallel, rounds - batch)).Select(i => RoundAsync(i, factory, gm, first, second)));
            foreach (var (pair, problem) in results)
            {
                outcomes[pair] = outcomes.GetValueOrDefault(pair) + 1;
                if (problem != null)
                {
                    failures++;
                    Console.WriteLine("  FAIL " + problem);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"rounds {rounds}, outcomes: {string.Join(", ", outcomes.Select(o => $"[{o.Key}]×{o.Value}"))}");
        Console.WriteLine(failures == 0 ? "ACC-06: PASS" : $"ACC-06: FAIL ({failures} round(s))");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<(string Pair, string? Problem)> RoundAsync(int round, ClientFactory factory, GmClient gm, Uri first, Uri second)
    {
        var a = factory.Create(first);
        var (localId, secret) = ClientFactory.NewDevice();
        var login = await a.LoginGuestAsync(localId, secret);
        var player = new VirtualPlayer(a);
        var startGold = player.Balance("gold");
        if (startGold > Price)
        {
            return ("setup", $"round {round} {login.PlayerId}: starting gold {startGold} > price {Price} - update the scenario to the current config");
        }

        if (startGold < Price)
        {
            await VirtualPlayer.GrantAsync(a, "gold", Price - startGold);
        }

        var potionsBefore = player.Balance("potion");
        var b = factory.Create(second, new InMemorySessionStore { Token = login.SessionToken, PlayerId = login.PlayerId });

        using var go = new SemaphoreSlim(0);
        async Task<string> BuyAsync(GameServerClient client)
        {
            await go.WaitAsync();
            try
            {
                return await VirtualPlayer.BuyAsync(client);
            }
            catch (Exception ex)
            {
                return Outcomes.Of(ex);
            }
        }

        var buys = new[] { BuyAsync(a), BuyAsync(b) };
        go.Release(2);
        var codes = await Task.WhenAll(buys);
        var pair = string.Join('+', codes.Order(StringComparer.Ordinal));

        await a.RefreshSectionsAsync();
        var gold = player.Balance("gold");
        var potions = player.Balance("potion");
        var expected = codes.Count(c => c == "OK") == 1 && codes.Count(c => c == "INSUFFICIENT_FUNDS") == 1;
        string? problem = null;
        if (!expected)
        {
            problem = $"round {round} {login.PlayerId}: codes {pair} (want OK + INSUFFICIENT_FUNDS)";
        }
        else if (gold != 0 || potions != potionsBefore + 1)
        {
            problem = $"round {round} {login.PlayerId}: gold {gold} (want 0), potion {potions} (want {potionsBefore + 1})";
        }
        else
        {
            var ledger = await gm.LedgerTotalsAsync(login.PlayerId, expectGold: 0, expectPotion: potions, TimeSpan.FromSeconds(5));
            if (ledger != null)
            {
                problem = $"round {round} {login.PlayerId}: ledger {ledger}";
            }
        }

        Console.WriteLine($"round {round,3} {login.PlayerId}: {pair}, gold {gold}, potion {potionsBefore}→{potions}{(problem == null ? " ok" : string.Empty)}");
        return (pair, problem);
    }
}

/// <summary>Minimal GM JSON client (cookie session) used to read the ledger projection.</summary>
public sealed class GmClient : IDisposable
{
    private readonly HttpClient _http;

    private GmClient(HttpClient http) => _http = http;

    /// <summary>GM dev seed password: env <c>GM_DEV_PASSWORD</c>, else the committed Development seed (never a CLI argument).</summary>
    public static string DevPassword() => Environment.GetEnvironmentVariable("GM_DEV_PASSWORD") ?? "dev-only-change-me";

    /// <summary>Logs in, retrying for up to <paramref name="wait"/> (the store may still be recovering after a drill).</summary>
    public static async Task<GmClient> LoginAsync(Uri baseUrl, string staffId, string password, TimeSpan wait)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var http = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() }) { BaseAddress = baseUrl };
            string failure;
            try
            {
                using var response = await http.PostAsJsonAsync("gm/v1/auth/login", new { staffId, password });
                if (response.IsSuccessStatusCode)
                {
                    return new GmClient(http);
                }

                failure = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
            }
            catch (HttpRequestException ex)
            {
                failure = ex.Message;
            }
            catch (TaskCanceledException)
            {
                failure = "timeout";
            }

            http.Dispose();
            if (watch.Elapsed >= wait)
            {
                throw new InvalidOperationException($"GM login failed: {failure}");
            }

            await Task.Delay(1000);
        }
    }

    /// <summary>
    /// Sums ledger deltas per resource; returns null when they match, otherwise a description. Polls up to
    /// <paramref name="wait"/> because the ledger is an outbox projection (inline relay, or the sweeper after a crash).
    /// </summary>
    public async Task<string?> LedgerTotalsAsync(string playerId, long expectGold, long expectPotion, TimeSpan wait)
    {
        string? last = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var first = true; first || watch.Elapsed < wait; first = false)
        {
            Dictionary<string, long> totals;
            try
            {
                totals = await ReadTotalsAsync(playerId);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // Query/index service may still be warming up after an outage: keep polling until the wait ends.
                last = "ledger read failed: " + ex.GetType().Name;
                await Task.Delay(500);
                continue;
            }

            var gold = totals.GetValueOrDefault("gold");
            var potion = totals.GetValueOrDefault("potion");
            if (gold == expectGold && potion == expectPotion)
            {
                return null;
            }

            last = $"gold Σ{gold} (wallet {expectGold}), potion Σ{potion} (inventory {expectPotion})";
            await Task.Delay(500);
        }

        return last;
    }

    private async Task<Dictionary<string, long>> ReadTotalsAsync(string playerId)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await _http.GetFromJsonAsync<JsonObject>(
                $"gm/v1/players/{playerId}/ledger?pageSize=200{(cursor == null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}")
                ?? throw new InvalidOperationException("empty ledger page");
            foreach (var item in page["items"]?.AsArray() ?? [])
            {
                var payload = item?["payload"];
                var resource = payload?["resource"]?.GetValue<string>() ?? "?";
                totals[resource] = totals.GetValueOrDefault(resource) + (payload?["delta"]?.GetValue<long>() ?? 0);
            }

            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(cursor));
        return totals;
    }

    public void Dispose() => _http.Dispose();
}
