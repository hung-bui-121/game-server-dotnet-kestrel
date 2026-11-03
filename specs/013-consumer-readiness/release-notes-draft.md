# Release notes draft — v0.7.0 (slice 013 consumer-readiness)

Input of the `v0.7.0` annotated tag message (FR-090). Each user story appends its part; the final text is copied into the
tag message when the slice closes. Slices 014–016 append their own sections to this file; the `v0.7.0` tag message is built
from the merged notes of 013–016 (one tag for the round).

FG ids fixed by slice 013: FG-010, FG-001, FG-006, FG-003, FG-004, FG-002, FG-007, FG-011, FG-012, FG-025. Breaking changes:
BC-1 … BC-8. Release rule (FR-051): the annotated tag of declared version `X.Y.Z` is `vX.Y.Z`; `GameServerFrameworkVersion` in
`Directory.Build.props` and `version` in `sdk/unity/com.gameserver.client/package.json` are bumped together in one commit
(`VersionTests` fails on a mismatch, naming both values). This round: `0.7.0` → tag `v0.7.0`.

## Fixed (FG ids)

- **FG-010 (HIGH, security)** — Dev-profile (cheat) endpoints and commands were refused only in Production: a host that
  registered `CheatModule` in beta or uat gave every player with a real bearer token a free resource grant. The Dev
  profile is now allowed only where development tools are allowed (Development/Test: flavors `local`, `dev` and framework
  test hosts), decided by one property `GameServerOptions.AllowsDevProfile` (= `AllowsDevelopmentTools`, no setting) and
  enforced by three independent layers:
  1. startup validation: one report line per Dev endpoint and per registered Dev command (also service-only modules,
     `Hosting:MapPlayerApi=false`, commands without route and `--provision`), exit 1;
  2. request pipeline: a Dev route reached anyway (mapped after `UseGameServer`) answers 403 `FORBIDDEN` before any
     authentication scheme runs (no session touch, no login/resume analytics event, no CSRF/body handling), with one
     Warning naming method, route template and environment; an unknown security-profile value is refused the same way;
  3. command runner: Dev commands are refused with `FORBIDDEN` for every actor on `ExecuteAsync` and `TryReplayAsync`,
     before the target-player check (a Dev command without target player is `FORBIDDEN`, not `BAD_REQUEST`), the
     per-player limiter, the receipt read, the guards and the transaction (a Dev receipt from a development store is
     never replayed); `command_results_total{code=FORBIDDEN}` counts both paths.
  `DeploymentEnvironment=Test` (which allows the development tools) is accepted only with `EnvironmentId=test` (BC-8).
  Development and Test behave as in `v0.6.0`.
- **FG-001** — consumer test support: a game could only link three sample-free files of `tests/Shared` and copy the
  local-flavor factory settings by hand. New project `src/GameServer.TestKit` (namespace `GameServer.TestKit`, no test
  framework, no sample, referenced by path): `ProtobufHttp`, `IdentityHttp`, `TestStores`/`TestStoreKind`, `GmBrowser` +
  `CookieJarHandler`, an in-process `TestHost` (Identity only by default, caller-chosen modules, in-memory or file store,
  fake clock, test authentication, in-memory analytics sink, `Logs` and structured `LogEntries`), `TestGameConfig`
  (`Empty`, `FromJson`, `FromDirectory`; the host loader's `ConfigVersion`), and `LocalFlavorFactory<TEntryPoint>`
  (`WebApplicationFactory` on the host's flavor file with `LocalFlavorSettings` applied to one temporary root per
  factory: `data/`, `analytics/`, `logs/`, instance id `lf-{8 hex}`, sweeper/jobs off, no drain delay; root deleted on
  dispose unless `LocalFlavorOptions.Folder` is given). The framework's own tests run on the kit (`SampleTestHost`,
  `SampleHostSmokeTests`) and a consumer-shaped fixture (`tests/ConsumerFixture`) builds and tests against it on every
  framework build. `Microsoft.AspNetCore.Mvc.Testing` flows through the kit; the game test project needs no direct
  reference. A host with `GameServer.TestKit` loaded fails startup outside Development/Test (except on the kit's own
  in-process test server), so the kit cannot ship in a production host by accident.
- **FG-006** — command-context test double: `CommandContextDouble.Create(operation, modules…)` is an `ICommandContext`
  for handler unit tests without host, store or DI. It wraps the runner's own context, so every access rule
  (Touches/Reads, state type, claim limit, `receipts`/`pa_*` claims, duplicate and pre-existing claims) throws the
  runner's exception and message, and `NewId`/`NewSeed` equal the runner's for the same receipt id (conformance test).
  Configure actor (`AsPlayer`, `AsGm`, `WithActor`), `At`, `WithConfig`, `WithRequestId`/`WithReceiptId`, `Seed`,
  `SeedClaim`, `WithCommandSettings`; read `Writes`/`Written<T>` (like the runner, a `Set` of the loaded state or a change
  set back is no write), `Outbox` (audit and analytics entries included), `Claims`; `CheckCommit(outcome)` returns the
  runner's commit-rule messages (uncatalogued or non-200 commit code, written aggregate above its `HardBytes` = the
  runner's `AGGREGATE_TOO_LARGE`, GM audit count) and `CheckCommit(outcome, encodeResult)` also the resultCompact limit.
  Not checked: the receipt size (`ReceiptMaxBytes`; the double has no request body hash, config version or host
  encoding).
- **FG-003** — a consumer's root `Directory.Build.targets` reached framework projects (the framework had none, so MSBuild
  imported the nearest one above it). The framework root now has its own behaviour-free `Directory.Build.targets`, which
  imports no parent; a game may add its own root `Directory.Build.targets` for its projects.
- **FG-004** — a framework checkout below any folder named `tests` built every framework project as a test project (no
  analyzers or banned APIs; `CS0246 'Xunit'`), because `IsTestProject` matched `\tests\` anywhere in the absolute path.
  It is now decided by the path relative to the framework root: true exactly for projects under the framework's own
  `tests/` folder. `BuildSealingTests` (every test run: framework projects evaluated below a hostile `tests/` parent with
  its own `Directory.Build.props`/`.targets`) and `tools/local/check-build-seal.ps1` (full Release build in that layout:
  0 errors, 0 warnings, no marker) check both fixes.
- **FG-002** — `tools/local/run-local.ps1|.sh` hardcoded the sample host, its port, SDK version and log folder, so a game
  kept ~200-line copies of both. New parameters `-HostProject`/`--host-project` (project file or its folder),
  `-LaunchProfile`/`--launch-profile` (default `local`) and `-Name`/`--name`; the host is read (never written) by the new
  framework tool `tools/GameServer.LocalRun` (`describe`): launch profile flavor and first `http://` port (none → 5080
  with a note), `FileStore:Folder` and `Observability:Logging:Directory` of the flavor's settings (+ environment
  variables, profile variables last), `sdk.version` of the nearest `global.json` (a non-string value is invalid input).
  Invalid host input exits 2 naming the cause and the fix; the host starts with `dotnet run --project <file>`, so its
  folder may hold other project files; `run-local.sh` warns on stderr under bash < 4.4 (stock macOS 3.2: no forced
  stop after 35 s); the banner of a non-sample host has only host-independent lines (name, project, base URL, health);
  a failure before ready also tails the host's newest log file. Without the new parameters both scripts behave as in
  `v0.6.0` (sample banner unchanged; new line `run-local: host project …`; LAN warning text is host-neutral).
  Repeatable checks: `RunLocalScriptTests` (exit 2/4 every run; start/stop of the sample and the fixture host with
  `GAMESERVER_SCRIPT_TESTS=1`) and `LocalRunDescribeTests`.
- **FG-025** — per-request visibility: the framework wrote no event per request and its Serilog pipeline reached no
  console, so a game had to buffer and parse response bodies to log what clients received. Now:
  1. one structured event per HTTP request (category `GameServer.Requests`, event id `1`/`Request`, template
     `{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms`) with `HttpMethod`, `Route` (template; unmatched
     paths: first segment + `/...`, cleaned and cut at 120), `Profile`, `Operation`, `ActorType`, `ActorId` (principal only, cleaned, ≤ 64),
     `RequestId`, `TraceId`, `Status` (null when aborted), `ResultCode` (`OK`, business code at HTTP 200, error code,
     `ABORTED`, `INTERNAL`, `HTTP_{status}`), `Replayed`, `NotModified`, `Aborted`, `DurationMs`; Information < 400,
     Warning 4xx/aborts, Error 5xx; never for `/health/live`, `/health/ready` or `GameServer:RequestLog:ExcludedPaths`;
     no body, query string, header, cookie, token, password or IP; on by default in Development/Test only;
  2. public `IRequestOutcomeFeature` (`HttpContext.RequestOutcome()`, namespace `GameServer.Platform.Web.Requests`), set by
     every framework writer and final after the pipeline, so a game middleware placed before `UseGameServer` reads the
     same code/status/operation/replayed/not-modified as the event without touching the body (also through a reference
     taken before `await next()`: the framework finalizes the instance it finds); consumer endpoints may set their own
     code; `RequestResultCodes.Aborted`/`ForStatus`, `RequestLog.Category`/`EventId`;
  3. optional console sink `Observability:Logging:Console` (`Enabled` false, `MinimumLevel` Information, `BufferSize`
     1 000): one human-readable line per event (control characters of every value printed as `?`: no terminal escape
     sequences), its own bounded dropping queue, in addition to the unchanged file sink; the sample host's `Local` and
     `Dev` flavor files turn it on. `log_dropped_total` (and `log_queue_entries`) now carry the tag `sink=file|console`
     (a sum over the tag equals the former value); the GM health detail `log` reports `dropped` (file) and
     `consoleDropped`; `LogSinkMonitor` is no longer itself a Serilog `IAsyncLogEventSinkMonitor`: its per-sink queues are
     `FileQueue`/`ConsoleQueue` (`LogQueueMonitor`), `DroppedCount` stays the sum (no consumer use found);
  4. new counter `request_log_failures_total` (meter `GameServer.Platform`) for events that could not be built or written.
  Measured in process (Windows 11, Release, 2026-11-03): p95 of a command round trip 0.786 ms off vs 0.681 ms with event +
  console (noise); stalled console + saturated file queue: 2 000/2 000 requests OK in 0.23 s, drops counted.

- **FG-007** — no version anywhere: framework assemblies said `1.0.0.0`, nothing logged or exported which framework or
  game build ran, so the game stamped a framework tag into its own assembly and logged it by hand. Now:
  1. one declaration `GameServerFrameworkVersion` = `0.7.0` in the framework's `Directory.Build.props` → every framework
     assembly (also when built as a project reference from a game) has AssemblyVersion `0.7.0.0`, FileVersion `0.7.0.0`,
     InformationalVersion `0.7.0+{40-hex git sha}` (verified in a clone and in a submodule checkout); the Unity package
     `com.gameserver.client` is `0.7.0` too;
  2. public API (`GameServer.Platform`): `GameServerVersion.Framework` (`0.7.0`), `FrameworkInformational`,
     `Of(Assembly)`/`InformationalOf(Assembly)`, and the singleton record `GameServerRuntimeInfo` (game id, environment id,
     deployment environment, instance id, game assembly = `IHostEnvironment.ApplicationName`, game version = its
     informational version, framework version), also `GameServerBuilder.Runtime`; `GameServerRuntimeInfo.ResolveInstanceId`
     is now the only implementation of the instance-id rule (log file names, `service.instance.id`, analytics files, GM);
  3. startup line (Information, category `GameServer.Startup`, after a successful validation; never with `--provision`):
     `Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}), instance {InstanceId}.`;
  4. telemetry resource: `service.version` = game version, new attribute `gameserver.framework.version`;
  5. GM `GET /gm/v1/ops/version` (`ops.read`): `{gameId, environmentId, instanceId, gameVersion, frameworkVersion}` of the
     instance that answered (OpenAPI regenerated); public endpoints and health probes carry no version.
- **FG-011** — no reserved range for game error codes: a framework release could take the range a game used. The band
  **50000–89999** is reserved for game (consumer) modules (`GameServer.Errors.ErrorCodeBands.GameStart`/`GameEnd`;
  `FrameworkReservedStart`/`End` = 90000–99999); `ErrorCodeBandTests` fails, naming module and range or code, if a framework,
  sample or template module ever uses it. The module-authoring example moved from 50000–50099 to the template's 90000–90099.
- **FG-012** — no consumer documentation: new English `docs/guides/consumer-guide.md` (submodule pinned to an annotated tag,
  build files mirrored vs sealed, CPM import, project references, host composition, test kit, local run, versions, request
  log and console, error band, Unity SDK by UPM URL `…#v0.7.0`, bump checklist), every sample copied from
  `tests/ConsumerFixture`; `docs/guides/module-authoring.md` §8 and `docs/html/02-them-command.html` §4.6/§4.8/§5 step 7
  give the in-repo and the consumer-repo variant (kit `TestHost`, `CommandContextDouble` handler test). Walked through on a
  fresh game repository (two gaps found and fixed in the guide: copy `framework/.editorconfig`; `config/base/manifest.json`).

## Bug fixes

- **Resume tracker spurious clear (slice 012 `DailyPlayerSet`)** — when two requests of the same not-yet-seen player
  raced and the winner's add filled the per-day set to its capacity (`GameServer:Analytics:ResumeTrackerCapacity`,
  default 200 000), the loser treated the full set as "new player at capacity": it cleared the day's set, counted a
  `login_resume_tracker_resets_total` and emitted a second `source=resume` event for that player. The capacity path now
  looks the player up again after reading the full count, so only a player that is really absent clears the set.
  No configuration or contract change.

## Breaking changes and migration

### BC-1 — Dev endpoints and commands fail startup outside Development/Test

Was: only Production. Now: every deployment environment other than Development and Test (Staging = beta, uat;
Production = live; any other value), one line per Dev endpoint and per Dev command in the aggregated startup report.

Migration:

1. Register cheat/Dev modules only inside `if (gameServer.Options.AllowsDevelopmentTools)` (as
   `samples/SampleHost/Program.cs`).
2. Start each non-development flavor once (`--provision` included) to confirm.
3. Composition tests that build Staging/Production hosts with a Dev module: expect the new failure or drop the module.
4. The `FRAMEWORK-GAP FG-010` markers can go; the condition stays as hygiene.

### BC-2 — Dev requests and Dev commands are refused at run time outside Development/Test

A Dev route mapped after `UseGameServer` answers 403 `FORBIDDEN` (error contract) plus a Warning outside
Development/Test; the runner refuses Dev commands there (jobs, GM flows, direct `ICommandRunner` callers).

Migration: same as BC-1; jobs or GM flows that call Dev commands in Staging must use a non-Dev command.

### BC-3 — startup message text for Dev endpoints changed, new line for Dev commands

Old:

```text
[endpoint POST /api/v1/cheat/grant] uses profile Dev in Production. Fix: do not register cheat/dev modules in Production (OPS-09).
```

New (example for beta):

```text
[endpoint POST /api/v1/cheat/grant] uses profile Dev in deployment environment 'Staging' (environment id 'beta'). Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09, SEC-08).
[module economy.cheat] command 'cheat.grant' has profile Dev in deployment environment 'Staging' (environment id 'beta'). Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09).
```

Runner refusal (`GameServerException` message, code `FORBIDDEN`):
`Dev command 'cheat.grant' refused in deployment environment 'Staging' (environment id 'beta').`
Pipeline Warning: `Dev endpoint POST /api/v1/cheat/grant refused in deployment environment Staging (beta).`

Migration: tests matching the old text match the endpoint label (`[endpoint POST /api/v1/cheat/grant]`) or the new text.

### BC-4 — framework test helpers moved from `tests/Shared` into `src/GameServer.TestKit`

Moved (namespace `GameServer.Tests.Shared` → `GameServer.TestKit`, otherwise the same members unless noted):

| Removed file | Replacement |
| --- | --- |
| `tests/Shared/ProtobufHttp.cs` | `src/GameServer.TestKit/ProtobufHttp.cs` (`ProtobufHttp`), unchanged |
| `tests/Shared/IdentityHttp.cs` | `src/GameServer.TestKit/IdentityHttp.cs` (`IdentityHttp`), unchanged |
| `tests/Shared/TestStores.cs` | `src/GameServer.TestKit/TestStores.cs` (`TestStores`, `TestStoreKind`); `TestStores.CreateFileStore` is no longer public (kit-internal for `TestHost`, FR-014) |
| `tests/Shared/GmBrowser.cs` | `src/GameServer.TestKit/GmBrowser.cs` (`GmBrowser`, `CookieJarHandler`), unchanged |
| `tests/Shared/TestHost.cs` | `src/GameServer.TestKit/TestHost.cs` + `CapturedLog.cs`: default modules **Identity only** (was Identity + `Sample.Counter`), game config = the new last parameter `config`, else `GameServer:ConfigPath` when the test's settings set it, else **`TestGameConfig.Empty`** (label `test`; `DefaultSettings()` no longer sets `GameServer:ConfigPath`); new `LogEntries` |

`tests/Shared` now holds framework-only support: `GameHttp.cs`, `SampleTestHost.cs` (the old defaults: Identity +
`Sample.Counter`, sample config folder), `FileStoreTestSupport.cs` (the former `TestStores.CreateFileStore`),
`GlobalUsings.cs` and `config/`. It is not consumer API.

Migration (game test projects):

1. Delete the `<Compile Include="$(GameServerRoot)tests/Shared/…">` links.
2. Add `<ProjectReference Include="$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj" />`; a direct
   `Microsoft.AspNetCore.Mvc.Testing` reference in the test project may stay but is no longer needed (its build targets
   flow through the kit).
3. Replace `using GameServer.Tests.Shared;` with `using GameServer.TestKit;`.
4. Replace hand-written local-flavor factories with `LocalFlavorFactory<Program>` (data now under `{Root}/data`: use
   `factory.DataFolder`, `AnalyticsFolder`, `LogFolder`, `InstanceId`; extra settings through `LocalFlavorOptions.Settings`,
   services through `LocalFlavorOptions.Services`, a restart on the same data through `LocalFlavorOptions.Folder`).
5. Replace a fake `ICommandContext` with `CommandContextDouble.Create(operation, modules…)` (pass every module whose
   aggregates the command touches or reads, e.g. `new IdentityModule()` for `profile`; config through `WithConfig` and
   `TestGameConfig.FromJson`).
6. Remove the `UPGRADE.md` sync lines for `tests/Shared` and the smoke test.

A game that called `TestHost.StartAsync()` from the linked file and relied on the Counter module or the sample config
passes its own `modules:` and `config:`/`GameServer:ConfigPath` now.

### BC-5 — consumer `Directory.Build.targets` no longer reaches framework projects; relative test-project rule

Was: a `Directory.Build.targets` in the game repository (or any folder above the framework checkout) was imported by every
framework project, and a framework project was a test project when its absolute path contained a `tests` folder. Now:
the framework root's own `Directory.Build.targets` (empty) is the nearest one for framework projects, and
`IsTestProject` is true exactly for projects under the framework's `tests/` folder (path relative to the framework root).
Sealed for framework projects: `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`.editorconfig` (`root = true`). Still taken from the consumer: the `global.json` of the folder `dotnet` runs in and the
`nuget.config` hierarchy (manual sync, FG-008).

Migration:

1. Anything a game injected into framework projects through its root `Directory.Build.targets` (knowingly or not) moves
   into the game's own projects or its `Directory.Build.props`; it no longer applies to framework projects.
2. The game may now add its own root `Directory.Build.targets` for game projects (remove the "never add a root
   `Directory.Build.targets`" rule and its `FRAMEWORK-GAP FG-003` marker).
3. Remove the `tests`-ancestor guard target (`FRAMEWORK-GAP FG-004`, e.g. `ZhPvpRefuseTestsAncestor`) and the "path
   must not contain a `tests` folder" notes in README, CLAUDE.md, COMMANDS and known limitations.
4. Nothing to do for a game that never had a root `Directory.Build.targets`; framework build output is unchanged.

### BC-6 — framework version stamped; telemetry carries `service.version` and the framework version

Was: framework assemblies `1.0.0.0` (file `1.0.0.0`, informational `1.0.0+{sha}`, the SDK default); telemetry resource without `service.version`.
Now: `0.7.0.0` / `0.7.0.0` / `0.7.0+{sha}`; `service.version` = the host's (game's) informational version,
`gameserver.framework.version` = the framework's; one startup Information line names both. Surfaces in dashboards,
assembly-version checks and log queries.

Migration:

1. Update dashboards, alerts or tooling keyed on assembly version `1.0.0.0` or on the absence of `service.version`; group
   by `service.version` (game) and `gameserver.framework.version` (framework).
2. Delete hand-written framework-tag stamping and the hand-written startup version line (zh-pvp-server:
   `src/ZhPvp.Host/BuildInfo.cs`, `AssemblyMetadata("FrameworkTag")` in `ZhPvp.Host.csproj`, the
   `zhpvp {GameVersion} on framework {FrameworkTag}` log call in `Program.cs`).
3. Keep the game's own `<Version>`: it becomes `service.version` and `gameVersion` of the GM read.
4. A "checked-out framework tag = pinned tag" check can compare the pinned tag with `"v" + GameServerVersion.Framework`.

### BC-7 — one request event per request in Development/Test, optional console output

Was: no per-request event, no console output from operational logs. Now: Development/Test hosts (flavors `local`, `dev`,
test hosts) write one `GameServer.Requests` event per request (~300–400 B per event in the file, ≈ 13–17 GB per instance
per day at 500 requests/s); Staging/Production write none unless `GameServer:RequestLog:Enabled=true` (then one startup
Warning with the volume estimate). With the default file limits (100 MiB × 30 files) 500 requests/s keep only ~4–5 hours
of operational logs. Test hosts capture one extra log entry per request (formatted message without actor id).

Migration:

1. Nothing in Staging/Production unless enabled. Before `GameServer:RequestLog:Enabled=true` there: budget the volume,
   raise `Observability:Logging:FileSizeLimitBytes`/`RetainedFileCountLimit` or ship the files; optionally
   `Observability:Logging:LevelOverrides:GameServer.Requests=Warning` (failures only).
2. Delete the game's `RequestLog/*` middleware, body capture, sink, startup filter, settings check, its registration and
   `ZhPvp:RequestLog:Enabled`, and its tests.
3. Set `Observability:Logging:Console:Enabled: true` in the game's local (and dev) flavor file.
4. Rebuild any game-specific per-request behaviour on `HttpContext.RequestOutcome()` in a middleware before
   `UseGameServer` (never parse response bodies).
5. Tests that count every log entry of a test host: filter category `GameServer.Requests` out, or set
   `GameServer:RequestLog:Enabled=false` for that host.

### BC-8 — `DeploymentEnvironment=Test` only with `EnvironmentId=test`

Was: `GameServer:DeploymentEnvironment=Test` was accepted with any environment id that is not a standard flavor, so a
deployed environment such as `qa` could run as Test — with test authentication, cheats (Dev profile), the staff seed and
the file/in-memory stores, like Development. Now (fail closed): startup refuses Test unless `GameServer:EnvironmentId`
is `test` — `[deployment] GameServer:DeploymentEnvironment=Test is reserved for automated test hosts with
GameServer:EnvironmentId 'test', but EnvironmentId is '<id>' …` (exit 1). Framework test hosts (`TestHost`,
`SampleTestHost`, `LocalFlavorFactory`) are unaffected (they use `test`, or Development).

Migration:

1. A deployed environment of the game that set `DeploymentEnvironment=Test`: use `Staging` (no development tools) or,
   for a developer-only environment, `Development`.
2. A game test host that sets `Test` with another environment id: set `GameServer:EnvironmentId=test`.

### Non-breaking but recommended — run the game host with the framework's run-local

1. Call `framework/tools/local/run-local.ps1 -HostProject src/<Game>.Host -LaunchProfile local -Name "<Game>"` (bash:
   `framework/tools/local/run-local.sh --host-project src/<Game>.Host --launch-profile local --name "<Game>"`) instead of
   the copied scripts.
2. Keep game-only pre-checks (for example the submodule check, exit 8) in a thin wrapper that calls the framework script
   and passes its exit code through; the framework uses 0 and 2–7 only.
3. The host needs `Properties/launchSettings.json` with the profile's `applicationUrl` (first `http://` URL = default
   port) and `environmentVariables.ASPNETCORE_ENVIRONMENT`, and a `global.json` at or above it; data and log folders come
   from its own flavor settings.
4. Remove the `FRAMEWORK-GAP FG-002` notes and the "keep in step on every framework bump" rule.

### New configuration keys (slice 013)

| Key | Default | Notes |
| --- | --- | --- |
| `GameServer:RequestLog:Enabled` | unset = on in Development/Test, off in Staging/Production | explicit `true` outside Development/Test → startup Warning |
| `GameServer:RequestLog:ExcludedPaths` | `[]` | ≤ 50 entries, each starts with `/`, ≤ 200 chars, no `?`; segment prefix, case-insensitive |
| `Observability:Logging:Console:Enabled` | `false` | sample `Local`/`Dev` set `true`; outside Development/Test → startup Warning; `LocalFlavorSettings` forces `false` (8th key) |
| `Observability:Logging:Console:MinimumLevel` | `Information` | Serilog level name |
| `Observability:Logging:Console:BufferSize` | `1000` | ≥ 1 |

No configuration key was added for versions (FR-050..FR-055) or the error band. New GM endpoint (additive):
`GET /gm/v1/ops/version` (`ops.read`).

## Consumer workarounds now removable

| FG | Workaround today (zh-pvp-server) | Removable after `v0.7.0` | Stays |
| --- | --- | --- | --- |
| FG-010 | `src/ZhPvp.Host/ZhPvpModules.cs` marker on the `AllowsDevelopmentTools` condition; `tests/ZhPvp.Tests/Host/CompositionTests.cs` marker (test as the only protection) | Both markers (framework refuses Dev in beta/uat itself) | The condition and the 5-flavor composition test may stay as game hygiene; CLAUDE.md rule 7 |
| FG-001 | `tests/ZhPvp.Tests/ZhPvp.Tests.csproj` links `tests/Shared/{ProtobufHttp,IdentityHttp,TestStores}.cs`; `tests/ZhPvp.Tests/Support/LocalFlavorHost.cs` copies the factory settings of the framework smoke test | The three `Compile` links (→ kit `ProjectReference`); `LocalFlavorHost` (→ `LocalFlavorFactory<Program>`, `Documents(...)` reads `factory.DataFolder`) | Nothing: the framework console sink (`Observability:Logging:Console:Enabled`) replaces the game's console capture hook |
| FG-025 | `src/ZhPvp.Host/RequestLog/*.cs` (middleware, body capture, envelope parsing, sink, startup filter, settings check; markers in `RequestLogMiddleware.cs`, `RequestOutcome.cs`, `RequestLogSink.cs`), registration in `ZhPvpModules.cs`, `ZhPvp:RequestLog:Enabled` in `appsettings.Local.json`, `tests/ZhPvp.Tests/Host/RequestLogTests.cs` | The whole folder, its registration, setting and tests (framework request event; `Observability:Logging:Console:Enabled: true` in the local flavor file) | Any game-specific per-request behaviour, rebuilt on `HttpContext.RequestOutcome()` |
| FG-006 | `tests/ZhPvp.Tests/Support/FakeCommandContext.cs` (dictionary aggregates, throwing `Config`/claims/ids) | The whole file (→ `CommandContextDouble`; handler tests add `CheckCommit(outcome).ShouldBeEmpty()`) | — |
| FG-003 | Root `Directory.Build.props` comment "never add a root `Directory.Build.targets`" + `FRAMEWORK-GAP FG-003` marker; CLAUDE.md rule on game build settings | The rule, the marker and the CLAUDE.md sentence (framework root has its own `Directory.Build.targets`) | Game build settings in the game's own files (now also a root `Directory.Build.targets` if wanted) |
| FG-004 | `ZhPvpRefuseTestsAncestor` target + `FRAMEWORK-GAP FG-004` marker in `Directory.Build.props`; "no `tests` folder in the clone path" in README, CLAUDE.md, `docs/COMMANDS.md`, `docs/KNOWN_LIMITATIONS.md` | The guard target, the marker and the four notes | The game's own name-based `IsTestProject` rule (`*.Tests`) |
| FG-002 | `tools/local/run-local.ps1` (251 lines) and `run-local.sh` (224 lines), adapted copies with `FRAMEWORK-GAP FG-002` headers; `docs/KNOWN_LIMITATIONS.md` row | Both copies (→ `framework/tools/local/run-local.* -HostProject src/ZhPvp.Host -Name "ZhPvp"`) and the limitation row | A thin wrapper for the submodule/tag check (exit 8) if the game keeps it |
| FG-007 | `src/ZhPvp.Host/BuildInfo.cs` (marker), `AssemblyMetadata("FrameworkTag")` in `src/ZhPvp.Host/ZhPvp.Host.csproj`, the startup log line in `Program.cs` | All three (framework startup line, `GameServerVersion`, `GameServerRuntimeInfo`, telemetry, GM read) | The game's `<Version>`; `GameServerFrameworkTag` in props for the "checked-out tag = pinned tag" test, now comparable with `GameServerVersion.Framework` |
| FG-011 | Band 50000–50999 recorded only in `CLAUDE.md` and `docs/pcr/placeholder.md` | Nothing to remove: the band lies inside the reserved game band; the notes can point to `docs/pcr/platform.md` §8 / `ErrorCodeBands` | The game's own sub-ranges per module |
| FG-012 | No marker; consumer steps written in `docs/COMMANDS.md` and `docs/framework-feedback/UPGRADE.md` | Duplicated consumer steps can point to `framework/docs/guides/consumer-guide.md` (bump checklist §11) | Game-specific steps (friction log, game-only checks) |

SC-010: after the bump to `v0.7.0` the markers of **8** of the 20 marker ids open on 2026-11-03 can be removed — FG-001,
FG-002, FG-003, FG-004, FG-006, FG-007, FG-010, FG-025 — and the `UPGRADE.md` sync lines for `tests/Shared`, the smoke test
and `tools/local` are no longer needed. Still open after 013: FG-005, FG-008, FG-009 (deferred packaging) and the ids owned
by slices 014–016.

## Docs not re-checked

Pages and guides **touched** by slice 013 carry "framework v0.7.0 (unreleased)" / "Đúng với framework v0.7.0 (chưa phát
hành)": `README.md`, `docs/guides/consumer-guide.md` (new), `docs/guides/module-authoring.md`,
`docs/guides/environments-and-local-run.md`, `docs/pcr/platform.md` (§8, §13, §17, §19), `docs/pcr/gm.md`,
`docs/pcr/unity-sdk.md`, `templates/module/README.md`, `docs/html/index.html`, `docs/html/01-gioi-thieu.html`,
`docs/html/02-them-command.html`, `docs/html/03-deploy.html` (all `175df65` stamps replaced), `docs/api/gm-openapi.json`
(regenerated).

**Not re-checked** against this release (FR-072; they keep their own stamp or none):

- `docs/guides/analytics.md`
- `docs/guides/local-real-env-testing.md`
- `docs/adr/0001-runner-store-abstraction.md`
- `docs/pcr/economy.md`, `docs/pcr/iap.md`, `docs/pcr/identity.md`, `docs/pcr/sample-counter.md`, `docs/pcr/sample-game.md`
- `specs/011-local-env/contracts/operations-cli.md` (its §4 is superseded by `specs/013-consumer-readiness/contracts/run-local-cli.md`)
- In `docs/html/03-deploy.html` the "→ 015" badges still name the old ops-readiness slice number (the page says so in its
  scope line); the slice gets a new number after 013–016.

## Manual checks

| Check | State |
| --- | --- |
| T075 consumer-guide walkthrough (SC-009) | **done** 2026-11-03 (implementer agent): fresh game repo in `%TEMP%`, framework as submodule of a throwaway clone holding the uncommitted 013 tree (tag `v0.7.0` in that clone only), one module (`walk.step`, codes 51000–51099), handler test with `CommandContextDouble`, `LocalFlavorFactory<Program>` smoke test, host via `framework/tools/local/run-local.ps1 -HostProject src/Walk.Host -Name "Walk game"` (port 5291): build 0 warnings, 3/3 tests green, ready banner + startup version line + console request line; gaps fixed in the guide (`.editorconfig` copy, `config/base/manifest.json`, `using` lines in samples); temp repo deleted |
| T076 `GAMESERVER_SCRIPT_TESTS=1` `RunLocalScriptTests` (default + fixture host) | **done** 2026-11-03: 12/12 (ps1 + Git Bash on Windows: sample default, fixture host, fixture exit 6, two-project host folder) in 2 min 34 s |
| T076 `GAMESERVER_PERF=1` `RequestLogPerformanceTests` (SC-008) | **done** 2026-11-03: p95 off 0.433 ms / on 0.506 ms (delta 0.073 ms ≤ 1 ms); stalled console + saturated file queue 2 000/2 000 OK in 0.21 s, drops counted per sink |
| T076 `tools/local/check-build-seal.ps1` (SC-004) | **done** 2026-11-03: exit 0, 0 errors, 0 warnings, marker absent |
| `run-local.sh` on macOS/Linux | open (no machine; spec assumption; includes the bash < 4.4 warning) |
| Full suite with Couchbase (T080) | **done** 2026-11-03: 1 482 total, 1 464 passed, 17 skipped, 1 known flaky (`TelemetryTests.A_command_is_one_trace…`, green on re-run); in memory and File 1 482 / 1 387 / 95 skipped, 0 failed |
