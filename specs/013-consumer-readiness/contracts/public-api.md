# Contract — public C# surface added or changed by 013

Signatures only. Everything here is consumer-facing (game hosts, game modules, game tests) and recorded in PCR platform
§19; removing or changing a member after `v0.7.0` is a breaking change (ARC-08). Internal types named for the
implementer are marked `internal`.

## 1. Core (`GameServer.Core`)

```csharp
namespace GameServer.Errors;

/// <summary>Error-code bands (API-05, FR-060). Framework-owned modules never use the game band (architecture test).</summary>
public static class ErrorCodeBands
{
    public const int GameStart = 50000;
    public const int GameEnd = 89999;
    public const int FrameworkReservedStart = 90000;
    public const int FrameworkReservedEnd = 99999;
}
```

## 2. Platform (`GameServer.Platform`)

```csharp
namespace GameServer.Platform;

public sealed class GameServerOptions
{
    // existing members unchanged

    /// <summary>
    /// The single decision for the Dev profile (FR-006): startup validation, request pipeline and command runner allow
    /// Dev endpoints and commands only when true. Today equals AllowsDevelopmentTools (Development/Test); a future
    /// Staging switch (PCR platform §17 R13) would extend this property only, never for Production.
    /// </summary>
    public bool AllowsDevProfile { get; }

    /// <summary>Per-request event (FR-080..FR-086), section GameServer:RequestLog.</summary>
    public RequestLogSettings RequestLog { get; set; }
}

public sealed class RequestLogSettings
{
    /// <summary>Null = on in Development/Test, off in Staging/Production.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Paths never logged in addition to /health/live and /health/ready (segment prefix, case-insensitive).</summary>
    public string[] ExcludedPaths { get; set; }

    public bool IsEnabled(GameServerOptions options);

    /// <summary>Startup-report lines for invalid values; empty when valid.</summary>
    public IEnumerable<string> Validate();
}

/// <summary>Framework version (FR-050..FR-052).</summary>
public static class GameServerVersion
{
    /// <summary>Declared SemVer of the framework build files, e.g. "0.7.0".</summary>
    public static string Framework { get; }

    /// <summary>Informational version, e.g. "0.7.0+{sha}" (no suffix without git metadata).</summary>
    public static string FrameworkInformational { get; }

    /// <summary>SemVer core of an assembly's informational version (before '+'), else AssemblyName.Version (3 parts), else "unknown".</summary>
    public static string Of(System.Reflection.Assembly assembly);

    public static string InformationalOf(System.Reflection.Assembly assembly);
}

/// <summary>Identity and versions of the running instance; one singleton per host (FR-052..FR-055).</summary>
public sealed record GameServerRuntimeInfo(
    string GameId,
    string EnvironmentId,
    string DeploymentEnvironment,
    string InstanceId,
    string GameAssembly,
    string GameVersion,
    string FrameworkVersion)
{
    /// <summary>configured → HOSTNAME → machine name (LOG-03); the only implementation of the instance-id rule.</summary>
    public static string ResolveInstanceId(string? configured);
}
```

```csharp
namespace GameServer.Platform.Commands;

/// <summary>Commit rules shared by CommandRunner and the test kit (API-11, SEC-14). Messages unchanged.</summary>
internal static class CommitRules
{
    internal static string? CheckCode(
        GameServer.Commands.CommandRegistration registration,
        GameServer.Platform.Registry.ModuleRegistry registry,
        GameServer.Errors.ErrorCode? code);

    internal static string? CheckAudit(
        GameServer.Commands.CommandDescriptor descriptor,
        IReadOnlyList<GameServer.Platform.Outbox.OutboxEntry> outbox);
}
```

`GameServer.Platform.csproj`: `<InternalsVisibleTo Include="GameServer.TestKit" />` (for `CommandContext`, the
`LoadedAggregate` constructor and `CommitRules`).

## 3. Platform.Web (`GameServer.Platform.Web`)

```csharp
namespace GameServer.Platform.Web.Requests;

/// <summary>
/// Outcome of the current request (FR-084), on HttpContext.Features. Set by the framework for every response it writes;
/// a consumer endpoint may set ResultCode/flags for its own responses. After the framework pipeline completed (e.g. in
/// a consumer middleware placed before UseGameServer, after await next()), every member is final and ResultCode is never
/// null. Reading it never needs the response body.
/// </summary>
public interface IRequestOutcomeFeature
{
    string? ResultCode { get; set; }

    string? Operation { get; set; }

    bool Replayed { get; set; }

    bool NotModified { get; set; }

    /// <summary>HTTP status sent; null when the client aborted before the response started.</summary>
    int? StatusCode { get; }

    bool Aborted { get; }

    bool Completed { get; }
}

public static class RequestOutcomeExtensions
{
    /// <summary>The request's outcome feature (installed by the framework; created on first use otherwise). Never null.</summary>
    public static IRequestOutcomeFeature RequestOutcome(this Microsoft.AspNetCore.Http.HttpContext context);
}

/// <summary>Result codes that are not error-catalog entries (log/outcome only, never sent to clients).</summary>
public static class RequestResultCodes
{
    public const string Aborted = "ABORTED";

    /// <summary>Code of a non-2xx response the framework did not write: "HTTP_{status}".</summary>
    public static string ForStatus(int statusCode);
}

/// <summary>Logger category of the request event.</summary>
public static class RequestLog
{
    public const string Category = "GameServer.Requests";
    public static readonly Microsoft.Extensions.Logging.EventId EventId; // (1, "Request")
}

internal sealed class RequestLogMiddleware; // outermost middleware: installs/finalizes the feature, writes the event
```

```csharp
namespace GameServer.Platform.Web.Hosting;

public sealed class GameServerBuilder
{
    // existing members unchanged

    /// <summary>Identity and versions of this host, resolved by AddGameServer (also registered in DI).</summary>
    public GameServerRuntimeInfo Runtime { get; }
}
```

Behaviour changes without signature change:

| Member | Change |
| --- | --- |
| `GameServerApplicationExtensions.UseGameServer` | registers `RequestLogMiddleware` first; validates `RequestLog`; logs the startup version line after successful validation |
| `EndpointSecurityValidator.Validate` | Dev endpoint refused when `!AllowsDevProfile` (new message, BC-3) |
| `ProfileEnforcementMiddleware` | Dev refused before authentication when `!AllowsDevProfile`; unknown profile → 403 |
| `ErrorResponseWriter.WriteAsync` | sets `RequestOutcome().ResultCode` |
| `CommandEndpointFactory.MapPlayerCommand/MapDevCommand`, `GmCommandEndpointFactory` | set code, replayed, operation on the outcome |
| `CommandEndpointFactory.MapDevCommand` doc | "allowed only where development tools are allowed" |

## 4. Platform.Observability (`GameServer.Platform.Observability`)

```csharp
namespace GameServer.Platform.Observability;

public sealed class LoggingSettings
{
    // existing members unchanged
    public ConsoleLoggingSettings Console { get; set; }
}

public sealed class ConsoleLoggingSettings
{
    public bool Enabled { get; set; }            // false
    public string MinimumLevel { get; set; }     // "Information"
    public int BufferSize { get; set; }          // 1000
}

public sealed class ObservabilityOptions
{
    // unchanged signature; now delegates to GameServerRuntimeInfo.ResolveInstanceId(InstanceId)
    public string ResolveInstanceId();
}

internal sealed class ConsoleLineFormatter; // Serilog ITextFormatter: request events as one compact line
```

## 5. GM module (`GameServer.Modules.Gm.Adapters`)

`GmModule.VersionRoute`, `GmVersionResponse` — see [gm-ops-version.md](gm-ops-version.md).

## 6. Consumer test kit (`src/GameServer.TestKit`, namespace `GameServer.TestKit`)

No dependency on xUnit, Shouldly, `Sample.*`, `SampleGame` or any framework test project. Members only grow
(additive), all parameters added later are optional and last (slice 014 adds the FG-027 mapping check here).

### 6.1 HTTP helpers (moved from `tests/Shared`, namespace changed)

```csharp
public static class ProtobufHttp
{
    public const string ContentType = "application/x-protobuf";
    public static Task<HttpResponseMessage> PostProtobufAsync(this HttpClient client, string path, Google.Protobuf.IMessage message, string? requestId = null);
    public static Task<HttpResponseMessage> PostBytesAsync(this HttpClient client, string path, byte[] body, string? requestId = null, string contentType = ContentType);
    public static Task<T> ReadProtobufAsync<T>(this HttpResponseMessage response) where T : Google.Protobuf.IMessage<T>, new();
    public static Task<GameServer.Protocol.Core.V1.ErrorEnvelope> ReadErrorAsync(this HttpResponseMessage response);
    public static string NewRequestId();
}

public static class IdentityHttp
{
    public static byte[] NewSecret();
    public static string NewLocalId();
    public static Task<HttpResponseMessage> PostLoginAsync(this HttpClient client, string localId, byte[] secret);
    public static Task<GameServer.Modules.Identity.Protocol.V1.LoginResponse> LoginAsync(this HttpClient client, string localId, byte[] secret);
}

public enum TestStoreKind { InMemory, File }

public static class TestStores
{
    public const string Variable = "GAMESERVER_TEST_STORE";
    public static TestStoreKind Default { get; }
    public static string NewFolder();
    public static void DeleteFolder(string folder);
}

public static class GmBrowser
{
    public static HttpClient Browser(this Microsoft.AspNetCore.TestHost.TestServer server, System.Net.CookieContainer? cookies = null, bool https = false);
    public static HttpClient Browser(this TestHost host, System.Net.CookieContainer? cookies = null, bool https = false);
    public static Task<string> RefreshCsrfAsync(this HttpClient client);
    public static Task<HttpClient> GmLoginAsync(this Microsoft.AspNetCore.TestHost.TestServer server, string staffId, string password, System.Net.CookieContainer? cookies = null);
    public static Task<HttpClient> GmLoginAsync(this TestHost host, string staffId, string password, System.Net.CookieContainer? cookies = null);
}

public sealed class CookieJarHandler(System.Net.CookieContainer cookies) : DelegatingHandler
{
    public System.Net.CookieContainer Cookies { get; }
}
```

### 6.2 In-process test host

```csharp
public sealed class TestHost : IAsyncDisposable
{
    public WebApplication App { get; }
    public GameServer.Platform.Testing.InMemoryDocumentStore Store { get; }
    public GameServer.Platform.Testing.FakeClock Clock { get; }
    public IServiceProvider Services { get; }

    /// <summary>Formatted messages (unchanged, "no secret in logs" checks).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Logs { get; }

    /// <summary>Structured entries: category, level, event id, message, properties, exception.</summary>
    public IReadOnlyCollection<CapturedLog> LogEntries { get; }

    public GameServer.Platform.Testing.InMemoryAnalyticsSink Analytics { get; }
    public Task FlushAnalyticsAsync();

    public static Dictionary<string, string?> DefaultSettings();

    /// <summary>Identity only.</summary>
    public static void DefaultModules(GameServer.Platform.Web.Hosting.GameServerBuilder builder);

    public static Task<TestHost> StartAsync(
        Action<GameServer.Platform.Web.Hosting.GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        Action<WebApplication>? beforeUseGameServer = null,
        bool testAuthentication = true,
        Action<WebApplication>? afterUseGameServer = null,
        GameServer.Platform.Testing.InMemoryDocumentStore? store = null,
        GameServer.Platform.Testing.FakeClock? clock = null,
        Action<IServiceCollection>? services = null,
        string environmentName = "Development",
        TestStoreKind? storeKind = null,
        GameServer.Config.IGameConfig? config = null);

    public static WebApplication BuildApp(
        Action<GameServer.Platform.Web.Hosting.GameServerBuilder>? modules = null,
        Action<Dictionary<string, string?>>? settings = null,
        bool testAuthentication = true,
        Action<IServiceCollection>? services = null,
        string environmentName = "Development",
        GameServer.Config.IGameConfig? config = null);

    public HttpClient Client();
    public HttpClient PlayerClient(string playerId);
    public HttpClient BearerClient(string token);
    public HttpClient GmClient(string staffId, params string[] permissions);
    public ValueTask DisposeAsync();
}

public sealed record CapturedLog(
    string Category,
    Microsoft.Extensions.Logging.LogLevel Level,
    Microsoft.Extensions.Logging.EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);
```

### 6.3 Local-flavor factory

```csharp
public class LocalFlavorFactory<TEntryPoint> : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    public LocalFlavorFactory(LocalFlavorOptions? options = null);

    public string Root { get; }
    public string DataFolder { get; }
    public string AnalyticsFolder { get; }
    public string LogFolder { get; }
    public string InstanceId { get; }

    /// <summary>The settings actually applied (LocalFlavorSettings + overrides).</summary>
    public IReadOnlyDictionary<string, string?> AppliedSettings { get; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder);
    public override ValueTask DisposeAsync();
}

public sealed class LocalFlavorOptions
{
    /// <summary>ASP.NET Core environment = flavor file (appsettings.{Environment}.json). Default "Local".</summary>
    public string Environment { get; set; }

    /// <summary>Existing root to reuse (restart on the same data); the factory never deletes it.</summary>
    public string? Folder { get; set; }

    public bool OutboxSweeper { get; set; }

    /// <summary>Applied after LocalFlavorSettings (test overrides win).</summary>
    public IDictionary<string, string?> Settings { get; }

    /// <summary>Runs after the host's own registrations (ConfigureTestServices).</summary>
    public Action<IServiceCollection>? Services { get; set; }
}

/// <summary>The documented set of settings that keep a local-flavor host in tests away from the content root and background work (FR-012).</summary>
public static class LocalFlavorSettings
{
    public static IReadOnlyList<string> Keys { get; }
    public static IReadOnlyDictionary<string, string?> For(string root, string instanceId, bool outboxSweeper = false);
}
```

### 6.4 Game config and command-context double

```csharp
public static class TestGameConfig
{
    /// <summary>Label "test", no files.</summary>
    public static GameServer.Config.IGameConfig Empty { get; }

    /// <summary>Files given as JSON text; ConfigVersion computed exactly like the host loader.</summary>
    public static GameServer.Config.IGameConfig FromJson(
        IReadOnlyDictionary<string, string> files,
        string label = "test",
        IEnumerable<string>? clientVisible = null);

    /// <summary>The host loader itself: {configRoot}/base overlaid by {configRoot}/env/{deploymentEnvironment}.</summary>
    public static GameServer.Config.IGameConfig FromDirectory(string configRoot, string deploymentEnvironment = "Development");
}

/// <summary>
/// ICommandContext for handler unit tests without host, store or DI (FR-020..FR-026). Wraps the runner's own context, so
/// every access rule, exception type and message is the runner's. Configure first; the first ICommandContext call freezes
/// the configuration.
/// </summary>
public sealed class CommandContextDouble : GameServer.Commands.ICommandContext
{
    public static CommandContextDouble Create(string operation, params GameServer.Modules.IGameModule[] modules);

    // configuration (before the handler runs)
    public CommandContextDouble AsPlayer(string playerId);
    public CommandContextDouble AsGm(string staffId, string targetPlayerId, params string[] permissions);
    public CommandContextDouble WithActor(GameServer.Actors.ActorContext actor);
    public CommandContextDouble At(DateTimeOffset now);
    public CommandContextDouble WithConfig(GameServer.Config.IGameConfig config);
    public CommandContextDouble WithRequestId(string requestId);
    public CommandContextDouble WithReceiptId(string receiptId);
    public CommandContextDouble Seed<TState>(string aggregateId, TState state) where TState : class;
    public CommandContextDouble SeedClaim(string collection, string claimKey);
    public CommandContextDouble WithCommandSettings(GameServer.Platform.CommandSettings commands); // limits of CheckCommit (default: framework defaults)

    // ICommandContext (forwarded to the runner's context)
    public GameServer.Actors.ActorContext Actor { get; }
    public DateTimeOffset Now { get; }
    public string ConfigVersion { get; }
    public GameServer.Config.IGameConfig Config { get; }
    public TState Get<TState>(string aggregateId) where TState : class;
    public void Set<TState>(string aggregateId, TState state) where TState : class;
    public ValueTask<bool> TryClaimAsync(string collection, string claimKey);
    public ValueTask<bool> TryClaimAsync(string collection, string claimKey, IReadOnlyDictionary<string, string>? attributes);
    public void AddOutbox(string kind, object payload);
    public string NewId(string purpose);
    public long NewSeed(string purpose);

    // recorded (call order)
    public GameServer.Commands.CommandDescriptor Descriptor { get; }
    public string ReceiptId { get; }
    public IReadOnlyList<RecordedWrite> Writes { get; }   // what the runner writes: a Set of the loaded state (or a change set back) is no write
    public TState? Written<TState>(string aggregateId) where TState : class;
    public IReadOnlyList<GameServer.Platform.Outbox.OutboxEntry> Outbox { get; }
    public IReadOnlyList<RecordedClaim> Claims { get; }

    /// <summary>
    /// What the runner would refuse at commit (FR-026), with the runner's rules (CommitRules): uncatalogued or non-200 commit
    /// code, written aggregate above its HardBytes (runner: AGGREGATE_TOO_LARGE; envelope measured with the actor's
    /// GameId/EnvironmentId), GM command without exactly one audit entry, non-GM command with one. Empty for a rejection or
    /// a committable outcome. Messages = runner messages. Never checked: the receipt size (ReceiptMaxBytes).
    /// </summary>
    public IReadOnlyList<string> CheckCommit(GameServer.Commands.ICommandOutcome outcome);

    /// <summary>Plus the resultCompact limit (ResultCompactMaxBytes) on encodeResult(outcome.Result), the endpoint's encoding.</summary>
    public IReadOnlyList<string> CheckCommit(GameServer.Commands.ICommandOutcome outcome, Func<object, byte[]> encodeResult);
}

public sealed record RecordedWrite(string AggregateId, object State);

public sealed record RecordedClaim(string Collection, string Key, IReadOnlyDictionary<string, string> Attributes);
```

Typical handler test (xUnit shown; the kit itself has no test framework):

```csharp
var ctx = CommandContextDouble.Create(FixtureModule.SaveOperation, new IdentityModule(), new FixtureModule())
    .AsPlayer("p_1")
    .Seed(FixtureAggregates.Notes, new NotesState { Text = "old" });
var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("new"), ctx);
ctx.CheckCommit(outcome).ShouldBeEmpty();
ctx.Written<NotesState>(FixtureAggregates.Notes)!.Text.ShouldBe("new");
```

## 7. Framework test support that stays in `tests/Shared` (not consumer API)

```csharp
namespace GameServer.Tests.Shared;

/// <summary>Kit TestHost with the framework's old defaults: Identity + Sample.Counter, sample config folder.</summary>
public static class SampleTestHost
{
    public static Dictionary<string, string?> DefaultSettings();
    public static void DefaultModules(GameServer.Platform.Web.Hosting.GameServerBuilder builder);
    public static Task<TestHost> StartAsync(/* same parameters as TestHost.StartAsync */);
    public static WebApplication BuildApp(/* same parameters as TestHost.BuildApp */);
}

/// <summary>Direct file-store construction for the file-store tests (was TestStores.CreateFileStore).</summary>
public static class FileStoreTestSupport
{
    public static GameServer.Platform.Testing.FileDocumentStore Create(string folder, string gameId, GameServer.Time.IClock clock, Microsoft.Extensions.Logging.ILogger<GameServer.Platform.Testing.FileDocumentStore> logger);
}
```
