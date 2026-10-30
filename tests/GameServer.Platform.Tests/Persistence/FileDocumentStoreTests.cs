using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using GameServer.Platform.Persistence;
using GameServer.Platform.Registry;
using GameServer.Platform.Testing;
using Shouldly;

namespace GameServer.Platform.Tests.Persistence;

/// <summary>
/// Slice 011 T019: lock, format, malformed files, expiry, layout, reset and commit failures of the file store
/// (US2-4..US2-7, FR-019..FR-025, SC-007; research R7–R9).
/// </summary>
public sealed class FileDocumentStoreTests : IDisposable
{
    private static readonly TransactionRunOptions Options = new(TimeSpan.FromSeconds(10));

    private readonly FileStoreTestFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    private static Task InsertAsync(InMemoryDocumentStore store, params (DocumentKey Key, int Value)[] documents) =>
        store.RunTransactionAsync(async tx =>
        {
            foreach (var (key, value) in documents)
            {
                await tx.InsertAsync(key, FileStoreTestFolder.Doc(value));
            }

            return true;
        }, Options);

    [Fact]
    public void A_second_store_on_a_locked_folder_fails_fast_naming_the_folder_and_changes_nothing()
    {
        using var first = _folder.Open(_clock);
        first.Store.Seed(new DocumentKey("docs", "a"), FileStoreTestFolder.Doc(1));
        var before = _folder.Files();

        var stopwatch = Stopwatch.StartNew();
        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        ex.Message.ShouldContain($"the data folder '{_folder.Root}' is used by another host");
        ex.Message.ShouldContain("FileStore:Folder");
        _folder.Files().ShouldBe(before);
    }

    [Fact]
    public void The_lock_is_released_by_dispose()
    {
        _folder.Open(_clock).Dispose();

        using var second = _folder.Open(_clock);
        second.Store.Count("docs").ShouldBe(0);
    }

    [Fact]
    public void The_lock_is_released_when_the_handle_closes_without_dispose()
    {
        OpenAndAbandon();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        using var second = _folder.Open(_clock);
        second.Store.Peek(new DocumentKey("docs", "a")).ShouldNotBeNull();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenAndAbandon() => _folder.Open(_clock).Store.Seed(new DocumentKey("docs", "a"), FileStoreTestFolder.Doc(1));

    [Fact]
    public void An_empty_folder_gets_the_format_file()
    {
        using var store = _folder.Open(_clock);

        JsonNode.Parse(File.ReadAllText(Path.Combine(_folder.Root, "store.json")))!.ToJsonString()
            .ShouldBe("""{"format":"gameserver-file-store","version":1}""");
    }

    /// <summary>T055 m3: an I/O error of the file system (here a file where the folder should be) is a startup error naming the folder.</summary>
    [Fact]
    public void A_file_in_place_of_the_folder_is_a_startup_error_naming_the_folder()
    {
        File.WriteAllText(_folder.Root, "not a folder");
        try
        {
            var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

            ex.Message.ShouldContain($"[filestore] the data folder '{_folder.Root}' cannot be opened (IOException: ");
            ex.Message.ShouldContain("FileStore:Folder");
        }
        finally
        {
            File.Delete(_folder.Root);
        }
    }

    /// <summary>T055 m3: a folder this user may not write (Windows ACL deny) is a startup error, not an unhandled exception.</summary>
    [Fact]
    public void A_folder_the_user_may_not_write_is_a_startup_error_naming_the_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The ACL deny case is Windows-only; the I/O error case covers the same path on every system.");
            return;
        }

        Directory.CreateDirectory(_folder.Root);
        var deny = DenyWrite(_folder.Root);
        try
        {
            var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

            ex.Message.ShouldContain($"[filestore] the data folder '{_folder.Root}' cannot be opened (UnauthorizedAccessException: ");
        }
        finally
        {
            AllowAgain(_folder.Root, deny);
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule DenyWrite(string folder)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(
            user,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            AccessControlType.Deny);
        var directory = new DirectoryInfo(folder);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        return rule;
    }

    [SupportedOSPlatform("windows")]
    private static void AllowAgain(string folder, FileSystemAccessRule rule)
    {
        var directory = new DirectoryInfo(folder);
        var security = directory.GetAccessControl();
        security.RemoveAccessRule(rule);
        directory.SetAccessControl(security);
    }

    [Fact]
    public void A_foreign_non_empty_folder_is_refused_before_anything_is_created()
    {
        Directory.CreateDirectory(_folder.Root);
        File.WriteAllText(Path.Combine(_folder.Root, "notes.txt"), "mine");

        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

        ex.Message.ShouldContain("has no store.json: it is not a file store folder");
        Directory.EnumerateFileSystemEntries(_folder.Root).Select(Path.GetFileName).ShouldBe(["notes.txt"]);
    }

    [Theory]
    [InlineData("""{"format":"other-store","version":1}""", "declares format 'other-store' version 1")]
    [InlineData("""{"format":"gameserver-file-store","version":2}""", "declares format 'gameserver-file-store' version 2")]
    [InlineData("not json", "declares format '(unreadable)'")]
    public void Another_format_or_version_is_refused_with_reset_guidance(string content, string expected)
    {
        Directory.CreateDirectory(_folder.Root);
        File.WriteAllText(Path.Combine(_folder.Root, "store.json"), content);

        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

        ex.Message.ShouldContain(expected);
        ex.Message.ShouldContain("--FileStore:ResetOnStart=true");
    }

    [Fact]
    public void Malformed_files_are_all_named_in_one_error_and_nothing_is_loaded()
    {
        _folder.Open(_clock).Dispose();
        var docs = Directory.CreateDirectory(Path.Combine(_folder.GameFolder, "docs")).FullName;
        File.WriteAllText(Path.Combine(docs, "bad-json.json"), "{ nope");
        File.WriteAllText(Path.Combine(docs, "wrong-name.json"), """{"key":"other","expiresAtUtc":null,"content":{}}""");
        File.WriteAllText(Path.Combine(docs, "array-content.json"), """{"key":"array-content","expiresAtUtc":null,"content":[1]}""");
        File.WriteAllText(Path.Combine(docs, "bad-expiry.json"), """{"key":"bad-expiry","expiresAtUtc":"tomorrow","content":{}}""");
        File.WriteAllText(Path.Combine(docs, "readme.txt"), "foreign");
        File.WriteAllText(Path.Combine(docs, "good.json"), """{"key":"good","expiresAtUtc":null,"content":{"value":1}}""");

        var store = _folder.Create(_clock);
        var ex = Should.Throw<GameServerStartupException>(store.Open);

        ex.Violations.Count.ShouldBe(5);
        ex.Message.ShouldContain("bad-json.json' is not a valid document (invalid JSON");
        ex.Message.ShouldContain("wrong-name.json' is not a valid document (the file name does not match its key 'other')");
        ex.Message.ShouldContain("array-content.json' is not a valid document ('content' must be a JSON object)");
        ex.Message.ShouldContain("bad-expiry.json' is not a valid document ('expiresAtUtc' must be null or an ISO-8601 UTC time");
        ex.Message.ShouldContain("readme.txt' is not a valid document (not a document file name");
        ex.Message.ShouldNotContain("good.json");
        Should.Throw<InvalidOperationException>(() => store.Store.Count("docs"));

        // The failed open released the lock: a fixed folder opens.
        foreach (var name in new[] { "bad-json.json", "wrong-name.json", "array-content.json", "bad-expiry.json", "readme.txt" })
        {
            File.Delete(Path.Combine(docs, name));
        }

        using var fixedStore = _folder.Open(_clock);
        fixedStore.Store.Peek(new DocumentKey("docs", "good"))!["value"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void More_than_twenty_malformed_files_are_counted()
    {
        _folder.Open(_clock).Dispose();
        var docs = Directory.CreateDirectory(Path.Combine(_folder.GameFolder, "docs")).FullName;
        for (var i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(docs, $"bad{i}.json"), "{");
        }

        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock));

        ex.Violations.Count.ShouldBe(21);
        ex.Violations[^1].ShouldContain("and 5 more malformed files");
    }

    [Fact]
    public async Task An_expired_document_is_dropped_at_the_next_start_and_its_file_deleted()
    {
        var session = new DocumentKey("sessions", "s1");
        using (var store = _folder.Open(_clock))
        {
            await ((IKeyValueStore)store.Store).InsertAsync(session, FileStoreTestFolder.Doc(1), TimeSpan.FromHours(1), CancellationToken.None);
            await ((IKeyValueStore)store.Store).InsertAsync(new DocumentKey("sessions", "s2"), FileStoreTestFolder.Doc(2), TimeSpan.FromHours(3), CancellationToken.None);
        }

        var file = _folder.DocumentPath(session);
        JsonNode.Parse(File.ReadAllText(file))!["expiresAtUtc"]!.GetValue<string>().ShouldBe("2026-10-26T01:00:00.0000000Z");

        _clock.Advance(TimeSpan.FromHours(2));
        using var reopened = _folder.Open(_clock);

        reopened.Store.Peek(session).ShouldBeNull();
        File.Exists(file).ShouldBeFalse();
        (await reopened.Store.GetAsync(new DocumentKey("sessions", "s2"), CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Documents_live_in_one_file_each_and_hand_edits_while_closed_are_loaded()
    {
        var key = new DocumentKey("pa_wallet", "p_ABC::1");
        using (var store = _folder.Open(_clock))
        {
            await InsertAsync(store.Store, (key, 1));
        }

        var file = Path.Combine(_folder.Root, "g_t", "pa_wallet", "p_%41%42%43%3A%3A1.json");
        File.Exists(file).ShouldBeTrue();
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["key"]!.GetValue<string>().ShouldBe("p_ABC::1");
        json["expiresAtUtc"].ShouldBeNull();
        json["content"]!["value"]!.GetValue<int>().ShouldBe(1);

        json["content"]!["value"] = 42;
        File.WriteAllText(file, json.ToJsonString());

        using var reopened = _folder.Open(_clock);
        reopened.Store.Peek(key)!["value"]!.GetValue<int>().ShouldBe(42);
    }

    [Fact]
    public async Task Reset_on_start_gives_an_empty_store()
    {
        using (var store = _folder.Open(_clock))
        {
            await InsertAsync(store.Store, (new DocumentKey("docs", "a"), 1));
        }

        using var reset = _folder.Open(_clock, reset: true);

        reset.Store.Count("docs").ShouldBe(0);
        Directory.Exists(_folder.GameFolder).ShouldBeFalse();
        File.Exists(Path.Combine(_folder.Root, "store.json")).ShouldBeTrue();
    }

    [Fact]
    public void Reset_is_refused_for_the_repository_root_the_content_root_its_ancestors_and_git_folders()
    {
        var repository = RepositoryRoot();
        var contentRoot = Path.Combine(repository, "samples", "SampleHost");

        Refusal(repository, contentRoot).ShouldBe("the content root or one of its ancestors");
        Refusal(contentRoot, contentRoot).ShouldBe("the content root or one of its ancestors");
        Refusal(Path.Combine(repository, "samples"), contentRoot).ShouldBe("the content root or one of its ancestors");
        Refusal(repository, _folder.Root).ShouldBe("a folder with a .git entry");
        Refusal(Path.GetPathRoot(repository)!, _folder.Root).ShouldBe("a file-system root");
        Refusal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _folder.Root).ShouldBe("the user profile folder");
        Refusal(_folder.Root, contentRoot).ShouldBeNull();

        // Through the store, on a throw-away folder holding a .git entry: refused before anything is created or deleted.
        Directory.CreateDirectory(Path.Combine(_folder.Root, ".git"));
        File.WriteAllText(Path.Combine(_folder.Root, "store.json"), """{"format":"gameserver-file-store","version":1}""");
        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock, reset: true));
        ex.Message.ShouldContain($"FileStore:ResetOnStart refused for '{_folder.Root}': it is a folder with a .git entry");
        Directory.Exists(Path.Combine(_folder.Root, ".git")).ShouldBeTrue();
        File.Exists(Path.Combine(_folder.Root, ".lock")).ShouldBeFalse();
    }

    [Fact]
    public void Reset_of_a_non_store_folder_is_refused_and_deletes_nothing()
    {
        Directory.CreateDirectory(_folder.Root);
        File.WriteAllText(Path.Combine(_folder.Root, "notes.txt"), "mine");

        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock, reset: true));

        ex.Message.ShouldContain("not a file store folder");
        File.Exists(Path.Combine(_folder.Root, "notes.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Reset_of_a_locked_folder_fails_at_the_lock_and_deletes_nothing()
    {
        using var running = _folder.Open(_clock);
        await InsertAsync(running.Store, (new DocumentKey("docs", "a"), 1));
        var before = _folder.Files();

        var ex = Should.Throw<GameServerStartupException>(() => _folder.Open(_clock, reset: true));

        ex.Message.ShouldContain("is used by another host");
        _folder.Files().ShouldBe(before);
    }

    [Fact]
    public async Task Deleting_the_folder_is_a_reset()
    {
        using (var store = _folder.Open(_clock))
        {
            await InsertAsync(store.Store, (new DocumentKey("docs", "a"), 1));
        }

        Directory.Delete(_folder.Root, recursive: true);

        using var fresh = _folder.Open(_clock);
        fresh.Store.Count("docs").ShouldBe(0);
    }

    [Fact]
    public async Task A_failure_before_the_commit_point_fails_the_transaction_and_writes_nothing()
    {
        using var store = _folder.Open(_clock);
        await InsertAsync(store.Store, (new DocumentKey("docs", "seed"), 0));
        var before = _folder.Files();

        // A folder where the journal's temporary file must go makes the write fail before the commit point.
        Directory.CreateDirectory(Path.Combine(_folder.Root, "journal.json.tmp"));
        var multi = await Should.ThrowAsync<TransactionFailedException>(() => InsertAsync(store.Store, (new DocumentKey("docs", "a"), 1), (new DocumentKey("docs", "b"), 2)));
        Directory.Delete(Path.Combine(_folder.Root, "journal.json.tmp"));

        var single = new DocumentKey("docs", "c");
        Directory.CreateDirectory(_folder.DocumentPath(single) + ".tmp");
        await Should.ThrowAsync<TransactionFailedException>(() => InsertAsync(store.Store, (single, 3)));
        Directory.Delete(_folder.DocumentPath(single) + ".tmp");

        multi.InnerException.ShouldNotBeNull();
        _folder.Files().ShouldBe(before);
        store.Store.Count("docs").ShouldBe(1);
        store.IsFaulted.ShouldBeFalse();
        await InsertAsync(store.Store, (single, 3)); // the store keeps working
    }

    [Fact]
    public async Task A_failure_after_the_commit_point_succeeds_faults_the_store_and_replays_at_restart()
    {
        var a = new DocumentKey("docs", "a");
        var b = new DocumentKey("docs", "b");
        var store = _folder.Open(_clock);
        try
        {
            // A folder where b's file must go: the journal commits, applying b fails.
            Directory.CreateDirectory(_folder.DocumentPath(b));

            await InsertAsync(store.Store, (a, 1), (b, 2));

            store.IsFaulted.ShouldBeTrue();
            store.Store.Peek(b).ShouldNotBeNull();
            File.Exists(Path.Combine(_folder.Root, "journal.json")).ShouldBeTrue();
            await Should.ThrowAsync<TransactionFailedException>(() => InsertAsync(store.Store, (new DocumentKey("docs", "c"), 3)));
            await Should.ThrowAsync<IOException>(() => ((IKeyValueStore)store.Store).InsertAsync(new DocumentKey("docs", "d"), FileStoreTestFolder.Doc(4), null, CancellationToken.None));
            await Should.ThrowAsync<IOException>(() => new FileStoreHealthProbe(store).CheckAsync(CancellationToken.None));
        }
        finally
        {
            store.Dispose();
        }

        Directory.Delete(_folder.DocumentPath(b));
        using var reopened = _folder.Open(_clock);
        reopened.Store.Peek(a)!["value"]!.GetValue<int>().ShouldBe(1);
        reopened.Store.Peek(b)!["value"]!.GetValue<int>().ShouldBe(2);
        reopened.Store.Peek(new DocumentKey("docs", "c")).ShouldBeNull();
        File.Exists(Path.Combine(_folder.Root, "journal.json")).ShouldBeFalse();
        await new FileStoreHealthProbe(reopened).CheckAsync(CancellationToken.None);
    }

    [Fact]
    public void A_folder_too_long_for_windows_fails_the_path_budget_check()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The path budget applies on Windows only (MAX_PATH).");
        }

        var registry = new ModuleRegistry();
        var shortFolder = new FileStoreSettings(_folder.Root, "sample", false, AppContext.BaseDirectory);
        var longFolder = shortFolder with { Folder = Path.Combine(_folder.Root, new string('x', 120)) };

        Check(new FileStoreCheck(shortFolder, registry, null)).ShouldBeEmpty();
        Check(new FileStoreCheck(longFolder, registry, null)).ShouldHaveSingleItem().ShouldContain("is too long for Windows");
        Check(new FileStoreCheck(shortFolder with { Folder = string.Empty }, registry, null)).ShouldHaveSingleItem().ShouldContain("FileStore:Folder is not set");
        FileStoreCheck.PathBudgetError(@"C:\d", "sample", ["receipts"]).ShouldBeNull();
    }

    private static List<string> Check(FileStoreCheck check)
    {
        var result = new StartupCheckResult();
        check.Check(result);
        return result.Errors;
    }

    private static string? Refusal(string folder, string contentRoot) => FileStoreFolder.ResetRefusal(folder, contentRoot);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameServer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("GameServer.slnx not found above the test output.");
    }
}
