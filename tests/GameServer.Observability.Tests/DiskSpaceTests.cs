using GameServer.Platform.Observability;
using Shouldly;

namespace GameServer.Observability.Tests;

/// <summary>Free space of the log/analytics directory volume, queried for that path only (review T066 minor 5).</summary>
public sealed class DiskSpaceTests : IDisposable
{
    private readonly TempLogDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_existing_directory_reports_the_free_space_of_its_volume()
    {
        var free = DiskSpace.FreeBytes(_temp.Path).ShouldNotBeNull();

        free.ShouldBeGreaterThan(0);
        // Other processes write meanwhile: same volume = within 512 MiB of the drive root's figure.
        var root = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_temp.Path))!).AvailableFreeSpace;
        Math.Abs(free - root).ShouldBeLessThan(512L * 1024 * 1024);
    }

    [Fact]
    public void A_directory_not_created_yet_is_measured_on_its_nearest_existing_parent()
    {
        var missing = Path.Combine(_temp.Path, "not", "yet", "created");

        DiskSpace.FreeBytes(missing).ShouldNotBeNull().ShouldBeGreaterThan(0);
        Directory.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public void A_path_without_a_local_volume_is_unknown_not_an_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths have no drive letter only on Windows.");

        DiskSpace.FreeBytes(@"\\gameserver-no-such-host.invalid\share\analytics").ShouldBeNull();
    }
}
