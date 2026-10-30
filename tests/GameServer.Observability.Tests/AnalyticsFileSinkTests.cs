using System.Text;
using System.Text.Json;
using GameServer.Platform;
using GameServer.Platform.Analytics;
using GameServer.Platform.Observability.Analytics;
using GameServer.Platform.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>File names, UTC day roll, append and repair, single writer, write failures and retention (T040, US4-1..US4-5, SC-005, SC-007).</summary>
public sealed class AnalyticsFileSinkTests : IDisposable
{
    private static readonly DateTimeOffset LastMs = new(2026, 10, 30, 23, 59, 59, 999, TimeSpan.Zero);

    private readonly TempLogDirectory _temp = new();
    private readonly FakeClock _clock = new(LastMs);

    public void Dispose() => _temp.Dispose();

    private string Dir => Path.Combine(_temp.Path, "analytics");

    private AnalyticsFileSink Sink(string instanceId = "inst-1", int? retainDays = null, bool active = true, string? directory = null) =>
        new(directory ?? Dir, "g", instanceId, active, retainDays, _clock, NullLogger<AnalyticsFileSink>.Instance);

    private async Task<AnalyticsFileSink> StartedAsync(string instanceId = "inst-1", int? retainDays = null)
    {
        var sink = Sink(instanceId, retainDays);
        await sink.StartingAsync(CancellationToken.None);
        return sink;
    }

    private static AnalyticsRecord Record(string eventName, int n) =>
        new(eventName, "id" + n, Encoding.UTF8.GetBytes($$"""{"event":"{{eventName}}","n":{{n}}}"""));

    private static ValueTask WriteAsync(AnalyticsFileSink sink, params AnalyticsRecord[] records) => sink.WriteAsync(records, CancellationToken.None);

    private string File(string eventName, string day, string instance = "inst-1") => Path.Combine(Dir, $"g-{instance}-{eventName}-{day}.jsonl");

    private static string[] Lines(string path) => ObservedHost.ReadShared(path).Split('\n');

    /// <summary>What a consumer following the contract reads: every valid JSON line, skipping any other (SD2).</summary>
    private static int[] Parsed(string path) =>
        Lines(path).Where(l => l.Length > 0).Select(l =>
        {
            try
            {
                return JsonDocument.Parse(l).RootElement.GetProperty("n").GetInt32();
            }
            catch (JsonException)
            {
                return -1;
            }
        }).Where(n => n >= 0).ToArray();

    [Theory]
    [InlineData("g", "inst-1", "login", "g-inst-1-login-20261030.jsonl", ".g-inst-1.lock")]
    [InlineData("my game", "pod/7-a", "payment", "my_game-pod_7-a-payment-20261030.jsonl", ".my_game-pod_7-a.lock")]
    public void Names_are_sanitized_and_parse_back_from_the_right(string game, string instance, string eventName, string file, string lockFile)
    {
        var names = new AnalyticsFileNames(game, instance);

        names.DataFile(eventName, new DateOnly(2026, 10, 30)).ShouldBe(file);
        names.LockFile.ShouldBe(lockFile);
        AnalyticsFileNames.TryParse(file, out var owner, out var parsedEvent, out var day).ShouldBeTrue();
        (owner + "-").ShouldBe(names.Prefix);
        parsedEvent.ShouldBe(eventName);
        day.ShouldBe(new DateOnly(2026, 10, 30));
        names.IsOwnDataFile(file, out _).ShouldBeTrue();
        new AnalyticsFileNames(game, instance + "-x").IsOwnDataFile(file, out _).ShouldBeFalse();
        AnalyticsFileNames.TryParse("g-inst-login-2026103.jsonl", out _, out _, out _).ShouldBeFalse();
        AnalyticsFileNames.TryParse(".g-inst.lock", out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task One_file_per_event_type_rolled_at_utc_midnight_and_a_closed_day_is_never_reopened()
    {
        using var sink = await StartedAsync();

        await WriteAsync(sink, Record("login", 1), Record("payment", 2), Record("login", 3));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await WriteAsync(sink, Record("login", 4));

        // NTP step back across midnight: still the new day.
        _clock.Set(LastMs.AddSeconds(-5));
        await WriteAsync(sink, Record("login", 5));
        sink.Dispose();

        Parsed(File("login", "20261030")).ShouldBe([1, 3]);
        Parsed(File("payment", "20261030")).ShouldBe([2]);
        Parsed(File("login", "20261031")).ShouldBe([4, 5]);
        System.IO.File.ReadAllBytes(File("login", "20261031"))[^1].ShouldBe((byte)'\n');
    }

    [Fact]
    public async Task A_restart_the_same_day_appends_and_repairs_a_torn_last_line()
    {
        using (var first = await StartedAsync())
        {
            await WriteAsync(first, Record("login", 1));
        }

        // A crash cut the last line (bytes without the newline).
        System.IO.File.AppendAllText(File("login", "20261030"), """{"event":"login","n":""");
        using (var second = await StartedAsync())
        {
            await WriteAsync(second, Record("login", 2), Record("login", 3));
        }

        Lines(File("login", "20261030")).Length.ShouldBe(5);
        Parsed(File("login", "20261030")).ShouldBe([1, 2, 3]);
    }

    /// <summary>[VERIFY] V4 (2026-10-31, Windows 11, .NET 10.0.403): FileShare.None refuses a second open in the same process too.</summary>
    [Fact]
    public async Task A_second_writer_on_the_same_directory_and_instance_fails_startup_until_the_holder_is_gone()
    {
        var holder = await StartedAsync();

        var ex = await Should.ThrowAsync<GameServerStartupException>(() => Sink().StartingAsync(CancellationToken.None));
        ex.Message.ShouldContain("Observability:InstanceId");
        ex.Message.ShouldContain("'inst-1'");
        ex.Message.ShouldContain(Dir);
        Should.Throw<IOException>(() => new FileStream(Path.Combine(Dir, ".g-inst-1.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());

        // Another instance id shares the directory without trouble.
        using (await StartedAsync("inst-2"))
        {
        }

        holder.Dispose();
        using var next = await StartedAsync();
        await WriteAsync(next, Record("login", 1));
    }

    [Fact]
    public async Task An_uncreatable_directory_fails_startup_naming_the_key()
    {
        Directory.CreateDirectory(_temp.Path);
        var file = Path.Combine(_temp.Path, "a-file");
        await System.IO.File.WriteAllTextAsync(file, "x");

        var ex = await Should.ThrowAsync<GameServerStartupException>(() => Sink(directory: Path.Combine(file, "analytics")).StartingAsync(CancellationToken.None));

        ex.Message.ShouldContain("GameServer:Analytics:Directory");
        ex.Message.ShouldContain(Path.Combine(file, "analytics"));
    }

    [Fact]
    public async Task A_write_failure_throws_closes_the_handles_and_the_next_batch_after_the_fix_repairs()
    {
        using var sink = await StartedAsync();
        await WriteAsync(sink, Record("login", 1));

        // The payment file path is taken by a directory: the batch fails.
        Directory.CreateDirectory(File("payment", "20261030"));
        await Should.ThrowAsync<UnauthorizedAccessException>(async () => await WriteAsync(sink, Record("login", 2), Record("payment", 3)));

        Directory.Delete(File("payment", "20261030"));
        await WriteAsync(sink, Record("login", 4), Record("payment", 5));
        sink.Dispose();

        // Login 2 was written before the failure but never acknowledged: a duplicate after a retry is allowed, never a loss.
        Parsed(File("login", "20261030")).ShouldBe([1, 2, 4]);
        Parsed(File("payment", "20261030")).ShouldBe([5]);
    }

    [Fact]
    public async Task Retention_deletes_only_this_instances_files_older_than_n_days()
    {
        Directory.CreateDirectory(Dir);
        string[] old = [File("login", "20261020"), File("payment", "20261026")];
        string[] kept = [File("login", "20261027"), File("login", "20261020", "inst-2"), Path.Combine(Dir, "notes.txt")];
        foreach (var path in old.Concat(kept))
        {
            await System.IO.File.WriteAllTextAsync(path, "{}\n");
        }

        using (await StartedAsync(retainDays: null))
        {
        }

        old.Concat(kept).ShouldAllBe(p => System.IO.File.Exists(p));

        using var sink = await StartedAsync(retainDays: 3);
        old.ShouldAllBe(p => !System.IO.File.Exists(p));
        kept.ShouldAllBe(p => System.IO.File.Exists(p));

        // Day change: the pass runs again.
        _clock.Set(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
        await WriteAsync(sink, Record("login", 1));
        System.IO.File.Exists(File("login", "20261027")).ShouldBeFalse();
        System.IO.File.Exists(File("login", "20261020", "inst-2")).ShouldBeTrue();
    }

    [Fact]
    public async Task Inactive_analytics_creates_no_directory_and_no_lock()
    {
        var sink = Sink(active: false);

        await sink.StartingAsync(CancellationToken.None);
        await sink.StoppedAsync(CancellationToken.None);

        Directory.Exists(Dir).ShouldBeFalse();
    }

    [Fact]
    public async Task A_batch_with_a_guaranteed_record_is_flushed_to_disk_before_the_write_returns()
    {
        using var sink = await StartedAsync();

        // Flush to disk itself is not observable here; the guaranteed path must write every record of the mixed batch and
        // leave them readable by a shipper while the file is still open.
        await WriteAsync(sink, Record("login", 1), Record("payment", 2) with { Guaranteed = true }, Record("login", 3));

        Parsed(File("login", "20261030")).ShouldBe([1, 3]);
        Parsed(File("payment", "20261030")).ShouldBe([2]);
    }
}
