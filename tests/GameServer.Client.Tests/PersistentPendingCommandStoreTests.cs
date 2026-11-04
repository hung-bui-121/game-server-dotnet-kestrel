extern alias sdk;

using System.Text;
using sdk::GameServer.Client;
using Shouldly;

namespace GameServer.Client.Tests;

/// <summary>
/// Slice 016 T057 (FR-045, SC-007, contracts/sdk-pending-store.md §3, §7): one atomic file per entry; a fault at any file step keeps
/// the old or the new entry loadable by the next store and never throws; leftovers and damaged entries are dropped at load.
/// </summary>
public sealed class PersistentPendingCommandStoreTests : IDisposable
{
    private const string IdA = "0a2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d";
    private const string IdB = "0b2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d";
    private const string IdC = "0c2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d";

    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gs-pending-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static PendingCommand Entry(string id, string body = "body", int minutes = 0) =>
        new(id, "p_1", "api/v1/shop/buy", Encoding.UTF8.GetBytes(body), T0.AddMinutes(minutes));

    private string EntryFile(string id) => Path.Combine(_folder, id + PersistentPendingCommandStore.EntrySuffix);

    private PersistentPendingCommandStore Reload() => new(_folder);

    private static string Bodies(IPendingCommandStore store) =>
        string.Join(",", store.Entries.Select(e => e.RequestId[..2] + "=" + Encoding.UTF8.GetString(e.Body)));

    [Fact]
    public void Entries_survive_a_new_store_in_recorded_order_then_id()
    {
        var store = new PersistentPendingCommandStore(_folder);
        store.Record(Entry(IdC, minutes: 1));
        store.Record(Entry(IdB));
        store.Record(Entry(IdA));

        store.WriteProblem.ShouldBeNull();
        Bodies(Reload()).ShouldBe("0a=body,0b=body,0c=body");
        Reload().LoadProblem.ShouldBeNull();
        Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe([IdA + ".pending", IdB + ".pending", IdC + ".pending"]);
    }

    [Fact]
    public void Recording_the_same_id_again_replaces_the_entry()
    {
        var store = new PersistentPendingCommandStore(_folder);
        store.Record(Entry(IdA, "old"));

        store.Record(Entry(IdA, "new"));

        store.WriteProblem.ShouldBeNull();
        Bodies(store).ShouldBe("0a=new");
        Bodies(Reload()).ShouldBe("0a=new");
    }

    [Fact]
    public void Remove_deletes_the_entry_and_ignores_unknown_ids()
    {
        var store = new PersistentPendingCommandStore(_folder);
        store.Record(Entry(IdA));

        store.Remove("unknown");
        store.Remove(IdB);
        store.Remove(IdA);

        store.Entries.ShouldBeEmpty();
        store.WriteProblem.ShouldBeNull();
        File.Exists(EntryFile(IdA)).ShouldBeFalse();
        Reload().Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PendingStep.OpenTemp)]
    [InlineData(PendingStep.Write)]
    [InlineData(PendingStep.Flush)]
    [InlineData(PendingStep.Move)]
    public void A_fault_while_recording_a_new_entry_keeps_it_in_memory_and_leaves_no_file(PendingStep step)
    {
        var store = new PersistentPendingCommandStore(_folder, new FaultyPendingOps { FailAt = step });

        Should.NotThrow(() => store.Record(Entry(IdA)));

        store.WriteProblem.ShouldBe($"not saved: IOException: injected fault at {step}");
        Bodies(store).ShouldBe("0a=body");
        Reload().Entries.ShouldBeEmpty();
        Directory.GetFiles(_folder).ShouldBeEmpty("the temp file is deleted after a failed record");
    }

    [Theory]
    [InlineData(PendingStep.OpenTemp)]
    [InlineData(PendingStep.Write)]
    [InlineData(PendingStep.Flush)]
    [InlineData(PendingStep.Replace)]
    public void A_fault_while_recording_an_entry_again_keeps_the_old_file(PendingStep step)
    {
        var ops = new FaultyPendingOps();
        var store = new PersistentPendingCommandStore(_folder, ops);
        store.Record(Entry(IdA, "old"));
        ops.FailAt = step;

        store.Record(Entry(IdA, "new"));

        store.WriteProblem.ShouldStartWith("not saved: IOException:");
        Bodies(store).ShouldBe("0a=new");
        Bodies(Reload()).ShouldBe("0a=old");
    }

    [Fact]
    public void A_fault_while_removing_keeps_the_file_and_the_next_store_lists_it()
    {
        var ops = new FaultyPendingOps();
        var store = new PersistentPendingCommandStore(_folder, ops);
        store.Record(Entry(IdA));
        ops.FailAt = PendingStep.Delete;

        Should.NotThrow(() => store.Remove(IdA));

        store.Entries.ShouldBeEmpty();
        store.WriteProblem.ShouldBe("not removed: IOException: injected fault at Delete");
        Bodies(Reload()).ShouldBe("0a=body");
    }

    [Fact]
    public void A_successful_write_clears_the_last_problem()
    {
        var ops = new FaultyPendingOps { FailAt = PendingStep.Flush, Times = 1 };
        var store = new PersistentPendingCommandStore(_folder, ops);
        store.Record(Entry(IdA));
        store.WriteProblem.ShouldNotBeNull();

        store.Record(Entry(IdB));

        store.WriteProblem.ShouldBeNull();
        Bodies(Reload()).ShouldBe("0b=body");
    }

    [Fact]
    public void A_leftover_temp_file_is_deleted_at_load_and_never_read()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, IdA + ".pending.tmp"), [1, 2, 3]);

        var store = Reload();

        store.Entries.ShouldBeEmpty();
        store.LoadProblem.ShouldBeNull();
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void Two_valid_entries_and_one_damaged_load_two_and_name_the_damaged_one()
    {
        var store = new PersistentPendingCommandStore(_folder);
        store.Record(Entry(IdA));
        store.Record(Entry(IdB));
        store.Record(Entry(IdC));
        var damaged = File.ReadAllBytes(EntryFile(IdB));
        damaged[20] ^= 0xFF;
        File.WriteAllBytes(EntryFile(IdB), damaged);

        var reloaded = Reload();

        Bodies(reloaded).ShouldBe("0a=body,0c=body");
        reloaded.LoadProblem.ShouldBe($"{IdB}.pending: integrity check failed");
        File.Exists(EntryFile(IdB)).ShouldBeFalse("a damaged entry is deleted");
        Reload().LoadProblem.ShouldBeNull();
    }

    [Fact]
    public void An_entry_copied_under_another_id_is_refused()
    {
        var store = new PersistentPendingCommandStore(_folder);
        store.Record(Entry(IdA));
        File.Copy(EntryFile(IdA), EntryFile(IdB));

        var reloaded = Reload();

        Bodies(reloaded).ShouldBe("0a=body");
        reloaded.LoadProblem.ShouldBe($"{IdB}.pending: the request id does not match the file name");
    }

    /// <summary>VERIFY V9: Windows matches "*.pending" case-insensitively; only exact lowercase entry names are read (or deleted).</summary>
    [Theory]
    [InlineData("0A2A9C1E-7B11-4D2A-9C0D-5E6F7A8B9C0D.pending")]
    [InlineData("0a2a9c1e-7b11-4d2a-9c0d-5e6f7a8b9c0d.PENDING")]
    [InlineData("notes.pending")]
    public void A_file_that_is_not_an_exact_entry_name_is_named_and_left_alone(string name)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1]);

        var store = Reload();

        store.Entries.ShouldBeEmpty();
        store.LoadProblem.ShouldBe($"{name}: not an entry file name");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void An_unreadable_folder_gives_an_empty_store_with_the_reason()
    {
        var store = new PersistentPendingCommandStore(_folder, new FaultyPendingOps { FailAt = PendingStep.Enumerate });

        store.Entries.ShouldBeEmpty();
        store.LoadProblem.ShouldBe("unreadable: IOException");
    }

    [Fact]
    public void A_read_only_folder_keeps_entries_in_memory_and_reports_the_write_problem()
    {
        Directory.CreateDirectory(_folder);
        var store = new PersistentPendingCommandStore(_folder);
        using (ReadOnlyFolder.DenyWrites(_folder))
        {
            Should.NotThrow(() => store.Record(Entry(IdA)));

            store.WriteProblem.ShouldStartWith("not saved: UnauthorizedAccessException:");
            Bodies(store).ShouldBe("0a=body");
        }

        Reload().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void An_entry_too_large_to_store_stays_in_memory_with_the_reason()
    {
        var store = new PersistentPendingCommandStore(_folder);

        store.Record(new PendingCommand(IdA, "p_1", "r", new byte[(1024 * 1024) + 1], T0));

        store.Entries.ShouldHaveSingleItem();
        store.WriteProblem.ShouldStartWith("not saved: the player id must be");
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_folder_path_is_refused()
    {
        Should.Throw<ArgumentException>(() => new PersistentPendingCommandStore(" "));
    }
}
