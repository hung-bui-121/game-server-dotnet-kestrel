using System.Text;
using Shouldly;

namespace GameServer.WireGen.Tests;

/// <summary>Slice 014 T049 (FR-023, FR-026, FR-027, US2-3, US2-6, US2-7): normalization, write-if-changed, verify, <c>.meta</c> untouched.</summary>
public sealed class OutputWriterTests : IDisposable
{
    private const string Text = "﻿line one\r\nline two\nline three\n";

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Files_are_normalized_to_the_line_ending_without_bom()
    {
        OutputWriter.Normalize("A.cs", Text, LineEnding.Lf).Bytes.ShouldBe(Encoding.UTF8.GetBytes("line one\nline two\nline three\n"));
        OutputWriter.Normalize("A.cs", Text, LineEnding.Crlf).Bytes.ShouldBe(Encoding.UTF8.GetBytes("line one\r\nline two\r\nline three\r\n"));
        OutputWriter.Normalize("sub\\A.cs", "x", LineEnding.Lf).RelativePath.ShouldBe("sub/A.cs");
    }

    [Fact]
    public void An_unchanged_file_keeps_its_timestamp_and_a_changed_one_is_rewritten()
    {
        var directory = _tree.Folder("client/Generated");
        var unchanged = OutputWriter.Normalize("Same.cs", "same\n", LineEnding.Lf);
        var changed = OutputWriter.Normalize("Changed.cs", "new\n", LineEnding.Lf);
        _tree.Write("client/Generated/Same.cs", "same\n");
        _tree.Write("client/Generated/Changed.cs", "old\n");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_tree.Path("client/Generated/Same.cs"), stamp);

        var report = OutputWriter.Generate(_tree.Path("client"), directory, [unchanged, changed]);

        report.Written.ShouldBe(["Generated/Changed.cs"]);
        report.Created.ShouldBeEmpty();
        report.Unchanged.ShouldBe(1);
        File.GetLastWriteTimeUtc(_tree.Path("client/Generated/Same.cs")).ShouldBe(stamp);
        File.ReadAllText(_tree.Path("client/Generated/Changed.cs")).ShouldBe("new\n");
        Directory.GetFiles(directory, ".*").ShouldBeEmpty("no temp file is left behind");
    }

    [Fact]
    public void New_files_are_reported_and_by_root_subfolders_are_created_inside_the_directory()
    {
        var directory = _tree.Folder("client/Generated");

        var report = OutputWriter.Generate(_tree.Path("client"), directory, [OutputWriter.Normalize("gameserver/core/v1/Core.cs", "x\n", LineEnding.Lf)]);

        report.Created.ShouldBe(["Generated/gameserver/core/v1/Core.cs"]);
        File.Exists(_tree.Path("client/Generated/gameserver/core/v1/Core.cs")).ShouldBeTrue();
    }

    [Fact]
    public void Verify_lists_edited_and_missing_files_and_writes_nothing()
    {
        var directory = _tree.Folder("client/Generated");
        _tree.Write("client/Generated/Edited.cs", "edited\n");
        _tree.Write("client/Generated/Same.cs", "same\n");
        var before = _tree.Snapshot();

        var report = OutputWriter.Verify(
            _tree.Path("client"),
            directory,
            [
                OutputWriter.Normalize("Edited.cs", "generated\n", LineEnding.Lf),
                OutputWriter.Normalize("Missing.cs", "generated\n", LineEnding.Lf),
                OutputWriter.Normalize("Same.cs", "same\n", LineEnding.Lf),
            ]);

        report.Differences.ShouldBe(["differs: Generated/Edited.cs", "missing: Generated/Missing.cs"]);
        report.Unchanged.ShouldBe(1);
        _tree.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void A_meta_file_and_a_stray_file_are_left_alone()
    {
        var directory = _tree.Folder("client/Generated");
        _tree.Write("client/Generated/Game.cs.meta", "guid: 0123\n");
        _tree.Write("client/Generated/Stray.cs", "mine\n");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_tree.Path("client/Generated/Game.cs.meta"), stamp);
        File.SetLastWriteTimeUtc(_tree.Path("client/Generated/Stray.cs"), stamp);

        OutputWriter.Generate(_tree.Path("client"), directory, [OutputWriter.Normalize("Game.cs", "generated\n", LineEnding.Lf)]);

        File.ReadAllText(_tree.Path("client/Generated/Game.cs.meta")).ShouldBe("guid: 0123\n");
        File.GetLastWriteTimeUtc(_tree.Path("client/Generated/Game.cs.meta")).ShouldBe(stamp);
        File.ReadAllText(_tree.Path("client/Generated/Stray.cs")).ShouldBe("mine\n");
        File.GetLastWriteTimeUtc(_tree.Path("client/Generated/Stray.cs")).ShouldBe(stamp);
    }
}
