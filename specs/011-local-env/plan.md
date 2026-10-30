# Implementation Plan: Environments per flavor and easy local run (slice 011 local-env)

**Branch**: `011-local-env` | **Date**: 2026-10-30 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/011-local-env/spec.md`

## Summary

A flavor is the host environment name (`Local`, `Dev`, `Beta`, `Uat`, `Live`) and loads `appsettings.{Flavor}.json`
over a cleaned shared base that holds no deployment environment, environment id, store, staff seed or secret.
`GameServer:DeploymentEnvironment` is normalized and validated (no silent fallback for `Local`/`Dev`), and a standard
environment id must match the flavor table. Stores and the host contribute startup checks through one new
`IStartupCheck` seam, and module option validators are merged, so one start reports every problem at once. The local
flavor uses a new **file store**: the existing `InMemoryDocumentStore` (all semantics, conformance-tested) fed at startup
from a folder of one-JSON-file-per-document and persisting every mutation before it is visible — single-document writes by
temp-file + atomic rename, multi-document commits through a journal whose rename is the commit point; an exclusive lock
file keeps one host per folder; it is refused outside Development/Test. `CouchbaseOptions` gains KV/query timeouts, TLS
with a trusted CA (TLS mandatory in Production), compression and pool settings, with range validation and warnings for the
owner's legacy keys. The host binary gains `--provision` (scope, `txn_meta` first, collections, indexes, wait until
online; idempotent; exit codes 0/1/2) and, when startup provisioning is off, refuses to start if a collection is missing
(KV probe with the runtime credential). A launch profile and `tools/local/run-local.ps1`/`.sh` start the local flavor on
`http://localhost:5080` in one command. Detailed decisions: [research.md](research.md); configuration and on-disk format:
[data-model.md](data-model.md); command-line contract: [contracts/operations-cli.md](contracts/operations-cli.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403); scripts: Windows PowerShell 5.1 + PowerShell 7, bash (macOS/Linux,
Git Bash)

**Primary Dependencies**: unchanged (ASP.NET Core Minimal API, CouchbaseNetClient 3.10.0 pinned, Google.Protobuf pinned);
no new package (`System.Text.Json.Nodes`, `System.Security.Cryptography.X509Certificates`, `IStartupValidator` are in the
shared framework)

**Storage**: Couchbase (dev/beta/uat/live), new file store (local; development-only), in-memory (tests). No new
collection or index. New on-disk format `gameserver-file-store` v1 (data-model §6)

**Testing**: xUnit v3 + Shouldly; store conformance suite on in-memory, file (with reopen check) and Couchbase; file store
crash-point tests; `TestHost`/`GmTestHost`; `WebApplicationFactory<Program>` on the local flavor; Couchbase integration
(`COUCHBASE_CONNSTR`) for provisioning, schema check, settings mapping, data-only credential; optional TLS integration
(`COUCHBASE_TLS_CONNSTR`, `COUCHBASE_CA_PATH`); opt-in performance and real-kill tests; manual script acceptance

**Target Platform**: Linux server (beta/uat/live), Windows/macOS/Linux developer machines (local)

**Project Type**: web-service framework + sample host + developer tooling

**Performance Goals**: file store: restart with 10 000 documents ready ≤ 10 s; command commit p95 ≤ 50 ms on a developer
SSD (SC-006); second host refused ≤ 5 s (SC-007); local script ready ≤ 60 s after build (SC-001). No change on the
Couchbase hot path (settings only)

**Constraints**: OPS-01/OPS-08/OPS-09/SEC-03 (no development settings or secrets reach Staging/Production), SEC-01 (TLS to
the database in Production, least privilege), DAT-03 (absolute expiry), DAT-04 (all-or-nothing commits, also locally),
DAT-07 (`txn_meta` first, never dropped; ops command in Production), PCR-01 (new keys, exit codes, defaults published),
ARC-06 (fail before traffic, every error at once)

**Scale/Scope**: 5 standard flavors; 1 new store; 12 new Couchbase keys; 1 host switch; 2 scripts; no player-facing API
change

## Overview

```text
 ASPNETCORE_ENVIRONMENT = Local | Dev | Beta | Uat | Live
          │
 appsettings.json  <  appsettings.{Flavor}.json  <  environment variables  <  command line (--urls, --provision, …)
          │
 Program.cs ── GameServerCommandLine.Parse(args) ──► mode Serve | Provision
          │
 AddGameServer ── DeploymentEnvironmentResolution ──► DeploymentIdentityCheck (IStartupCheck)
          │
          ├─ Store=File ──────► AddFileDocumentStore ── FileDocumentStore ── InMemoryDocumentStore (+ internal IDocumentPersistence)
          │                          │ lock .lock · store.json · journal.json · g_<game>/<collection>/<key>.json
          │                          └─ FileStoreCheck, FileStoreHealthProbe, IStoreInitializer (load before traffic)
          ├─ Store=Couchbase ─► AddCouchbaseDocumentStore ── CouchbaseConnection (ClusterOptions factory: timeouts, TLS, pool)
          │                          ├─ CouchbaseSettingsCheck (ranges, TLS rule, legacy-key warnings)
          │                          └─ CouchbaseProvisioner : IStoreInitializer, IStoreProvisioner
          │                                 startup: provision (Dev default) | verify collections (KV probe) → exit 1 if missing
          └─ Store=InMemory ──► tests / throwaway runs
          │
 app.RunGameServerAsync(mode)
   Serve:     UseGameServer (registry + IStartupCheck* + IStartupValidator + existing checks → one report) → RunAsync
              → listen warning (Development + non-loopback)                                      exit 0 | 1
   Provision: registry.Validate + IStartupCheck* → IStoreProvisioner.ProvisionAsync(progress)      exit 0 | 1 | 2

 Developer: run-local.ps1/.sh ─ build ─ dotnet run --launch-profile local -- --urls … ─ poll /health/ready ─ print URLs
```

Style: unchanged modular monolith; configuration-driven flavor selection; one validation report before traffic;
development-only components refused at runtime rather than excluded at build time (one artifact for all flavors).

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | No change to the runner. The file store keeps `InMemoryDocumentStore`'s transaction semantics and makes a multi-document commit all-or-nothing on disk (journal commit point); TransactionOnly and runner-owned collections are enforced by the same code | PASS |
| II Explicit module registration | Store packages register `IStartupCheck` explicitly; provisioning uses the registry of the deployed build (no discovery); flavor files are explicit configuration | PASS |
| III Security deny-by-default | Invalid/missing deployment environment fails closed (never Development); base file has no development values; secrets never in committed files (repository test, all flavors); file/in-memory stores refused outside Development/Test; TLS required in Production; data-only runtime credential possible; LAN binding warned; beta/uat keep no dev tools (R13) | PASS |
| IV Async, bounded load | All new limits numeric and in PCR (timeouts, pool, provisioning timeout, script readiness 60 s, key length 120). **Exception:** the development-only file store does synchronous file I/O under the in-memory store lock on the request path — see Complexity Tracking | PASS with justified exception |
| V Stable contract | PCR platform §17 records store kinds, file store semantics/limits, Couchbase keys with defaults and bounds, flavor rules, provisioning contract and exit codes, default port. Breaking items announced: `Couchbase:ConnectionString` has no default; `TestHost` environment id `test`; sample `Program.cs` shape (`RunGameServerAsync`); `SecretKeys` + PlayGames secret (stricter). No player/GM wire change | PASS |
| VI Multi-layer tests | Conformance suite runs on the file store unchanged (+ reopen invariant); crash-point tests; flavor misconfiguration matrix; repository config test; Couchbase integration for provisioning, schema check, settings mapping and data-only credential (skip with message without `COUCHBASE_CONNSTR`). The file store is **not** evidence for Couchbase behaviour and replaces no TST-01/TST-04 test. **Gap carried, not introduced:** constitution VI requires Couchbase integration tests "in CI" and no CI pipeline exists (CI-01 open); they run locally against EE 7.6.12 before merge, as in 007–010 | PASS (CI gap pre-existing, noted) |
| VII Observability | Startup warnings logged once (`GameServer.Startup`); file store load count/time, faulted-state error log and readiness unhealthy; provisioning progress lines on stdout + logs; listen warning; no secret or connection string with credentials in messages | PASS |

Re-check after design: unchanged.

## Key decisions (one line each; details in research.md)

1. **R1** Flavor = host environment name → `appsettings.{Flavor}.json`; env vars and command line override; overlay stays per deployment environment.
2. **R1** Local port via `"Urls"` in `appsettings.Local.json` (never `Kestrel:Endpoints`); `--urls` overrides; launch profile carries the same URL.
3. **R2** `DeploymentEnvironment` normalized (canonical casing) and validated; fallback only to a host name that is itself a deployment environment; invalid ⇒ fail-closed (not Development) and reported.
4. **R2** Standard `EnvironmentId` (local/dev/beta/uat/live) must match the flavor table (`StandardFlavors`); `TestHost` default id becomes `test`.
5. **R3** Base `appsettings.json` keeps only flavor-neutral keys; five flavor files; GM seed in local/dev files only; repository test enforces no secrets and the table.
6. **R3** `SecretKeys` gains `Identity:Providers:PlayGames:ClientSecret`.
7. **R4** New `IStartupCheck`/`StartupCheckResult` seam; `UseGameServer` merges checks + `IStartupValidator` failures into one report and logs warnings.
8. **R5** `CouchbaseOptions`: `KvTimeoutMs` 2500, `QueryTimeoutMs` 20000, `TrustCertificatePath`, `Compression`, `CompressionMinSizeBytes`, `Num/MaxKvConnections`, `ProvisioningTimeoutSeconds`; `ConnectionString` required (no localhost default).
9. **R5** Production requires `couchbases://` (error), Staging warns; a configured CA becomes the only trusted root; legacy keys warn with their replacement.
10. **R6** File store = `InMemoryDocumentStore` + internal `IDocumentPersistence` seam, owned by `FileDocumentStore` in `GameServer.Platform.Testing`; refused outside Development/Test by the existing `IDevelopmentOnlyStore` rule, before the folder is touched.
11. **R7** One file per document under `g_<gameId>/<collection>/`, `{key, expiresAtUtc, content}`; percent-encoding with lowercase literals (case-fold safe), device-name escaping, hashed names above 120 chars; `store.json` format version.
12. **R8** Commit point = rename (one document) or `journal.json` rename (several); recovery replays the journal; failure after the commit point → faulted store, readiness unhealthy; durability = flushed to the OS (survives process kill, not power loss).
13. **R9** `.lock` with `FileShare.None` (OS-released on crash); parallel load; malformed files fail startup by name, all at once; expired files deleted at load; `FileStore:ResetOnStart` resets after locking, with folder safety rules.
14. **R10** `--provision` mode in the host binary: reduced validation, `IStoreProvisioner` progress report, `txn_meta` first, wait for indexes online, exit 0/1/2; admin credential via the same `Couchbase:Username/Password` keys in the pipeline step.
15. **R11** Startup provisioning off ⇒ KV-probe every required collection with the runtime credential; missing ⇒ exit 1 naming collections and `--provision`; indexes not checked at runtime.
16. **R12** Launch profiles `local` (default) and `dev-couchbase`; `run-local.ps1`/`.sh` (build, `dotnet run --launch-profile local` from the project folder, readiness wait, URL block, Ctrl+C, `-Port/-Reset/-Lan`, exit codes 2–7); listen warning for Development on non-loopback; Unity/SDK defaults → 5080.
17. **R13** Beta/uat (Staging) keep no development tools; no per-deployment cheat switch (deferred extension point).

## Spec conflicts resolved (checklist "Conflicts found" 1–9)

| # | Conflict | Resolution |
| --- | --- | --- |
| 1 | Base `appsettings.json` holds dev-only values; arrays cannot be removed by a flavor | Base cleaned; values moved to flavor files; seed only in local/dev files (R3). Consumers fixed: smoke test (R12), `SsoTests` role override, `start-hosts.ps1` |
| 2 | Unvalidated host-name fallback in `GameServerBuilder` | `DeploymentEnvironmentResolution` + `DeploymentIdentityCheck` (R2) |
| 3 | `start-hosts.ps1` sets no environment | Sets `ASPNETCORE_ENVIRONMENT=Dev` per host, restored afterwards (R12) |
| 4 | OPS-09 allows cheats outside Production; code allows them only in Development/Test | Keep the stricter code rule for beta/uat; no switch now; deferred `GameServer:Staging:AllowCheatModule` (R13) |
| 5 | No TLS option in `CouchbaseOptions` | `couchbases://` + `TrustCertificatePath`; Production error / Staging warning (R5) |
| 6 | Provisioning only via `ProvisionOnStartup` | `--provision` mode + startup schema check (R10, R11) |
| 7 | Runtime secret check skipped in Development | Repository test over every committed flavor file (R3) |
| 8 | Beta/uat share the Staging overlay | Accepted (owner Q4); documented; per-`EnvironmentId` overlay deferred (CFG-01 change) |
| 9 | `WebApplicationFactory<Program>` smoke test relies on the base file | Smoke test runs the local flavor on a temp folder (R12); `TestHost`-based tests that read the copied base file reviewed (R3) |

## Data flow

**Start (serve).** Configuration layered → `AddGameServer` resolves the deployment environment and registers
`DeploymentIdentityCheck` → `Program` registers the store of `GameServer:Store` (and its check) and modules → `Build` →
`RunGameServerAsync(Serve)` → `UseGameServer` collects registry, service, secret, data protection, gate, endpoint,
`IStartupCheck` and `IStartupValidator` errors → any error: stderr + exit 1 (nothing touched the store) → else
`RunAsync`: `KeyRingService.StartingAsync` awaits `IStoreInitializer` → file store: lock, recover, load / Couchbase:
provision (Dev) or verify collections → hosted services → Kestrel listens → listen warning check.

**Command on the file store.** Runner → `InMemoryDocumentStore.RunTransactionAsync` → body → `TryCommit` (versions
checked under lock) → `FileCommitWriter.Persist(changes)`: journal tmp → rename (commit point) → apply files → delete
journal → memory updated → response. KV writes (sessions, leases): temp → rename → memory.

**Restart.** Lock → delete `*.tmp` → replay `journal.json` if present → parallel load of declared collections → drop expired
→ `LoadCommitted` → ready. Second host on the same folder → exit 1 at the lock.

**Provision.** Pipeline sets flavor + admin credential → `--provision` → reduced validation → scope, `txn_meta`,
collections, KV visibility wait, indexes, online wait, verify → stdout report → exit code.

**State ownership.** Flavor values: flavor file (non-secret) + environment (secrets, deployment-specific). Deployment
environment: `GameServerOptions.DeploymentEnvironment` (normalized once in `AddGameServer`). Local data: the folder
(source of truth across restarts) mirrored in the `InMemoryDocumentStore` dictionary (source of truth while running;
the folder never diverges because writes persist first). Schema: Couchbase (created only by the provisioner).

## Spec deviations

Where the code or a requirement contradicts the spec, the plan decides as follows (no silent change):

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| D1 | Edge case "disk full … the commit fails with the store-unavailable error"; US4-4 "the store-unavailable / timeout error the framework already maps" | No new error code. A file store write failure before the commit point → `TransactionFailedException` → `INTERNAL` (500), as a Couchbase transaction failure today; KV-path I/O errors → `INTERNAL`; a Couchbase KV/query timeout surfaces as today (`INTERNAL`, or `REQUEST_TIMEOUT` 503 when the endpoint timeout fires first). Readiness reports the store unhealthy in both cases | The framework has no core "store unavailable" code (`STORE_UNAVAILABLE` 12002 is the IAP store, not the database); adding one changes the error catalog contract (API-11) outside this slice's scope |
| D2 | FR-032 "does not serve traffic"; Independent test "does not become ready" | The host **exits with code 1 before Kestrel listens**, naming the missing collections and `--provision`; only collections are checked, not indexes | The key ring is read before readiness exists, so a missing collection already prevents start; an explicit message replaces an SDK timeout. Index listing needs the `Query System Catalog` role, which the data-only identity lacks (R11) |
| D3 | FR-031 "its own administrative credential supplied at run time" | Same keys `Couchbase:Username`/`Couchbase:Password`, set to the administrative identity by the provisioning step's environment | The provisioning run is a separate process; a second key pair would add a secret key and a precedence rule for no gain |
| D4 | FR-036 the **script** deletes the data folder | The script passes `--FileStore:ResetOnStart=true`; the **host** deletes the folder content after taking the lock, refusing non-store folders, roots, the content root, its ancestors and folders containing `.git` | The host knows the effective folder (layered config); bash cannot parse JSON reliably without extra tools; one implementation of the safety rules; a running host blocks the reset |
| D5 | FR-019 "persisted before success" | Written and flushed to the OS before success; no fsync. Guarantee: any process death; not OS crash/power loss (documented, torn files fail startup by name) | SC-002's failure model is normal stop and forced kill; fsync of N+1 files per commit risks SC-006 (R8) |
| D6 | FR-021 "key-to-file-name mapping is reversible" | Reversible by decoding for names ≤ 120 characters; longer keys get `~<sha256>` names, reversible through the `key` field stored in the file | Path length limits on Windows (MAX_PATH 260 without long-path policy) |
| D7 | FR-022 "removed from disk eventually" | Expired files are deleted at the next start (or overwritten by a newer write of the key); no background sweeper | Dev store; expired documents are already invisible at runtime |
| D8 | FR-006 secrets = keys of `GameServer:SecretKeys` | The default list gains `Identity:Providers:PlayGames:ClientSecret` | It is a secret (P0.5 summary, 009) missing from the OPS-08 list |
| D9 | Edge case "Host environment name `Development` … test settings supply EnvironmentId" | `TestHost` default `EnvironmentId` changes from `dev` to `test` | With FR-004, `dev` + `Staging/Production/Test` (used by several tests) would fail startup |
| D10 | FR-013 "missing connection string" | `CouchbaseOptions.ConnectionString` default `couchbase://localhost` removed (breaking default) | A Production deployment missing the variable would otherwise try localhost |
| D11 | US1-1 "the script returns control (logs streaming)" vs FR-035 "keep streaming logs, stop on Ctrl+C" | The script stays in the foreground streaming logs after printing the URL block; Ctrl+C stops host and script | FR-035 is the precise requirement; returning to the prompt would need a background host and a stop script |
| D12 | FR-015 lists `moxiPort`, `bucketType`, `dbcache` | Also warns for renamed reference keys (`serverIp`, `operationTimeOut`, `compressThreshold`, `bucketName`, `localFolder`) with their new key | A silently ignored `operationTimeOut: 2000` would leave the SDK default in force while ops believes otherwise |
| D13 | FR-029 provisioning for "the registered modules" (Couchbase implied) | `--provision` with `Store` = InMemory/File exits 1 ("needs GameServer:Store=Couchbase") | A pipeline running it on a non-Couchbase flavor is misconfigured; succeeding silently would hide that |
| D14 | Edge case "Index creation … reports progress" | Progress is per item (start/end with elapsed seconds), not percentage | `system:indexes` exposes state, not build percentage, without extra admin REST calls |

## Project Structure

### Documentation (this feature)

```text
specs/011-local-env/
├── spec.md
├── plan.md                         # this file
├── research.md                     # decisions R1..R13
├── data-model.md                   # flavor table, config keys, flavor files, on-disk format
├── contracts/operations-cli.md     # host switch, exit codes, provisioning output, scripts, launch profiles
├── checklists/requirements.md
└── tasks.md                        # next stage (/speckit-tasks)
```

### Source Code (repository root)

```text
src/GameServer.Platform/
├── GameServerOptions.cs                    # StoreKind.File; SecretKeys + PlayGames secret
├── DeploymentEnvironments.cs               # DeploymentEnvironments, StandardFlavors
├── StartupChecks.cs                        # IStartupCheck, StartupCheckResult
└── Persistence/StoreProvisioning.cs        # IStoreProvisioner, ProvisioningItem, ProvisioningOutcome, ProvisioningFailedException
src/GameServer.Platform.Web/Hosting/
├── GameServerBuilder.cs                    # DeploymentEnvironmentResolution, register DeploymentIdentityCheck
├── DeploymentIdentityCheck.cs              # FR-003/FR-004
├── GameServerApplicationExtensions.cs      # merge IStartupCheck + IStartupValidator, warnings, generalized dev-only store message
├── GameServerCommandLine.cs                # HostMode, GameServerCommandLine, HostExitCodes
├── GameServerRunExtensions.cs              # RunGameServerAsync (serve / provision), listen warning
└── ProvisioningRunner.cs                   # provision mode (internal)
src/GameServer.Platform.Couchbase/
├── CouchbaseOptions.cs                     # new keys, UsesTls, no localhost default
├── CouchbaseClusterOptionsFactory.cs       # ClusterOptions mapping, TLS trust
├── CouchbaseConnection.cs                  # uses the factory; resolves TrustCertificatePath (content root)
├── CouchbaseSettingsCheck.cs               # ranges, TLS rule, legacy keys
├── CouchbaseProvisioner.cs                 # IStoreProvisioner, txn_meta first, index wait, VerifySchemaAsync
└── CouchbaseServiceCollectionExtensions.cs # register check + IStoreProvisioner; bucket check moved to the check
src/GameServer.Platform.Testing/
├── InMemoryDocumentStore.cs                # internal persistence seam, LoadCommitted, open gate
└── FileStore/{FileStoreOptions,FileDocumentStore,FileStoreServiceCollectionExtensions,IDocumentPersistence,
               FileKeyCodec,FileStoreFolder,FileCommitWriter,FileStoreLoader,FileStoreHealthProbe,FileStoreCheck}.cs
samples/SampleHost/
├── Program.cs                              # command line, store switch (File), try around composition, RunGameServerAsync
├── appsettings.json                        # cleaned base
├── appsettings.{Local,Dev,Beta,Uat,Live}.json
└── Properties/launchSettings.json          # local (default), dev-couchbase
samples/SampleGame/KestrelDemo/Assets/
├── SampleGame/SampleGameClient.cs          # default URL 5080
├── Scenes/SampleGameClient.unity           # serialized serverUrl 5080
└── Editor/SmokeBuild.cs                    # default URL 5080
sdk/unity/ci/smoke-build.ps1                # default URL 5080
tools/local/
├── run-local.ps1, run-local.sh             # new
└── start-hosts.ps1                         # ASPNETCORE_ENVIRONMENT=Dev
.gitignore                                  # _database/
README.md                                   # one primary local path, 5080, flavors pointer
docs/guides/environments-and-local-run.md   # new guide (FR-040)
docs/guides/local-real-env-testing.md       # dev flavor, provisioning pointer
docs/pcr/platform.md                        # §17 environments, stores, Couchbase settings, provisioning (FR-041)
docs/pcr/unity-sdk.md                       # sample default URL
tests/Shared/{TestHost.cs, TestStores.cs}   # EnvironmentId "test", environmentName param, GAMESERVER_TEST_STORE=File switch
tests/GameServer.Platform.Tests/Persistence/
├── FileStoreConformanceTests.cs            # suite + reopen invariant
├── FileDocumentStoreTests.cs               # codec, lock, format, malformed, expiry, reset, faulted, recovery
├── FileStoreCrashTests.cs                  # crash points, torn temp files, (opt-in) perf
└── FileStoreProcessKillTests.cs            # opt-in real kill (GAMESERVER_KILL_TEST=1)
tests/GameServer.Platform.Web.Tests/
├── FlavorValidationTests.cs                # FR-003/FR-004/FR-007/FR-009 matrix
├── GameServerCommandLineTests.cs           # switch parsing, exit codes, provision with non-Couchbase store
├── AccountPurgeTests.cs                    # abstract base: InMemory + File subclasses
└── StartupValidationTests.cs               # IStartupValidator merge, warnings
tests/GameServer.Platform.Couchbase.IntegrationTests/
├── CouchbaseSettingsTests.cs               # options mapping (unit), connect with non-default settings, validation, TLS (opt-in)
└── CouchbaseProvisioningTests.cs           # provision empty scope / re-run / schema check / data-only user / txn_meta first
tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs   # FR-005/FR-006/FR-002/FR-033 on the source files
tests/Sample.Counter.Tests/SampleHostSmokeTests.cs           # local flavor, temp folder, restart persistence
tests/GameServer.Gm.Tests/SsoTests.cs                        # drop dependency on the base-file seed
```

**Structure Decision**: no new project. The file store lives in `GameServer.Platform.Testing` next to
`InMemoryDocumentStore` so the persistence seam stays internal (research R6); flavor/deployment validation and the run
modes live in `GameServer.Platform.Web/Hosting` where the composition root already is; Couchbase-specific settings,
checks and provisioning stay in `GameServer.Platform.Couchbase`; Platform gains only the two small cross-cutting
contracts (`IStartupCheck`, `IStoreProvisioner`) and the flavor constants.

## Test strategy

| Story / rule | Unit / host tests (no infrastructure) | Couchbase integration / opt-in |
| --- | --- | --- |
| US2 file store semantics (FR-018, SC-003) | `FileStoreConformanceTests : StoreConformanceTests` (all cases, none skipped); after each test the folder is reopened and its documents must equal the store's last committed state (catches any unpersisted write path); `AccountPurgeTests` refactored to an abstract base with `InMemory` and `File` subclasses; full suite once with `GAMESERVER_TEST_STORE=File` before merge (TestHost creates a temp-folder file store) | — |
| US2 durability/atomicity (FR-019/020, SC-002) | crash points (journal tmp, journal committed, each temp, each rename, journal deleted, single tmp) → reopen → all-or-nothing; torn `journal.json.tmp` and document `.tmp` ignored; unparsable `journal.json` → startup error; write failure before commit point → `TransactionFailedException`, nothing on disk; after commit point → faulted, health unhealthy, restart replays | real-process kill loop on SampleHost local flavor (`GAMESERVER_KILL_TEST=1`) |
| US2 format, lock, expiry, malformed, reset (FR-021..025, SC-007) | codec: round trip of keys with `::`, `/`, `\`, `:`, `%`, uppercase, Unicode, device names, 500-char keys; case-fold collision property test; second `FileDocumentStore` on a locked folder fails < 5 s naming it, folder unchanged; lock released after dispose; expired session absent after reopen with `FakeClock` advanced, file deleted; malformed/wrong-key/foreign-folder/unknown-version → startup error naming files; reset refuses repo root, content root, `.git` folder, non-store folder, a locked folder | perf (`GAMESERVER_PERF=1`): 10 000-document load ≤ 10 s, p95 commit ≤ 50 ms |
| US2 refusal (FR-007, FR-027) | `Store=File` with Staging/Production → startup error naming the store and the fix; the folder is not created/locked | — |
| US2-9 / FR-028 persistence of sessions and key ring | GM sign-in on the local flavor (`WebApplicationFactory`, temp folder), restart on the same folder, session cookie still valid; player bearer still valid | — |
| US3 flavors (FR-001..010, SC-004) | misconfiguration matrix via `TestHost(environmentName:)`: `Local` without key → error naming key + `appsettings.Local.json` + table; `banana` value → error with allowed values; `live`+Development, `beta`+Production → both keys; lowercase `development` normalized (overlay folder found); standard flavor files load the table values; missing secrets in Production listed together (Couchbase password, DP master key, GM SSO via `IStartupValidator`); `SampleHostConfigurationFileTests` (no secrets in any committed file, clean base, table, `Urls`) | each Couchbase flavor file + env vars against the IT bucket becomes ready (beta/uat/live with test SSO/IAP placeholders) |
| US4 Couchbase settings (FR-011..016) | `CouchbaseClusterOptionsFactory` maps every key (timeouts, compression, pool, TLS flag, durability timeout unchanged); `CouchbaseSettingsCheck`: each out-of-range value, missing required keys, bad scheme, unreadable/invalid CA file, Production non-TLS error, Staging warning, Development silent, legacy keys → warnings | connect with every setting changed from default and run the conformance smoke; TLS with self-signed CA (`COUCHBASE_TLS_CONNSTR` + `COUCHBASE_CA_PATH`, skip with message otherwise) |
| US5 provisioning (FR-029..032, SC-005) | command-line parsing (switch removed from host args); provision with InMemory/File → exit 1; invalid identity → exit 1 without touching the store; DP/GM/IAP secrets absent → not required | empty throw-away scope: exit 0, `txn_meta` reported first, all indexes online; re-run: exit 0, everything `present`; data-only RBAC user created by the test: host with startup provisioning off becomes ready; drop one collection → host exits 1 naming it and `--provision`; unknown bucket → exit 2 naming the bucket; cleanup scope + user |
| US1 local run (FR-033..039, SC-001) | `--urls` overrides `Urls` from the local file; listen warning logged for `0.0.0.0` in Development, not for loopback, not in Staging; smoke test on the local flavor (temp folder) + restart persistence | manual acceptance checklist (Windows PowerShell 5.1, PowerShell 7, Git Bash; macOS/Linux bash when available): clean run ≤ 60 s, URLs printed, guest login + command, Ctrl+C graceful, restart keeps player, busy port exit 4, `-Port`, `-Reset`, `-Lan` warning, locked folder exit 6; `start-hosts.ps1 -Count 2` still ready on 5080/5081; Unity E2E smoke on 5080 |
| US6 docs (SC-008) | test that every configuration key named in the new guide and PCR §17 tables exists as a bound property (reflection over `GameServerOptions`, `CouchbaseOptions`, `FileStoreOptions`) | review |
| Arch | existing rules unchanged; new rule: no assembly other than `GameServer.Platform.Testing` references `FileDocumentStore` except hosts and tests (keeps the dev store out of module code) | — |

## Implementation phases (input for tasks.md)

1. **Foundations (blocking)** — `StoreKind.File`; `DeploymentEnvironments`, `StandardFlavors`; `IStartupCheck`,
   `StartupCheckResult`; `DeploymentEnvironmentResolution` in `AddGameServer` + `DeploymentIdentityCheck`;
   `UseGameServer` merges checks + `IStartupValidator`, logs warnings, generalized dev-only store message;
   `SecretKeys` addition; `IStoreProvisioner`/`ProvisioningItem`/`ProvisioningFailedException`; `GameServerCommandLine`,
   `HostExitCodes`, `RunGameServerAsync` (serve path + listen warning; provision path stubbed to "needs Couchbase");
   `TestHost` (`EnvironmentId=test`, `environmentName`), fix tests depending on `dev`; flavor validation tests.
2. **US2 file store** — internal seam in `InMemoryDocumentStore` (every mutation path, open gate, `LoadCommitted`);
   `FileKeyCodec`; `FileStoreFolder` (lock, `store.json`, reset safety, path budget); `FileCommitWriter` (single,
   journal, faulted, Windows retry, crash points); `FileStoreLoader`; `FileDocumentStore`, options, DI extension, health
   probe, `FileStoreCheck`; conformance + reopen, crash, codec, lock, malformed, expiry, reset tests; `AccountPurgeTests`
   base/subclasses; `TestStores` env switch; opt-in perf and kill tests.
3. **US3 sample host flavors** — cleaned base + five flavor files; `Program.cs` (command line, store switch, `try`
   around composition, `RunGameServerAsync`); `.gitignore`; `SampleHostConfigurationFileTests`; smoke test on the local
   flavor with restart; `SsoTests` fix; `start-hosts.ps1` sets `Dev`.
4. **US4 Couchbase settings** — `CouchbaseOptions` keys; `CouchbaseClusterOptionsFactory` (+ TLS trust);
   `CouchbaseConnection` uses it; `CouchbaseSettingsCheck` (ranges, TLS rule, legacy keys); bucket check moved out of
   `AddCouchbaseDocumentStore`; unit + integration tests (TLS opt-in).
5. **US5 provisioning** — `CouchbaseProvisioner : IStoreProvisioner` (`txn_meta` first, KV visibility wait, existing
   index names, online wait, verify, progress); `VerifySchemaAsync` on startup when provisioning is off; provision mode
   in `RunGameServerAsync` (`ProvisioningRunner`, output contract, timeout, exit codes); integration tests incl.
   data-only user.
6. **US1 local run** — `launchSettings.json`; `run-local.ps1`, `run-local.sh`; Unity sample URL (code, scene, editor
   smoke) and `smoke-build.ps1`; manual acceptance runs recorded in tasks.md.
7. **US6 docs and close** — README (one primary local path, Couchbase multi-instance as "real environment"), new guide
   `docs/guides/environments-and-local-run.md` (flavor table, required env vars per flavor, Couchbase keys + mapping +
   RBAC roles, file store layout/reset/limits/security note, provisioning, scripts, profiles), update
   `local-real-env-testing.md`, PCR platform §17 + unity-sdk; docs key-existence test; full suite (in-memory, file
   switch, Couchbase) → tech-lead review → resume file `AgentMemories/features/p05-speckit/00_summary.md`.

Phase 2 and phase 4 depend only on phase 1 and can run in parallel; phase 3 needs 1 and 2; phase 5 needs 4; phase 6
needs 3; phase 7 last.

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The persistence seam changes `InMemoryDocumentStore`, which 600+ tests use | Semantics regress everywhere | Seam is a no-op without persistence; full suite before and after; reopen invariant catches missing persistence calls |
| Cleaning the base file changes inputs of every test project that copies SampleHost's `appsettings.json` (GM seed, `EnvironmentId=dev`) | Unrelated tests fail | Grep list in tasks (`SsoTests` known); run the whole suite in phase 3; `TestHost` sets every required key itself |
| Windows antivirus/indexer holds files briefly → rename/delete `IOException` | Spurious commit failures locally | Bounded retry (5 × 20 ms) on sharing violations; failure before the commit point is a clean `TransactionFailedException`; guide recommends a Defender exclusion |
| SDK 3.10 property names for compression/pool/TLS trust or the unknown-collection exception differ from research | Build errors or a wrong "missing" classification | **[VERIFY]** items with fallbacks in research R5/R11; integration test pins the unknown-collection behaviour on 7.6.12 |
| `ASPNETCORE_URLS` vs `Urls` precedence or `dotnet run` working directory differ from assumptions | Script/IDE on an unexpected port or folder | Launch profile and local file carry the same URL; scripts pass `--urls` and set the working directory explicitly; test asserts `--urls` wins |
| POSIX script signal handling (Git Bash/mintty, SIGINT to background jobs) | Ctrl+C does not stop the host gracefully | `dotnet run` runs in the foreground, poller in background; manual acceptance on Git Bash; worst case the host is killed, which is safe (commits are durable) |
| Production TLS rule rejects an existing non-TLS live cluster | Live cannot start | No live deployment exists yet (P0.5); rule documented in PCR before the first live deploy; Staging only warns |
| `IStartupValidator` merge reports module option errors twice (once merged, once at `StartAsync`) | Confusing double output | Merge happens only on the error path before `StartAsync`; startup never reaches `StartAsync` when errors exist |
| Data folder holds unencrypted Data Protection keys and guest credentials | Leak if the folder is shared | Development only, ignored by git, loopback by default; guide warning |
| Hand edits while the host runs are overwritten | Developer confusion | Documented (Q5); hot reload out of scope |
| Index creation on a large live collection exceeds the provisioning timeout | Pipeline step fails | Exit 2, idempotent re-run continues; `ProvisioningTimeoutSeconds` up to 7200 |

## Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| Constitution IV "I/O MUST async": the file store performs synchronous file writes on the request path, under the `InMemoryDocumentStore` lock | Disk order must equal commit order and the commit is a synchronous critical section (`System.Threading.Lock`, which cannot be held across `await`); writing inside it is the only way to persist before visibility without re-architecting the store | An async rewrite of `InMemoryDocumentStore` (async lock on every path) risks the semantics 600+ tests and the conformance suite rely on, for a store that is refused outside Development/Test and serves one developer; writing after the lock would let a later commit reach disk before an earlier one |
| Constitution VI "Couchbase integration tests MUST run in CI": still local-only | No CI pipeline exists yet (CI-01, owner skipped packaging/CI) | Not introduced by this slice; integration tests run against EE 7.6.12 before merge and skip with a message without `COUCHBASE_CONNSTR`, as in 007–010 |
