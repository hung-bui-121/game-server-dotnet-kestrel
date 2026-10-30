using GameServer.Modules.Economy;
using GameServer.Modules.Gm;
using GameServer.Modules.Iap;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Observability;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using Sample.Counter.Adapters;
using SampleGame.Adapters;

// The flavor is the host environment name (Local, Dev, Beta, Uat, Live): appsettings.{Flavor}.json over the shared base.
// --provision creates the Couchbase schema and exits (contracts/operations-cli.md).
HostMode mode;
WebApplication app;
try
{
    // Inside the try: a misspelled switch (--provision=x, /provision) is refused with exit 1.
    var commandLine = GameServerCommandLine.Parse(args);
    mode = commandLine.Mode;
    var builder = WebApplication.CreateBuilder(commandLine.HostArgs);
    var gameServer = builder.AddGameServer();

    // File logs per instance, OpenTelemetry (OTLP when Observability:Otlp:Endpoint is set), /health/live and /health/ready.
    gameServer.AddObservability();
    switch (gameServer.Options.Store)
    {
        case StoreKind.Couchbase:
            // Secrets come from environment variables (Couchbase__Password), not from appsettings (OPS-08).
            // GetSettings: a value of the wrong type (Couchbase__KvTimeoutMs=abc) is a startup error, not a crash.
            var couchbase = builder.Configuration.GetSettings<CouchbaseOptions>(CouchbaseOptions.SectionName);
            builder.Services.AddCouchbaseDocumentStore(couchbase);
            break;
        case StoreKind.File:
            // Local flavor: FileStore:Folder, relative to the content root (refused outside Development/Test).
            builder.Services.AddFileDocumentStore(builder.Configuration, builder.Environment);
            break;
        default:
            builder.Services.AddInMemoryDocumentStore();
            break;
    }

    if (gameServer.Options.AllowsDevelopmentTools)
    {
        gameServer.UseTestAuthentication();
    }

    // SampleGame (G7): Login (identity), StartBattle/EndBattle/BuyVirtualItem (sample.game), IAPurchase (iap).
    gameServer
        .AddModule<IdentityModule>()
        .AddModule<EconomyModule>()
        .AddModule<IapModule>()
        .AddModule<SampleGameModule>()
        .AddModule<CounterModule>()
        .AddModule<GmModule>();

    if (gameServer.Options.AllowsDevelopmentTools)
    {
        // Cheat endpoints (OPS-09) never exist outside Development/Test.
        gameServer.AddModule<CheatModule>();
    }

    app = builder.Build();
}
catch (GameServerStartupException ex)
{
    // The command line, configuration binding and composition (AddObservability) refuse invalid settings by design: fail fast
    // before serving traffic (ARC-06).
    await Console.Error.WriteLineAsync(ex.Message);
    return HostExitCodes.InvalidConfiguration;
}

return await app.RunGameServerAsync(mode);

/// <summary>Entry point type, exposed for WebApplicationFactory.</summary>
public partial class Program;
