using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GameServer.Client.Tests;

/// <summary>Makes a folder refuse new files for the current user (ACL deny on Windows, mode 0555 on Unix) until disposed.</summary>
internal static class ReadOnlyFolder
{
    public static IDisposable DenyWrites(string folder) => OperatingSystem.IsWindows() ? DenyWritesByAcl(folder) : DenyWritesByMode(folder);

    [SupportedOSPlatform("windows")]
    private static IDisposable DenyWritesByAcl(string folder)
    {
        var directory = new DirectoryInfo(folder);
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.AppendData,
            AccessControlType.Deny);
        var acl = directory.GetAccessControl();
        acl.AddAccessRule(rule);
        directory.SetAccessControl(acl);
        return new Restore(() => AllowWrites(directory, rule));
    }

    [SupportedOSPlatform("windows")]
    private static void AllowWrites(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        var acl = directory.GetAccessControl();
        acl.RemoveAccessRule(rule);
        directory.SetAccessControl(acl);
    }

    [UnsupportedOSPlatform("windows")]
    private static IDisposable DenyWritesByMode(string folder)
    {
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return new Restore(() => RestoreMode(folder, mode));
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestoreMode(string folder, UnixFileMode mode) => File.SetUnixFileMode(folder, mode);

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
