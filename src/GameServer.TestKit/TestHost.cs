using System.Collections.Concurrent;
using GameServer.Config;
using GameServer.Modules.Identity;
using GameServer.Platform.Analytics;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Hosting;
using GameServer.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GameServer.TestKit;

/// <summary>
/// Builds a framework host on TestServer with the in-memory store, a fake clock and test authentication.
/// Unlike WebApplicationFactory it lets each test choose modules, settings and extra endpoints, and lets two hosts
/// share one store and clock to play two instances (ACC-20). With <see cref="TestStoreKind.File"/> (or
/// <c>GAMESERVER_TEST_STORE=File</c> when no store is passed) the host runs on a file store in a temporary folder and
/// <see cref="Store"/> is its inner in-memory store; the folder is deleted with the host.
/// Defaults: Identity only, game config = the <c>config</c> argument, else the loader on <c>GameServer:ConfigPath</c> when the
/// settings set it, else <see cref="TestGameConfig.Empty"/>.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private const string ConfigPathKey = "GameServer:ConfigPath";

    private readonly FileDocumentStore? _fileStore;
    private readonly LogCapture _capture;

    private TestHost(WebApplication app, InMemoryDocumentStore store, FakeClock clock, LogCapture capture, FileDocumentStore? fileStore)
    {
        App = app;
        Store = store;
        Clock = clock;
        _capture = capture;
        _fileStore = fileStore;
    }

    public WebApplication App { get; }

    public InMemoryDocumentStore Store { get; }

    public FakeClock Clock { get; }

    /// <summary>Every formatted log message (for "no secret in logs" checks, ACC-25).</summary>
    public ConcurrentQueue<string> Logs => _capture.Logs;

    /// <summary>Every log call with category, level, event id, message, structured properties and exception, in call order.</summary>
    public IReadOnlyCollection<CapturedLog> LogEntries => _capture.Entries;

    public IServiceProvider Services => App.Services;

    /// <summary>
    /// The in-memory analytics sink every test host registers (slice 012); throws when a test removed it through the
    /// <c>services</c> hook.
    /// </summary>
    public InMemoryAnalyticsSink Analytics =>
        Services.GetService<InMemoryAnalyticsSink>() ?? throw new InvalidOperationException("This test host has no InMemoryAnalyticsSink.");

    /// <summary>Waits until every analytics record queued so far reached the sinks.</summary>
    public Task FlushAnalyticsAsync() => Services.GetRequiredService<AnalyticsPipeline>().FlushAsync(CancellationToken.None);

    public static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["GameServer:GameId"] = "test",
        // Not a standard flavor (slice 011 D9): tests may switch the deployment environment without tripping FR-004.
        ["GameServer:EnvironmentId"] = "test",
        ["GameServer:DeploymentEnvironment"] = "Development",
        ["GameServer:Outbox:SweeperEnabled"] = "false",
        // Lease jobs run only through LeaseJobRunner.RunOnceAsync unless a test enables the loop.
        ["GameServer:Jobs:Enabled"] = "false",
        ["Identity:CacheSeconds"] = "0",
        ["GameServer:Hosting:DrainDelaySeconds"] = "0",
        // Analytics is active in every test host (in-memory sink): a zone keeps Staging/Production starts valid.
        ["GameServer:Analytics:TimeZone"] = "UTC",
    };

    /// <summary>Default modules: Identity only.</summary>
    public static void DefaultModules(GameServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddModule<IdentityModule>();
    }

    /// <summary>Builds and starts the app; <see cref="GameServerApplicationExtensions.UseGameServer"/> exceptions propagate.</summary>
    public static async Task<TestHost> StartAsync(
        Action<GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null,
        bool testAuthentication = true,
        Action<WebApplication>? afterUseGameServer = null,
        InMemoryDocumentStore? store = null,
        FakeClock? clock = null,
        Action<IServiceCollection>? services = null,
        string environmentName = "Development",
        TestStoreKind? storeKind = null,
        IGameConfig? config = null)
    {
        var capture = new LogCapture();
        clock ??= new FakeClock();
        var values = DefaultSettings();
        settings?.Invoke(values);

        // An explicit store is shared with another host (two instances): it stays what it is.
        FileDocumentStore? fileStore = null;
        if (store is null && (storeKind ?? TestStores.Default) == TestStoreKind.File)
        {
            fileStore = TestStores.CreateFileStore(
                TestStores.NewFolder(), values["GameServer:GameId"] ?? string.Empty, clock, capture.CreateLogger<FileDocumentStore>());
        }

        store ??= fileStore?.Store ?? new InMemoryDocumentStore(clock);
        try
        {
            var app = Compose(modules, values, testAuthentication, store, fileStore, clock, services, environmentName, capture, config);
            beforeUseGameServer?.Invoke(app);
            app.UseGameServer();
            afterUseGameServer?.Invoke(app);
            await app.StartAsync();
            return new TestHost(app, store, clock, capture, fileStore);
        }
        catch when (fileStore is not null)
        {
            DisposeFileStore(fileStore);
            throw;
        }
    }

    /// <summary>
    /// Builds the app on the in-memory store without <see cref="GameServerApplicationExtensions.UseGameServer"/> and without
    /// starting it (run modes, provisioning). The caller disposes it.
    /// </summary>
    public static WebApplication BuildApp(
        Action<GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        bool testAuthentication = true,
        Action<IServiceCollection>? services = null,
        string environmentName = "Development",
        IGameConfig? config = null)
    {
        var clock = new FakeClock();
        var values = DefaultSettings();
        settings?.Invoke(values);
        return Compose(modules, values, testAuthentication, new InMemoryDocumentStore(clock), null, clock, services, environmentName, new LogCapture(), config);
    }

    private static WebApplication Compose(
        Action<GameServerBuilder>? modules,
        Dictionary<string, string?> values,
        bool testAuthentication,
        InMemoryDocumentStore store,
        FileDocumentStore? fileStore,
        FakeClock clock,
        Action<IServiceCollection>? services,
        string environmentName,
        LogCapture capture,
        IGameConfig? config)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        RemoveFlavorFile(builder.Configuration, environmentName);
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(capture.CreateProvider());

        var gameServer = builder.AddGameServer();
        if (fileStore is null)
        {
            builder.Services.AddInMemoryDocumentStore(store);
        }
        else
        {
            builder.Services.AddFileDocumentStore(fileStore);
        }

        // The decision reads the test's settings only: a host appsettings.json copied into the test output (ConfigPath
        // "config") must not switch a kit host to files the test never chose.
        var useLoader = config is null && !string.IsNullOrEmpty(values.GetValueOrDefault(ConfigPathKey));
        if (!useLoader)
        {
            builder.Services.RemoveAll<IGameConfig>();
            builder.Services.AddSingleton(config ?? TestGameConfig.Empty);
        }

        builder.Services.RemoveAll<IClock>();
        builder.Services.AddSingleton<IClock>(clock);
        var analytics = new InMemoryAnalyticsSink();
        builder.Services.AddSingleton(analytics);
        builder.Services.AddSingleton<IAnalyticsSink>(analytics);
        if (testAuthentication)
        {
            gameServer.UseTestAuthentication();
        }

        (modules ?? DefaultModules)(gameServer);
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>
    /// A referenced host's flavor files (<c>appsettings.Local.json</c>, …) are copied into every test output that references it.
    /// A test host sets every key itself and never runs a host flavor, so its environment name must not load one (slice 011, T025).
    /// </summary>
    private static void RemoveFlavorFile(ConfigurationManager configuration, string environmentName)
    {
        var flavorFile = $"appsettings.{environmentName}.json";
        foreach (var source in configuration.Sources.OfType<FileConfigurationSource>().Where(s => string.Equals(s.Path, flavorFile, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            configuration.Sources.Remove(source);
        }
    }

    public HttpClient Client() => App.GetTestClient();

    public HttpClient PlayerClient(string playerId)
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, playerId);
        return client;
    }

    public HttpClient BearerClient(string token)
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient GmClient(string staffId, params string[] permissions)
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.GmHeader, staffId);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.GmPermissionsHeader, string.Join(",", permissions));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        if (_fileStore is not null)
        {
            DisposeFileStore(_fileStore);
        }
    }

    /// <summary>An instance registration is not disposed by the container: release the folder lock, then delete the folder.</summary>
    private static void DisposeFileStore(FileDocumentStore fileStore)
    {
        fileStore.Dispose();
        TestStores.DeleteFolder(fileStore.Folder);
    }
}
