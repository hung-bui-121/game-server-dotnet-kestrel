using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GameServer.Platform.Registry;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace GameServer.Platform.Web.Tests;

/// <summary>
/// SC-007 (slice 015 T050): the config phase (load, registry checks, rules, catalogs, bundle and its size check) stays within its
/// startup budget: the sample config against five times its budget, 1 MiB of row tables against its own. Both are opt-in
/// (<c>GAMESERVER_PERF=1</c>): timing budgets are not reliable in a normal test run (slice 018 review r1: the sample-config median
/// measured 12–130 ms over 13 runs of this test alone and 3–33 ms inside its project, and failed once in each setting).
/// </summary>
public sealed class ConfigPhasePerformanceTests(ITestOutputHelper output)
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the config phase (SC-007: sample config <= 5 x 20 ms, 1 MiB of row tables <= 300 ms).";
    private const double SampleBudgetMs = 20;

    /// <summary>The sample-config budget is checked against five times its value (a developer machine, not a quiet bench).</summary>
    private const double SampleMargin = 5;
    private const double LargeBudgetMs = 300;

    /// <summary>The first phase of a process pays the JIT of the loader, the rules and the catalogs; a start pays it once.</summary>
    private const int WarmUpStarts = 1;

    private const int MeasuredStarts = 3;

    [Fact]
    public void The_phase_on_the_sample_config_stays_within_the_budget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        var path = Path.Combine(RepositoryRoot(), "samples", "SampleHost", "config");

        var median = MedianPhaseMs(s => s["GameServer:ConfigPath"] = path);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"config phase, samples/SampleHost/config: median {median:F1} ms"));
        median.ShouldBeLessThanOrEqualTo(SampleBudgetMs * SampleMargin);
    }

    [Fact]
    public void The_phase_on_1_mib_of_row_tables_stays_within_the_budget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        var resources = LargeResources(1024 * 1024);
        using var seeded = new SeededConfig().With("resources.json", resources);

        var median = MedianPhaseMs(s =>
        {
            seeded.Apply(s);
            s["GameServer:Limits:ConfigBundleBytes"] = (4 * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
        });

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"config phase, resources.json {Encoding.UTF8.GetByteCount(resources)} B: median {median:F1} ms"));
        median.ShouldBeLessThanOrEqualTo(LargeBudgetMs);
    }

    /// <summary>
    /// Runs exactly what the first block of <c>UseGameServer</c> runs, on a freshly built host per start (the phase publishes once
    /// per host), with the sample game modules (economy, IAP and sample game rules and catalogs).
    /// </summary>
    private static double MedianPhaseMs(Action<Dictionary<string, string?>> settings)
    {
        var samples = new List<double>();
        for (var start = 0; start < WarmUpStarts + MeasuredStarts; start++)
        {
            var app = SampleTestHost.BuildApp(GameHttp.FullGame, settings);
            try
            {
                var services = app.Services;
                var errors = new List<string>();
                var watch = Stopwatch.StartNew();
                ConfigStartup.Run(services, services.GetRequiredService<GameServerOptions>(), services.GetRequiredService<ModuleRegistry>(), errors);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                errors.ShouldBeEmpty();
                if (start >= WarmUpStarts)
                {
                    samples.Add(elapsed);
                }
            }
            finally
            {
                ((IDisposable)app).Dispose();
            }
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>A valid <c>resources.json</c> of about <paramref name="bytes"/> bytes, keeping the rows the shared iap/shop/battle files name.</summary>
    private static string LargeResources(int bytes)
    {
        var rows = new JsonArray(
            Row("gold", "currency", 1_000_000_000, "Gold"),
            Row("gem", "currency", 1_000_000, "Gem"),
            Row("potion", "item", 999, "Potion"),
            Row("sword", "item", 10, "Sword"));
        var length = rows.ToJsonString().Length;
        for (var i = 0; length < bytes; i++)
        {
            var row = Row($"item_{i:D6}", i % 2 == 0 ? "item" : "currency", 1 + (i % 5000), $"Generated item {i:D6} of the balance table");
            length += row.ToJsonString().Length + 1;
            rows.Add(row);
        }

        return new JsonObject { ["resources"] = rows }.ToJsonString();
    }

    private static JsonObject Row(string id, string kind, long maxQuantity, string displayName) =>
        new() { ["id"] = id, ["kind"] = kind, ["maxQuantity"] = maxQuantity, ["displayName"] = displayName };

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"GameServer.slnx not found above {AppContext.BaseDirectory}.");
    }
}
