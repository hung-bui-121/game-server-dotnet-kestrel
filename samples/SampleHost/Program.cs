using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using Sample.Counter.Adapters;

var builder = WebApplication.CreateBuilder(args);

var gameServer = builder.AddGameServer();
if (gameServer.Options.Store == StoreKind.Couchbase)
{
    // Secrets come from environment variables (Couchbase__Password), not from appsettings (OPS-08).
    var couchbase = builder.Configuration.GetSection(CouchbaseOptions.SectionName).Get<CouchbaseOptions>() ?? new CouchbaseOptions();
    builder.Services.AddCouchbaseDocumentStore(couchbase);
}
else
{
    builder.Services.AddInMemoryDocumentStore();
}

if (!gameServer.Options.IsProduction)
{
    gameServer.UseTestAuthentication();
}

gameServer
    .AddModule<IdentityModule>()
    .AddModule<CounterModule>();

var app = builder.Build();
try
{
    app.UseGameServer();
}
catch (GameServerStartupException ex)
{
    // Fail fast before serving traffic (ARC-06, ACC-26).
    await Console.Error.WriteLineAsync(ex.Message);
    Environment.ExitCode = 1;
    return;
}

await app.RunAsync();

/// <summary>Entry point type, exposed for WebApplicationFactory.</summary>
public partial class Program;
