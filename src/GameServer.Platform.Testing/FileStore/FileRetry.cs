namespace GameServer.Platform.Testing;

/// <summary>
/// Rename/delete with a bounded retry on Windows sharing violations (antivirus, indexer holding a file briefly; research R8).
/// Runs under the store lock, so it waits synchronously.
/// </summary>
internal static class FileRetry
{
    /// <summary>Retries after the first attempt.</summary>
    public const int Retries = 5;

    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(20);

    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    public static void Run(Action operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (attempt < Retries && IsTransient(ex))
            {
                Thread.Sleep(Delay);
            }
        }
    }

    public static void Move(string source, string target) => Run(() => File.Move(source, target, overwrite: true));

    /// <summary>Deletes a file; a missing file or folder is not an error.</summary>
    public static void Delete(string path) => Run(() =>
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    });

    /// <summary>Best-effort cleanup of a temporary file after a failure; never throws.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start deletes every *.tmp (research R8).
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException io && (io.HResult & 0xFFFF) is SharingViolation or LockViolation);
}
