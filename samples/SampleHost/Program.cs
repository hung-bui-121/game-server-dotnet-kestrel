using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using Sample.Counter.Adapters;

var builder = WebApplication.CreateBuilder(args);

var gameServer = builder.AddGameServer();
if (gameServer.Options.Store == StoreKind.InMemory)
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
