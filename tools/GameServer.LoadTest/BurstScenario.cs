using System.Collections.Concurrent;
using System.Globalization;

namespace GameServer.LoadTest;

/// <summary>
/// ACC-16 burst (table 12.1, TBD-06 assumption): open-loop arrivals at <c>commandRps</c> / <c>loginRps</c> for warm-up,
/// × <c>burstFactor</c> for the burst, then back to base for cool-down. Commands come from a pool of logged-in players
/// (one in flight per player); logins are new guest devices. Run it while stopping Couchbase for the outage drill.
/// </summary>
public sealed class BurstScenario(Options options)
{
    public async Task<int> RunAsync()
    {
        var urls = options.Urls;
        var recorder = new Recorder();
        using var factory = new ClientFactory(options.Count("retries", 0));
        using var health = new HealthWatcher(urls, recorder);
        using var stop = new CancellationTokenSource();
        var healthTask = health.RunAsync(stop.Token);

        var players = options.Count("players", 150);
        var commandRps = options.Number("command-rps", 50);
        var loginRps = options.Number("login-rps", 20);
        var factor = options.Number("burst-factor", 3);
        var warmup = options.Number("warmup-s", 30);
        var burst = options.Number("burst-s", 60);
        var cooldown = options.Number("cooldown-s", 30);
        var window = options.Number("window-s", 5);
        var target = options.Number("p95-target-ms", 300);
        var purchaseEvery = options.Count("purchase-every", 0);
        if (purchaseEvery < 0)
        {
            throw new ArgumentException("--purchase-every must be 0 (off) or a positive number of actions");
        }

        Console.WriteLine($"setup: logging in {players} players across {urls.Count} instance(s)...");
        var idle = new ConcurrentQueue<VirtualPlayer>();
        using (var gate = new SemaphoreSlim(16))
        {
            await Task.WhenAll(Enumerable.Range(0, players).Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    idle.Enqueue(await VirtualPlayer.LoginAsync(factory, urls[i % urls.Count], gold: 1_000_000, purchaseEvery));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"setup login failed: {Outcomes.Of(ex)}");
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        Console.WriteLine($"setup: {idle.Count} players ready. Phases: warmup {warmup}s @ {commandRps}/{loginRps} rps, " +
            $"burst {burst}s × {factor}, cooldown {cooldown}s. Window = {window}s.");

        if (options.Get("ready-file") is { } readyFile)
        {
            File.WriteAllText(readyFile, "ready");   // couchbase-outage.ps1 times the outage from here
        }

        var start = recorder.Elapsed;
        var total = warmup + burst + cooldown;
        double RateFactor(double t) => t >= warmup && t < warmup + burst ? factor : 1;
        string PhaseAt(double t) => t < warmup ? "warmup" : t < warmup + burst ? "burst" : "cooldown";

        var inFlight = new ConcurrentDictionary<int, Task>();
        var nextId = 0;
        void Track(Task task)
        {
            var id = Interlocked.Increment(ref nextId);
            inFlight[id] = task;
            _ = task.ContinueWith(_ => inFlight.TryRemove(id, out Task? _), TaskScheduler.Default);
        }

        var loginUrl = 0;
        double commandsDue = 0, loginsDue = 0, lastT = 0, nextWindow = window;
        var commandsFired = 0L;
        var loginsFired = 0L;
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        while (await ticker.WaitForNextTickAsync())
        {
            var t = recorder.Elapsed - start;
            if (t >= total)
            {
                break;
            }

            recorder.Phase = PhaseAt(t);
            var dt = t - lastT;
            lastT = t;
            commandsDue += dt * commandRps * RateFactor(t);
            loginsDue += dt * loginRps * RateFactor(t);

            // Arrivals are scheduled on this tick (5 ms granularity): latency counts from here, including client queuing.
            var due = recorder.Elapsed;
            for (; commandsFired < (long)commandsDue; commandsFired++)
            {
                if (!idle.TryDequeue(out var player))
                {
                    recorder.Drop();
                    continue;
                }

                Track(Task.Run(async () =>
                {
                    await player.StepAsync(recorder, due);
                    idle.Enqueue(player);
                }));
            }

            for (; loginsFired < (long)loginsDue; loginsFired++)
            {
                var url = urls[loginUrl++ % urls.Count];
                Track(Task.Run(() => recorder.TimeAsync("login", async () =>
                {
                    var client = factory.Create(url);
                    var (localId, secret) = ClientFactory.NewDevice();
                    await client.LoginGuestAsync(localId, secret);
                    return "OK";
                }, due)));
            }

            // A window is printed one window after it closes, so calls started in it have (mostly) finished.
            if (t >= nextWindow + window)
            {
                PrintWindow(recorder, start, nextWindow, window, inFlight.Count, PhaseAt(nextWindow - 0.001));
                nextWindow += window;
            }
        }

        Console.WriteLine($"draining {inFlight.Count} in-flight calls...");
        await Task.WhenAll(inFlight.Values);
        for (; nextWindow <= total + 0.001; nextWindow += window)
        {
            PrintWindow(recorder, start, nextWindow, window, 0, PhaseAt(nextWindow - 0.001));
        }

        await stop.CancelAsync();
        await healthTask;

        var result = Report(recorder, health, target);
        var verify = options.Count("verify-players", 20);
        return verify > 0 && !await VerifyAsync(factory, idle.Take(verify).ToArray()) ? 1 : result;
    }

    private static void PrintWindow(Recorder recorder, double start, double end, double window, int inFlight, string phase)
    {
        Console.WriteLine($"[{end,6:F1}s] {phase} - window {end - window:F0}..{end:F0}s, in flight {inFlight}");
        Console.Write(recorder.Window(start + end - window, start + end));
    }

    /// <summary>
    /// After the run (and any drill): wallet/inventory read from the server must equal the sum of the player's ledger —
    /// a lost or doubled grant, or a receipt without its state, shows up here. Uses whichever instance is still ready.
    /// </summary>
    private async Task<bool> VerifyAsync(ClientFactory factory, VirtualPlayer[] players)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        Uri? live = null;
        foreach (var url in options.Urls)
        {
            try
            {
                using var response = await probe.GetAsync(new Uri(url, "health/ready"));
                if (response.IsSuccessStatusCode)
                {
                    live = url;
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        if (live is null)
        {
            Console.WriteLine("verify: no instance is ready - skipped.");
            return false;
        }

        var wait = TimeSpan.FromSeconds(options.Number("verify-wait-s", 90));
        using var gm = await GmClient.LoginAsync(live, options.Get("gm-staff") ?? "admin", GmClient.DevPassword(), wait);
        var problems = await Task.WhenAll(players.Select(async player =>
        {
            try
            {
                var reader = new VirtualPlayer(factory.Create(live, player.Session));
                await reader.Client.RefreshSectionsAsync();
                var ledger = await gm.LedgerTotalsAsync(player.Session.PlayerId!, reader.Balance("gold"), reader.Balance("potion"), wait);
                return ledger == null ? null : $"{player.Session.PlayerId}: {ledger}";
            }
            catch (Exception ex)
            {
                return $"{player.Session.PlayerId}: verification failed {Outcomes.Of(ex)}";
            }
        }));

        foreach (var problem in problems.Where(p => p != null))
        {
            Console.WriteLine("verify FAIL " + problem);
        }

        var failed = problems.Count(p => p != null);
        Console.WriteLine($"verify: {players.Length - failed}/{players.Length} players have ledger = wallet/inventory (via {live.Authority}).");
        return failed == 0;
    }

    private int Report(Recorder recorder, HealthWatcher health, double target)
    {
        var measured = recorder.Samples.Where(s => s.Phase is "warmup" or "burst" or "cooldown").ToArray();
        Console.WriteLine();
        Console.WriteLine("phase     op        n     rps   p50ms   p95ms   p99ms   maxms  outcomes");
        var commandP95Burst = double.NaN;
        foreach (var phase in new[] { "warmup", "burst", "cooldown" })
        {
            var inPhase = measured.Where(s => s.Phase == phase).ToArray();
            if (inPhase.Length == 0)
            {
                continue;
            }

            var seconds = Math.Max(0.001, inPhase.Max(s => s.AtSeconds) - inPhase.Min(s => s.AtSeconds));
            var rows = inPhase.GroupBy(s => s.Operation).Select(g => (Key: g.Key, Items: g.ToArray())).ToList();
            rows.Add(("commands", inPhase.Where(s => s.Operation is "start" or "end" or "buy" or Recorder.DroppedOperation).ToArray()));
            foreach (var (key, items) in rows.Where(r => r.Items.Length > 0).OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                var served = items.Where(Recorder.Answered).Select(s => s.LatencyMs).Order().ToArray();
                var p95 = Recorder.Percentile(served, 0.95);
                if (phase == "burst" && key == "commands")
                {
                    commandP95Burst = p95;
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{phase,-9} {key,-8} {items.Length,6} {items.Length / seconds,6:F1} {Recorder.Percentile(served, 0.5),7:F1} {p95,7:F1} {Recorder.Percentile(served, 0.99),7:F1} {(served.Length == 0 ? double.NaN : served[^1]),7:F1}  {Recorder.Codes(items)}"));
            }
        }

        var dropped = measured.Count(s => s.Outcome == Recorder.ClientSaturated);
        var unanswered = measured.Count(s => s.Outcome != Recorder.ClientSaturated && !Recorder.Answered(s));
        Console.WriteLine();
        var scope = "burst";
        if (double.IsNaN(commandP95Burst))
        {
            // No burst phase (outage drill runs at base rate): judge the whole run.
            scope = "whole run";
            commandP95Burst = Recorder.Percentile(
                measured.Where(s => s.Operation is "start" or "end" or "buy" && Recorder.Answered(s)).Select(s => s.LatencyMs).Order().ToArray(), 0.95);
        }

        Console.WriteLine($"commands p95 during {scope} = {commandP95Burst:F1} ms (target ≤ {target} ms) → {(commandP95Burst <= target ? "PASS" : "FAIL")}");
        if (unanswered > 0)
        {
            Console.WriteLine($"note: percentiles exclude {unanswered} calls without a server answer (NETWORK_ERROR: instance down or " +
                "unreachable - locally there is no load balancer to take a stopped instance out).");
        }

        var maxDropped = options.Count("max-dropped", 0);
        if (dropped > maxDropped)
        {
            Console.WriteLine($"FAIL: client saturated - {dropped} command arrivals dropped because every virtual player was busy " +
                $"(allowed {maxDropped}); the offered load was not delivered. Raise --players.");
        }

        Console.WriteLine($"health transitions: {health.Transitions.Count}");
        if (options.Get("out") is { } path)
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { samples = measured, health = health.Transitions }));
            Console.WriteLine($"samples written to {path}");
        }

        return commandP95Burst <= target && dropped <= maxDropped ? 0 : 1;
    }
}
