namespace GameServer.Platform.Observability;

/// <summary>Free space of the volume holding a directory (log and analytics directories, LOG-03, R10).</summary>
internal static class DiskSpace
{
    /// <summary>
    /// Free bytes of the volume holding <paramref name="directory"/>; null when unknown. Only that path is queried (Windows:
    /// its drive root; Linux/macOS: <c>statvfs</c> of the path, i.e. the mount the directory really lives on), never the list
    /// of every mount, so a hung mount elsewhere (stale NFS) cannot block the probe. On Linux/macOS a directory not created yet
    /// is measured on its nearest existing parent; on Windows only the drive root is used (a UNC path has none: unknown, and
    /// no network access).
    /// </summary>
    public static long? FreeBytes(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var target = OperatingSystem.IsWindows() ? full : NearestExisting(full);
            return target is null ? null : new DriveInfo(target).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // IOException covers DriveNotFoundException; ArgumentException a UNC path on Windows (no drive letter).
            return null;
        }
    }

    private static string? NearestExisting(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
        }

        return null;
    }
}
