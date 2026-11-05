using System.Diagnostics;
using GameServer.Modules.Gm;
using Shouldly;

namespace GameServer.Gm.Tests;

/// <summary>
/// Slice 017 T049 (SC-004): the catalog route answers the first request (document generated) within 1 s and warm requests (bytes
/// kept) within 5 ms p95, in process. Opt-in (<c>GAMESERVER_PERF=1</c>): timing budgets are not reliable inside the parallel suite.
/// </summary>
public sealed class GmCatalogPerformanceTests(ITestOutputHelper output)
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the GM catalog route (SC-004: first <= 1 s, warm p95 <= 5 ms).";
    private const int WarmCalls = 1000;

    [Fact]
    public async Task The_catalog_is_generated_once_and_served_warm_within_budget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        await using var host = await GmTestHost.StartAsync();
        var staff = await host.GmClientAsync("admin");

        var watch = Stopwatch.StartNew();
        (await staff.GetAsync(GmModule.ConsoleCatalogRoute)).EnsureSuccessStatusCode();
        var first = watch.Elapsed;

        var warm = new double[WarmCalls];
        for (var i = 0; i < WarmCalls; i++)
        {
            watch.Restart();
            using var response = await staff.GetAsync(GmModule.ConsoleCatalogRoute);
            await response.Content.ReadAsByteArrayAsync();
            warm[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(warm);
        var p95 = warm[(int)Math.Ceiling(WarmCalls * 0.95) - 1];
        output.WriteLine($"GM catalog: first {first.TotalMilliseconds:F1} ms, warm p50 {warm[WarmCalls / 2]:F3} ms, p95 {p95:F3} ms.");
        first.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
        p95.ShouldBeLessThanOrEqualTo(5);
    }
}
