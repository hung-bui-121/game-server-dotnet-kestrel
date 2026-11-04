# Consumer guide — building a game on the framework

> Matches framework 0.7.0 (unreleased). Once tagged: `v0.7.0`.

This guide is for a **game repository** that uses this framework as a git submodule (the first consumer is
`zh-pvp-server`). Every code sample below is taken from `tests/ConsumerFixture`, a consumer-shaped repository inside the
framework that is built and tested on every framework build; only the path to the framework changes (the fixture
finds it two folders up, a game finds it in `framework/`). When this guide and the fixture differ, the fixture is right
and this guide has a bug. One known difference is by design: the fixture sits inside the framework tree and inherits
`framework/.editorconfig`; a game repository copies it (section 2).

Vietnamese pages on the same topics: `docs/guides/module-authoring.md` (writing a module),
`docs/guides/environments-and-local-run.md` (flavors, run-local, request log, versions), `docs/html/02-them-command.html`
(adding a command, with in-repo and consumer-repo variants).

## 1. Repository layout and submodule

```text
<game>/
  framework/                  # this repository, git submodule pinned to an annotated tag
  src/<Game>/                 # game modules (business + adapters)
  src/<Game>.Host/            # the game's host (entry point)
  tests/<Game>.Tests/         # game tests on the framework test kit
  Directory.Build.props       # the game's build settings (section 2)
  Directory.Packages.props    # imports the framework's package versions (section 2)
  global.json                 # copy of framework/global.json (section 2)
  .editorconfig               # copy of framework/.editorconfig (section 2)
  <Game>.slnx
```

```powershell
git submodule add https://github.com/hung-bui-121/game-server-dotnet-kestrel.git framework
git -C framework checkout v0.7.0
git -C framework describe --tags --exact-match      # must print v0.7.0
git add .gitmodules framework
```

Rules:

- Pin an **annotated tag** (`vX.Y.Z`). Never a branch: no `branch =` line in `.gitmodules`, never
  `git submodule update --remote`. A bare commit only while testing an unreleased framework change locally.
- Never edit or commit files inside `framework/`; framework changes go through the framework repository and a new tag.
- After every `git pull` of the game: `git submodule update --init` (or `git pull --recurse-submodules`).
- A game may live below a folder named `tests`: framework test projects are detected relative to the framework root
  (FG-004 fixed in 0.7.0).

## 2. Build files

**Sealed** (the framework's own files govern framework projects; nothing of the game reaches them):

| File | Why it is sealed |
| --- | --- |
| `framework/Directory.Build.props` | MSBuild stops at the first `Directory.Build.props` found upward |
| `framework/Directory.Build.targets` | exists (behaviour-free) so a game's root `Directory.Build.targets` is never imported by framework projects (FG-003 fixed in 0.7.0) — the game may have its own |
| `framework/Directory.Packages.props` | nearest one for framework projects |
| `framework/.editorconfig` | `root = true` |

**Still taken from the game side** (keep them in step by hand on every bump, FG-008):

| File | Rule |
| --- | --- |
| `global.json` | `dotnet` reads the `global.json` of the folder it runs in and above, so the game's copy decides the SDK for framework projects too. Copy `framework/global.json` |
| `nuget.config` | the NuGet configuration hierarchy of the folder `dotnet` runs in; the framework has none (nuget.org only) |
| `.editorconfig` | the framework's `.editorconfig` covers only files below `framework/`. Copy it to the game root so game code and tests get the same analyzer severities; without it, rules the framework turns off fail the game build under warnings-as-errors (for example `xUnit1051` on every `client.GetAsync(...)` in a test, `CA1716` on a type named `Step`). Add game-only rules below the copied lines |
| `Directory.Build.props` of the game | the game's own projects mirror the framework settings (target framework, nullable, warnings as errors, central package management, analyzers, banned APIs) |

The game's `Directory.Build.props` (from `tests/ConsumerFixture/Directory.Build.props`; `GameServerRoot` points at the
submodule):

```xml
<Project>
  <PropertyGroup>
    <GameServerRoot>$(MSBuildThisFileDirectory)framework/</GameServerRoot>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <!-- By name, not by path: a game repository may itself sit under a folder named "tests". -->
    <IsTestProject Condition="$(MSBuildProjectName.EndsWith('.Tests'))">true</IsTestProject>
  </PropertyGroup>

  <ItemGroup Condition="'$(IsTestProject)' == 'true'">
    <Using Include="Xunit" />
  </ItemGroup>

  <!-- Game code: the framework's analyzers and banned APIs (PERF-01, TIME-01). -->
  <PropertyGroup Condition="'$(IsTestProject)' != 'true'">
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup Condition="'$(IsTestProject)' != 'true'">
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" PrivateAssets="all" />
    <AdditionalFiles Include="$(GameServerRoot)BannedSymbols.txt" />
  </ItemGroup>
</Project>
```

The game's `Directory.Packages.props` imports the framework's versions, so game and framework projects always resolve
the same packages:

```xml
<Project>
  <!-- Package versions of the framework: game and framework projects always resolve the same versions. -->
  <Import Project="$(MSBuildThisFileDirectory)framework/Directory.Packages.props" />
</Project>
```

Game-only packages are added below the import with `<PackageVersion Include="…" Version="…" />`. Never `Include` a
package the framework already pins (NU1506); if a game really must override one, use `<PackageVersion Update="…" />` and
record why. Framework projects never read the game's file: they import the nearest `Directory.Packages.props`, the
framework's own (checked by `BuildSealingTests` with a conflicting file above the framework). A game pin that differs
from the framework's version therefore applies to the game's projects only, and when a game project references a
framework project the restore reports the downgrade as **NU1605** (an error with warnings as errors): expected
behaviour — align the version or drop the pin.

## 3. Project references

Reference framework projects by path through `$(GameServerRoot)`. A game module (from
`tests/ConsumerFixture/src/Fixture.Game/Fixture.Game.csproj`; a two-project module references `GameServer.Core` from its
business project and `GameServer.Platform.Web` from its adapter, see `templates/module/README.md`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$(GameServerRoot)src/GameServer.Platform.Web/GameServer.Platform.Web.csproj" />
  </ItemGroup>
</Project>
```

The host (from `tests/ConsumerFixture/src/Fixture.Host/Fixture.Host.csproj`); `<Version>` is the **game version**
(section 7):

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <Version>1.2.3</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(GameServerRoot)src/GameServer.Platform.Web/GameServer.Platform.Web.csproj" />
    <ProjectReference Include="$(GameServerRoot)src/GameServer.Platform.Testing/GameServer.Platform.Testing.csproj" />
    <ProjectReference Include="$(GameServerRoot)src/GameServer.Platform.Couchbase/GameServer.Platform.Couchbase.csproj" />
    <ProjectReference Include="$(GameServerRoot)src/GameServer.Platform.Observability/GameServer.Platform.Observability.csproj" />
    <ProjectReference Include="$(GameServerRoot)src/GameServer.Modules.Identity.Adapters/GameServer.Modules.Identity.Adapters.csproj" />
    <ProjectReference Include="../Fixture.Game/Fixture.Game.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Update="config\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Add `GameServer.Modules.Economy.Adapters`, `GameServer.Modules.Iap.Adapters` or `GameServer.Modules.Gm.Adapters` when the
host registers those modules. Framework projects keep the framework's build settings and version stamp when built
as project references from the game.

## 4. Host composition

The host's `Program.cs` is a copy of the framework's `samples/SampleHost/Program.cs` with the game's modules (a reusable
composition host is deferred, FG-009). From `tests/ConsumerFixture/src/Fixture.Host/Program.cs`:

```csharp
using Fixture.Game;
using Fixture.Host;
using GameServer.Modules.Identity;
using GameServer.Platform;
using GameServer.Platform.Couchbase;
using GameServer.Platform.Observability;
using GameServer.Platform.Observability.Analytics;
using GameServer.Platform.Testing;
using GameServer.Platform.Web.Hosting;
using GameServer.Platform.Web.Requests;

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
```

- **Dev (cheat) modules only when development tools are allowed.** Register them inside
  `if (gameServer.Options.AllowsDevelopmentTools)` (as SampleHost does with `CheatModule`). Since 0.7.0 a Dev endpoint or
  Dev command anywhere else (beta, uat, live) fails startup with one report line each; a Dev route mapped after
  `UseGameServer` answers 403 `FORBIDDEN` and the runner refuses Dev commands (FG-010).
- Flavor files: `appsettings.json` (shared: `GameServer:GameId`, `GameServer:ConfigPath`) plus one
  `appsettings.{Flavor}.json` per flavor (`Local`, `Dev`, `Beta`, `Uat`, `Live`) with `GameServer:DeploymentEnvironment`,
  `GameServer:EnvironmentId` and `GameServer:Store`. The fixture's `appsettings.Local.json` uses the file store
  (`FileStore:Folder`) and turns the console on (section 8). Details: `docs/guides/environments-and-local-run.md`.
- Game config: the host loads `config/base/*.json` (overlaid by `config/env/{DeploymentEnvironment}/`) from its content
  root (`GameServer:ConfigPath`, default `config`); `config/base/manifest.json` is required even without config files,
  else startup fails with `[config] '…/config/base/manifest.json' not found`. The fixture's
  `config/base/manifest.json` (one file, `notes.json`, which the fixture module lists in `RequiredConfigFiles`):

  ```json
  {
    "label": "fixture-1",
    "files": [
      "notes.json"
    ],
    "clientVisible": []
  }
  ```

  The host project copies the folder to its output (`<Content Update="config\**\*" CopyToOutputDirectory="PreserveNewest" />`,
  section 3), so `LocalFlavorFactory` and `run-local` find it.
- `Properties/launchSettings.json` with a `local` profile: `applicationUrl` (first `http://` URL = port) and
  `environmentVariables.ASPNETCORE_ENVIRONMENT` (= flavor). run-local needs both (section 6).
- A game middleware that needs the request result is registered on `app` before `RunGameServerAsync` (so it wraps the
  framework pipeline) and reads `HttpContext.RequestOutcome()` after `await next(context)`; never parse response bodies.
- **Keep `GameServerCommandLine.Parse(args)` and `app.RunGameServerAsync(mode)`** as above. Besides `--provision`, they
  implement the export mode the Unity code generation runs (`--export-wire-contract <file>`, section 10.3): the host builds
  its composition, writes the route and section catalog and exits without serving, opening the store, loading game config
  or reading a secret. A `Program` that calls `app.Run()` itself cannot be used by the generator.
- **Every endpoint is declared** (section 10.1). Module endpoints are declared in `ModuleDescriptor.Routes`. An endpoint the
  host maps itself, outside any module, is declared on the builder before `Build()` and mapped from the declaration:

  ```csharp
  // Before builder.Build():
  gameServer.DeclareRoutes(HostRoutes.Status);

  // After Build(), anywhere before RunGameServerAsync (also after UseGameServer):
  app.MapDeclared(HostRoutes.Status, () => Results.Ok()).AsPublicEndpoint(protobuf: false);

  public static class HostRoutes
  {
      public static RouteDeclaration Status { get; } = RouteDeclaration.Get("host.status", "/v1/status", SecurityProfile.Public, RouteSurface.Host);
  }
  ```

  A host route with profile `Public` still needs its template in the operator allowlist `GameServer:PublicEndpointAllowlist`
  (SEC-09); declaring it does not open it. An endpoint that is mapped but not declared fails startup in every environment
  (section 10.2).
- **The assembly name prefix `GameServer.` is reserved for the framework.** The catalog gives every module from an assembly
  named `GameServer.*` the origin `framework`; the generator then leaves its routes and sections out of the game's
  generated files (they belong to the Unity package). Name game assemblies after the game (`ZhPvp.Game`, `Fixture.Game`).
  Host-level routes (`DeclareRoutes`) always have the origin `game`.

## 5. Tests

The test project references the framework **test kit** (`src/GameServer.TestKit`, namespace `GameServer.TestKit`) and the
game projects, and links no file of the framework's `tests/` folder. From
`tests/ConsumerFixture/tests/Fixture.Tests/Fixture.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" PrivateAssets="all" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
  </ItemGroup>
  <ItemGroup>
    <!-- The consumer test kit is the only framework reference; Microsoft.AspNetCore.Mvc.Testing flows through it (V4). -->
    <ProjectReference Include="$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj" />
    <ProjectReference Include="../../src/Fixture.Game/Fixture.Game.csproj" />
    <ProjectReference Include="../../src/Fixture.Host/Fixture.Host.csproj" />
  </ItemGroup>
</Project>
```

Run a test project with `dotnet test --project <test csproj> -c Release` (the fixture, from `tests/ConsumerFixture`:
`dotnet test --project tests/Fixture.Tests/Fixture.Tests.csproj -c Release`; the mirrored `global.json` selects the
Microsoft.Testing.Platform runner, section 2).

A direct `Microsoft.AspNetCore.Mvc.Testing` reference is not needed (its build targets flow through the kit) but does no
harm. The kit has no test framework dependency; the samples use xUnit v3 and Shouldly. Reference the kit from test
projects only: a host with `GameServer.TestKit` loaded fails startup outside Development/Test (`[deployment] the consumer
test kit (GameServer.TestKit) is loaded in …`), except on the kit's own in-process test server.

Config rules are tested without a host through the kit call `ConfigDeclarationCheck.Check(config, modules)` (section 12.4).

### 5.1 Handler unit test — `CommandContextDouble`

`CommandContextDouble.Create(operation, modules…)` is an `ICommandContext` without host, store or DI. It wraps the
runner's own context, so every access rule (Touches/Reads, state type, claim limit, duplicate claims) throws exactly what
the runner throws. Pass every module whose aggregates the command touches or reads (`new IdentityModule()` for
`profile`). Configure (`AsPlayer`, `AsGm`, `WithActor`, `At`, `WithConfig`, `WithRequestId`/`WithReceiptId`, `Seed`,
`SeedClaim`, `WithCommandSettings`) before the handler runs; read `Writes`/`Written<T>`, `Outbox`, `Claims` afterwards.
`Writes` holds what the runner would write: a `Set` of the loaded (seeded or default) state, or a change set back, is no
write. `CheckCommit(outcome)` returns what the runner would refuse at commit, with the runner's rules and messages; empty =
the runner commits:

| Check | Done by the double | Note |
| --- | --- | --- |
| commit code catalogued and HTTP 200 (API-11) | yes | |
| GM command exactly one audit, others none (SEC-14) | yes | |
| written aggregate within its `HardBytes` (runner: `AGGREGATE_TOO_LARGE`, nothing commits) | yes | envelope measured with the actor's GameId/EnvironmentId (`test`/`test` by default); other ids change the size by their length |
| result within `ResultCompactMaxBytes` (CMD-03, runner: `COMMAND_OUTPUT_TOO_LARGE`, part `result`, nothing commits) | only `CheckCommit(outcome, encodeResult)` | pass the endpoint's encoding, e.g. `r => ToWire((MyResult)r).ToByteArray()`; limits = framework defaults or `WithCommandSettings(...)` |
| receipt within `ReceiptMaxBytes` (CMD-03, runner: `COMMAND_OUTPUT_TOO_LARGE`, part `receipt`, nothing commits) | only `CheckCommit(outcome, encodeResult)` (0.7.0, FG-016) | measured with the runner's own receipt builder from the configured actor, request id, receipt id, config, `At`, seeded revisions (`Seed(id, state, rev)`), claims and outbox; the request body hash is a 64-character placeholder (same length as the runner's); section 13.4 |
| Touches/Reads, state type, claim limits | while the handler runs | the shared context throws, before any commit check |

From `tests/ConsumerFixture/tests/Fixture.Tests/SaveNoteHandlerTests.cs`:

```csharp
using Fixture.Game;
using GameServer.Config;
using GameServer.Modules.Identity;
using GameServer.TestKit;
using Shouldly;

private static readonly IGameConfig Rules =
    TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = "{\"maxLength\":10}" }, "fixture-test");

private static CommandContextDouble Context() =>
    CommandContextDouble.Create(SaveNoteHandler.Operation, new IdentityModule(), new FixtureModule()).AsPlayer("p_1").WithConfig(Rules);

[Fact]
public async Task Save_replaces_the_note_and_the_runner_would_commit_it()
{
    var context = Context().Seed(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = "old", Saves = 1 });

    var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("new"), context);

    context.CheckCommit(outcome).ShouldBeEmpty();
    outcome.Result.ShouldBe(new SaveNoteResult("new", 2));
    context.Written<NotesState>(FixtureModuleDefinition.NotesAggregate).ShouldBe(new NotesState { Text = "new", Saves = 2 });
    context.Outbox.ShouldBeEmpty();
}
```

A rejection is the handler's `CommandOutcome<T>.Error` (the declared `ErrorCode` instance), not a `Code` string: the
string (`"FIXTURE_TOO_LONG"`) exists only on the wire, in `CommandResponse.Code`. A rejected outcome writes nothing (same
file):

```csharp
[Fact]
public async Task A_note_that_is_too_long_is_rejected_and_writes_nothing()
{
    var context = Context();

    var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote("far too long"), context);

    outcome.IsOk.ShouldBeFalse();
    outcome.Error.ShouldBe(FixtureErrors.TooLong);
    context.Writes.ShouldBeEmpty();
}
```

Time: `ICommandContext.Now` is a `DateTimeOffset`, the server time fixed for the attempt (read it instead of
`DateTime.UtcNow` in a handler). The double starts at `2026-01-01T00:00:00Z`; set another instant with
`At(DateTimeOffset now)` (returns the double, so it chains:
`Context().At(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero))`).

`TestGameConfig`: `Empty`, `FromJson(files, label)` and `FromDirectory(configRoot, deploymentEnvironment)` (the host
loader itself, same `ConfigVersion`).

An account initializer has its own double, `CommandContextDouble.ForAccountInitializer(name, modules…)` with
`CheckAccountCreation()` (section 13.2).

### 5.2 The game host on its real local flavor — `LocalFlavorFactory<Program>`

`LocalFlavorFactory<TEntryPoint>` is a `WebApplicationFactory` that runs the host with its own `appsettings.Local.json`
and redirects everything with side effects into one temporary root per factory (`Root` with `data/`, `analytics/`,
`logs/`; deleted on dispose unless `LocalFlavorOptions.Folder` is given). From
`tests/ConsumerFixture/tests/Fixture.Tests/FixtureHostSmokeTests.cs` (shortened; `Program` is the host's entry point type,
`public partial class Program;`):

```csharp
using System.Net;
using Fixture.Game;
using GameServer.Protocol.Core.V1;
using GameServer.TestKit;
using Google.Protobuf.WellKnownTypes;

await using (var factory = new LocalFlavorFactory<Program>())
{
    var client = factory.CreateClient();
    (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

    var login = await client.LoginAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
    var player = factory.CreateClient();
    player.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.SessionToken);
    var response = await player.PostProtobufAsync(FixtureModule.SaveRoute, new StringValue { Value = "hello" }, ProtobufHttp.NewRequestId());
    var command = await response.ReadProtobufAsync<CommandResponse>();

    command.Code.ShouldBe("OK");
}
```

`CommandResponse` (`GameServer.Protocol.Core.V1`, from `core.proto`): `Code` (`"OK"` or the business code), `CodeNumber`,
`ReceiptId` (empty on a rejection), `Updates` (repeated `SectionUpdate`), `Result` (the bytes of the command's result
message: `StringValue.Parser.ParseFrom(command.Result)`), `ConfigVersion`, `ServerTimeUtcMs`, `Replayed`. A `SectionUpdate`
carries `Id`, `BaseRev`, `NewRev`, `Payload` and `Full` directly (no nested section): `command.Updates[0].NewRev`,
`Wire.CardState.Parser.ParseFrom(command.Updates[0].Payload)`. Only aggregates the command changed are in `Updates`.

The settings the factory applies (`LocalFlavorSettings.Keys`; overrides through `LocalFlavorOptions.Settings`, services
through `LocalFlavorOptions.Services`, the sweeper through `LocalFlavorOptions.OutboxSweeper`):

| Key | Value |
| --- | --- |
| `FileStore:Folder` | `{Root}/data` (`factory.DataFolder`) |
| `GameServer:Outbox:SweeperEnabled` | `false` |
| `GameServer:Jobs:Enabled` | `false` |
| `GameServer:Hosting:DrainDelaySeconds` | `0` |
| `GameServer:Analytics:Directory` | `{Root}/analytics` (`factory.AnalyticsFolder`) |
| `Observability:InstanceId` | `lf-{8 hex}`, unique per factory (`factory.InstanceId`) |
| `Observability:Logging:Directory` | `{Root}/logs` (`factory.LogFolder`) |
| `Observability:Logging:Console:Enabled` | `false` (tests do not print) |

Rule: a framework setting that writes to disk, starts background work or writes to the console joins this list in the
slice that introduces it, so a game never copies settings by hand. Game settings with side effects go into the game's own
`LocalFlavorOptions.Settings`.

A startup failure throws `GameServerStartupException` from `StartServer()`/`CreateClient()` with the report as `Violations`
(the real host prints it and exits 1). Disposing a factory whose start failed does not throw and still deletes its root.

### 5.3 In-process host with chosen modules — `TestHost`

`TestHost.StartAsync(modules: …, settings: …, config: …)` builds a host on TestServer with the in-memory store, a fake clock
(`host.Clock`), test authentication (`host.PlayerClient(id)`, `host.GmClient(staff, permissions…)`), an in-memory analytics
sink (`host.Analytics`) and captured logs (`host.Logs`, structured `host.LogEntries`). Defaults: Identity only and an empty
game config. `storeKind: TestStoreKind.File` (or `GAMESERVER_TEST_STORE=File`) runs on a temporary file store.

### 5.4 The request outcome in tests

The fixture's middleware (section 4) counts `HttpContext.RequestOutcome().ResultCode` per code; its smoke test reads the
counts after each response returned (`OutcomeCounts`), e.g. `OK` for the save and `NOT_FOUND` for an unknown route — the
codes the client received. In a `TestHost`, the request event itself is a `LogEntries` entry of category
`GameServer.Requests` (event id 1) with `ResultCode`, `Status`, `Operation`, `Replayed`, `NotModified`, `RouteName` (the
catalog name of the matched route; also `HttpContext.RequestOutcome().RouteName`).

### 5.5 Wire-mapper conformance — `WireConformance`

A wire mapper that forgets a state member sends a default value to Unity without any error. The kit's conformance check
proves that every client-visible aggregate's mapper carries every persisted member that is not declared hidden, and that
every proto field it sets is not left at its default. It needs no host, no store and no test framework. From
`tests/ConsumerFixture/tests/Fixture.Tests/FixtureConformanceTests.cs`:

```csharp
[Fact]
public void The_game_and_framework_mappers_pass()
{
    var report = WireConformance.CheckModules([new IdentityModule(), new FixtureModule()]);

    report.CheckedAggregates.ShouldBe([FixtureModuleDefinition.CardAggregate, IdentityAggregates.Profile]);
    report.ThrowIfFailed();
}
```

- `WireConformance.CheckModules(modules, options?)` runs each module's `ConfigureServices` against a bare service collection
  and checks every client-visible aggregate the modules declare. Pass the framework modules whose sections the game
  sends too (`IdentityModule`, `EconomyModule`). `Check(module, aggregateId, options?)` checks one aggregate;
  `CheckAll(services, options?)` checks every client-visible aggregate of a built composition (`factory.Services` of a
  `LocalFlavorFactory`, `TestHost.BuildApp(...).Services`). `Check` and `CheckModules` see the mappers the modules
  register; only `CheckAll` sees a host's `ConfigureExtensions(e => e.ReplaceWireMapper(…))` override (section 10.2).
- When a test is needed: one `CheckModules` test per game covers every client-visible aggregate its modules declare, so a
  new command whose mapper and state are unchanged needs nothing new (the existing test still passes). Rerun it, and
  extend its expected `CheckedAggregates`, when a module adds a client-visible aggregate or changes a state type or its
  mapper. A state that only a command produces (values the default sample does not reach) is checked with
  `WithSampleState` (below).
- `ThrowIfFailed()` throws `WireConformanceException` with one line per finding:
  `[aggregate {id}] {problem}: {path} — {fix}`. `report.Findings` lists them as data (`AggregateId`, `Problem`, `Path`,
  `Message`); `report.Passed` is true when there is none.
- How it works: the kit builds a sample state from the JSON contract (`GameServerJson.Options`) of the aggregate's state
  type (a mapper typed on a base class is still checked for the derived members), maps it, and parses the wire bytes. A proto field still at its default is `FieldAtDefault` (proto field path, e.g.
  `slots.unlock_end_utc_ms`). Then it changes one state member at a time; when the wire message does not change, the
  member is `MemberNotOnWire` (C# member path, e.g. `Slots.UnlockEndUtc`), unless it is declared hidden. A hidden member
  that does change the wire is `HiddenMemberOnWire`.
- Sample and change per member type:

  | Member type | Sample | Change |
  | --- | --- | --- |
  | integer types | `n` (ordinal of the member) | `+1` |
  | `float`, `double`, `decimal` | `n + 0.5` | `+1` |
  | `bool` | `true` | `false` |
  | `string` | `"s{n}"` | `+ "x"` |
  | enum | the defined values other than 0 in turn, one per element of a list (the first one outside a list) | another value, not the one the same member has in the other element |
  | `DateTimeOffset`, `DateTime` (UTC) | `2026-01-02T03:04:05.006Z` + `n` minutes | `+1 s` (survives a millisecond wire field) |
  | `TimeSpan` | `n` s | `+1 s` |
  | `Guid` | deterministic from `n` | another deterministic value |
  | `byte[]` | `[n]` | `[n, 1]` |
  | `T?` | sample of `T` | change of `T` |
  | list / collection | two elements | the first and the last element's members, one at a time (a mapper that keeps only one end, `.First()`, `Take(1)`, `.Last()` or `Skip(1)`, is `MemberNotOnWire`) |
  | dictionary | two entries (`"k{n}"`, `n`, enum or `Guid` key; an enum with a single non-zero value gives one entry) | the first and the last entry's key, then its value's members |
  | anything else (`object`, `JsonNode`, an interface) | none: `MemberNotFillable` | — |

- `WireConformanceOptions`: `WithMemberValue(aggregateId, memberPath, value)` supplies the sample for a member the kit
  cannot fill (`MemberNotFillable`; that member is then not changed); `WithSampleState(aggregateId, state)` uses your own
  state as the sample (the changes still apply to it; give its collections two elements, since the first and the last are changed).
  Each `With*` returns the options, so they chain. Pass the options as the last argument of `Check*` (from
  `FixtureConformanceTests.cs`):

  ```csharp
  var options = new WireConformanceOptions()
      .WithSampleState(FixtureModuleDefinition.CardAggregate, new CardState
      {
          Title = "after save",
          Counts = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 },
          Slots = [new CardSlot("sword", DateTimeOffset.UnixEpoch.AddDays(1)), new CardSlot("shield", DateTimeOffset.UnixEpoch.AddDays(2))],
          Nickname = "n",
          UpdatedUtc = DateTimeOffset.UnixEpoch.AddDays(3),
          InternalNote = "kept on the server",
      });

  WireConformance.CheckModules([new IdentityModule(), new FixtureModule()], options).ThrowIfFailed();
  ```
- Other problems: `SampleNotSerializable` (the sample does not deserialize into the state type, or the mapper throws),
  `UntypedMapper` (registered with the obsolete one-type `AddAggregateWireMapper<TState>`, section 10.2), `NoMapper` (a
  client-visible aggregate without a mapper).
- Fix a finding in the mapper, or declare at the registration what the server keeps on purpose (section 10.2):
  `o.Hidden("ActiveBattle.ConfigVersion")` for a state member, `o.MayStayDefault("mode")` for a proto field whose
  meaningful value is its default (an enum zero).

## 6. Local run

Run the game host with the framework's script (no copy in the game):

```powershell
framework/tools/local/run-local.ps1 -HostProject src/ZhPvp.Host -LaunchProfile local -Name "ZhPvp"
```

```bash
framework/tools/local/run-local.sh --host-project src/ZhPvp.Host --launch-profile local --name "ZhPvp"
```

- `-HostProject` / `--host-project`: the host project file or its folder (relative to the current directory). Default
  without it: the framework's SampleHost.
- `-LaunchProfile` / `--launch-profile` (default `local`): flavor = the profile's `ASPNETCORE_ENVIRONMENT`, port = its
  first `http://` URL (none → 5080 with a note). `-Port`, `-Reset` (empties only the host's `FileStore:Folder` after
  locking it), `-Lan` as for the sample.
- `-Name` / `--name`: banner title (default: the project name).
- The script reads the host's `Properties/launchSettings.json`, `appsettings.json` + `appsettings.{flavor}.json` and
  environment variables (data and log folder) and the nearest `global.json`; it never writes into the game repository.
- Exit codes: 0 graceful stop, 2 invalid argument or host input (message names the fix), 3 SDK, 4 port busy, 5 build,
  6 host exited before ready, 7 not ready in time (tail of the newest log file). A game wrapper may add its own codes
  (≥ 8) for game-only checks and must pass the framework codes through.

Contract: `specs/013-consumer-readiness/contracts/run-local-cli.md`.

## 7. Versions

- Framework: `GameServerVersion.Framework` (`0.7.0`) and `GameServerVersion.FrameworkInformational` (`0.7.0+{git sha}`),
  stamped into every framework assembly from the framework's `Directory.Build.props`, also when built as project
  references from the game. The Unity package `com.gameserver.client` has the same version.
- Game: the host's `<Version>`; read it with `GameServerVersion.InformationalOf(assembly)` or from
  `GameServerRuntimeInfo.GameVersion`.
- `GameServerRuntimeInfo` (DI singleton, also `GameServerBuilder.Runtime`): game id, environment id, deployment
  environment, instance id, game assembly, game version, framework version.
- The framework logs one Information line at startup (category `GameServer.Startup`):
  `Starting fixture 1.2.3+… on framework 0.7.0+…: environment local (Development), instance lf-1a2b3c4d.` A game does not
  log its own version line.
- Telemetry resource: `service.version` = game version, `gameserver.framework.version` = framework version.
- GM `GET /gm/v1/ops/version` (permission `ops.read`) returns the five values of the instance; public endpoints and health
  probes never show a version.
- A game that checks "checked-out framework tag = pinned tag" can compare its pinned tag with `v` +
  `GameServerVersion.Framework`. The framework tags `v{GameServerFrameworkVersion}`.

From `tests/ConsumerFixture/tests/Fixture.Tests/FixtureVersionTests.cs`:

```csharp
var runtime = factory.Services.GetRequiredService<GameServerRuntimeInfo>();

runtime.GameAssembly.ShouldBe("Fixture.Host");
runtime.GameVersion.ShouldStartWith("1.2.3");
runtime.FrameworkVersion.ShouldBe(GameServerVersion.FrameworkInformational);
```

## 8. Request log and console

The framework writes one structured event per HTTP request (category `GameServer.Requests`; never for `/health/live` and
`/health/ready`): method, route template, profile, operation, actor, request id, trace id, status, result code (what the
client received), replayed, not-modified, aborted, duration — never bodies, query strings, headers, cookies, tokens or IPs.

| Flavor | Request event | Console |
| --- | --- | --- |
| Development (`local`, `dev`) and test hosts | on (`GameServer:RequestLog:Enabled` unset) | set `Observability:Logging:Console:Enabled: true` in the game's `appsettings.Local.json` (and `Dev`); `LocalFlavorSettings` forces it off in tests |
| Staging (`beta`, `uat`), Production (`live`) | off unless `GameServer:RequestLog:Enabled=true` (startup Warning with the volume estimate) | off |

- `GameServer:RequestLog:ExcludedPaths`: more paths never logged (segment prefix, case-insensitive).
- `Observability:Logging:Console:MinimumLevel` (`Information`), `Observability:Logging:Console:BufferSize` (1 000): the
  console has its own bounded queue that drops (counted in `log_dropped_total{sink="console"}`, apart from the file's
  `sink="file"`) and never blocks a request. Control characters in console values become `?` (no terminal escapes).
- Unmatched requests (404, 405) log only the first path segment: `GET /api/...`, never the rest of the path.
- Volume: ~300–400 B per event in the file (≈ 13–17 GB per instance per day at 500 requests/s); before enabling it in
  Staging/Production raise the file limits or ship logs (`docs/guides/environments-and-local-run.md` §8).
- `IRequestOutcomeFeature` (`HttpContext.RequestOutcome()`, namespace `GameServer.Platform.Web.Requests`): set by the
  framework for every response it writes; after the framework pipeline (a middleware placed before `UseGameServer`, after
  `await next()`) every member is final and `ResultCode` is never null (`OK`, business code at HTTP 200, error code,
  `ABORTED`, `HTTP_{status}`). A game endpoint may set `ResultCode`/flags for its own responses.

Contract: `docs/pcr/platform.md` §13.

## 9. Error codes

| Range | Owner |
| --- | --- |
| 0–999 | framework core |
| 10000–10099, 11000–11099, 12000–12099, 13000–13099, 14000–14099, 40000–40099 | framework modules and samples (`sample.counter`, `economy`, `iap`, `gm`, `identity`, `sample.game`; `docs/pcr/platform.md` §8) |
| **50000–89999** | **game modules** (`ErrorCodeBands.GameStart`–`GameEnd`) |
| 90000–99999 | framework territory (`ErrorCodeBands.FrameworkReservedStart`–`FrameworkReservedEnd`; the module template's example 90000–90099) |

Each game module picks its own sub-range inside 50000–89999 (the fixture: 50000–50099). The framework never puts a code
in the game band (architecture test), so a framework bump cannot collide with game codes. Overlapping ranges or duplicate
names/numbers between registered modules fail startup. From `tests/ConsumerFixture/src/Fixture.Game/FixtureModuleDefinition.cs`:

```csharp
/// <summary>Game error codes: the first block of the game band (50000–89999).</summary>
public static class FixtureErrors
{
    public static readonly ErrorCode Unchanged = new(50001, "FIXTURE_UNCHANGED", 200, false, "The note is unchanged.");
    public static readonly ErrorCode TooLong = new(50002, "FIXTURE_TOO_LONG", 200, false, "The note is too long.");

    public static ErrorCodeSet All { get; } = new(50000, 50099, Unchanged, TooLong);
}
```

## 10. Unity SDK

Install the client SDK by UPM git URL pinned to the same tag as the submodule (`Packages/manifest.json` of the Unity
project):

```text
https://github.com/hung-bui-121/game-server-dotnet-kestrel.git?path=sdk/unity/com.gameserver.client#v0.7.0
```

The package version equals the framework version (`0.7.0`). It ships the framework's messages and two generated files,
`Runtime/Generated/FrameworkRoutes.cs` (player-facing framework routes) and `FrameworkSections.cs` (framework sections:
`wallet`, `inventory`, `profile`). The game generates the same three kinds of file for its own routes, sections and
`.proto` messages from its own repository with the framework's generator, **WireGen** (sections 10.3–10.6). Nothing in the
framework is edited or copied.

Config versions (0.7.0, 015/BC-4): `client.ClientConfigVersion` is the version of the client-visible files (the server's
`client_config_version`, or `config_version` from a server before 0.7.0); `client.ServerConfigVersion` stays the server
version, for correlating receipts only. `ConfigOutdated`, `ConfigVersionChanged(version)` and the config cache key follow the
client version, so a server-only config edit no longer raises `ConfigVersionChanged` or triggers a download. Compare a cache
with `ClientConfigVersion`. Keep the bundle across app starts with the persistent cache (section 12.8). Boot on the login
answer, pass request ids and keep unanswered commands across app restarts with the pending store: section 13 (13.1, 13.5).

### 10.1 Declare every route

A new command needs more than its route. First add the command itself, in this order (`docs/html/02-them-command.html`
§5, steps (1)–(5), worked through for `counter.reset`): the request/result messages in the game's `.proto` (section 10.3
for its C# namespace), the domain records, the handler with its `CommandDescriptor` (Touches/Reads), and its registration
`Command.Define<TRequest, TResult>(Handler.Descriptor).HandledBy<Handler>()` in `ModuleDescriptor.Commands` (a route that
runs an unregistered command fails startup, ARC-07). Then declare and map its route as below. From
`FixtureModuleDefinition.Describe()`:

```csharp
Commands = [Command.Define<SaveNote, SaveNoteResult>(SaveNoteHandler.Descriptor).HandledBy<SaveNoteHandler>()],
```

A route is declared once, as data, in the module's business project (no ASP.NET, no Protobuf), and listed in
`ModuleDescriptor.Routes`. From `tests/ConsumerFixture/src/Fixture.Game/FixtureModuleDefinition.cs`:

```csharp
public static class FixtureRoutes
{
    public const string SaveTemplate = "/api/v1/fixture/save";

    public const string CardTemplate = "/api/v1/fixture/card";

    public static RouteDeclaration Save { get; } = RouteDeclaration.Command(SaveNoteHandler.Descriptor, SaveTemplate);

    /// <summary>The player's card (a query: no command, returns the <c>card</c> section).</summary>
    public static RouteDeclaration Card { get; } = RouteDeclaration.Post("fixture.card", CardTemplate, SecurityProfile.Player);
}

// In Describe():
Routes = [FixtureRoutes.Save, FixtureRoutes.Card],
```

- `RouteDeclaration.Command(descriptor, template, name?, condition?)`: `POST` running the command; the name is the
  operation (`fixture.save`), profile and permission come from the command descriptor. `Get`/`Post`/`Put`/`Delete`
  (name, template, profile, surface = `PlayerApi`, permission, condition) for other routes; `AnyMethod` for a probe.
- Name rule: `<area>.<route>` in lower case, `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, at most 64 characters, unique in the
  host. The name is the generated member name in Unity (`fixture.save` → `FixtureRoutes.FixtureSave`) and appears in the
  request log (`RouteName`); renaming it is a breaking change for the client.
- Template: one leading `/` and a version segment (`/api/v1/…`). A player-facing route the client calls can use plain
  `{name}` parameters (also inside a segment, `{id}.json`), no constraints, optional parameters, defaults or catch-alls,
  and one method per declaration.
- Map the endpoint from the declaration in the adapter (`FixtureModule.cs`):

  ```csharp
  routes.MapPlayerCommand<StringValue, SaveNote, SaveNoteResult>(FixtureRoutes.Save, ToDomain, result => new StringValue { Value = result.Text });

  routes.MapDeclared(FixtureRoutes.Card, async (HttpContext http) => { /* … */ }).AsPlayerEndpoint();
  ```

  `MapPlayerCommand`/`MapDevCommand`/`MapGmCommand(declaration, …)` take method, template, operation and permission from
  the declaration. `MapDeclared(declaration, handler)` maps its methods and template; call the `As*Endpoint()` helper of
  the declared profile as before. A library helper endpoint (`MapHealthChecks`) is tagged with `.WithDeclaration(route)`.
- Map from the **same instance** that `ModuleDescriptor.Routes` lists (the static property, `FixtureRoutes.Save`): an
  endpoint mapped from a declaration no module lists is "not declared", and one mapped from a copy (`new`/`Post(...)` again
  with the same name) is `[endpoint …] is mapped from a declaration named … that is not the one {owner} lists`. Either way
  the route would be missing from the export and from the generated client routes.
- The request and result types of `MapPlayerCommand<TWireRequest, TRequest, TResult>` are the protoc classes of the game's
  messages. A proto without `option csharp_namespace` gets protoc's package rule: `package fixture.v1;` →
  namespace `Fixture.V1` (each `.`-separated part with its first letter upper-cased, `_` removed, a letter after a digit
  upper-cased). Domain and wire types often share names, so alias the wire namespace (`FixtureModule.cs`):

  ```csharp
  using Wire = Fixture.V1;

  services.AddAggregateWireMapper<CardState, Wire.CardState>(FixtureModuleDefinition.CardAggregate, ToWire, o => o.Hidden(nameof(CardState.InternalNote)));
  // a command with its own messages: routes.MapPlayerCommand<Wire.RenameRequest, Rename, RenameResult>(FixtureRoutes.Rename, …);
  ```
- A route listed in `ModuleDescriptor.PublicEndpoints` must be declared with profile `Public` (else startup fails, 014/BC-5).
- Hosting conditions: a declaration with `condition: "x"` is mapped only where condition `x` holds; the module registers
  it with `services.AddRouteCondition("x", sp => …)`. Built in: `development_tools` (Development/Test servers), which Dev
  routes use. The check holds both ways: a declaration whose condition holds must be mapped, and an endpoint mapped while
  its condition does not hold fails startup (`[route {name}] is mapped but its condition 'x' does not hold. …`); a Public
  route mapped while its condition does not hold gets that line only. A condition that reads options failing validation
  does not hold, and the options failure is a line of the report (also for options with `Validate()` but no
  `ValidateOnStart()`).

### 10.2 The endpoint check and typed section mappers

At startup the host compares every mapped endpoint with the declarations and refuses to serve on any fault (exit 1, one
line per fault, each naming the fix), in every environment. Checked: an endpoint that is not declared (also one mapped
from a declaration instance no module lists, section 10.1), a template, method, profile, command or permission that
differs from its declaration, an endpoint mapped while its hosting condition does not hold, and a declaration that is
never mapped. The check also covers endpoints mapped **after** `UseGameServer` (directly on `app`, inside `MapGroup`): they
are validated with the same rules before the server accepts a connection. For an otherwise passing configuration,
endpoint faults now surface from `StartAsync`/`RunAsync` (and the `WebApplicationFactory` start), not from
`UseGameServer`, and the startup line (`Starting {game} … on framework …`) moves there too: it is logged once the endpoint
check passed. With another fault, `UseGameServer` still throws one report that holds the endpoint lines as well. Example
line:

```text
[endpoint POST /api/v1/account/init] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).
```

Register each client-visible aggregate's wire mapper **with its message type**, and declare what the server keeps on
purpose next to it (`FixtureModule.cs`):

```csharp
services.AddAggregateWireMapper<CardState, Wire.CardState>(FixtureModuleDefinition.CardAggregate, ToWire, o => o.Hidden(nameof(CardState.InternalNote)));
```

- The message type gives the section its message in the catalog, so the generator emits a typed key and Unity gets a
  parser (section 10.5).
- `o.Hidden("Member", "Nested.Member")`: state members that never reach the client, as C# member names joined by `.`
  (collection elements and dictionary values are transparent). `o.MayStayDefault("field")`: proto fields whose meaningful
  value may be the default, as proto field names joined by `.`. An unknown path fails startup naming the aggregate and the
  path. Both lists are in the catalog export (`hiddenMembers`, `mayStayDefault`) and are checked by the conformance test
  (section 5.5).
- The one-type `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` is obsolete (CS0618, a build error under
  `TreatWarningsAsErrors`); a section registered that way still works but gets a startup Warning and no parser in Unity.
- The mapper's `TState` must be the aggregate's state type (or a base of it): otherwise every snapshot of the section
  would fail with a cast error, so startup fails instead:
  `[aggregate {id}] wire mapper takes {TState} but the aggregate holds {state}. Fix: AddAggregateWireMapper<{state}, TWire>(…) (API-10).`
- One mapper per aggregate: a second registration for the same id fails startup. To replace a framework mapper use
  `gameServer.ConfigureExtensions(e => e.ReplaceWireMapper<WalletState, Wire.WalletState>("wallet", map, o => …))`. The
  replacement **must keep the message** of the mapper it replaces (`TWire`): the Unity client parses the section with that
  message's parser (for a framework section, the one in the package), so another message fails startup with
  `[aggregate wallet] ReplaceWireMapper must keep gameserver.economy.v1.WalletState; a new payload needs a new aggregate (FG-021).`
  A new payload needs a new client-visible aggregate of the game. Conformance of an override is checked by
  `WireConformance.CheckAll` on the built host only (section 5.5).

### 10.3 Generator config — `wiregen.json`

Commit one config in the game repository. Paths are relative to the config file's folder (`/` and `\` both accepted).
The zh-pvp shape (server repository with the framework at `framework/`, Unity client in a sibling checkout):

```json
{
  "format": "gameserver.wiregen/1",
  "catalog": { "hostProject": "../../src/ZhPvp.Host", "environment": "Local" },
  "origin": "game",
  "protos": [
    { "root": "../../src/ZhPvp.Game.Adapters/Protos", "files": ["zhpvp_game.proto"] }
  ],
  "output": {
    "root": "../../../research-and-development-games/src/GameClient",
    "marker": "Assets/MadProject/Scripts",
    "directory": "Assets/MadProject/Scripts/Online/Generated",
    "layout": "flat",
    "lineEndings": "crlf"
  },
  "namespace": "ZhPvp.Client",
  "routesClass": "GameRoutes",
  "sectionsClass": "GameSections"
}
```

| Field | Meaning |
| --- | --- |
| `catalog.hostProject` | the game host project; the tool runs it with `--environment <catalog.environment> --export-wire-contract <temp file>` (default flavor `Local`). Or `catalog.file`: a committed export instead of a host run. The host writes the export next to its path as `<file name>.<guid>.wire-contract.tmp` and moves it over the path; a run killed in between can leave that file behind (safe to delete) |
| `origin` | `game` (default): only the game's own routes and sections are generated (the framework's are in the package) |
| `protos[]` | the game's `.proto` files, each list under its protoc import root. Framework protos (`gameserver/core/v1/core.proto`, identity, economy, iap) are always import roots: import them, never list or copy them. `importRoots[]` adds the game's other import roots |
| `output.root` | the Unity project folder (must exist); `--output-root <dir>` overrides it for another checkout |
| `output.marker` | a path that must exist under the root (guards against a wrong root); relative, inside the root |
| `output.directory` | the folder that receives the files (must exist; the tool never creates it); relative to the root and inside it (an absolute path or a `..` that leaves the root is exit 1) |
| `output.layout` | `flat` (file name only, default) or `by-root` (the proto's folder under its root) |
| `output.lineEndings` | `lf` (default) or `crlf` |
| `namespace`, `routesClass`, `sectionsClass` | namespace and class names of the route and section files (defaults `GameRoutes`, `GameSections`) |

Outputs: one C# file per listed proto (`zhpvp_game.proto` → `ZhpvpGame.cs`, protoc naming), `<routesClass>.cs` and
`<sectionsClass>.cs`. The message classes keep protoc's namespace, the same on server and client: `option
csharp_namespace` when the proto sets it, else the package rule of section 10.1 (`fixture.v1` → `Fixture.V1`); the
`namespace` of the config applies to the route and section classes only. Every input or contract error (missing path, protoc error, a game message that redefines a
framework message, a route template the client cannot express, a name collision) stops the run with exit 1 before any
file is written; each line names the fix. When the host project itself does not build, the tool relays only the
`dotnet run` summary ("The build failed…"): run `dotnet build <host project>` to see the compiler errors.

**Line endings.** Every output file is normalized to `output.lineEndings`, UTF-8 without BOM. Choose what the client
repository has in its working tree: the zh-pvp client commits CRLF, so its config says `crlf`. If git converts line
endings on checkout there (`core.autocrlf`, `.gitattributes`), `verify` compares with the converted files, so the config
must match the checkout.

### 10.4 Generate and verify

```text
# game repo (framework submodule at framework/), client in a sibling checkout
dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config tools/unity-codegen/wiregen.json
dotnet run --project framework/tools/GameServer.WireGen -c Release -- generate --config tools/unity-codegen/wiregen.json --output-root D:/work/research-and-development-games/src/GameClient

# game CI, after the solution build
dotnet run --project framework/tools/GameServer.WireGen -c Release -- verify --config tools/unity-codegen/wiregen.json --no-build

# the consumer fixture inside the framework, run from tests/ConsumerFixture (the framework is two folders up)
dotnet run --project ../../tools/GameServer.WireGen -c Release -- generate --config tools/wiregen.json
```

- `generate` writes only the files whose bytes differ (an unchanged file keeps its timestamp, so Unity does not
  reimport it) and prints one line per written file. `verify` writes nothing and exits 2 listing each file that differs
  or is missing: run it in CI so the committed client files cannot drift from the server. Exit codes: 0 done, 1 input or
  contract error (nothing written; an unexpected failure of the tool itself is exit 1 with an `[internal] …` line), 2
  `verify` found differences.
- Options: `--output-root <dir>`, `--catalog <export file>` (no host run), `--configuration <cfg>` (default `Release`),
  `--no-build` (the host is already built), `--dotnet <path>` (the dotnet executable to run the host with).
- The run is deterministic: the same inputs give the same bytes on every machine and checkout path (no timestamp,
  absolute path, user or machine name in the output).
- **Version pair.** The tool generates with the framework's pinned protoc (`Grpc.Tools` 2.84.0, `libprotoc 35.1`), and
  the Unity package ships `Runtime/Plugins/Google.Protobuf.dll` 3.36.2. The game must use the same versions: the tool
  stops with exit 1 when the export's `frameworkVersion` is not the tool's, when the host's `Google.Protobuf` is not
  3.36.2, or when the host project's `Grpc.Tools`/`Google.Protobuf` package versions (central package management) differ
  from the pin; the message names both versions and which side to change. Mirror the framework's
  `Directory.Packages.props` versions (section 2).
- **Cleaning.** The generated files are not outputs of any MSBuild project: `dotnet clean` or a rebuild never deletes them,
  and the tool itself never deletes a file. When a proto is removed from the config, delete its generated file (and its
  `.meta`) by hand.
- **`.meta` files.** The tool never reads, writes or deletes `.meta` files. For a file it creates it prints
  `new file <path>: Unity creates <path>.meta on import; commit it`: in a Unity project (`Assets/…`), open the project
  once and commit the `.meta` Unity created with the file. Inside a UPM package folder (immutable when installed from
  git) Unity cannot create it: the `.meta` must be written and committed with the file (the framework does this for its
  package).
- **IL2CPP / `link.xml`.** The generated route and section files reference their messages' `Parser` directly, so
  managed stripping keeps what they use: no `link.xml` entry is needed for generated code. An existing `link.xml` that
  preserves the message assembly may stay.

### 10.5 Use the generated code in Unity

```csharp
// Unity game code
client.Sections.Register(GameSections.Table);                          // once, after creating the client
var progress = client.Sections.Get(GameSections.Progress);              // ProgressState or null
var wallet = client.Sections.Get(FrameworkSections.Wallet);
var init = await client.SendCommandAsync(GameRoutes.AccountInit, new InitAccountRequest(), InitAccountResult.Parser);

client.Sections.SectionChanged += id =>
{
    switch (client.Sections.TryRead(id, out var message))
    {
        case SectionRead.Parsed when message is ProgressState p: /* … */ break;
        case SectionRead.Unknown: /* newer server: section kept raw, update the client */ break;
    }
};
```

- Routes: `GameRoutes.X` is the route path without the leading slash (relative to the SDK base URL), `GameRoutes.XMethod`
  its HTTP method; a route with parameters is a method, `GameRoutes.X(string id)`, which escapes each value. Only
  player-facing routes (profiles Player, Public, Dev; never GM) are generated, and never the unversioned health probes
  (`/health/live`, `/health/ready`: they serve load balancers, not the client).
- Sections: `GameSections.X` is a typed key (`SectionKey<TMessage>`), `GameSections.XId` the id constant,
  `GameSections.Table` every key. `FrameworkSections.Table` is registered by the SDK itself; register the game's table
  once. `Get(key)` returns the parsed message or null when the client has not received it; `TryRead(id, out message)`
  returns `Absent`, `Unknown` (no registered table knows the id: kept raw, never parsed) or `Parsed`.
  `Get<T>(id, parser)`, `TryGet`, `RevOf(id)` and `SectionChanged` are unchanged.
- No route string or section id literal is needed in client code any more; the framework's own sample client
  (`samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`) is checked for that by a test.

### 10.6 Checklist for the game

1. Register the command in `ModuleDescriptor.Commands` (`Command.Define<…>().HandledBy<…>()`), declare the route
   (`ModuleDescriptor.Routes`, `DeclareRoutes` for host endpoints) and map from the declaration (section 10.1).
2. Register every section mapper typed, with `Hidden`/`MayStayDefault` where the server keeps data on purpose.
3. Add the conformance test once (section 5.5). Rerun `CheckModules` (and update its `CheckedAggregates`) for a new
   client-visible aggregate or a changed state or mapper; use `WithSampleState` to check a state that a command
   produces. A new command with an unchanged mapper and state needs no new conformance test.
4. Commit `wiregen.json`; run `generate`; commit the generated files and the `.meta` files Unity creates.
5. Add `verify --no-build` to CI after the solution build.

## 11. Bump checklist

1. Read the release notes of the new tag (annotated tag message): fixed FG ids, breaking changes with migration steps,
   workarounds that become removable, new settings.
2. Move the submodule: `git -C framework fetch --tags origin`, `git -C framework checkout vX.Y.Z`,
   `git -C framework describe --tags --exact-match`.
3. Diff the mirrored build files and copy what changed: `framework/global.json` → `global.json`, `framework/.editorconfig`
   → `.editorconfig` (keep the game-only rules); the framework's `Directory.Build.props`, `BannedSymbols.txt`,
   `Directory.Packages.props` (game props mirror them);
   `samples/SampleHost/Program.cs` and its flavor files (the host is a copy until FG-009).
4. Update every place that names the tag (game props, README, UPM URL `#vX.Y.Z` in the Unity project).
5. Act on the breaking changes the release notes list. For 0.7.0: declare every route and map it from the declaration
   (an undeclared endpoint fails startup, 014/BC-1) and switch every section mapper to the typed
   `AddAggregateWireMapper<TState, TWire>` (the one-type overload is CS0618, 014/BC-3) — section 10. Game config (slice 015,
   section 12): replace `new GameConfig(label, FileGameConfigLoader.ComputeVersion(label, files), files, clientVisible)` with
   `new GameConfig(label, files, clientVisible)` (CS0618, 015/BC-6); start each flavor once and fix every `[module …] config …`,
   `[config] … twice at …` or bundle-size line (015/BC-1, 015/BC-2, 015/BC-7); move config checks out of `IStartupCheck`s that take
   `IGameConfig` into file declarations and rules, or make the check take `GameConfigStatus` (section 12.5); turn lazy catalog
   factories in `ConfigureServices` into `ConfigCatalogs` declarations (section 12.3); rewrite tests that pin a config version
   (section 12.6). A check moved into a rule reports in the framework line format, so tests that assert its old text change
   (`[module zhpvp.game] config ConfigGunChest.json row 2: …` instead of `[zhpvp.game] config ConfigGunChest.json: row 2: …`).
   A game test helper with the name of a kit type (a game `TestGameConfig`) is ambiguous (CS0104) in a file that also has
   `using GameServer.TestKit;`: add a `using` alias for the game type or rename it.
   Login and commands (slice 016, section 13): move a "first login" game command into an `IAccountInitializer` (13.2,
   016/BC-6, 016/BC-9) and reset dev data created before the bump (accounts are never seeded later); read
   `LoginResponse.IsNewPlayer` and boot on the login sections instead of a second snapshot (13.1); handle
   `unavailable_sections` and the codes `AGGREGATE_VERSION_AHEAD`/`AGGREGATE_UNREADABLE` where the game matched `INTERNAL`
   (13.3, 016/BC-3, 016/BC-4, 016/BC-8); expect `COMMAND_OUTPUT_TOO_LARGE` instead of a 500 for an oversized result or receipt,
   keep every `GameServer:Commands:*` value inside its range and replace file-store size measurements with the kit check (13.4,
   016/BC-1, 016/BC-2); match the typed login store failures `COMMIT_UNKNOWN_RETRY`/`CONCURRENT_UPDATE` (016/BC-10); keep
   module rejection details safe to show, they now reach the client (016/BC-11); pass request ids and set the pending store for
   commands that must not run twice (13.5).
6. Regenerate the client files (`generate`, section 10.4), commit them and the `.meta` files Unity creates in the client
   repository, and check that `verify --no-build` passes (the framework's protoc, messages or generator may have changed).
7. Build Debug and Release (0 warnings) and run the full game test suite; run the host once with `run-local`.
8. Remove the workarounds the release notes list as removable (code, `FRAMEWORK-GAP` markers, friction-log entries).
9. Commit the gitlink, the copied files, the regenerated client files, the docs and the removed workarounds together
   (`chore: bump framework to vX.Y.Z`).

## 12. Game config

Config is a folder of files the host loads once at startup (`GameServer:ConfigPath`, default `config` under the content root).
From 0.7.0 the framework checks the **content** of every file a module declares, builds every declared catalog before traffic
and reports each fault as one startup line naming module, file, location and fix (FG-018). PCR platform §21 has every line
template.

### 12.1 Manifest and overlay

`config/base/manifest.json` lists the files, the label and which files clients receive. From
`tests/ConsumerFixture/src/Fixture.Host/config/base/manifest.json`:

```json
{
  "label": "fixture-1",
  "files": [
    "notes.json"
  ],
  "clientVisible": []
}
```

`config/env/<DeploymentEnvironment>/manifest.json` (optional) overlays it: a non-empty label replaces the base label, a file
of the same name in `config/env/<env>/` replaces the base bytes, and its `clientVisible` adds to the base set. A rule line for an
overlaid file names it: `resources.json (config/env/Staging)`. The loader reports **every** fault in one start (015/BC-3), and a
property name that appears twice in one JSON object is a fault (015/BC-2): `[config] '{path}' has property '{name}' twice at
{jsonPath} (line {line}, byte {byte}). Fix: keep one property; the last one used to win silently (CFG-01).` Two names that
differ only in case (`maxQuantity`, `MaxQuantity`) are the same fault, because config is read ignoring case: `[config] '{path}'
has properties '{first}' and '{name}' at {jsonPath} (line {line}, byte {byte}), which differ only in case. Fix: keep one
property; config is read ignoring case, so the last one used to win silently (CFG-01).` Case-variant keys fail startup even in
files a game reads with its own case-sensitive reader: the loader checks every listed `.json` file, whoever reads it. A file the host process cannot read is
one line too: `[config] '{path}' cannot be read: {message} Fix: check that the host process can read the file (CFG-01).`

### 12.2 File declarations, rules and the line format

A module declares each file it reads in its `ModuleDescriptor`, with the rules that check its content. The economy module
(`src/GameServer.Modules.Economy/EconomyModuleDefinition.cs`):

```csharp
ConfigFiles = [ConfigFileDeclaration.Required(ResourceCatalog.ConfigFile, EconomyConfigRules.Resources)],
ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => ResourceCatalog.Load(c.Config), ResourceCatalog.ConfigFile)],
```

A rule is a name and a check over `ConfigContext`; it reports through `ConfigProblems` and never throws. Write it with
`ConfigJson` so every module uses the same wording. The fixture's rule
(`tests/ConsumerFixture/src/Fixture.Game/FixtureModuleDefinition.cs`):

```csharp
public static class FixtureConfigRules
{
    /// <summary>Rule <c>fixture.notes</c>: <c>maxLength</c> is an integer of at least 1.</summary>
    public static ConfigRule Notes { get; } = new("fixture.notes", CheckNotes);

    private static void CheckNotes(ConfigContext context) =>
        ConfigJson.RequiredInteger(
            context.Json(FixtureModuleDefinition.RulesFile),
            "maxLength",
            ConfigLocation.WholeFile,
            context.Problems,
            min: 1,
            fix: "set the longest note a player may save, at least 1");
}
```

declared with `ConfigFiles = [ConfigFileDeclaration.Required(RulesFile, FixtureConfigRules.Notes)]`. Each fault is one line
`[module {module}] config {file}{location}: {problem}. Fix: {fix}.`, where the location is empty, ` row {n}`, ` key '{key}'` or
` at {jsonPath}`. The framework's economy rule, for example:

```text
[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found "999". Fix: set the largest quantity a player may hold (ECO-07).
[module economy] config resources.json row 4: id 'gold' is already defined in row 1. Fix: give every resource a unique id (ECO-07).
```

- `ConfigFileDeclaration.Required(name, rules…)` / `Optional(name, rules…)`; `RequiredConfigFiles` still means "required,
  no rules". A missing required file gives the existing `[module {module}] required config file '{file}' is missing. …` line, and
  no rule of that file runs.
- A game may put its own rule on a framework file (a "CASH and GOLD must be currencies" rule on `resources.json`): both the
  economy lines and the game lines appear, each naming its module.
- Porting a check that builds its own text lines: report each fault as
  `context.Problems.Error(ConfigLocation.Row(n), problem, fix)` (or `WholeFile`, `Key`, `Path`); `problem` and `fix` are both
  required and end without a period, and the framework adds the module, the file and the location, so they are not repeated in
  `problem`. A rule may read other files (`context.Config`, `context.Json(file)`) for cross-file checks.
- A "JSON integer" is `TryGetInt64`: `1.0`, `1e3` and `"5"` are faults. Unknown properties are ignored.
- `ConfigJson` helpers (namespace `GameServer.Config`) take the JSON element first, then the property name, the location and
  the problem sink; an `out` value comes next, and the optional `fix` is always last (named: `fix: "…"`). Without `fix` the
  line says `set '{property}' to {type}`. Each helper reports at most one problem and returns what it read:

  | Helper | Returns |
  | --- | --- |
  | `TryGetObject(owner, property, at, problems, out JsonElement value, fix: null)` | true and the object; false after reporting |
  | `TryGetArray(owner, property, at, problems, out JsonElement value, fix: null)` | true and the array; false after reporting |
  | `RequiredString(owner, property, at, problems, allowEmpty: false, fix: null)` | the string, or null after reporting |
  | `RequiredInteger(owner, property, at, problems, min, max, fix: null)` | `long?`, null after reporting |
  | `OptionalInteger(owner, property, at, problems, min, max, fix: null)` | null when absent (no problem); a problem when present but wrong |
  | `RequireObject(element, at, problems, fix: null)` | true when the element (a row) is an object |
  | `PathOf(segments…)` | `$.a.b`, `$.items['key with.dot']`, `$.rows[2]` (0-based index, as JSON paths are) |

  The problems read `'{property}' is missing`, `'{property}' is null`, `'{property}' must be {type}, found {kind}` and
  `'{property}' must be an integer of at least 1, found "999"`. Locations: `ConfigLocation.WholeFile` (no location),
  `ConfigLocation.Row(n)` (` row {n}`, **1-based**: the first row of the array is `Row(1)`), `ConfigLocation.Key(key)`
  (` key '{key}'`) and `ConfigLocation.Path("$.a.b")` (` at $.a.b`, must start with `$`); `.InFile(file)` names another file.

A full row-array rule, the framework's `resources.json` rule (`src/GameServer.Modules.Economy/EconomyConfigRules.cs`,
shortened):

```csharp
public static ConfigRule Resources { get; } = new("economy.resources", CheckResources);

private static void CheckResources(ConfigContext context)
{
    var problems = context.Problems;
    if (!ConfigJson.TryGetArray(context.Json(ResourceCatalog.ConfigFile), "resources", ConfigLocation.WholeFile, problems, out var rows, "put the resource rows in a \"resources\" array (ECO-07)"))
    {
        return;
    }

    var firstRow = new Dictionary<string, int>(StringComparer.Ordinal);
    var number = 0;
    foreach (var row in rows.EnumerateArray())
    {
        var at = ConfigLocation.Row(++number);
        if (!ConfigJson.RequireObject(row, at, problems, "write each resource as an object (ECO-07)"))
        {
            continue;
        }

        if (ConfigJson.RequiredString(row, "id", at, problems, fix: "give every resource a non-empty id (ECO-07)") is { } id && !firstRow.TryAdd(id, number))
        {
            problems.Error(at, $"id '{id}' is already defined in row {firstRow[id]}", "give every resource a unique id (ECO-07)");
        }

        ConfigJson.RequiredInteger(row, "maxQuantity", at, problems, min: 1, fix: "set the largest quantity a player may hold (ECO-07)");
    }
}
```
- A warning (`context.Problems.Warning(…)`) has the same text and is logged once at startup; it does not stop the host.
- The framework ships rules for `resources.json` (`economy.resources`) and `iap.json` (`iap.products`, grants checked
  against the well-formed rows of `resources.json`); PCR economy and PCR iap list every line. A deployment whose config was
  accepted only because nothing read the bad row before now stops at startup (015/BC-1).

### 12.3 Catalogs

Declare every typed view of a config file as a catalog (`ConfigCatalogs` above). The platform builds it once before traffic,
only when its files are present and free of rule errors (an absent file declared `Optional` does not stop the build: the build
must then return an empty catalog, and a build that throws stops the host), and registers the instance in DI; handlers take it as a constructor
parameter. **Never register a declared catalog in `ConfigureServices`** (a registration there is caught by the
framework's own architecture test for its modules) — an undeclared catalog stays lazy and its faults surface on the first request
instead of at startup. A host or test that registers an instance of the catalog type, before or after `AddModule`, wins.

A catalog is a plain class with a static build over `IGameConfig`. `ResourceCatalog`
(`src/GameServer.Modules.Economy/EconomyModel.cs`):

```csharp
public sealed class ResourceCatalog
{
    public const string ConfigFile = "resources.json";

    private readonly Dictionary<string, ResourceDefinition> _resources;

    public ResourceCatalog(IEnumerable<ResourceDefinition> resources) => _resources = resources.ToDictionary(r => r.Id, StringComparer.Ordinal);

    public ResourceDefinition? Find(string id) => _resources.GetValueOrDefault(id);

    public static ResourceCatalog Load(IGameConfig config) => new(config.GetFile<ResourcesFile>(ConfigFile).Resources);

    private sealed record ResourcesFile(IReadOnlyList<ResourceDefinition> Resources);
}
```

`GetFile<T>` binds with `GameServerJson.Options`, the System.Text.Json web defaults: camelCase JSON names bind PascalCase
properties and names match ignoring case, enums are camelCase strings, and a number may be read from a string (`"5"`). The rules
run first and are stricter than the binding, so a build reads files that passed them. A build reports a problem it finds with
`c.Problems.Error(ConfigLocation.Row(n).InFile(file), problem, fix)` and returns null: `[module {module}] catalog {Type}
({file} row {n}): {problem}. Fix: {fix}.` A build that throws gives `[module {module}] catalog {Type}: the build threw
{ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018).` (when the build reads an
absent optional file through `c.Json`: `[module {module}] catalog {Type}: the build read optional config file '{file}', which is
absent. Fix: build an empty catalog when Config.HasFile("{file}") is false, or declare the file Required (FG-018).`), and a null without a problem gives `[module {module}] catalog {Type}: the build
returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018).` Each of them stops the host: no
declared catalog is ever built on first use.

Code that builds a service collection from a module's `ConfigureServices` alone (without `AddGameServer`) no longer finds
`ResourceCatalog`, `IapCatalog`, `BattleCatalog` or `ShopCatalog` (015/BC-8). Register the instance, or use the kit (12.4):
`ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)`.

### 12.4 The test kit call

`ConfigDeclarationCheck.Check(config, modules…)` runs the same declarations as the host, without a host, and gives exactly
the host's lines. From `tests/ConsumerFixture/tests/Fixture.Tests/FixtureConfigTests.cs`:

```csharp
[Fact]
public void The_committed_config_passes_the_rules()
{
    var config = TestGameConfig.FromDirectory(CommittedConfig());

    var report = ConfigDeclarationCheck.Check(config, new IdentityModule(), new FixtureModule()).ThrowIfFailed();

    report.Passed.ShouldBeTrue();
    report.Warnings.ShouldBeEmpty();
}

[Theory]
[InlineData("{\"maxLength\": 0}", "0")]
[InlineData("{\"maxLength\": \"40\"}", "\"40\"")]
public void A_bad_max_length_gives_the_startup_line(string notes, string found)
{
    var config = TestGameConfig.FromJson(new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = notes }, "fixture-test");

    var report = ConfigDeclarationCheck.Check(config, new IdentityModule(), new FixtureModule());

    report.Passed.ShouldBeFalse();
    report.Errors.ShouldBe(
    [
        $"[module fixture] config notes.json: 'maxLength' must be an integer of at least 1, found {found}. " +
        "Fix: set the longest note a player may save, at least 1.",
    ]);
}
```

The report also has `Warnings`, `Catalog<T>()` (throws listing the lines when the catalog was not built), `TryGetCatalog<T>()`
and `AddCatalogsTo(services)`.

`TestGameConfig.FromJson` must include every `Required` file of the modules passed: a missing one is the registry line
(`[module {module}] required config file '{file}' is missing. …`). So declaring a new `Required` file breaks every kit test whose
`FromJson` config lacks it; add the file there (or use `FromDirectory` on the committed config).

**Calling the SDK from a game test.** Reference the SDK with an alias:

```xml
<ProjectReference Include="$(GameServerRoot)sdk/GameServer.Client/GameServer.Client.csproj" Aliases="sdk" />
```

```csharp
extern alias sdk;
using sdk::GameServer.Client;
```

The alias is required: the SDK and the server compile the same generated protocol types. Use HttpClientTransport over
factory.CreateClient(). Shortened from `tests/ConsumerFixture/tests/Fixture.Tests/FixtureSdkConfigTests.cs`:

```csharp
await using var factory = new LocalFlavorFactory<Program>();
var http = factory.CreateClient();
var client = new GameServerClient(new GameServerClientOptions(http.BaseAddress!, "1.2.3", "editor", "fixture-test"), new HttpClientTransport(http));

await client.LoginGuestAsync(IdentityHttp.NewLocalId(), IdentityHttp.NewSecret());
if (client.ConfigOutdated)
{
    await client.RefreshConfigAsync();
}

client.Config.Version.ShouldBe(factory.Services.GetRequiredService<IGameConfig>().ClientConfigVersion);
```

A test project that pins its references (the fixture's `FixtureProjectTests` allows exactly the kit, the aliased SDK and the
game's own projects) adds the SDK reference to that allow-list, with its `Aliases="sdk"`.

**The host on a real port.** `LocalFlavorFactory` runs on the in-process `TestServer`. A test that needs a socket (an external
client, a second process) sets `LocalFlavorOptions.Settings["Urls"] = "http://127.0.0.1:{free port}"` (never the flavor's own
port, which a developer's `run-local` may hold), points `Settings["GameServer:ConfigPath"]` at the config to serve, then calls
`factory.UseKestrel()` and `factory.StartServer()`. A test of a refused start uses `TestHost.StartAsync(…, config: …)` or
`LocalFlavorFactory.StartServer()`; both throw the `GameServerStartupException` with the report as `Violations`.

**A new route and the generated client files.** A route added to a module changes the client files: regenerate them
(`generate`, section 10.4), or `verify` and the game's codegen test fail. The fixture's `FixtureCodegenTests` run the built
generator, so build it first: `dotnet build tools/GameServer.WireGen` (the solution build does it too).

### 12.5 Startup checks read config through `GameConfigStatus`

Every `IStartupCheck` is created from its registration in its own isolation, once per start, and never disposed (015/BC-10); a
constructor that throws adds one line naming the check and the other checks still run. A check that reads config takes
`GameConfigStatus` (namespace `GameServer.Platform`), never `IGameConfig`:

- `State`: `NotLoaded` (provision mode, which never loads config, 015/BC-9), `Loaded` or `Failed`;
- `Config`: the instance handlers get (`TryGetConfig(out config)`);
- `DeclarationsPassed`: no required-file, rule or catalog line in this start (warnings do not count).
- `TryGetCatalog<T>(out catalog)`: a declared catalog built in this start (false otherwise).

It is published once, before the checks run, and none of its members throws. A check from the framework's tests
(`tests/GameServer.Platform.Web.Tests/StartupCheckHostTests.cs`) that only reports the state:

```csharp
private sealed class StatusCheck(GameConfigStatus status) : IStartupCheck
{
    public void Check(StartupCheckResult result) => result.Errors.Add($"status: {status.State}");
}
```

A real check returns when `!status.TryGetConfig(out var config) || !status.DeclarationsPassed` (the report already has the
cause) and otherwise reads `config`, or a declared catalog with `status.TryGetCatalog<MyCatalog>(out var catalog)`. Never
rebuild a catalog with its `Load` in a check (the build already ran once). A check that still takes `IGameConfig` or a catalog
is safe: when the config or the catalog is unavailable it gets one "did not run" line (a warning in provision mode) instead of
repeating the cause.

Register a check with `services.AddSingleton<IStartupCheck, MyCheck>()` in the module's `ConfigureServices` or in the host.
Register every IStartupCheck in IServiceCollection; a registration made only in a third-party container builder is not seen and
never runs. The check lives in the module's `.Adapters` project (or the host), because `IStartupCheck` and `GameConfigStatus` are
in `GameServer.Platform` (the fixture has a single game project, `Fixture.Game`, which references `GameServer.Platform.Web`). Each string added to `result.Errors` (or `Warnings`) is one report line, printed as it is: write it in
the framework's format, `[{check or module}] {problem}. Fix: {fix}.` A constructor that throws a `GameServerStartupException`
adds its `Violations` as they are. The fixture's check (`tests/ConsumerFixture/src/Fixture.Game/NotesResultSizeCheck.cs`):

```csharp
public sealed class NotesResultSizeCheck(GameConfigStatus status, GameServerOptions options) : IStartupCheck
{
    public void Check(StartupCheckResult result)
    {
        if (!status.DeclarationsPassed || !status.TryGetConfig(out var config))
        {
            return;
        }

        var maxLength = config.GetFile<NotesRules>(FixtureModuleDefinition.RulesFile).MaxLength;
        // … one line when maxLength * 3 > options.Commands.ResultCompactMaxBytes
    }
}
```

In a unit test build the status with `TestGameConfigStatus.NotLoaded()`, `.Failed()` or `.From(report)`. `From(report)` is
exactly what the host publishes for that config and those modules (`DeclarationsPassed = report.Passed`, the report's catalogs
for `TryGetCatalog`):

```csharp
var report = ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(dir), new IdentityModule(), new GameModule());
var result = new StartupCheckResult();

new RewardsCheck(TestGameConfigStatus.From(report)).Check(result);

result.Errors.ShouldBeEmpty();
```

### 12.6 Two versions

| Version | Hashes | Carried as |
| --- | --- | --- |
| server (`IGameConfig.ConfigVersion`) | label, every effective file, the `clientVisible` set | `config_version` (meaning unchanged), receipts, `ICommandContext.ConfigVersion`, metric `config_version_info`, readiness `configVersion` |
| client (`IGameConfig.ClientConfigVersion`) | label, the client-visible files only | `client_config_version` on login, snapshot, command, error envelope and `GetConfig` responses; metric `client_config_version_info`; readiness `clientConfigVersion` |

| Change | Server version | Client version |
| --- | --- | --- |
| a server-only file's bytes | changes | **unchanged** |
| a client-visible file's bytes | changes | changes |
| a file added to or removed from `clientVisible` (base or overlay) | changes | changes |
| the label | changes | changes |
| order or duplicates in the manifest lists | unchanged | unchanged |

Do not bump the label because a file became client-visible: both versions change by themselves. The server version's value
changes once at the upgrade to 0.7.0 (015/BC-5). `GetConfig` answers `not_modified` for either current version. Custom
`IGameConfig` implementations add `ClientConfigVersion`; build test configs with `TestGameConfig` or
`new GameConfig(label, files, clientVisible)` (the 4-argument constructor is obsolete, 015/BC-6).

### 12.7 Compression and the bundle limit

`GetConfig` is compressed per request with Brotli (quality 4) or gzip (level 6) when the request's `Accept-Encoding` asks for
it, the response carries files and is at least 1024 bytes; an encoding that does not shrink the bundle (checked once at
startup) is never used, and at most max(1, CPUs / 4) responses per host are compressed at the same time (others are sent
uncompressed, so a burst of full downloads cannot take the CPU); no other route is ever compressed. Each full response sent
uncompressed although the client accepted an encoding is counted in `config_compression_skipped_total{reason}`: `busy` (no free
permit) or `probe` (the client accepts only encodings the startup probe dropped). `not_modified` and small bodies
are sent as they are. Unity's UnityWebRequest sends `Accept-Encoding: deflate, gzip` and decodes gzip itself (Editor and
Windows player checked); `HttpClient` without `AutomaticDecompression` sends no header and gets the plain body. No client change
is needed. `GameServer:ConfigCompression=false` is the rollback switch (today's bytes and headers, no `Vary`).

`GameServer:Limits:ConfigBundleBytes` (1 MiB) limits the **uncompressed** response. The host computes the largest response its
config can produce at startup; over the limit it stops with `[config] the client-visible bundle needs a GetConfig response of
up to {size} bytes ({files} files), over GameServer:Limits:ConfigBundleBytes = {limit}. Fix: raise the limit or remove files from
'clientVisible' (CFG-03, API-06).` (015/BC-7; before, every client got 413). The startup log has one line with both versions:
`Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount}
client-visible, bundle {BundleBytes} B, compression {Compression}), {RuleCount} rules, {CatalogCount} catalogs.`, where
`{Compression}` is `off` (rollback switch) or the encodings the probe kept and the host's permits, e.g. `br,gzip, permits 2`.

### 12.8 The persistent cache in Unity

`PersistentConfigCache` keeps the last client config on disk, so a second app start has `client.Config` before login and,
while the client version is unchanged (also across server-only edits), downloads nothing. One folder per server environment:
`UnityConfigCache.Create(environmentName)` roots it at `Application.persistentDataPath/gameserver-config/{environmentName}`.
From `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`:

```csharp
var client = new GameServerClient(
    new GameServerClientOptions(serverUrl, Application.version, platform, Application.buildGUID),
    new UnityWebRequestTransport(),
    config: UnityConfigCache.Create("local"));
```

and after login:

```csharp
// The persistent cache (FG-019) holds the last client config: a start whose version is unchanged downloads nothing.
if (client.ConfigOutdated)
{
    if (await client.RefreshConfigAsync())
    {
        log($"config downloaded {client.Config.Version}: {string.Join(", ", client.Config.Files.Keys)}");
    }
}
else if (client.Config.Version != null)
{
    log($"config from cache {client.Config.Version}: {string.Join(", ", client.Config.Files.Keys)}");
}
```

`environmentName` is a plain folder name chosen by the game, one per server it talks to (`local`, `dev`, `beta`, `live`): keep
it in the client settings next to the server URL. `Create` accepts 1 to 64 lowercase letters, digits, `.`, `_` and `-` (not
starting or ending with `.`, not a Windows device name such as `con` or `nul`), the same on every platform, and throws
`ArgumentException` otherwise: `localhost:5080` is refused everywhere (use `localhost-5080`). On iOS the folder is excluded from
iCloud/iTunes backup. Without the
`ConfigOutdated` gate above, a start with an unchanged client version still sends one `GetConfig`, answered `not_modified` (no
files downloaded).

A damaged or unknown-format file reads as an empty cache (`LoadProblem` says why); a failed save keeps the new files in memory
(`WriteProblem`); it never throws for file problems. It is not thread-safe (use it from the main thread, like the client) and
not encrypted. Format and rules: PCR unity-sdk.

## 13. Login, account creation and command robustness

Slice 016 (FG-013, FG-015, FG-029, FG-016, FG-022). The breaking changes are listed as `016/BC-1` … `016/BC-11` in the release
notes; PCR platform §22 has the limits table and PCR unity-sdk the client rules.

### 13.1 The login flow

`LoginResponse.is_new_player` (SDK `LoginResponse.IsNewPlayer`) is `true` only for the login whose transaction created the
player. It is `false` for a returning player, for the loser of two racing first logins of one credential, for a second
device, for an unconfirmed `switch_account` and — the case to design for — **for the retry of a first login whose response
was lost**: the server committed the creation, so the retry is a normal login. Use the flag for presentation only (a welcome
screen); keep onboarding progress that must survive a lost response in the game's own state (an aggregate the game writes,
for example a `tutorialStep` that an account initializer seeds, section 13.2). The analytics login record's `isNewUser`
comes from the same value.

The login answer holds every client-visible section with its `rev`: **the login sections are the boot read**. Apply them and
mark the client ready; no second snapshot call is needed. From
`samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`:

```csharp
var login = await client.LoginGuestAsync(localId, secret);
log($"Player {login.PlayerId}, server time {client.ServerNow:O}");

// The login answer holds every client-visible section with its rev: it is the boot read, no snapshot call (FG-013).
log($"new player: {login.IsNewPlayer}");
```

A section the server cannot read is left out and listed (section 13.3). Login store failures answer typed codes instead of
500 (016/BC-10): an ambiguous commit `COMMIT_UNKNOWN_RETRY` (503, retryable), an expired transaction or three lost insert races
`CONCURRENT_UPDATE` (409, retryable). The SDK never retries a login after an unknown outcome; the game shows a retry.

### 13.2 Account initializers

An `IAccountInitializer` (namespace `GameServer.Accounts`) writes the starting state of a new account **in the transaction
that creates it**: either the profile and every initializer's writes commit together, or nothing does. Register it in the
module's `ModuleDescriptor.AccountInitializers`. From `tests/ConsumerFixture/src/Fixture.Game/FixtureWelcomeInitializer.cs`
and `FixtureModuleDefinition.cs`:

```csharp
public sealed class FixtureWelcomeInitializer : IAccountInitializer
{
    public const string Name = "fixture.welcome";
    public const string WelcomeText = "Welcome! Write your first note here.";

    public static AccountInitializerRegistration Registration { get; } =
        AccountInitializerRegistration.Of<FixtureWelcomeInitializer>(Name, [FixtureModuleDefinition.NotesAggregate]);

    public ValueTask<ErrorCode?> InitializeAsync(ICommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var maxLength = context.Config.GetFile<NotesRules>(FixtureModuleDefinition.RulesFile).MaxLength;
        var text = WelcomeText.Length <= maxLength ? WelcomeText : WelcomeText[..maxLength];
        context.Set(FixtureModuleDefinition.NotesAggregate, new NotesState { Text = text, Saves = 0 });
        return ValueTask.FromResult<ErrorCode?>(null);
    }
}

// FixtureModuleDefinition.Describe():
// Runs once per account, in the transaction that creates it (FG-015): the welcome note.
AccountInitializers = [FixtureWelcomeInitializer.Registration],
```

Rules (checked at startup; one report line per fault, 016/BC-9):

- **Name** `<area>.<name>` in lowercase, unique per deployment (no "module name" prefix rule).
- **Touches**: the aggregates it may read and write, never `profile`. `profile` plus the union of every initializer's touches
  is at most 4 aggregates (`CommandDescriptor.MaxTouches`); the claim markers of all initializers together are at most 2. A
  `Get`/`Set` outside the touches throws the runner's message naming the initializer
  (`Command 'identity.create_account (initializer fixture.welcome)' wrote aggregate 'inventory' which is not declared in Touches (DAT-09).`).
- **Purity**: like a command handler. The transaction may run it more than once (a conflict re-runs the attempt), so read
  only the context (`Config`, `Now`, `NewId`, `NewSeed`) and DI-injected catalogs; no I/O, clock or random of its own. It is
  created per attempt from DI (constructor injection).
- **Order**: module registration order, then list order, over one shared context.
- **Receipt**: when an initializer changed an aggregate, reserved a claim or added an outbox entry, the creation writes one
  receipt `identity.create_account` (actor type `system`, actor id = the player); its outbox (ledger entries of grants) is
  relayed like a command's. Without effects, only the profile is written.
- **Failure**: a returned code, a throw or a broken rule (aggregate size, an audit entry, receipt size) fails the creation:
  nothing is written, the login answers 500 `INTERNAL`, one Error line in category `GameServer.Accounts`
  (`Account creation of player {id} failed: … Nothing was created; the next login retries.`, never an exception message)
  and `account_initializer_failures_total{initializer, result}` plus `account_creations_total{result="failed"}`. The next
  login of the same credential retries the creation.
- **Legacy accounts** (created before the initializer was registered) are never seeded later (no backfill). Two ways out:
  keep the old "init" game command for them (guarded by a state flag, as before), or — before launch — reset the dev and
  test data so every account is created with the initializer.

Test it host-free with the kit: `ForAccountInitializer(name, modules…)` runs it behind the account writer's view (system
actor for `p_test`, touches at their default state, operation `identity.create_account`), and `CheckAccountCreation()`
returns the writer's rule lines (empty = the creation commits). From
`tests/ConsumerFixture/tests/Fixture.Tests/FixtureInitializerTests.cs`:

```csharp
private static CommandContextDouble Creation(int maxLength) =>
    CommandContextDouble.ForAccountInitializer(FixtureWelcomeInitializer.Name, new IdentityModule(), new FixtureModule())
        .WithConfig(TestGameConfig.FromJson(
            new Dictionary<string, string> { [FixtureModuleDefinition.RulesFile] = $"{{\"maxLength\":{maxLength}}}" },
            "fixture-test"));

[Fact]
public async Task A_new_account_starts_with_the_welcome_note_and_the_creation_would_commit()
{
    var context = Creation(maxLength: 40);

    (await new FixtureWelcomeInitializer().InitializeAsync(context)).ShouldBeNull();

    context.Written<NotesState>(FixtureModuleDefinition.NotesAggregate)
        .ShouldBe(new NotesState { Text = FixtureWelcomeInitializer.WelcomeText, Saves = 0 });
    context.Outbox.ShouldBeEmpty();
    context.CheckAccountCreation().ShouldBeEmpty();
}
```

Every new player of a host with an initializer that has effects gets one creation receipt; a test that counts a new
player's receipts or asserts `rev = 0` on a seeded section changes (016/BC-6).

### 13.3 Degraded reads

One broken aggregate no longer takes down the snapshot or the login (FG-029). Two codes:

| Code | Number | HTTP | Retryable | Cause |
| --- | --- | --- | --- | --- |
| `AGGREGATE_VERSION_AHEAD` | 24 | 503 | yes (`retry_after_ms = 1000`) | the stored `schemaVersion` is newer than the code's (a rolling deploy) |
| `AGGREGATE_UNREADABLE` | 25 | 500 | no | an upgrader is missing or threw, an envelope field has the wrong type, the state is missing, `null`, not a JSON object or does not deserialize, or the wire mapper threw |

- The snapshot, the login, a command replay, the command response after a mapper failure and the GM player view answer 200
  **without** the broken section and list it in `unavailable_sections` (`{id, code, code_number}`, ordered by id). A section is
  never sent with an empty payload; every other section is byte-identical (016/BC-3).
- A command that touches or reads a broken aggregate answers the typed error envelope before its handler and writes
  nothing (016/BC-4). A broken `profile` fails the login and every authenticated request with the code.
- The SDK marks the section and keeps its last good copy: `client.Sections.IsUnavailable(id)`,
  `client.Sections.Unavailable[id].Code`, event `AvailabilityChanged(id)`; the next `Set`/`Apply` of it clears the mark. A login
  of the **same** player keeps the copies it lists as unavailable; another player's login starts empty (016/BC-8).
  `AGGREGATE_VERSION_AHEAD` is retried by the SDK's retryable rule with the same bytes and request id.
- Show a recoverable state for a marked section (for example "this part of your account is being updated, try again"),
  and never treat a missing section as "default state": a write from that state would overwrite the real data.
- **Deploy order** (PCR platform §6, unchanged): deploy code that reads a new `schemaVersion` everywhere before any instance
  writes it. The degradation only keeps the other sections and the login working during the window; alert on
  `aggregate_read_failures_total{reason="version_ahead"}` during a deploy.

### 13.4 Command limits

The five `GameServer:Commands:*` settings are validated at startup, one line per value out of range (016/BC-2):
`GameServer:Commands:ReplayWindowHours` 1..168 (default 24), `GameServer:Commands:PerPlayerQueueLimit` 0..16 (2),
`GameServer:Commands:ReceiptMaxBytes` 1024..65536 (4096), `GameServer:Commands:ResultCompactMaxBytes` 64..16384 (512),
`GameServer:Commands:MaxAggregatesPerGame` 1..16 (16), plus the cross rule `4·⌈ResultCompactMaxBytes/3⌉ + 1024 ≤
ReceiptMaxBytes` (the largest result must fit its own receipt). Because of the cross rule the smallest `ReceiptMaxBytes`
that starts is 1112 (with `ResultCompactMaxBytes` = 64). PCR platform §22 has every limit with its cost.

Which to raise: `ReceiptMaxBytes` when a command adds many outbox entries (each ledger entry of a grant is about 278 bytes in
the receipt; at 4096 about 11 entries fit, at 16384 about 55); `ResultCompactMaxBytes` when a result is large (prefer returning
less: the result is stored base64 in the receipt and replayed byte-identical). Cost: every receipt is stored for the replay
window (`ReplayWindowHours`) and read on every replay, so a larger limit means larger receipt documents and more bytes per
replay; the maximum (65536) keeps a receipt within the 64 KiB document budget.

Going over a limit is the business rejection `COMMAND_OUTPUT_TOO_LARGE` (23, HTTP 200, not retryable, 016/BC-1) with details
`part` (`result` or `receipt`), `bytes` and `limit`; exactly at the limit commits. Nothing commits and no receipt is written,
so a retry with the same request id runs again and gets the same code. The server logs one Error line (`Command {Operation}
refused with COMMAND_OUTPUT_TOO_LARGE: {Part} is {Bytes} bytes, limit {Limit}. Fix: return less or raise
GameServer:Commands:{SettingName} (PCR platform §22).`) and counts `command_output_too_large_total{operation, part}`. Treat it as
a design bug of the command. Rejection details of every HTTP 200 code reach the client in `CommandResponse.details`
(module codes too, 016/BC-11): keep them safe to show.

Check it in a unit test instead of measuring on a file store: `CheckCommit(outcome, encodeResult)` measures the receipt with
the runner's own builder (section 5.1). From `tests/ConsumerFixture/tests/Fixture.Tests/FixtureReceiptSizeTests.cs`:

```csharp
/// <summary>The endpoint's result mapping (<c>FixtureModule.MapPlayerApi</c>): the receipt stores these bytes.</summary>
private static byte[] Encode(object result) => new StringValue { Value = ((SaveNoteResult)result).Text }.ToByteArray();

[Fact]
public async Task A_save_fits_the_default_limits()
{
    var context = CommandContextDouble.Create(SaveNoteHandler.Operation, new IdentityModule(), new FixtureModule()).AsPlayer("p_1").WithConfig(Rules);

    var outcome = await new SaveNoteHandler().HandleAsync(new SaveNote(new string('n', 40)), context);

    outcome.IsOk.ShouldBeTrue();
    context.CheckCommit(outcome, Encode).ShouldBeEmpty();
}
```

With `WithCommandSettings(new CommandSettings { ReceiptMaxBytes = … })` set to the game's value, a command whose receipt
would exceed it returns the runner's line (same file, the host and the kit measure the same bytes):

```text
Command 'fixture.save' receipt would be {bytes} bytes; ReceiptMaxBytes is {limit}: the runner rejects the command with COMMAND_OUTPUT_TOO_LARGE (part receipt) and nothing commits. Fix: fewer ledger/outbox entries or raise GameServer:Commands:ReceiptMaxBytes.
```

The double applies the limit as given (the ranges are checked by the host at startup). Seed the aggregates the command
writes at the revision the host has (`Seed(id, state, rev)`): the receipt records the new revisions.

### 13.5 Request ids and the pending store

A command whose answer is lost (app killed, network gone after the server committed) has an unknown outcome. Resending it
with the **same request id and the same bytes** replays the committed command instead of running it twice (CMD-02, within
`GameServer:Commands:ReplayWindowHours`, default 24 h).

- `GameServerClient.NewRequestId()` returns a lowercase UUID; pass it (or `null` = generated) as the fourth argument:
  `SendCommandAsync(route, request, parser, requestId)`. Every retry of the call sends it; `CommandResult.RequestId` and
  `GameServerError.RequestId` carry it. An id that is not a UUID in format D throws `ArgumentException` before any network
  call. The three-argument call is unchanged.
- `GameServerClientOptions.PendingCommands` (opt-in, default null): with a store, every command is recorded before its first
  attempt and removed once the server answered it (a command response with any code, or an error that is not retryable). It
  stays when the outcome is unknown (no response, an unreadable response, a retryable last answer, cancellation). Login,
  logout, snapshot, config and time calls never touch it. The SDK never resends by itself.
- Unity: `UnityPendingCommandStore.Create(environmentName)` →
  `Application.persistentDataPath/gameserver-pending/{environmentName}` (the config-cache name rule, section 12.8; iOS no
  backup). **One client per folder**, used from the Unity main thread. From
  `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`:

```csharp
var client = new GameServerClient(
    new GameServerClientOptions(serverUrl, Application.version, platform, Application.buildGUID)
    {
        // A command whose answer was lost (app killed, network gone) stays on disk until the next start (FG-022).
        PendingCommands = UnityPendingCommandStore.Create("local"),
    },
    new UnityWebRequestTransport(),
    config: UnityConfigCache.Create("local"));
```

- After login, list `client.PendingCommands`: `StatusOf(entry)` is `Resendable`, `OtherPlayer` (another player's, or no
  session) or `Expired`; resend the `Resendable` ones with `ResendPendingAsync(entry, parser)` (a committed command answers
  `Replayed` with its original receipt) and discard the others with `DiscardPendingCommand(id)` after deciding from the
  player's state. **Iterate a copy**: `PendingCommands` is the store's live list and a resend or a discard removes the entry
  from it. Same file:

```csharp
// A copy: PendingCommands is the store's live list, and a resend or a discard removes the entry from it.
foreach (var pending in new List<PendingCommand>(client.PendingCommands))
{
    var status = client.StatusOf(pending);
    log($"pending {pending.RequestId} {status}");
    if (status != PendingCommandStatus.Resendable)
    {
        client.DiscardPendingCommand(pending.RequestId);
        continue;
    }

    bool replayed;
    switch (pending.Route)
    {
        case SampleGameRoutes.BattleStart:
            replayed = (await client.ResendPendingAsync(pending, StartBattleResult.Parser)).Replayed;
            break;
        case SampleGameRoutes.BattleEnd:
            replayed = (await client.ResendPendingAsync(pending, EndBattleResult.Parser)).Replayed;
            break;
        case SampleGameRoutes.ShopBuy:
            replayed = (await client.ResendPendingAsync(pending, BuyItemResult.Parser)).Replayed;
            break;
        default:
            // A route this build no longer sends: nothing to parse its result with.
            client.DiscardPendingCommand(pending.RequestId);
            continue;
    }

    log($"resent {pending.RequestId}: replayed={replayed}");
}
```

and for a command whose outcome is unknown after the SDK's retries:

```csharp
var endRequestId = GameServerClient.NewRequestId();
try
{
    var end = await client.SendCommandAsync(
        SampleGameRoutes.BattleEnd,
        new EndBattleRequest { BattleId = start.Result!.BattleId, Won = true },
        EndBattleResult.Parser,
        endRequestId);
    endCode = end.Code;
    log($"EndBattle → {end.Code}");
}
catch (GameServerException) when (IsPending(client, endRequestId))
{
    // Unknown outcome (no answer after the retries): the store keeps it and the next start resends it.
    endPendingId = endRequestId;
    log($"EndBattle → pending {endRequestId}");
}
```

- **Max age vs replay window**: `GameServerClientOptions.PendingCommandMaxAge` (default 23 h, at most 168 h) marks entries
  `Expired` on the server clock, so a device clock change does not move it. Keep it below the server's `ReplayWindowHours`:
  after the window a resend would run the command again instead of replaying it. Raise both together if the game must
  survive longer offline periods.
- A disk failure never stops a command: the store keeps the entry in memory and reports `WriteProblem`; damaged files are
  skipped and named in `LoadProblem`. File format and the crash table: PCR unity-sdk.
