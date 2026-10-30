# Tasks: Environments per flavor and easy local run (P0.5 batch, slice 011 local-env)

**Input**: [plan.md](plan.md) (authoritative; deviations D1–D14 override the spec), [spec.md](spec.md),
[research.md](research.md) (R1–R13), [data-model.md](data-model.md), [contracts/operations-cli.md](contracts/operations-cli.md)

**Tests**: mandatory (constitution VI). Each phase has its own tests. Couchbase tests skip with a message when
`COUCHBASE_CONNSTR` is not set and run locally against EE 7.6.12 before merge (no CI, CI-01). Scenario ids (`US1-3`)
refer to the spec's acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USx] description with paths`. `[P]` = different files, no open dependency.

**[VERIFY]**: an SDK or runtime fact from research to confirm on the pinned versions (CouchbaseNetClient 3.10.0,
.NET SDK 10.0.403, Couchbase EE 7.6.12) **before** coding the rest of that task. Record the result as a `Note:` on the
task. If the fact is wrong, apply the stated fallback and record any change to keys or docs as a deviation.

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; Couchbase variables as in `AgentMemories/features/p05-speckit/00_summary.md`
(password from the user variable `COUCHBASE_PW`, never printed). Never grep recursively over `samples/` (Unity `Library/`).
`.specify/feature.json` already points to `specs/011-local-env`.

## Phase 1: Foundational (blocks every story)

- [X] T001 [P] In `src/GameServer.Platform/GameServerOptions.cs`, add `StoreKind.File`. The class default stays
  `InMemory`. Add `Identity:Providers:PlayGames:ClientSecret` to the default `SecretKeys` (D8).
  Done when: the solution builds; `new GameServerOptions().SecretKeys` has 6 entries; nothing else reads `StoreKind`
  except `samples/SampleHost/Program.cs`.
- [X] T002 [P] Create `src/GameServer.Platform/DeploymentEnvironments.cs` with `DeploymentEnvironments` (constants,
  `All`, `TryNormalize` → canonical spelling) and `StandardFlavors` (`DeploymentEnvironmentById`, ordinal lowercase
  keys `local, dev, beta, uat, live`; `Describe()`), as in research R2.
  Note: `Describe()` uses ASCII `->` (`local->Development, …`) instead of `→`, so the text survives any console code
  page.
- [X] T003 [P] Create `src/GameServer.Platform/StartupChecks.cs` with `IStartupCheck` and `StartupCheckResult`
  (`Errors`, `Warnings`), as in research R4.
- [X] T004 [P] Create `src/GameServer.Platform/Persistence/StoreProvisioning.cs` with `IStoreProvisioner`,
  `ProvisioningOutcome`, `ProvisioningItem(Kind, Name, Outcome, Elapsed)` and
  `ProvisioningFailedException(kind, name, cause, inner)`, as in research R10.
  Note: `ProvisioningFailedException` also exposes `Cause` (the provision report prints `FAILED <kind> <name>:
  <cause>`).
- [X] T005 [P] Write unit tests in `tests/GameServer.Platform.Tests/Config/DeploymentEnvironmentsTests.cs` (after T002):
  - `TryNormalize`: `development`, `STAGING` and `Test` return the canonical spelling; `Local`, `Dev`, `banana`, `""`
    and `null` are refused.
  - `StandardFlavors` equals the FR-002 table.
  - `Describe()` lists all five flavors.
- [X] T006 Deployment environment resolution in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs` (after T002,
  T003). Replace the unvalidated fallback at lines 118–120 with `DeploymentEnvironmentResolution`:
  - A configured value is normalized. Without a configured value, the host environment name is used only when it is
    itself a deployment environment.
  - An invalid value keeps the raw value, so it never equals Development or Test (fail-closed).
  - `FlavorFileFound` comes from the configuration sources.
  - The game config overlay loads `config/env/<canonical>`.

  Register `DeploymentIdentityCheck : IStartupCheck` in a new file
  `src/GameServer.Platform.Web/Hosting/DeploymentIdentityCheck.cs`. It enforces FR-003 (message names the key, the
  allowed values, the expected `appsettings.<Name>.json` with found yes/no, and `StandardFlavors.Describe()`) and
  FR-004 (message names both keys and the expected value; `Test` is not exempt).
  Note: `DeploymentEnvironmentResolution` and the check live together in `DeploymentIdentityCheck.cs`; the record also
  carries the content root for the message. FR-004 is skipped when FR-003 already failed (one error per cause).
- [X] T007 Startup validation in `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs` (after T001,
  T003):
  1. `UseGameServer` runs every `IStartupCheck` and merges its errors into the one `GameServerStartupException`.
     Warnings are logged once under the category `GameServer.Startup`.
  2. It calls `IStartupValidator.Validate()` when the service is registered, and merges
     `OptionsValidationException.Failures` (single, or nested in an `AggregateException`).
  3. The development-only store message at line 131 becomes the R4 text: it names `{Store}` (`InMemory`/`File`), the
     environment and the fix.

  Update the two assertions in `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs` (lines 78 and 87) that
  match the old `in-memory store is configured` text.
  Deviation (smallest fix): `GmModule.MapGmApi` reads `IOptions<GmOptions>.Value` while mapping, so in Production
  without SSO the mapping threw `OptionsValidationException` before the report existed. `UseGameServer` now maps each
  module inside a `try` that adds those failures (deduplicated) to the report and continues. The store kind in the
  message comes from `GameServer:Store` (`File` → File, otherwise InMemory).
- [X] T008 Command line and run modes (after T003, T004, T007). Add `<InternalsVisibleTo Include="GameServer.Platform.Web.Tests" />`
  to `src/GameServer.Platform.Web/GameServer.Platform.Web.csproj`. Create these files:
  - `src/GameServer.Platform.Web/Hosting/GameServerCommandLine.cs`: `HostMode`, `GameServerCommandLine.Parse`
    (removes `--provision`, keeps the other arguments in order) and `HostExitCodes`.
  - `src/GameServer.Platform.Web/Hosting/GameServerRunExtensions.cs`: `RunGameServerAsync(app, mode)`.
  - `src/GameServer.Platform.Web/Hosting/ProvisioningRunner.cs` (internal).

  Serve mode runs `UseGameServer` and then `RunAsync`. A `GameServerStartupException` thrown by validation or by a
  hosted service's start (unwrap aggregate and inner exceptions) prints the existing stderr format and returns 1.
  - Never call `Environment.Exit`: `WebApplicationFactory<Program>` runs the same entry point in the test process.
  - Listen warning (FR-038): register on `ApplicationStarted`. When `AllowsDevelopmentTools` is true and any
    `IServerAddressesFeature` address is not loopback, log one warning.
  - Make the address classifier internal so it can be tested.

  Provision mode (the full contract, against the interface, so a fake provisioner can test it):
  - Validate only `registry.Validate(options, null)` and the `IStartupCheck`s. Errors → 1.
  - No `IStoreProvisioner` registered → 1 with "`--provision` needs GameServer:Store=Couchbase" (D13).
  - Print the header and the `provision:` lines, then the summary of contracts §2.
  - Timeout: `Couchbase:ProvisioningTimeoutSeconds` (default 600) plus Ctrl+C. A `ProvisioningFailedException` or a
    timeout → 2, with the `FAILED`/`stopped after` lines.
  - Read the header's bucket name and the timeout from `IConfiguration` (`Couchbase:BucketName`,
    `Couchbase:ProvisioningTimeoutSeconds`), because Platform.Web cannot reference `CouchbaseOptions`.
  - An internal overload takes `TextWriter` out and err, for the tests.
  Note: provision mode writes `IStartupCheck` warnings to stderr as `warning: …` (no logger runs there). Validation
  errors are reported before the D13 "no provisioner" error. `ServeAsync(app, TextWriter)` and `FindStartupException`
  are internal for tests. The app is disposed after a provision run (serve: `RunAsync` disposes it).
- [X] T009 Test host defaults in `tests/Shared/TestHost.cs`: `DefaultSettings()` sets `GameServer:EnvironmentId` to
  `test` (D9). `StartAsync` gains an optional `string environmentName = "Development"` parameter. Fix the host tests
  that depend on `dev`:
  - `tests/GameServer.Gm.Tests/SsoTests.cs:160`: `EffectivePermissions("test", "dev")` → `"test"`.
  - `tests/GameServer.Platform.Web.Tests/AccountDeletionTests.cs:338`: seeded mapping `environmentId`, for consistency.

  Unit tests that build `new GameServerOptions { EnvironmentId = "dev" }` are unaffected and stay as they are.
  Done when: the full existing suite is green.
  Note: `TestHost.BuildApp(...)` added (composition without `UseGameServer`/`StartAsync`) for the run-mode tests of
  T011.
- [X] T010 Flavor validation tests in a new file `tests/GameServer.Platform.Web.Tests/FlavorValidationTests.cs`, using
  `TestHost.StartAsync(environmentName:)` with the deployment-environment key removed from the settings (after T006,
  T007, T009). Cases:
  - `Local` without the key → an error naming the key, `appsettings.Local.json` (found: no) and the flavor table
    (US3-2). The same start also reports `test authentication is enabled in 'Local'`, which proves fail-closed.
  - `banana` → the allowed values (US3-3).
  - `live` with Development, and `beta` with Production → both keys plus the expected value (US3-4). `dev` with Test →
    error.
  - `qa2` with Staging → no environment-id error.
  - `development` in lowercase → `GameServerOptions.DeploymentEnvironment == "Development"`, and the overlay is found in
    a temporary `ConfigPath` that has `env/Development`.
  - Host name `Staging` without the key → valid fallback.
  - `Store` InMemory in Staging → the generalized message (FR-007).
  - Production with nothing configured → one exception that lists the GM SSO key (through `IStartupValidator`), the
    data protection master key and the guest pepper together (US3-7, FR-009).

  In `StartupValidationTests.cs`, add:
  - a test `IStartupCheck` error that appears in the message;
  - a warning that is logged exactly once under `GameServer.Startup`;
  - a module `ValidateOnStart` failure that is reported by `UseGameServer`, before `StartAsync`.
  Note: the tests set `GameServer:DeploymentEnvironment` to null instead of removing it: the SampleHost base file
  copied into the test output still sets `Development` until T025 cleans it, and a null in-memory value hides it.
- [X] T011 Command-line tests in `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs` (after T008).
  Parsing:
  - `--urls x --provision --FileStore:ResetOnStart=true` → Provision mode, and the switch is removed;
  - no switch → Serve mode.

  Provision mode, on a `TestHost`-built app with a fake `IStoreProvisioner`:
  - success → 0, with `provision:` lines and a summary of created/present counts;
  - `ProvisioningFailedException` → 2 with a `FAILED` line;
  - a slow fake with `Couchbase:ProvisioningTimeoutSeconds=1` → 2;
  - invalid identity (`live` with Development) → 1, and the fake is never called;
  - Production without GM SSO, IAP, pepper or master key → 0 (US5-4, FR-031);
  - no provisioner registered → 1 with the D13 text.

  Serve mode: a startup error → 1. Listen classifier: `0.0.0.0`, `[::]`, `*`, `+`, `192.168.1.5` and `myhost` are
  non-loopback; `localhost`, `127.0.0.1` and `[::1]` are loopback. The warning is emitted only when development tools
  are on (FR-038, US1-7 host side).

**Checkpoint**: Release build has 0 warnings. The full existing suite is green. The SampleHost base file still carries
Development, so the sample host and `start-hosts.ps1` are unchanged.

## Phase 2: US2 — File store (P1)

- [X] T012 [US2] Persistence seam (research R6):
  - Create `src/GameServer.Platform.Testing/FileStore/IDocumentPersistence.cs` with the internal `IDocumentPersistence`
    and `DocumentChange`.
  - In `src/GameServer.Platform.Testing/InMemoryDocumentStore.cs`, add the internal constructor `(IClock, IDocumentPersistence)`
    and an open gate: store access before `LoadCommitted` throws, but `UseCollections` and
    `UseTransactionOnlyCollections` are allowed because `UseGameServer` calls them before `Open`.
  - Every mutation path calls `Persist` exactly once, with the full change set, after its checks and before
    `_documents` changes: `TryCommit`, KV `Insert`/`Replace`/`Remove`, `InsertIfAbsentAsync`, `MarkOutboxRelayedAsync`,
    `MarkOutboxFailedAsync` and `Seed`.
  - `Live()` expiry does not persist.
  - A persistence failure in `TryCommit` surfaces as `TransactionFailedException`. On KV paths the `IOException`
    propagates.
  - Add `<InternalsVisibleTo Include="GameServer.Platform.Tests" />` to `src/GameServer.Platform.Testing/GameServer.Platform.Testing.csproj`.

  Tests in `tests/GameServer.Platform.Tests/Persistence/InMemoryPersistenceSeamTests.cs`, with a recording persistence:
  - each mutation path is persisted once, with the absolute `ExpiresAt` from `FakeClock`;
  - a throwing persistence applies nothing;
  - a conflict-retried transaction persists only the committed attempt;
  - access before `LoadCommitted` throws;
  - `StoreFaultInjector` `FailBeforeCommit` fires before `Persist`.

  Done when: the full existing suite is green, unchanged.
  Note: KV `RemoveAsync` persists only when the key is still in memory (an expired entry still has a file); a
  transaction with no writes does not call `Persist`; any persistence exception in `TryCommit` becomes
  `TransactionFailedException`.
- [X] T013 [P] [US2] `FileKeyCodec` (research R7) in `src/GameServer.Platform.Testing/FileStore/FileKeyCodec.cs`.
  Tests in `tests/GameServer.Platform.Tests/Persistence/FileKeyCodecTests.cs`:
  - the data-model §6 examples;
  - round trips of `::`, `/`, `\`, `:`, `%`, uppercase, Unicode and 500-character keys;
  - device names (`con` → `%63on`);
  - more than 120 characters → `~` + 52 base32 characters;
  - a property test: distinct keys give names that are distinct under `OrdinalIgnoreCase`;
  - `TryDecode` refuses lowercase hex, encoded literals and foreign names;
  - the empty key is refused.

  The codec tests live in their own file, not in `FileDocumentStoreTests.cs`, so this task can run in parallel.
- [X] T014 [P] [US2] `FileStoreFolder` in `src/GameServer.Platform.Testing/FileStore/FileStoreFolder.cs`:
  - `.lock` opened with `FileShare.None`;
  - `store.json` created in an empty folder, or checked. A missing file in a non-empty folder, another format or an
    unknown version → `GameServerStartupException` with reset guidance;
  - document paths `g_<gameId>/<collection>/<name>.json`;
  - reset after the lock: delete everything except `.lock` and recreate `store.json`. Refuse a non-store non-empty
    folder, a file-system root, the user profile, the content root or its ancestors, and a folder with a `.git` entry
    (D4);
  - the Windows path-budget computation (≤ 259).
  Deviation: open order is lock → reset (when requested) → format → recovery → load. The format check runs after the
  reset so that an unknown version can be reset, as its guidance says; the reset itself refuses anything but a store
  folder or an empty one. Read-only checks run before the lock so nothing (not even `.lock`) is created in a foreign
  folder or a refused reset target. The path budget counts the temporary name `<stem>.json.tmp` (129 characters), 3
  stricter than R7's 126.
- [X] T015 [US2] `FileCommitWriter : IDocumentPersistence` (research R8) in
  `src/GameServer.Platform.Testing/FileStore/FileCommitWriter.cs` (after T012, T013, T014):
  - Document file `{key, expiresAtUtc ("O" or null), content}`, UTF-8, indented.
  - One change: write a temp file, flush, then `File.Move(overwrite)`; or delete.
  - Several changes: `journal.json.tmp` → rename (the commit point) → apply → delete the journal.
  - Failure before the commit point: clean up and throw.
  - Failure after the commit point: return normally and enter the faulted state. Every later `Persist` throws. Log one
    `LogError` naming the file and the cause.
  - Windows sharing violations: retry 5 × 20 ms.
  - Internal crash hook `FileCommitStep`: `JournalTempWritten`, `JournalCommitted`, `DocumentTempWritten(i)`,
    `DocumentReplaced(i)`, `JournalDeleted`, `SingleTempWritten`.
  - Times come from `IClock` only (`BannedSymbols.txt`). No fsync (D5).
  Note: the writer needs no clock (expiry is already absolute when it arrives); the crash hook throws
  `SimulatedCrashException` and closes the writer. Document JSON is written with relaxed escaping (readable
  non-ASCII).
- [X] T016 [US2] `FileStoreLoader` (research R9) in `src/GameServer.Platform.Testing/FileStore/FileStoreLoader.cs`
  (after T013–T015):
  - Delete `*.tmp`. Replay `journal.json` (idempotent). An unparsable journal → startup error naming it.
  - Enumerate the declared collections (all folders when the store is unrestricted). Undeclared folders → one
    information log.
  - Parse in parallel with `Parallel.ForEach`.
  - Validate the shape and name ↔ key (decode, or SHA-256 for `~` names).
  - Collect every malformed file: name the first 20 and count the rest.
  - Drop expired documents and delete their files, using `IClock`.
- [X] T017 [US2] File store assembly (research R6) in `src/GameServer.Platform.Testing/FileStore/` (after T015, T016).
  Create:
  - `FileStoreOptions.cs` (`FileStoreOptions`, `FileStoreSettings`);
  - `FileDocumentStore.cs` (`Open` order: lock → format → reset → recovery → load → `LoadCommitted`; idempotent;
    `EnsureInitializedAsync` opens once; `Dispose`/`StopAsync` release the lock; log `Loaded {Count} documents from
    {Folder} in {Ms} ms`);
  - `FileStoreHealthProbe.cs` (unhealthy when faulted);
  - `FileStoreCheck.cs` (`FileStore:Folder` required when `Store=File`; Windows path budget);
  - `FileStoreServiceCollectionExtensions.cs`.

  The extension method `AddFileDocumentStore(services, configuration, environment)`:
  - reads `FileStore:Folder` and `FileStore:ResetOnStart` from `IConfiguration` without the binder package, because
    Testing only references Platform (no new package);
  - resolves the folder against the content root;
  - registers `Store` for the four store interfaces (as `AddInMemoryDocumentStore(store)` does), `IStoreInitializer`,
    the hosted service and `IStartupCheck`;
  - **replaces** the `IStoreHealthProbe` that `AddGameServer` added with `TryAdd`.

  Add a public overload `AddFileDocumentStore(services, FileDocumentStore store)` for `TestHost` (T022).
  Deviation: `StopAsync` keeps the lock; `Dispose` releases it. Hosted services that stop after the store (lease
  release, key ring) still write during shutdown. The configuration overload registers a container-owned store
  (disposed with the host); the instance overload leaves disposal to the caller (`TestHost`).
- [X] T018 [US2] Conformance tests in `tests/GameServer.Platform.Tests/Persistence/FileStoreConformanceTests.cs : StoreConformanceTests`
  (after T017). Each instance uses a temporary folder and the same collections as `InMemoryStoreConformanceTests`.
  After every test, dispose the store, reopen the folder with a new `FileDocumentStore`, and assert that the reopened
  documents equal the last committed state (the reopen invariant). No case is skipped (FR-018, SC-003, US2-3).
- [X] T019 [US2] File store behaviour tests in `tests/GameServer.Platform.Tests/Persistence/FileDocumentStoreTests.cs`
  (after T017):
  - Lock: a second store on the folder fails in under 5 s, naming the folder, and the folder bytes are unchanged; the
    lock is released after `Dispose`, and after the handle closes without `Dispose` (crash) (US2-5, SC-007).
  - Format: an empty folder gets `store.json`; a foreign non-empty folder, another format or an unknown version → error
    with reset guidance.
  - Malformed files: invalid JSON, key ≠ name, non-object `content`, bad `expiresAtUtc`, a foreign file name → all
    named in one error, nothing loaded (US2-7, FR-024).
  - Expiry: a document with expiry, the clock advanced while the store is closed → absent after reopen and the file is
    deleted (US2-4, FR-022).
  - Layout: `g_<gameId>/<collection>/<encoded>.json`. A hand edit made while closed is visible after reopen (US2-6).
  - Reset: `ResetOnStart` gives an empty store. It is refused for the repository root, the content root, an ancestor, a
    `.git` folder, a non-store folder and a file-system root. A locked folder → lock error, nothing deleted.
  - Deleting the folder = reset (FR-025).
  - Failure before the commit point → `TransactionFailedException`, and nothing on disk.
  - Failure after the commit point → success is returned, `IsFaulted` is set, the next write throws, the health probe is
    unhealthy, and a reopen replays the journal.
  - `FileStoreCheck` on Windows with a long folder → path-budget error.
  Note: failures before/after the commit point are induced with a directory at the temporary or target path. The reset
  refusals for the repository root, the content root and its ancestors are asserted on `ResetRefusal` only; the
  store-level refusal uses a throw-away folder with a `.git` entry (never the real repository).
- [X] T020 [US2] Crash tests in `tests/GameServer.Platform.Tests/Persistence/FileStoreCrashTests.cs` (after T017):
  - Abort at every `FileCommitStep` of a multi-document commit (insert, replace and remove mixed) and of a single
    write, release the lock handle, reopen → all-or-nothing. Before `JournalCommitted` the commit is absent; from it
    on, the commit is present (FR-020, SC-002, US2-2).
  - A torn `journal.json.tmp` or document `.tmp` is ignored and deleted.
  - An unparsable `journal.json` → error naming it.
  - Opt-in performance (`GAMESERVER_PERF=1`, otherwise skip with a message): 10 000 documents load in ≤ 10 s, and p95
    ≤ 50 ms over 500 commits of 6 documents. Print the numbers for PCR (SC-006).
  Note (perf, `GAMESERVER_PERF=1`, Windows 11, 2026-10-30): on D: (where `_database` lives) load 10 000 documents 546
  ms, 500 commits of 6: p50 22.8 ms, p95 44.8 ms, max 404 ms — pass. In `%TEMP%` on C: p95 was 82 ms (fail): raw
  write+rename there costs ~3.5 ms vs ~0.8 ms on D: (antivirus). Run with `TMP`/`TEMP` on the data drive, or exclude
  the folder from Defender (guide, T050).
- [X] T021 [US2] Host tests in a new file `tests/GameServer.Platform.Web.Tests/FileStoreHostTests.cs`, using `TestHost`
  with `services: s => s.AddFileDocumentStore(...)` on a temporary folder (after T017):
  - Staging or Production with `Store=File` → the generalized message names the File store and the fix, and the
    folder is neither created nor locked (US2-8, FR-007, FR-027).
  - A guest login and a counter command → the files exist before the response (FR-019, US2-1).
  - Dispose, then a new host on the same folder → the same snapshot, and the old bearer token still works (FR-028).
  Note: also covers the `TestHost` file switch itself (folder created while running, deleted with the host).
- [X] T022 [US2] Test store switch (after T017). Create `tests/Shared/TestStores.cs`; the plan lists it, but the file
  does not exist yet. Use it from `tests/Shared/TestHost.cs`: when `GAMESERVER_TEST_STORE=File` and the caller passes
  no `store:`, `TestHost` creates a `FileDocumentStore` on a unique temporary folder, registers it through the T017
  overload, exposes the inner `InMemoryDocumentStore` as `Store`, and deletes the folder on dispose. Tests that share
  an explicit store between two hosts stay in memory.
  Done when: the full suite with `GAMESERVER_TEST_STORE=File` is green, or every failure is listed as a Note with its
  reason.
  Note: `TestHost.StartAsync` gains `storeKind:`; File applies only when no `store:` is passed. Result: full suite
  with `GAMESERVER_TEST_STORE=File` green (787 total, 730 passed, 57 skipped = Couchbase/opt-in), no failure to list.
  A `beforeUseGameServer` callback that touched the store would throw on the file store (not open yet); none does.
- [X] T023 [US2] Run the purge tests on both stores (SC-003; Couchbase already runs them in `CouchbaseLeaseAndPurgeTests`,
  after T017). In `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs`, turn the sealed class into an abstract
  base with a virtual store factory, plus the subclasses `InMemoryAccountPurgeTests` and `FileAccountPurgeTests`.
  `host.Store.Faults` keeps working on the inner store. Both subclasses are green.
  Note: the "factory" is an abstract `StoreKind` property passed to `TestHost`. Both subclasses share one xUnit
  collection: the purge metrics are read through a process-wide `MeterListener`, and the two classes in parallel
  polluted each other (measured failure before the fix).
- [X] T024 [P] [US2] Architecture rule in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` (after T017): no
  framework or module assembly other than `PlatformTesting` depends on `FileDocumentStore` (hosts and tests are not in
  `AllFrameworkAndModules`). The existing rules stay green.

**Checkpoint**: Release build is clean. The full suite is green in memory. T022 is green with `GAMESERVER_TEST_STORE=File`.

## Phase 3: US3 — Sample host flavors (P1)

- [X] T025 [US3] Configuration files in `samples/SampleHost/`:
  - Clean `appsettings.json` down to the data-model §3 base: remove `DeploymentEnvironment`, `EnvironmentId`, `Store`
    and `Gm:Bootstrap` (FR-005).
  - Create `appsettings.Local.json`, `appsettings.Dev.json`, `appsettings.Beta.json`, `appsettings.Uat.json` and
    `appsettings.Live.json` with exactly the data-model §3 content. The Dev file keeps
    `Couchbase:ConnectionString=couchbase://localhost`, which is needed after D10.
  - The GM staff seed exists only in the Local and Dev files.
  Note: `appsettings.Uat.json` uses the bucket `sample-uat`. The flavor files are copied into the output of every test
  project that references SampleHost (8 projects), so `TestHost` now drops the `appsettings.{EnvironmentName}.json`
  source (it sets every key itself; a test host named `Local` never loads the sample's Local file). With the base file
  clean, the T010 null masks became `s.Remove("GameServer:DeploymentEnvironment")` (the key still comes from
  `TestHost.DefaultSettings`, not from the base file).
- [X] T026 [US3] `samples/SampleHost/Program.cs` (after T008, T017, T025):
  - `var cl = GameServerCommandLine.Parse(args)`, then `WebApplication.CreateBuilder(cl.HostArgs)`.
  - Store switch: `File` → `AddFileDocumentStore(builder.Services, builder.Configuration, builder.Environment)`;
    `Couchbase` and `InMemory` stay as today.
  - Wrap composition in a `try`, because `AddObservability` throws by design: stderr, then `return 1`.
  - End with `return await app.RunGameServerAsync(cl.Mode);`.
  - Keep `public partial class Program`.
  Note: only `GameServerStartupException` is caught around composition (`WebApplicationFactory` aborts the entry point
  with `HostAbortedException`, which must pass); a configuration binding error keeps the .NET default exit ("other" in
  contracts §1). Verified: `dotnet SampleHost.dll` without a flavor exits 1 with the EnvironmentId, InMemory store,
  master key, pepper and GM SSO errors together, never Development.
- [X] T027 [P] [US3] `.gitignore` gains `_database/`.
  Done when:
  - `git check-ignore -v samples/_database/dbbase/store.json` reports the new rule;
  - `git check-ignore samples/SampleHost/appsettings.Local.json` reports nothing (the existing `appsettings.*.local.json`
    rule must not hide flavor files on a case-insensitive checkout).
  Note: both checks verified (`.gitignore:30:_database/`; the Local file is not ignored).
- [X] T028 [US3] Repository configuration tests in a new file `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs`
  (after T025, T026). They read the **source** files (walk up to `GameServer.slnx`, then glob
  `samples/SampleHost/appsettings*.json`, so a new file is covered automatically) with `ConfigurationBuilder`, and
  assert the 4 rules of research R3:
  - no key of `new GameServerOptions().SecretKeys` has a value in any file, Local and Dev included (FR-006, US3-8);
  - the base file is clean (FR-005);
  - each flavor file carries its table values (FR-002, US3-1);
  - only the Local file sets `Urls`, and it is loopback on port 5080 (FR-033).

  Host case: `WebApplicationFactory<Program>` with `UseEnvironment("Production")` and no flavor file → startup fails,
  naming `GameServer:EnvironmentId` and the store refusal, never Development (US3-5).
  Note: two extra rules: every standard flavor file exists, and `Gm:Bootstrap` (the committed seed password) appears
  only in files whose `DeploymentEnvironment` is Development (R3). The host case reads the report from stderr
  (`Console.SetError`, so the class runs in the non-parallel collection `ConsoleErrorCollection`); the factory itself
  throws `InvalidOperationException` ("The server has not been started…") because the entry point returned 1.
- [X] T029 [US3] Smoke tests in `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` on the real local flavor (after
  T026). Setup: `UseEnvironment("Local")`, `UseSetting("FileStore:Folder", <temp>)`, jobs off. Keep the outbox
  sweeper **on** in one case, because `OutboxSweeper` does not await `IStoreInitializer` and relies on
  `KeyRingService.StartingAsync` opening the store first. Cases:
  - the existing flow is green;
  - restart: dispose the first factory (this releases the lock), start a new factory on the same folder → counter
    `rev` 1, and the guest bearer token still works (US1 persistence, US2-1);
  - GM local sign-in with the Local seed (`GmBrowser` from `tests/Shared`), then restart → the session cookie is still
    accepted, so the key ring and the GM session survived (US2-9, FR-028).

  The temporary folder is deleted at the end.
  Note: the factory sets `GameServer:Hosting:DrainDelaySeconds=0` (otherwise each dispose waits 5 s: 28 s → 3.5 s).
  `tests/Shared/GmBrowser.cs` gains `Browser`/`GmLoginAsync` overloads on `TestServer` (the `TestHost` ones delegate). The
  first case also asserts the data is in the temporary folder (`UseSetting` beats the flavor file's
  `../_database/dbbase`).
- [X] T030 [US3] In `tests/GameServer.Gm.Tests/SsoTests.cs`, remove the override
  `values["Gm:Bootstrap:Staff:0:Roles:0"] = "Support"` and its comment, because the base file no longer seeds staff
  (after T025). Grep `tests/` for other dependencies on the base-file seed (`admin`, `dev-only-change-me`) or on
  `EnvironmentId=dev` coming from the copied base file. Fix each one, and list them in a Note.
  Done when: `GameServer.Gm.Tests` and `SampleGame.Tests` are green.
  Note: grep result: only `SsoTests` depended on the base-file seed. `GmTestHost` (indexes 0–7), `ObservedHost` (index
  0) and `HealthAndShutdownTests` (index 1) set every field of their own staff. No host test read `EnvironmentId=dev`
  from the copied base file (`TestHost` sets `test`, `CouchbaseGameHost` sets `it`); unit tests that build
  `new GameServerOptions { EnvironmentId = "dev" }` are unaffected.
- [X] T031 [P] [US3] In `tools/local/start-hosts.ps1`, add `ASPNETCORE_ENVIRONMENT` to `$names` (saved and restored)
  and set it to `Dev` for each host (FR-033, US1-9). Nothing else changes.
- [X] T032 [US3] Opt-in real kill test in a new file `tests/SampleGame.Tests/FileStoreProcessKillTests.cs`
  (`GAMESERVER_KILL_TEST=1`; otherwise skip with a message) (after T026, T029). The plan placed it in
  `GameServer.Platform.Tests`, which does not reference SampleHost; `SampleGame.Tests` does.
  - Start `SampleHost.dll` from the test output with `ASPNETCORE_ENVIRONMENT=Local`, a temporary
    `--FileStore:Folder`, and `--urls http://127.0.0.1:<free port>`.
  - Send a burst of counter and economy commands with the test-auth header, and kill the process tree N = 20 times at
    random moments.
  - After each kill, reopen the folder with `FileDocumentStore` and check that every receipt has its aggregate
    revisions and that no partial commit exists (SC-002).
  Note: the check is two-way: every receipt's aggregate is at or past the receipt's revision, and every counter revision
  1..rev has its receipt; the test also fails if nothing was committed. `dotnet` is the host of the test runtime. The
  content root is the test output, so `config/` is the tests' copy (a superset of the sample config). Run
  (`GAMESERVER_KILL_TEST=1`, Windows 11, 2026-10-30): 20 kills in 45 s, pass.

**Checkpoint**: The full suite is green in memory and with `GAMESERVER_TEST_STORE=File`. `dotnet run` of SampleHost
without a flavor fails closed.

## Phase 4: US4 — Couchbase settings (P2) — parallel with Phase 2 after Phase 1

- [X] T033 [US4] **[VERIFY first]** Check the CouchbaseNetClient 3.10.0 `ClusterOptions` member names and defaults,
  using the package XML docs under `~/.nuget/packages/couchbasenetclient/3.10.0/` or a reflection scratch test:
  - `KvTimeout`, `QueryTimeout`, `Compression`, `CompressionMinSize` (default 32?), `NumKvConnections`,
    `MaxKvConnections`;
  - whether a built-in trusted-root option exists, or `KvCertificateCallbackValidation` and
    `HttpCertificateCallbackValidation`.

  Fallbacks: use the callbacks for TLS trust. If a pool or compression property is missing, drop that key from
  `CouchbaseOptions`, data-model §4 and the docs, and record a deviation.
  Then update `src/GameServer.Platform.Couchbase/CouchbaseOptions.cs`: add the R5 members and `UsesTls`, and give
  `ConnectionString` the default `string.Empty` (D10).
  Note ([VERIFY], XML docs + reflection on the 3.10.0 package): `KvTimeout` (2.5 s), `QueryTimeout` (75 s),
  `Compression` (true), `CompressionMinSize` (32), `NumKvConnections` (2), `MaxKvConnections` (5) exist with these
  defaults; no key is dropped.
  - Compression: the default `ICompressionAlgorithm` is `NullCompressionAlgorithm`, so `Compression=true` alone
    compresses nothing. 3.10 bundles Snappier (`WithSnappyCompression()`), and the factory registers it when
    `Compression` is true. Deviation: KV bodies are now really compressed by default (they were not before); the whole
    Couchbase suite runs with it.
  - TLS trust: 3.10 has `WithTrustedServerCertificates`, but it checks the OS trust store first and the given CAs only
    as a fallback, so it is not "only that CA". The plan's callbacks are used instead.
    `ClusterOptions.Kv/HttpCertificateCallbackValidation` are `[Obsolete]` in 3.10 (warnings are errors), so the factory
    sets `TlsSettings.KvCertificateValidationCallback` and `TlsSettings.HttpCertificateValidationCallback`.
  - Deviation (found by the full Couchbase run): the 20 s `QueryTimeoutMs` default (SDK: 75 s) also bounded the
    provisioner's `CREATE INDEX`, which returns once the index is built. Under suite load a statement timed out
    client-side; the `IF NOT EXISTS` retry then returned before the build finished, and an EXPLAIN test picked another
    index. `CouchbaseProvisioner.CreateIndexAsync` now passes `Couchbase:ProvisioningTimeoutSeconds` as the statement
    timeout (Phase 5 rewrites this method; keep that bound). The sequential-scan helpers of
    `CouchbaseLeaseAndPurgeTests` set an explicit 75 s timeout (test-only scans, not GM pages).
- [X] T034 [US4] Create `src/GameServer.Platform.Couchbase/CouchbaseClusterOptionsFactory.cs` with the internal
  `Create(...)`. It maps every key and leaves the durability timeout unchanged. With `couchbases://` and
  `TrustCertificatePath`, it trusts only that CA: `X509Chain` with `CustomRootTrust`, host-name validation kept.
  `src/GameServer.Platform.Couchbase/CouchbaseConnection.cs` uses the factory and resolves `TrustCertificatePath`
  against the content root (`IHostEnvironment`). The certificate is PEM or DER, loaded through `X509CertificateLoader`
  or `CreateFromPem`, not the obsolete constructors. Depends on T033.
  Note: loading and validation live in a new internal `CouchbaseTrustedRoots.cs` (PEM through
  `X509Certificate2Collection.ImportFromPem`, DER through `X509CertificateLoader`; the chain uses `CustomRootTrust`, the
  server's intermediates and no revocation check, like the SDK default). A name mismatch is still refused. The factory
  assigns `MaxKvConnections` before `NumKvConnections`. `CouchbaseConnection` has a DI constructor with
  `IHostEnvironment`; the 3-argument constructor stays for `CouchbaseFixture` and resolves a relative CA against
  `AppContext.BaseDirectory`. The CA is loaded at connect time, only for `couchbases://`.
- [X] T035 [US4] Create `src/GameServer.Platform.Couchbase/CouchbaseSettingsCheck.cs` with
  `CouchbaseSettingsCheck : IStartupCheck` (after T003, T033). Every message names the key and the allowed range. It
  checks:
  - required keys: `ConnectionString`, `Username`, `BucketName`;
  - `Password` outside Development/Test, with the message naming `Couchbase__Password`;
  - the scheme (`couchbase://` or `couchbases://`, at least one host);
  - the R5 ranges, using `GameServer:Limits:PlayerTimeoutSeconds`/`GmTimeoutSeconds`;
  - the CA file: readable, and an X.509 certificate;
  - `TrustCertificatePath` with a non-TLS scheme → warning;
  - TLS rule: Production error, Staging warning, nothing in Development/Test (FR-014);
  - legacy reference keys from the R5 table (case-insensitive, D12) → warnings, never errors (FR-015).
  Note: one error per cause: the TLS rule runs only on a valid scheme, and the `MaxKvConnections` lower bound follows
  `NumKvConnections` only when that value is valid. Messages for required keys also name the environment variable
  (`Couchbase__<Key>`). The connection string is never echoed (only a wrong scheme). The CA file is validated even
  without TLS, where a warning says it is ignored. Provision mode gets the same errors (exit 1) and warnings (stderr),
  so `ProvisioningTimeoutSeconds` 30..7200 is enforced there.
- [X] T036 [US4] In `src/GameServer.Platform.Couchbase/CouchbaseServiceCollectionExtensions.cs`, remove the
  `BucketName` throw (the check now reports it) and register `CouchbaseSettingsCheck` as `IStartupCheck` (after T035).
  The signature `AddCouchbaseDocumentStore(services, options)` stays, because `Program.cs` and
  `tests/GameServer.Platform.Couchbase.IntegrationTests/Infrastructure/CouchbaseGameHost.cs` call it.
- [X] T037 [US4] Unit tests in a new file `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseSettingsTests.cs`.
  They need no cluster and must **not** skip without `COUCHBASE_CONNSTR` (after T034–T036). Cases:
  - the factory maps every key (FR-011, FR-016);
  - the check: each out-of-range value, each missing required key, `http://`, `couchbase://` with no host, a missing CA
    file, a CA file that is not a certificate (US4-2, US4-5, FR-013);
  - Production with `couchbase://` → error; Staging → warning; Development → silent (US4-3);
  - each legacy key → a warning naming its replacement (US4-6);
  - Production without a password → the message names `Couchbase__Password` (US3-7).
  Note: the file holds two classes: `CouchbaseSettingsTests` (no fixture, never skipped) and
  `CouchbaseSettingsIntegrationTests` (T038). Extra cases: inclusive bounds, bounds following `GameServer:Limits`, the
  connection string is not echoed, PEM and DER CA files, the custom-root validator (right CA, foreign CA, name mismatch),
  and a `TestHost` start with `AddCouchbaseDocumentStore(new CouchbaseOptions())` that reports the three required keys
  instead of throwing at registration. The algorithm check reads the SDK's internal service provider by reflection
  (SDK pinned).
- [X] T038 [US4] Integration tests in the same file (after T037; skip with a message without `COUCHBASE_CONNSTR`):
  - Connect with every setting changed from its default (`KvTimeoutMs` 3000, `QueryTimeoutMs` 15000,
    `Compression` false, `CompressionMinSizeBytes` 2048, `NumKvConnections` 1, `MaxKvConnections` 2), then insert, get
    and run a transaction.
  - Seed list `couchbase://unreachable.invalid,<host>` connects (US4-1).
  - Query timeout honoured: a compute-heavy statement such as
    `SELECT RAW SUM(x) FROM ARRAY_RANGE(0, 100000000) AS x` with `QueryTimeoutMs=200` fails with the SDK timeout in
    under 2 s, which surfaces as `INTERNAL` per D1 (US4-4). KV timeout: the mapping is asserted only, because a local
    node cannot delay KV reliably; record that as a Note.
  - TLS is opt-in (`COUCHBASE_TLS_CONNSTR` + `COUCHBASE_CA_PATH`): the right CA connects; a freshly generated
    self-signed CA is refused.
  Note: KV timeout: the mapping is asserted only (`CouchbaseSettingsTests`), because a local node cannot delay KV
  reliably. Deviation: on EE 7.6.12, `ARRAY_RANGE(0, 100000000)` fails at once with 5037 (`array_range()` result above
  20 MiB, surfaced as `InternalServerFailureException`). The test uses `SELECT RAW COUNT(1) FROM ARRAY_RANGE(0, 3000) AS a
  UNNEST ARRAY_RANGE(0, 3000) AS b` instead (about 18 s server-side), which fails with the SDK `TimeoutException` in under
  2 s. A round trip of a compressible 13 KB document with Snappy on was added. The TLS case was skipped locally (no TLS
  cluster).

**Checkpoint**: Couchbase suite green. Existing `CouchbaseFixture` and `CouchbaseGameHost` unaffected (they set the connection string).

## Phase 5: US5 — Production provisioning (P2)

- [X] T039 [US5] Provisioner in `src/GameServer.Platform.Couchbase/CouchbaseProvisioner.cs` (after T004 and Phase 4).
  **[VERIFY first]** whether `CREATE INDEX` without `defer_build` already blocks until the index is built on 7.6.12.
  Polling is correct either way, so keep it.
  `CouchbaseProvisioner : IStoreProvisioner`. `ProvisionAsync(progress, ct)` order:
  1. scope;
  2. **`txn_meta` first**, then the other collections in ordinal order;
  3. wait until each collection answers KV (bounded);
  4. read the existing index names once (`system:indexes`);
  5. `CREATE INDEX … IF NOT EXISTS` for the missing ones (statements unchanged);
  6. poll every 2 s until all are online, with elapsed time;
  7. verify.

  Each item is reported `Created` or `AlreadyPresent`. A failure → `ProvisioningFailedException` whose cause never
  contains credentials. Nothing is ever dropped. Keep the existing 503 retry. The startup path (`ProvisionOnStartup`)
  calls the same method with a logging progress. Keep the constructor that
  `tests/GameServer.Platform.Couchbase.IntegrationTests/Infrastructure/CouchbaseFixture.cs:89` uses.
  Note ([VERIFY], EE 7.6.12, scratch program on SDK 3.10): `CREATE INDEX` without `defer_build` blocks until the index is
  built: on 200 000 documents it returned after 8.3 s and `system:indexes` showed `online` right after; `IF NOT EXISTS` on
  an existing index returned in 2 ms. Polling is kept (it also waits for an index left building by an interrupted run).
  The statement timeout stays `ProvisioningTimeoutSeconds` (handoff from T033) and now also carries the cancellation token.
  - What to create moved into the internal `src/GameServer.Platform.Couchbase/CouchbaseSchema.cs` (collections, indexes
    with unchanged statements, new public record `IndexDefinition`); `CouchbaseProvisioner` keeps `RequiredCollections()`,
    `ReferenceIndexes()` and `IndexDefinitions()` as delegates (tests call them). The key-value probe is the internal
    `CouchbaseCollectionProbe.cs`, shared with T040. The constructor and `ProvisionAsync(CancellationToken)` (logging
    progress) stay for `CouchbaseFixture`.
  - Item names: `scope g_x`, `collection g_x.c`, `index <name> on <collection>`; a connection or bucket failure is
    `bucket <name>`, the catalog read is `index catalog of g_x`. The cause is `<exception type>[ (HTTP n)]: <first message
    line>` (SDK messages hold no credentials; T042 asserts the admin password is not in the output).
  - A collection created by another instance at the same time is reported `present`. An existing index in state
    `deferred` fails with a `BUILD INDEX` hint (never altered). The KV visibility wait is bounded by
    `ProvisioningTimeoutSeconds` on the startup path too. Verification = the T040 probe over every collection.
  - Startup provisioning logs created items at Information and present ones at Debug (every Development start).
- [X] T040 [US5] `VerifySchemaAsync` in the same file (research R11; after T039). **[VERIFY first]** the SDK 3.10
  exception for an unknown collection: is it `CollectionNotFoundException`, or a timeout whose retry reasons include
  `CollectionNotFound`? Pin it with a throw-away collection drop in T042 before finishing the classification.
  Fallback: classify both forms as missing.
  When `ProvisionOnStartup` resolves to false, `EnsureInitializedAsync` runs the check once (as a shared task). It
  probes every required collection, `txn_meta` included, with a parallel `GetAsync("_schema_probe")` and a 3 s
  timeout.
  - Missing collections → `GameServerStartupException` with the text of contracts §3.
  - Any other error is rethrown unchanged.

  The host exits with 1 before Kestrel listens, through T008 (D2).
  Note ([VERIFY], SDK 3.10 + EE 7.6.12, pinned by T042): an unknown collection never raises
  `CollectionNotFoundException`. It is an `UnambiguousTimeoutException` (op `GetCidByName`, after `KvTimeout`) whose
  `KeyValueErrorContext.RetryReasons` are all `CollectionNotFound`; a dropped collection whose id the client cached times
  out on the `Get` itself (3 s) with the same retry reason; an unknown scope gives `ScopeNotFound`. Classification: both
  exception types and a timeout with either retry reason mean missing; anything else propagates unchanged. The message
  is the contracts §3 text with the literal `<host> --provision`.
- [X] T041 [US5] In `src/GameServer.Platform.Couchbase/CouchbaseServiceCollectionExtensions.cs`, register
  `IStoreProvisioner` → the `CouchbaseProvisioner` singleton (after T036, T039; same file as T036).
  Note: one singleton serves `IHostedService`, `IStoreInitializer` and `IStoreProvisioner`.
- [X] T042 [US5] Integration tests in a new file `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseProvisioningTests.cs`
  (after T040, T041). Use a throw-away scope (unique game id `prov<8 hex>`). Cleanup of the scope and the test RBAC
  user runs even on failure. Cases:
  - Empty scope: `RunGameServerAsync(HostMode.Provision)` → 0. The first collection line is `txn_meta`. Every
    framework, module and purge index is `online`. The summary counts are correct (US5-1, SC-005).
  - Re-run → 0; every item is `present` and nothing is created (US5-2, FR-030).
  - Data-only RBAC user (`Data Reader`, `Data Writer`, `Query Select`/`Insert`/`Update`/`Delete` on the scope, created
    through the management API): a **Staging** host with startup provisioning off becomes ready (readiness 200)
    (FR-031, FR-032).
  - The same user runs `--provision` → 2 naming the item; a re-run with the admin user completes (US5-3).
  - Drop one collection → the host start fails, naming it and `--provision`. This pins the [VERIFY] from T040 (US5-5,
    D2).
  - Unknown bucket → 2 naming the bucket (US5-3).
  - A Development host still provisions at startup by default (US5-6).
  - Production variants (data-only ready, provision without SSO, IAP or master key) run only with
    `COUCHBASE_TLS_CONNSTR`, because Production requires TLS (FR-014). Without it they skip with a message; US5-4 is
    already covered by T011.
  Note: `--provision` and refused starts run the real binary (`dotnet SampleHost.dll` of the test output, settings as
  environment variables, inherited `*__*`/`ASPNETCORE_*` variables removed) through `Infrastructure/SampleHostProcess.cs`;
  ready hosts run in process (`WebApplicationFactory<Program>`, `Infrastructure/SampleHostFactory.cs`). The scope
  `g_prov<8 hex>` is provisioned once (Beta flavor, admin) by the collection fixture `ProvisionedSampleScope`
  (`SampleScopeCollection`, shared with T043 so the classes never overlap); its dispose deletes the data-only user and
  drops the scope. The data-only user is created with `PUT /settings/rbac/users/local/<name>` and the roles on
  `[bucket:scope]` (7.6 refuses `[bucket:scope:*]`), random password, never printed.
  Measured: the data-only `--provision` fails at the first item, `FAILED scope g_x: CouchbaseException (HTTP 403)` (data
  roles cannot list scopes), exit 2; the admin re-run recreates the dropped collection **and its indexes** (dropping a
  collection drops its indexes). Serve with a dropped collection → exit 1 with the §3 text (pins T040). Unknown bucket →
  `FAILED bucket <name>: …`, exit 2. The TLS variant was skipped locally (no TLS cluster).
  Under full-suite load the index service queues the builds of every class (each index of the empty scope took 23–88 s,
  measured), so the test hosts run with `Couchbase:ProvisioningTimeoutSeconds` 1800 and the process harness waits longer
  than that. US5-6 is shown on the shared scope: a collection without indexes is dropped and a Dev host recreates it
  before it is ready (a second empty scope would cost minutes of index builds).
- [X] T043 [US3] Flavor readiness tests in a new file `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs`
  (US3 independent test, SC-004; after T025, T026, T042). Use `WebApplicationFactory<Program>` per flavor, with
  environment overrides that point to the IT bucket and the throw-away scope provisioned by `--provision` first.
  - **Dev**: ready; development tools on.
  - **Beta and Uat**: ready with test secrets set through `UseSetting`: master key; GM OIDC with an `https`
    authority; pepper; IAP key material generated in the test, with a valid RSA PEM. Development tools are off: the
    cheat route returns 404, the test-auth header is ignored, GM OpenAPI returns 404. The `config/env/Staging` overlay
    is used (US3-6, FR-008, FR-010).
  - **Live**: ready only with `COUCHBASE_TLS_CONNSTR`; otherwise assert that the TLS error is the only error.
  - Missing Couchbase settings in Beta → one start lists every missing key (US3-7).
  Note: secrets come from `Infrastructure/StagingSecrets.cs` (master key, OIDC authority `https://sso.example.invalid/`,
  pepper, a fresh RSA PEM for the Google service account, Apple bundle id); the IAP verifiers are built lazily, so the PEM
  is not parsed at startup. Beta/Uat run on the data-only identity (startup schema check, not provisioning). There is no
  `config/env/Staging` in the sample, so the test copies the base config to a temporary root with an
  `env/Staging/manifest.json` overlay and asserts its label (folder deleted after). Development tools off: cheat route
  404, GM OpenAPI 404, test-auth header → 401. Live without TLS: exit 1 with exactly one violation, the `couchbases://`
  error. Beta without Couchbase variables: exit 1 with exactly three violations (`Couchbase__ConnectionString`,
  `__Username`, `__Password`; the bucket comes from the flavor file).

**Checkpoint**: The Couchbase suite is green, including provisioning and flavor readiness.

## Phase 6: US1 — Local run (P1)

- [X] T044 [US1] **[VERIFY first]** Two runtime facts. Record both as a Note.
  1. Does `ASPNETCORE_URLS` (set by a launch profile's `applicationUrl`) beat `"Urls"` from `appsettings.Local.json`?
     Run SampleHost once with the two values deliberately different and see which port it binds.
  2. What working directory does `dotnet run --launch-profile` use?

  Fallback (the plan's design, whatever the result): the profile and the Local file carry the same URL, and the
  scripts pass `--urls` and set the working directory explicitly.
  Then create `samples/SampleHost/Properties/launchSettings.json` with the data-model §3 content: `local` first, so it
  is the default; then `dev-couchbase` (FR-034).
  Add a test in `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs` (after T028, same file): build
  `WebApplication.CreateBuilder` with `Args = ["--urls", "http://localhost:5999"]`, `EnvironmentName = "Local"` and the
  SampleHost source folder as content root. `Configuration["urls"]` must be `http://localhost:5999` (US1-4). The test
  also asserts that the `applicationUrl` of each profile equals the Local file's `Urls`.
  Note ([VERIFY], .NET SDK 10.0.403, Windows): (1) `ASPNETCORE_URLS=http://localhost:5998` with the Local file's `Urls`
  5080 → the host bound 5080: the file beats `ASPNETCORE_URLS` (host-prefixed variables are the lowest configuration
  source), so a profile's `applicationUrl` cannot move the port; `--urls` beats both (bound 5997). (2) `dotnet run
  --project samples/SampleHost/SampleHost.csproj --launch-profile local`, started from the repository root, used the
  project folder as working directory and content root (flavor file found, logs in `samples/SampleHost/logs`). The plan's
  design stays (same URL in profile and file; scripts pass `--urls` and set the working directory). The profile test
  also asserts that no profile carries a password.
- [X] T045 [P] [US1] Create `tools/local/run-local.ps1` per contracts §4 and research R12, compatible with Windows
  PowerShell 5.1 and PowerShell 7. The script uses no `&&`, `?:` or `??`, and is **ASCII only**: `.gitattributes`
  stores LF without a BOM, and 5.1 reads non-ASCII as ANSI. Behaviour, with exit codes:
  - parameters `-Port`, `-Reset`, `-Lan`; invalid → 2;
  - SDK from `PATH`, else `~/.dotnet10`; `dotnet --version` run from the repository root → 3 if missing;
  - port probe with a loopback `TcpListener` → 4, naming the port and `-Port`;
  - `dotnet build samples/SampleHost/SampleHost.csproj -c Debug` → 5 on failure;
  - `Start-Process dotnet run --no-build --launch-profile local -- --urls … [--FileStore:ResetOnStart=true]` with
    `-WorkingDirectory samples/SampleHost -NoNewWindow -PassThru`;
  - poll `/health/ready` every 500 ms for at most 60 s; host exit during the wait → 6; timeout → print the last 20
    lines of the newest `samples/SampleHost/logs/*`, stop the host, 7;
  - the URL block; with `-Lan`, a red warning and the LAN IPv4 URLs;
  - wait loop; `finally` waits up to 35 s, then kills the process tree. No path leaves a host running.
  Note: the readiness probe targets `127.0.0.1` (found in T048: with `-Lan` the host binds `0.0.0.0` only, and
  `localhost` tries `::1` first; Windows retries a refused connect for about 2 s, longer than the probe timeout, so the
  `-Lan` run never saw ready). SDK candidates are tried in order with `--version` (here `C:\Program Files\dotnet` on
  `PATH` has no SDK 10, so `~/.dotnet10` is used). The port probe covers IPv4 and IPv6 loopback (and Any with `-Lan`).
  `$ErrorActionPreference` is `Continue`: 5.1 turns stderr of a native command into a terminating error under `Stop`.
  `$process.Handle` is opened at start, otherwise 5.1 reports no `ExitCode`. The data folder in the URL block is read
  from `appsettings.Local.json`. Verified pure ASCII, LF.
- [X] T046 [P] [US1] Create `tools/local/run-local.sh` for bash on macOS, Linux and Git Bash, with the same contract.
  It differs from T045 as follows:
  - options `--port`, `--reset`, `--lan`;
  - port probe through `/dev/tcp`;
  - the readiness poller and URL printer run in a background subshell, while `dotnet run` runs in the **foreground**,
    so SIGINT reaches it;
  - a `trap` kills the poller;
  - exit codes as in T045.

  Set the executable bit (`git update-index --chmod=+x`). `.gitattributes` already forces LF.
  Note: `dotnet run` stays in the foreground through `sh -c 'echo $$ > <state>/pid; exec "$@"'`, so the poller knows its
  PID; on timeout it kills the tree (Git Bash: `taskkill //T` on `/proc/<pid>/winpid`; elsewhere children first through
  `pgrep -P`). Readiness is an HTTP/1.0 request over `/dev/tcp/127.0.0.1` (no `curl`); LAN addresses come from
  `ipconfig.exe`, `hostname -I` or `ifconfig`. **Executable bit not set**: the file is untracked and this run commits
  nothing; set it when adding the file (`git add --chmod=+x tools/local/run-local.sh`). Verified ASCII, LF, `bash -n`.
- [X] T047 [P] [US1] Change the default URL to `http://localhost:5080/` (FR-039). Edit these files as text; do not open
  the Unity Editor:
  - `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs:24`;
  - the serialized value in `samples/SampleGame/KestrelDemo/Assets/Scenes/SampleGameClient.unity:151`;
  - `samples/SampleGame/KestrelDemo/Assets/Editor/SmokeBuild.cs:38`;
  - `sdk/unity/ci/smoke-build.ps1` (lines 9 and 20).

  Done when: grepping these four files for `localhost:5000` finds nothing.
  Note: done with a text replacement (5 lines; LF kept); the grep finds nothing. `README.md:45` still says 5000 (T049).
- [X] T048 [US1] Manual acceptance, recorded as a Note on this task with OS and shell per row (after T026, T031,
  T044–T047, Phase 5). Run each row on Windows PowerShell 5.1, PowerShell 7 and Git Bash; on macOS/Linux when
  available, otherwise record "not run".
  1. A clean clone is ready in ≤ 60 s after the build, and the URLs are printed (US1-1, SC-001).
  2. Guest login and a command work. The host answers on both `127.0.0.1` and `[::1]`.
  3. Ctrl+C: readiness goes to 503, the host drains, exit 0 (US1-5). A restart keeps the player.
  4. Busy port → 4 (US1-3). `-Port 5090` → the URLs use 5090 (US1-4).
  5. `-Reset` → empty store (US1-6).
  6. `-Lan` → warning, and reachable from a phone (US1-7).
  7. A second script on the same folder → 6 with the host's lock message in ≤ 5 s (US2-5).
  8. IDE or `dotnet run` uses the `local` profile and the same folder (US1-2).
  9. The `dev-couchbase` profile works with the user variable `Couchbase__Password` (US1-8).
  10. `start-hosts.ps1 -Count 2` is ready on 5080 and 5081 (US1-9).
  11. `--provision` of the Dev flavor against the local cluster → output contract and exit 0.
  12. Unity E2E smoke (`sdk/unity/ci/smoke-build.ps1 -E2E`) on 5080.
  13. A new developer following only the README (after T049) reaches a ready local server (SC-008, US6-1).

  Note (2026-10-30, Windows 11 Enterprise 10.0.26300, SDK 10.0.403). Shells: Windows PowerShell 5.1 (`powershell -File`)
  and Git Bash 5.2.15 (`bash.exe` in a Windows console window, not mintty). PowerShell 7 is not installed on this machine;
  macOS/Linux were not available: those columns are "not run". Ctrl+C was produced with `tools/local/send-ctrl-c.ps1
  -Signal C` against a console whose processes had Ctrl+C enabled (a real key press in that console is the same event).
  Store = the default `samples/_database/dbbase` (did not exist before; deleted after the runs).

  | Row | Windows PowerShell 5.1 | Git Bash | PowerShell 7 / macOS / Linux |
  | --- | --- | --- | --- |
  | 1 ready + URLs | pass: build 32 s (no-op incremental build of 15 projects), ready about 9 s after the build (41 s total); URL block as contracts §4 | pass: build 32 s, ready 44 s total | not run |
  | 2 guest login + command, `127.0.0.1` and `[::1]` | pass: login 200, counter command `OK` on both addresses, same player | pass (login on `127.0.0.1`, command on `[::1]`) | not run |
  | 3 Ctrl+C → 503, drain, exit 0; restart keeps player | pass: readiness 503 seen during the drain, exit 0 after 6.3 s, port free; restart → same player id | pass: 503 seen, exit 0 after 6.1 s; restart → same player id | not run |
  | 4 busy port → 4; `-Port 5090` | pass: exit 4 in 2 s, message names the port and `-Port` (IPv4-only and IPv6-only listeners both detected); 5090 URLs printed, ready on 5090 | pass: exit 4 in 1 s; `--port 5090` URLs and ready | not run |
  | 5 `-Reset` → empty store | pass: "started empty", the old guest logs in as a new player | pass | not run |
  | 6 `-Lan` → warning, reachable from a phone | pass after the probe fix (see T045): red warning + LAN URLs, `/health/ready` 200 through the LAN IPv4 from this machine; the host logged the FR-038 listen warning. No phone available | pass: warning + LAN URLs | not run |
  | 7 second script, same folder → 6 with the lock message in ≤ 5 s | pass: exit 6 with the `[filestore]` lock message; the second host itself refused in 2.7 s (`dotnet run --no-build`); the script total was 42 s because it builds first | pass: exit 6 with the lock message | not run |
  | 8 IDE / `dotnet run` uses `local` and the same folder | `dotnet run --project samples/SampleHost/SampleHost.csproj --no-build` (no profile named): ready on 5080, holds the lock of `samples/_database/dbbase`. IDE not run | n/a | not run |
  | 9 `dev-couchbase` profile with `Couchbase__Password` | pass with the variable set in the process (not written to the user scope): ready in 4 s, login + command OK | n/a | not run |
  | 10 `start-hosts.ps1 -Count 2` on 5080/5081 | pass: both ready in 41 s, `ASPNETCORE_ENVIRONMENT` restored, `stop-hosts.ps1` freed both ports | n/a | not run |
  | 11 `--provision`, Dev flavor, local cluster | pass: `dotnet run --launch-profile dev-couchbase -- --provision` → exit 0 in 98 s, header + one line per item, `done: 12 created, 26 present` (`gameserver-dev` lacked the 010/011 items) | n/a | not run |
  | 12 Unity E2E smoke on 5080 | not run: drive C: was nearly full and Unity writes its caches and logs to C: | n/a | n/a |
  | 13 README-only new developer | not run: the README is T049 (Phase 7) | — | — |

  Extra paths: invalid arguments → 2 (`-Bogus`, `-Port abc`, `-Port 70000`, `-Port 0`; bash `--bogus`, `--port abc`,
  `--port 0`, `--port` without value); SDK missing (profile pointing elsewhere) → 3 with the fix text; readiness timeout
  forced with a temporary copy whose limit was 2 s → 7 with the log tail, and no host left (PowerShell and Git Bash).
  Build failure (5) was not induced. After every run no `SampleHost` process and no listener on 5080–5092 remained.
- [X] T048a Test temp-folder leak (owner request 2026-10-30): `ObservedHost` tests left a `gs-logs-*` folder per host, and
  `LoggingTests.Sink_errors_are_counted_and_make_readiness_degraded` left a `gs-blocker-*` **file**, in `%TEMP%` (about
  1 200 entries, 2.3 GB on the owner's machine).
  Note: new `tests/GameServer.Observability.Tests/TempLogDirectory.cs` (`gs-logs-*`, best-effort recursive delete with 5
  retries of 100 ms). `ObservedHost` owns one when the caller passes no directory and deletes it on dispose and on a
  failed start; the four `LoggingTests` that read the files after the host stopped own a `TempLogDirectory` themselves
  (`NewLogDirectory()` removed); the blocker file now lives inside a `TempLogDirectory`. Other `gs-*` creators already
  clean up (`gs-appsettings-*.json` in `finally`, `gs-smoke-*`, `gs-kill-*`, `gs-config-*`, file-store test folders,
  `gs-flavor-config-*` of T043).
  Result (2026-10-30, after Phases 5–6, `TMP`/`TEMP` = `D:\devcache\gstmp`): Release build 0 warnings; full suite incl.
  Couchbase 888 total, 884 passed, 0 failed, 4 skipped (opt-in kill, perf, TLS settings, TLS Production provisioning);
  the Couchbase project took 16.5 min. No new `gs-*` entry after the run; no test scope or `gs-it-*` user left on the
  cluster; no host process or listener left.
  Environment note: Couchbase keeps its data on C:. A run that died with C: full left 7 test scopes (`g_it*`,
  `g_prov*`) in `gameserver-it`; dropping them through the REST API freed about 9.6 GB on C: at once.

**Checkpoint**: Scripts and profiles accepted on Windows. Unity defaults point to 5080.

## Phase 7: US6 — Docs and close (P3)

Write documentation in the language of the existing repository docs (Vietnamese, as in `README.md`,
`docs/guides/local-real-env-testing.md` and `docs/pcr/platform.md`). Configuration keys, commands and exit codes stay
literal.

- [X] T049 [P] [US6] `README.md`: one primary local path (`tools/local/run-local.ps1`/`.sh` or the `local` launch
  profile), the GM URL on port 5080 (line 45 says 5000 today), a pointer to the flavor table and guide, and the
  Couchbase multi-instance path presented as the "real environment" option (US6-1, FR-040).
  Note: new section "Chạy server local (một lệnh)" (script commands, URL table on 5080 with the seed, options, `local`
  profile), the flavor/guide pointer, "Build và test" without the old `dotnet run` in-memory line, and "Môi trường thật:
  Couchbase, nhiều instance" (`dev-couchbase` profile, `start-hosts.ps1`, pointer to `local-real-env-testing.md`). The
  stale GM URL on 5000 and the seed in the base `appsettings.json` are gone. The structure table names the file store,
  `run-local` and the new guide. Row 13 of T048 (README-only new developer) is still not run.
- [X] T050 [P] [US6] New guide `docs/guides/environments-and-local-run.md` (FR-040, US6-2). It covers:
  - the flavor table and how to select a flavor;
  - required environment variables per flavor, from the data-model §3 table, including the provisioning run;
  - every Couchbase key with its default and range;
  - the reference mapping and dropped fields (FR-012);
  - the TLS rule and CA;
  - RBAC roles for the admin identity and the data-only identity;
  - file store layout, reset, limits (no N1QL, no hot reload, durability without fsync D5, 120-character names D6,
    expiry cleanup at start D7) and a security note (`_database/` holds key material and guest credentials; Defender
    exclusion);
  - the provisioning command (`--provision`, exit codes, idempotency);
  - scripts and their exit codes; launch profiles;
  - beta and uat sharing the Staging overlay; the dev environment must be network-restricted.
  Note: sections 1 flavors (selection, layering, validation, Staging without dev tools, dev network restriction), 2 env
  vars per flavor + key → `__` variable table of the 6 secret keys, 3 local run (scripts, exit codes, profiles, URL
  precedence, `start-hosts.ps1`, Unity 5080), 4 file store (layout, limits table incl. disk full D1, perf, reset,
  security + `Add-MpPreference` exclusion), 5 Couchbase (keys, reference mapping, TLS/CA, RBAC, `--provision`, schema
  check), 6 host exit codes. Found while writing: IAP keys are **not** validated at startup (verifiers are lazy), so
  the guide says a missing IAP key fails the first receipt verification, not the start (data-model §3 lists them as
  "required"). RBAC: the provisioning identity also needs `Data Reader` (the KV visibility wait and the verification
  read `_schema_probe`; per Couchbase docs `Bucket Admin` cannot read data). contracts §2 lists only `Bucket Admin` +
  `Query Manage Index` (+ `Query System Catalog`); the guide and PCR add `Data Reader` and say the minimal admin role
  set is unverified (tests use `Administrator`) — for T055.
- [X] T051 [P] [US6] `docs/guides/local-real-env-testing.md`: `start-hosts.ps1` now selects the Dev flavor; add a
  pointer to `--provision` and the new guide.
  Note: also the `Couchbase__Password` user variable of the `dev-couchbase` profile, a `--provision` step for
  `gameserver-dev`, and the test-hygiene paragraph (scopes `g_it*`/`g_prov*` and users `gs-it-*` left by a dead run;
  PowerShell REST snippet that lists and drops them without printing the password).
- [X] T052 [P] [US6] PCR (FR-041). `docs/pcr/platform.md` gets a new **§17**. It records:
  - store kinds and where each is allowed;
  - file store semantics and limits;
  - Couchbase keys with defaults and bounds;
  - flavor validation rules;
  - the `IStartupCheck` extension point;
  - the provisioning contract: switch, `provision:` output prefix, exit codes 0/1/2, idempotency, credential and roles;
  - script exit codes 2–7; default port 5080;
  - perf numbers from T020, if run;
  - breaking items: no default `Couchbase:ConnectionString`; `TestHost` environment id `test`; `Program.cs` shape
    `RunGameServerAsync`; `SecretKeys` gains the PlayGames secret; the generalized development-only store message;
  - not provided: `GameServer:Staging:AllowCheatModule` (R13).

  `docs/pcr/unity-sdk.md`: sample default URL 5080. No URL is stated there today, so add one line to the sample
  section.
  Note: §17 has subsections flavor, store, file store (incl. T020 perf), `IStartupCheck`, Couchbase (keys, TLS,
  compression deviation, legacy keys, index statement timeout), provisioning (switch, output, exit codes, idempotency,
  credential/roles, timing, schema check with the `UnambiguousTimeoutException`/`CollectionNotFound` detection), local
  run (port, URL precedence, profiles, script exit codes, T048 coverage), breaking items, not provided. The version line
  names slice 011; §13's secret list points to §17 for the current list (it still shows the original 3 keys).
  `unity-sdk.md`: the sample row states `serverUrl` `http://localhost:5080/`.
- [X] T053 [US6] Docs key test in a new file `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` (after T050,
  T052; SC-008). It extracts the back-ticked keys matching `^(GameServer|Couchbase|FileStore):[A-Za-z0-9:]+$` from the
  new guide, PCR §17 and `README.md`. Each key must resolve, by reflection, to a bound property of `GameServerOptions`
  (nested), `CouchbaseOptions` or `FileStoreOptions`. Legacy keys listed in the mapping table are excluded explicitly.
  The test also catches `Limits:` written without `GameServer:`, as in data-model §4.
  Note: "bound" = public instance property with a public setter (computed members such as `UsesTls` are refused);
  the walk is case-insensitive and follows dictionaries and list indexes like the binder. The `GameServer`
  subsections (from reflection: `Limits`, `Jobs`, `Hosting`, …) written without the prefix fail. Excluded explicitly:
  the 6 `Couchbase:` legacy keys and `GameServer:Staging:AllowCheatModule` (documented as not provided); a test asserts
  the excluded keys bind to nothing. Extra: the guide and §17 must name every bound `CouchbaseOptions` and
  `FileStoreOptions` key, and yield at least 10 keys each (guards against a broken extraction). 9 tests, green;
  injecting `GameServer:Limits:Bogus`, `Limits:PlayerTimeoutSeconds` and `Couchbase:UsesTls` into the guide failed 2
  of them as expected (reverted).
- [X] T054 Pre-review full run: Release build; full suite in memory; full suite with `GAMESERVER_TEST_STORE=File`;
  Couchbase suite including T042 and T043. Record the counts.
  Note (2026-10-30, Windows 11, `TMP`/`TEMP` = `D:\devcache\gstmp`, EE 7.6.12):
  - Release build `GameServer.slnx`: 0 warnings, 0 errors.
  - Full suite in memory (no `COUCHBASE_*`): 897 total, 822 passed, 0 failed, 75 skipped (Couchbase + opt-in), 34 s.
  - Full suite with `GAMESERVER_TEST_STORE=File`: 897 total, 822 passed, 0 failed, 75 skipped, 38 s.
  - Full suite with Couchbase, first run: 897 total, 889 passed, **4 failed**, 4 skipped (opt-in kill, perf, TLS settings,
    TLS Production provisioning), 17 min. All 4 in `CouchbaseProvisioningTests`. Cause (host log
    `logs/prov<id>-*.log` of the IT output): the fixture's first `--provision` failed at its first item, `FAILED scope
    g_prov…: CouchbaseException (HTTP 503)`: the SDK logged "Failed to get all scopes" 6.5 s after "Attempting to get all
    scopes". `CouchbaseProvisioner.ProvisionAsync` retries 503 only on `CreateScopeAsync`/`CreateCollectionAsync`
    (`CreateMissingAsync`), not on the `GetAllScopesAsync` read that precedes them, so a cluster manager busy with the
    other classes' manifest changes fails the run. Cascade: `FirstRun` was the failed run (2 tests read its lines and
    failed at once); "a rerun reports present" did the real first provisioning (12.5 min, everything `created`); "a
    development host still provisions" found no `FirstRun` line. Not fixed here (production code, outside T049–T054):
    for T055. Suggested root fix: run the scope listing through the same 503 retry as the creates.
  - Couchbase project re-run alone (cluster clean before): 126 total, 124 passed, 0 failed, 2 skipped (TLS), 16.3 min.
  - After the runs: no test scope or `gs-it-*` user left on the cluster, no `SampleHost` process, no new `gs-*` entry in
    the temp folder (51 older ones from 19:00–21:00 remain, not from these runs).
- [X] T055 Run tech-lead-reviewer on the slice. Fix Blocker and Major findings. Append the review summary to this file.
- [ ] T056 Update `AgentMemories/features/p05-speckit/00_summary.md`: the 011 local-env row, technical notes (flavors,
  file store, `IStartupCheck`, `--provision`, test-store switch, `TestHost` environment id `test`) and open items (CI,
  TLS integration coverage, macOS/Linux script runs if not done).
- [ ] T057 Final gate:
  - `build GameServer.slnx -c Release` has 0 warnings;
  - `test --solution GameServer.slnx -c Release --no-build` is all green, **including Couchbase** (`COUCHBASE_*`
    variables) and `GameServer.Architecture.Tests`;
  - one extra pass with `GAMESERVER_TEST_STORE=File` is green.

  Then commit `feat:` with no signature lines.

## Dependencies

- Phase 1 blocks everything. Inside Phase 1:
  - T001–T004 run in parallel; T005 follows T002.
  - T006 needs T002 and T003. T007 needs T001 and T003. T008 needs T007.
  - T009 runs after T006 and T007, because the env-id change only matters once FR-004 exists.
  - T010 needs T006, T007 and T009. T011 needs T008 and T009.
- **Phase 2 (US2) ∥ Phase 4 (US4)** after Phase 1. They share no files: `Platform.Testing` vs `Platform.Couchbase`,
  and their tests are in different projects.
  - Phase 2: T012 → T015 → T016 → T017; T013 and T014 are parallel with T012. T018–T024 follow T017 (T024 [P]).
  - Phase 4: T033 → T034, T035 → T036 → T037 → T038.
- Phase 3 needs Phase 1 and T017. T025 → T026 → T028, T029, T032. T030 follows T025. T027 and T031 are [P].
- Phase 5 needs Phase 4. T039 → T040 → T042, and T041 follows T036. T043 also needs Phase 3.
- Phase 6 needs Phase 3. T044 needs T028 (same file). T045, T046 and T047 are [P]. T048 needs Phase 5 for rows 9 and 11.
- Phase 7 comes last. T049–T052 are [P]. T053 follows T050 and T052. Then T054 → T055 → T056 → T057.
- Shared files that force order:
  - `CouchbaseServiceCollectionExtensions.cs`: T036 → T041.
  - `SampleHostConfigurationFileTests.cs`: T028 → T044.
  - `TestHost.cs`: T009 → T022.
  - `StartupValidationTests.cs`: T007 → T010.
  - `CouchbaseProvisioner.cs`: T039 → T040.
- Parallel examples: T001 ∥ T002 ∥ T003 ∥ T004; T013 ∥ T014 ∥ T033; T018 ∥ T019 ∥ T020 ∥ T024; T027 ∥ T031;
  T045 ∥ T046 ∥ T047; T049 ∥ T050 ∥ T051 ∥ T052.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The persistence seam or the open gate in `InMemoryDocumentStore` (used by 600+ tests) changes semantics, or a caller touches the store before `Open` (`OutboxSweeper` does not await `IStoreInitializer`; `UseCollections` runs in `UseGameServer`) | High | The seam is a no-op on the public constructor. The gate exempts `Use*Collections`. T012 runs the full suite unchanged. The reopen invariant is in T018. T029 runs with the sweeper on |
| `DeploymentIdentityCheck` plus the env-id change break host tests (`SsoTests:160` asserts `"dev"`); the generalized message breaks `StartupValidationTests:78,87` | Medium | Fixed explicitly in T007 and T009. Checkpoint after Phase 1 |
| `RunGameServerAsync` breaks `WebApplicationFactory<Program>` (exit via `Environment.Exit`, or a different app instance) | Medium | T008 forbids `Environment.Exit` and returns the code. T028 and T029 exercise WAF on the new `Program.cs` |
| Cleaning the base file hides dependencies in test projects that copy SampleHost's `appsettings.json` (seed, `EnvironmentId=dev`) | Medium | T030 grep and Note. `TestHost` sets every required key itself |
| SDK 3.10 names (TLS trust, compression, pool) or the unknown-collection exception differ from research | Medium | [VERIFY] steps T033 and T040 with fallbacks. T042 pins the exception on 7.6.12 |
| `ASPNETCORE_URLS` vs `Urls` precedence, or the `dotnet run` working directory | Low | T044 verifies. Same URL in profile and file. Scripts pass `--urls` and set the working directory |
| The local cluster has no TLS, so Production readiness, data-only Production and Live readiness cannot run by default | Medium (SC-004/SC-005 partially opt-in) | Staging runs these paths by default. Production variants are opt-in with `COUCHBASE_TLS_CONNSTR`. Recorded in PCR and the resume file |
| Staging flavor readiness needs believable IAP, SSO and pepper secrets (PEM parsed at startup) | Medium | T043 generates key material in the test. No real credentials |
| `GameServer.Platform.Testing` has no `Microsoft.Extensions.Configuration.Binder` | Low | T017 reads two keys from `IConfiguration` by hand. No new package |
| PowerShell 5.1 reads a BOM-less non-ASCII script as ANSI; the bash script loses its executable bit | Low | T045 is ASCII only. T046 uses `git update-index --chmod=+x`. `.gitattributes` already forces LF |
| Windows Defender or the indexer slows the 10 000-document load or causes sharing violations | Low | Retry 5 × 20 ms (T015). The perf test is opt-in. The guide recommends an exclusion |
| The kill test is flaky (port race, slow start) | Low | Opt-in. Free-port allocation, readiness poll and a generous timeout |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T002, T006, T025, T026, T028, T043 |
| FR-002 | T002, T005, T006, T025, T028, T043 |
| FR-003 | T002, T006, T010 |
| FR-004 | T006, T009, T010 |
| FR-005 | T025, T028, T030 |
| FR-006 | T001, T028 |
| FR-007 | T001, T007, T010, T021, T026 |
| FR-008 | T006, T010, T043, T050 |
| FR-009 | T007, T010, T035, T037, T043, T050 |
| FR-010 | T006, T043 |
| FR-011 | T033, T034, T037, T038 |
| FR-012 | T035, T037, T050, T052 |
| FR-013 | T033, T035, T036, T037 |
| FR-014 | T035, T037, T038, T042 |
| FR-015 | T035, T037 |
| FR-016 | T034, T037, T039 |
| FR-017 | T017, T025, T027, T029 |
| FR-018 | T012, T017, T018, T022, T023 |
| FR-019 | T012, T015, T021 |
| FR-020 | T015, T016, T020, T032 |
| FR-021 | T013, T014, T015, T016, T019 |
| FR-022 | T015, T016, T019 |
| FR-023 | T014, T017, T019, T048 |
| FR-024 | T016, T019 |
| FR-025 | T014, T019, T048 |
| FR-026 | T050, T052 |
| FR-027 | T007, T021 |
| FR-028 | T021, T029 |
| FR-029 | T004, T008, T011, T039, T042 |
| FR-030 | T039, T042 |
| FR-031 | T008, T011, T042 |
| FR-032 | T040, T042 |
| FR-033 | T025, T028, T031, T044 |
| FR-034 | T044, T048 |
| FR-035 | T045, T046, T048 |
| FR-036 | T014, T045, T046, T048 |
| FR-037 | T045, T046, T048 |
| FR-038 | T008, T011, T048 |
| FR-039 | T047, T048 |
| FR-040 | T049, T050, T051, T053 |
| FR-041 | T052, T053 |

| Success criterion | Tasks |
| --- | --- |
| SC-001 | T045, T046, T048 |
| SC-002 | T020, T032 |
| SC-003 | T018, T023 (Couchbase: existing suites) |
| SC-004 | T010, T028, T043 |
| SC-005 | T042 (Production variant opt-in TLS) |
| SC-006 | T020 (opt-in) |
| SC-007 | T019, T048 |
| SC-008 | T053, T048 |

Scenarios:
- US1: US1-1 → T029, T045/T046, T048; US1-2 and US1-8 → T044, T048; US1-3 → T045/T046, T048; US1-4 → T044, T048;
  US1-5 → T008, T029, T048; US1-6 → T019, T048; US1-7 → T011, T045/T046, T048; US1-9 → T031, T048.
- US2: US2-1 → T021, T029; US2-2 → T020, T032; US2-3 → T018, T023; US2-4..US2-7 → T019 (US2-5 also T048);
  US2-8 → T021; US2-9 → T029.
- US3: US3-1 → T028, T043; US3-2..US3-4 → T010; US3-5 → T028; US3-6 → T043; US3-7 → T010, T037, T043;
  US3-8 → T028.
- US4: US4-1 and US4-4 → T038; US4-2 → T037, T038; US4-3, US4-5 and US4-6 → T037.
- US5: US5-1, US5-2, US5-3, US5-5 and US5-6 → T042; US5-4 → T011, T042.
- US6: US6-1 → T049, T048; US6-2 → T050, T052, T053.

## Planning notes (gaps between spec, plan and code, resolved here)

1. **No TLS on the local cluster.** SC-005 asks for a Production-flavor host on a data-only credential, and SC-004 asks
   for Live readiness, but FR-014 makes Production require TLS. The default run therefore covers these paths with
   Staging (T042, T043). The Production and Live variants are opt-in with `COUCHBASE_TLS_CONNSTR`.
2. **Disk full.** The spec edge case says "readiness reports the store as unhealthy" on disk full. Per D1 and R8, the
   probe is unhealthy only after a failure **past** the commit point (faulted state). A failure before the commit
   point is a clean `TransactionFailedException`, and readiness stays healthy. Accepted and documented in T050 and T052.
3. **Environment variable names in error messages.** US3-7 asks for the environment variable name of each missing
   secret. `CouchbaseSettingsCheck` names `Couchbase__Password` (T035). The module validators that are merged through
   `IStartupValidator` (GM SSO, pepper) keep their configuration-key text. The guide's table maps each key to its
   variable (T050). Changing the module validators' messages is left out of this slice.
4. **Paths in the plan that do not exist.** `tests/Shared/TestStores.cs` does not exist; T022 creates it.
   `samples/SampleHost/Properties/` does not exist; T044 creates it. `GameServer.Platform.Testing` and
   `GameServer.Platform.Web` have no `InternalsVisibleTo` for the tests; T012 and T008 add it.
5. **Test placement.** The opt-in kill test moves to `tests/SampleGame.Tests` (T032), because
   `GameServer.Platform.Tests` does not reference SampleHost. The codec tests get their own file (T013).
6. **Configuration coupling in provision mode.** The provision-mode header (bucket name) and the timeout come from
   Couchbase keys, which Platform.Web reads from `IConfiguration` (T008). An alternative would be a describe member on
   `IStoreProvisioner`, but that changes the R10 signature.
7. **Key name in data-model §4.** The table writes `Limits:PlayerTimeoutSeconds`; the real key is
   `GameServer:Limits:PlayerTimeoutSeconds`. T053 catches this in the docs.
8. **Docs language.** The README, guides and PCR are in Vietnamese. The plan does not say which language the new
   guide uses, so the Phase 7 docs follow the existing language.

## Review (T055, tech-lead-reviewer, 2026-10-30): 0 Blocker, 4 Major, 9 Minor

- M1 provisioning scope listing had no 503 retry (the 4 red `CouchbaseProvisioningTests` of T054) — fixed: new
  `CouchbaseManagementRetry` (`RunAsync<T>` with the `ManagementErrorContext { HttpStatus: 503 }` filter, 8 attempts, backoff
  0.5 s doubling to at most 5 s, ~22 s) used by `GetAllScopesAsync` (the scope listing is also the collection listing) and by
  `CreateMissingAsync`, which keeps only "already exists → false" on top. The index statement retry uses the same backoff. The
  index catalog reads (`system:indexes`) are query-service reads, not cluster-manager calls: marked `Readonly(true)` so the SDK
  retries them as idempotent. Tests: `CouchbaseManagementRetryTests` (503 retried until success, lasting 503 fails after 8
  attempts with the original exception, 403 and non-Couchbase errors not retried, backoff sequence).
- M2 provisioning RBAC — contract §2 now lists `Bucket Admin`, `Query Manage Index`, `Data Reader` on the bucket +
  `Query System Catalog` (7.6 syntax, bucket level because the scope does not exist yet), with the secret-source and retry
  rules. New `CouchbaseProvisionerRolesTests` (restricted user `gs-it-*` with exactly these roles, fresh scope `g_prov*`): real
  binary `--provision` exit 0 (all created), re-run exit 0 (all present, catalog path); scope and user dropped in `finally`.
  Shared REST helper `Infrastructure/RbacRest.cs` (`DataOnlyUser` uses it), `Infrastructure/ProvisionerUser.cs`. Minimality
  measured by hand (fresh scope, one role removed each time, EE 7.6.12): no `data_reader` → `FAILED collection
  g_x.txn_meta: AuthenticationFailureException` (5 s); no `query_manage_index` → `FAILED index …: AuthenticationFailureException
  … [13014]` (29 s); no `query_system_catalog` → the index is created but hidden from `system:indexes` → `FAILED index …: not in
  system:indexes after CREATE INDEX` (16 s; the message now names the missing role); no `bucket_admin` → `FAILED scope g_x:
  CouchbaseException (HTTP 403)`. Guide §5.4 and PCR §17 updated to the tested set.
- M3 TLS — `CouchbaseTrustedRoots.IsTrusted` sets `ChainPolicy.DisableCertificateDownloads = true` (no AIA fetch in the
  handshake). Guide §5.3 and PCR §17: the file must be the root CA (an intermediate alone → `PartialChain` → refused; the node
  must send its intermediate) and the two opt-in TLS tests (`CouchbaseSettingsIntegrationTests.Tls_trusts_the_configured_ca_and_refuses_another`,
  `CouchbaseProvisioningTests.Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls`) are a
  mandatory release gate before any Live cutover. Unit test `Only_the_root_ca_anchors_the_chain_and_missing_intermediates_are_not_fetched`
  (root + sent intermediate → trusted; intermediate as the only root → refused; root without the sent intermediate → refused;
  roots still usable after the chain-element certificates are disposed).
- M4 IAP settings validated at startup — new `IapOptionsValidator` (`IValidateOptions<IapOptions>` + `ValidateOnStart`, merged
  into the one report through `IStartupValidator` like GM SSO and the pepper). Outside Development/Test `UseFakeVerifier=true`
  is an error; whenever the real verifiers are used (default outside Development/Test, or `UseFakeVerifier=false`) each missing
  `Iap:Google:PackageName`, `Iap:Google:ServiceAccountEmail`, `Iap:Google:ServiceAccountPrivateKeyPem`, `Iap:Apple:BundleId`,
  `Iap:Apple:RootCertificateBase64` is one error naming the environment variable; the PEM must import with `RSA.ImportFromPem`
  and sign (a public key is refused); the Apple root must load as a DER certificate. Messages never echo a value.
  `StagingSecrets` gained a self-signed Apple root. Guide §1/§2, data-model §3, PCR iap (new section) and PCR §17 (breaking
  row) updated. Tests: `IapStartupValidationTests` (6: missing keys in Production and Staging, valid keys add nothing, fake
  refused in Production, junk PEM / public-key PEM / junk base64 without echo, Development opting into the real verifiers).
- m1 index wait: `error` state → `ProvisioningFailedException` (drop and re-run hint); the wait is bounded by
  `Couchbase:ProvisioningTimeoutSeconds` (`TimeoutException` → exit 2 naming the index). Not unit-testable without a cluster;
  covered by reading the code path (no fake catalog exists).
- m2 report bypasses: `GameServerApplicationExtensions.CheckAll` runs every `IStartupCheck` in a try/catch
  (`GameServerStartupException` → its violations; other → `[<CheckType>] the startup check failed: <Type>: <message>`), used by
  serve and provision mode; `CouchbaseSettingsCheck` resolves the CA path inside the try (also `ArgumentException`/
  `NotSupportedException`); new `ConfigurationBinding.GetSettings<T>` turns a binder `InvalidOperationException` into
  `[configuration] …` → exit 1, used for `Couchbase` in `samples/SampleHost/Program.cs` and for `GameServer` in
  `AddGameServer` (same bypass: `GameServer__Limits__PlayerTimeoutSeconds=abc` crashed the same way). Tests: throwing check in
  serve and provision mode, binder unit test, `SampleHostEntryPointTests` (WAF, Dev + `Couchbase:KvTimeoutMs=abc`, Local +
  `GameServer:Limits:PlayerTimeoutSeconds=abc` → stderr report), invalid CA path.
- m3 file store open errors: `FileDocumentStore.Open` wraps the open (`OpenFolder`) and maps `IOException` /
  `UnauthorizedAccessException` to `[filestore] the data folder '…' cannot be opened (<Type>: …)`. Tests: a file in place of
  the folder (IOException) and a Windows ACL deny on the folder (UnauthorizedAccessException; skipped elsewhere).
- m4 switch: `--provision` matched case-insensitively; `--provision=…`, `-provision`, `/provision[=…]` → `GameServerStartupException`
  (value never echoed) → exit 1. `Program.cs` now parses inside its try (PCR §17 breaking row updated). Tests: case variants,
  5 refused forms, values/other keys untouched, real `SampleHost.dll /provision` and `--provision=true` → exit 1.
- m5 provisioning runs `DeploymentChecks.ValidateSecretSources(app.Configuration, …)`. Test: Production provisioning with
  `Couchbase:Password` in a JSON file → exit 1, provisioner not called, value not echoed.
- m6 `IsTrusted` disposes the certificates of `custom.ChainElements` in `finally` (covered by the M3 test's reuse assertion).
- m7 `Couchbase:Password` required in every environment. Local Couchbase tests set it (fixture/`IntegrationCluster`), the
  `dev-couchbase` profile reads the user variable. Test now a theory over Production/Staging/Development/Test.
- m8 `run-local.sh`: the readiness poller became a watcher that lives until the host exits and traps SIGINT (async subshell;
  bash ≥ 4.4) → kills the process tree 35 s after Ctrl+C; `--lan` probes `0.0.0.0` and every LAN IPv4 too (`lan_addresses`
  moved up). ASCII/LF kept. Manual Git Bash checks (Windows 11): `--lan --port 5187` with a listener on 172.25.0.1:5187 →
  exit 4 naming that address; real host, simulated Ctrl+C after ready → graceful, exit 0 after 8 s; stub host that ignores
  Ctrl+C (copy of the script) → "did not stop within 35 s; killing the process tree" at 38 s, exit 6, nothing left running.
- m9 PCR §17: compression rollback `Couchbase:Compression=false` without a deploy, watch data-service CPU and KV p95/p99 on beta.

**Deviations:** M4 uses the module-options path (`IValidateOptions` + `ValidateOnStart`) instead of an `IStartupCheck`: every
`IStartupCheck` also runs in `--provision`, which by contract §2 needs no IAP credentials
(`Production_provisioning_needs_no_sso_iap_pepper_or_master_key`); the errors still join the one serve-mode report.
`Iap:Apple:RootCertificateBase64` is required too (without it every Apple receipt is rejected), beyond the keys the review
listed. m2 also covers the `GameServer` section binding. m4 makes `GameServerCommandLine.Parse` throw, so hosts parse inside
their composition try (sample and PCR updated). `CouchbaseManagementRetry` attempts 6 → 8 with exponential backoff (also for
index statements: a permanent index error now fails after ~22 s instead of ~7.5 s).

**Tests:** 33 new (Release build `GameServer.slnx`: 0 warnings, 0 errors). Windows 11, `TMP`/`TEMP` = `D:\devcache\gstmp`, EE 7.6.12:
full suite in memory 930 total, 854 passed, 0 failed, 76 skipped (Couchbase + opt-in), 35 s; with `GAMESERVER_TEST_STORE=File`
930 / 854 / 0 / 76, 35 s; with Couchbase 930 total, 926 passed, **0 failed**, 4 skipped (opt-in kill, perf, the two TLS
tests), 18.9 min — the 4 T054 provisioning failures are gone. `CouchbaseProvisioningTests` alone 3 more times: 7 total, 6
passed, 1 skipped (TLS) each, 3.8 min each. Afterwards: no `g_it*`/`g_prov*` scope, no `gs-it-*` user, no `SampleHost`
process, no new `gs-*` temp folder.
