# Release notes draft — v0.7.0 (slice 013 consumer-readiness)

Input of the `v0.7.0` annotated tag message (FR-090). Each user story appends its part; the final text is copied into the
tag message when the slice closes.

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
     before the per-player limiter, the receipt read, the guards and the transaction (a Dev receipt from a development
     store is never replayed); `command_results_total{code=FORBIDDEN}` counts both paths.
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
  reference.
- **FG-006** — command-context test double: `CommandContextDouble.Create(operation, modules…)` is an `ICommandContext`
  for handler unit tests without host, store or DI. It wraps the runner's own context, so every access rule
  (Touches/Reads, state type, claim limit, `receipts`/`pa_*` claims, duplicate and pre-existing claims) throws the
  runner's exception and message, and `NewId`/`NewSeed` equal the runner's for the same receipt id (conformance test).
  Configure actor (`AsPlayer`, `AsGm`, `WithActor`), `At`, `WithConfig`, `WithRequestId`/`WithReceiptId`, `Seed`,
  `SeedClaim`; read `Writes`/`Written<T>`, `Outbox` (audit and analytics entries included), `Claims`;
  `CheckCommit(outcome)` returns the runner's commit-rule messages (uncatalogued or non-200 commit code, GM audit count).

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

## Consumer workarounds now removable

| FG | Workaround today (zh-pvp-server) | Removable after `v0.7.0` | Stays |
| --- | --- | --- | --- |
| FG-010 | `src/ZhPvp.Host/ZhPvpModules.cs` marker on the `AllowsDevelopmentTools` condition; `tests/ZhPvp.Tests/Host/CompositionTests.cs` marker (test as the only protection) | Both markers (framework refuses Dev in beta/uat itself) | The condition and the 5-flavor composition test may stay as game hygiene; CLAUDE.md rule 7 |
| FG-001 | `tests/ZhPvp.Tests/ZhPvp.Tests.csproj` links `tests/Shared/{ProtobufHttp,IdentityHttp,TestStores}.cs`; `tests/ZhPvp.Tests/Support/LocalFlavorHost.cs` copies the factory settings of the framework smoke test | The three `Compile` links (→ kit `ProjectReference`); `LocalFlavorHost` (→ `LocalFlavorFactory<Program>`, `Documents(...)` reads `factory.DataFolder`) | Game-specific capture hooks (the console request-log sink) until US3 ships the framework console sink |
| FG-006 | `tests/ZhPvp.Tests/Support/FakeCommandContext.cs` (dictionary aggregates, throwing `Config`/claims/ids) | The whole file (→ `CommandContextDouble`; handler tests add `CheckCommit(outcome).ShouldBeEmpty()`) | — |

## Docs not re-checked

## Manual checks
