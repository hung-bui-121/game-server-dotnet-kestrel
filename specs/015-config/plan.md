# Implementation Plan: Game config (slice 015 config)

**Branch**: `015-config` | **Date**: 2026-11-04 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/015-config/spec.md`

## Summary

Group C of the improvement round from the first consumer (zh-pvp-server, framework `v0.6.0`): FG-018, FG-023, FG-024 and
FG-019, before tag `v0.7.0`.

- **Config content checked at startup (FG-018).** Modules declare, in `ModuleDescriptor` (Core, business code, ARC-01
  green), the config files they read with named rules (`ConfigFiles`) and the catalogs they build (`ConfigCatalogs`). One
  engine in Platform (`ConfigDeclarationRunner`) runs every rule of every registered module, then builds every catalog
  whose files are clean. It is called by `UseGameServer` and by the test kit (`ConfigDeclarationCheck`), so both report
  the same lines. The loader reports every fault, including duplicate JSON properties. The platform registers declared
  catalogs itself (`AddModule`) and builds them once before traffic. A host or test registration of the same type wins
  and is resolved eagerly.
- **Config status and check isolation (FG-023).** `GameConfigStatus` (`NotLoaded` / `Loaded` / `Failed`, never throws)
  is published once by the config phase. `CheckAll` creates each `IStartupCheck` from its own registration inside its own
  `try`. A check that needs `IGameConfig` when config failed gets one "did not run" line, and the config lines are not
  repeated. Provision mode never loads config: checks see `NotLoaded`.
- **Two versions (FG-024, FG-019).** The server config version (CFG-01) now also hashes the `clientVisible` set. A new
  client config version hashes only what a client receives. **Architect decision (research R1, overrides spec D6):**
  `config_version` keeps its meaning on every message (API-03), and five player messages gain an additive
  `client_config_version` field. `GetConfig` answers `not_modified` for either version. The SDK caches by the client
  version when present.
- **Delivery (FG-019).** The bundle is serialized once at startup. Its size is checked against `ConfigBundleBytes` before
  serving. `GetConfig` compresses per request (br/gzip, explicit `Accept-Encoding` only, ≥ 1 KiB, rollback switch). No
  other route can be compressed. The SDK gains `PersistentConfigCache` (one atomic container file) and a Unity helper.
- **Dogfood and docs.** Economy, IAP and the sample game declare rules and catalogs. The fixture tests a rule with the
  kit. KestrelDemo uses the persistent cache. Consumer guide §12, PCRs, module authoring, HTML pages, release notes draft.

Decisions: [research.md](research.md) (R1–R18, VERIFY V1–V13); entities and lines: [data-model.md](data-model.md);
contracts: [public-api.md](contracts/public-api.md), [wire-changes.md](contracts/wire-changes.md),
[sdk-cache.md](contracts/sdk-cache.md), [startup-report.md](contracts/startup-report.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403, ASP.NET Core 10.0.14); Unity package C# 9 / `netstandard2.1`

**Primary Dependencies**: unchanged. `System.Text.Json` (BCL: `AllowDuplicateProperties`), `System.IO.Compression`
(BCL: `BrotliStream`, `GZipStream`), `System.Security.Cryptography` (SHA-256), `Google.Protobuf` 3.36.2 /
`Grpc.Tools` 2.84.0 (regeneration through WireGen)

**Storage**: no change to Couchbase documents. New on-device file in the SDK (`gameserver-config.bin`, format v1)

**Testing**: xUnit v3 + Shouldly; `TestHost`, `LocalFlavorFactory`, `SampleTestHost`; SDK tests outside Unity
(`GameServer.Client.Tests`); consumer fixture; architecture tests; opt-in perf (`GAMESERVER_PERF=1`); manual Unity
(`smoke-build.ps1`) and ACC-18 device run

**Target Platform**: Linux containers; Windows/macOS/Linux dev machines; Unity 6 clients (Editor, Windows, Android
IL2CPP, iOS IL2CPP)

**Project Type**: web-service framework + Unity SDK + consumer test kit

**Performance Goals**: config phase ≤ 20 ms (sample), ≤ 300 ms (1 MiB of row tables) (SC-007); compression ≤ 20 ms p95
per 1 MiB request, gzip ≤ 30% of raw (SC-005); cache load/save ≤ 50 ms per 1 MiB (SC-006); endpoint set still built
once before serving (014 V16)

**Constraints**: API-03 (no field meaning change), ARC-01, ARC-06, ARC-08, CFG-01..03, SEC-09, PCR-01, SDK-01..03,
constitution II/V/VI; the Unity package stays `0.7.0` with additive public API; no setting disables rules or catalogs
(FR-013)

**Scale/Scope**: about 20 new public types or members (Core 9, Platform 5, TestKit 2, SDK 3); 4 framework/sample
catalogs migrated; 5 rule sets; 5 proto fields; 10 breaking changes (BC-1..BC-10), all with a mechanical fix

## Overview

```text
 Core (business, ARC-01)                    Platform                                   Platform.Web
 ModuleDescriptor                           GameConfigLoad ── FileGameConfigLoader      GameServerBuilder.AddModule
  ├ RequiredConfigFiles (existing)           (once, Mode Serve|NotLoaded)  every fault   └ TryAdd(T → ConfigCatalogStore)  (R7)
  ├ ConfigFiles: Required/Optional(name,     GameConfig (+ClientConfigVersion)            + DeclaredCatalogBindings
  │                rules: ConfigRule[])      GameConfigVersions (server, client)
  └ ConfigCatalogs: Of<T>(build, files)      ConfigDeclarationRunner ◄──────────────┐     UseGameServer
 ConfigContext / ConfigProblems /            ├ rules (per module, per file)         │      └ ConfigStartup.Run  (R10)
 ConfigLocation / ConfigJson                 └ catalogs (Declared build | Provided) │         1 resolve IGameConfig (status Failed?)
                                             ConfigCatalogStore (Type → instance)   │         2 registry.Validate (required files)
                                             GameConfigStatus (public, published)   │         3 ConfigDeclarationRunner.Run
                                             ModuleRegistry.ValidateConfig          │         4 ConfigBundle + size check
                                                                                     │         5 publish GameConfigStatus
 TestKit: ConfigDeclarationCheck.Check(config, modules) ───────────────────────────┘      … WireCatalog, services, gate …
          → report (lines, catalogs, AddCatalogsTo)                                           CheckAll (per-descriptor, R9)
                                                                                              … endpoints (014 rule unchanged)
 Player API:  config_version = server (unchanged)  +  client_config_version = client (new, 5 messages)
 GetConfig:   ConfigBundle.Prefix ++ tail(time, client)  → ConfigResponseCompression (br|gzip|identity, ≥1 KiB, Vary)
 SDK:         TrackConfig(server, client) → ClientConfigVersion → IConfigCache (InMemory | PersistentConfigCache file)
```

Style: unchanged modular monolith with explicit registration. Declarations are data and pure functions in the business
layer. The platform owns ordering, isolation, reporting and DI binding. Delivery concerns stay inside the one endpoint
that needs them.

## Module breakdown

| Module (assembly / namespace) | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Core `GameServer.Config` declarations (`ConfigFileDeclaration`, `ConfigRule`, `ConfigCatalogDeclaration`, `ConfigContext`, `ConfigProblems`, `ConfigLocation`, `ConfigJson`) | Describe a module's config files, rules and catalogs; give rules a sink and parsed JSON | `IGameConfig`, `System.Text.Json` | ordering, line text, DI registration |
| Core `ModuleDescriptor.ConfigFiles/ConfigCatalogs`, `IGameConfig.ClientConfigVersion` | Carry declarations; expose the client version | declarations | — |
| Platform `Config.FileGameConfigLoader` | Load base+env, collect every fault, detect duplicate properties, record overlay origin | STJ | rules |
| Platform `Config.GameConfigVersions`, `GameConfig` | Compute both versions; immutable effective config | SHA-256 | where versions go |
| Platform `Config.GameConfigLoad` (internal) | One load per host; mode; marked unavailability exception | loader | status publication |
| Platform `Config.ConfigDeclarationRunner` (internal) | Run rules then catalogs over a config for a module list; return lines + catalogs | Core declarations, `RegisteredModule` | when it runs, DI |
| Platform `Config.ConfigCatalogStore`, `DeclaredCatalogBindings` (internal) | Hold built catalogs; decide Declared vs Provided per type | captured `IServiceCollection` | building |
| Platform `GameConfigStatus` (public) | Read-only startup view of config | — | computing it |
| Platform `Registry.ModuleRegistry` | Required-file line over the extended set | descriptors | content rules |
| Platform.Web `Hosting.ConfigStartup` (internal) | The config phase inside `UseGameServer` | all of the above, `ConfigBundle` | other startup checks |
| Platform.Web `Hosting.GameServerApplicationExtensions.CheckAll` + `StartupCheckRegistrations` (internal) | Create and run each check in isolation; classify failures | captured `IServiceCollection` | what checks do |
| Platform.Web `Config.ConfigBundle` (internal) | Precomputed full-response prefix, max size, response writing | Google.Protobuf | negotiation |
| Platform.Web `Config.ConfigResponseCompression` (internal) | Negotiate br/gzip, compress, headers | `System.IO.Compression`, typed headers | other routes |
| Platform.Web endpoints (snapshot, config, command, errors) + Identity login | Carry both versions | `IGameConfig` | — |
| TestKit `ConfigDeclarationCheck`, `ConfigDeclarationReport` | Host-free run of declarations with host-identical lines; catalogs for DI | Platform internals (IVT) | unit-test framework |
| Unity package `Runtime/Core` (`GameServerClient`, `PersistentConfigCache`, `CacheFileOps`) | Track client version; persist one version atomically | BCL only | Unity paths |
| Unity package `Runtime/Unity/UnityConfigCache` | Folder under `persistentDataPath` | UnityEngine | file format |

No circular dependency: Core ← Platform ← Platform.Web ← {adapters, Observability} ← TestKit. The SDK depends on nothing
server-side. Core gains one `InternalsVisibleTo` (Platform). It is a friend declaration, not a dependency.

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | No change to runner, documents or transactions; catalogs are immutable read models built before traffic | PASS |
| II Explicit registration | Config files, rules and catalogs declared in the descriptor; every declared rule runs in every environment with no off switch; duplicate catalog declarations fail startup; no discovery | PASS (strengthened) |
| III Security deny-by-default | `GetConfig` stays Player; compression limited to it (BREACH: login/session responses never compressed); cache file names never become paths | PASS |
| IV Async, bounded load | Startup-only CPU work with budgets (SC-007); per-request compression bounded (SC-005, V6); limits numeric and in PCR (`ConfigBundleBytes`, 1 KiB threshold, levels) | PASS |
| V Stable contract | **API-03 respected: `config_version` keeps its meaning (R1)**; five additive proto fields; PCR records line format, versions, compression and the cache format; breaking changes listed (ARC-08) | PASS |
| VI Multi-layer tests | Unit (loader, versions, rules, engine, negotiation, cache), host (report matrices, status, checks, carriage, compression, size), kit, fixture, SDK end to end, opt-in perf; manual Unity + ACC-18 | PASS |
| VII Observability | Lines name module, file, location and fix; new metric `client_config_version_info`; readiness shows both versions; startup config line | PASS |

Requirements check (constitution "do not add requirements contrary to the document"): CFG-01/02/03 keep one
`configVersion` with its CFG-01 meaning on every surface. The client config version is an addition. The CFG-03/SDK-02
clause "the client caches by `configVersion`" is read as "by the version of the client-visible part", recorded under the
spec's Assumptions for the owner to confirm (Owner confirmations 1). Re-check after design: unchanged.

## Design decisions (details in research.md)

| # | Decision | Research |
| --- | --- | --- |
| D1 | `config_version` unchanged everywhere; additive `client_config_version` on `ErrorEnvelope`(10), `CommandResponse`(9), `SnapshotResponse`(4), `GetConfigResponse`(5), `LoginResponse`(7); `GetConfig` `not_modified` on either version. **Architect decision, owner may override (spec D6 rejected: API-03)** | R1 |
| D2 | `GameConfigVersions.Compute` → (server, client), domain-separated, length-prefixed, label hashed; `GameConfig(label, files, clientVisible)`; 4-arg ctor and `ComputeVersion` obsolete; `IGameConfig.ClientConfigVersion` regular member | R2 |
| D3 | Loader collects every independent fault; `AllowDuplicateProperties = false` for manifests and `.json`; overlay origin recorded | R3, V1 |
| D4 | D3 strictness confirmed; `kind` case-insensitive string, integer kinds refused; JSON integer = `TryGetInt64`; zh-pvp config passes (no duplicates, maxQuantity 1e9) | R4 |
| D5 | Declarations in Core: `ConfigFiles`, `ConfigCatalogs`, named `ConfigRule`, one `ConfigContext`, `ConfigProblems`, `ConfigLocation`, `ConfigJson`; Core IVT → Platform | R5 |
| D6 | One engine (`ConfigDeclarationRunner`) for host and kit: rules first, then catalogs; rule errors block catalogs of that file; one parse per file per start | R6 |
| D7 | Platform registers declared catalogs in `AddModule` (`TryAdd` after `ConfigureServices`); last descriptor decides Declared vs Provided; module-alone code gets the DI "no service" error, fixed by `ConfigDeclarationCheck…AddCatalogsTo`; no lazy fallback | R7 |
| D8 | `GameConfigLoad` loads once and marks unavailability exceptions (`Exception.Data`); `GameConfigStatus` published once; provision mode blocks `IGameConfig` (checks see `NotLoaded`, "did not run" is a warning there) | R8 |
| D9 | `CheckAll` creates each check from its descriptor (instance / `ActivatorUtilities` / factory) in its own `try`; marker-based "did not run" lines; no disposal | R9, V2–V4 |
| D10 | Config phase = first block of `UseGameServer` (before relay construction and checks); enumerates no endpoint; startup config info line | R10, V12 |
| D11 | `ConfigBundle` prefix serialized once; size checked at startup with worst-case tail; 413 kept | R11, V5 |
| D12 | Compression owned by the `GetConfig` handler, per request, explicit `Accept-Encoding` only, br > gzip, ≥ 1 KiB, never larger, `Vary` always, `GameServer:ConfigCompression` switch; no `ResponseCompression` middleware; gzip splice = fallback | R12, V6, V7 |
| D13 | SDK sets no `Accept-Encoding`; gzip sniff in Core only if V8 shows a platform that advertises without decoding | R13, V8, V9 |
| D14 | SDK effective version = `client_config_version` ?? `config_version`; new `ClientConfigVersion`; `ConfigOutdated`/event follow it | R14 |
| D15 | `PersistentConfigCache`: one container v1 (magic, version, length, payload, SHA-256), temp + replace, temp recovery, `LoadProblem`/`WriteProblem`, `CacheFileOps` seam + IVT; `UnityConfigCache.Create(env)` | R15, V10, V11 |
| D16 | Metric `client_config_version_info`, readiness `clientConfigVersion` | R16 |
| D17 | Kit `ConfigDeclarationCheck.Check(config, modules)` incl. required-file lines; `AddCatalogsTo` | R17 |

## Design per user story

### US1 — Bad config stops the host; catalogs before traffic (FR-001..FR-025)

- **Loader** (`FileGameConfigLoader.cs`): `Load` = collect → throw once (R3). Private helpers keep their line texts and
  return violations instead of throwing. Manifests and `.json` files go through `JsonDocument.Parse(bytes, NoDuplicates)`.
  The manifest is then bound from the parsed document (no second parse). `GameConfig` gets the overlay set (internal).
- **Declarations** (Core, `src/GameServer.Core/Config/ConfigDeclarations.cs`, `ConfigJson.cs`; descriptor members):
  [public-api.md §1](contracts/public-api.md#1-core-gameservercore).
- **Engine** (`src/GameServer.Platform/Config/ConfigDeclarationRunner.cs`, `ConfigReportLines.cs`): algorithm R6, line
  templates data-model §5.3.
- **Registry** (`ModuleRegistry.ValidateConfig`): required set extended (data-model §2.1). The text is unchanged.
- **Catalog binding** (`GameServerBuilder.AddModule`, `ConfigCatalogStore.cs`, `DeclaredCatalogBindings.cs`): R7.
- **Phase** (`src/GameServer.Platform.Web/Hosting/ConfigStartup.cs`, called from `UseGameServer`): R10.
- **Framework rules**: `src/GameServer.Modules.Economy/EconomyConfigRules.cs` and
  `src/GameServer.Modules.Iap/IapConfigRules.cs` (data-model §2.2, line texts in startup-report §4). Descriptors declare
  files and catalogs. `EconomyModule`/`IapModule.ConfigureServices` drop the lazy factories. `ResourceCatalog.Load` and
  `IapCatalog.Load` stay public and unchanged (FR-024).
- **Kit** (`src/GameServer.TestKit/ConfigDeclarationCheck.cs`, `ConfigDeclarationReport.cs`): R17.

### US2 — Checks never lose the report; config status (FR-030..FR-034)

- `src/GameServer.Platform/GameConfigStatus.cs` (public) and `src/GameServer.Platform/Config/GameConfigLoad.cs`
  (internal). `AddGameServer` registers `GameConfigLoad`, `GameConfigStatus` (instance) and `IGameConfig` (`sp =>
  load.GetOrThrow()`).
- `CheckAll(IServiceProvider, StartupCheckMode)` (R9) + `StartupCheckRegistrations` (captures the collection).
  `ProvisioningRunner` sets `GameConfigLoad.Mode = NotLoaded` first and calls `CheckAll(…, Provision)`. The provision
  warnings go to stderr as `warning: …` (existing behaviour).
- `ValidateServices`: a marked exception from the relay construction is skipped (its cause is already reported).
- XML doc of `IStartupCheck` (FR-034), PCR platform §17, consumer guide §12.4.

### US3 — Download only when client-visible config changes (FR-040..FR-047)

- `GameConfigVersions` + `GameConfig` ctor (R2). `TestGameConfig` uses them.
- Proto fields (wire-changes §1). Server sites: `CoreEndpoints` (snapshot, config), `IdentityModule` (login),
  `CommandEndpointFactory` (from `IGameConfig.ClientConfigVersion` of the instance, next to
  `execution.ConfigVersion`), `ErrorResponseWriter`. GM, receipts and `ICommandContext` unchanged.
- `GetConfig` comparison on either version (wire-changes §3).
- Metric, readiness and startup line (R16).
- SDK `TrackConfig(server, client)`, `ClientConfigVersion`, `RefreshConfigAsync` keyed on the client version (R14).
  Regenerate `Core.cs` and `Identity.cs` with WireGen (`sdk/GameServer.Client/wiregen.json`), then `verify`.

### US4 — Client config survives restarts (FR-060..FR-067)

- `Runtime/Core/PersistentConfigCache.cs`, `CacheFileOps.cs` (internal), `AssemblyInfo.cs` (IVT), each with a `.meta`.
  `Runtime/Unity/UnityConfigCache.cs` + `.meta`. Format and algorithms in [sdk-cache.md](contracts/sdk-cache.md).
- `GameServerClient` default stays `InMemoryConfigCache` (FR-065).

### US5 — Compressed bundle; oversized bundle found at startup (FR-050..FR-056)

- `src/GameServer.Platform.Web/Config/ConfigBundle.cs` (R11) and `ConfigResponseCompression.cs` (R12).
  `GameServerOptions.ConfigCompression` (default true).
- The handler order: parse request → not-modified? (small message) : `bundle.Full(time)` → over the limit? 413 →
  `compression.WriteAsync(http, bytes)`.
- Startup size line in `ConfigStartup` (startup-report §6).
- Unity: V8 drives R13. The outcome is recorded in PCR unity-sdk. The ACC-18 manual run checks the bundle on devices.

### US6 — Framework modules, samples, docs (FR-070..FR-077)

- Sample game: `samples/SampleGame/SampleGame/SampleGameConfigRules.cs` (battle, shop) and the descriptor in
  `ShopHandler.cs:77`. `SampleGameModule.ConfigureServices` drops the two lazy factories.
- Fixture: `Fixture.Game/FixtureModuleDefinition.cs` declares `Required(notes.json, rule)`.
  `tests/ConsumerFixture/tests/Fixture.Tests/FixtureConfigTests.cs` uses the kit call.
- KestrelDemo `Assets/SampleGame/SampleGameClient.cs` (opened by exact path): `config: UnityConfigCache.Create("local")`
  and a log line "config from cache {v}" / "config downloaded {v}".
- Docs: research R18 list. `specs/015-config/release-notes-draft.md` (FR-090).

## Where the plan narrows the spec

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| N1 | FR-043/D6/BC-4: `config_version` becomes the client version, no proto field added | `config_version` unchanged; additive `client_config_version` on 5 messages; `GetConfig` accepts either (R1). **Spec updated** (FR-043, FR-046, BC-4, D6, story 3, SC-004, edge cases) | API-03 forbids changing a published field's meaning; CFG-03 "every response carries `configVersion`" stays literal |
| N2 | FR-051 threshold "default 1 KiB" | Constant 1024 B in PCR platform §1, not a setting | One knob (the rollback switch) is enough; nothing needs tuning |
| N3 | FR-031 / FR-032 provision `NotLoaded` | A check that needs `IGameConfig` in provision gets a **warning**, not an error; `IGameConfig` is not loadable in provision (BC-9) | Provision does not validate config (D4); a missing config folder on an ops box must not fail provisioning |
| N4 | FR-021 "host or test registered … first" | Any host/test registration of the type wins, before or after the module (last-descriptor rule) | Plain DI semantics (last wins) must not be silently overridden by a build |
| N5 | FR-055 "SDK MUST decode gzip itself or keep that platform uncompressed" | Decided by V8: no SDK code if every platform decodes; else gzip-only sniff in Core (V9); Brotli never decoded by the SDK | `BrotliStream` is not in netstandard2.1 |
| N6 | Edge case "overlay folder in the line" | Lines name `(config/env/{env})` when the effective file came from the overlay | Cheap, the loader knows it |
| N7 | FR-015 "`kind` one of currency, item" | Case-insensitive string (what the catalog accepts); integer kinds refused | Not stricter than today for names; integers are mistakes |
| N8 | FR-044 "the startup log keeps the server version" | No config line exists today; a new Information line logs both versions | Operators need the client version without readiness access |
| N9 | FR-053 "per-request fields at their maximum size" | Server time counted as a 10-byte varint; client version is constant | Exact upper bound |
| N10 | FR-018 kit returns "the lines the host would report" | Includes the required-file lines of the given modules; excludes loader lines (the kit gets a loaded config) | Same function as the host for everything after loading |
| N11 | FR-030 "created inside its own fault isolation" | Created from the registration by the framework, not resolved as DI singletons; not disposed (BC-10) | MS DI cannot resolve one registration of a multi-registered service |

## Data flow

**Serve start.** `AddGameServer` (registers `GameConfigLoad`, `GameConfigStatus`, `IGameConfig` factory,
`ConfigCatalogStore`, `DeclaredCatalogBindings`, `StartupCheckRegistrations`) → `AddModule` ×n (`ConfigureServices`,
then `TryAdd` catalog bindings) → `Build()` → `UseGameServer` → `ConfigStartup.Run`: resolve `IGameConfig` (one load,
cached) → registry lines → `ConfigDeclarationRunner.Run` (parse cache → rules → catalogs → store) → `ConfigBundle` + size
→ publish status, set metrics → remaining checks → `CheckAll` → mapping → one report or hand-over to the pre-serving
endpoint check → startup line + config line → Kestrel binds.

**Provision.** `GameConfigLoad.Mode = NotLoaded` → registry (config null) → wire catalog → secret sources →
`CheckAll(Provision)` (config-needing checks: warnings) → provisioner. No rule, catalog or bundle.

**Request `GetConfig`.** Body → `GetConfigRequest` → version ∈ {client, server}? `not_modified` message (small) :
`bundle.Full(now)` → length > limit? 413 → negotiate → compress (or not) → headers → write.

**Request any other.** Unchanged. Responses carry `config_version` = server and `client_config_version` = client.

**SDK.** App start: `PersistentConfigCache(folder)` loads v → login: `TrackConfig(server, client)` → `ConfigOutdated`?
→ `RefreshConfigAsync(Config.Version)` → `not_modified` (nothing) | files → `Store(client, files)` (memory, then tmp +
replace).

**State ownership.**
- Effective config: `GameConfig` (immutable, singleton).
- Load outcome: `GameConfigLoad`.
- Status: `GameConfigStatus` (published once, read-only).
- Built catalogs: `ConfigCatalogStore` (written once by the phase, read by DI factories).
- Declared-vs-provided: `DeclaredCatalogBindings` (decided once after `Build()`).
- Bundle bytes: `ConfigBundle` (built once).
- Device cache: `PersistentConfigCache` (one file per folder; the client is its only writer).

## Performance

- **Startup.** Each `.json` is parsed once by the loader, for validation and duplicates. Rules use a second, cached
  `JsonDocument` per file (the loader's parse is not kept: the loader must stay all or nothing, and parsing 1 MiB costs
  about 3–5 ms). Catalog builds use their existing STJ binding. Bundle prefix: one serialization. Budgets SC-007: a test
  measures the phase on the sample (always on, generous bound) and on 1 MiB of row tables (opt-in).
- **`GetConfig`.** No per-request file copies (one buffer concat of prefix + ≤ 30 B tail). Compression per request at
  the V6 levels, into an `ArrayPool` buffer released after the write. `not_modified` is unchanged in cost.
- **Other requests.** One more string field set per response (a reference to a cached string).
- **SDK.** Synchronous file I/O only on `Store` (rare) and construction; SHA-256 over ≤ 1 MiB.
- **Memory.** Prefix ≈ bundle size (≤ 1 MiB) for the host lifetime; the old per-request `ByteString` copies are gone.

## Seams for later slices and extension points

- **016 (login/commands).** Login without snapshot (FG-013) keeps both version fields. An account-creation hook (FG-015)
  resolves catalogs, which exist before traffic. The kit call can run in the hook's tests.
- **FG-020 (hot reload, deferred).** All config-derived state lives in `GameConfig`, `ConfigCatalogStore`, `ConfigBundle`
  and `GameConfigStatus`. A reload would rebuild these four and swap them. Modules that keep config-derived data in
  declared catalogs need no change. The guide says so.
- **Per-file diff (deferred).** `ConfigFile` could gain a per-file hash (additive field). The client version already
  identifies the visible set.
- **Gzip splice (deferred).** R12 alternative 2, if V6 or production shows per-request compression is too costly. The
  variable fields are already last on the wire.
- **Export.** The 014 export format accepts additive members; config file declarations could be exported later.
- **Not designed now:** schema languages, visibility declarations per module ("never client-visible"), cache encryption,
  pre-login fetch.

## Project Structure

### Documentation (this feature)

```text
specs/015-config/
├── spec.md                      # updated: R1 (client_config_version), D3 confirmation, BC-9/BC-10
├── plan.md                      # this file
├── research.md                  # R1..R18, VERIFY V1..V13, code facts
├── data-model.md                # versions, declarations, startup state, bundle, compression, lines, cache states
├── contracts/public-api.md      # Core, Platform, Platform.Web, TestKit, SDK signatures
├── contracts/wire-changes.md    # proto fields, values per response, GetConfig comparison, compression, compatibility
├── contracts/sdk-cache.md       # container format v1, load/store algorithms, crash table, tests
├── contracts/startup-report.md  # every line, order, examples for SC-001/SC-003
├── release-notes-draft.md       # implementation phase (FR-090)
└── tasks.md                     # next stage (/speckit-tasks)
```

### Source Code (files to change)

```text
proto/gameserver/core/v1/core.proto                                   # + client_config_version (ErrorEnvelope 10, CommandResponse 9, SnapshotResponse 4, GetConfigResponse 5)
src/GameServer.Core/
├── GameServer.Core.csproj                                            # + InternalsVisibleTo GameServer.Platform
├── Config/IGameConfig.cs                                             # + ClientConfigVersion
├── Config/ConfigDeclarations.cs                                      # NEW ConfigFileDeclaration, ConfigRule(Check), ConfigCatalogDeclaration, ConfigContext, ConfigProblems, ConfigLocation(Kind)
├── Config/ConfigJson.cs                                              # NEW helpers
└── Modules/ModuleDescriptor.cs                                       # + ConfigFiles, ConfigCatalogs
src/GameServer.Platform/
├── Config/FileGameConfigLoader.cs                                    # collect-all, duplicates, overlay origin, obsolete ComputeVersion, GameConfig ctor/ClientConfigVersion
├── Config/GameConfigVersions.cs                                      # NEW
├── Config/GameConfigLoad.cs · ConfigDeclarationRunner.cs · ConfigDeclarationResult.cs · ConfigCatalogStore.cs · DeclaredCatalogBindings.cs · ConfigReportLines.cs · Unavailable.cs   # NEW internal
├── GameConfigStatus.cs                                               # NEW public (GameConfigStatus, GameConfigState)
├── StartupChecks.cs                                                  # XML doc (FR-034)
├── GameServerOptions.cs                                              # + ConfigCompression
├── Registry/ModuleRegistry.cs                                        # ValidateConfig extended set
└── Diagnostics/PlatformMetrics.cs                                    # + client_config_version_info
src/GameServer.Platform.Web/
├── Hosting/GameServerBuilder.cs                                      # registrations; AddModule catalog bindings; IGameConfig via GameConfigLoad
├── Hosting/GameServerApplicationExtensions.cs                        # ConfigStartup call, CheckAll per descriptor, ValidateServices marker, LogStartup config line
├── Hosting/ConfigStartup.cs · StartupCheckRegistrations.cs           # NEW internal
├── Hosting/ProvisioningRunner.cs                                     # Mode NotLoaded, CheckAll(Provision)
├── Config/ConfigBundle.cs · ConfigResponseCompression.cs             # NEW internal
├── Endpoints/CoreEndpoints.cs                                        # config handler (bundle, either version, compression), snapshot client version
├── Commands/CommandEndpointFactory.cs                                # client_config_version
└── Errors/ErrorResponseWriter.cs                                     # client_config_version
src/GameServer.Platform.Observability/HealthChecks.cs                 # clientConfigVersion
src/GameServer.Modules.Identity.Adapters/Protos/identity.proto · IdentityModule.cs   # LoginResponse 7
src/GameServer.Modules.Economy/EconomyModuleDefinition.cs · EconomyConfigRules.cs (NEW)
src/GameServer.Modules.Economy.Adapters/EconomyModule.cs              # no lazy ResourceCatalog factory
src/GameServer.Modules.Iap/IapModel.cs (descriptor) · IapConfigRules.cs (NEW)
src/GameServer.Modules.Iap.Adapters/IapModule.cs                      # no lazy IapCatalog factory
src/GameServer.TestKit/ConfigDeclarationCheck.cs · ConfigDeclarationReport.cs (NEW) · TestGameConfig.cs
samples/SampleGame/SampleGame/ShopHandler.cs (descriptor) · SampleGameConfigRules.cs (NEW)
samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs           # no lazy factories
samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs # persistent cache + log line
templates/module/Template.Module/TemplateModuleDefinition.cs · README.md   # example declaration (commented or one rule)
tests/ConsumerFixture/src/Fixture.Game/FixtureModuleDefinition.cs    # notes.json rule
tests/ConsumerFixture/tests/Fixture.Tests/FixtureConfigTests.cs      # NEW
sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs      # TrackConfig(server, client), ClientConfigVersion
sdk/unity/com.gameserver.client/Runtime/Core/PersistentConfigCache.cs · CacheFileOps.cs · AssemblyInfo.cs (+ .meta)   # NEW
sdk/unity/com.gameserver.client/Runtime/Unity/UnityConfigCache.cs (+ .meta)                                          # NEW
sdk/unity/com.gameserver.client/Runtime/Generated/gameserver/core/v1/Core.cs · Generated/Identity.cs                  # regenerated
tests/GameServer.Platform.Tests/Config/FileGameConfigLoaderTests.cs · GameConfigVersionsTests.cs (NEW) · ConfigDeclarationRunnerTests.cs (NEW) · ConfigJsonTests.cs (NEW)
tests/GameServer.Platform.Tests/Infrastructure/RunnerHarness.cs       # new GameConfig ctor
tests/GameServer.Platform.Web.Tests/
├── ConfigStartupReportTests.cs (NEW) · ConfigCatalogBindingTests.cs (NEW) · StartupCheckIsolationTests.cs (NEW)
├── ConfigVersionCarriageTests.cs (NEW) · ConfigCompressionTests.cs (NEW) · ConfigBundleTests.cs (NEW)
├── EconomyConfigRulesTests.cs · IapConfigRulesTests.cs (NEW) · ConfigPhasePerformanceTests.cs (NEW, opt-in parts)
└── StartupValidationTests.cs · TestGameConfigTests.cs · SnapshotTests.cs · IdentityLoginTests.cs (existing: updated expectations)
tests/SampleGame.Tests/SampleConfigRulesTests.cs (NEW) · ConfigAndCheatTests.cs
tests/GameServer.Client.Tests/PersistentConfigCacheTests.cs · ClientConfigVersionTests.cs · ConfigCacheEndToEndTests.cs (NEW) · SdkPublicApiTests.cs (baseline)
tests/GameServer.Architecture.Tests/ArchitectureTests.cs · UnityPackageMetaTests.cs (new .meta files covered)
docs/guides/consumer-guide.md (§11, §12 new) · docs/guides/module-authoring.md
docs/pcr/platform.md (§1, §9, §17, §21 new) · economy.md · iap.md · sample-game.md · unity-sdk.md
docs/html/01-gioi-thieu.html · 02-them-command.html (+ stamp) · docs/html/index.html (stamp)
specs/015-config/release-notes-draft.md                               # NEW
```

**Structure Decision**: no new project. Core gets two files, Platform a `Config/` group of internals, Platform.Web a
`Config/` folder for delivery, TestKit two files, the SDK three runtime files.

## Test strategy

| Story / rule | Tests |
| --- | --- |
| FR-001..FR-003, SC-001 loader | `FileGameConfigLoaderTests`: each fault class alone keeps its text; 2 missing + 1 invalid JSON = 3 lines; base + env manifest faults together; duplicate property in manifest, root, nested object, array element, escaped vs plain name, case variants distinct (V1); overlay origin recorded |
| FR-010..FR-014, FR-017, FR-020..FR-023 engine | `ConfigDeclarationRunnerTests` (Platform): rule order; optional absent → no run; required absent → no rule line (registry reports); rule throws → one line, others run; warning not an error; two modules on one file → both lines; catalog skipped on rule error (no line); build problem/throw/null → one line each, others built; duplicate type → one line naming both; Provided binding resolved, failure line; `ConfigJson` texts (`ConfigJsonTests`) |
| FR-015, FR-016 framework rules | `EconomyConfigRulesTests`, `IapConfigRulesTests`: every row of startup-report §4 (duplicate id names the first row, missing/null/wrong type per property, unknown kind, integer kind, maxQuantity 0/-1/1.0/"5", unknown property accepted; iap key without store, unknown store, productId 129 chars, empty grants, unknown resource, quantity 0 and > maxQuantity, resources.json malformed → no reference lines); `Load` never throws on a config that passed (FR-024, property-style over generated valid configs) |
| US1 host (SC-001, SC-002, story 1 scenarios) | `ConfigStartupReportTests` on `SampleTestHost`: each seeded fault = exactly one line; 6 faults / 3 files + gate = 7 lines (startup-report §4 example); 2 missing + 1 invalid = 3 lines and no rule/catalog line; catalog built once before the first request and the same instance for handlers (`ConfigCatalogBindingTests`); first command makes 0 `GetFile*`/`GetFileJson`/`GetFileBytes` calls (counting `IGameConfig` wrapper via `TestHost` `config:`); host-registered `ResourceCatalog` instance wins (before and after the module), rules still run; module-alone services → DI error text names the type; phase-never-ran → `InvalidOperationException` text |
| FR-018, FR-070 kit | `ConfigDeclarationCheckTests` (Platform.Web.Tests): lines equal the host's for the same faults (sample modules); `AddCatalogsTo` + resolve; `FixtureConfigTests` (consumer-shaped, no host) |
| US2 (FR-030..FR-034, SC-003) | `StartupCheckIsolationTests`: the four-check scenario (startup-report §7 example, exact lines); every creation kind (instance, type, factory lambda, factory method group) → one line with the right name; check depending on a catalog that failed → not-run-c line; status `Loaded` with the same `IGameConfig` instance as handlers and `DeclarationsPassed`; status `Failed`; provision → `NotLoaded`, config-needing check → warning on stderr, exit not affected; a check resolving `IGameConfig` lazily inside `Check` and catching (zh-pvp shape) → 0 extra lines; checks run after catalogs (order probe) |
| US3 (FR-040..FR-047, SC-004) | `GameConfigVersionsTests`: full matrix of data-model §1.4 with `TestGameConfig` **and** the loader on temp folders (base + env overlay); list permutations and duplicates; all-visible separation; `TestGameConfig` = loader. `ConfigVersionCarriageTests` (sample host): login, snapshot, command OK / rejected / replayed, error envelope, `GetConfig` full / `not_modified` carry server in `config_version` and client in `client_config_version`; receipt and `ReceiptView` carry the server version; `GetConfig` with client → `not_modified`, with server → `not_modified`, with a v0.6-format value → full; metric and readiness show both. SDK `ClientConfigVersionTests`: effective version rule against a server with and without field 5; `ConfigOutdated`/event/dedupe; refresh stores under the client version |
| US4 (FR-060..FR-067, SC-006) | `PersistentConfigCacheTests` (sdk-cache §7 list), `ConfigCacheEndToEndTests` (second client on the same folder downloads 0 files; server-only edit → 0 downloads); perf opt-in 1 MiB load/save ≤ 50 ms; SDK project builds as C# 9 / netstandard2.1 with 0 warnings; `UnityPackageMetaTests` for new files; `SdkPublicApiTests`: 0 removed/changed members (SC-008) |
| US5 (FR-050..FR-056, SC-005) | `ConfigCompressionTests`: wire-changes §5 table row by row (headers + decoded body byte-identical); `not_modified` and < 1 KiB never compressed; never-larger rule; switch off → today's bytes/headers, no `Vary`; endpoint sweep of SampleHost (every mapped route, `Accept-Encoding: gzip, br`) → only `platform.config` may carry `Content-Encoding`. `ConfigBundleTests`: prefix + tail = `ToByteArray()` (V5); size exactly at the limit starts, +1 byte fails with the line; 413 still at request time with a limit lowered after start (unit). Perf opt-in: sample / 28 KB / 1 MiB ratio ≤ 30% and p95 ≤ 20 ms (V6). Manual: Unity Editor + Android + iOS (ACC-18) parse a compressed bundle (V8) |
| US6 (FR-070..FR-077) | Sample rules tests (`SampleConfigRulesTests`); no lazy factory left (architecture test: no `TryAddSingleton(sp => *Catalog.Load(...))` in framework/sample adapters, by reflection over registrations of `AddModule` in isolation); manual: KestrelDemo opened twice (`smoke-build.ps1`, second start logs "config from cache"), reviewer walkthrough of consumer guide §12 |
| SC-007 | `ConfigPhasePerformanceTests`: sample phase ≤ 20 ms (always-on with a 5× margin to avoid flakiness), 1 MiB row tables ≤ 300 ms (opt-in) |
| Cross-cutting (FR-091) | Debug + Release 0 warnings; full suite in-memory, File, Couchbase; WireGen `verify` of SDK outputs after regeneration (`CommittedOutputTests`) |

## Breaking changes and migration (feeds the release notes, FR-090)

| BC | What breaks | Steps for a game (zh-pvp) |
| --- | --- | --- |
| BC-1 | Framework rules for `resources.json`/`iap.json`, sample rules, catalogs built before traffic | Start each flavor once; fix each reported line. zh-pvp's committed config passes (research R4) |
| BC-2 | Repeated JSON property name in a config file or manifest = startup fault | Keep one property. zh-pvp: none found |
| BC-3 | `FileGameConfigLoader.Load` reports every fault in one exception | Tests reading `Violations[0]` read every violation |
| BC-4 | Additive `client_config_version` on 5 messages; SDK `ConfigOutdated`/`ConfigVersionChanged` follow the client version when the server sends it (semantic for SDK users, PCR unity-sdk) | None on the wire. Game code that logs the event argument now logs the client version. Tests comparing `config_version` with `IGameConfig.ConfigVersion` keep passing (after BC-6) |
| BC-5 | Server config version value changes once (hashes `clientVisible`, new input format) | Update pinned metric values; receipts after the deploy carry the new value |
| BC-6 | `IGameConfig.ClientConfigVersion` member; 4-arg `GameConfig` ctor and `ComputeVersion` obsolete (CS0618 = error) | `tests/ZhPvp.Tests/Support/TestGameConfig.cs`: `return new GameConfig(label, files, clientVisible);` |
| BC-7 | Bundle over `ConfigBundleBytes` fails startup | Raise the limit or make files server-only |
| BC-8 | Declared catalogs registered by the platform, not by module `ConfigureServices` | Code building services from modules alone: `ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)` or register an instance |
| BC-9 | Provision mode never loads game config: `IGameConfig` cannot be resolved there; a check that needs it is skipped with a warning | Take `GameConfigStatus` and skip on `NotLoaded`. zh-pvp `GameConfigCheck` catches and returns: unaffected |
| BC-10 | `IStartupCheck`s are created by the framework from their registration each start (not resolved as container singletons) and are not disposed | A check that is also resolved elsewhere gets a different instance there; make checks stateless and not disposable |

zh-pvp order after the bump: BC-6 one-line fix → start, fix BC-1/BC-2 lines (none expected) → move `GameConfigCheck`
into `ConfigFiles`/`ConfigCatalogs` (`GameCatalog` as a declared catalog via `TryLoad` reporting into `ConfigProblems`;
CASH/GOLD rule on `resources.json`) → rewrite the FG-019 pinned test (client version unchanged, server version changed)
and delete or invert the FG-024 pinned test → drop the `docs/pcr/game.md` label rule → client: `config:
UnityConfigCache.Create(env)` at `OnlineSession.cs:83`, drop both FG-019 markers.

## Implementation phases (input for tasks.md)

1. **Foundations and VERIFY (blocking).** V1, V2, V3, V4, V5, V12 first (spikes as tests). Core declarations +
   `ConfigJson` + IVT. `IGameConfig.ClientConfigVersion`, `GameConfigVersions`, `GameConfig` ctor, obsolete marks.
   Migrate framework callers of the obsolete API (`RunnerHarness`, `TestGameConfig`, tests).
2. **US1 loader and engine.** Loader collect-all + duplicates + overlay. `ConfigDeclarationRunner`, report lines,
   registry extension. Unit tests.
3. **US2 load, status, checks.** `GameConfigLoad`, `GameConfigStatus`, `StartupCheckRegistrations`, `CheckAll`,
   provision mode, `ValidateServices` marker. Tests.
4. **US1 host wiring and migration.** Catalog bindings in `AddModule`, `ConfigCatalogStore`, `ConfigStartup` in
   `UseGameServer`, economy/IAP/sample rules + declarations, lazy factories removed, kit call, fixture rule. Host report
   tests, binding tests, kit tests.
5. **US3 versions on the wire.** Proto fields, server sites, `GetConfig` comparison, metric, readiness, startup line.
   WireGen regenerate + verify. SDK `TrackConfig`. Carriage and matrix tests.
6. **US5 delivery.** `ConfigBundle`, size check, compression, option. Tests + opt-in perf (V6, V7).
7. **US4 SDK cache.** `PersistentConfigCache`, `CacheFileOps`, IVT, Unity helper, `.meta`. Tests + perf (V10, V11
   outside Unity).
8. **Unity and devices (manual).** V8, V9, V10/V11 on devices: KestrelDemo persistent cache, smoke build, ACC-18 run. R13
   fallback only if V8 fails.
9. **Docs and close.** Consumer guide §11/§12, module authoring, template, PCRs (platform §1/§9/§17/§21, economy, iap,
   sample-game, unity-sdk), HTML 01/02 + stamps, release-notes draft, reviewer walkthrough, full suite (in-memory, File,
   Couchbase), tech-lead review, resume file.

Phase 1 blocks all. 2 → 4. 3 can run in parallel with 2 (disjoint files except `GameServerApplicationExtensions.cs`: do 3's
`CheckAll` before 4's `ConfigStartup`). 5 needs 1. 6 needs 5 (field 5 in the bundle). 7 needs 5 (`ClientConfigVersion`)
and can run in parallel with 6. 8 needs 6 and 7. 9 last. **Estimated tasks: ~72** (foundations 10, loader/engine 9,
status/checks 8, host wiring/migration 12, versions 9, delivery 8, SDK cache 7, Unity 3, docs/close 6).

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Owner prefers D6 over R1 | Rework of US3 wire part | R1 is additive and keeps D6 reachable later (stop sending field 5 = old behaviour); the decision is isolated in 6 server sites + `TrackConfig`; asked in Owner confirmations |
| `AllowDuplicateProperties` gives no usable path/position (V1) | Duplicate line without location | Own `Utf8JsonReader` walk (fallback), same line |
| `ActivatorUtilities` picks another constructor than DI for some check (V3) | A check created differently than before | Single-constructor checks in framework/fixture/zh-pvp; fallback `CreateFactory` with DI's rule; BC-10 note |
| Hidden eager resolution of a catalog before the phase (V12) | `InvalidOperationException` at startup instead of a line | Grep + startup test with a consumer taking a catalog; phase is the first block |
| Framework tests seeding broken economy/IAP configs now fail with new lines (BC-1 in our own suite) | Red tests during migration | Phase 4 updates `StartupValidationTests`/`ConfigAndCheatTests` expectations together with the rules |
| Per-request compression exceeds SC-005 on the reference machine (V6) | Budget missed for 1 MiB bundles | Lower level; fallback gzip splice (R12 alt 2); real bundles are ≤ 28 KB today |
| A Unity platform advertises gzip but does not decode (V8) | That platform cannot parse config → outage for it | Server compresses only on explicit `Accept-Encoding`; SDK gzip sniff (V9); rollback switch `GameServer:ConfigCompression=false`; ACC-18 run before release |
| `File.Replace` emulated non-atomically on a Unity target (V10) | Partial file after a crash | Load-time temp recovery + integrity check keep "old or new"; fault tests at every step |
| Mixed rollout makes clients alternate versions | Extra downloads during a rollout | Documented (CFG-01 accepts it); compressed bundles are a few KB |
| Rules diverge from `Load` (a config passes the rules but `Load` throws) | 500 on first use is gone but a catalog line appears instead | FR-024 property-style test: every rule-clean generated config loads |
| `Exception.Data` marker lost through DI wrapping (V2) | "did not run" line degrades to "could not be created" | Inner-exception walk; fallback `ReferenceEquals(Violations, load.Violations)` |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| `InternalsVisibleTo GameServer.Platform` on Core | `ConfigContext`/`ConfigProblems` constructed only by the framework; keeps their shape changeable | Public constructors (freeze an internal shape for no consumer need; the kit covers consumer tests) |
| Per-descriptor creation of startup checks (`StartupCheckRegistrations`) | FR-030: one throwing constructor must not hide the other checks | `GetServices<IStartupCheck>()` (all or nothing; the FG-023 bug) |
| `Exception.Data` marker on unavailability exceptions | FR-031 "did not run" vs real creation failures, without changing the sealed public exception | Unseal + subtype (public API change); message matching (fragile) |
| `DeclaredCatalogBindings` capturing the service collection | FR-021 host override before **or** after the module, decided after `Build()` | Only `TryAdd` (a host registration made after the module wins in DI, yet the platform would still build the declared catalog, report its lines and keep an instance nobody resolves) |
| Precomputed bundle prefix | Exact startup size check (FR-053) and no per-request copy of 1 MiB | Building the message per request (kept as V5 fallback) |
| Two versions on the wire (R1) | API-03 forbids reusing `config_version` for a new meaning | D6 semantic change (rejected, R1) |
| SDK file-ops seam + IVT | FR-067 "fault injected at each save step" | Real-filesystem tricks (cannot fail between flush and replace deterministically) |
