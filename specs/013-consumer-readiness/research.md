# Research — 013 consumer-readiness

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent. Items marked **[VERIFY]** are runtime or
tooling facts the implementer confirms on the pinned versions (.NET SDK 10.0.403, Serilog.AspNetCore 10.0.0,
Microsoft.AspNetCore.Mvc.Testing 10.0.14) before coding against them; each has a stated fallback.

Code facts this research relies on (verified 2026-11-03 on branch `013-consumer-readiness`, base `fea3d29`):

- Dev profile: `EndpointSecurityValidator.cs:86` reports Dev endpoints only when `options.IsProduction`;
  `ProfileEnforcementMiddleware.cs:69-77` authenticates every non-Public profile through a `switch` whose default branch
  (Dev and any unknown value) tries `PlayerSchemes` then `GmSchemes`; `CommandRunner.EnsureActorAllowed`
  (`CommandRunner.cs:148-161`) has `SecurityProfile.Dev => true` and is a static method without options. It runs before
  the limiter on the execute path (`:64` vs `:71`) and on `TryReplayAsync` (`:124`), but `TryReplayAsync` records no
  command metric when it throws (`ExecuteAsync` does, `:45-53`). `ModuleRegistry.ValidateCommands` checks Public and GM
  command profiles but not Dev and receives no options (`ModuleRegistry.cs:108-124, 271-280`). `--provision` calls the
  same `ModuleRegistry.Validate(options, null)` (`ProvisioningRunner.cs:25`).
- `GameServerOptions` is one mutable singleton instance read per request by the middleware and the runner
  (`AllowsDevelopmentTools => IsDevelopment || IsTest`, `GameServerOptions.cs`). A host on the in-memory or file store
  cannot start outside Development/Test (`GameServerApplicationExtensions.cs:224-230`, `IDevelopmentOnlyStore`).
- `GameHttp.FullGame` always adds `CheatModule` (`tests/Shared/GameHttp.cs`); tests composing it in Staging/Production:
  `SampleGame.Tests/ConfigAndCheatTests.cs:77-86` (asserts the old message), `IapStartupValidationTests.cs:32` (filters
  violations by prefix), `GameServer.Gm.Tests/{GmAuthTests.cs:160, SsoTests.cs:146}` (Development). Old message text is
  also asserted in `GameServer.Platform.Web.Tests/StartupValidationTests.cs:83`.
- Test helpers: `tests/Shared/*.cs` are linked into 7 test projects (`<Compile Include="../Shared/*.cs" LinkBase="Shared"/>`),
  namespace `GameServer.Tests.Shared`; `TestHost.cs` uses `Sample.Counter.Adapters` (default modules Identity + Counter)
  and the sample config folder `tests/Shared/config/base` (counter, battle, shop, iap…); `GmBrowser.cs` extends both
  `TestServer` and `TestHost`; `GameHttp.cs` uses SampleGame; `TestStores.CreateFileStore` returns `FileDocumentStore`
  and is used by `FileStoreHostTests.cs:26` and `FileStoreProcessKillTests.cs:97`. `TestHost.StartAsync(` has 110 call
  sites, most relying on the default composition.
- `CommandContext` is `internal sealed` (`CommandContext.cs:20`), built per attempt from `ActorContext`, `now`,
  `IGameConfig`, `IReadOnlyDictionary<string, LoadedAggregate>`, the `CommandDescriptor`, an `ITransactionContext` (used
  only by `TryClaimAsync` for the existence read) and the receipt id. `LoadedAggregate` is public with an `internal`
  constructor; `AggregateDocumentSerializer`, `ITransactionContext`, `OutboxEntry`, `ReceiptIds`, `ModuleRegistry`
  (public `Add(module, serviceOnly)`), `GameConfig` (public constructor) and `FileGameConfigLoader.ComputeVersion` are
  public. The commit rules `EnsureCommittableCode` and `EnsureAudit` are private to `CommandRunner` (`:352-405`).
  `GameServer.Platform` grants `InternalsVisibleTo` only to `GameServer.Platform.Tests`.
- Pipeline order (`GameServerApplicationExtensions.cs:64-80`): ForwardedHeaders → `ExceptionMiddleware` →
  `CorrelationMiddleware` → routing → GM CORS → `GateMiddleware` → `ProfileEnforcementMiddleware` → request timeouts →
  rate limiter → endpoint. Every framework error body goes through `ErrorResponseWriter.WriteAsync` (exception middleware,
  404 of unmatched paths, 405, profile/CSRF/permission refusals, gate, rate limiter `OnRejected`, timeout policy). Player
  command responses are written in `CommandEndpointFactory.MapCommand` (`:98-131`), GM ones in
  `GmCommandEndpointFactory` (`:136-142`), config in `CoreEndpoints.MapPlayer` (`ConfigRoute`, `:58-70`, `NotModified`).
  Command routes carry `CommandOperationMetadata`. The client abort path writes nothing (`ExceptionMiddleware`
  `OperationCanceledException` branch).
- Logging: `AddObservability` → Serilog `AddSerilog(..., preserveStaticLogger: true, writeToProviders: false)`, one
  file sink in `WriteTo.Async(bufferSize, blockWhenFull: false, monitor: LogSinkMonitor.Instance)`, enrichers
  `InstanceId`, `GameId`, `EnvironmentId`, `SensitiveDataRedactor`; `LogSinkMonitor` aggregates drops over several
  inspectors (`log_dropped_total`). `CorrelationMiddleware` puts `RequestId` in a logging scope.
  `TestHost` never calls `AddObservability` and captures formatted messages only. Serilog.AspNetCore 10.0.0 depends on
  Serilog.Sinks.Console.
- Telemetry: `.AddService(serviceName: "game-server-" + gameId, serviceInstanceId: instanceId)` without
  `serviceVersion` (`ObservabilityExtensions.cs:101-104`). Instance id = `ObservabilityOptions.ResolveInstanceId()`
  (`Observability:InstanceId` → `HOSTNAME` → machine name), used by logs, OTel and the analytics file sink.
- Build: `Directory.Build.props:10` decides `IsTestProject` from `MSBuildProjectDirectory.Contains('\tests\')`; there is
  no root `Directory.Build.targets`; `.editorconfig` has `root = true`; `Directory.Packages.props` exists at the root; no
  `<Version>` anywhere (assemblies 1.0.0.0). Unity package `sdk/unity/com.gameserver.client/package.json` = `0.6.0`.
- Local run: `tools/local/run-local.ps1` hardcodes `samples\SampleHost`, `SampleHost.csproj`, profile `local`, port
  5080, `$SdkVersion = "10.0.403"`, log folder `samples/SampleHost/logs`, the data folder from
  `appsettings.Local.json` and the sample banner; `run-local.sh` reads JSON with `sed` (`"Folder"` first match).
  Launch profile `local` sets `ASPNETCORE_ENVIRONMENT=Local` (the flavor file name). `tools/local/send-ctrl-c.ps1`
  sends Ctrl+Break/Ctrl+C to a console process.
- GM: `ops.read` exists (`GmPermissions.OpsRead`); `/gm/v1/health` is the precedent of an `ops.read` read
  (`GmModule.cs:138`); `docs/api/gm-openapi.json` is generated and compared by `GameServer.Gm.Tests/OpenApiTests.cs`
  (regenerate with `UPDATE_OPENAPI=1`).
- Error codes: ranges in use — core 0–999, counter 10000–10099, economy 11000–11099, iap 12000–12099, gm 13000–13099,
  identity 14000–14099, sample.game 40000–40099, template 90000–90099; `docs/guides/module-authoring.md:60` example uses
  50000–50099.

---

## R1 — One decision for the Dev profile: `GameServerOptions.AllowsDevProfile`

**Decision.** Add `public bool AllowsDevProfile => AllowsDevelopmentTools;` to `GameServerOptions` with a doc comment
naming the three layers and PCR platform §17 R13. The validator, the registry validation, the middleware and the runner
read only this property. No new setting (FR-006).

**Rationale.** `AllowsDevelopmentTools` also gates test authentication, the GM staff seed, the OpenAPI document and
fake verifiers. The deferred Staging cheat switch (R13) would extend *only* the Dev profile; a dedicated property is the
single place that change would touch, while today it is exactly `AllowsDevelopmentTools` (FR-006 wording holds).

**Alternatives.** Read `AllowsDevelopmentTools` directly in four places (rejected: a later switch would need four edits
and could miss one — the very bug of FG-010); a `DevProfilePolicy` service (rejected: DI indirection for a boolean read
from a singleton the layers already hold).

## R2 — Layer 1: startup validation (FR-001, FR-002)

**Decision.**

- `EndpointSecurityValidator.Validate` (line 86) replaces `options.IsProduction` by `!options.AllowsDevProfile`. Message:
  `[endpoint {methods} {route}] uses profile Dev in deployment environment '{DeploymentEnvironment}' (environment id
  '{EnvironmentId}'). Fix: register Dev (cheat) modules only when development tools are allowed
  (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09, SEC-08).`
- `ModuleRegistry.ValidateCommands(options, errors)` gains the options and one rule per command with
  `Profile == Dev && !options.AllowsDevProfile`, for every registered module including service-only ones:
  `[module {name}] command '{operation}' has profile Dev in deployment environment '{…}' (environment id '{…}'). Fix: …
  (OPS-09).` Because `--provision` validates the same registry, it fails the same way (edge case).

**Rationale.** The registry rule covers every path where no Dev endpoint exists (`ServiceOnly()`,
`Hosting:MapPlayerApi=false`, command without route), so a Dev command can never be part of a non-development
composition. Both lines join the one aggregated report (exit 1).

**Alternatives.** Endpoint rule only (rejected: misses service-only/no-route commands, FR-002); refuse at
`AddModule<CheatModule>()` time (rejected: throws outside the aggregated report, and the deployment environment is
validated later by `DeploymentIdentityCheck`).

## R3 — Layer 2: request pipeline, explicit per profile (FR-003, FR-004)

**Decision.** In `ProfileEnforcementMiddleware.InvokeAsync`, directly after `profile` is known (and the 405/no-profile
branches), before the GM same-site check, the `Cache-Control` header, `SetBodyLimit`, any `AuthenticateAsync`, CSRF and
permission checks:

```csharp
if (profile.Value == SecurityProfile.Dev && !options.AllowsDevProfile)
{
    // LogWarning("Dev endpoint {Method} {Route} refused in deployment environment {DeploymentEnvironment} ({EnvironmentId}).")
    // await writer.WriteAsync(context, CoreErrorCodes.Forbidden); return;
}
```

The authentication `switch` becomes explicit and exhaustive: `Public` → none, `Player` → player schemes, `Gm` → GM
schemes, `Dev` → player schemes then GM schemes (only reachable when allowed), **any other value → 403 FORBIDDEN**
(no default authentication branch). Route in the warning = `RouteEndpoint.RoutePattern.RawText` (no path values, no
headers).

**Rationale.** Refusing before authentication guarantees no session touch, no `LoginAnalytics.Authenticated` resume
event and no cookie/bearer validation (FR-003). 403 `FORBIDDEN` is the existing deny-by-default answer (spec assumption).

**Alternatives.** 404 (rejected by spec assumption); refusing after authentication (rejected: resume analytics and
session touch would happen, FR-003).

## R4 — Layer 3: runner (FR-005)

**Decision.** `EnsureActorAllowed` becomes an instance method using `options`; `SecurityProfile.Dev =>
options.AllowsDevProfile` (player or GM actor as today when allowed). The refusal throws
`GameServerException(CoreErrorCodes.Forbidden, "Dev command {op} refused in deployment environment …")`. `TryReplayAsync`
wraps its pre-checks so a refusal records `metrics.CommandCompleted(operation, "FORBIDDEN", replayed: false)` like
`ExecuteAsync`. Order unchanged: actor check → limiter → receipt read → guards → transaction, so nothing is read or
written for a refused Dev command, and a Dev receipt committed in a development store is never replayed by a
non-development host (edge case).

**Alternatives.** Refuse in `CommandEndpointFactory.MapDevCommand` only (rejected: jobs, GM flows and direct runner
callers bypass endpoints).

## R5 — Testing each layer in isolation (FR-007, SC-001)

**Decision.**

| Layer | Test | Technique |
| --- | --- | --- |
| Validator | `DevProfileValidationTests` (Platform.Web.Tests) | `EndpointSecurityValidator.Validate` on a fake endpoint list and `ModuleRegistry.Validate` with a Dev command, for Development/Test/Staging/Production/"Qa"; ServiceOnly and `MapPlayerApi=false` registrations |
| Middleware | `DevProfileMiddlewareTests` | (a) unit: `ProfileEnforcementMiddleware` on a `DefaultHttpContext` with a Dev endpoint, Staging options and a recording `IAuthenticationService` → 403, zero `AuthenticateAsync` calls, `next` not called, one Warning; (b) host: Development `TestHost` with `CheatModule`, then the test sets the singleton `GameServerOptions.DeploymentEnvironment = "Staging"` (validator already passed = "validator bypassed"), calls `POST /api/v1/cheat/grant` with a real guest bearer token, test-auth headers and a GM test header → 403 `FORBIDDEN`, wallet unchanged, no receipt, `host.Analytics` has no resume record, session document unchanged |
| Runner | `DevProfileRunnerTests` | same flipped host; `ICommandRunner.ExecuteAsync` and `TryReplayAsync` with the cheat command for player, GM and system actors → `FORBIDDEN`, store document count unchanged, `command_results_total{code=FORBIDDEN}` incremented; a receipt committed before the flip is not replayed |
| Matrix | `DevProfileFlavorMatrixTests` | `TestHost` with `CheatModule` × flavors local/dev (start) and beta/uat/live with the matching `DeploymentEnvironment` (startup fails; violations contain the Dev endpoint and Dev command lines; other in-memory-store violations ignored by prefix filter) + Test, unknown value; ServiceOnly and no-player-API variants |

**Rationale.** A non-development host cannot start on the in-memory store, so a real Staging host needs Couchbase.
Flipping the deployment environment after startup is exactly the "validator bypassed" situation (route or command seen by
a validator that allowed it) and exercises the real middleware/runner reading the same singleton. The Couchbase flavor
test (`SampleHostFlavorTests`) keeps its "cheat route is 404 on beta" check.

**Alternatives.** A test store wrapper without `IDevelopmentOnlyStore` to start real Staging hosts in memory (rejected:
also needs Staging secrets and OIDC settings, and tests a composition no deployment uses).

## R6 — Framework tests that compose the cheat module (BC-1)

**Decision.** `GameHttp.FullGame` adds `CheatModule` only when `builder.Options.AllowsDevelopmentTools` (the rule of
`SampleHost/Program.cs:62-66`). `ConfigAndCheatTests` "cheat in Production fails" composes `CheatModule` explicitly and
asserts the new message (also for Staging); `StartupValidationTests:83` asserts the new text. `IapStartupValidationTests`
and the GM tests need no change (prefix filter / Development).

## R7 — Consumer test kit: one new project `src/GameServer.TestKit` (FR-010, FR-011, FR-013, FR-014)

**Decision.** New class library `src/GameServer.TestKit/GameServer.TestKit.csproj`, namespace `GameServer.TestKit`,
referenced by path (`framework/src/GameServer.TestKit/GameServer.TestKit.csproj`). References: Platform.Web,
Platform.Testing, Modules.Identity.Adapters, Modules.Gm.Adapters, `FrameworkReference Microsoft.AspNetCore.App`,
`PackageReference Microsoft.AspNetCore.Mvc.Testing` (brings TestHost). No xUnit/Shouldly/sample/test reference. It
lives under `src/`, so analyzers and banned APIs apply (framework code). `GameServer.Platform` adds
`InternalsVisibleTo GameServer.TestKit` (R9).

Moves out of `tests/Shared` into the kit (namespace changes to `GameServer.TestKit`):

| From `tests/Shared` | To the kit | Change |
| --- | --- | --- |
| `ProtobufHttp.cs` | `ProtobufHttp` | none |
| `IdentityHttp.cs` | `IdentityHttp` | none |
| `TestStores.cs` | `TestStores`, `TestStoreKind` | `CreateFileStore` (returns `FileDocumentStore`) leaves the public surface (FR-014): kit-internal for `TestHost`; framework tests that build file stores directly get `tests/Shared/FileStoreTestSupport.cs` |
| `GmBrowser.cs` | `GmBrowser`, `CookieJarHandler` | unchanged `TestServer` overloads + `TestHost` overloads |
| `TestHost.cs` | `TestHost`, `CapturedLog` | default modules = Identity only; game config = in-memory `TestGameConfig.Empty` unless `config` is passed or `GameServer:ConfigPath` is set; adds structured `LogEntries`; new optional last parameter `IGameConfig? config` |

Stays in `tests/Shared` (sample-specific): `GameHttp.cs` (GamePlayer, FullGame), `config/` (sample config), plus new
`SampleTestHost.cs` (static forwarders `StartAsync`/`BuildApp`/`DefaultSettings` with the old defaults: Identity +
Counter, `GameServer:ConfigPath` = `{BaseDirectory}/config`) and `GlobalUsings.cs` (`global using GameServer.TestKit;`).
The 110 `TestHost.StartAsync(` / `TestHost.BuildApp(` / `TestHost.DefaultSettings()` call sites are renamed mechanically
to `SampleTestHost.*`; the type `TestHost` (return value) is the kit type. The 7 framework test projects that link it reference the
kit project and keep linking `tests/Shared/*.cs`.

**Rationale.** One project = one `ProjectReference` for a game; handler tests and host tests sit in the same game test
project anyway (it references the game host). Renaming call sites keeps 100+ tests on their current composition without
a kit default that knows the sample.

**Alternatives.** Name `GameServer.Testing` (FG-001 proposal; rejected: confusable with `GameServer.Platform.Testing`,
which production hosts ship today for the local stores, FG-005); two projects (double vs hosting) (rejected for now:
no consumer needs the split; revisit with NuGet packaging); keep the kit under `tests/` (rejected: the path rule would
make it a test project with global `using Xunit`); a kit-level settable default composition (rejected: mutable static
shared by parallel tests).

## R8 — Local-flavor factory (FR-012)

**Decision.** `LocalFlavorFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>` with `LocalFlavorOptions`. One
per-instance root `%TEMP%/gs-local-{guid}` with `data/`, `analytics/`, `logs/`; unique `InstanceId = "lf-{8 hex}"`.
`ConfigureWebHost` sets `UseEnvironment(options.Environment)` (default `Local`), applies
`LocalFlavorSettings.For(root, instanceId, sweeper)` through `UseSetting`, then `options.Settings` (test overrides win),
then `ConfigureTestServices(options.Services)`. `DisposeAsync` disposes the host (releases the file-store and analytics
locks), then deletes the root with `TestStores.DeleteFolder` retries. With `options.Folder` set, the factory uses that
folder and never deletes it (restart scenario: two factories on one folder).

The documented settings set (`LocalFlavorSettings.Keys`, FR-012 + this slice):

| Key | Value |
| --- | --- |
| `FileStore:Folder` | `{root}/data` |
| `GameServer:Outbox:SweeperEnabled` | `false` (opt-in `OutboxSweeper = true`) |
| `GameServer:Jobs:Enabled` | `false` |
| `GameServer:Hosting:DrainDelaySeconds` | `0` |
| `GameServer:Analytics:Directory` | `{root}/analytics` |
| `Observability:InstanceId` | `lf-{8 hex}` (unique per factory) |
| `Observability:Logging:Directory` | `{root}/logs` |
| `Observability:Logging:Console:Enabled` | `false` (this slice: the local flavor turns the console sink on; tests must not print) |

Rule (module-authoring, consumer guide, PCR): a framework setting that writes to disk, starts background work or writes
to the console is added to `LocalFlavorSettings` in the slice that introduces it; `LocalFlavorSettingsTests` checks
that every key in the list exists on an options type and is named in the consumer guide.

**[VERIFY] V4** `WebApplicationFactory` content-root discovery needs the `Microsoft.AspNetCore.Mvc.Testing` build
targets in the *test* project (they generate `WebApplicationFactoryContentRootAttribute`). If the package's targets do not
flow through the kit's `PackageReference`, the consumer test project references `Microsoft.AspNetCore.Mvc.Testing`
directly (version from the framework's `Directory.Packages.props`); the consumer guide states it either way.
**[VERIFY] V7** `WebApplicationFactory<T>.DisposeAsync` is overridable on 10.0.14; fallback: `Dispose(bool)` + an
`IAsyncDisposable` wrapper type.

**Alternatives.** Composition (a holder exposing `Factory`, as the game's `LocalFlavorHost`) (rejected: callers lose
`WithWebHostBuilder`/`CreateClient` on the factory itself); environment variables instead of `UseSetting` (rejected:
process-wide, breaks parallel factories).

## R9 — Command-context double wraps the runner's own context (FR-020..FR-026, SC-003)

**Decision.** `CommandContextDouble : ICommandContext` (kit) is a decorator over the internal `CommandContext`, created
lazily on the first `ICommandContext` call. Configuration methods (actor, now, config, request/receipt id, seeds) throw
after that ("configure the double before running the handler"). Construction:

- `ModuleRegistry` built from the given modules (`Add(module, serviceOnly: false)`, `Describe()` only — no services).
- Unknown operation → `ArgumentException` naming it and the registered operations; an aggregate of Touches ∪ Reads not
  registered by the given modules → `ArgumentException` naming the aggregate and "pass the module that registers it"
  (edge case; no null/blank state).
- Loaded aggregates: missing → `AggregateDocumentSerializer.Load(descriptor, playerId, null)` (descriptor default state,
  rev 0); seeded → `LoadedAggregate` via the internal constructor with the canonical JSON of the seeded state (rev 1,
  `Exists`). Seeding an id outside Touches ∪ Reads or with the wrong state type throws at `Seed`.
- `ITransactionContext` stub: `GetManyAsync` answers seeded claim markers as present, everything else missing; `Insert`,
  `Replace`, `Remove` throw `NotSupportedException` (the context never calls them).
- Receipt id: `WithReceiptId(id)`, else `ReceiptIds.Compute(actor.ActorId, playerId, operation, requestId)` with
  `WithRequestId` or the fixed default request id `00000000-0000-4000-8000-000000000001`; so `NewId`/`NewSeed` are the
  runner's for the same receipt (FR-024).
- Recording: the decorator appends to ordered lists after the inner call succeeded: writes (first-`Set` order, latest
  state), outbox entries (from the inner `Outbox`, so `AddAudit`/`AddAnalyticsEvent` extension calls appear), claims
  (collection, key, attributes; only when `TryClaimAsync` returned true).
- Commit check: `CheckCommit(ICommandOutcome outcome)` returns the violations the runner would raise at commit, from the
  extracted internal `CommitRules` (below); empty for rejections and committable outcomes.

Extraction in Platform (no behaviour change): `internal static class CommitRules` in `GameServer.Platform.Commands`
with `CheckCode(...)` and `CheckAudit(...)` returning `string?` messages; `CommandRunner.EnsureCommittableCode` and
`EnsureAudit` throw `InvalidOperationException(message)` when non-null (same text as today).

Conformance (FR-025, SC-003): `CommandContextConformanceTests` (Platform.Web.Tests) registers a test module whose one
handler performs a violation chosen by the request (write outside Touches, read outside Touches ∪ Reads, wrong state
type, claim over the limit, claim in `receipts`, claim in `pa_x`, duplicate claim, pre-existing claim, `Set`→`Get`
round trip, `NewId`/`NewSeed` for 5 purposes × 5 request ids) and runs every case through `ICommandRunner` on a
`TestHost` and through the double; it compares exception type and the innermost `InvalidOperationException` message
(the runner wraps handler failures in `GameServerException(INTERNAL)`), return values and derived ids. A rule added to
`CommandContext` is automatically in the double (shared code); a rule added to the runner around the context fails
the conformance or `CheckCommit` tests.

**Rationale.** Sharing the implementation makes drift impossible by construction (FR-025 first option); the conformance
test still proves equality end to end and pins the messages consumers will match.

**Alternatives.** A separate kit implementation + conformance test only (rejected: two copies of every rule, drift
caught only by the test); making `CommandContext` public (rejected: exposes `ITransactionContext` in the public module
API and invites module code to build contexts); a public factory in Platform for detached contexts (rejected: public API
for test-only use; IVT to the kit keeps it internal).

## R10 — Game config test double (FR-021, 015 seam)

**Decision.** `TestGameConfig` (kit, static) builds real `GameConfig` instances: `Empty` (label `test`, no files),
`FromJson(files, label, clientVisible)` (version via `FileGameConfigLoader.ComputeVersion`, identical to a host loading
the same bytes) and `FromDirectory(configRoot, deploymentEnvironment)` (the loader itself). Used by the double and the
kit `TestHost`.

**Rationale.** Same version algorithm as production, so tests of config-dependent handlers and slice 015's
config-validation tests see real `ConfigVersion` strings.

## R11 — Request outcome feature and request log (FR-080..FR-086)

**Decision.**

- **Feature.** Public `IRequestOutcomeFeature` (namespace `GameServer.Platform.Web.Requests`) with settable
  `ResultCode`, `Operation`, `Replayed`, `NotModified` and read-only `StatusCode`, `Aborted`, `Completed`; extension
  `HttpContext.RequestOutcome()` returns it (creating one if absent, e.g. a consumer endpoint mapped in a host without the
  middleware — never null).
- **Who sets it.** `ErrorResponseWriter.WriteAsync` (`ResultCode = code.Name` — covers exception middleware, 404, 405,
  profile/CSRF/permission/Dev refusals, gate, rate limiter, timeouts), `CommandEndpointFactory.MapCommand` and
  `GmCommandEndpointFactory` (`ResultCode = execution.Code.Name`, `Replayed`, `Operation`) just before writing the
  response, `CoreEndpoints` config route (`NotModified`). `Operation` is also filled from `CommandOperationMetadata` at
  completion when a command route failed before the runner. A consumer endpoint may set `ResultCode` itself.
- **Finalization** (in `RequestLogMiddleware`, after `next`): aborted = `RequestAborted.IsCancellationRequested &&
  !Response.HasStarted` → `StatusCode = null`, `ResultCode = "ABORTED"`; an exception escaping the framework pipeline →
  `StatusCode = 500` (unless started), `ResultCode = "INTERNAL"`, rethrow; else `StatusCode = Response.StatusCode` and,
  when no code was set, `OK` for status < 400 or `HTTP_{status}` otherwise. `Completed = true`.
- **Middleware.** `RequestLogMiddleware` (internal, Platform.Web) is the **outermost** framework middleware (registered
  first in `UseGameServer`, before forwarded headers and `ExceptionMiddleware`), so it measures the whole pipeline and sees
  the final status. It installs the feature on entry, finalizes it, then — when request logging is on and the path is not
  excluded — writes one event. Any exception while writing the event is swallowed and counted (never changes the
  response, FR-085). It never touches the request or response body.
- **Event.** Category `GameServer.Requests`, `EventId(1, "Request")`, written through `ILogger` with a custom
  `IReadOnlyList<KeyValuePair<string, object?>>` state (allocation-light, all fields as properties, one fixed message
  template); levels: Information for status < 400 (business codes at HTTP 200 included), Warning for 4xx and aborts,
  Error for 5xx. `IsEnabled(level)` is checked first. Fields and names: data-model §1.
- **Exclusions.** Always `/health/live`, `/health/ready` (`EndpointSecurityValidator.UnversionedProbeRoutes`); plus
  `GameServer:RequestLog:ExcludedPaths` (segment-prefix match, case-insensitive). Excluded requests still get the feature.
- **Settings.** `GameServerOptions.RequestLog` (`GameServer:RequestLog`): `Enabled` (`bool?`, null = on iff
  `AllowsDevelopmentTools`, i.e. on in local/dev/Test, off in beta/uat/live — FR-086 decision), `ExcludedPaths`.
  Validation joins the startup report; an explicit `Enabled=true` outside Development/Test adds a startup warning with the
  volume estimate. Level filtering uses the existing `Observability:Logging:LevelOverrides:GameServer.Requests`
  (e.g. `Warning` = failures only).
- **Sanitizing (FR-082).** Route = endpoint `RoutePattern.RawText`; unmatched → `Request.Path` cleaned: control characters
  → `?`, cut at 120 characters + `...`; never the query string. Actor id from `ActorClaims.ActorId` only (never headers,
  body or query), cleaned and cut at 64. No header, cookie, token or body is read.

**[VERIFY] V5** Serilog's MEL bridge turns every state key/value not in the template into an event property and keeps
`{OriginalFormat}` as the template (SerilogLogger behaviour). Fallback: put every field in the template.

**Rationale.** `ErrorResponseWriter` is already the single funnel for error codes, so one assignment covers every
failure path; the outermost placement is the only point that sees exceptions escaping `ExceptionMiddleware` and the final
status; a feature on `HttpContext.Features` is the ASP.NET idiom consumer middleware can read after `await next()` without
body access (FR-084). The command metric and the event read the same `execution.Code` / exception code (FR-083).

**Alternatives.** Parse response bodies (the game's workaround: rejected, FR-084); `HttpContext.Items` string keys
(rejected: untyped, collision-prone); a response header (rejected: leaks to clients, changes the wire); log inside
`ExceptionMiddleware` (rejected: misses successful requests and escapes); `UseHttpLogging` (rejected: no result code,
logs headers by default, separate options model).

## R12 — Console sink (FR-087, FR-088)

**Decision.** `Observability:Logging:Console` (`ConsoleLoggingSettings`: `Enabled` false, `MinimumLevel` Information,
`BufferSize` 1 000). When enabled, `AddLogging` adds `WriteTo.Async(sink => sink.Console(new ConsoleLineFormatter(),
restrictedToMinimumLevel), bufferSize, blockWhenFull: false, monitor: LogSinkMonitor.Instance)` next to the file sink
(file sink unchanged). `ConsoleLineFormatter : ITextFormatter` (internal, Observability) renders request events as one
compact line built from their properties (null parts omitted) and other events as
`HH:mm:ss.fff LVL message [SourceContext]` plus the exception on following lines. Colours only when stdout is a terminal
(`Console.IsOutputRedirected` false). An internal `TextWriter` override is the test seam. Enabling it outside
Development/Test adds a startup warning through an `IStartupCheck` of the observability module. Package
`Serilog.Sinks.Console` gets an explicit `PackageVersion` (already a dependency of Serilog.AspNetCore).

**[VERIFY] V6** A blocked stdout (pipe not read) blocks only the async worker; `TryAdd` drops are counted by the
monitor. Test: internal blocking writer, 2 000 requests succeed, `DroppedCount` increases.

**Alternatives.** `AddSimpleConsole` provider (the game's choice: rejected — Serilog `writeToProviders: false` bypasses
providers, and the default console provider waits when its queue is full unless reconfigured; one logging pipeline is
simpler); compact JSON on the console (rejected: spec assumption "human-readable one line").

## R13 — Build sealing and relative `IsTestProject` (FR-030..FR-033, SC-004)

**Decision.**

- New root `Directory.Build.targets` containing an empty `<Project>` with a comment ("seals framework projects against
  `Directory.Build.targets` files above the framework root, FG-003; keep it free of behaviour"). MSBuild imports only the
  nearest `Directory.Build.targets`, and this file does not import parents.
- `Directory.Build.props`: `IsTestProject` from the path relative to the framework root:
  `$([MSBuild]::MakeRelative('$(MSBuildThisFileDirectory)', '$(MSBuildProjectDirectory)').Replace('\','/').ToLowerInvariant().StartsWith('tests/'))`
  (property `_GameServerRelativeDir` computed first). Only the framework's own `tests/` decides, also for
  `D:\tests\game\framework\tests\X` (edge case).
- Automated check `BuildSealingTests` (Architecture.Tests): copies the framework build inputs (`Directory.Build.props`,
  `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `BannedSymbols.txt`, `.editorconfig`) and every
  framework `*.csproj` (from `GameServer.slnx`, excluding the consumer fixture) into
  `{temp}/gs-seal-{guid}/tests/consumer/framework/`, writes a hostile `Directory.Build.targets` (property
  `GameServerHostileTargets=leaked` + an `Error` target before `Build`) and a hostile `Directory.Build.props` in
  `.../tests/consumer/`, then runs `dotnet msbuild <proj> -getProperty:IsTestProject -getProperty:IsFrameworkCode
  -getProperty:GameServerHostileTargets -getProperty:Version -getItem:AdditionalFiles` per project (parallel, ≤ 8) and
  asserts: no hostile property, `IsTestProject` true exactly for `tests/*`, `IsFrameworkCode` and `BannedSymbols.txt`
  for all others, `Version` = declared. The `dotnet` executable is `DOTNET_HOST_PATH` → `DOTNET_ROOT/dotnet` → `dotnet`
  on PATH (skip with a message if none).
- Full Release build in that layout: `tools/local/check-build-seal.ps1` (manual / future CI, SC-004 0 errors 0 warnings),
  run once before the slice closes and recorded in tasks.md.

**[VERIFY] V2** `-getProperty`/`-getItem` output (JSON when several are requested) on SDK 10.0.403. **[VERIFY] V3**
property functions `MakeRelative`, `Replace`, `ToLowerInvariant`, `StartsWith` in a property definition.

**Alternatives.** Name-based rule `*.Tests` (rejected: `GameServer.Platform.Couchbase.IntegrationTests` and future
names; location is what the framework controls); full build copy in the default suite (rejected: minutes of build per
test run); `$(MSBuildProjectDirectory.StartsWith($(MSBuildThisFileDirectory)tests))` (equivalent but case and separator
sensitive on Windows).

## R14 — Version stamping (FR-050..FR-055)

**Decision.**

- `Directory.Build.props`: `<GameServerFrameworkVersion>0.7.0</GameServerFrameworkVersion>` and
  `<Version>$(GameServerFrameworkVersion)</Version>` for every framework-repo project → AssemblyVersion `0.7.0.0`,
  FileVersion `0.7.0.0`, InformationalVersion `0.7.0+{git sha}` (SDK Source Link default
  `IncludeSourceRevisionInInformationalVersion`). Because the framework's own props govern framework projects, the same
  stamp applies when a consumer builds them as project references.
- Public API in Platform: `GameServerVersion` (static: `Framework` = declared SemVer core, `FrameworkInformational`,
  `Of(Assembly)`, `InformationalOf(Assembly)`) and `GameServerRuntimeInfo` (record: game id, environment id, deployment
  environment, instance id, game assembly name, game version, framework version — both informational). The game
  assembly is `IHostEnvironment.ApplicationName` (the entry point's assembly, also under `WebApplicationFactory<TEntryPoint>`),
  fallback `Assembly.GetEntryAssembly()`, then `"unknown"`.
- Instance id has one implementation: `GameServerRuntimeInfo.ResolveInstanceId(string? configured)` (configured →
  `HOSTNAME` → machine name) in Platform; `ObservabilityOptions.ResolveInstanceId()` delegates to it; `AddGameServer`
  reads `Observability:InstanceId` from configuration, builds the record once, registers it and exposes it as
  `GameServerBuilder.Runtime`.
- Startup line (FR-054): at the end of a successful `UseGameServer` (category `GameServer.Startup`, Information):
  `Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}),
  instance {InstanceId}.`
- Telemetry (FR-053): `AddService(serviceName, serviceVersion: runtime.GameVersion, serviceInstanceId)` +
  attribute `gameserver.framework.version` = `runtime.FrameworkVersion`.
- GM read (FR-055): `GET /gm/v1/ops/version`, `ops.read`, in `GmModule.MapGmApi` (contract
  [gm-ops-version.md](contracts/gm-ops-version.md)); OpenAPI regenerated; PCR gm lists it. Public routes and health
  probes stay version-free (a test scans their bodies and headers).
- Unity package `0.7.0`; `VersionTests` (Architecture.Tests): declared version = `package.json` version (message names
  both), every `GameServer.*` assembly and SampleHost carry it, informational version has `+` and 40 hex digits when the
  repo has `.git`.

**[VERIFY] V1** Source Link fills `SourceRevisionId` in a normal clone **and in a submodule checkout** (`.git` file).
Fallback in the new `Directory.Build.targets`: a target before `GetAssemblyVersion` that runs `git rev-parse HEAD` in
`$(MSBuildThisFileDirectory)` (`ContinueOnError=true`) when `SourceRevisionId` is empty — the targets file then holds
"what the framework needs" (FR-030).

**Alternatives.** Version from the git tag at build time (MinVer/Nerdbank) (rejected: new package and tag-dependent
builds of untagged commits); version in `/health/live` (rejected: public, OBS-03, spec assumption); entry assembly via
`Assembly.GetEntryAssembly()` only (rejected: the test runner under `WebApplicationFactory`).

## R15 — Reusable `run-local` (FR-040..FR-046, SC-005)

**Decision.**

- New parameters: PowerShell `-HostProject <path>`, `-LaunchProfile <name>` (default `local`), `-Name <text>`; bash
  `--host-project`, `--launch-profile`, `--name` (`=value` forms too). Without `-HostProject` the host is
  `samples/SampleHost/SampleHost.csproj` and every slice 011 behaviour (banner, exit codes, port 5080) is unchanged.
- Host-file reading is delegated to a small framework tool `tools/GameServer.LocalRun` (console app, `FrameworkReference
  Microsoft.AspNetCore.App`, no package) invoked by both scripts as
  `dotnet run --project <framework>/tools/GameServer.LocalRun -c Debug -- describe --host-project <p> --launch-profile <n>`.
  It resolves the project file (file, or the folder's single `*.csproj`), reads `Properties/launchSettings.json`
  (comments allowed), the profile's first `http://` URL port (none → 5080 + note), `ASPNETCORE_ENVIRONMENT` (= flavor),
  layers `appsettings.json` + `appsettings.{flavor}.json` + environment variables exactly like the host
  (`ConfigurationBuilder`), and prints `key=value` lines: `project`, `projectDir`, `projectName`, `isSample`, `flavor`,
  `port`, `portNote`, `dataFolder`, `logFolder`, `sdkVersion` (nearest upward `global.json`), `globalJson`. Failures
  print one line naming the problem and the fix and exit 2.
- Script order: arguments (2) → SDK (3; version shown = nearest `global.json`, read natively) → describe (2; a tool
  build failure → 5) → port probe (4) → build host (5) → start from the project folder with `--launch-profile` and
  `--urls` (6/7) → banner. Banner: `{Name} is ready (flavor {flavor}, data {dataFolder}{reset note})`, base URL, health;
  the sample host additionally prints the GM UI line with the development staff password and the GM OpenAPI line; LAN
  warning text becomes host-neutral ("development tools of this host"). Default `Name` = `Local game server` for the
  sample, the project name otherwise. Failure before ready tails the newest file of `logFolder`. Scripts print the
  resolved project path and never write into consumer folders (the tool's own `bin/obj` live in the framework, git-ignored).
- Repeatable check (FR-041): `RunLocalScriptTests` (Architecture.Tests, repository tooling): **always** (Windows: `powershell.exe`; bash when
  found): exit 2 for unknown argument, missing host project, unknown launch profile, host without launch settings; exit 4
  for a busy port (listener held by the test) — all before any host build. **Opt-in** `GAMESERVER_SCRIPT_TESTS=1`: start
  to ready and graceful stop (Ctrl+Break through `send-ctrl-c.ps1`, exit 0) for the default invocation and for the fixture
  host (`tests/ConsumerFixture/src/Fixture.Host`, port 5180, `-Name "Fixture game"`), banner lines checked, `-Reset`
  empties the fixture data folder, a fixture configuration that fails startup gives exit 6 and the log tail. Unit tests of
  the tool's resolution (`LocalRunDescribeTests`). `run-local.sh` on macOS/Linux stays a documented manual check (spec
  assumption).
- Contract: [run-local-cli.md](contracts/run-local-cli.md) supersedes `specs/011-local-env/contracts/operations-cli.md`
  §4 (a note is added there).

**Rationale.** One resolver gives identical semantics to both scripts (FR-040) and to the host (env var overrides,
case-insensitive keys, JSON comments), and is unit-testable; `sed` over nested JSON cannot tell
`GameServer:Analytics:Directory` from `Observability:Logging:Directory`.

**Alternatives.** Native parsing per script (`ConvertFrom-Json` / `sed`) (rejected: bash cannot parse nested JSON
reliably, semantics drift); `dotnet run file.cs` file-based app (rejected: not unit-testable, first-run restore
behaviour less predictable); a `--describe` run mode in every host (rejected: public host API for a script concern).

## R16 — Game error-code band (FR-060..FR-062)

**Decision.** Core `ErrorCodeBands` (static, `GameStart = 50000`, `GameEnd = 89999`, `FrameworkReservedStart = 90000`
documented). `ErrorCodeBandTests` (Architecture.Tests) discovers every `IGameModule` type with a parameterless
constructor in the framework, sample and template assemblies (SampleHost graph + new reference to
`Template.Module.Adapters`) plus `CoreErrorCodes`, and fails naming module and range on overlap with the game band. The
module-authoring example moves to the template's own 90000–90099 with the instruction "a game module picks its range in
50000–89999". Docs list the band next to the used ranges.

**Alternatives.** A runtime startup check (rejected: a game *must* be allowed in the band; framework-owned modules are
the target, and their set is known at test time).

## R17 — Consumer fixture repository inside the framework (SC-002, SC-005, FR-013)

**Decision.** `tests/ConsumerFixture/` emulates a game repository: its own `Directory.Build.props` (consumer-style,
name-based `IsTestProject` for `*.Tests`, analyzers, `TreatWarningsAsErrors`), `Directory.Packages.props` importing the
framework's, `src/Fixture.Game` (one module `fixture`, one command `fixture.save`, codes 50000–50099),
`src/Fixture.Host` (Program copied from SampleHost, flavors `appsettings.json` + `appsettings.Local.json`, launch profile
`local` on port 5180, `<Version>1.2.3</Version>`), `tests/Fixture.Tests` (xUnit v3; references only
`GameServer.TestKit` + the fixture projects; handler test with the double, `LocalFlavorFactory<Program>` smoke test,
version check, outcome-feature middleware check). All three are in `GameServer.slnx` under `/tests/ConsumerFixture/`.
Because the fixture has its own `Directory.Build.props`, the framework's path rule never applies to it, exactly as in a
game repo.

**Rationale.** The consumer surface is exercised by every framework build (FR-013), SC-002 is automated, the second host
for `run-local` exists, and the consumer guide walkthrough (SC-009, manual) has a reference to compare against.

**Alternatives.** A temp-folder consumer generated by a test (rejected: slow restore/build per run); no fixture, manual
only (rejected: SC-002 would regress silently).

## R18 — Documentation and stamps (FR-008, FR-033, FR-060, FR-070..FR-073, FR-089, FR-090)

**Decision.** New `docs/guides/consumer-guide.md` (English, orchestrator instruction); updates in Vietnamese where the
page is Vietnamese: `docs/guides/module-authoring.md` (§4 band + example, §5 Dev rule, §8 tests: in-repo and consumer
variants, double), `docs/guides/environments-and-local-run.md` (Dev rule, request log/console per flavor + volume,
consumer `run-local`, version read), `docs/pcr/platform.md` (§8 band, §13 request event/console, §17 Dev rule text, new
§19 slice 013 contracts + breaking changes), `docs/pcr/gm.md` (version endpoint), `templates/module/README.md`
(in-repo vs consumer paths, band), `README.md` (pointer), HTML pages `01-gioi-thieu`, `02-them-command`, `03-deploy`,
`index` (Dev rule, request log in the pipeline, console line in local run, version read, stamp
"Đúng với framework v0.7.0 (chưa phát hành)" → `v0.7.0` when tagged). Release notes draft `docs/releases/v0.7.0.md`
(group A section; slices 014–016 append), source of the tag message (FR-090).
