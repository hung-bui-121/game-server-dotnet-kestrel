namespace GameServer.Observability.Tests;

/// <summary>
/// A private log directory under the temp folder (<c>gs-logs-*</c>), deleted on dispose. Tests that read the log files after the
/// host stopped own one; <see cref="ObservedHost"/> owns one when none is passed. Deletion is best effort: a file the sink still
/// holds is retried briefly, then left (never fails a test).
/// </summary>
public sealed class TempLogDirectory : IDisposable
{
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gs-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        for (var attempt = 1; attempt <= DeleteAttempts && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }
}
