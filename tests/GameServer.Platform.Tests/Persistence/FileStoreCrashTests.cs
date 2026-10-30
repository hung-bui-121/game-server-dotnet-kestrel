using System.Diagnostics;
using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>
/// Slice 011 T020: a commit interrupted at any point is all-or-nothing after a restart (FR-020, SC-002, US2-2). The writer
/// stops at a crash point without any further disk operation, the lock handle is released as the OS would, and a new
/// store opens the folder: before the journal rename nothing of the commit exists, from it on all of it does.
/// </summary>
public sealed class FileStoreCrashTests : IDisposable
{
    private static readonly TransactionRunOptions Options = new(TimeSpan.FromSeconds(10));
    private static readonly DocumentKey Inserted = new("docs", "inserted");
    private static readonly DocumentKey Replaced = new("docs", "replaced");
    private static readonly DocumentKey Removed = new("docs", "removed");

    private readonly FileStoreTestFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>Crash points of the commit {insert, replace, remove}, and whether the commit survives each.</summary>
    public static TheoryData<string, bool> MultiDocumentSteps() => new()
    {
        { nameof(FileCommitStep.JournalTempWritten), false },
        { nameof(FileCommitStep.JournalCommitted), true },
        { "DocumentTempWritten(0)", true },
        { "DocumentReplaced(0)", true },
        { "DocumentTempWritten(1)", true },
        { "DocumentReplaced(1)", true },
        { "DocumentReplaced(2)", true },
        { nameof(FileCommitStep.JournalDeleted), true },
    };

    [Theory]
    [MemberData(nameof(MultiDocumentSteps))]
    public async Task A_multi_document_commit_is_all_or_nothing_after_a_crash(string step, bool committed)
    {
        using (var store = _folder.Open(_clock))
        {
            await store.Store.RunTransactionAsync(async tx =>
            {
                await tx.InsertAsync(Replaced, FileStoreTestFolder.Doc(1));
                await tx.InsertAsync(Removed, FileStoreTestFolder.Doc(1));
                return true;
            }, Options);

            store.Writer.CrashAt = Parse(step);
            await Should.ThrowAsync<TransactionFailedException>(() => store.Store.RunTransactionAsync(async tx =>
            {
                var current = await tx.GetManyAsync([Replaced, Removed]);
                await tx.InsertAsync(Inserted, FileStoreTestFolder.Doc(2));
                await tx.ReplaceAsync(current[0]!, FileStoreTestFolder.Doc(2));
                await tx.RemoveAsync(current[1]!);
                return true;
            }, Options));
        }

        using var reopened = _folder.Open(_clock);
        (int?, int?, int?) expected = committed ? (2, 2, null) : (null, 1, 1);
        (Value(reopened, Inserted), Value(reopened, Replaced), Value(reopened, Removed)).ShouldBe(expected, $"crash at {step}");
        TemporaryFiles().ShouldBeEmpty();
        File.Exists(Path.Combine(_folder.Root, "journal.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_single_write_interrupted_before_its_rename_is_absent()
    {
        var key = new DocumentKey("sessions", "s1");
        using (var store = _folder.Open(_clock))
        {
            await ((IKeyValueStore)store.Store).InsertAsync(key, FileStoreTestFolder.Doc(1), null, CancellationToken.None);
            var existing = (await store.Store.GetAsync(key, CancellationToken.None))!;
            store.Writer.CrashAt = FileCommitStep.SingleTempWritten;

            await Should.ThrowAsync<SimulatedCrashException>(() =>
                ((IKeyValueStore)store.Store).ReplaceAsync(existing, FileStoreTestFolder.Doc(2), null, CancellationToken.None));
            File.Exists(_folder.DocumentPath(key) + ".tmp").ShouldBeTrue();
        }

        using var reopened = _folder.Open(_clock);
        Value(reopened, key).ShouldBe(1);
        TemporaryFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Torn_temporary_files_are_ignored_and_deleted()
    {
        var key = new DocumentKey("docs", "a");
        using (var store = _folder.Open(_clock))
        {
            store.Store.Seed(key, FileStoreTestFolder.Doc(1));
        }

        File.WriteAllText(Path.Combine(_folder.Root, "journal.json.tmp"), """{"version":1,"writes":[{"coll""");
        File.WriteAllText(_folder.DocumentPath(key) + ".tmp", """{"key":"a","cont""");
        File.WriteAllText(Path.Combine(_folder.GameFolder, "docs", "b.json.tmp"), "{");

        using var reopened = _folder.Open(_clock);
        Value(reopened, key).ShouldBe(1);
        reopened.Store.Count("docs").ShouldBe(1);
        TemporaryFiles().ShouldBeEmpty();
    }

    [Fact]
    public void An_unparsable_journal_fails_the_start_naming_it()
    {
        _folder.Open(_clock).Dispose();
        var journal = Path.Combine(_folder.Root, "journal.json");
        File.WriteAllText(journal, """{"version":1,"writes":[{"coll""");

        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

        ex.Message.ShouldContain($"'{journal}' cannot be replayed");
        ex.Message.ShouldContain("delete it to drop the last commit, or reset the local data");
        File.Exists(journal).ShouldBeTrue();
    }

    [Fact]
    public async Task Load_and_commit_performance_is_within_the_developer_budget()
    {
        if (Environment.GetEnvironmentVariable("GAMESERVER_PERF") != "1")
        {
            Assert.Skip("Opt-in: set GAMESERVER_PERF=1 to measure the file store (SC-006: 10 000 documents load in <= 10 s, commit p95 <= 50 ms).");
        }

        const int Documents = 10_000;
        const int Commits = 500;
        const int DocumentsPerCommit = 6;
        using (var store = _folder.Open(_clock))
        {
            for (var batch = 0; batch < Documents / 100; batch++)
            {
                var first = batch * 100;
                await store.Store.RunTransactionAsync(async tx =>
                {
                    for (var i = first; i < first + 100; i++)
                    {
                        await tx.InsertAsync(new DocumentKey("pa_perf", $"p_{i:D5}"), new JsonObject { ["value"] = i, ["payload"] = new string('x', 300) });
                    }

                    return true;
                }, Options);
            }
        }

        var load = Stopwatch.StartNew();
        using var reopened = _folder.Open(_clock);
        load.Stop();
        reopened.Store.Count("pa_perf").ShouldBe(Documents);

        var durations = new List<double>(Commits);
        for (var commit = 0; commit < Commits; commit++)
        {
            var round = commit;
            var watch = Stopwatch.StartNew();
            await reopened.Store.RunTransactionAsync(async tx =>
            {
                var keys = Enumerable.Range(0, DocumentsPerCommit).Select(i => new DocumentKey("pa_perf", $"p_{(round * DocumentsPerCommit + i) % Documents:D5}")).ToArray();
                var current = await tx.GetManyAsync(keys);
                foreach (var document in current)
                {
                    var content = (JsonObject)document!.Content.DeepClone();
                    content["value"] = round;
                    await tx.ReplaceAsync(document, content);
                }

                return true;
            }, Options);
            durations.Add(watch.Elapsed.TotalMilliseconds);
        }

        durations.Sort();
        var p95 = durations[(int)Math.Ceiling(0.95 * durations.Count) - 1];
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"file store perf: load {Documents} documents in {load.ElapsedMilliseconds} ms; {Commits} commits of {DocumentsPerCommit}: " +
            $"p50 {durations[durations.Count / 2]:F1} ms, p95 {p95:F1} ms, max {durations[^1]:F1} ms");
        load.Elapsed.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        p95.ShouldBeLessThanOrEqualTo(50);
    }

    private static int? Value(FileDocumentStore store, DocumentKey key) => store.Store.Peek(key)?["value"]?.GetValue<int>();

    private string[] TemporaryFiles() =>
        Directory.EnumerateFiles(_folder.Root, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".tmp", StringComparison.Ordinal)).ToArray();

    private static FileCommitStep Parse(string step) => step switch
    {
        nameof(FileCommitStep.JournalTempWritten) => FileCommitStep.JournalTempWritten,
        nameof(FileCommitStep.JournalCommitted) => FileCommitStep.JournalCommitted,
        nameof(FileCommitStep.JournalDeleted) => FileCommitStep.JournalDeleted,
        _ when step.StartsWith("DocumentTempWritten(", StringComparison.Ordinal) => FileCommitStep.DocumentTempWritten(step[^2] - '0'),
        _ when step.StartsWith("DocumentReplaced(", StringComparison.Ordinal) => FileCommitStep.DocumentReplaced(step[^2] - '0'),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };
}
