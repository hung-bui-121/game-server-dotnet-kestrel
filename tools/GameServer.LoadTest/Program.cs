using System.Globalization;
using GameServer.LoadTest;

// Local load / concurrency driver for a SampleHost running on Couchbase. See docs/guides/local-real-env-testing.md.
//   burst       ACC-16: warm-up, burst xN, cool-down; per-window console timeline + phase table; exit 1 if burst p95 > target
//   contention  ACC-06: two simultaneous Buy for one player on two instances; checks wallet, inventory and ledger
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        usage: GameServer.LoadTest <burst|contention> --urls http://localhost:5080/,http://localhost:5081/ [options]
          burst:      --players 150 --command-rps 50 --login-rps 20 --burst-factor 3 --warmup-s 30 --burst-s 60
                      --cooldown-s 30 --window-s 5 --p95-target-ms 300 --max-dropped 0 --retries 0 --out samples.json
                      --verify-players 20 --verify-wait-s 90   (ledger = wallet/inventory after the run)
                      --ready-file <path>                       (written when setup is done; used by couchbase-outage.ps1)
                      --purchase-every 0                        (n > 0: every n-th action of a player is an IAP purchase,
                                                                 fake verifier, Development hosts; reported as op "purchase")
          contention: --rounds 20 --parallel 5 --retries 0
          both:       --gm-staff admin; GM password from env GM_DEV_PASSWORD (default: the Development seed)
        """);
    return 2;
}

string[] common = ["urls", "retries", "gm-staff"];
string[]? allowed = args[0] switch
{
    "burst" => [.. common, "players", "command-rps", "login-rps", "burst-factor", "warmup-s", "burst-s", "cooldown-s", "window-s",
        "p95-target-ms", "max-dropped", "out", "verify-players", "verify-wait-s", "ready-file", "purchase-every"],
    "contention" => [.. common, "rounds", "parallel"],
    _ => null,
};
Options options;
try
{
    options = allowed is null ? throw new ArgumentException($"unknown scenario '{args[0]}' (burst | contention)") : Options.Parse(args.Skip(1).ToArray(), allowed);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
{
    await Console.Error.WriteLineAsync("error: " + ex.Message);
    return 2;
}

return args[0] == "burst" ? await new BurstScenario(options).RunAsync() : await new ContentionScenario(options).RunAsync();

namespace GameServer.LoadTest
{
    public sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public IReadOnlyList<Uri> Urls { get; private set; } = [new Uri("http://localhost:5080/")];

        /// <summary>Parses <c>--name value</c> pairs; unknown names and a name without a value are errors (no silent defaults).</summary>
        public static Options Parse(string[] args, IReadOnlyCollection<string> allowed)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i += 2)
            {
                var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
                if (name is null || !allowed.Contains(name))
                {
                    throw new ArgumentException($"unknown option '{args[i]}'; allowed: --{string.Join(", --", allowed)}");
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"option '{args[i]}' needs a value");
                }

                options._values[name] = args[i + 1];
            }

            if (options.Get("urls") is { } urls)
            {
                options.Urls = urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(u => new Uri(u.EndsWith('/') ? u : u + "/"))
                    .ToArray();
            }

            return options;
        }

        public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

        public int Count(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        public double Number(string name, double fallback) => Get(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
    }
}
