using GameServer.Modules.Identity;
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
using Sample.Counter.Adapters;

namespace GameServer.Tests.Shared;

/// <summary>
/// Builds a framework host on TestServer with the in-memory store, a fake clock and test authentication.
/// Unlike WebApplicationFactory it lets each test choose modules, settings and extra endpoints.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private TestHost(WebApplication app, InMemoryDocumentStore store, FakeClock clock)
    {
        App = app;
        Store = store;
        Clock = clock;
    }

    public WebApplication App { get; }

    public InMemoryDocumentStore Store { get; }

    public FakeClock Clock { get; }

    public IServiceProvider Services => App.Services;

    public static Dictionary<string, string?> DefaultSettings() => new()
    {
        ["GameServer:GameId"] = "test",
        ["GameServer:EnvironmentId"] = "dev",
        ["GameServer:DeploymentEnvironment"] = "Development",
        ["GameServer:ConfigPath"] = Path.Combine(AppContext.BaseDirectory, "config"),
        ["GameServer:Outbox:SweeperEnabled"] = "false",
    };

    /// <summary>Default modules: Identity + Sample.Counter.</summary>
    public static void DefaultModules(GameServerBuilder builder) => builder.AddModule<IdentityModule>().AddModule<CounterModule>();

    /// <summary>Builds the app; <see cref="GameServerApplicationExtensions.UseGameServer"/> exceptions propagate.</summary>
    public static async Task<TestHost> StartAsync(
        Action<GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null,
        bool testAuthentication = true,
        Action<WebApplication>? afterUseGameServer = null)
    {
        var values = DefaultSettings();
        settings?.Invoke(values);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();

        var clock = new FakeClock();
        var store = new InMemoryDocumentStore(clock);
        var gameServer = builder.AddGameServer();
        builder.Services.AddInMemoryDocumentStore(store);
        builder.Services.RemoveAll<IClock>();
        builder.Services.AddSingleton<IClock>(clock);
        if (testAuthentication)
        {
            gameServer.UseTestAuthentication();
        }

        (modules ?? DefaultModules)(gameServer);

        var app = builder.Build();
        beforeUseGameServer?.Invoke(app);
        app.UseGameServer();
        afterUseGameServer?.Invoke(app);
        await app.StartAsync();
        return new TestHost(app, store, clock);
    }

    public HttpClient Client() => App.GetTestClient();

    public HttpClient PlayerClient(string playerId)
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PlayerHeader, playerId);
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
    }
}
