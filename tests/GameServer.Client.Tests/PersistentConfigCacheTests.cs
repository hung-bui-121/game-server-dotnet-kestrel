extern alias sdk;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// FR-060..FR-064, FR-066, FR-067, SC-006 (slice 015 T042, contracts/sdk-cache.md §7): the persistent config cache keeps "old, new or
/// empty" through every injected fault and every damaged file, never throws for file problems, and never turns names into paths.
/// </summary>
public sealed class PersistentConfigCacheTests : IDisposable
{
    private const string OptIn = "Opt-in: set GAMESERVER_PERF=1 to measure the persistent config cache (SC-006: 1 MiB load and save p95 <= 50 ms).";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-cache-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public PersistentConfigCacheTests(ITestOutputHelper output) => _output = output;

    private string MainFile => Path.Combine(_folder, PersistentConfigCache.FileName);

    private string TempFile => MainFile + ".tmp";

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.EnumerateFiles(_folder))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_new_folder_is_created_and_the_cache_starts_empty_without_a_problem()
    {
        var cache = new PersistentConfigCache(_folder);

        Directory.Exists(_folder).ShouldBeTrue();
        cache.Folder.ShouldBe(_folder);
        cache.Version.ShouldBeNull();
        cache.Files.ShouldBeEmpty();
        cache.LoadProblem.ShouldBeNull();
        cache.WriteProblem.ShouldBeNull();
    }

    [Fact]
    public void A_stored_version_survives_a_restart()
    {
        var files = Files(("shop.json", "{\"a\":1}"), ("battle.json", "[]"));
        new PersistentConfigCache(_folder).Store("v1+abc", files);

        var restarted = new PersistentConfigCache(_folder);

        restarted.Version.ShouldBe("v1+abc");
        ShouldEqual(restarted.Files, files);
        restarted.LoadProblem.ShouldBeNull();
        Directory.GetFiles(_folder).Select(Path.GetFileName).ShouldBe([PersistentConfigCache.FileName]);
    }

    [Fact]
    public void An_empty_bundle_round_trips()
    {
        new PersistentConfigCache(_folder).Store("v+0", new Dictionary<string, byte[]>());

        var restarted = new PersistentConfigCache(_folder);

        restarted.Version.ShouldBe("v+0");
        restarted.Files.ShouldBeEmpty();
    }

    [Fact]
    public void A_new_version_replaces_the_old_one_and_one_version_is_kept()
    {
        var cache = new PersistentConfigCache(_folder);
        cache.Store("v1", Files(("a.json", "1")));
        cache.Store("v2", Files(("b.json", "2")));

        var restarted = new PersistentConfigCache(_folder);

        restarted.Version.ShouldBe("v2");
        restarted.Files.Keys.ShouldBe(["b.json"]);
        Directory.GetFiles(_folder).Select(Path.GetFileName).ShouldBe([PersistentConfigCache.FileName]);
    }

    [Fact]
    public void An_identical_store_is_not_written_again()
    {
        var ops = new FaultyOps();
        var cache = new PersistentConfigCache(_folder, ops);
        cache.Store("v1", Files(("a.json", "1")));
        var opens = ops.Opens;

        cache.Store("v1", Files(("a.json", "1")));

        ops.Opens.ShouldBe(opens);
        cache.Store("v1", Files(("a.json", "2")));
        ops.Opens.ShouldBe(opens + 1);
    }

    [Theory]
    [InlineData(Step.OpenTemp)]
    [InlineData(Step.Write1)]
    [InlineData(Step.Write2)]
    [InlineData(Step.Write3)]
    [InlineData(Step.Flush)]
    [InlineData(Step.Replace)]
    public void A_fault_while_replacing_keeps_the_new_version_in_memory_and_the_old_one_on_disk(Step step)
    {
        new PersistentConfigCache(_folder).Store("v1", Files(("a.json", "old")));
        var cache = new PersistentConfigCache(_folder, new FaultyOps { FailAt = step });

        cache.Store("v2", Files(("a.json", "new")));

        cache.Version.ShouldBe("v2");
        Encoding.UTF8.GetString(cache.Files["a.json"]).ShouldBe("new");
        cache.WriteProblem.ShouldNotBeNull().ShouldContain("injected");
        File.Exists(TempFile).ShouldBeFalse();
        var restarted = new PersistentConfigCache(_folder);
        restarted.Version.ShouldBe("v1");
        restarted.LoadProblem.ShouldBeNull();
    }

    [Fact]
    public void A_fault_on_the_first_move_leaves_nothing_and_the_next_store_clears_the_problem()
    {
        var cache = new PersistentConfigCache(_folder, new FaultyOps { FailAt = Step.Move, Times = 1 });

        cache.Store("v1", Files(("a.json", "1")));
        cache.WriteProblem.ShouldNotBeNull();
        new PersistentConfigCache(_folder).Version.ShouldBeNull();

        cache.Store("v1", Files(("a.json", "1")));
        cache.WriteProblem.ShouldBeNull();
        new PersistentConfigCache(_folder).Version.ShouldBe("v1");
    }

    [Fact]
    public void A_temp_file_cut_at_any_byte_never_hides_the_valid_main_file()
    {
        new PersistentConfigCache(_folder).Store("v1", Files(("a.json", "old")));
        var next = Container("v2", Files(("a.json", "new"), ("b.json", "more")));

        for (var length = 0; length <= next.Length; length++)
        {
            File.WriteAllBytes(TempFile, next[..length]);

            var cache = new PersistentConfigCache(_folder);

            cache.Version.ShouldBe("v1", $"temp cut at {length}");
            cache.LoadProblem.ShouldBeNull();
        }
    }

    [Fact]
    public void Without_a_main_file_a_complete_temp_file_is_used_and_a_cut_one_is_ignored()
    {
        Directory.CreateDirectory(_folder);
        var next = Container("v2", Files(("a.json", "new")));

        for (var length = 0; length <= next.Length; length++)
        {
            File.WriteAllBytes(TempFile, next[..length]);

            var cache = new PersistentConfigCache(_folder);

            cache.Version.ShouldBe(length == next.Length ? "v2" : null, $"temp cut at {length}");
            cache.LoadProblem.ShouldBeNull();
        }
    }

    [Fact]
    public void A_version_recovered_from_the_temp_file_is_promoted_by_the_next_identical_store()
    {
        Directory.CreateDirectory(_folder);
        var files = Files(("a.json", "new"));
        File.WriteAllBytes(TempFile, Container("v2", files));
        var cache = new PersistentConfigCache(_folder);

        cache.Store("v2", files);

        File.Exists(MainFile).ShouldBeTrue();
        File.Exists(TempFile).ShouldBeFalse();
        new PersistentConfigCache(_folder).Version.ShouldBe("v2");
    }

    [Fact]
    public void A_main_file_cut_at_any_byte_reads_as_empty_with_a_problem()
    {
        Directory.CreateDirectory(_folder);
        var valid = Container("v1", Files(("a.json", "content")));

        for (var length = 0; length < valid.Length; length++)
        {
            File.WriteAllBytes(MainFile, valid[..length]);

            var cache = Loads(problem: "truncated", $"main cut at {length}");

            cache.Files.ShouldBeEmpty();
        }
    }

    [Fact]
    public void One_flipped_bit_in_the_header_payload_or_hash_is_detected()
    {
        Directory.CreateDirectory(_folder);
        var valid = Container("v1", Files(("a.json", "content")));
        var reserved = new[] { 6, 7 };

        for (var index = 0; index < valid.Length; index++)
        {
            var damaged = valid.ToArray();
            damaged[index] ^= 0x10;
            File.WriteAllBytes(MainFile, damaged);

            var cache = new PersistentConfigCache(_folder);

            if (reserved.Contains(index))
            {
                cache.Version.ShouldBe("v1", "the reserved field is ignored on read");
            }
            else
            {
                cache.Version.ShouldBeNull($"bit flipped at {index}");
                cache.LoadProblem.ShouldNotBeNull();
            }
        }
    }

    [Theory]
    [InlineData("magic", "not a config cache file")]
    [InlineData("format", "unknown format version 2")]
    [InlineData("trailing", "trailing bytes")]
    [InlineData("hash", "integrity check failed")]
    [InlineData("empty", "truncated")]
    public void A_damaged_main_file_names_the_reason(string damage, string problem)
    {
        Directory.CreateDirectory(_folder);
        var bytes = Container("v1", Files(("a.json", "content")));
        bytes = damage switch
        {
            "magic" => [(byte)'X', .. bytes[1..]],
            "format" => [.. bytes[..4], 2, 0, .. bytes[6..]],
            "trailing" => [.. bytes, 0],
            "hash" => [.. bytes[..^1], (byte)(bytes[^1] ^ 1)],
            _ => [],
        };
        File.WriteAllBytes(MainFile, bytes);

        Loads(problem, damage);
    }

    [Fact]
    public void Duplicate_names_invalid_utf8_and_empty_names_read_as_empty()
    {
        Directory.CreateDirectory(_folder);

        File.WriteAllBytes(MainFile, Raw(Payload("v1", ("a.json"u8.ToArray(), "1"u8.ToArray()), ("a.json"u8.ToArray(), "2"u8.ToArray()))));
        Loads("duplicate file name", "duplicate");

        File.WriteAllBytes(MainFile, Raw(Payload("v1", ([0xC3, 0x28], "1"u8.ToArray()))));
        Loads("invalid UTF-8", "invalid UTF-8");

        File.WriteAllBytes(MainFile, Raw(Payload("v1", ([], "1"u8.ToArray()))));
        Loads("invalid content", "empty name");
    }

    [Fact]
    public void A_length_larger_than_the_payload_is_refused_without_allocating_it()
    {
        Directory.CreateDirectory(_folder);
        var payload = Payload("v1", ("a.json"u8.ToArray(), "1"u8.ToArray()));

        // The content length of the only file claims 2 GiB; the file holds one byte.
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + 2 + 4 + 4 + 6), int.MaxValue);
        File.WriteAllBytes(MainFile, Raw(payload));

        Loads("truncated", "huge length");
    }

    [Fact]
    public void A_locked_main_file_reads_as_unreadable()
    {
        new PersistentConfigCache(_folder).Store("v1", Files(("a.json", "1")));
        using var locked = new FileStream(MainFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Loads("unreadable: IOException", "locked");
    }

    [Fact]
    public void A_read_only_main_file_keeps_the_new_version_in_memory()
    {
        new PersistentConfigCache(_folder).Store("v1", Files(("a.json", "1")));
        File.SetAttributes(MainFile, FileAttributes.ReadOnly);
        var cache = new PersistentConfigCache(_folder);

        Should.NotThrow(() => cache.Store("v2", Files(("a.json", "2"))));

        cache.Version.ShouldBe("v2");
        cache.WriteProblem.ShouldNotBeNull();
        new PersistentConfigCache(_folder).Version.ShouldBe("v1");
    }

    [Fact]
    public void An_unwritable_folder_keeps_the_values_in_memory_and_sets_the_write_problem()
    {
        var cache = new PersistentConfigCache(_folder);
        using (ReadOnlyFolder.DenyWrites(_folder))
        {
            Should.NotThrow(() => cache.Store("v1", Files(("a.json", "1"))));
        }

        cache.Version.ShouldBe("v1");
        cache.Files.Keys.ShouldBe(["a.json"]);
        cache.WriteProblem.ShouldNotBeNull().ShouldStartWith("not saved: UnauthorizedAccessException");
        new PersistentConfigCache(_folder).Version.ShouldBeNull();
    }

    [Fact]
    public void Path_like_names_stay_data_and_the_folder_holds_only_the_cache_file()
    {
        var files = Files(("../x.json", "1"), ("/etc/passwd", "2"), ("C:\\x", "3"), ("a/b.json", "4"));

        new PersistentConfigCache(_folder).Store("v1", files);
        var restarted = new PersistentConfigCache(_folder);

        ShouldEqual(restarted.Files, files);
        Directory.GetFileSystemEntries(_folder).Select(Path.GetFileName).ShouldBe([PersistentConfigCache.FileName]);
        File.Exists(Path.Combine(Path.GetDirectoryName(_folder)!, "x.json")).ShouldBeFalse();
    }

    [Fact]
    public void A_version_or_name_the_format_cannot_hold_stays_in_memory_with_a_write_problem()
    {
        var cache = new PersistentConfigCache(_folder);

        cache.Store(new string('v', 257), Files(("a.json", "1")));
        cache.Version!.Length.ShouldBe(257);
        cache.WriteProblem.ShouldNotBeNull();

        cache.Store("v1", Files(("\ud800", "lone surrogate")));
        cache.WriteProblem.ShouldNotBeNull();
        File.Exists(MainFile).ShouldBeFalse();
    }

    [Fact]
    public void The_container_follows_the_documented_layout()
    {
        var bytes = Container("v1", Files(("b.json", "B"), ("a.json", "A")));
        var payload = Payload("v1", ("a.json"u8.ToArray(), "A"u8.ToArray()), ("b.json"u8.ToArray(), "B"u8.ToArray()));

        bytes.ShouldBe(Raw(payload));
    }

    [Fact]
    public void One_mib_loads_and_saves_within_the_budget()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GAMESERVER_PERF") == "1", OptIn);
        const int Iterations = 50;
        const double BudgetMs = 50;
        var files = Enumerable.Range(0, 16).ToDictionary(i => $"table{i:D2}.json", _ => RandomNumberGenerator.GetBytes(64 * 1024), StringComparer.Ordinal);
        var saves = new List<double>();
        var loads = new List<double>();
        var cache = new PersistentConfigCache(_folder);
        for (var i = -5; i < Iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            cache.Store($"v{i}", files);
            var save = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var loaded = new PersistentConfigCache(_folder);
            var load = watch.Elapsed.TotalMilliseconds;
            loaded.Version.ShouldBe($"v{i}");
            if (i >= 0)
            {
                saves.Add(save);
                loads.Add(load);
            }
        }

        var (saveP95, loadP95) = (P95(saves), P95(loads));
        _output.WriteLine($"folder {_folder} (drive {Path.GetPathRoot(_folder)}): save p95 {saveP95:F1} ms, load p95 {loadP95:F1} ms");
        saveP95.ShouldBeLessThanOrEqualTo(BudgetMs);
        loadP95.ShouldBeLessThanOrEqualTo(BudgetMs);
    }

    private static double P95(List<double> samples) => samples.Order().ElementAt((int)Math.Ceiling(samples.Count * 0.95) - 1);

    private static Dictionary<string, byte[]> Files(params (string Name, string Content)[] files) =>
        files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Content), StringComparer.Ordinal);

    private static void ShouldEqual(IReadOnlyDictionary<string, byte[]> actual, Dictionary<string, byte[]> expected)
    {
        actual.Keys.Order(StringComparer.Ordinal).ShouldBe(expected.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, content) in expected)
        {
            actual[name].ShouldBe(content);
        }
    }

    /// <summary>The container the cache writes, through the SDK's own serializer (internal, InternalsVisibleTo).</summary>
    private static byte[] Container(string version, Dictionary<string, byte[]> files) =>
        ConfigCacheFormat.Serialize(version, files, out _)!.SelectMany(part => part).ToArray();

    /// <summary>A payload written by hand from contracts/sdk-cache.md §2 (no validation, so damaged content can be built).</summary>
    private static byte[] Payload(string version, params (byte[] Name, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var versionBytes = Encoding.UTF8.GetBytes(version);
        writer.Write((uint)versionBytes.Length);
        writer.Write(versionBytes);
        writer.Write((uint)files.Length);
        foreach (var (name, content) in files)
        {
            writer.Write((uint)name.Length);
            writer.Write(name);
            writer.Write((uint)content.Length);
            writer.Write(content);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Header + payload + SHA-256, as format v1 lays them out.</summary>
    private static byte[] Raw(byte[] payload)
    {
        var header = new byte[12];
        "GSCC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        return [.. header, .. payload, .. SHA256.HashData(payload)];
    }

    private PersistentConfigCache Loads(string problem, string because)
    {
        PersistentConfigCache? cache = null;
        Should.NotThrow(() => cache = new PersistentConfigCache(_folder), because);
        cache!.Version.ShouldBeNull(because);
        cache.Files.ShouldBeEmpty(because);
        cache.LoadProblem.ShouldBe(problem, because);
        return cache;
    }

    public enum Step
    {
        None,
        OpenTemp,
        Write1,
        Write2,
        Write3,
        Flush,
        Replace,
        Move,
    }

    /// <summary>The real file system with one step failing (contracts/sdk-cache.md §7).</summary>
    private sealed class FaultyOps : CacheFileOps
    {
        private int _writes;
        private int _failures;

        public Step FailAt { get; init; }

        /// <summary>How many times the step fails (then it works).</summary>
        public int Times { get; init; } = int.MaxValue;

        public int Opens { get; private set; }

        public override Stream OpenTemp(string path)
        {
            Fail(Step.OpenTemp);
            Opens++;
            _writes = 0;
            return base.OpenTemp(path);
        }

        public override void Write(Stream stream, byte[] buffer)
        {
            _writes++;
            Fail(_writes switch { 1 => Step.Write1, 2 => Step.Write2, _ => Step.Write3 });
            base.Write(stream, buffer);
        }

        public override void Flush(Stream stream)
        {
            Fail(Step.Flush);
            base.Flush(stream);
        }

        public override void Replace(string source, string destination)
        {
            Fail(Step.Replace);
            base.Replace(source, destination);
        }

        public override void Move(string source, string destination)
        {
            Fail(Step.Move);
            base.Move(source, destination);
        }

        private void Fail(Step step)
        {
            if (step == FailAt && _failures < Times)
            {
                _failures++;
                throw new IOException($"injected fault at {step}");
            }
        }
    }
}
