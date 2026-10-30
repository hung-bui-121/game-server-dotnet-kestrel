using GameServer.Modules.Gm;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Auth;
using GameServer.Platform.Web.Hosting;
using GameServer.Tests.Shared;
using GameServer.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GameServer.Platform.Couchbase.IntegrationTests.Infrastructure;

/// <summary>
/// One instance of the full sample game (plus GM) on TestServer, backed by the fixture's Couchbase scope. Several hosts of a
/// test share the fixture's connection (one scope, one transaction metadata collection) and one <see cref="FakeClock"/>, like
/// instances of one deployment. Each host provisions its own collections and indexes on start (Development).
/// </summary>
public sealed class CouchbaseGameHost : IAsyncDisposable
{
    private CouchbaseGameHost(WebApplication app, FakeClock clock)
    {
        App = app;
        Clock = clock;
    }

    public WebApplication App { get; }

    public FakeClock Clock { get; }

    public IServiceProvider Services => App.Services;

    public static async Task<CouchbaseGameHost> StartAsync(
        CouchbaseFixture fixture,
        FakeClock clock,
        Action<Dictionary<string, string?>>? settings = null,
        Action<GameServerBuilder>? extraModules = null,
        Action<IServiceCollection>? services = null)
    {
        var values = TestHost.DefaultSettings();
        values["GameServer:GameId"] = fixture.GameServer.GameId;
        values["GameServer:EnvironmentId"] = fixture.GameServer.EnvironmentId;
        values["GameServer:Store"] = nameof(StoreKind.Couchbase);
        settings?.Invoke(values);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();

        var gameServer = builder.AddGameServer();
        builder.Services.AddCouchbaseDocumentStore(fixture.Options);

        // The fixture's cluster connection is shared by the hosts of a test class; an instance registration is not disposed with a host.
        builder.Services.RemoveAll<CouchbaseConnection>();
        builder.Services.AddSingleton(fixture.Connection);
        builder.Services.RemoveAll<IClock>();
        builder.Services.AddSingleton<IClock>(clock);
        gameServer.UseTestAuthentication();
        GameHttp.FullGame(gameServer.AddModule<GmModule>());
        extraModules?.Invoke(gameServer);
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseGameServer();
        await app.StartAsync();
        return new CouchbaseGameHost(app, clock);
    }

    public HttpClient Client() => App.GetTestClient();

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
    }
}
