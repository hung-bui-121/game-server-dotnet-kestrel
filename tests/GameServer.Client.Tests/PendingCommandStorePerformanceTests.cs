extern alias sdk;

using System.Diagnostics;
using System.Security.Cryptography;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T061 (SC-008, VERIFY V8 final): record (temp write + device flush + rename) and remove (delete) of a 4 KiB entry each
/// take at most 10 ms p95, measured in the temp folder of the user profile (C:, the drive of <c>persistentDataPath</c> on Windows).
/// </summary>
public sealed class PendingCommandStorePerformanceTests : IDisposable
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the pending-command store (SC-008: 4 KiB record and remove p95 <= 10 ms).";
    private const int WarmUp = 20;
    private const int Iterations = 200;
    private const int BodyBytes = 4 * 1024;
    private const double BudgetMs = 10;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-pending-perf-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public PendingCommandStorePerformanceTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Record_and_remove_of_a_4_kib_entry_stay_within_10_ms_p95()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        var store = new PersistentPendingCommandStore(_folder);
        var body = RandomNumberGenerator.GetBytes(BodyBytes);
        var record = new List<double>(Iterations);
        var remove = new List<double>(Iterations);

        for (var i = 0; i < WarmUp + Iterations; i++)
        {
            var entry = new PendingCommand(GameServerClient.NewRequestId(), "p_perf", "api/v1/shop/buy", body, DateTimeOffset.UtcNow);
            var watch = Stopwatch.StartNew();
            store.Record(entry);
            var recorded = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            store.Remove(entry.RequestId);
            var removed = watch.Elapsed.TotalMilliseconds;
            store.WriteProblem.ShouldBeNull();
            if (i >= WarmUp)
            {
                record.Add(recorded);
                remove.Add(removed);
            }
        }

        var recordP95 = P95(record);
        var removeP95 = P95(remove);
        _output.WriteLine($"folder {_folder} (drive {Path.GetPathRoot(_folder)}): record p50 {P50(record):F2} / p95 {recordP95:F2} ms, remove p50 {P50(remove):F2} / p95 {removeP95:F2} ms");
        recordP95.ShouldBeLessThanOrEqualTo(BudgetMs);
        removeP95.ShouldBeLessThanOrEqualTo(BudgetMs);
    }

    private static double P95(List<double> samples) => samples.Order().ElementAt((int)Math.Ceiling(samples.Count * 0.95) - 1);

    private static double P50(List<double> samples) => samples.Order().ElementAt((samples.Count / 2) - 1);
}
