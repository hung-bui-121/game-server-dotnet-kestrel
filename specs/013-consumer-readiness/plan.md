# Implementation Plan: Consumer readiness (slice 013 consumer-readiness)

**Branch**: `013-consumer-readiness` | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/013-consumer-readiness/spec.md`

## Summary

Group A of the improvement round from the first consumer (zh-pvp-server, framework `v0.6.0`), closing FG-010, FG-001,
FG-006, FG-003, FG-004, FG-002, FG-007, FG-011, FG-012 and FG-025 before tag `v0.7.0`.

- **Dev profile lockdown (FG-010, HIGH).** One decision `GameServerOptions.AllowsDevProfile` (= `AllowsDevelopmentTools`)
  read by three independent layers: startup validation of endpoints *and* registered commands, an explicit per-profile
  pipeline that refuses Dev with 403 before any authentication (unknown profile → 403), and the runner refusing Dev
  commands on execute and replay paths before limiter, receipt read and transaction.
- **Consumer test kit (FG-001, FG-006).** New project `src/GameServer.TestKit` (no test framework, no sample): HTTP,
  identity, store and GM-browser helpers moved out of `tests/Shared`, an in-process `TestHost` with Identity-only default
  composition, `LocalFlavorFactory<TEntryPoint>` applying a documented settings set, `TestGameConfig`, and
  `CommandContextDouble` — a decorator over the runner's own internal `CommandContext` (shared rules by construction, plus
  a conformance test) with `CheckCommit` built on rules extracted from the runner.
- **Request event, outcome feature, console (FG-025).** An outermost framework middleware installs a public
  `IRequestOutcomeFeature`, framework writers fill it (`ErrorResponseWriter`, command endpoints, config), and one structured
  event per request is written to category `GameServer.Requests` (on by default in Development/Test, off in
  Staging/Production — FR-086 decision). An optional bounded Serilog console sink renders it as one line.
- **Build sealing (FG-003, FG-004).** Empty root `Directory.Build.targets`; `IsTestProject` from the path relative to the
  framework root; an evaluation test under a hostile `tests/` parent.
- **Reusable `run-local` (FG-002).** Host/profile/name parameters; host files resolved by a small framework tool
  `tools/GameServer.LocalRun` shared by the PowerShell and bash scripts; default invocation unchanged.
- **Version (FG-007).** `0.7.0` declared once in `Directory.Build.props` (= Unity package), informational version with
  the git sha, `GameServerVersion`/`GameServerRuntimeInfo` API, startup line, OTel `service.version` +
  `gameserver.framework.version`, GM `GET /gm/v1/ops/version` (`ops.read`).
- **Error band + docs (FG-011, FG-012).** Band 50000–89999 reserved for games (`ErrorCodeBands`, architecture test);
  English consumer guide, updated module-authoring/environments guides, PCR, template README and HTML pages stamped with
  the framework version instead of a commit.

Decisions: [research.md](research.md) (R1–R18); schemas and settings: [data-model.md](data-model.md); contracts:
[public-api.md](contracts/public-api.md), [request-log-and-console.md](contracts/request-log-and-console.md),
[gm-ops-version.md](contracts/gm-ops-version.md), [run-local-cli.md](contracts/run-local-cli.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403), PowerShell 5.1/7 and bash ≥ 4.4 for the scripts

**Primary Dependencies**: unchanged, plus an explicit `PackageVersion` for `Serilog.Sinks.Console` (already a dependency
of Serilog.AspNetCore 10.0.0). The kit uses `Microsoft.AspNetCore.Mvc.Testing` (already pinned, 10.0.14).

**Storage**: no change (no collection, index, document or wire change)

**Testing**: xUnit v3 + Shouldly; `TestHost`; `WebApplicationFactory` through `LocalFlavorFactory`; architecture tests;
`dotnet msbuild` evaluation in a temp layout; script tests (fast checks always, start/stop opt-in
`GAMESERVER_SCRIPT_TESTS=1`); opt-in performance comparison `GAMESERVER_PERF=1`; full suite incl. Couchbase

**Target Platform**: Linux containers (beta/uat/live), Windows/macOS/Linux developer machines, game repositories that
consume the framework as a git submodule

**Project Type**: web-service framework + sample host + consumer test kit

**Performance Goals**: request path +≤ 1 ms p95 with request event and console on (SC-008); zero blocking on log sinks

**Constraints**: OPS-09 (made stricter), SEC-07/08, LOG-04/05/06, OBS-02/04, ARC-08, API-05/11, PCR-01, TST-01,
constitution III/VI/VII; no NuGet packaging (FG-005/008/009 deferred); no player wire, document or GM response change
except the additive GM version read

**Scale/Scope**: 1 new library project (kit), 1 new tool project, 1 consumer fixture (3 projects); ~10 new public
types; 5 new settings; 1 GM endpoint; 7 breaking changes (BC-1..BC-7)

## Overview

```text
                         ┌──────────────── consumer middleware (before UseGameServer) reads IRequestOutcomeFeature
HTTP ─► RequestLogMiddleware (NEW, outermost: install feature · stopwatch · finalize · event "GameServer.Requests")
          └► ForwardedHeaders ► ExceptionMiddleware ► Correlation ► Routing ► GM CORS ► Gate
               └► ProfileEnforcementMiddleware ── profile Dev && !AllowsDevProfile ─► 403 FORBIDDEN (no auth)   [layer 2]
                     explicit: Public | Player | Gm | Dev(allowed) | other ─► 403
                     └► timeouts ► rate limiter ► endpoint
                          ├ command endpoints ─► outcome(code, replayed, op) ─► ICommandRunner
                          │                                                      └ EnsureActorAllowed: Dev && !AllowsDevProfile ─► FORBIDDEN [layer 3]
                          ├ config endpoint ─► outcome(notModified)
                          └ ErrorResponseWriter (every error) ─► outcome(code)
UseGameServer: ModuleRegistry.Validate (Dev command) + EndpointSecurityValidator (Dev endpoint)                   [layer 1]
                                   └──────────── all read GameServerOptions.AllowsDevProfile (FR-006) ───────────┘

ILogger ─► Serilog (AddObservability) ─► Async(file, compact JSON)  [unchanged]
                                     └► Async(console, ConsoleLineFormatter)  [NEW, optional, dropping]

GameServerRuntimeInfo (Platform) ─► startup line · OTel resource · GM GET /gm/v1/ops/version
Directory.Build.props: Version 0.7.0, relative IsTestProject │ Directory.Build.targets (seal, empty)

GameServer.TestKit ─► TestHost · LocalFlavorFactory<T> · HTTP/identity/GM helpers · TestGameConfig
                  └► CommandContextDouble ─(IVT)─► internal CommandContext + CommitRules (shared with runner)
run-local.ps1|.sh ─► tools/GameServer.LocalRun describe ─► launchSettings + appsettings layering + global.json
```

Style: unchanged modular monolith; ASP.NET feature + middleware for the per-request outcome; ports-and-adapters log
sinks; the test kit is a framework-owned library that reuses internals through `InternalsVisibleTo` instead of copying
rules.

## Module breakdown

| Module (assembly / namespace) | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Core `GameServer.Errors.ErrorCodeBands` | Band constants | — | enforcement (test) |
| Platform `GameServerOptions.AllowsDevProfile` | The single Dev decision | `AllowsDevelopmentTools` | where it is enforced |
| Platform `RequestLogSettings` | Request-log settings + validation | options | writing events |
| Platform `GameServerVersion`, `GameServerRuntimeInfo` | Versions, instance id rule, runtime identity | assembly attributes, config | how they are shown |
| Platform `Commands.CommitRules` (internal) | Commit code + audit rules (messages) | registry, descriptor | throwing (runner) / reporting (kit) |
| Platform `CommandRunner` | + Dev refusal, replay metric | `AllowsDevProfile`, `CommitRules` | — |
| Platform `ModuleRegistry` | + Dev command rule | options | endpoints |
| Platform.Web `Validation.EndpointSecurityValidator` | + Dev endpoint rule | options | commands |
| Platform.Web `Middleware.ProfileEnforcementMiddleware` | Explicit per-profile auth; Dev refusal before auth | options, schemes | outcome finalization |
| Platform.Web `Requests.*` | Outcome feature, finalization, request event | correlation, endpoint metadata, claims | sinks, formatting |
| Platform.Web writers (`ErrorResponseWriter`, command factories, config endpoint) | Fill the outcome | `Requests` | logging |
| Platform.Observability console sink + `ConsoleLineFormatter` | Optional bounded console output | Serilog, `LogSinkMonitor` | event content |
| Platform.Observability telemetry | + version resource attributes | `GameServerRuntimeInfo` | — |
| Gm.Adapters `GmModule` | + `GET /gm/v1/ops/version` | `GameServerRuntimeInfo` | version computation |
| `GameServer.TestKit` | Consumer test support (§US2) | Platform.Web, Platform.Testing, Identity/Gm adapters, Mvc.Testing; IVT into Platform | unit-test framework, samples |
| `tools/GameServer.LocalRun` | Resolve a host's launch profile, flavor settings, SDK | ASP.NET shared framework (configuration) | starting hosts |
| `tools/local/run-local.{ps1,sh}` | Build, start, wait, banner, stop | `GameServer.LocalRun` | reading JSON themselves |
| `tests/ConsumerFixture` | Consumer-shaped repo inside the framework | TestKit, framework projects by path | framework rules |
| `tests/Shared` (framework only) | Sample composition (`GameHttp`, `SampleTestHost`), file-store test helper | TestKit, samples | consumer API |

No circular dependency: Core ← Platform ← Platform.Web ← {Observability, modules' adapters} ← TestKit; TestKit is
referenced only by test projects and the fixture's tests (architecture test: SampleHost and every `src`/`samples`
project never reference it).

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | Runner unchanged except an earlier refusal; the double never writes and reuses the runner's context; no new write path | PASS |
| II Explicit registration | Dev modules still registered explicitly, now refused per environment at startup (aggregated report); kit composition chosen by the caller; no discovery (the band test discovers module *types* in tests only) | PASS |
| III Security deny-by-default | Dev refused outside Development/Test at three layers; unknown profile → 403 (no fallback branch); request event excludes secrets (scan test); version only behind `ops.read` | PASS (strengthened) |
| IV Async, bounded load | Request event and console go through bounded dropping queues (10 000 / 1 000), numeric and in PCR; no sync I/O on the request path; scripts unchanged in timeouts | PASS |
| V Stable contract | Additive GM endpoint in OpenAPI + PCR gm; event fields, outcome semantics, settings, band, kit API in PCR §19; 7 breaking changes listed with migration steps | PASS |
| VI Multi-layer tests | Unit (validator, middleware, formatter, resolver, double), host (layers, request log, kit), conformance (double ↔ runner), architecture (band, kit deps, seal, version), consumer fixture, opt-in script/perf; Couchbase suite unchanged and run. Gap carried: no CI (CI-01), `run-local.sh` on macOS/Linux manual | PASS (gaps pre-existing) |
| VII Observability | One event per request with correlation and trace id; version in log, telemetry and GM; console for developers; drops counted | PASS |

Re-check after design: unchanged.

## Design decisions (details in research.md)

| # | Decision | Research |
| --- | --- | --- |
| D1 | One property `GameServerOptions.AllowsDevProfile` (= `AllowsDevelopmentTools`) is the only Dev decision; no setting | R1 |
| D2 | Layer 1: `EndpointSecurityValidator` (endpoints) + `ModuleRegistry.ValidateCommands(options)` (every registered Dev command, also service-only / no route / `--provision`), new messages naming environment, environment id and fix | R2 |
| D3 | Layer 2: Dev refused in `ProfileEnforcementMiddleware` right after profile lookup, before CSRF, cache header, body limit and any `AuthenticateAsync`; Warning without header values; exhaustive profile switch, unknown → 403 | R3 |
| D4 | Layer 3: `CommandRunner.EnsureActorAllowed` instance method, `Dev => AllowsDevProfile`, before limiter on execute and `TryReplayAsync`; replay refusal counted in `command_results_total` | R4 |
| D5 | Layer tests: pure validator; middleware unit with recording auth service; host tests flip the singleton deployment environment after startup ("validator bypassed"); flavor matrix | R5 |
| D6 | `GameHttp.FullGame` adds `CheatModule` only when development tools are allowed; old-message tests updated | R6 |
| D7 | New `src/GameServer.TestKit` (namespace `GameServer.TestKit`); five helpers move out of `tests/Shared`; framework tests keep sample defaults through `SampleTestHost` (mechanical rename of 110 call sites) | R7 |
| D8 | `LocalFlavorFactory<TEntryPoint>` subclass of `WebApplicationFactory` with one temp root and the documented `LocalFlavorSettings` (7 FR-012 keys + console off) | R8 |
| D9 | `CommandContextDouble` decorates the internal `CommandContext` (IVT to the kit), detached `ITransactionContext` stub, receipt id = `ReceiptIds.Compute` with a fixed default request id; `CommitRules` extracted from the runner; conformance test | R9 |
| D10 | `TestGameConfig` builds real `GameConfig` (same version hash as the loader) | R10 |
| D11 | Outermost `RequestLogMiddleware` + public `IRequestOutcomeFeature`; writers set code/flags; finalization rules `OK`/`ABORTED`/`INTERNAL`/`HTTP_{n}`; event category `GameServer.Requests`, PascalCase fields, levels by status; `GameServer:RequestLog` (`Enabled` null = by environment, `ExcludedPaths`) | R11 |
| D12 | Console sink = Serilog console in `WriteTo.Async(blockWhenFull:false)` with `ConsoleLineFormatter`, `Observability:Logging:Console`, sample Local/Dev on | R12 |
| D13 | Empty root `Directory.Build.targets`; `IsTestProject` via `MakeRelative(...)` starts with `tests/`; evaluation test + manual full build script | R13 |
| D14 | `<Version>` from `GameServerFrameworkVersion` 0.7.0 in props; Source Link sha; `GameServerVersion`/`GameServerRuntimeInfo`; one instance-id rule; startup line; OTel attributes; GM `/gm/v1/ops/version` | R14 |
| D15 | `run-local` host parameters; resolver tool `tools/GameServer.LocalRun describe`; no new exit code; fast checks always, start/stop opt-in | R15 |
| D16 | `ErrorCodeBands` in Core + architecture test over framework-owned module types; docs example → template range 90000–90099 | R16 |
| D17 | `tests/ConsumerFixture` emulates a game repo (own props), in `GameServer.slnx` | R17 |
| D18 | English consumer guide; Vietnamese pages updated in place; stamps name the framework version; release-notes draft `docs/releases/v0.7.0.md` | R18 |

## Design per user story

### US1 — Dev profile only where development tools are allowed (FG-010; FR-001..FR-008)

| Layer | File | Check | Message / answer |
| --- | --- | --- | --- |
| Decision | `src/GameServer.Platform/GameServerOptions.cs` | `public bool AllowsDevProfile => AllowsDevelopmentTools;` | — |
| 1a endpoints | `src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs` (line 86) | `!options.AllowsDevProfile && profile == Dev` | `[endpoint POST /api/v1/cheat/grant] uses profile Dev in deployment environment 'Staging' (environment id 'beta'). Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09, SEC-08).` |
| 1b commands | `src/GameServer.Platform/Registry/ModuleRegistry.cs` (`ValidateCommands(options, errors)`) | every registered command with `Profile == Dev` | `[module cheat] command 'cheat.grant' has profile Dev in deployment environment 'Staging' (environment id 'beta'). Fix: … (OPS-09).` |
| 2 pipeline | `src/GameServer.Platform.Web/Middleware/ProfileEnforcementMiddleware.cs` | after the profile lookup, before CSRF/body/auth | 403 `FORBIDDEN` (error contract) + Warning `Dev endpoint {Method} {Route} refused in deployment environment {DeploymentEnvironment} ({EnvironmentId}).`; switch: `Public`/`Player`/`Gm`/`Dev`, `_ =>` 403 |
| 3 runner | `src/GameServer.Platform/Commands/CommandRunner.cs` (`EnsureActorAllowed`) | `Dev => options.AllowsDevProfile` (player or GM actor) | `GameServerException(FORBIDDEN, "Dev command 'cheat.grant' refused in deployment environment 'Staging' (environment id 'beta').")`; metric on both paths |

Unchanged in Development/Test: Dev endpoints authenticate with player schemes then GM schemes, ledger source `cheat`
(US1-5). Doc strings of `MapDevCommand`/`AsDevEndpoint` and the PCR/guide/HTML texts change from "forbidden in
Production" to "only where development tools are allowed (local, dev, tests)".

Framework tests: `GameHttp.FullGame` conditional (D6); `ConfigAndCheatTests` composes `CheatModule` explicitly for the
Staging and Production failure cases; `StartupValidationTests:83` asserts the new text.

### US2 — Consumer test kit and command-context double (FG-001, FG-006; FR-010..FR-026)

**Project** `src/GameServer.TestKit/GameServer.TestKit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <ProjectReference Include="..\GameServer.Platform.Web\GameServer.Platform.Web.csproj" />
    <ProjectReference Include="..\GameServer.Platform.Testing\GameServer.Platform.Testing.csproj" />
    <ProjectReference Include="..\GameServer.Modules.Identity.Adapters\GameServer.Modules.Identity.Adapters.csproj" />
    <ProjectReference Include="..\GameServer.Modules.Gm.Adapters\GameServer.Modules.Gm.Adapters.csproj" />
  </ItemGroup>
</Project>
```

Consumer reference (game repo): `<ProjectReference Include="$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj" />`
plus `Microsoft.AspNetCore.Mvc.Testing` in the test project if [VERIFY] V4 shows the content-root targets do not flow.

**Public surface**: [public-api.md §6](contracts/public-api.md#6-consumer-test-kit-srcgameservertestkit-namespace-gameservertestkit).
Contents and what moves out of `tests/Shared`: research R7 table. `tests/Shared` keeps `GameHttp.cs`, `config/`, and
gains `SampleTestHost.cs`, `FileStoreTestSupport.cs`, `GlobalUsings.cs` (`global using GameServer.TestKit;`); every
framework test project that links `tests/Shared/*.cs` adds a `ProjectReference` to the kit. No file in the kit depends
on `Sample.*` or `SampleGame` (architecture test).

**Kit `TestHost`** = today's `TestHost` with: default modules Identity only; game config `config` argument →
`GameServer:ConfigPath` setting → `TestGameConfig.Empty`; structured `LogEntries` (category, level, event id, message,
properties, exception) alongside `Logs`; file store created through kit-internal code; otherwise identical behaviour
(shared store/clock for two instances, file store switch `GAMESERVER_TEST_STORE`, in-memory analytics sink, test
authentication, `beforeUseGameServer`/`afterUseGameServer` hooks).

**`LocalFlavorFactory<TEntryPoint>`**: settings set in data-model §3.4; root deleted on dispose (unless
`LocalFlavorOptions.Folder`); `SampleHostSmokeTests` moves onto it (including the restart test through `Folder`);
`LocalFlavorSettingsTests` checks every key exists on an options type and is listed in the consumer guide.

**`CommandContextDouble` contract** (FR-020..FR-026):

| Aspect | Behaviour |
| --- | --- |
| Build | `Create(operation, modules…)`: registry from `Describe()`; unknown operation or unregistered aggregate in Touches ∪ Reads → `ArgumentException` naming it |
| Configure | `AsPlayer`, `AsGm`, `WithActor`, `At`, `WithConfig`, `WithRequestId`/`WithReceiptId`, `Seed<T>`, `SeedClaim`; frozen at the first `ICommandContext` call (later configuration throws) |
| Defaults | data-model §6 |
| Read | missing aggregate → descriptor default state; seeded → its state; `Set` then `Get` → new state (runner context semantics) |
| Rules | the runner's `CommandContext` itself: `Set` outside Touches, `Get`/`Set` outside Touches ∪ Reads, state type mismatch, claim limit, claim in `receipts`/`pa_*` → `InvalidOperationException` with the runner's message; duplicate claim in one run → false; seeded claim → false, nothing recorded |
| Ids | `NewId`/`NewSeed` = runner values for the same receipt id |
| Records | `Writes` (first-`Set` order, latest state), `Written<T>(id)`, `Outbox` (`OutboxEntry`, includes `AddAudit` and `AddAnalyticsEvent` entries), `Claims` (collection, key, attributes), `ReceiptId`, `Descriptor` |
| Commit check | `CheckCommit(outcome)`: uncatalogued commit code, catalogued-name-but-different instance, non-200 commit code, GM command without exactly one audit, non-GM command with audit → runner messages; rejection → empty |
| Not simulated | transactions, retries, receipts, outbox relay (spec assumption) |

**Platform change for the double**: `internal static class CommitRules` (`CheckCode`, `CheckAudit`) extracted from
`CommandRunner.EnsureCommittableCode`/`EnsureAudit` (same texts); `InternalsVisibleTo GameServer.TestKit` in
`GameServer.Platform.csproj`.

### US3 — Request event, outcome feature, console (FG-025; FR-080..FR-089)

Contract: [request-log-and-console.md](contracts/request-log-and-console.md); fields: data-model §1–§2.

| Piece | File | Design |
| --- | --- | --- |
| Feature | `src/GameServer.Platform.Web/Requests/IRequestOutcomeFeature.cs` | public interface + `RequestOutcomeExtensions.RequestOutcome(this HttpContext)` + `RequestResultCodes` + `RequestLog` (category, event id) |
| Implementation | `Requests/RequestOutcome.cs` (internal sealed) | mutable fields; `Finalize(status, aborted, exception)` |
| Middleware | `Requests/RequestLogMiddleware.cs` (internal) | outermost (`UseGameServer` registers it first); install feature → `Stopwatch.GetTimestamp()` → `await next` in try/catch/finally → finalize → `if (enabled && !excluded && logger.IsEnabled(level)) Write(...)` inside its own try/catch |
| Event state | `Requests/RequestLogEntry.cs` (internal readonly struct, `IReadOnlyList<KeyValuePair<string, object?>>`) | 14 fields + `{OriginalFormat}`; no LINQ, no closures |
| Writers | `Errors/ErrorResponseWriter.cs`, `Commands/CommandEndpointFactory.cs` (`MapCommand`), `Commands/GmCommandEndpointFactory.cs`, `Endpoints/CoreEndpoints.cs` (config) | one assignment each before the response is written |
| Settings | `src/GameServer.Platform/GameServerOptions.cs` (`RequestLog`, `RequestLogSettings`) | validated in `UseGameServer` (`[request-log] …`); explicit `Enabled=true` outside Development/Test → startup warning |
| Console | `src/GameServer.Platform.Observability/ObservabilityOptions.cs` (`ConsoleLoggingSettings`), `ObservabilityExtensions.AddLogging`, `ConsoleLineFormatter.cs` (new), `ObservabilityModule` startup check (warning outside Development/Test) | `WriteTo.Async(console, BufferSize, blockWhenFull:false, LogSinkMonitor.Instance)`; validation of level and buffer joins the observability errors |
| Sample | `samples/SampleHost/appsettings.Local.json`, `appsettings.Dev.json` | `Observability:Logging:Console:Enabled: true` |

### US4 — Sealed build (FG-003, FG-004; FR-030..FR-033)

- `Directory.Build.targets` (new, root): `<Project>` with a comment only (+ the V1 fallback target if needed).
- `Directory.Build.props`:

```xml
<_GameServerRelativeDir>$([MSBuild]::MakeRelative('$(MSBuildThisFileDirectory)', '$(MSBuildProjectDirectory)').Replace('\', '/').ToLowerInvariant())</_GameServerRelativeDir>
<IsTestProject Condition="$(_GameServerRelativeDir.StartsWith('tests/'))">true</IsTestProject>
```

- `BuildSealingTests` (Architecture.Tests) per research R13; `tools/local/check-build-seal.ps1` for the full Release
  build (SC-004), run once before close.
- Consumer guide FR-033 list: sealed = `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
  `.editorconfig` (`root = true`); still from the consumer = `global.json` of the directory `dotnet` runs in,
  `nuget.config` hierarchy (manual sync of FG-008).

### US5 — Reusable `run-local` (FG-002; FR-040..FR-046)

Contract: [run-local-cli.md](contracts/run-local-cli.md). Parameters `-HostProject`/`--host-project`,
`-LaunchProfile`/`--launch-profile` (default `local`), `-Name`/`--name`; everything the scripts read comes from
`dotnet run --project tools/GameServer.LocalRun -- describe …` (data-model §7). The scripts keep their 011 structure;
the hardcoded `samples\SampleHost`, `SampleHost.csproj`, `local`, `5080`, `10.0.403`, `samples/SampleHost/logs` and the
sample banner become values from the description (`isSample=true` keeps the sample banner). The 011 contract §4 gets a
"superseded by `specs/013-consumer-readiness/contracts/run-local-cli.md`" note.

Repeatable check: `RunLocalScriptTests` + `LocalRunDescribeTests` (Architecture.Tests, "repository and tooling" tests):
fast exit-code checks always, start/stop for the default and the fixture host opt-in (`GAMESERVER_SCRIPT_TESTS=1`).

### US6 — Version (FG-007; FR-050..FR-055)

- Props: `GameServerFrameworkVersion` 0.7.0 → `Version`; `sdk/unity/com.gameserver.client/package.json` → `0.7.0`.
- `src/GameServer.Platform/GameServerRuntimeInfo.cs` (new): `GameServerVersion`, `GameServerRuntimeInfo` with
  `ResolveInstanceId`; `ObservabilityOptions.ResolveInstanceId()` delegates.
- `GameServerBuilderExtensions.AddGameServer`: builds `GameServerRuntimeInfo` (game assembly from
  `builder.Environment.ApplicationName`, instance id from `Observability:InstanceId`), registers it, exposes
  `GameServerBuilder.Runtime`.
- `UseGameServer`: startup line after validation succeeded (data-model §4.3).
- `ObservabilityExtensions.AddTelemetry`: `AddService(..., serviceVersion: runtime.GameVersion, ...)` +
  `gameserver.framework.version`.
- `GmModule`: `GET /gm/v1/ops/version` ([gm-ops-version.md](contracts/gm-ops-version.md)), `GmVersionResponse`;
  regenerate `docs/api/gm-openapi.json`; PCR gm tables.
- Release procedure (PCR platform §19 + consumer guide): annotated tag `v{GameServerFrameworkVersion}`; bump the props
  value and `package.json` together (`VersionTests`).

### US7 — Error band and consumer documentation (FG-011, FG-012; FR-060..FR-073)

- `src/GameServer.Core/Errors/ErrorCodeBands.cs`; `ErrorCodeBandTests`; module-authoring example → 90000–90099 with
  "a game picks 50000–89999".
- `docs/guides/consumer-guide.md` (new, English) — sections: 1 Repository layout and submodule pinned to an annotated tag
  (no branch, no `--remote`); 2 Build files: mirror vs sealed (FR-033), CPM import and pinned versions (FG-008 manual sync
  remains); 3 Project references; 4 Host composition (copy of SampleHost until FG-009; register Dev modules only when
  development tools are allowed); 5 Tests: test kit, `TestHost`, `LocalFlavorFactory` and its settings list,
  `CommandContextDouble` and `CheckCommit`, outcome feature in tests; 6 Local run with `-HostProject`; 7 Versions (API,
  startup line, OTel, GM read); 8 Request log and console (settings per flavor, volume); 9 Error codes (band); 10 Unity
  SDK by UPM URL `…#v0.7.0` (game `.proto` codegen: "coming in slice 014"); 11 Bump checklist (read release notes, bump
  submodule to the tag, diff the mirrored build files, run tests, remove workarounds listed under "Consumer impact").
- `docs/html/02-them-command.html`: §4.2 band; §4.6 and §4.8/§5 step 7 as two variants (in-repo: `GameServer.slnx`,
  `tests/Shared`, `SampleTestHost`; consumer repo: `$(GameServerRoot)` references, kit `TestHost`,
  `CommandContextDouble` handler test); link to the consumer guide; footer/meta stamp "Đúng với framework v0.7.0".
- Other pages and guides: research R18 list.

## Spec points resolved and deviations

**Status: FR-086 resolved in spec.md (orchestrator default 2026-11-03, owner may override).** The rows below are plan
decisions within the spec's latitude; none changes a requirement's intent.

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| SD1 | FR-086 default `[NEEDS CLARIFICATION]` | Request event on by default in Development/Test (local, dev, test hosts), off in Staging/Production; `GameServer:RequestLog:Enabled` explicit switch + startup warning | Volume ~13–17 GB/instance/day at 500 req/s would also evict other logs from the default 3 GiB retention within hours |
| SD2 | FR-081 "field names fixed in the plan" | PascalCase properties (`HttpMethod`, `ResultCode`, …) | Same convention as the existing enrichment (`GameId`, `InstanceId`, `RequestId`) in the compact JSON files |
| SD3 | FR-082 sanitize actor ids "from test-authentication headers" | Every actor id is cleaned and cut at 64 | Same cost, no branch on the scheme, safer |
| SD4 | US3 levels unspecified | Information < 400 (business codes at 200 included), Warning 4xx/aborts, Error 5xx | A business rejection is normal gameplay; `LevelOverrides … = Warning` then yields exactly the failures |
| SD5 | Edge case "result code … HTTP status otherwise" | `HTTP_{status}` (e.g. `HTTP_418`) | Distinguishable from catalog codes and from numbers in other fields |
| SD6 | FR-012 list of 7 settings | 7 + `Observability:Logging:Console:Enabled=false` | The rule of FR-012 itself: this slice adds a setting with side effects (console output) |
| SD7 | FR-007 "middleware with the validator bypassed" | Host tests flip the singleton deployment environment after startup (plus a unit test of the middleware) | A Staging host cannot start on the in-memory store; the flip is exactly "routes/commands the validator did not refuse" |
| SD8 | FR-032 "build or evaluate" | Default suite evaluates every framework project under a hostile `tests/` parent; full Release build there is `tools/local/check-build-seal.ps1` (manual, recorded) | A full build copy costs minutes per test run |
| SD9 | FR-041 "automated where the platform allows" | Exit-code checks always; start→ready→stop opt-in (`GAMESERVER_SCRIPT_TESTS=1`) | Starting a script builds the host in Debug inside the test run (slow, conflicts with parallel builds) |
| SD10 | FR-055 fields | + `gameId` in the GM response | Same identity triple as the startup line; additive |
| SD11 | FR-061 "example moves out of 50000–50099" | Example uses the template's 90000–90099 and tells the reader to pick a range in 50000–89999 | A second invented range outside the band would invite copying it into games |
| SD12 | FR-013 "shared test host built on the kit" | Framework tests call `SampleTestHost` (forwarders with sample defaults) → kit `TestHost` | Keeps 110 call sites on their composition with a mechanical rename |

## Data flow

**Request (any route).** `RequestLogMiddleware` installs `RequestOutcome`, timestamps → framework pipeline: refusals and
errors set `ResultCode` in `ErrorResponseWriter`; command endpoints set code/replayed/operation; config sets
not-modified → pipeline returns (or throws, or the client aborted) → finalize → consumer middleware outside reads the
final outcome → if enabled and not excluded: `ILogger("GameServer.Requests").Log(level, EventId 1, RequestLogEntry)` →
Serilog bounded queue → file (compact JSON) and, if enabled, console (one line).

**Dev request outside Development/Test.** Routing finds the Dev endpoint → `ProfileEnforcementMiddleware` → 403 via
`ErrorResponseWriter` (outcome `FORBIDDEN`) + Warning → no scheme, no CSRF, no handler. A Dev command reaching the runner
by any other path → `EnsureActorAllowed` throws `FORBIDDEN` before the limiter → metric → envelope.

**Startup.** `AddGameServer` resolves `GameServerRuntimeInfo` → `AddObservability` uses it for the OTel resource and
the console sink → `UseGameServer` registers the request middleware first, validates (registry incl. Dev commands,
request-log settings, endpoints incl. Dev) → on success logs the version line → host starts.

**Handler unit test.** `CommandContextDouble.Create(op, modules)` → configure → handler runs against the wrapped
`CommandContext` (rules identical to the runner) → test reads `Writes`/`Outbox`/`Claims` → `CheckCommit(outcome)`.

**Local run.** script → SDK check → `GameServer.LocalRun describe` → port probe → build → `dotnet run --launch-profile` →
readiness poll → banner → host stdout (request lines via the console sink) → Ctrl+C → graceful stop.

**State ownership.** Outcome: per `HttpContext`, framework-owned (consumer endpoints may set their own). Runtime info:
one immutable singleton per host. Dev decision: derived from the validated deployment environment, never configured.
Double: one per test, configuration frozen at first use. Version: the props value is the single source; the Unity
package mirrors it (test).

## Performance

- Hot path, logging off (Staging/Production default): one `RequestOutcome` allocation (~64 B), one
  `Stopwatch.GetTimestamp()`, a few field writes, finalization; `logger.IsEnabled` short-circuits the event.
- Logging on: one struct state per event (no closure, no LINQ); the Serilog bridge allocates the `LogEvent` and boxes
  ~6 value properties (~1–2 KB per event); formatting and I/O happen on the async sink workers, never on the request
  thread. Target SC-008 ≤ +1 ms p95 (opt-in `RequestLogPerformanceTests`, `GAMESERVER_PERF=1`, console sink on a null
  writer).
- Console sink: separate queue (1 000), drops counted; a slow terminal never back-pressures requests.
- Route/actor cleaning only for unmatched paths and actor ids (bounded 120/64 chars, `string.Create`).
- Double and kit: test-only code, no production cost. Runner: one extra boolean read.

## Seams for later slices and extension points

- **014 (route catalog, codegen, FG-027).** The kit is additive by rule (new types/optional last parameters only), so
  the FG-027 mapping check ships as a new kit type (`AggregateMappingCheck` or similar) without breaking consumers; the
  request event's `Route` (template) can gain a stable route name field from the catalog (additive property); the consumer
  guide keeps a "Game `.proto` codegen for Unity — slice 014" placeholder section.
- **015 (config).** `TestGameConfig.FromJson/FromDirectory` use the real loader/version algorithm, reusable by
  config-validation tests; `NotModified` is set by the config endpoint from its own comparison, so it follows any
  `config_version` change of FG-024 automatically.
- **016 (login, limits, request ids).** The double takes `MaxClaimMarkers`/Touches from the same `CommandDescriptor`
  and the same `CommandContext` as the runner, so FG-016 limit changes reach both; `RequestId` in the event is the
  correlation id, which FG-022 may let clients supply; a login "new player" flag (FG-013) can be added to the outcome as
  an optional property.
- **Deferred FG-005/FG-008/FG-009.** The kit references `GameServer.Platform.Testing` for the stores and exposes only
  `InMemoryDocumentStore`/`FakeClock`/`InMemoryAnalyticsSink` types (FR-014): moving the stores changes namespaces or a
  reference. Splitting the kit (double vs hosting) and NuGet packaging come with FG-005/FG-008. Build sealing is the part
  of consumer build hygiene that needs no packaging.
- **Deferred Staging cheat switch (R13 of §17).** Extend `AllowsDevProfile` only; never Production.
- **Other extension points.** New request-event property (additive); new excluded path (setting); another sink
  (Serilog `WriteTo`, operational logs only); consumer per-request behaviour via the outcome feature.

## Project Structure

### Documentation (this feature)

```text
specs/013-consumer-readiness/
├── spec.md                               # FR-086 resolved (orchestrator default)
├── plan.md                               # this file
├── research.md                           # R1..R18
├── data-model.md                         # request event, outcome, settings, versions, band, double defaults, resolver output
├── contracts/public-api.md               # C# surface (Core, Platform, Platform.Web, Observability, GM, TestKit)
├── contracts/request-log-and-console.md  # event, outcome semantics, console, flavor defaults, volume
├── contracts/gm-ops-version.md           # GET /gm/v1/ops/version
├── contracts/run-local-cli.md            # supersedes 011 operations-cli §4
└── tasks.md                              # next stage (/speckit-tasks)
```

### Source Code (repository root)

```text
Directory.Build.props                                     # GameServerFrameworkVersion 0.7.0 → Version; relative IsTestProject
Directory.Build.targets                                   # NEW empty seal (FG-003)
Directory.Packages.props                                  # + Serilog.Sinks.Console version
GameServer.slnx                                           # + src/GameServer.TestKit, tools/GameServer.LocalRun, tests/ConsumerFixture/*
.gitignore                                                # /tests/ConsumerFixture/.local/
src/GameServer.Core/Errors/ErrorCodeBands.cs              # NEW
src/GameServer.Platform/
├── GameServer.Platform.csproj                            # InternalsVisibleTo GameServer.TestKit
├── GameServerOptions.cs                                  # AllowsDevProfile; RequestLog + RequestLogSettings
├── GameServerRuntimeInfo.cs                              # NEW GameServerVersion, GameServerRuntimeInfo (+ResolveInstanceId)
├── Commands/CommitRules.cs                               # NEW (extracted)
├── Commands/CommandRunner.cs                             # Dev refusal (instance EnsureActorAllowed), replay metric, CommitRules
└── Registry/ModuleRegistry.cs                            # Dev command rule (ValidateCommands(options, …))
src/GameServer.Platform.Web/
├── Validation/EndpointSecurityValidator.cs               # Dev rule + message
├── Middleware/ProfileEnforcementMiddleware.cs            # Dev refusal before auth; explicit switch, unknown → 403
├── Requests/IRequestOutcomeFeature.cs                    # NEW public feature, extensions, RequestResultCodes, RequestLog
├── Requests/RequestOutcome.cs                            # NEW internal implementation + finalization
├── Requests/RequestLogMiddleware.cs                      # NEW outermost middleware
├── Requests/RequestLogEntry.cs                           # NEW event state
├── Errors/ErrorResponseWriter.cs                         # outcome code
├── Commands/CommandEndpointFactory.cs                    # outcome code/replayed/operation; MapDevCommand doc
├── Commands/GmCommandEndpointFactory.cs                  # outcome code/replayed/operation
├── Endpoints/CoreEndpoints.cs                            # outcome NotModified
├── Routing/ApiModules.cs                                 # AsDevEndpoint doc
├── Hosting/GameServerBuilder.cs                          # GameServerRuntimeInfo registration, GameServerBuilder.Runtime
└── Hosting/GameServerApplicationExtensions.cs            # middleware first, RequestLog validation/warning, startup version line
src/GameServer.Platform.Observability/
├── GameServer.Platform.Observability.csproj              # + Serilog.Sinks.Console
├── ObservabilityOptions.cs                               # ConsoleLoggingSettings; ResolveInstanceId delegates
├── ObservabilityExtensions.cs                            # console sink, validation, OTel service.version + framework attribute
├── ConsoleLineFormatter.cs                               # NEW
└── ObservabilityModule.cs                                # startup check: console enabled outside Development/Test → warning
src/GameServer.Modules.Gm.Adapters/GmModule.cs            # VersionRoute, GmVersionResponse, endpoint
src/GameServer.TestKit/                                   # NEW consumer test kit
├── GameServer.TestKit.csproj
├── ProtobufHttp.cs · IdentityHttp.cs · TestStores.cs · GmBrowser.cs     # moved from tests/Shared
├── TestHost.cs · CapturedLog.cs                                         # moved + generalized
├── LocalFlavorFactory.cs · LocalFlavorSettings.cs
├── TestGameConfig.cs
├── CommandContextDouble.cs · RecordedWrite.cs · RecordedClaim.cs
└── DetachedTransaction.cs                                               # internal ITransactionContext stub
samples/SampleHost/appsettings.{Local,Dev}.json           # Observability:Logging:Console:Enabled true
sdk/unity/com.gameserver.client/package.json              # 0.7.0
tools/GameServer.LocalRun/                                # NEW resolver (GameServer.LocalRun.csproj, Program.cs, LocalHostDescription.cs)
tools/local/run-local.ps1 · run-local.sh                  # host parameters, resolver, neutral banner
tools/local/check-build-seal.ps1                          # NEW manual full-build seal check
tests/ConsumerFixture/                                    # NEW consumer-shaped repo
├── Directory.Build.props · Directory.Packages.props
├── src/Fixture.Game/ (module "fixture", command fixture.save, codes 50000–50099)
├── src/Fixture.Host/ (Program, appsettings.json + .Local.json, launchSettings local:5180, Version 1.2.3, config/base)
└── tests/Fixture.Tests/ (handler test with the double, local-flavor smoke test, version, outcome middleware)
tests/Shared/
├── (removed) ProtobufHttp.cs, IdentityHttp.cs, TestStores.cs, GmBrowser.cs, TestHost.cs
├── GameHttp.cs                                           # FullGame: CheatModule only when development tools are allowed
├── SampleTestHost.cs · FileStoreTestSupport.cs · GlobalUsings.cs        # NEW
tests/*/*.csproj (7 projects linking Shared)               # + ProjectReference GameServer.TestKit
tests/**/*.cs                                             # TestHost.StartAsync/BuildApp/DefaultSettings → SampleTestHost.*; namespace usings
tests/GameServer.Platform.Web.Tests/
├── DevProfileValidationTests.cs · DevProfileMiddlewareTests.cs · DevProfileRunnerTests.cs · DevProfileFlavorMatrixTests.cs
├── CommandContextDoubleTests.cs · CommandContextConformanceTests.cs · TestKitHostTests.cs · LocalFlavorSettingsTests.cs
├── RequestLogTests.cs · RequestOutcomeFeatureTests.cs · RequestLogPrivacyTests.cs
├── StartupValidationTests.cs                             # new Dev message
└── FileStoreHostTests.cs                                 # FileStoreTestSupport
tests/GameServer.Observability.Tests/
├── ConsoleSinkTests.cs · ConsoleLineFormatterTests.cs     # NEW (blocked writer, drops, format)
├── TelemetryTests.cs                                     # service.version + gameserver.framework.version
└── LoggingTests.cs                                       # request event in the compact JSON file, enrichment
tests/GameServer.Gm.Tests/
├── GmVersionEndpointTests.cs                             # NEW (ops.read, 401/403, no version on public routes)
└── OpenApiTests.cs                                       # committed docs/api/gm-openapi.json regenerated
tests/GameServer.Architecture.Tests/
├── GameServer.Architecture.Tests.csproj                  # + Template.Module.Adapters, tools/GameServer.LocalRun references
├── ArchitectureTests.cs                                  # TestKit in AllFrameworkAndModules; allowed ITransactionContext/FileDocumentStore; kit deps; no host references the kit
├── ErrorCodeBandTests.cs · VersionTests.cs · BuildSealingTests.cs       # NEW
└── RunLocalScriptTests.cs · LocalRunDescribeTests.cs                    # NEW (repository tooling)
tests/Sample.Counter.Tests/SampleHostSmokeTests.cs        # on LocalFlavorFactory<Program>; console line format smoke
tests/SampleGame.Tests/
├── ConfigAndCheatTests.cs                                # explicit CheatModule, Staging + Production, new message
├── SampleHostConfigurationFileTests.cs                   # console flag per flavor, no RequestLog key in flavor files
└── RequestLogPerformanceTests.cs                         # NEW opt-in GAMESERVER_PERF=1 (SC-008)
docs/guides/consumer-guide.md                             # NEW (English)
docs/guides/module-authoring.md · environments-and-local-run.md          # updated (Vietnamese)
docs/pcr/platform.md                                      # §8 band, §13 request event/console, §17 Dev rule, §19 slice 013 (+ breaking)
docs/pcr/gm.md                                            # version endpoint
docs/api/gm-openapi.json                                  # regenerated
docs/html/{index,01-gioi-thieu,02-them-command,03-deploy}.html          # Dev rule, request log, console line, version; stamp v0.7.0
docs/releases/v0.7.0.md                                   # NEW release-notes draft (group A)
templates/module/README.md                                # in-repo vs consumer paths, band
README.md                                                 # pointer to the consumer guide
specs/011-local-env/contracts/operations-cli.md           # §4 "superseded by" note
```

**Structure Decision**: one new library for consumers (`src/GameServer.TestKit`, under `src/` so analyzers apply and it
is never a test project), one new tool (`tools/GameServer.LocalRun`), one consumer fixture under `tests/ConsumerFixture`
with its own build files; everything else is a change in the existing Platform, Platform.Web, Observability and GM
projects. Repository-wide invariants (seal, version, band, scripts) live in Architecture.Tests.

## Test strategy

| Story / rule | Tests |
| --- | --- |
| US1 (FR-001..FR-007, SC-001) | `DevProfileValidationTests` (endpoint + registry rules × Development/Test/Staging/Production, ServiceOnly, `MapPlayerApi=false`, command without route, provision path through `ModuleRegistry.Validate(options, null)`); `DevProfileMiddlewareTests` (unit: 403, 0 `AuthenticateAsync`, `next` not called, Warning without header values; unknown profile → 403; host flip: bearer/test headers/GM header → 403, wallet/receipts/session unchanged, no resume analytics record); `DevProfileRunnerTests` (execute + `TryReplayAsync`, player/GM/system actors, 0 documents written, metric `FORBIDDEN`, Dev receipt not replayed after the flip); `DevProfileFlavorMatrixTests` (local/dev start with `CheatModule`; beta/uat/live fail with both lines; US1-5 Development behaviour unchanged incl. ledger source `cheat`) |
| US2 kit (FR-010..FR-014, SC-002) | `TestKitHostTests` (Identity-only default, empty config, `GAMESERVER_TEST_STORE=File`, `LogEntries`); `LocalFlavorSettingsTests`; `SampleHostSmokeTests` on the factory (temp root only, restart via `Folder`, two factories in parallel → distinct folders/ids, folder gone after dispose); consumer fixture `Fixture.Tests` (references only the kit; 0 files outside temp after run); architecture rules (kit deps, no host references it) |
| US2 double (FR-020..FR-026, SC-003) | `CommandContextDoubleTests` (build errors, defaults, seeds, records in order, audit/analytics extension entries, frozen configuration, `CheckCommit` 5 cases); `CommandContextConformanceTests` (≥ 9 rule cases + 25 id/seed samples through runner and double, identical outcomes and messages) |
| US3 (FR-080..FR-089, SC-007, SC-008) | `RequestLogTests` (scripted session on `TestHost`: guest login, snapshot, config get + not-modified, command OK, business rejection at 200, replay, 400, 401, 404, 405, 413, invalid Protobuf, gate maintenance, GM request, client abort, injected 500, health probes → exactly one event each with expected fields, none for probes, exclusion list, Staging default off via settings flip, levels); `RequestOutcomeFeatureTests` (consumer middleware before `UseGameServer` reads the same code for every request; consumer endpoint sets its own code; `afterExecute` sees partial values); `RequestLogPrivacyTests` (secret scan over all events: tokens, guest secrets, passwords, cookies, CSRF, purchase tokens, bodies, query strings; test-auth actor id with control chars/1 000 chars cleaned); `LoggingTests` (event in the compact JSON file with enrichment); `ConsoleSinkTests` (blocked writer: 2 000 requests OK, drops counted, bounded memory; file content unchanged with console on); `ConsoleLineFormatterTests`; `RequestLogPerformanceTests` opt-in; metric/event agreement for command routes |
| US4 (FR-030..FR-033, SC-004) | `BuildSealingTests` (evaluation under hostile parent `tests/`); `tools/local/check-build-seal.ps1` once (manual, recorded) |
| US5 (FR-040..FR-046, SC-005) | `LocalRunDescribeTests` (folder vs file, profile port/none/several URLs, flavor layering + env vars, comments, missing pieces → messages, nearest `global.json`); `RunLocalScriptTests` (fast exit 2/4 always; opt-in start/stop default + fixture host, banner, `-Reset`, exit 6 + tail); `run-local.sh` on macOS/Linux manual |
| US6 (FR-050..FR-055, SC-006) | `VersionTests` (props = package.json with a deliberate-mismatch message format test, assemblies stamped, sha present); fixture test (framework version through a consumer project reference, game version 1.2.3); startup line test on `TestHost`; `TelemetryTests` (resource attributes); `GmVersionEndpointTests` |
| US7 (FR-060..FR-073, SC-009) | `ErrorCodeBandTests`; `DocumentedConfigurationKeysTests` extended to the new keys; consumer-guide walkthrough by a reviewer on a fresh temp game repo (manual, every gap fixed and recorded in tasks.md) |
| Cross-cutting (FR-091) | Debug + Release build 0 warnings; full suite in-memory, `GAMESERVER_TEST_STORE=File`, Couchbase |

## Migration notes for consumers (v0.6.0 → v0.7.0, feeds the release notes)

| BC | What breaks | Steps for a game |
| --- | --- | --- |
| BC-1 | Dev endpoints and commands fail startup outside Development/Test (was Production only), one report line per endpoint and per command | 1. Register cheat/Dev modules only inside `if (gameServer.Options.AllowsDevelopmentTools)` (as SampleHost). 2. Start each non-development flavor once (`--provision` included) to confirm. 3. Composition tests that build Staging/Production hosts with a Dev module: expect the new failure or drop the module. 4. The `FRAMEWORK-GAP FG-010` markers can go; the condition stays as hygiene. |
| BC-2 | A Dev route mapped after `UseGameServer` answers 403 `FORBIDDEN` outside Development/Test; the runner refuses Dev commands from jobs/GM flows there | Same as BC-1; jobs or GM flows that call Dev commands in Staging must use a non-Dev command. |
| BC-3 | Startup message text changed (`uses profile Dev in Production` → `uses profile Dev in deployment environment '…' (environment id '…')`) | Tests matching the old text: match the endpoint label (`[endpoint POST /api/v1/cheat/grant]`) or the new text. |
| BC-4 | `tests/Shared/{ProtobufHttp,IdentityHttp,TestStores,GmBrowser,TestHost}.cs` removed (moved to the kit, namespace `GameServer.TestKit`); `TestStores.CreateFileStore` is no longer public; kit `TestHost` defaults to Identity only and an empty game config | 1. Delete the `<Compile Include="$(GameServerRoot)tests/Shared/…">` links. 2. Add `<ProjectReference Include="$(GameServerRoot)src/GameServer.TestKit/GameServer.TestKit.csproj" />` (keep `Microsoft.AspNetCore.Mvc.Testing` in the test project). 3. Replace `using GameServer.Tests.Shared;` with `using GameServer.TestKit;`. 4. Replace hand-written local-flavor factories with `LocalFlavorFactory<Program>` (data now under `{Root}/data`, use `factory.DataFolder`). 5. Replace a fake `ICommandContext` with `CommandContextDouble.Create(op, modules…)`. 6. Remove the `UPGRADE.md` sync lines for `tests/Shared` and the smoke test. |
| BC-5 | A consumer root `Directory.Build.targets` no longer applies to framework projects; framework test detection is relative to the framework root | Move anything injected into framework projects into the game's own projects; the game may now add its own root `Directory.Build.targets`; remove the `tests`-ancestor guard target (FG-004) and the related notes. |
| BC-6 | Framework assemblies carry `0.7.0.0` / informational `0.7.0+sha`; telemetry has `service.version` (= game version) and `gameserver.framework.version` | Update dashboards keyed on 1.0.0.0 or on a missing `service.version`; delete hand-written framework-tag stamping and the startup version line (`BuildInfo`, `AssemblyMetadata("FrameworkTag")`); compare a pinned tag with `GameServerVersion.Framework` if the game keeps that check. |
| BC-7 | One request event per request in Development/Test (default) and optional console output; more log volume where enabled | Nothing in Staging/Production unless enabled (`GameServer:RequestLog:Enabled=true`); budget ~300–400 B/request and raise file limits or ship logs first; delete the game's `RequestLog/*` middleware, setting and tests, enable `Observability:Logging:Console:Enabled` in the game's local flavor file; rebuild any game-specific per-request behaviour on `HttpContext.RequestOutcome()`. |

Non-breaking but recommended: call `framework/tools/local/run-local.ps1 -HostProject src/<Game>.Host -LaunchProfile
local -Name "<Game>"` instead of the copied scripts (keep game-only pre-checks as a thin wrapper, exit codes 8+); point
the game's error-code note to the framework band 50000–89999; replace duplicated consumer steps with links to
`docs/guides/consumer-guide.md`.

## Implementation phases (input for tasks.md)

1. **Foundations (blocking)** — `Directory.Build.props` (version, relative `IsTestProject`), `Directory.Build.targets`;
   `GameServer.TestKit` skeleton + slnx + IVT; `AllowsDevProfile`; `GameServerRuntimeInfo`/`GameServerVersion` + instance-id
   unification; `CommitRules` extraction (no behaviour change, existing tests green).
2. **US1 Dev lockdown** — validator, registry, middleware, runner; `GameHttp.FullGame`; message tests; layer + matrix tests.
3. **US2 kit** — move helpers; kit `TestHost`; `SampleTestHost` + mechanical rename; `FileStoreTestSupport`;
   `TestGameConfig`; `LocalFlavorFactory`; `CommandContextDouble`; conformance; smoke test migration; consumer fixture;
   architecture rules.
4. **US3 request log** — feature + middleware + writers; settings/validation; console sink + formatter + warnings; sample
   flavor files; tests incl. privacy, blocked sink, opt-in perf.
5. **US4 seal** — `BuildSealingTests`, `check-build-seal.ps1`.
6. **US5 run-local** — `GameServer.LocalRun` + tests; both scripts; script tests; 011 contract note.
7. **US6 version** — startup line, OTel, GM endpoint + OpenAPI regeneration, Unity package, `VersionTests`.
8. **US7 docs and close** — `ErrorCodeBands` + test; consumer guide; module-authoring; environments guide; PCR platform §19
   and gm; template README; HTML pages; README; release-notes draft; consumer walkthrough; full suite (in-memory, File,
   Couchbase); `check-build-seal.ps1`; opt-in script/perf runs; tech-lead review; resume file update.

Phase 1 blocks all. Phases 2, 4, 5, 6, 7 are independent of each other after 1 (disjoint files except
`GameServerApplicationExtensions.cs`, shared by 4 and 7 → do 4 before 7); phase 3 depends on 1 and should precede 4's
tests (they use the kit `TestHost` `LogEntries`); 8 last. **Estimated tasks: ~72** (foundations 7, US1 10, US2 16,
US3 12, US4 3, US5 7, US6 7, US7/close 10).

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| A game or framework test composes a Dev module in Staging/Production (BC-1) | Startup exit 1 after the bump | Clear per-line message with the fix; release notes first item; sample and fixture already conditional |
| Mechanical rename of 110 `TestHost.*` call sites misses or over-matches | Compile errors or tests on the wrong composition | Rename only `TestHost.StartAsync(`/`BuildApp(`/`DefaultSettings(`; build + full suite; tests that relied on Identity-only are none today |
| Mvc.Testing content-root targets do not flow through the kit (V4) | Consumer smoke test cannot find the content root | Consumer test project references Mvc.Testing directly (documented, fixture does the same) |
| Source Link sha missing in a submodule checkout (V1) | Informational version without revision | Fallback target in `Directory.Build.targets` running `git rev-parse` |
| Request event enabled in production without raising file limits | Operational logs rotate out within hours | Off by default there; startup warning with volume; environments guide/PCR |
| Console sink on a stalled stdout | — | Bounded async queue, drops counted, request path unaffected (test) |
| IVT from Platform to the kit lets the kit depend on more internals over time | Coupling | Architecture test lists the allowed internal types (`CommandContext`, `LoadedAggregate` ctor, `CommitRules`) by review rule; kit never referenced by hosts |
| `dotnet msbuild -getProperty` per project slows the suite | +5–10 s | Parallel ≤ 8; evaluation only, no restore |
| `GameServer.LocalRun` must build before every script run | +2–4 s per `run-local` | Incremental build; acceptable next to the host build |
| Scripts behave differently in PowerShell 5.1 vs 7 or bash versions | Consumer friction | Same resolver for both; fast checks run on Windows PowerShell 5.1 in the suite; bash manual on macOS/Linux |
| Consumer walkthrough finds gaps late | Slice extends | Walkthrough is a task before review; fixture mirrors the guide |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| New project `src/GameServer.TestKit` | Consumers cannot link `tests/Shared` (sample deps, FG-001) and need a referenceable unit | Keeping linkable files (rejected: per-file sync on every bump; GmBrowser/TestHost cannot be linked alone) |
| `InternalsVisibleTo GameServer.TestKit` on Platform | The double must reuse the runner's context and commit rules (FR-025) | Public `CommandContext` or factory (rejected: test-only public API exposing `ITransactionContext`); a second implementation (rejected: drift) |
| New tool project `tools/GameServer.LocalRun` | Both scripts must read nested JSON identically to the host (FR-040, FR-044) | Native parsing per script (rejected: bash `sed` cannot tell nested keys apart) |
| Consumer fixture (3 projects) in the solution | SC-002/SC-005 need a consumer-shaped build and a second host on every run | Manual-only verification (rejected: silent regressions of the consumer surface) |
| Outermost framework middleware | Only place that sees the final status, escaping exceptions and aborts (FR-083, FR-084) | Logging in `ExceptionMiddleware` (rejected: misses successes and escapes) |
| Architecture-test exceptions for the kit (`ITransactionContext`, `FileDocumentStore`) | Detached context stub and file-store test host | — (the kit is test-only; a separate rule forbids host references to it) |
