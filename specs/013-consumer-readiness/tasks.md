# Tasks: Consumer readiness — Dev-profile lockdown, consumer test kit, request log, sealed build, reusable local run, version stamping and consumer docs (slice 013 consumer-readiness)

**Input**: [plan.md](plan.md) (authoritative design, decisions D1–D18, deviations SD1–SD12), [spec.md](spec.md)
(FR-001..FR-091, US1–US7, BC-1..BC-7, SC-001..SC-010; FR-086 resolved 2026-11-03), [research.md](research.md) (R1–R18),
[data-model.md](data-model.md), [contracts/public-api.md](contracts/public-api.md),
[contracts/request-log-and-console.md](contracts/request-log-and-console.md),
[contracts/gm-ops-version.md](contracts/gm-ops-version.md), [contracts/run-local-cli.md](contracts/run-local-cli.md)

**Tests**: mandatory (constitution VI). Each story phase writes its tests before or alongside the code they cover and
ends green. Couchbase tests skip with a message when `COUCHBASE_CONNSTR` is not set; the full Couchbase suite (~25 min)
runs in the final gate (T080). Opt-in suites: `GAMESERVER_SCRIPT_TESTS=1` (run-local start/stop), `GAMESERVER_PERF=1`
(request-log latency). Scenario ids (`US1-3`) refer to the spec's acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USn] description with paths`, then `Done when:` (verifiable) and the FR/SC ids covered.
`[P]` = different files and no open dependency. Record findings as `Note:` lines under the task.

**MVP = US1 (Phases 1–3).** FG-010 is a HIGH security fix: Phase 3 depends only on T001 and T007 and is shippable on its
own (for example as a `v0.6.1` hotfix cherry-picked from this branch) before any other story lands.

**[VERIFY]**: a tooling or runtime fact from research to confirm on the pinned versions (.NET SDK 10.0.403,
Serilog.AspNetCore 10.0.0, Microsoft.AspNetCore.Mvc.Testing 10.0.14) **before** the task that depends on it. Each check
runs in a throw-away project under the scratch/temp folder (never committed). Record the result as a `Note:`; if the fact
is wrong, apply the stated fallback and record the deviation (keys, messages, docs).

| Item | Fact | Fallback | Check | Used by |
| --- | --- | --- | --- | --- |
| V1 | Source Link fills `SourceRevisionId` (→ `InformationalVersion` `0.7.0+{sha}`) in a normal clone **and** in a submodule checkout (`.git` file) | target in `Directory.Build.targets` running `git rev-parse HEAD` (`ContinueOnError`) when `SourceRevisionId` is empty | T003 | T061, T065 |
| V2 | `dotnet msbuild -getProperty:A -getProperty:B -getItem:C` prints JSON when several are requested (single value: plain text) on SDK 10.0.403 | request one property per call and parse plain text | T002 | T051 |
| V3 | `MakeRelative`, `Replace`, `ToLowerInvariant`, `StartsWith` work in a property definition of `Directory.Build.props` | `$(MSBuildProjectDirectory.StartsWith(...))` with normalized separators, case-insensitive compare | T002 | T050 |
| V4 | `Microsoft.AspNetCore.Mvc.Testing` build targets (content-root attribute) flow to a test project through a library's `PackageReference` | the consumer test project references `Microsoft.AspNetCore.Mvc.Testing` itself (fixture + consumer guide say so) | T004 | T009, T026, T033, T070 |
| V5 | Serilog's MEL bridge turns every state key/value of an `IReadOnlyList<KeyValuePair<string, object?>>` state into an event property and keeps `{OriginalFormat}` as the template | put every field in the message template | T005 | T038, T045 |
| V6 | A blocked console writer inside `WriteTo.Async(blockWhenFull: false)` blocks only the async worker; `TryAdd` drops are counted by `LogSinkMonitor` | own bounded `Channel` in front of the console sink | T005 | T043, T044 |
| V7 | `WebApplicationFactory<T>.DisposeAsync` is overridable on 10.0.14 | `Dispose(bool)` override + an `IAsyncDisposable` wrapper type | T004 | T026 |
| V8 | the `Serilog.Sinks.Console` version resolved transitively by Serilog.AspNetCore 10.0.0 (the explicit `PackageVersion` must equal it: no NU1605 downgrade, no silent upgrade) | pin the transitive version found | T006 | T043 |

V1–V7 are the `[VERIFY]` items of research.md; V8 comes from the plan's Technical Context ("explicit `PackageVersion`
for `Serilog.Sinks.Console`, already a dependency of Serilog.AspNetCore 10.0.0").

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
`test --solution GameServer.slnx -c Release --no-build` (one class: `--project X --filter-class "*Name"`). Couchbase
variables as in `AgentMemories/features/p05-speckit/00_summary.md` ("Environment"; password from the user variable
`COUCHBASE_PW`, never printed). Never grep recursively over `samples/` (Unity `Library/`); targeted paths are fine.
`.specify/feature.json` already points to `specs/013-consumer-readiness`. Repository docs are written in Vietnamese,
except the new consumer guide (English, plan D18); keys, field names, commands and code stay literal.

## Phase 1: Setup

- [X] T001 Baseline on branch `013-consumer-readiness` (base `fea3d29`): Debug and Release build of `GameServer.slnx`;
  full suite in memory (no `COUCHBASE_*`) and with `GAMESERVER_TEST_STORE=File`.
  Done when: 0 warnings in both configurations, 0 failures; total/passed/skipped counts recorded as a Note (reference
  for T019, T035, T077).
  Note: Baseline 2026-11-03 (HEAD `cd06f31` = base `fea3d29` + slice docs): Debug and Release build 0 warnings/0 errors;
  full suite in memory 1190 total, 1105 passed, 85 skipped, 0 failed (35 s); with `GAMESERVER_TEST_STORE=File` the same
  1190/1105/85/0 (35 s). No `COUCHBASE_CONNSTR` set.
- [X] T002 [P] **[VERIFY] V2 and V3** in a throw-away layout under `%TEMP%`: one `Directory.Build.props` defining
  `_GameServerRelativeDir` with `MakeRelative(...).Replace('\','/').ToLowerInvariant()` and `IsTestProject` from
  `StartsWith('tests/')`, two empty projects (`src/A`, `tests/B`) under a parent folder named `tests`; run
  `dotnet msbuild <proj> -getProperty:IsTestProject -getProperty:_GameServerRelativeDir -getItem:AdditionalFiles` and
  the same with a single `-getProperty`.
  Done when: a Note records the exact output shape (JSON vs plain text) and that `IsTestProject` is true only for
  `tests/B`; fallbacks chosen for T050/T051 if needed.
  Note: V2 confirmed on SDK 10.0.403: two or more `-getProperty`/`-getItem` switches print one JSON object
  `{"Properties": {...}, "Items": {"AdditionalFiles": [{"Identity": ..., "FullPath": ..., ...}]}}`; a single
  `-getProperty` prints the bare value (an empty line when the property is empty). Without `-t:` the switches only
  evaluate (`dotnet build -getProperty` also skips the build); with `-t:Build` they report values after the targets ran.
  JSON escapes `+` as `\u002B` (parse it, never string-match). V3 confirmed: `MakeRelative(...).Replace('\','/')
  .ToLowerInvariant()` gives `src/a` and `tests/b` (no trailing slash) in a layout `...\tests\fw\{src\A,tests\B}`;
  `IsTestProject` from `StartsWith('tests/')` is `true` for `tests/B` only (empty for `src/A`, although its path contains
  `\tests\`). No fallback needed for T050/T051.
- [X] T003 [P] **[VERIFY] V1**: build one framework project (`src/GameServer.Core`) with a temporary
  `<Version>0.7.0</Version>` (command-line `-p:Version=0.7.0`, nothing committed) (a) in this clone and (b) in a
  temp game repo that adds this repository as a submodule from the local path (`git -c protocol.file.allow=always
  submodule add`); read `InformationalVersion` with `-getProperty` and from the built assembly.
  Done when: a Note records both values; if (b) has no `+{sha}`, T061 adds the `git rev-parse` fallback target. The temp
  repo is deleted.
  Note: V1 confirmed. (a) this clone, `dotnet msbuild src/GameServer.Core/GameServer.Core.csproj -t:Build
  -p:Version=0.7.0` (intermediate/output redirected to the scratch folder): `SourceRevisionId` = HEAD sha,
  `InformationalVersion` = `0.7.0+cd06f31dbe2b2108d9312f38e67350fb9eb4d0e6`, built DLL ProductVersion the same,
  FileVersion `0.7.0.0`. (b) temp game repo with this repository as submodule `framework/` (`.git` file
  `gitdir: ../.git/modules/framework`): identical values. Source Link fills the sha in both, so T061 needs **no**
  `git rev-parse` fallback target. Values appear only after the build targets ran (evaluation-only `-getProperty`
  returns an empty `InformationalVersion`). Temp repo deleted; nothing written to this clone.
- [X] T004 [P] **[VERIFY] V4 and V7** in a throw-away solution: a class library referencing
  `Microsoft.AspNetCore.Mvc.Testing` (10.0.14) with a `WebApplicationFactory<T>` subclass overriding `DisposeAsync`, a
  minimal web app, and an xUnit test project that references only the library; run one test that creates a client.
  Also log `IHostEnvironment.ApplicationName` inside the factory (expected: the entry point's assembly name, used by T060).
  Done when: a Note records whether the content root is found without a direct `Mvc.Testing` reference in the test
  project (V4), whether `DisposeAsync` is overridable (V7) and the `ApplicationName` value; the fallbacks for T009/T026
  are chosen.
  Note: V4 confirmed: the package ships `buildTransitive/net10.0/Microsoft.AspNetCore.Mvc.Testing.targets`, imported
  into the test project's `nuget.g.targets` through the library's `PackageReference`; the test output gets
  `MvcTestingAppManifest.json` (entries for the app and the library) and `App.deps.json`, and the factory found the app's
  content root (folder of `App/appsettings.json`) with no `Mvc.Testing` reference in the test project. T009/T026/T033 need
  no direct reference (the consumer guide may still recommend it as optional). V7 confirmed: `public override async
  ValueTask DisposeAsync()` compiles with warnings as errors and runs. `IHostEnvironment.ApplicationName` = `App` (the
  entry point's assembly name) under `WebApplicationFactory<Program>` (T060).
- [X] T005 [P] **[VERIFY] V5 and V6** in a throw-away console app with `Serilog.AspNetCore` 10.0.0: (V5) log a custom
  `IReadOnlyList<KeyValuePair<string, object?>>` state with `{OriginalFormat}` through `ILogger` → compact JSON file;
  check every key is a property and `@mt` is the template; (V6) `WriteTo.Async(console with a blocking TextWriter,
  bufferSize 10, blockWhenFull: false, monitor)` + 2 000 events: the logging calls return immediately and the monitor
  counts drops.
  Done when: a Note records both results and the fallback chosen for T038/T043 if needed.
  Note: V5 confirmed (Serilog 4.3.0 / Serilog.Extensions.Logging 10.0.0 through `AddSerilog(..., preserveStaticLogger:
  true, writeToProviders: false)`): a custom `IReadOnlyList<KeyValuePair<string, object?>>` state with `{OriginalFormat}`
  gives `@mt` = the template and every key as a property, also keys not in the template (`ActorId`, `NotModified`); a
  null value is written as `"ActorId":null`; `EventId` renders `{"Id":1,"Name":"Request"}`, `SourceContext` = the
  category, enrichers kept. No fallback for T038. V6 confirmed (Serilog.Sinks.Console 6.1.1 under `Console.SetOut` with a
  writer that blocks forever): `WriteTo.Async(console, bufferSize: 10, blockWhenFull: false, monitor)` + 2 000 events
  -> the calls returned in 3.8 ms, the writer was entered once (worker blocked), the inspector reported 1 989 drops and
  a queue of 10. No own `Channel` needed for T043. The console sink resolves `Console.Out` per event, so `Console.SetOut`
  would work as a seam, but it is process-wide and shared by parallel tests: T043/T044 keep the plan's internal
  `TextWriter` seam. Consequence for T043: `Serilog.Sinks.Console` 6.1.1 has only two `Console(...)` overloads
  (output template, or `ITextFormatter`) and **no `TextWriter` parameter**, so the seam is realised by writing to
  Serilog core's `WriteTo.TextWriter(formatter, writer, restrictedToMinimumLevel)` inside the same `WriteTo.Async(...)`
  when the internal writer is set, and `sink.Console(formatter, ...)` otherwise.
- [X] T006 [P] **[VERIFY] V8**: `dotnet list src/GameServer.Platform.Observability package --include-transitive` → the
  resolved `Serilog.Sinks.Console` version.
  Done when: a Note records the version that T043 pins in `Directory.Packages.props`.
  Note: V8: `Serilog.Sinks.Console` resolves to **6.1.1** (transitive through Serilog.AspNetCore 10.0.0; also Serilog
  4.3.0, Serilog.Extensions.Hosting/Logging/Settings.Configuration 10.0.0, Serilog.Sinks.Debug 3.0.0). T043 pins
  `<PackageVersion Include="Serilog.Sinks.Console" Version="6.1.1" />`.

**Checkpoint**: baseline counts known; V1–V8 decided with their fallbacks.

## Phase 2: Foundational (behaviour-neutral)

US1 depends only on T007. T008 and T009 are needed by US2 (and T009 by every later use of the kit); none of them changes
behaviour. Version stamping and build sealing are **not** here (they are breaking changes BC-5/BC-6 and belong to US4/US6),
so US1 can ship without them.

- [X] T007 `GameServerOptions.AllowsDevProfile` in `src/GameServer.Platform/GameServerOptions.cs` (research R1, D1):
  `public bool AllowsDevProfile => AllowsDevelopmentTools;` with the doc comment of contracts/public-api.md §2 (three
  layers, PCR platform §17 R13, never Production). No setting (FR-006).
  Tests in a new file `tests/GameServer.Platform.Tests/DevProfileDecisionTests.cs`: true for Development and Test, false
  for Staging, Production and an arbitrary value set directly on the options (`"Qa"`); always equal to
  `AllowsDevelopmentTools`.
  Done when: green; nothing else reads the new property yet. (FR-006)
- [X] T008 [P] Extract the commit rules into a new file `src/GameServer.Platform/Commands/CommitRules.cs` (research R9,
  contracts/public-api.md §2): `internal static class CommitRules` with `CheckCode(registration, registry, code)` and
  `CheckAudit(descriptor, outbox)` returning `string?`; `CommandRunner.EnsureCommittableCode`/`EnsureAudit`
  (`src/GameServer.Platform/Commands/CommandRunner.cs:352-405`) throw `InvalidOperationException(message)` when non-null,
  with **byte-identical** messages.
  Tests in a new file `tests/GameServer.Platform.Tests/Commands/CommitRulesTests.cs` (IVT to `GameServer.Platform.Tests`
  exists): uncatalogued code, catalogued name with a different instance, non-200 code, GM command with 0 or 2 audit
  entries, non-GM command with one → the runner's message; committable → null.
  Done when: new tests green; existing `tests/GameServer.Platform.Web.Tests/UncataloguedCommitCodeTests.cs`, GM audit
  tests and `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs` green unchanged. (FR-025, FR-026)
  Note: `DescribeOwner` moved into `CommitRules` with the code rule (it needs the registry only). `CommitRulesTests`
  compare each message with the exception the runner throws for the same outcome (`RunnerHarness`), so equality with the
  runner is checked, not only the literal text.
- [X] T009 [P] Kit project skeleton (after T004; research R7, plan US2 "Project"): new
  `src/GameServer.TestKit/GameServer.TestKit.csproj` (FrameworkReference `Microsoft.AspNetCore.App`, PackageReference
  `Microsoft.AspNetCore.Mvc.Testing`, ProjectReferences Platform.Web, Platform.Testing, Modules.Identity.Adapters,
  Modules.Gm.Adapters; no xUnit/Shouldly/sample/test reference); add it to `GameServer.slnx` under `/src/`;
  `<InternalsVisibleTo Include="GameServer.TestKit" />` in `src/GameServer.Platform/GameServer.Platform.csproj`.
  Done when: Release build 0 warnings; `dotnet msbuild src/GameServer.TestKit/GameServer.TestKit.csproj
  -getProperty:IsTestProject -getProperty:IsFrameworkCode` → not a test project, framework code (analyzers and banned
  APIs on). (FR-010)
  Note: `IsTestProject` empty, `IsFrameworkCode` true, `AnalysisLevel` latest-recommended, `AdditionalFiles` =
  `BannedSymbols.txt`. No direct `Mvc.Testing` reference needed downstream (V4).

**Checkpoint**: Release build 0 warnings; full suite green and unchanged; no behaviour change.

## Phase 3: US1 — Cheat endpoints never run outside Development and Test (P1, FG-010) MVP

Goal: three independent layers refuse the Dev profile whenever `!AllowsDevProfile`; Development/Test unchanged.
Independent test: T016 (flavor matrix) + T015 (middleware with the validator bypassed) + T013 (runner called directly).
Depends on: T001, T007 only. T010, T011 and T014 must land in the same change (they change the startup report that
`ConfigAndCheatTests` and `StartupValidationTests` assert).

- [X] T010 [US1] Layer 1a — endpoint rule in `src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs:86`
  (research R2, D2): `options.IsProduction` → `!options.AllowsDevProfile`; message exactly
  `[endpoint {methods} {route}] uses profile Dev in deployment environment '{DeploymentEnvironment}' (environment id
  '{EnvironmentId}'). Fix: register Dev (cheat) modules only when development tools are allowed
  (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09, SEC-08).`
  Doc comments: `src/GameServer.Platform.Web/Routing/ApiModules.cs:77` (`AsDevEndpoint`) and
  `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs:75` (`MapDevCommand`) → "allowed only where development
  tools are allowed (Development/Test)".
  Tests first, in a new file `tests/GameServer.Platform.Web.Tests/DevProfileValidationTests.cs` (endpoint part):
  `EndpointSecurityValidator.Validate` on a fake endpoint list with one Dev endpoint × Development, Test (no line),
  Staging, Production, `"Qa"` set directly on the options (one line each, exact text). Update
  `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs:83` to the new text (BC-3).
  Done when: both files green; the message names method, route, deployment environment, environment id and the fix.
  (FR-001, FR-006, FR-007, US1-1)
  Note: also updated the class summary of `EndpointSecurityValidator` and the `CheatModule` summary
  (`src/GameServer.Modules.Economy.Adapters/EconomyModule.cs`), which said "in Production".
- [X] T011 [US1] Layer 1b — Dev command rule in `src/GameServer.Platform/Registry/ModuleRegistry.cs` (research R2, D2):
  `ValidateCommands(options, errors)` receives the options (call sites `:108-124`, `:271-280`) and reports every
  registered command with `Profile == Dev && !options.AllowsDevProfile`, for every module including service-only ones:
  `[module {name}] command '{operation}' has profile Dev in deployment environment '{…}' (environment id '{…}'). Fix: …
  (OPS-09).` `--provision` uses the same `ModuleRegistry.Validate(options, null)`
  (`src/GameServer.Platform.Web/Hosting/ProvisioningRunner.cs:25`), so it fails the same way.
  Tests in `tests/GameServer.Platform.Web.Tests/DevProfileValidationTests.cs` (registry part, same file as T010 → after
  it): a module with one Dev command added normally, with `ServiceOnly()`, with `Hosting:MapPlayerApi = false`, and a
  Dev command without route → one line per command in Staging/Production/`"Qa"`, none in Development/Test. Provision path
  in `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs`: Production, `CheatModule` registered, fake
  `IStoreProvisioner` → exit 1, the report names `cheat.grant`, the provisioner is never called.
  Done when: green. (FR-002, FR-006, FR-007, US1-2, edge case `--provision`)
  Note: `Hosting:MapPlayerApi=false` does not change the registry rule (the registry never looks at it); the
  registry-level test sets it on the options to document that, the host-level variant is in T016. Fix text of the command
  line = the endpoint fix text with `(OPS-09)`.
- [X] T012 [US1] Layer 2 — pipeline in `src/GameServer.Platform.Web/Middleware/ProfileEnforcementMiddleware.cs:69-77`
  (research R3, D3): directly after the profile lookup (and the existing 405/no-profile branches), before the GM same-site
  check, `Cache-Control`, `SetBodyLimit`, CSRF, permission checks and any `AuthenticateAsync`: Dev && `!AllowsDevProfile`
  → Warning `Dev endpoint {Method} {Route} refused in deployment environment {DeploymentEnvironment} ({EnvironmentId}).`
  (route = `RoutePattern.RawText`, no header or token value) and `ErrorResponseWriter.WriteAsync(context,
  CoreErrorCodes.Forbidden)`. The authentication switch becomes explicit: `Public` → none, `Player` → player schemes,
  `Gm` → GM schemes, `Dev` → player then GM schemes (only reachable when allowed), any other value → 403 `FORBIDDEN`;
  no default authentication branch.
  Tests first, in a new file `tests/GameServer.Platform.Web.Tests/DevProfileMiddlewareTests.cs` (unit part):
  `DefaultHttpContext` with a Dev endpoint, Staging options and a recording `IAuthenticationService` → 403 error
  contract, 0 `AuthenticateAsync` calls, `next` not called, one Warning without any header value; Development → `next`
  called after player scheme then GM scheme; an endpoint whose `SecurityProfileMetadata` carries an undefined enum value
  (`(SecurityProfile)99`) → 403 and 0 authentication calls.
  Done when: green; existing profile/CSRF/permission tests green. (FR-003, FR-004, FR-006, FR-007, US1-3, US1-6)
  Note: the Dev refusal sits right after the no-profile branch, i.e. before the GM same-site check, `Cache-Control`,
  `SetBodyLimit`, authentication, CSRF and permissions. The unknown-profile refusal is the `default` arm of the explicit
  authentication `switch` (after `SetBodyLimit`, before any `AuthenticateAsync`). Route in the Warning =
  `RoutePattern.RawText` (endpoint display name if the endpoint is not a route endpoint).
- [X] T013 [US1] Layer 3 — runner in `src/GameServer.Platform/Commands/CommandRunner.cs` (after T008, same file; research
  R4, D4): `EnsureActorAllowed` (`:148-161`) becomes an instance method, `SecurityProfile.Dev => options.AllowsDevProfile`
  for player and GM actors; refusal = `GameServerException(CoreErrorCodes.Forbidden, "Dev command '{op}' refused in
  deployment environment '{…}' (environment id '{…}').")`; `TryReplayAsync` (`:124`) records
  `metrics.CommandCompleted(operation, "FORBIDDEN", replayed: false)` on a pre-check refusal like `ExecuteAsync`
  (`:45-53`). Order unchanged: actor check → limiter → receipt read → guards → transaction. Doc comment `:146` → "Dev
  commands only where development tools are allowed".
  Tests in a new file `tests/GameServer.Platform.Web.Tests/DevProfileRunnerTests.cs` on a Development `TestHost` with
  `CheatModule`, then the test sets the singleton `GameServerOptions.DeploymentEnvironment = "Staging"` (validator
  already passed = bypassed, plan SD7): `ICommandRunner.ExecuteAsync` and `TryReplayAsync` with `cheat.grant` for a
  player, a GM and a system actor → `FORBIDDEN`; store document count unchanged; `command_results_total{code=FORBIDDEN}`
  incremented on both paths (meter listener filtered to the host's meter); a receipt committed by `cheat.grant` before
  the flip is not replayed after it.
  Done when: green; `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs` green. (FR-005, FR-006, FR-007,
  US1-4, SC-001 runner part, edge case "Dev receipt replayed")
  Note: in Development/Test `Dev => options.AllowsDevProfile` keeps today's behaviour exactly (any actor type, system
  actors included; `v0.6.0` had `Dev => true`); no actor restriction added. `TryReplayAsync` records the metric for every
  `GameServerException` of its pre-checks (unknown operation, no target player, actor refusal), as `ExecuteAsync` does;
  callers never run `ExecuteAsync` after a throwing `TryReplayAsync`, so nothing is counted twice. The metric assertion
  filters `command_results_total` by `operation=cheat.grant, code=FORBIDDEN, replayed=false` (no public handle on the
  host's `Meter` instance; only this class makes the runner refuse that command).
- [X] T014 [US1] Framework tests broken by BC-1/BC-3 (research R6, D6; lands with T010/T011):
  - `tests/Shared/GameHttp.cs`: `FullGame` adds `CheatModule` only when `builder.Options.AllowsDevelopmentTools` (rule of
    `samples/SampleHost/Program.cs:62-66`);
  - `tests/SampleGame.Tests/ConfigAndCheatTests.cs:77-86`: compose `CheatModule` explicitly, assert the new endpoint
    **and** command lines, for Staging and for Production;
  - re-check `tests/SampleGame.Tests/IapStartupValidationTests.cs:32` (prefix filter), `tests/GameServer.Gm.Tests/GmAuthTests.cs:160`
    and `SsoTests.cs:146` (Development) — no change expected;
  - search every `tests/**/*.cs` (not `samples/`) for `CheatModule`, `AsDevEndpoint`, `MapDevCommand`, `"profile Dev"`
    and `"in Production"` and fix any other composition that starts Staging/Production with a Dev module.
  Done when: full suite green in memory; a Note lists every file changed. (FR-001, FR-002, BC-1, BC-3)
  Note: changed `tests/Shared/GameHttp.cs` (`FullGame` adds `CheatModule` only when `AllowsDevelopmentTools`),
  `tests/SampleGame.Tests/ConfigAndCheatTests.cs` (Staging + Production theory composing `CheatModule` explicitly, exact
  endpoint and command lines; new theory: `FullGame` in Staging/Production has no Dev line),
  `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs` (new text). Re-checked unchanged:
  `IapStartupValidationTests` (prefix filter), `GmAuthTests`, `SsoTests`, `GmTestHost`, `ObservedHost`, `AccountPurgeTests`,
  `AnalyticsSeamTests`, Client tests, `TelemetryTests`, `FileStoreProcessKillTests` (all Development/Test) and Couchbase
  `CouchbaseGameHost` (deployment environment from `TestHost.DefaultSettings()` = Test). Full suite in memory 1245 total,
  1160 passed, 85 skipped, 0 failed at that point.
- [X] T015 [US1] Middleware host tests, validator bypassed, in `tests/GameServer.Platform.Web.Tests/DevProfileMiddlewareTests.cs`
  (host part, after T012, T014; research R5): Development `TestHost` with `CheatModule` + Identity + Economy + GM, flip
  the singleton deployment environment to `Staging` after startup, then `POST /api/v1/cheat/grant` with (a) a real guest
  bearer token (guest login before the flip), (b) test-authentication headers, (c) a GM test header and (d) a GM session
  cookie → 403 `FORBIDDEN` error contract each; wallet unchanged, no receipt, session document unchanged (no touch),
  `host.Analytics` has no `login`/resume record from these requests, one Warning per request naming route and
  `Staging` and containing no token, cookie or header value.
  Done when: green; 4/4 cases with 0 state changes. (FR-003, FR-007, US1-3, SC-001 middleware part)
  Note: state check = every document of every registered collection + `sessions` serialized before and after (wallet,
  receipts, ledger, player and GM sessions); analytics `login` records counted before/after a flush; Warnings captured by
  an extra `ILoggerProvider` (category, level) and scanned for the bearer token, GM cookie values and CSRF token.
- [X] T016 [US1] Flavor matrix in a new file `tests/GameServer.Platform.Web.Tests/DevProfileFlavorMatrixTests.cs` (after
  T010, T011, T014; research R5): `TestHost` with `CheatModule` × flavors local and dev (`Development`) and test
  (`Test`) → start and grant works for a player and for a GM actor with ledger source `cheat` (US1-5); beta, uat
  (`Staging`) and live (`Production`) with the matching environment ids → startup fails and the report contains the Dev
  endpoint line and the Dev command line (other in-memory-store/test-auth violations ignored by prefix filter); same for
  the service-only and `MapPlayerApi=false` variants (command line only).
  Done when: green; the matrix covers 5 flavors × {with cheat} + 2 variants, 100% as expected. (FR-001, FR-002, FR-005
  Development part, FR-007, US1-1, US1-2, US1-5, SC-001)
  Note: 15 cases: local/dev/test start and grant; beta/uat/live fail with exactly the two Dev lines; service-only and
  `MapPlayerApi=false` fail on the command line alone in beta/uat/live and start in local/dev/test. A GM actor passes the
  pipeline on the cheat route but gets 400 `BAD_REQUEST` (a GM has no target player on a player-style route — unchanged
  since `v0.6.0`), so the GM grant with ledger source `cheat` goes through `ICommandRunner` with a target player, as GM
  flows do.
- [X] T017 [US1] Docs of the Dev rule (FR-008; Vietnamese; stamps stay until T072): `docs/pcr/platform.md` (lines ~200
  and ~661: security profiles, flavors, runner rules → "Dev chỉ khi development tools được phép (local, dev, test) —
  chặt hơn OPS-09"; a "Breaking" line BC-1..BC-3 in §17); `docs/guides/module-authoring.md:99-103`;
  `docs/guides/environments-and-local-run.md` (flavor table, cheat note); HTML `docs/html/01-gioi-thieu.html` (~617,
  619, 658), `docs/html/02-them-command.html` (~563, 962, 978, 979), `docs/html/03-deploy.html` (~539) — every
  "Dev — forbidden in Production" statement.
  Done when: a search of `docs/` for `Dev` within 60 characters of `Production` shows no statement that Dev is allowed in
  Staging; `DocumentedConfigurationKeysTests` green. (FR-008, BC-1..BC-3)
  Note: `docs/pcr/platform.md` §12 (runner rule + new "Profile Dev" rule), §17 (startup rule row, Staging note, new
  "Breaking (slice 013, FG-010)" table BC-1..BC-3, deferred-switch row → "extends `AllowsDevProfile` only");
  `docs/guides/module-authoring.md:99`; `docs/guides/environments-and-local-run.md` (dev-tools note, startup checks,
  beta/uat note); HTML `01-gioi-thieu.html` (profile table, deny-by-default, startup checks, startup-error examples),
  `02-them-command.html` (`MapDevCommand` row, route ↔ command note, new troubleshooting row), `03-deploy.html` (dev-tools
  bullet). Remaining hits of the search are the new rule or the old message quoted in BC-3. Stamps untouched (T072).
- [X] T018 [US1] Release-notes draft: new file `specs/013-consumer-readiness/release-notes-draft.md` (input of the
  `v0.7.0` tag message, FR-090): sections "Fixed (FG ids)", "Breaking changes and migration" (one subsection per BC with
  the steps of plan "Migration notes"), "Consumer workarounds now removable" (spec "Consumer impact" table rows),
  "Docs not re-checked", "Manual checks"; fill FG-010 and BC-1, BC-2, BC-3 (old and new message text) now; later stories
  append their parts.
  Done when: the file exists with the FG-010/BC-1..BC-3 content complete and the other sections as headings.
  (FR-090)
- [X] T019 [US1] US1 checkpoint: Release build 0 warnings; full suite in memory and with `GAMESERVER_TEST_STORE=File`.
  Done when: all green; counts = T001 + new tests (recorded as a Note); US1 is shippable alone (T007 + T010–T018).
  (FR-007, FR-091 partial)
  Note: 2026-11-03: Debug and Release build 0 warnings/0 errors; full suite in memory 1261 total, 1176 passed, 85
  skipped, 0 failed; with `GAMESERVER_TEST_STORE=File` the same 1261/1176/85/0. 1261 = 1190 (T001) + 71 new: Platform.Tests
  17 (`DevProfileDecisionTests` 8, `CommitRulesTests` 9), Platform.Web.Tests 51 (`DevProfileValidationTests` 19,
  `DevProfileMiddlewareTests` 9, `DevProfileRunnerTests` 7, `DevProfileFlavorMatrixTests` 15, `GameServerCommandLineTests`
  +1), SampleGame.Tests +3 (`ConfigAndCheatTests`: 1 Production case → 2×2 theory cases). US1 touches no file of T008/T009
  except `CommandRunner.cs` (T008 and T013 share it): a hotfix cherry-pick of US1 alone needs T007 + T010–T018 and the
  `CommandRunner.cs` hunks of T013 only.

**Checkpoint (MVP)**: Dev refused at startup (endpoints and commands, incl. service-only and `--provision`), in the
pipeline before authentication, and in the runner (execute + replay) outside Development/Test; Development unchanged.

## Phase 4: US2 — A game tests its modules and host with framework test helpers (P2, FG-001, FG-006)

Goal: `src/GameServer.TestKit` (no test framework, no sample) with HTTP/identity/store/GM helpers, an Identity-only
`TestHost`, `LocalFlavorFactory<T>`, `TestGameConfig` and `CommandContextDouble`; framework tests use it through
`tests/Shared/SampleTestHost`. Independent test: T033 (consumer fixture) + T031 (conformance).
Depends on: Phase 2 (T008, T009). Code moved from `tests/Shared` into `src/` becomes **framework code**:
`AnalysisLevel latest-recommended`, `TreatWarningsAsErrors` and `BannedSymbols.txt` (no `DateTime.UtcNow`, `.Result`,
`.Wait()`) now apply to it — fix analyzer findings without changing behaviour and list them in a Note.

- [X] T020 [P] [US2] `TestGameConfig` in a new file `src/GameServer.TestKit/TestGameConfig.cs` (research R10, D10):
  `Empty` (label `test`, no files), `FromJson(files, label, clientVisible)` (version via
  `FileGameConfigLoader.ComputeVersion`), `FromDirectory(configRoot, deploymentEnvironment)` (the loader itself).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/TestGameConfigTests.cs`: `FromJson` and a host loading the
  same bytes from disk report the same `ConfigVersion`; `clientVisible` honoured; `FromDirectory` overlay
  `base` + `env/{environment}` equals the host's view of `tests/Shared/config`.
  Done when: green. (FR-021, 015 seam)
  Note: `src/GameServer.TestKit/TestGameConfig.cs` builds real `GameConfig`s (`FromJson` rejects a client-visible name that
  is not a given file); 5 tests in `TestGameConfigTests` green (kit host on disk = `FromJson`, overlay `base` + `env/Development`
  = host, sample config = `SampleTestHost` host).
- [X] T021 [US2] Move `tests/Shared/ProtobufHttp.cs`, `IdentityHttp.cs`, `TestStores.cs`, `GmBrowser.cs` into
  `src/GameServer.TestKit/` (namespace `GameServer.TestKit`; research R7 table; contracts/public-api.md §6.1):
  `TestStores.CreateFileStore` becomes `internal` (FR-014); `GmBrowser` keeps both `TestServer` and `TestHost` overloads
  (the `TestHost` overloads compile once T022 lands — move `GmBrowser.cs` in T022 if needed). New
  `tests/Shared/FileStoreTestSupport.cs` (`FileStoreTestSupport.Create(folder, gameId, clock, logger)`, public-api §7)
  replaces the direct file-store construction in `tests/GameServer.Platform.Web.Tests/FileStoreHostTests.cs:26` and
  `tests/SampleGame.Tests/FileStoreProcessKillTests.cs:97`.
  Done when: the kit builds with 0 warnings under the framework analyzers; analyzer fixes listed in a Note.
  (FR-010, FR-011, FR-014, BC-4)
  Note: moved with `git mv` (history kept), namespace `GameServer.TestKit`; `TestStores.CreateFileStore` internal;
  `FileStoreTestSupport.Create` replaces it in `FileStoreHostTests.cs:26` and `FileStoreProcessKillTests.cs:97`. Analyzer
  fixes in the kit (framework rules now apply): CA1859 `TestHost.RemoveFlavorFile` takes `ConfigurationManager` instead of
  `IConfigurationBuilder` (the only finding in the moved code); CA1816 `LocalFlavorFactory.DisposeAsync` calls
  `GC.SuppressFinalize` (T026). No suppression added.
- [X] T022 [US2] Kit `TestHost` + `CapturedLog` in `src/GameServer.TestKit/TestHost.cs` and `CapturedLog.cs` (after
  T020, T021; moved from `tests/Shared/TestHost.cs`; plan "Kit TestHost", data-model §3.5): default modules Identity
  only (`DefaultModules`), game config = `config` argument → `GameServer:ConfigPath` setting → `TestGameConfig.Empty`;
  new optional last parameter `IGameConfig? config` on `StartAsync`/`BuildApp`; structured `LogEntries` (category, level,
  event id, message, properties from `IReadOnlyList<KeyValuePair<string, object?>>` states, exception) next to `Logs`;
  file store created through kit-internal code; otherwise identical behaviour (shared store/clock for two instances,
  `GAMESERVER_TEST_STORE`, in-memory analytics sink, test authentication, `beforeUseGameServer`/`afterUseGameServer`,
  `services`, `environmentName`, `storeKind`).
  Done when: the kit builds with 0 warnings and depends on no `Sample.*`/`SampleGame` assembly. (FR-011)
  Note: log capture split into internal `LogCapture.cs` (formatted `Logs` unchanged, `LogEntries` = `CapturedLog` with the
  `IReadOnlyList<KeyValuePair<string, object?>>` state incl. `{OriginalFormat}`); the file store's own logger feeds both.
  The config decision reads the test's settings dictionary only: a host `appsettings.json` copied into the test output
  (`ConfigPath: config`, SampleHost or any game host) never switches a kit host to files. The kit references no
  `Sample.*`/`SampleGame` assembly (T032 rule).
- [X] T023 [US2] Framework-only support in `tests/Shared/` (after T022; research R7, public-api §7): new
  `SampleTestHost.cs` (namespace `GameServer.Tests.Shared`; `StartAsync`/`BuildApp`/`DefaultSettings`/`DefaultModules`
  forwarding to the kit with the old defaults: Identity + `CounterModule`, `GameServer:ConfigPath` =
  `{BaseDirectory}/config`, every other parameter passed through), new `GlobalUsings.cs`
  (`global using GameServer.TestKit;`); delete `tests/Shared/TestHost.cs`, `ProtobufHttp.cs`, `IdentityHttp.cs`,
  `TestStores.cs`, `GmBrowser.cs`; add `<ProjectReference Include="../../src/GameServer.TestKit/GameServer.TestKit.csproj" />`
  to the 7 projects that link `../Shared/*.cs`: `tests/GameServer.Client.Tests`, `GameServer.Gm.Tests`,
  `GameServer.Observability.Tests`, `GameServer.Platform.Couchbase.IntegrationTests`, `GameServer.Platform.Web.Tests`,
  `Sample.Counter.Tests`, `SampleGame.Tests` (`*.csproj`).
  Done when: `tests/Shared` holds only `GameHttp.cs`, `SampleTestHost.cs`, `FileStoreTestSupport.cs`, `GlobalUsings.cs`
  and `config/`. (FR-013, SD12)
  Note: `tests/Shared` = `GameHttp.cs`, `SampleTestHost.cs`, `FileStoreTestSupport.cs`, `GlobalUsings.cs`, `config/`. The kit
  `ProjectReference` is in the 7 projects; they keep `<Compile Include="../Shared/*.cs">`.
- [X] T024 [US2] Mechanical rename of the framework call sites (after T023; plan D7, SD12, risk "rename"):
  - Rename **only** the static calls matching `(?<![\w.])TestHost\.(StartAsync|BuildApp|DefaultSettings|DefaultModules)\b`
    to `SampleTestHost.$1` in `tests/**/*.cs` (outside `tests/Shared/SampleTestHost.cs`). Measured on `fea3d29`: 102 sites
    in 35 files (89 `StartAsync`, 5 `BuildApp`, 1 `DefaultSettings`, 7 `DefaultModules`) — plus the new US1 test files.
    **Never** touch `GmTestHost.StartAsync` (20 sites), `GmTestHost.SessionCookie`/`Password`: they contain the substring
    `TestHost.`, so a plain text replace corrupts them. `GmTestHost.cs` itself calls `TestHost.StartAsync` → renamed.
  - The type name `TestHost` (return values, fields, 73 type uses) stays: it is now the kit type.
  - Why the grep is mandatory: a missed call **still compiles** (the kit has the same static members) but runs with
    Identity only and an empty config, so it fails at runtime or, worse, tests less than before.
  Done when: (1) Debug and Release build 0 warnings; (2) the regex above matches 0 lines in `tests/**/*.cs` outside
  `tests/Shared/SampleTestHost.cs`; (3) `GmTestHost.StartAsync(` still has its 20 sites; (4) no
  `GameServer.Tests.Shared.TestHost`, `GameServer.Tests.Shared.ProtobufHttp` (etc.) fully qualified name remains;
  (5) full suite in memory and with `GAMESERVER_TEST_STORE=File` green with the same counts as T019 (no test lost).
  (FR-013)
  Note: rename by script (regex of this task, `tests/**/*.cs`, `SampleTestHost.cs` excluded): **111 occurrences in 39
  files** (97 `StartAsync`, 5 `BuildApp`, 1 `DefaultSettings`, 8 `DefaultModules`). The plan's "102 in 35" is a *line*
  count at `fea3d29` (`git grep -c` = 102 lines in 36 files, 103 occurrences: one line has two calls); + 8 sites in the
  US1 test files = 111. After: regex 0 hits, `GmTestHost.StartAsync(` 20 sites, 0 fully qualified
  `GameServer.Tests.Shared.{TestHost,ProtobufHttp,IdentityHttp,TestStores,TestStoreKind,GmBrowser,CookieJarHandler}`.
  Debug and Release 0 warnings; suite 1261/1176/85/0 in memory and with `GAMESERVER_TEST_STORE=File` (= T019). The
  later kit tests (T020, T025, T031) call the kit `TestHost` directly on purpose (11 hits, all in those new files).
- [X] T025 [US2] Kit host tests in a new file `tests/GameServer.Platform.Web.Tests/TestKitHostTests.cs` (after T024):
  kit `TestHost.StartAsync()` without arguments → only Identity routes (`/api/v1/counter/*` is 404), config label `test`
  and empty; `config:` argument wins over `GameServer:ConfigPath`; `storeKind: File` and `GAMESERVER_TEST_STORE=File` →
  file store in a temp folder deleted on dispose; `LogEntries` carries category, level, event id and properties of a
  structured log call; two hosts sharing store and clock see each other's writes.
  Done when: green. (FR-011, US2-2)
  Note: 6 tests (`TestKitHostTests` 5 + `TestKitHostStoreVariableTests` 1). The `GAMESERVER_TEST_STORE` case sets the
  process-wide variable, so it runs in its own `DisableParallelization` collection and restores the previous value.
- [X] T026 [US2] `LocalFlavorFactory<TEntryPoint>`, `LocalFlavorOptions`, `LocalFlavorSettings` in new files
  `src/GameServer.TestKit/LocalFlavorFactory.cs` and `LocalFlavorSettings.cs` (after T004 V4/V7; research R8, D8,
  data-model §3.4, public-api §6.3): one root `%TEMP%/gs-local-{guid}` with `data/`, `analytics/`, `logs/`; instance id
  `lf-{8 hex}`; `UseEnvironment(options.Environment)` (default `Local`); the 8 keys of data-model §3.4 through
  `UseSetting`, then `options.Settings` (test overrides win), then `ConfigureTestServices(options.Services)`;
  `AppliedSettings`; `DisposeAsync` disposes the host first (releases file-store and analytics locks), then deletes the
  root with `TestStores.DeleteFolder` retries; `options.Folder` → reuse, never delete.
  Done when: builds with 0 warnings; T027 and T028 cover it. (FR-012, SD6)
  Note: new `LocalFlavorOptions.cs` besides the two named files (one type per file). `LocalFlavorSettings.Keys` holds the
  **7 FR-012 keys**: `Observability:Logging:Console:Enabled` binds to nothing until `ConsoleLoggingSettings` exists, so
  T028's binding rule would fail; T043 adds it (note there). Deviation from "`TestStores.DeleteFolder` retries":
  measured under load (4 parallel `Sample.Counter.Tests` runs + fixture, File store) the root's log file was still open
  after `WebApplicationFactory.DisposeAsync` returned (with minimal hosting the entry point's `RunAsync` disposes the
  same host on its own thread, and the losing disposer returns at once), and the 5 x 50 ms retries left 1-3
  `gs-local-*/logs/*.log` behind (also once in a full-suite File run). The factory now waits for the release itself
  (`DeleteRootAsync`, async, 100 x 50 ms) and throws `IOException` naming the root if a file is still open after 5 s (a
  real leak fails the test instead of littering `%TEMP%`); same stress loop afterwards: 0 left. `TestStores` unchanged.
- [X] T027 [US2] `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` on `LocalFlavorFactory<Program>` (after T026;
  plan US2): remove the hand-written settings (6 of 7 today, no `Observability:Logging:Directory`); keep every existing
  case (including the outbox-sweeper case via `OutboxSweeper = true` and the restart case through `Folder`); new cases:
  two factories in parallel → distinct roots and instance ids; after dispose the root is gone; after a guest login,
  `/v1/time` and a command round trip, no file was created under `samples/SampleHost/` or the test output folder
  (`data/`, `analytics/`, `logs/`, `*.jsonl`, `*.log`).
  Done when: green, also when run in parallel with `SampleHostConfigurationFileTests` and `SampleHostEntryPointTests`.
  (FR-012, FR-013, US2-3, edge cases "factory settings win", "two factories in parallel")
  Note: 8 cases green (5 kept + 3 new); restart/GM/analytics cases share one `Folder` root (kept by the factories,
  deleted by the class). The "nothing outside" case searches the sample content root (bin/obj skipped),
  `samples/_database` and the test output for files named with the factory's instance id or the player id (both proven
  present inside the root first) and requires no `data`/`analytics`/`logs` folder and no `*.jsonl`/`*.log` in the test
  output. Green when run at the same time as `SampleHostConfigurationFileTests` (34) and `SampleHostEntryPointTests` (4).
- [X] T028 [US2] `tests/GameServer.Platform.Web.Tests/LocalFlavorSettingsTests.cs` (after T026): every key of
  `LocalFlavorSettings.Keys` binds to a property of `GameServerOptions`, `FileStoreOptions`, `AnalyticsOptions` or
  `ObservabilityOptions` (walk like the configuration binder, as `DocumentedConfigurationKeysTests` does); `For(...)`
  sets exactly those keys; `outboxSweeper: true` flips only the sweeper. The "named in the consumer guide" assertion is
  added by T070.
  Done when: green; adding a bogus key to the list fails the test (verified, reverted). (FR-012)
  Note: 8 tests green. Verified: appending `"GameServer:Bogus:Key"` to `Keys` fails `Every_key_binds_to_an_option_property`
  and `For_sets_exactly_the_listed_keys_under_the_root`; reverted. The walk also has permanent negative cases (5 keys).
- [X] T029 [US2] `CommandContextDouble` + `RecordedWrite` + `RecordedClaim` + internal `DetachedTransaction` in new files
  `src/GameServer.TestKit/CommandContextDouble.cs`, `RecordedWrite.cs`, `RecordedClaim.cs`, `DetachedTransaction.cs`
  (after T008, T020; research R9, D9, plan contract table, data-model §6, public-api §6.4): decorator over the internal
  `CommandContext` created lazily on the first `ICommandContext` call; registry from `Describe()` of the given modules;
  unknown operation → `ArgumentException` naming it and the registered operations; aggregate of Touches ∪ Reads not
  registered → `ArgumentException` naming it and "pass the module that registers it"; missing aggregate → descriptor
  default (rev 0), seeded → `LoadedAggregate` via the internal constructor (rev 1, `Exists`); `Seed` outside Touches ∪
  Reads or with the wrong state type throws at `Seed`; detached `ITransactionContext` (`GetManyAsync` answers seeded
  claims as present; `Insert`/`Replace`/`Remove` → `NotSupportedException`); receipt id = `WithReceiptId` or
  `ReceiptIds.Compute(actorId, playerId, operation, requestId)` with the default request id
  `00000000-0000-4000-8000-000000000001`; defaults of data-model §6 (GM commands: staff `staff_test` with the descriptor
  permission, target `p_test`); recording after successful inner calls (writes in first-`Set` order with latest state,
  outbox from the inner context incl. `AddAudit`/`AddAnalyticsEvent`, claims only when `TryClaimAsync` returned true);
  configuration methods throw after the first `ICommandContext` call; `CheckCommit(outcome)` via `CommitRules`.
  Done when: builds with 0 warnings; T030/T031 cover it. (FR-020, FR-021, FR-022, FR-023, FR-024, FR-025, FR-026)
  Note: kit builds with 0 warnings, no analyzer finding. Missing aggregates and seeds both go through the internal
  `LoadedAggregate` constructor (no `AggregateDocumentSerializer` instance needed: it would need `PlatformMetrics`);
  seeded state is stored as the canonical JSON of the state type (a copy, rev 1, `Exists`). `Writes` keep a copy
  deserialized from the inner context's new state (later mutation of the handler's object does not change the record).
  The default request id is private (not in public-api §6.4).
- [X] T030 [US2] `tests/GameServer.Platform.Web.Tests/CommandContextDoubleTests.cs` (after T029): build errors (unknown
  operation, missing aggregate module); defaults (actor, `Now`, empty config, receipt id); seeds and their errors;
  missing aggregate → default state; `Set` then `Get` → new state; records in call order (writes, outbox kinds/payloads
  incl. one audit and one analytics entry added through the Core extension methods, claims with attributes); seeded
  claim → false and nothing recorded; configuration after first use throws; `CheckCommit`: uncatalogued code, catalogued
  name with another instance, non-200 code, GM command with 0/2 audits, non-GM with one audit → the runner's messages;
  rejection and committable outcomes → empty. Touches/claim limits of FR-026 are enforced at call time by the shared
  context (FR-023), so `CheckCommit` does not repeat them: one test documents that a limit violation throws during the
  handler, before any commit check.
  Done when: green. (FR-020..FR-024, FR-026, US2-4, US2-5, US2-7, edge cases "built without descriptors",
  "Set then Get", "pre-existing claim")
  Note: 19 tests green; the probe module (`KitProbeModule.cs`, codes 24000-24099, aggregates `kit_notes`/`kit_ledger`/
  `kit_other`, claim collection `kit_claims`) is shared with T031.
- [X] T031 [US2] Conformance in a new file `tests/GameServer.Platform.Web.Tests/CommandContextConformanceTests.cs`
  (after T029, T024; research R9): a test module whose handler performs the violation chosen by the request — write
  outside Touches, read outside Touches ∪ Reads, wrong state type, claim over the limit, claim in `receipts`, claim in
  `pa_x`, duplicate claim in one run, pre-existing claim, `Set`→`Get` round trip — run through `ICommandRunner` on a kit
  `TestHost` and through the double; compare exception type and the innermost `InvalidOperationException` message (the
  runner wraps handler failures in `GameServerException(INTERNAL)`), return values; `NewId`/`NewSeed` for 5 purposes × 5
  request ids identical between runner and double (and stable across two doubles with the same receipt id).
  Done when: green; ≥ 9 rule cases and 25 id/seed samples, 100% identical. (FR-023, FR-024, FR-025, US2-5, US2-6, SC-003)
  Note: 10 tests green: 9 rule cases (each compares `Observed(threw, innermost InvalidOperationException type, message |
  return value)` and asserts the 6 violation cases really throw) + 25 id/seed samples (5 request ids x 5 purposes, one
  run each; a second double built only from `WithReceiptId` gives the same values). Pre-existing claim through the
  runner: a committed `kit.save` claims the key first.
- [X] T032 [US2] Architecture rules in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` and
  `tests/GameServer.Architecture.Tests/GameServer.Architecture.Tests.csproj` (after T029; plan "Module breakdown"):
  reference the kit project; add it to `AllFrameworkAndModules` with explicit exceptions for `ITransactionContext`
  (`DetachedTransaction`, line ~110 rule) and `FileDocumentStore` (kit-internal file store, line ~157 rule); new rules:
  the kit references no `xunit*`, `Shouldly`, `Sample.*`, `SampleGame*` or test assembly; no `src`/`samples`/`modules`/
  `templates`/`tools` project (SampleHost included) references the kit; the kit's use of Platform internals is limited
  to `CommandContext`, the `LoadedAggregate` constructor and `CommitRules` (review rule, named in the test).
  Done when: green; a temporary kit reference from `samples/SampleHost` fails the rule (verified, reverted).
  (FR-010, FR-014)
  Note: rules in a new file `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` (3 tests) to keep
  `ArchitectureTests.cs` at its size; the kit is in `AllFrameworkAndModules` with the named exceptions `DetachedTransaction`
  (ITransactionContext rule) and `TestHost`/`TestStores` (file-store rule), exactly the 3 types the rules reported. Kit
  dependencies: referenced assemblies + NetArchTest namespaces + the kit csproj (no xunit/Shouldly/Test.Sdk package, no
  tests/samples/modules project). No non-test solution project (read from `GameServer.slnx`, no file-system walk over
  `samples/`) references the kit. Internals: Mono.Cecil (via NetArchTest) lists every non-public Platform type/member the
  kit's IL refers to; allowed = `CommandContext`, `CommitRules`, `LoadedAggregate::.ctor` (each must be seen).
  Verified: a temporary kit `ProjectReference` in `samples/SampleHost/SampleHost.csproj` fails
  `No_project_outside_tests_references_the_kit` naming it; reverted (git clean).
- [X] T033 [US2] Consumer fixture `tests/ConsumerFixture/` (after T026, T029; research R17, D17): own
  `Directory.Build.props` (consumer style: name-based `IsTestProject` for `*.Tests`, analyzers, `TreatWarningsAsErrors`;
  defines `GameServerRoot` = the framework root) and `Directory.Packages.props` importing the framework's;
  `src/Fixture.Game` (module `fixture`, command `fixture.save`, aggregate `notes`, codes 50000–50099);
  `src/Fixture.Host` (Program copied from SampleHost, `appsettings.json` + `appsettings.Local.json`,
  `Properties/launchSettings.json` profile `local` on port 5180, `<Version>1.2.3</Version>`, `config/base`);
  `tests/Fixture.Tests` (xUnit v3; references only `GameServer.TestKit` + the fixture projects, plus
  `Microsoft.AspNetCore.Mvc.Testing` if V4 requires it): a handler unit test with `CommandContextDouble` and
  `CheckCommit`, and a `LocalFlavorFactory<Program>` smoke test (ready, `/v1/time`, `fixture.save` round trip, 0 files
  outside its temp root). Add the three projects to `GameServer.slnx` under `/tests/ConsumerFixture/`; `.gitignore`
  anchored `/tests/ConsumerFixture/.local/`.
  Done when: Release build 0 warnings; `Fixture.Tests` green; no `tests/Shared` link and no sample reference in the
  fixture (checked by a test in `Fixture.Tests` reading its own csproj, or by T032); `git status` shows no stray
  fixture data. (FR-010, FR-011, FR-013, US2-1, SC-002)
  Note: `Fixture.Game` = business + adapter in one project (wire = `Google.Protobuf.WellKnownTypes.StringValue`, no proto
  codegen; aggregate `notes` server-only; config `notes.json` rule `maxLength`, `RequiredConfigFiles`); `Fixture.Host`
  Program = SampleHost's minus the sample modules (Identity + fixture; Couchbase/File/InMemory switch kept); the Local
  flavor writes to `tests/ConsumerFixture/.local/{data,analytics,logs}` (gitignored). `Fixture.Tests`: 6 tests green
  (handler + `CheckCommit` 4, factory smoke 1, own-csproj check 1); no direct `Mvc.Testing` reference (V4). Evaluated:
  `Fixture.Game` `IsTestProject` empty and `AnalysisLevel` latest-recommended although under `tests/` (consumer props,
  framework path rule bypassed). The very first solution build after adding the projects failed once in restore
  (`NuGet.targets(198): Cannot create a file when that file already exists`); not reproduced on rebuild nor after
  deleting the fixture `obj/bin`.
- [X] T034 [US2] Release notes: append FG-001 and FG-006 to "Fixed", BC-4 with the exact moved files, the removed
  `TestStores.CreateFileStore`, the Identity-only default and the 6 migration steps of plan "Migration notes", and the
  FG-001/FG-006 rows of "Consumer workarounds now removable" in `specs/013-consumer-readiness/release-notes-draft.md`.
  Done when: BC-4 lists every moved file with its replacement. (FR-090, BC-4)
  Note: appended FG-001, FG-006 (Fixed), BC-4 (table of the 5 moved files and their replacements, `CreateFileStore`,
  Identity-only default + empty config, 6 migration steps) and the FG-001/FG-006 rows of "Consumer workarounds now
  removable".
- [X] T035 [US2] US2 checkpoint: Debug and Release build 0 warnings; full suite in memory and with
  `GAMESERVER_TEST_STORE=File`; the Couchbase integration project builds, and — when `COUCHBASE_*` is set — the two
  renamed Couchbase classes (`CouchbaseSettingsTests`, a class using `CouchbaseGameHost`) pass alone.
  Done when: all green; counts recorded; no `gs-local-*` folder left in `%TEMP%` by the run. (FR-091 partial)
  Note: 2026-11-03: Debug and Release build 0 warnings/0 errors (also `--no-incremental` Release). Suite in memory
  **1321 total, 1236 passed, 85 skipped, 0 failed** (T019: 1261/1176/85; +60 new tests: T020 5, T025 6, T027 3, T028 8,
  T030 19, T031 10, T032 3, fixture 6). With `GAMESERVER_TEST_STORE=File`: first run 1 failure in a pre-existing unit
  test unrelated to the kit (`LoginAnalyticsTests.Parallel_adds_keep_the_count_exact_and_seen_players_never_reach_the_capacity_path`,
  `DailyPlayerSet` of slice 012: `marked` 88 923 instead of 80 000; passes 6/6 alone; open item), second run
  1321/1236/85/0. No `gs-local-*` folder in `%TEMP%` after both runs. The Couchbase integration project builds;
  `COUCHBASE_*` not set, so the two Couchbase classes were not run alone (skipped in the suite).

**Checkpoint**: a consumer-shaped project uses the kit without `tests/Shared`; the double behaves like the runner.

## Phase 5: US3 — Developers and operators see every request; consumer middleware reads its result (P3, FG-025)

Goal: one structured event per request (category `GameServer.Requests`), a public `IRequestOutcomeFeature`, and an
optional bounded console sink. Independent test: T040 (scripted session) + T041 (consumer middleware) + T044 (blocked
sink). Depends on: Phase 2; the host tests need the kit `TestHost.LogEntries` (T022, T024). Implementation tasks T036–T039
and T043 may start right after Phase 2.

Note for every task here: request logging is **on by default in Development/Test**, so every `TestHost` now writes one
extra log entry per request. Its formatted message is `{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms`
(no actor id), so the existing "not in logs" checks (`SsoTests.cs:77`, `AccountPurgeTests.cs:188`,
`DeletionExtensionTests.cs:90`, `IdentityLoginTests.cs:126-129`) should stay green — T040 confirms it on the full suite.

- [ ] T036 [US3] Settings in `src/GameServer.Platform/GameServerOptions.cs` (after T007, same file; research R11,
  data-model §3.1, public-api §2): `RequestLog` (`RequestLogSettings`: `Enabled` `bool?`, `ExcludedPaths` `string[]`,
  `IsEnabled(options)` = `Enabled ?? AllowsDevelopmentTools`, `Validate()` → `[request-log] …` lines: ≤ 50 entries, each
  starts with `/`, ≤ 200 chars, no `?`). Validation joins the aggregated startup report in
  `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs`; explicit `Enabled = true` outside
  Development/Test → one startup Warning with the volume estimate (~300–400 B/event, ~13–17 GB/instance/day at 500 rps).
  Tests in a new file `tests/GameServer.Platform.Tests/RequestLogSettingsTests.cs` (rules, defaults per environment) and
  a startup case in `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs` (invalid entry → exit-1 report line;
  `Enabled=true` in Staging → the warning text, via the report of a Staging `TestHost` start).
  Done when: green. (FR-086)
- [ ] T037 [US3] Outcome feature in new files `src/GameServer.Platform.Web/Requests/IRequestOutcomeFeature.cs` (public:
  `IRequestOutcomeFeature`, `RequestOutcomeExtensions.RequestOutcome(this HttpContext)` never null, `RequestResultCodes`
  (`Aborted`, `ForStatus`), `RequestLog` (`Category = "GameServer.Requests"`, `EventId(1, "Request")`)) and
  `Requests/RequestOutcome.cs` (internal sealed; `Finalize(status, aborted, exception)`): aborted =
  `RequestAborted.IsCancellationRequested && !Response.HasStarted` → `StatusCode = null`, `ABORTED`; exception escaping
  → 500 (unless started), `INTERNAL`; else status and, without a code, `OK` (< 400) or `HTTP_{status}`; a code set by a
  consumer endpoint is kept except `ABORTED`/`INTERNAL`; `Completed = true` (research R11, data-model §2,
  contracts/request-log-and-console.md §2).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/RequestOutcomeTests.cs` (unit, IVT exists): every
  finalization rule, consumer-set code kept, `RequestOutcome()` on a bare `DefaultHttpContext` creates one.
  Done when: green. (FR-083, FR-084, SD5)
- [ ] T038 [US3] Middleware and event (after T036, T037, T005 V5): new `src/GameServer.Platform.Web/Requests/RequestLogMiddleware.cs`
  (internal; install feature → `Stopwatch.GetTimestamp()` → `await next` in try/catch/finally → finalize → `if (enabled
  && !excluded && logger.IsEnabled(level))` write inside its own try/catch, failures swallowed and counted) and
  `Requests/RequestLogEntry.cs` (internal readonly struct, `IReadOnlyList<KeyValuePair<string, object?>>`, the 14
  properties of data-model §1 + `{OriginalFormat}`, no LINQ/closures); levels Information < 400, Warning 4xx/aborted,
  Error 5xx; `Route` = `RoutePattern.RawText`, unmatched → cleaned path (control chars → `?`, ≤ 120 + `...`, no query);
  `ActorId` from `ActorClaims` only, cleaned, ≤ 64; `RequestId` from `CorrelationMiddleware.GetCorrelationId`;
  `TraceId` from `Activity.Current`; `/health/live`, `/health/ready` always excluded + `ExcludedPaths` (segment prefix,
  case-insensitive). Register it **first** in `UseGameServer` (`GameServerApplicationExtensions.cs:64-80`, before
  forwarded headers and `ExceptionMiddleware`). Never touches request/response bodies.
  Done when: builds with 0 warnings; T040–T042 cover it. (FR-080, FR-081, FR-082, FR-085, SD2, SD3, SD4)
- [ ] T039 [US3] Writers fill the outcome (after T037; research R11): `src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs`
  (`ResultCode = code.Name` — covers exception middleware, 404, 405, profile/CSRF/permission/Dev refusals, gate, rate
  limiter, timeouts); `Commands/CommandEndpointFactory.cs` `MapCommand` (`:98-131`) and
  `Commands/GmCommandEndpointFactory.cs` (`:136-142`) (`ResultCode = execution.Code.Name`, `Replayed`, `Operation`)
  just before writing; `Endpoints/CoreEndpoints.cs` config route (`:58-70`, `NotModified`); `Operation` from
  `CommandOperationMetadata` at finalization when a command route failed before the runner. One assignment each, no
  body access.
  Done when: builds; existing error-contract and command tests green. (FR-083, FR-084)
- [ ] T040 [US3] Scripted session in a new file `tests/GameServer.Platform.Web.Tests/RequestLogTests.cs` (after T038,
  T039, T024): on a kit `TestHost` with Identity + Counter + GM (+ a test module for the injected 500), read
  `LogEntries` of category `GameServer.Requests`: guest login, snapshot, config get + not-modified, command OK, business
  rejection at 200, replayed command, 400 validation, 401, 404, 405, 413 payload too large, invalid Protobuf, gate
  maintenance, GM request (staff actor), client abort (cancelled `HttpClient` call; `Status` null, `ABORTED`, never a
  commit claim), injected 500 (`INTERNAL`), `/health/live` and `/health/ready` → exactly one event per non-health
  request with the expected `HttpMethod`, `Route` template (no ids, no query), `Profile`, `Operation`, `ActorType`,
  `ActorId`, `RequestId`, `TraceId`, `Status`, `ResultCode`, `Replayed`, `NotModified`, `Aborted`, `DurationMs`, level;
  0 for probes; an `ExcludedPaths` entry suppresses its path; `Enabled=false` → 0 events but the outcome is still
  finalized; Staging default off (flip after startup, as T015); unmatched scanner path with control characters and
  500 characters → cleaned and cut. Metric/event agreement: for every request that reached the runner,
  `command_results_total{code}` = the event's `ResultCode` (requests refused before the runner have an event and no
  metric). Then run the full suite once.
  Done when: green; 100% of the 17 request kinds as expected; full suite green (no existing log assertion broken, or
  every fix listed in a Note). (FR-080, FR-081, FR-083, FR-086, US3-1, US3-2, US3-3, SC-007, edge cases "abort", "unmatched
  paths", "health probes")
- [ ] T041 [US3] Consumer middleware in a new file `tests/GameServer.Platform.Web.Tests/RequestOutcomeFeatureTests.cs`
  (after T040): a middleware registered **before** `UseGameServer` (`beforeUseGameServer` hook) records
  `context.RequestOutcome()` after `await next()` for the T040 session → same code/status/operation/replayed/not-modified
  as the event for 100% of requests, `Completed` true, `ResultCode` never null; a consumer endpoint mapped after
  `UseGameServer` sets its own `ResultCode` → event and outcome carry it; a consumer endpoint answering 418 without code
  → `HTTP_418`; inside an `afterExecute` hook of `MapPlayerCommand` the values set so far are readable; no response body
  is read (the test middleware never touches `Response.Body`).
  Done when: green. (FR-084, US3-4, SC-007 last clause, edge case "consumer endpoint")
- [ ] T042 [US3] Privacy in a new file `tests/GameServer.Platform.Web.Tests/RequestLogPrivacyTests.cs` (after T040):
  run login (guest secret), provider login (fake provider token), bearer requests, test-auth requests, GM login
  (password, cookie, CSRF), a fake purchase (purchase token), a config request with a query string; serialize every
  `GameServer.Requests` entry (all properties) and scan for each secret, token, cookie value, CSRF token, password, body
  bytes and query string → 0 hits; a test-auth actor id with control characters and 1 000 characters → cleaned and
  ≤ 64 characters.
  Done when: green; the scan list is printed in the test output. (FR-082, US3-7, SC-007)
- [ ] T043 [US3] Console sink (after T005 V6, T006 V8; research R12, D12, data-model §3.2): `ConsoleLoggingSettings`
  (`Enabled` false, `MinimumLevel` `Information`, `BufferSize` 1 000) on `LoggingSettings.Console` in
  `src/GameServer.Platform.Observability/ObservabilityOptions.cs`; in `ObservabilityExtensions.AddLogging`
  (`src/GameServer.Platform.Observability/ObservabilityExtensions.cs:40-87`) add
  `WriteTo.Async(sink => sink.Console(new ConsoleLineFormatter(), restrictedToMinimumLevel), BufferSize,
  blockWhenFull: false, monitor: LogSinkMonitor.Instance)` next to the unchanged file sink; validation of level and
  buffer joins the observability errors; new internal `src/GameServer.Platform.Observability/ConsoleLineFormatter.cs`
  (request events in the data-model §1 console shape; other events `HH:mm:ss.fff LVL message [SourceContext]` +
  exception lines; colours only when `Console.IsOutputRedirected` is false; internal `TextWriter` seam);
  `src/GameServer.Platform.Observability/ObservabilityModule.cs`: startup Warning when enabled outside
  Development/Test; `Serilog.Sinks.Console` `PackageVersion` (V8 value) in `Directory.Packages.props` and
  `PackageReference` in `src/GameServer.Platform.Observability/GameServer.Platform.Observability.csproj`.
  Done when: Release build 0 warnings; no restore warning (NU1605/NU1608). (FR-087, FR-088)
  Note (from T026): also add `Observability:Logging:Console:Enabled` = `false` to `LocalFlavorSettings.Keys`/`For` in
  `src/GameServer.TestKit/LocalFlavorSettings.cs` (data-model §3.4 eighth key; `LocalFlavorSettingsTests` checks it binds).
- [ ] T044 [US3] Console tests in new files `tests/GameServer.Observability.Tests/ConsoleLineFormatterTests.cs` (each
  line shape of data-model §1: operation, player/staff, replayed, not_modified, aborted `-`, other events with
  exception; no colour codes when redirected) and `tests/GameServer.Observability.Tests/ConsoleSinkTests.cs` (after
  T043, T038): host with `AddObservability()` and console on through the internal writer → one line per request; a
  blocking writer → 2 000 requests all succeed within the usual bound, `log_dropped_total`/`LogSinkMonitor` drops > 0,
  process memory bounded (queue ≤ `BufferSize`); with console on, the compact JSON file content equals a console-off
  run except timestamps/ids; operational logs reach no other provider.
  Done when: green in 3 consecutive runs. (FR-085, FR-087, FR-088, US3-5, US3-6, SC-008 second half, edge case "stdout
  redirected")
- [ ] T045 [US3] `tests/GameServer.Observability.Tests/LoggingTests.cs` (after T038): the request event appears in the
  compact JSON file with `@mt` = the template, every data-model §1 property, `SourceContext` `GameServer.Requests`,
  `EventId` `{Id:1,Name:Request}` and the enrichment `GameId`/`EnvironmentId`/`InstanceId`; the existing forbidden-value
  scan (`:84`) also covers request events.
  Done when: green. (FR-081, FR-088)
- [ ] T046 [US3] Sample flavor files and their users (after T043): `samples/SampleHost/appsettings.Local.json` and
  `appsettings.Dev.json` → `Observability:Logging:Console:Enabled: true`; no `GameServer:RequestLog` key in any flavor
  file (data-model §3.3); `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs` pins the §3.3 table. Hygiene for
  the other sample-host users: `tests/SampleGame.Tests/SampleHostEntryPointTests.cs` and the host case of
  `SampleHostConfigurationFileTests.cs` set `Observability:Logging:Console:Enabled=false`; confirm that the
  process-based users (`tests/SampleGame.Tests/FileStoreProcessKillTests.cs` Local flavor, Couchbase
  `Infrastructure/SampleHostProcess.cs`) drain stdout (they do on `fea3d29`: `BeginOutputReadLine`,
  `ReadToEndAsync`), so the console sink cannot stall them.
  Done when: green; a Note confirms the stdout check. (FR-087, US3-5)
- [ ] T047 [US3] Opt-in performance test in a new file `tests/SampleGame.Tests/RequestLogPerformanceTests.cs`
  (`GAMESERVER_PERF=1`, otherwise skip with a message; after T044): in-process p95 of a command round trip with request
  logging + console sink (null writer) on vs off → +≤ 1 ms; both sinks blocked → 100% success, bounded memory, drops
  counted.
  Done when: compiles and skips by default; the opt-in numbers are recorded by T076. (FR-085, SC-008)
- [ ] T048 [US3] Fixture outcome check in `tests/ConsumerFixture/tests/Fixture.Tests/` (after T033, T041): the fixture
  host gets a consumer middleware before `UseGameServer` (as a game would) that counts outcomes by `ResultCode`; the
  smoke test asserts `OK` for `fixture.save` and `NOT_FOUND` for an unknown route through it.
  Done when: green. (FR-084, SC-010 enabler for FG-025)
- [ ] T049 [US3] Docs and notes (after T040–T046; Vietnamese): `docs/pcr/platform.md` §13 — request event (fields,
  levels, exclusions, accepted loss LOG-06), outcome feature (semantic contract, contracts/request-log-and-console.md
  §2), console setting, flavor defaults and volume; `docs/guides/environments-and-local-run.md` — settings per flavor
  (contracts §4 table), `LevelOverrides:GameServer.Requests`, volume estimate and file-limit advice;
  `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` — the operator docs (guide, PCR) name every bound
  `RequestLogSettings` key (`GameServer:RequestLog:Enabled`, `GameServer:RequestLog:ExcludedPaths`), as the analytics
  test does; release notes: FG-025 in "Fixed", BC-7 with the steps of plan "Migration notes", FG-025 row of "Consumer
  workarounds now removable".
  Done when: `DocumentedConfigurationKeysTests` green; injecting `GameServer:RequestLog:Bogus` into the guide fails it
  (verified, reverted). (FR-086, FR-089, FR-090, BC-7)

**Checkpoint**: one event per request, outcome feature readable by consumer middleware, console line in local/dev;
full suite green in memory.

## Phase 6: US4 — The framework builds the same inside any consumer repository (P3, FG-003, FG-004)

Goal: a consumer's root `Directory.Build.targets` never reaches framework projects; `IsTestProject` comes from the path
relative to the framework root. Independent test: T051 (+ T052 full build, run in T076). Depends on: T002 (V2, V3).

- [ ] T050 [US4] Seal and relative test detection (research R13, D13): new root `Directory.Build.targets` = `<Project>`
  with only the comment "seals framework projects against Directory.Build.targets files above the framework root
  (FG-003); keep it free of behaviour" (T061 may add the V1 fallback target); in `Directory.Build.props:10` replace the
  `Contains('\tests\')` rule by `_GameServerRelativeDir` (`MakeRelative(MSBuildThisFileDirectory, MSBuildProjectDirectory)`,
  `/` separators, lower case) and `IsTestProject` = `StartsWith('tests/')` (V3 result or its fallback).
  Done when: Debug and Release build 0 warnings; for every project of `GameServer.slnx` except `tests/ConsumerFixture/**`
  (own props) `-getProperty:IsTestProject` is true exactly for `tests/*` (recorded as a Note). (FR-030, FR-031)
- [ ] T051 [US4] `tests/GameServer.Architecture.Tests/BuildSealingTests.cs` (after T050, V2; research R13): copy the
  build inputs (`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`,
  `BannedSymbols.txt`, `.editorconfig`) and every framework `*.csproj` listed in `GameServer.slnx` (excluding
  `tests/ConsumerFixture/**`) into `{temp}/gs-seal-{guid}/tests/consumer/framework/`; write a hostile
  `Directory.Build.targets` (property `GameServerHostileTargets=leaked` + an `Error` target before `Build`) and a hostile
  `Directory.Build.props` in `.../tests/consumer/`; run `dotnet msbuild <proj> -getProperty:IsTestProject
  -getProperty:IsFrameworkCode -getProperty:GameServerHostileTargets -getProperty:Version -getItem:AdditionalFiles`
  (parallel ≤ 8, no restore); assert no hostile property, `IsTestProject` true exactly for `tests/*`, `IsFrameworkCode`
  and `BannedSymbols.txt` for all others, and — once T061 landed — `Version` = the declared version. `dotnet` from
  `DOTNET_HOST_PATH` → `DOTNET_ROOT/dotnet` → PATH (skip with a message if none); temp folder deleted.
  Done when: green; removing the root `Directory.Build.targets` (temporarily) makes it fail naming the leaked property
  (verified, reverted). (FR-030, FR-031, FR-032, US4-1, US4-2, US4-3, edge case "tests above and inside")
- [ ] T052 [P] [US4] New `tools/local/check-build-seal.ps1` (research R13, SD8): same hostile layout with the **whole**
  framework tree (git-tracked files, no `samples/**/Library`), then `dotnet build GameServer.slnx -c Release`; prints the
  error/warning counts and exits non-zero on any error, warning or hostile marker; header documents usage and that it is
  a manual/future-CI check.
  Done when: the script runs once with 0 errors, 0 warnings, no marker (run recorded in T076). (FR-032, SC-004)
- [ ] T053 [US4] Release notes: FG-003, FG-004 in "Fixed"; BC-5 with its steps; FG-003/FG-004 rows of "Consumer
  workarounds now removable" in `specs/013-consumer-readiness/release-notes-draft.md`.
  Done when: present. (FR-090, BC-5)

**Checkpoint**: framework projects evaluate identically under a hostile `tests/` parent.

## Phase 7: US5 — A game runs its own host locally with the framework script (P3, FG-002)

Goal: `run-local.ps1|.sh` gain `-HostProject/-LaunchProfile/-Name` (`--host-project/--launch-profile/--name`), with host
files read by `tools/GameServer.LocalRun`; the default invocation is the slice 011 contract. Independent test: T058
(opt-in part with the fixture host). Depends on: nothing before it except T001; the opt-in part of T058 needs T033.

- [ ] T054 [US5] Resolver tool `tools/GameServer.LocalRun/` (`GameServer.LocalRun.csproj` console app with
  `FrameworkReference Microsoft.AspNetCore.App`, no package; `Program.cs`; `LocalHostDescription.cs`) (research R15,
  D15, data-model §7): `describe --host-project <p> --launch-profile <n>` → project file (file, or the folder's single
  `*.csproj`), `Properties/launchSettings.json` (comments allowed), first `http://` port (none → 5080 + `portNote`),
  `ASPNETCORE_ENVIRONMENT` (required) = flavor, `appsettings.json` + `appsettings.{flavor}.json` + environment variables
  through `ConfigurationBuilder`, `FileStore:Folder` and `Observability:Logging:Directory` (default `logs`) resolved
  against the project folder, nearest upward `global.json` `sdk.version`; prints the `key=value` lines of data-model §7
  (`isSample` = framework `samples/SampleHost/SampleHost.csproj`); every failure → one line naming problem and fix, exit
  2. Add to `GameServer.slnx` under `/tools/`.
  Done when: Release build 0 warnings; `describe` on the sample host prints port 5080, flavor `Local`, the data folder
  of `appsettings.Local.json`, SDK `10.0.403`. (FR-042, FR-043, FR-044, FR-045)
- [ ] T055 [US5] `tests/GameServer.Architecture.Tests/LocalRunDescribeTests.cs` (+ ProjectReference to the tool in
  `GameServer.Architecture.Tests.csproj`; after T054): temp host layouts — project file vs folder, folder with 0 and 2
  projects, profile with one URL / no http URL / several URLs (first http), `https` only, comments in launch settings,
  flavor layering + environment-variable override, missing launch settings, unknown profile (message lists the defined
  ones), profile without `ASPNETCORE_ENVIRONMENT`, broken JSON (file and position), no `global.json` above, nearest of
  two `global.json` files.
  Done when: green; every error case exits 2 with its message. (FR-042, FR-043, FR-044, FR-045, edge cases "launch
  profile without port", "relative paths")
- [ ] T056 [US5] `tools/local/run-local.ps1` (after T054; contracts/run-local-cli.md §1–§5): parameters `-HostProject`,
  `-LaunchProfile` (default `local`), `-Name`; the hardcoded `samples\SampleHost`, `SampleHost.csproj`, `local`, `5080`,
  `10.0.403`, `samples/SampleHost/logs` and the data folder become values of `describe`; sequence of contract §3
  (arguments 2 → SDK 3 → describe 2/5 → port 4 → build 5 → run 6/7 → banner); prints the resolved project; banner of §4
  (`isSample=true` keeps the 011 sample banner byte for byte; other hosts: name, project, base URL, health, port/reset
  notes; LAN warning host-neutral); `-Reset` passes `--FileStore:ResetOnStart=true`; failure before ready tails the
  newest file of `logFolder`; never writes into consumer folders.
  Done when: without parameters the script output and exit codes match slice 011 (manual diff of the banner recorded as
  a Note); `-HostProject tests/ConsumerFixture/src/Fixture.Host -Name "Fixture game"` reaches ready on 5180 (manual,
  recorded). (FR-040, FR-041, FR-042, FR-043, FR-044, FR-045, FR-046, US5-1, US5-2, US5-4)
- [ ] T057 [US5] `tools/local/run-local.sh` (after T054, T056 for identical semantics): `--host-project`,
  `--launch-profile`, `--name` (and `=value` forms); the `sed` JSON reading is replaced by `describe`; same sequence,
  banner and exit codes as T056; bash ≥ 4.4 note for the kill fallback unchanged.
  Done when: on Windows Git Bash the default and fixture invocations behave like T056 (recorded); `shellcheck` clean if
  available. (FR-040, FR-041, FR-045, FR-046)
- [ ] T058 [US5] `tests/GameServer.Architecture.Tests/RunLocalScriptTests.cs` (after T056, T057; research R15,
  contracts §6): **always** — `powershell.exe -File run-local.ps1` (and `bash run-local.sh` when bash is found): unknown
  argument, missing host project, unknown profile, host without launch settings → exit 2 with the message; busy port
  (listener held by the test) → exit 4; all before any host build. **Opt-in** `GAMESERVER_SCRIPT_TESTS=1` (after T033):
  default invocation → ready, sample banner, graceful stop through `tools/local/send-ctrl-c.ps1` → exit 0 (never with
  `-Reset`: the sample's local data folder is the owner's `samples/_database/dbbase`, slice 012 note 11); fixture host
  → ready on 5180, banner name `Fixture game`, one request line on the console after a `/v1/time` call (fixture
  `appsettings.Local.json` turns the console sink on), `-Reset` empties the fixture data folder (under
  `tests/ConsumerFixture/.local/`), a fixture setting that fails startup → exit 6 + log tail; no host process left.
  Done when: the always-part is green in the default suite; the opt-in part is run in T076. (FR-041, FR-045, SC-005,
  US3-5 console line)
- [ ] T059 [US5] Docs and notes: "superseded by `specs/013-consumer-readiness/contracts/run-local-cli.md`" note in
  `specs/011-local-env/contracts/operations-cli.md` §4; consumer invocation, parameters, banner and exit codes in
  `docs/guides/environments-and-local-run.md` (Vietnamese); release notes: FG-002 in "Fixed", the "non-breaking but
  recommended" run-local step, FG-002 row of "Consumer workarounds now removable".
  Done when: present; `DocumentedConfigurationKeysTests` green. (FR-046, FR-090)

**Checkpoint**: a second host runs with the framework script; the default invocation is unchanged.

## Phase 8: US6 — Everyone knows which framework and game version are running (P4, FG-007)

Goal: version `0.7.0` declared once, stamped into assemblies (with the git sha), logged at startup, in telemetry and in
`GET /gm/v1/ops/version`. Independent test: T065 + T066 + T064. Depends on: T050 (same props/targets files) for T061;
T038 (same `GameServerApplicationExtensions.cs`) for T062; T043 (same `ObservabilityExtensions.cs`/`ObservabilityOptions.cs`)
for T060/T063.

- [ ] T060 [US6] Runtime identity in a new file `src/GameServer.Platform/GameServerRuntimeInfo.cs` (research R14, D14,
  data-model §4.2, public-api §2): `GameServerVersion` (`Framework`, `FrameworkInformational`, `Of(Assembly)`,
  `InformationalOf(Assembly)`) and `GameServerRuntimeInfo` record with `ResolveInstanceId(configured)` (configured →
  `HOSTNAME` → machine name) as the only implementation; `ObservabilityOptions.ResolveInstanceId()` delegates to it;
  `AddGameServer` in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs` builds the record once (game assembly =
  `IHostEnvironment.ApplicationName` per T004, fallback entry assembly, then `unknown`; instance id from
  `Observability:InstanceId`), registers it and exposes `GameServerBuilder.Runtime`.
  Tests in a new file `tests/GameServer.Platform.Tests/GameServerRuntimeInfoTests.cs` (instance-id order, `Of` with and
  without `+sha`, 3-part fallback, `unknown`) and one host case in `tests/GameServer.Platform.Web.Tests/TestKitHostTests.cs`
  (`Runtime` resolved from DI equals `GameServerBuilder.Runtime`).
  Done when: green; log file names, OTel instance id and the analytics file sink still use the same instance id
  (existing observability and analytics tests green). (FR-052)
- [ ] T061 [US6] Declared version (after T050, T003 V1): `Directory.Build.props` →
  `<GameServerFrameworkVersion>0.7.0</GameServerFrameworkVersion>` and `<Version>$(GameServerFrameworkVersion)</Version>`;
  if V1 failed for submodules, the `git rev-parse HEAD` fallback target in `Directory.Build.targets` (before
  `GetAssemblyVersion`, `ContinueOnError=true`, only when `SourceRevisionId` is empty); `sdk/unity/com.gameserver.client/package.json`
  `version` → `0.7.0`.
  Done when: Release build 0 warnings; `GameServer.Core.dll` has AssemblyVersion `0.7.0.0`, FileVersion `0.7.0.0`,
  InformationalVersion `0.7.0+{40 hex}` (recorded). (FR-050, FR-051, BC-6)
- [ ] T062 [US6] Startup line in `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs` (after T038,
  T060): at the end of a successful `UseGameServer`, category `GameServer.Startup`, Information:
  `Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}),
  instance {InstanceId}.` (not logged when validation fails, not in `--provision`).
  Test in `tests/GameServer.Platform.Web.Tests/TestKitHostTests.cs` (via `LogEntries`): exactly one such line with the
  five properties; none on a failed start.
  Done when: green. (FR-054, US6-3)
- [ ] T063 [US6] Telemetry in `src/GameServer.Platform.Observability/ObservabilityExtensions.cs:101-104` (after T043,
  T060): `AddService(serviceName, serviceVersion: runtime.GameVersion, serviceInstanceId)` + resource attribute
  `gameserver.framework.version` = `runtime.FrameworkVersion`.
  Test in `tests/GameServer.Observability.Tests/TelemetryTests.cs`: a span and a metric carry `service.version` and
  `gameserver.framework.version` with the expected values; `service.name`/`service.instance.id` unchanged.
  Done when: green. (FR-053, US6-4)
- [ ] T064 [P] [US6] GM version read in `src/GameServer.Modules.Gm.Adapters/GmModule.cs` (after T060;
  contracts/gm-ops-version.md): `VersionRoute = "/gm/v1/ops/version"`, `GmVersionResponse(GameId, EnvironmentId,
  InstanceId, GameVersion, FrameworkVersion)` (namespace `GameServer.Modules.Gm`), `AsGmEndpoint(GmPermissions.OpsRead)`,
  `Produces<GmVersionResponse>()`; values from the `GameServerRuntimeInfo` singleton.
  Tests in a new file `tests/GameServer.Gm.Tests/GmVersionEndpointTests.cs`: `ops.read` → 200 with the five fields equal
  to the runtime info; no session → 401; session without `ops.read` → 403; `/health/live`, `/health/ready`, `/v1/time`,
  guest login and a player command carry no framework or game version in body or headers. Regenerate
  `docs/api/gm-openapi.json` with `UPDATE_OPENAPI=1` while running `GameServer.Gm.Tests`, review the diff (only the new
  path and schema), commit it with the slice.
  Done when: `GmVersionEndpointTests` and `OpenApiTests` green without `UPDATE_OPENAPI`. (FR-055, US6-5, SD10)
- [ ] T065 [US6] `tests/GameServer.Architecture.Tests/VersionTests.cs` (after T061; research R14): declared version
  (read from `Directory.Build.props`) = `package.json` `version`, failure message names both values (a unit test of the
  comparison helper with a deliberate mismatch pins the message); every `GameServer.*` assembly and `SampleHost`
  carry `0.7.0` (assembly, file, informational core); informational version has `+` and 40 hex digits when the
  repository has `.git`; `GameServerVersion.Framework` = declared version.
  Done when: green; editing `package.json` to `0.7.1` (temporarily) fails naming both values (verified, reverted).
  (FR-050, FR-051, US6-1, US6-2, SC-006)
- [ ] T066 [US6] Fixture version check in `tests/ConsumerFixture/tests/Fixture.Tests/` (after T033, T048 same folder,
  T061): `GameServerVersion.Framework` = `0.7.0` through the consumer project reference; the fixture host's
  `GameServerRuntimeInfo.GameVersion` starts with `1.2.3`; the GM version read on the fixture host (if the fixture maps
  the GM API) or the startup line shows both.
  Done when: green. (FR-050, FR-052, US6-1 consumer part, SC-006)
- [ ] T067 [US6] Docs and notes: `docs/pcr/gm.md` permission table and ops route table list `GET /gm/v1/ops/version`;
  `docs/guides/environments-and-local-run.md` — where to read the version (startup line, OTel, GM read); release notes:
  FG-007 in "Fixed", BC-6 with its steps, FG-007 row of "Consumer workarounds now removable", the release rule
  "annotated tag `v{GameServerFrameworkVersion}`; bump props and `package.json` together".
  Done when: present. (FR-051, FR-055, FR-090, BC-6)

**Checkpoint**: framework version identical in assemblies, Unity package, startup log, telemetry and GM read.

## Phase 9: US7 — A game follows one consumer guide and picks error codes that never collide (P4, FG-011, FG-012)

Goal: the game band 50000–89999 is reserved and enforced for framework-owned modules; one English consumer guide plus
updated Vietnamese pages describe the shipped behaviour, each stamped with the framework version. Independent test: T075
(walkthrough). T068 can run any time after T001; T069–T075 run after Phases 3–8 (the docs describe shipped behaviour).
HTML DoD (resume file): `docs/html` is updated because this slice changes the pipeline (request log, Dev refusal),
hosting (kit, version line) and the local run.

- [ ] T068 [P] [US7] `ErrorCodeBands` in a new file `src/GameServer.Core/Errors/ErrorCodeBands.cs` (public-api §1;
  research R16) and `tests/GameServer.Architecture.Tests/ErrorCodeBandTests.cs` (+ ProjectReference
  `templates/module/Template.Module.Adapters/Template.Module.Adapters.csproj` in `GameServer.Architecture.Tests.csproj`):
  discover every `IGameModule` type with a parameterless constructor in the framework, built-in, sample and template
  assemblies plus `CoreErrorCodes`; fail naming module and range on any overlap with 50000–89999; assert the known
  ranges of data-model §5 (core, counter, economy, iap, gm, identity, sample.game, template).
  Done when: green; a temporary code 50001 in the template module fails the test naming `template` (verified,
  reverted). (FR-060, FR-062, US7-2)
- [ ] T069 [US7] `docs/guides/module-authoring.md` (Vietnamese; after T017, same file): §4 ranges table + game band
  50000–89999, the example moves from 50000–50099 (`:60`) to the template's 90000–90099 with "a game module picks its
  range in 50000–89999" (SD11); §8 tests in two variants — in-repo (`GameServer.slnx`, `tests/Shared`,
  `SampleTestHost`) and consumer repo (`$(GameServerRoot)` references, kit `TestHost`, `CommandContextDouble` handler
  test, `CheckCommit`), link to the consumer guide; `templates/module/README.md` — in-repo vs consumer project paths
  (the `../../../src` path), band.
  Done when: no example in `docs/` or `templates/` uses a range inside 50000–89999 (search recorded);
  `DocumentedConfigurationKeysTests` green. (FR-060, FR-061, FR-071)
- [ ] T070 [US7] New `docs/guides/consumer-guide.md` (English; plan US7 section list): 1 repository layout, submodule
  pinned to an annotated tag (no branch, no `--remote`); 2 build files — what the game mirrors, what is sealed
  (`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `.editorconfig` `root = true`) and
  what still comes from the consumer (`global.json` of the directory `dotnet` runs in, `nuget.config` hierarchy, manual
  sync FG-008), CPM import and pinned versions; 3 project references; 4 host composition (copy of SampleHost until
  FG-009; register Dev modules only when development tools are allowed); 5 tests — kit reference (+ `Mvc.Testing` per
  V4), `TestHost`, `LocalFlavorFactory` and the full `LocalFlavorSettings` key list with the rule "a new setting that
  writes to disk, starts background work or writes to the console joins the list in its slice", `CommandContextDouble`,
  `CheckCommit`, outcome feature in tests; 6 local run with `-HostProject`/`--host-project`; 7 versions (API, startup
  line, OTel, GM read, tag rule); 8 request log and console (settings per flavor, volume, outcome feature contract);
  9 error codes (band); 10 Unity SDK by UPM URL `…#v0.7.0` (game `.proto` codegen: "coming in slice 014"); 11 bump
  checklist (release notes, submodule to the tag, diff mirrored build files, run tests, remove the workarounds listed in
  the release notes). Each code sample is copied from the fixture (`tests/ConsumerFixture`), not invented. Add the
  "named in the consumer guide" assertion to `tests/GameServer.Platform.Web.Tests/LocalFlavorSettingsTests.cs` (T028).
  Done when: every FR-070 item has a section (checklist in a Note); `LocalFlavorSettingsTests` green; the page states
  "matches framework 0.7.0 (unreleased)". (FR-012, FR-033, FR-070, FR-072, US7-3)
- [ ] T071 [US7] `docs/pcr/platform.md` (after T049, same file): §8 game band next to the used ranges; new **§19
  Consumer readiness (slice 013)** — consumer-facing public API of contracts/public-api.md (kit additive-only rule,
  `LocalFlavorSettings` rule, outcome feature, `ErrorCodeBands`, `GameServerVersion`/`GameServerRuntimeInfo`), Dev rule
  summary (three layers, one decision), request-log/console settings reference, version and release procedure (tag
  `v{version}`, props + `package.json` together), "Breaking" BC-1..BC-7; `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs`
  adds `## 19.` and `docs/guides/consumer-guide.md` to `Documents()`.
  Done when: `DocumentedConfigurationKeysTests` green; §19 lists BC-1..BC-7. (FR-051, FR-060, FR-089)
- [ ] T072 [US7] HTML pages (Vietnamese; after T017, T049, T059, T067; FR-071..FR-073): `docs/html/02-them-command.html`
  — §4.2 band, §4.6 host registration and §4.8 / §5 step 7 tests each in two variants (in-repo / consumer repo with the
  kit and a `CommandContextDouble` handler test), link to the consumer guide; `docs/html/01-gioi-thieu.html` — Dev rule,
  request log in the pipeline walkthrough, kit, Unity package version 0.6.0 → 0.7.0 (`:646`);
  `docs/html/03-deploy.html` — request log/console per flavor and volume, version read, Dev rule; `docs/html/index.html`
  — summary lines; on all four pages replace the stamp "Đúng với commit `175df65` …" (meta line ~499 and footer) with
  "Đúng với framework v0.7.0 (chưa phát hành)" (→ `v0.7.0` when tagged); `01-gioi-thieu.html:518` table header "tại
  commit 175df65" updated likewise.
  Done when: a search of `docs/html` finds no `175df65` and no "Dev … Production-only" statement; the request log
  appears in the pipeline walkthrough and the console line in the local-run step; links resolve (relative paths
  checked); the guides touched by this slice (`docs/guides/module-authoring.md`, `environments-and-local-run.md`,
  `consumer-guide.md`) and `templates/module/README.md` carry the same "framework 0.7.0 (unreleased)" line (FR-072 covers
  guides, plan R18 names only the HTML stamps). (FR-008, FR-060, FR-071, FR-072, FR-073, US7-4)
- [ ] T073 [P] [US7] `README.md` — pointer to `docs/guides/consumer-guide.md` and the run-local host parameters;
  `docs/pcr/unity-sdk.md:3` — package version 0.7.0 (lockstep with the framework version).
  Done when: present; `DocumentedConfigurationKeysTests` green (README is one of its documents). (FR-070, FR-051)
- [ ] T074 [US7] Finalize `specs/013-consumer-readiness/release-notes-draft.md` (after T018, T034, T049, T053, T059,
  T067, T072): FG ids fixed (FG-010, 001, 006, 003, 004, 002, 007, 011, 012, 025); BC-1..BC-7 each with "what breaks /
  how it surfaces / fix" (spec table + plan migration steps); the consumer workaround table (spec "Consumer impact"),
  SC-010 statement (8 marker ids removable); "Docs not re-checked" = every page and guide not touched by this slice
  (FR-072); open manual checks (T075, T076); a note that slices 014–016 append their sections and the tag message is
  built from the merged notes; the tag is `v0.7.0` (FR-051).
  Done when: every BC and FG id appears with its fix; the not-re-checked list is explicit. (FR-072, FR-090)
- [ ] T075 [US7] Consumer-guide walkthrough (manual, SC-009; after T070–T074): in a fresh temp folder outside the
  framework, create a game repo, add this repository as a submodule at the slice commit (local path), and — using only
  `docs/guides/consumer-guide.md` and the updated pages — add one module with one command and its error codes in
  50000–89999, one handler unit test with `CommandContextDouble`, one `LocalFlavorFactory` smoke test, and run the host
  with `framework/tools/local/run-local.ps1 -HostProject …`. Record every missing or wrong step; fix each in the docs
  (and the fixture if it differs) before the slice closes; delete the temp repo.
  Done when: a Note records who, date, steps, gaps found and their fixes; handler test, smoke test and host run green
  without reading framework source. (FR-070, FR-071, US7 independent test, SC-009)

**Checkpoint**: docs describe the shipped behaviour; release notes complete.

## Phase 10: Polish, verification and close

- [ ] T076 Opt-in and manual runs (after T047, T052, T058, T075), each recorded as a Note:
  - `GAMESERVER_SCRIPT_TESTS=1` `RunLocalScriptTests` (default + fixture host);
  - `GAMESERVER_PERF=1` `RequestLogPerformanceTests` (SC-008: p95 delta ≤ 1 ms; numbers also into PCR §19);
  - `tools/local/check-build-seal.ps1` (SC-004: 0 errors, 0 warnings, no marker);
  - `run-local.sh` on macOS/Linux if a machine or container with the .NET SDK is available, else "not run" (spec
    assumption, open item).
  Done when: every run green or its miss explained and listed as an open item. (FR-032, FR-041, FR-085, FR-091,
  SC-004, SC-005, SC-008)
- [ ] T077 Pre-review run (after every task above): Debug **and** Release build of `GameServer.slnx` 0 warnings; full
  suite in memory; full suite with `GAMESERVER_TEST_STORE=File`. Afterwards `git status --porcelain` shows only
  intended changes (plus the owner's untracked `tasks/`): no `tests/ConsumerFixture/.local/`, no `samples/SampleHost/logs`
  or `analytics/` created by tests, no `gs-local-*`/`gs-seal-*` left in `%TEMP%`, no `SampleHost`/`Fixture.Host` process.
  Done when: all green with counts recorded (total/passed/skipped vs T001), or every failure fixed. (FR-091)
- [ ] T078 Run tech-lead-reviewer on the slice (request + plan + this file + `git diff --stat` list of changed files).
  Focus: FG-010 layers (refusal before any authentication; no fallback branch; runner execute + replay; one decision),
  `CommandContextDouble` ↔ runner conformance and the IVT surface, request-log privacy (LOG-05) and non-blocking sinks
  (LOG-06), outcome finalization (aborts, escapes), rename completeness (no Identity-only default where the sample
  composition was meant), build seal, version stamping in a submodule, docs ↔ behaviour.
  Done when: the review verdict and findings are recorded in this file under "Review (T078)". (FR-091)
- [ ] T079 Fix the review findings (after T078): every Blocker and Major fixed with a test; Minors fixed or documented
  with the reason; spec/plan/contracts amended where a fix changes a documented behaviour; the review summary (counts,
  fixes, deviations, deferred items) appended under "Review (T078)"; affected test classes re-run.
  Done when: no open Blocker/Major; summary appended. (FR-091)
- [ ] T080 Final verification — full suite **including Couchbase** (after T079): Release build 0 warnings; Debug build
  0 warnings; `test --solution GameServer.slnx -c Release --no-build` in memory, with `GAMESERVER_TEST_STORE=File`, and
  with the Couchbase variables of the resume "Environment" section (`COUCHBASE_CONNSTR=couchbase://localhost`,
  `COUCHBASE_USERNAME=Administrator`, `COUCHBASE_PASSWORD` from the user variable `COUCHBASE_PW` read via
  `[Environment]::GetEnvironmentVariable('COUCHBASE_PW','User')` and never printed, `COUCHBASE_BUCKET=gameserver-it`),
  run through PowerShell **in the background** (~25 min) while nothing else builds. Afterwards: bucket `gameserver-it`
  has no leftover test scope or `gs-it-*` user; no host process left; `git status` as in T077.
  Then commit `feat:` (conventional, identity hung-bui-121, **no** signature or Co-Authored-By lines) only when the owner
  asks for the commit.
  Done when: counts of the three runs recorded in a table (total/passed/failed/skipped/time); 0 failed (the known flaky
  `TelemetryTests.A_command_is_one_trace…` re-run once if it fails, and noted). (FR-091)
- [ ] T081 Update `AgentMemories/features/p05-speckit/00_summary.md` (after T080): slice 013 row and state (branch,
  not yet tagged; round 013–016 → one tag `v0.7.0`), technical notes (`AllowsDevProfile` three layers,
  `GameServer.TestKit` + `SampleTestHost` rename rule and the `GmTestHost` substring trap, `LocalFlavorSettings` rule,
  `CommandContextDouble` over the internal context + IVT, `GameServer.Requests` event + outcome feature + console sink
  defaults, root `Directory.Build.targets` + relative `IsTestProject`, version single source `Directory.Build.props` =
  `package.json`, `GET /gm/v1/ops/version`, game band 50000–89999, release-notes draft location), open items (V-results
  that needed fallbacks, `run-local.sh` on macOS/Linux, walkthrough gaps, review deferrals), and the next step (slice
  014).
  Done when: the file names 013 as the latest slice and the next step. (process)

## Dependencies

```text
Phase 1  T001 ─┬─ T002 (V2,V3) ──────────────────────────────► US4 T050 → T051 ; T052
               ├─ T003 (V1) ─────────────────────────────────► T061
               ├─ T004 (V4,V7) ─► T009 (kit skeleton) ──┐
               ├─ T005 (V5,V6) ─────────────────────────┼──► T038, T043
               └─ T006 (V8) ────────────────────────────┼──► T043
Phase 2  T007 (AllowsDevProfile) ─► US1                 │
         T008 (CommitRules) ────────────────────────────┼──► T013, T029
                                                        │
US1 (MVP)  T010 → T011 ⇄ T014 (T010+T011+T014 land together) → T012 → T013 → T015 → T016 → T017 → T018 → T019
                                                        │
US2        T020 ∥ T021 → T022 → T023 → T024 (rename, after US1) → T025 ; T026 → T027, T028
           T029 (needs T008, T020) → T030, T031 ; T032 ; T033 (needs T026, T029) → T034 → T035
US3        T036 (after T007) ; T037 → T038 → T039 ─┐ (impl may start after Phase 2)
           tests T040 → T041, T042 need T024 ──────┘ ; T043 → T044, T045, T046 → T047 ; T048 (needs T033) ; T049
US4        T050 → T051 ; T052 [P] ; T053
US5        T054 → T055 ; T056 → T057 → T058 (opt-in part needs T033) → T059
US6        T060 (after T043) → T061 (after T050) → T062 (after T038), T063, T064 [P] → T065 → T066 (after T048) → T067
US7        T068 [P] any time ; T069–T074 after US1–US6 ; T075 after T070–T074
Close      T076 → T077 → T078 → T079 → T080 (Couchbase, background) → T081
```

- Phase 1 blocks everything; each V-check blocks only the tasks named in the [VERIFY] table.
- **US1 needs only T001 + T007.** Do it first and keep it shippable alone (T019 checkpoint).
- **US2 after US1**: the rename T024 must also cover the new US1 test files (`DevProfile*Tests.cs`). If US2 started
  in parallel, redo the T024 grep after merging US1.
- US3 implementation (T036–T039, T043) can start after Phase 2; its host tests (T040–T042) need T024 (kit
  `LogEntries`). US4 and US5 (except T058 opt-in) are independent of US2/US3. US6 waits for T043 and T050.
- Shared files that force order (no `[P]` between these tasks):
  - `src/GameServer.Platform/GameServerOptions.cs`: T007 → T036.
  - `src/GameServer.Platform/Commands/CommandRunner.cs`: T008 → T013.
  - `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs`: T036 → T038 → T062.
  - `src/GameServer.Platform.Observability/ObservabilityOptions.cs`, `ObservabilityExtensions.cs`: T043 → T060 → T063.
  - `Directory.Build.props`, `Directory.Build.targets`: T050 → T061. `Directory.Packages.props`: T043.
  - `GameServer.slnx`: T009 → T033 → T054. `tests/GameServer.Architecture.Tests/GameServer.Architecture.Tests.csproj`:
    T032 → T055 → T068.
  - `tests/GameServer.Platform.Web.Tests/DevProfileValidationTests.cs`: T010 → T011; `DevProfileMiddlewareTests.cs`:
    T012 → T015; `StartupValidationTests.cs`: T010 → T036; `TestKitHostTests.cs`: T025 → T060 → T062;
    `LocalFlavorSettingsTests.cs`: T028 → T070.
  - `tests/ConsumerFixture/tests/Fixture.Tests/`: T033 → T048 → T066.
  - `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs`: T049 → T071.
  - Docs: `docs/pcr/platform.md` T017 → T049 → T071; `docs/guides/environments-and-local-run.md` T017 → T049 → T059 →
    T067; `docs/guides/module-authoring.md` T017 → T069; `docs/html/*` T017 → T072;
    `release-notes-draft.md` T018 → T034 → T049 → T053 → T059 → T067 → T074.
- Parallel examples: T002 ∥ T003 ∥ T004 ∥ T005 ∥ T006; T008 ∥ T009 (after T004); T020 ∥ T021; T050 ∥ T054 ∥ T068
  (US4, US5, US7 band lanes beside US2); T052 ∥ T053; T064 ∥ T063; T073 ∥ T069.

**Critical path**: T001 → T007 → US1 (T010 … T019) → T021 (also needs T009 ← T004) → T022 → T023 → T024 (rename) → T026 → T029 →
T033 (fixture) → T040 (needs T038/T039) → T043 → T060 → T061 → T065/T066 → T070 → T072 → T074 → T075 → T076 → T077 →
T078 → T079 → T080 (~25 min Couchbase) → T081. The kit move + rename (T021–T024) is the long pole: US3 host tests, the
fixture, the run-local opt-in check and the US6 fixture check all wait for it.

## Implementation strategy

1. **MVP = US1 (Phases 1–3).** FG-010 is a HIGH security finding: Dev endpoints and commands refused outside
   Development/Test at three independent layers, Development unchanged. Stop at the T019 checkpoint; it can ship alone
   (cherry-pick T007 + T010–T018).
2. **US2** next: the kit and the rename touch ~45 test files; do it on a quiet tree, then run the full suite twice
   (memory, File).
3. **US3** (request log, outcome feature, console), then **US4/US5** (small, independent lanes), then **US6**.
4. **US7 and close**: docs describe the shipped behaviour, walkthrough, opt-in runs, review, Couchbase gate, resume.

With one implementer run the phases in order. With two: lane 1 = US1 → US2 → US3 → US6; lane 2 (after Phase 2) =
US4 → US5 (fast checks) → T068; merge before T058 opt-in and T061.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Rename misses a `TestHost.StartAsync` call: it still compiles against the kit and silently runs Identity-only with an empty config | High (tests pass while testing less, or fail far from the cause) | T024 exact-regex grep = 0, counts vs T019, full suite in memory + File |
| Plain text replace corrupts `GmTestHost.StartAsync` / `GmTestHost.SessionCookie` / `GmTestHost.Password` (substring `TestHost.`) | Medium (compile errors) | T024 uses the word-boundary regex and checks the 20 `GmTestHost.StartAsync` sites survive |
| Moved helpers become framework code (analyzers `latest-recommended`, warnings as errors, banned APIs) | Medium (build red after the move) | T021/T022 fix findings without behaviour change, listed in a Note |
| `FullGame` without `CheatModule` outside Development breaks a test that relied on it | Medium | T014 search over `tests/**`; Couchbase `CouchbaseGameHost` is Development (keeps the cheat); `SampleHostFlavorTests` already expects 404 on beta |
| Request log on by default in every `TestHost` changes captured logs | Low | formatted message has no actor id; T040 runs the full suite and lists any fix |
| Console sink on in the sample Local/Dev flavors floods or stalls processes that read the host's stdout | Low–Medium | bounded dropping sink (V6, T044); T046 turns it off in WAF users; process users drain stdout (checked) |
| A [VERIFY] fact is wrong (V1 submodule sha, V4 targets flow, V5 bridge) | Medium | T002–T006 before the dependent tasks; fallbacks stated in the table |
| `BuildSealingTests` slows the default suite | Low (+5–10 s) | evaluation only, parallel ≤ 8, no restore; full build only in `check-build-seal.ps1` |
| Opt-in default `run-local` writes into the owner's `samples/_database/dbbase` | Medium (owner data) | never `-Reset` in T058; fixture data under `tests/ConsumerFixture/.local/` |
| IVT from Platform to the kit grows over time | Low | T032 names the allowed internal types; kit never referenced by hosts |
| Docs drift from code across 10 FG ids | Medium | per-story docs tasks, docs-key tests (T049, T071), walkthrough T075 |
| Couchbase suite (~25 min) hits cluster-manager 503s | Low | existing retry; Couchbase only in T080 (and targeted classes in T035) |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T010, T014, T016 |
| FR-002 | T011, T014, T016 |
| FR-003 | T012, T015 |
| FR-004 | T012 |
| FR-005 | T013, T016 |
| FR-006 | T007, T010, T011, T012, T013 |
| FR-007 | T010, T011, T012, T013, T015, T016, T019 |
| FR-008 | T010 (doc comments), T013 (doc comment), T017, T072 |
| FR-010 | T009, T021, T032, T033 |
| FR-011 | T021, T022, T025, T033 |
| FR-012 | T026, T027, T028, T070 |
| FR-013 | T023, T024, T027, T033 |
| FR-014 | T021, T032 |
| FR-020 | T029, T030 |
| FR-021 | T020, T029, T030 |
| FR-022 | T029, T030 |
| FR-023 | T029, T030, T031 |
| FR-024 | T029, T030, T031 |
| FR-025 | T008, T029, T031 |
| FR-026 | T008, T029, T030 |
| FR-030 | T050, T051 |
| FR-031 | T050, T051 |
| FR-032 | T051, T052, T076 |
| FR-033 | T070 |
| FR-040 | T056, T057 |
| FR-041 | T056, T057, T058, T076 |
| FR-042 | T054, T055, T056 |
| FR-043 | T054, T055, T056 |
| FR-044 | T054, T055, T056 |
| FR-045 | T054, T055, T056, T057, T058 |
| FR-046 | T056, T057, T059 |
| FR-050 | T061, T065, T066 |
| FR-051 | T061, T065, T067, T071, T073, T074 |
| FR-052 | T060, T066 |
| FR-053 | T063 |
| FR-054 | T062 |
| FR-055 | T064, T067 |
| FR-060 | T068, T069, T071, T072 |
| FR-061 | T069 |
| FR-062 | T068 |
| FR-070 | T070, T073, T075 |
| FR-071 | T069, T072, T075 |
| FR-072 | T070, T072, T074 |
| FR-073 | T072 |
| FR-080 | T038, T040 |
| FR-081 | T038, T040, T045 |
| FR-082 | T038, T042 |
| FR-083 | T037, T039, T040 |
| FR-084 | T037, T039, T041, T048 |
| FR-085 | T038, T044, T047, T076 |
| FR-086 | T036, T040, T049 |
| FR-087 | T043, T044, T046 |
| FR-088 | T043, T044, T045 |
| FR-089 | T049, T071 |
| FR-090 | T018, T034, T049, T053, T059, T067, T074 |
| FR-091 | T019, T035, T076, T077, T078, T079, T080 |

| Success criterion | Tasks |
| --- | --- |
| SC-001 | T013, T015, T016 |
| SC-002 | T033 |
| SC-003 | T031 |
| SC-004 | T051, T052, T076 |
| SC-005 | T056, T057, T058, T076 |
| SC-006 | T061, T065, T066 |
| SC-007 | T040, T041, T042 |
| SC-008 | T044, T047, T076 |
| SC-009 | T075 |
| SC-010 | T018, T034, T048, T074 (release-notes workaround table; removal itself is owned by the game session) |

Scenarios:
- US1: US1-1 → T010, T016; US1-2 → T011, T016; US1-3 → T012, T015; US1-4 → T013; US1-5 → T016; US1-6 → T012.
- US2: US2-1 → T033; US2-2 → T025; US2-3 → T027; US2-4 → T030; US2-5 → T030, T031; US2-6 → T031; US2-7 → T030.
- US3: US3-1, US3-2, US3-3 → T040; US3-4 → T041, T048; US3-5 → T044, T046, T058; US3-6 → T044, T047; US3-7 → T042.
- US4: US4-1, US4-2, US4-3 → T051 (+ T052).
- US5: US5-1 → T056, T058; US5-2 → T054, T056, T058; US5-3 → T055, T058; US5-4 → T056, T058.
- US6: US6-1 → T061, T065, T066; US6-2 → T065; US6-3 → T062; US6-4 → T063; US6-5 → T064.
- US7: US7-1 → T069, T071, T072; US7-2 → T068; US7-3 → T070, T075; US7-4 → T072.

[VERIFY] items: V1 → T003, T061, T065; V2 → T002, T051; V3 → T002, T050; V4 → T004, T009, T026, T033, T070;
V5 → T005, T038, T045; V6 → T005, T043, T044; V7 → T004, T026; V8 → T006, T043.

## Planning notes (gaps between spec, plan and code, resolved here)

1. **Phase order vs plan "Implementation phases".** The plan's Phase 1 bundles version props, the build seal, the kit
   skeleton, runtime info and `CommitRules`. Here only behaviour-neutral pieces are foundational (T007–T009); the version
   (BC-6) moved to US6 and the seal (BC-5) to US4, so US1 can ship without other breaking changes.
2. **Rename size and trap.** Measured on `fea3d29`: 102 static call sites in 35 files (`StartAsync` 89, `BuildApp` 5,
   `DefaultSettings` 1, `DefaultModules` 7 — the plan's "110" counted `GmTestHost.StartAsync` too, 20 sites, which must
   **not** be renamed). A missed site compiles against the kit; T024 relies on the grep, not the compiler.
3. **Moved helpers become framework code** (`src/` → analyzers, banned APIs, warnings as errors). `tests/Shared` uses no
   banned API today, but analyzer findings are likely (T021/T022).
4. **Docs-key test scope.** `DocumentedConfigurationKeysTests` checks only `GameServer:`/`Couchbase:`/`FileStore:` keys and
   PCR §17/§18. `GameServer:RequestLog:*` resolves through `GameServerOptions.RequestLog` once T036 lands; T049 adds the
   "operator docs name every RequestLog key" rule, T071 adds PCR `## 19.` and the consumer guide. `Observability:*` keys
   are not covered by that test; `LocalFlavorSettingsTests` (T028) covers the kit's `Observability:*` keys.
5. **FR-026 "Touches/claim limits" in the commit check.** Plan `CheckCommit` covers code and audit rules; Touches and
   claim limits are enforced at call time by the shared context (FR-023), before any commit. T030 documents this with a
   test; no separate commit-time check.
6. **FR-083 "metric and event agree".** Interpreted for requests that reached the runner; a command route refused
   earlier (Dev in the pipeline, 401, gate) has an event and no `command_results_total` entry (T040).
7. **"Any other deployment environment"** (FR-001, edge case) cannot be configured: startup rejects unknown values
   (`FlavorValidationTests:47`). Tests set the value directly on the options (`"Qa"`, T007, T010, T011).
8. **Release notes location.** This file follows the orchestrator instruction: draft in
   `specs/013-consumer-readiness/release-notes-draft.md`. Plan D18/R18 names `docs/releases/v0.7.0.md` (shared by 014–016);
   that file is created at round close from the slice drafts (outside this slice) — confirm with the owner.
9. **FR-072 covers guides too**: plan R18 stamps only the four HTML pages; T070/T072 also stamp the touched guides and
   the template README.
10. **Paths**: `ProvisioningRunner.cs` is in `src/GameServer.Platform.Web/Hosting/` (research cites it without folder).
    The Couchbase integration project links `tests/Shared` too and needs the kit reference (T023); `CouchbaseGameHost`
    runs Development, so `FullGame` keeps the cheat there.
11. **[VERIFY] count**: research lists V1–V7; V8 (transitive `Serilog.Sinks.Console` version) is added from the plan's
    Technical Context.
12. **Stale wording**: plan SD1 still says "FR-086 default [NEEDS CLARIFICATION]"; the spec resolved it (on in
    Development/Test, off in Staging/Production) and the plan's design follows the resolution. No conflict.
