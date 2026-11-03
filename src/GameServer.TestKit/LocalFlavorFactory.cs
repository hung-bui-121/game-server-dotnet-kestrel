using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace GameServer.TestKit;

/// <summary>
/// The real host entry point on a flavor file (default <c>Local</c>) with <see cref="LocalFlavorSettings"/> applied to one
/// root of its own (<c>%TEMP%/gs-local-{guid}</c> with <c>data/</c>, <c>analytics/</c>, <c>logs/</c>) and a unique instance id
/// (<c>lf-{8 hex}</c>), so parallel factories never share a folder or a lock and nothing is written next to the host
/// (FR-012). Disposing stops the host, waits until it released its files (logs, file-store and analytics locks), then deletes
/// the root unless it was given through <see cref="LocalFlavorOptions.Folder"/>.
/// </summary>
public class LocalFlavorFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>Retries while the host releases its files: 100 x 50 ms = 5 s, far beyond a normal stop even under full-suite load.</summary>
    private const int ReleaseAttempts = 100;

    private static readonly TimeSpan ReleaseRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly LocalFlavorOptions _options;
    private readonly bool _ownsRoot;

    public LocalFlavorFactory(LocalFlavorOptions? options = null)
    {
        _options = options ?? new LocalFlavorOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Environment, nameof(options));
        _ownsRoot = _options.Folder is null;
        Root = _options.Folder ?? Path.Combine(Path.GetTempPath(), "gs-local-" + Guid.NewGuid().ToString("N"));
        InstanceId = "lf-" + Guid.NewGuid().ToString("N")[..8];

        var applied = new Dictionary<string, string?>(LocalFlavorSettings.For(Root, InstanceId, _options.OutboxSweeper), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _options.Settings)
        {
            applied[key] = value;
        }

        AppliedSettings = applied;
    }

    public string Root { get; }

    public string DataFolder => LocalFlavorSettings.DataFolderOf(Root);

    public string AnalyticsFolder => LocalFlavorSettings.AnalyticsFolderOf(Root);

    public string LogFolder => LocalFlavorSettings.LogFolderOf(Root);

    public string InstanceId { get; }

    /// <summary>The settings actually applied (<see cref="LocalFlavorSettings"/> + <see cref="LocalFlavorOptions.Settings"/>).</summary>
    public IReadOnlyDictionary<string, string?> AppliedSettings { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(_options.Environment);
        foreach (var (key, value) in AppliedSettings)
        {
            builder.UseSetting(key, value);
        }

        if (_options.Services is { } services)
        {
            builder.ConfigureTestServices(services);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_ownsRoot)
        {
            await DeleteRootAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// With minimal hosting the entry point's own <c>RunAsync</c> disposes the host too, so the file logger, the file store
    /// and the analytics lock can still be open when <see cref="WebApplicationFactory{TEntryPoint}.DisposeAsync"/> returns
    /// (measured: the log file outlived <see cref="TestStores.DeleteFolder"/>'s short retries under full-suite load). Waits for
    /// the release, bounded; a handle still open after that is a leak and fails the test instead of leaving the root behind.
    /// </summary>
    private async Task DeleteRootAsync()
    {
        for (var attempt = 1; Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReleaseAttempts)
                {
                    throw new IOException(
                        $"LocalFlavorFactory could not delete its root '{Root}' within {ReleaseAttempts * ReleaseRetryDelay.TotalSeconds:0} s " +
                        "after the host stopped: a file under it is still open.",
                        ex);
                }

                await Task.Delay(ReleaseRetryDelay);
            }
        }
    }
}
