using Fixture.Game;
using Fixture.Host;
using GameServer.Modules.Gm;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Observability;
using GameServer.Platform.Observability.Analytics;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Requests;

// Copied from the framework's SampleHost (research R17) with the fixture's modules: the flavor is the host environment name,
// appsettings.{Flavor}.json over the shared base; --provision creates the Couchbase schema and exits.
HostMode mode;
WebApplication app;
try
{
    var commandLine = GameServerCommandLine.Parse(args);
    mode = commandLine.Mode;
    var builder = WebApplication.CreateBuilder(commandLine.HostArgs);
    var gameServer = builder.AddGameServer();
    gameServer.AddObservability();
    gameServer.AddAnalyticsFiles();
    switch (gameServer.Options.Store)
    {
        case StoreKind.Couchbase:
            var couchbase = builder.Configuration.GetSettings<CouchbaseOptions>(CouchbaseOptions.SectionName);
            builder.Services.AddCouchbaseDocumentStore(couchbase);
            break;
        case StoreKind.File:
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

    gameServer
        .AddModule<IdentityModule>()
        .AddModule<GmModule>()
        .AddModule<FixtureModule>();
    builder.Services.AddSingleton<OutcomeCounts>();

    app = builder.Build();

    // As a game would: per-request behaviour on the framework's outcome, outside the framework pipeline (FR-084).
    var outcomes = app.Services.GetRequiredService<OutcomeCounts>();
    app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        await next(context);
        outcomes.Add(context.RequestOutcome().ResultCode!);
    });
}
catch (GameServerStartupException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return HostExitCodes.InvalidConfiguration;
}

return await app.RunGameServerAsync(mode);

/// <summary>Entry point type, exposed for WebApplicationFactory.</summary>
public partial class Program;
