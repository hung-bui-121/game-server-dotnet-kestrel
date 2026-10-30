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

- [ ] T001 [P] In `src/GameServer.Platform/GameServerOptions.cs`, add `StoreKind.File`. The class default stays
  `InMemory`. Add `Identity:Providers:PlayGames:ClientSecret` to the default `SecretKeys` (D8).
  Done when: the solution builds; `new GameServerOptions().SecretKeys` has 6 entries; nothing else reads `StoreKind`
  except `samples/SampleHost/Program.cs`.
- [ ] T002 [P] Create `src/GameServer.Platform/DeploymentEnvironments.cs` with `DeploymentEnvironments` (constants,
  `All`, `TryNormalize` → canonical spelling) and `StandardFlavors` (`DeploymentEnvironmentById`, ordinal lowercase
  keys `local, dev, beta, uat, live`; `Describe()`), as in research R2.
- [ ] T003 [P] Create `src/GameServer.Platform/StartupChecks.cs` with `IStartupCheck` and `StartupCheckResult`
  (`Errors`, `Warnings`), as in research R4.
- [ ] T004 [P] Create `src/GameServer.Platform/Persistence/StoreProvisioning.cs` with `IStoreProvisioner`,
  `ProvisioningOutcome`, `ProvisioningItem(Kind, Name, Outcome, Elapsed)` and
  `ProvisioningFailedException(kind, name, cause, inner)`, as in research R10.
- [ ] T005 [P] Write unit tests in `tests/GameServer.Platform.Tests/Config/DeploymentEnvironmentsTests.cs` (after T002):
  - `TryNormalize`: `development`, `STAGING` and `Test` return the canonical spelling; `Local`, `Dev`, `banana`, `""`
    and `null` are refused.
  - `StandardFlavors` equals the FR-002 table.
  - `Describe()` lists all five flavors.
- [ ] T006 Deployment environment resolution in `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs` (after T002,
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
- [ ] T007 Startup validation in `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs` (after T001,
  T003):
  1. `UseGameServer` runs every `IStartupCheck` and merges its errors into the one `GameServerStartupException`.
     Warnings are logged once under the category `GameServer.Startup`.
  2. It calls `IStartupValidator.Validate()` when the service is registered, and merges
     `OptionsValidationException.Failures` (single, or nested in an `AggregateException`).
  3. The development-only store message at line 131 becomes the R4 text: it names `{Store}` (`InMemory`/`File`), the
     environment and the fix.

  Update the two assertions in `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs` (lines 78 and 87) that
  match the old `in-memory store is configured` text.
- [ ] T008 Command line and run modes (after T003, T004, T007). Add `<InternalsVisibleTo Include="GameServer.Platform.Web.Tests" />`
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
- [ ] T009 Test host defaults in `tests/Shared/TestHost.cs`: `DefaultSettings()` sets `GameServer:EnvironmentId` to
  `test` (D9). `StartAsync` gains an optional `string environmentName = "Development"` parameter. Fix the host tests
  that depend on `dev`:
  - `tests/GameServer.Gm.Tests/SsoTests.cs:160`: `EffectivePermissions("test", "dev")` → `"test"`.
  - `tests/GameServer.Platform.Web.Tests/AccountDeletionTests.cs:338`: seeded mapping `environmentId`, for consistency.

  Unit tests that build `new GameServerOptions { EnvironmentId = "dev" }` are unaffected and stay as they are.
  Done when: the full existing suite is green.
- [ ] T010 Flavor validation tests in a new file `tests/GameServer.Platform.Web.Tests/FlavorValidationTests.cs`, using
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
- [ ] T011 Command-line tests in `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs` (after T008).
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

- [ ] T012 [US2] Persistence seam (research R6):
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
- [ ] T013 [P] [US2] `FileKeyCodec` (research R7) in `src/GameServer.Platform.Testing/FileStore/FileKeyCodec.cs`.
  Tests in `tests/GameServer.Platform.Tests/Persistence/FileKeyCodecTests.cs`:
  - the data-model §6 examples;
  - round trips of `::`, `/`, `\`, `:`, `%`, uppercase, Unicode and 500-character keys;
  - device names (`con` → `%63on`);
  - more than 120 characters → `~` + 52 base32 characters;
  - a property test: distinct keys give names that are distinct under `OrdinalIgnoreCase`;
  - `TryDecode` refuses lowercase hex, encoded literals and foreign names;
  - the empty key is refused.

  The codec tests live in their own file, not in `FileDocumentStoreTests.cs`, so this task can run in parallel.
- [ ] T014 [P] [US2] `FileStoreFolder` in `src/GameServer.Platform.Testing/FileStore/FileStoreFolder.cs`:
  - `.lock` opened with `FileShare.None`;
  - `store.json` created in an empty folder, or checked. A missing file in a non-empty folder, another format or an
    unknown version → `GameServerStartupException` with reset guidance;
  - document paths `g_<gameId>/<collection>/<name>.json`;
  - reset after the lock: delete everything except `.lock` and recreate `store.json`. Refuse a non-store non-empty
    folder, a file-system root, the user profile, the content root or its ancestors, and a folder with a `.git` entry
    (D4);
  - the Windows path-budget computation (≤ 259).
- [ ] T015 [US2] `FileCommitWriter : IDocumentPersistence` (research R8) in
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
- [ ] T016 [US2] `FileStoreLoader` (research R9) in `src/GameServer.Platform.Testing/FileStore/FileStoreLoader.cs`
  (after T013–T015):
  - Delete `*.tmp`. Replay `journal.json` (idempotent). An unparsable journal → startup error naming it.
  - Enumerate the declared collections (all folders when the store is unrestricted). Undeclared folders → one
    information log.
  - Parse in parallel with `Parallel.ForEach`.
  - Validate the shape and name ↔ key (decode, or SHA-256 for `~` names).
  - Collect every malformed file: name the first 20 and count the rest.
  - Drop expired documents and delete their files, using `IClock`.
- [ ] T017 [US2] File store assembly (research R6) in `src/GameServer.Platform.Testing/FileStore/` (after T015, T016).
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
- [ ] T018 [US2] Conformance tests in `tests/GameServer.Platform.Tests/Persistence/FileStoreConformanceTests.cs : StoreConformanceTests`
  (after T017). Each instance uses a temporary folder and the same collections as `InMemoryStoreConformanceTests`.
  After every test, dispose the store, reopen the folder with a new `FileDocumentStore`, and assert that the reopened
  documents equal the last committed state (the reopen invariant). No case is skipped (FR-018, SC-003, US2-3).
- [ ] T019 [US2] File store behaviour tests in `tests/GameServer.Platform.Tests/Persistence/FileDocumentStoreTests.cs`
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
- [ ] T020 [US2] Crash tests in `tests/GameServer.Platform.Tests/Persistence/FileStoreCrashTests.cs` (after T017):
  - Abort at every `FileCommitStep` of a multi-document commit (insert, replace and remove mixed) and of a single
    write, release the lock handle, reopen → all-or-nothing. Before `JournalCommitted` the commit is absent; from it
    on, the commit is present (FR-020, SC-002, US2-2).
  - A torn `journal.json.tmp` or document `.tmp` is ignored and deleted.
  - An unparsable `journal.json` → error naming it.
  - Opt-in performance (`GAMESERVER_PERF=1`, otherwise skip with a message): 10 000 documents load in ≤ 10 s, and p95
    ≤ 50 ms over 500 commits of 6 documents. Print the numbers for PCR (SC-006).
- [ ] T021 [US2] Host tests in a new file `tests/GameServer.Platform.Web.Tests/FileStoreHostTests.cs`, using `TestHost`
  with `services: s => s.AddFileDocumentStore(...)` on a temporary folder (after T017):
  - Staging or Production with `Store=File` → the generalized message names the File store and the fix, and the
    folder is neither created nor locked (US2-8, FR-007, FR-027).
  - A guest login and a counter command → the files exist before the response (FR-019, US2-1).
  - Dispose, then a new host on the same folder → the same snapshot, and the old bearer token still works (FR-028).
- [ ] T022 [US2] Test store switch (after T017). Create `tests/Shared/TestStores.cs`; the plan lists it, but the file
  does not exist yet. Use it from `tests/Shared/TestHost.cs`: when `GAMESERVER_TEST_STORE=File` and the caller passes
  no `store:`, `TestHost` creates a `FileDocumentStore` on a unique temporary folder, registers it through the T017
  overload, exposes the inner `InMemoryDocumentStore` as `Store`, and deletes the folder on dispose. Tests that share
  an explicit store between two hosts stay in memory.
  Done when: the full suite with `GAMESERVER_TEST_STORE=File` is green, or every failure is listed as a Note with its
  reason.
- [ ] T023 [US2] Run the purge tests on both stores (SC-003; Couchbase already runs them in `CouchbaseLeaseAndPurgeTests`,
  after T017). In `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs`, turn the sealed class into an abstract
  base with a virtual store factory, plus the subclasses `InMemoryAccountPurgeTests` and `FileAccountPurgeTests`.
  `host.Store.Faults` keeps working on the inner store. Both subclasses are green.
- [ ] T024 [P] [US2] Architecture rule in `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` (after T017): no
  framework or module assembly other than `PlatformTesting` depends on `FileDocumentStore` (hosts and tests are not in
  `AllFrameworkAndModules`). The existing rules stay green.

**Checkpoint**: Release build is clean. The full suite is green in memory. T022 is green with `GAMESERVER_TEST_STORE=File`.

## Phase 3: US3 — Sample host flavors (P1)

- [ ] T025 [US3] Configuration files in `samples/SampleHost/`:
  - Clean `appsettings.json` down to the data-model §3 base: remove `DeploymentEnvironment`, `EnvironmentId`, `Store`
    and `Gm:Bootstrap` (FR-005).
  - Create `appsettings.Local.json`, `appsettings.Dev.json`, `appsettings.Beta.json`, `appsettings.Uat.json` and
    `appsettings.Live.json` with exactly the data-model §3 content. The Dev file keeps
    `Couchbase:ConnectionString=couchbase://localhost`, which is needed after D10.
  - The GM staff seed exists only in the Local and Dev files.
- [ ] T026 [US3] `samples/SampleHost/Program.cs` (after T008, T017, T025):
  - `var cl = GameServerCommandLine.Parse(args)`, then `WebApplication.CreateBuilder(cl.HostArgs)`.
  - Store switch: `File` → `AddFileDocumentStore(builder.Services, builder.Configuration, builder.Environment)`;
    `Couchbase` and `InMemory` stay as today.
  - Wrap composition in a `try`, because `AddObservability` throws by design: stderr, then `return 1`.
  - End with `return await app.RunGameServerAsync(cl.Mode);`.
  - Keep `public partial class Program`.
- [ ] T027 [P] [US3] `.gitignore` gains `_database/`.
  Done when:
  - `git check-ignore -v samples/_database/dbbase/store.json` reports the new rule;
  - `git check-ignore samples/SampleHost/appsettings.Local.json` reports nothing (the existing `appsettings.*.local.json`
    rule must not hide flavor files on a case-insensitive checkout).
- [ ] T028 [US3] Repository configuration tests in a new file `tests/SampleGame.Tests/SampleHostConfigurationFileTests.cs`
  (after T025, T026). They read the **source** files (walk up to `GameServer.slnx`, then glob
  `samples/SampleHost/appsettings*.json`, so a new file is covered automatically) with `ConfigurationBuilder`, and
  assert the 4 rules of research R3:
  - no key of `new GameServerOptions().SecretKeys` has a value in any file, Local and Dev included (FR-006, US3-8);
  - the base file is clean (FR-005);
  - each flavor file carries its table values (FR-002, US3-1);
  - only the Local file sets `Urls`, and it is loopback on port 5080 (FR-033).

  Host case: `WebApplicationFactory<Program>` with `UseEnvironment("Production")` and no flavor file → startup fails,
  naming `GameServer:EnvironmentId` and the store refusal, never Development (US3-5).
- [ ] T029 [US3] Smoke tests in `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` on the real local flavor (after
  T026). Setup: `UseEnvironment("Local")`, `UseSetting("FileStore:Folder", <temp>)`, jobs off. Keep the outbox
  sweeper **on** in one case, because `OutboxSweeper` does not await `IStoreInitializer` and relies on
  `KeyRingService.StartingAsync` opening the store first. Cases:
  - the existing flow is green;
  - restart: dispose the first factory (this releases the lock), start a new factory on the same folder → counter
    `rev` 1, and the guest bearer token still works (US1 persistence, US2-1);
  - GM local sign-in with the Local seed (`GmBrowser` from `tests/Shared`), then restart → the session cookie is still
    accepted, so the key ring and the GM session survived (US2-9, FR-028).

  The temporary folder is deleted at the end.
- [ ] T030 [US3] In `tests/GameServer.Gm.Tests/SsoTests.cs`, remove the override
  `values["Gm:Bootstrap:Staff:0:Roles:0"] = "Support"` and its comment, because the base file no longer seeds staff
  (after T025). Grep `tests/` for other dependencies on the base-file seed (`admin`, `dev-only-change-me`) or on
  `EnvironmentId=dev` coming from the copied base file. Fix each one, and list them in a Note.
  Done when: `GameServer.Gm.Tests` and `SampleGame.Tests` are green.
- [ ] T031 [P] [US3] In `tools/local/start-hosts.ps1`, add `ASPNETCORE_ENVIRONMENT` to `$names` (saved and restored)
  and set it to `Dev` for each host (FR-033, US1-9). Nothing else changes.
- [ ] T032 [US3] Opt-in real kill test in a new file `tests/SampleGame.Tests/FileStoreProcessKillTests.cs`
  (`GAMESERVER_KILL_TEST=1`; otherwise skip with a message) (after T026, T029). The plan placed it in
  `GameServer.Platform.Tests`, which does not reference SampleHost; `SampleGame.Tests` does.
  - Start `SampleHost.dll` from the test output with `ASPNETCORE_ENVIRONMENT=Local`, a temporary
    `--FileStore:Folder`, and `--urls http://127.0.0.1:<free port>`.
  - Send a burst of counter and economy commands with the test-auth header, and kill the process tree N = 20 times at
    random moments.
  - After each kill, reopen the folder with `FileDocumentStore` and check that every receipt has its aggregate
    revisions and that no partial commit exists (SC-002).

**Checkpoint**: The full suite is green in memory and with `GAMESERVER_TEST_STORE=File`. `dotnet run` of SampleHost
without a flavor fails closed.

## Phase 4: US4 — Couchbase settings (P2) — parallel with Phase 2 after Phase 1

- [ ] T033 [US4] **[VERIFY first]** Check the CouchbaseNetClient 3.10.0 `ClusterOptions` member names and defaults,
  using the package XML docs under `~/.nuget/packages/couchbasenetclient/3.10.0/` or a reflection scratch test:
  - `KvTimeout`, `QueryTimeout`, `Compression`, `CompressionMinSize` (default 32?), `NumKvConnections`,
    `MaxKvConnections`;
  - whether a built-in trusted-root option exists, or `KvCertificateCallbackValidation` and
    `HttpCertificateCallbackValidation`.

  Fallbacks: use the callbacks for TLS trust. If a pool or compression property is missing, drop that key from
  `CouchbaseOptions`, data-model §4 and the docs, and record a deviation.
  Then update `src/GameServer.Platform.Couchbase/CouchbaseOptions.cs`: add the R5 members and `UsesTls`, and give
  `ConnectionString` the default `string.Empty` (D10).
- [ ] T034 [US4] Create `src/GameServer.Platform.Couchbase/CouchbaseClusterOptionsFactory.cs` with the internal
  `Create(...)`. It maps every key and leaves the durability timeout unchanged. With `couchbases://` and
  `TrustCertificatePath`, it trusts only that CA: `X509Chain` with `CustomRootTrust`, host-name validation kept.
  `src/GameServer.Platform.Couchbase/CouchbaseConnection.cs` uses the factory and resolves `TrustCertificatePath`
  against the content root (`IHostEnvironment`). The certificate is PEM or DER, loaded through `X509CertificateLoader`
  or `CreateFromPem`, not the obsolete constructors. Depends on T033.
- [ ] T035 [US4] Create `src/GameServer.Platform.Couchbase/CouchbaseSettingsCheck.cs` with
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
- [ ] T036 [US4] In `src/GameServer.Platform.Couchbase/CouchbaseServiceCollectionExtensions.cs`, remove the
  `BucketName` throw (the check now reports it) and register `CouchbaseSettingsCheck` as `IStartupCheck` (after T035).
  The signature `AddCouchbaseDocumentStore(services, options)` stays, because `Program.cs` and
  `tests/GameServer.Platform.Couchbase.IntegrationTests/Infrastructure/CouchbaseGameHost.cs` call it.
- [ ] T037 [US4] Unit tests in a new file `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseSettingsTests.cs`.
  They need no cluster and must **not** skip without `COUCHBASE_CONNSTR` (after T034–T036). Cases:
  - the factory maps every key (FR-011, FR-016);
  - the check: each out-of-range value, each missing required key, `http://`, `couchbase://` with no host, a missing CA
    file, a CA file that is not a certificate (US4-2, US4-5, FR-013);
  - Production with `couchbase://` → error; Staging → warning; Development → silent (US4-3);
  - each legacy key → a warning naming its replacement (US4-6);
  - Production without a password → the message names `Couchbase__Password` (US3-7).
- [ ] T038 [US4] Integration tests in the same file (after T037; skip with a message without `COUCHBASE_CONNSTR`):
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

**Checkpoint**: Couchbase suite green. Existing `CouchbaseFixture` and `CouchbaseGameHost` unaffected (they set the connection string).

## Phase 5: US5 — Production provisioning (P2)

- [ ] T039 [US5] Provisioner in `src/GameServer.Platform.Couchbase/CouchbaseProvisioner.cs` (after T004 and Phase 4).
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
- [ ] T040 [US5] `VerifySchemaAsync` in the same file (research R11; after T039). **[VERIFY first]** the SDK 3.10
  exception for an unknown collection: is it `CollectionNotFoundException`, or a timeout whose retry reasons include
  `CollectionNotFound`? Pin it with a throw-away collection drop in T042 before finishing the classification.
  Fallback: classify both forms as missing.
  When `ProvisionOnStartup` resolves to false, `EnsureInitializedAsync` runs the check once (as a shared task). It
  probes every required collection, `txn_meta` included, with a parallel `GetAsync("_schema_probe")` and a 3 s
  timeout.
  - Missing collections → `GameServerStartupException` with the text of contracts §3.
  - Any other error is rethrown unchanged.

  The host exits with 1 before Kestrel listens, through T008 (D2).
- [ ] T041 [US5] In `src/GameServer.Platform.Couchbase/CouchbaseServiceCollectionExtensions.cs`, register
  `IStoreProvisioner` → the `CouchbaseProvisioner` singleton (after T036, T039; same file as T036).
- [ ] T042 [US5] Integration tests in a new file `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseProvisioningTests.cs`
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
- [ ] T043 [US3] Flavor readiness tests in a new file `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs`
  (US3 independent test, SC-004; after T025, T026, T042). Use `WebApplicationFactory<Program>` per flavor, with
  environment overrides that point to the IT bucket and the throw-away scope provisioned by `--provision` first.
  - **Dev**: ready; development tools on.
  - **Beta and Uat**: ready with test secrets set through `UseSetting`: master key; GM OIDC with an `https`
    authority; pepper; IAP key material generated in the test, with a valid RSA PEM. Development tools are off: the
    cheat route returns 404, the test-auth header is ignored, GM OpenAPI returns 404. The `config/env/Staging` overlay
    is used (US3-6, FR-008, FR-010).
  - **Live**: ready only with `COUCHBASE_TLS_CONNSTR`; otherwise assert that the TLS error is the only error.
  - Missing Couchbase settings in Beta → one start lists every missing key (US3-7).

**Checkpoint**: The Couchbase suite is green, including provisioning and flavor readiness.

## Phase 6: US1 — Local run (P1)

- [ ] T044 [US1] **[VERIFY first]** Two runtime facts. Record both as a Note.
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
- [ ] T045 [P] [US1] Create `tools/local/run-local.ps1` per contracts §4 and research R12, compatible with Windows
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
- [ ] T046 [P] [US1] Create `tools/local/run-local.sh` for bash on macOS, Linux and Git Bash, with the same contract.
  It differs from T045 as follows:
  - options `--port`, `--reset`, `--lan`;
  - port probe through `/dev/tcp`;
  - the readiness poller and URL printer run in a background subshell, while `dotnet run` runs in the **foreground**,
    so SIGINT reaches it;
  - a `trap` kills the poller;
  - exit codes as in T045.

  Set the executable bit (`git update-index --chmod=+x`). `.gitattributes` already forces LF.
- [ ] T047 [P] [US1] Change the default URL to `http://localhost:5080/` (FR-039). Edit these files as text; do not open
  the Unity Editor:
  - `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs:24`;
  - the serialized value in `samples/SampleGame/KestrelDemo/Assets/Scenes/SampleGameClient.unity:151`;
  - `samples/SampleGame/KestrelDemo/Assets/Editor/SmokeBuild.cs:38`;
  - `sdk/unity/ci/smoke-build.ps1` (lines 9 and 20).

  Done when: grepping these four files for `localhost:5000` finds nothing.
- [ ] T048 [US1] Manual acceptance, recorded as a Note on this task with OS and shell per row (after T026, T031,
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

**Checkpoint**: Scripts and profiles accepted on Windows. Unity defaults point to 5080.

## Phase 7: US6 — Docs and close (P3)

Write documentation in the language of the existing repository docs (Vietnamese, as in `README.md`,
`docs/guides/local-real-env-testing.md` and `docs/pcr/platform.md`). Configuration keys, commands and exit codes stay
literal.

- [ ] T049 [P] [US6] `README.md`: one primary local path (`tools/local/run-local.ps1`/`.sh` or the `local` launch
  profile), the GM URL on port 5080 (line 45 says 5000 today), a pointer to the flavor table and guide, and the
  Couchbase multi-instance path presented as the "real environment" option (US6-1, FR-040).
- [ ] T050 [P] [US6] New guide `docs/guides/environments-and-local-run.md` (FR-040, US6-2). It covers:
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
- [ ] T051 [P] [US6] `docs/guides/local-real-env-testing.md`: `start-hosts.ps1` now selects the Dev flavor; add a
  pointer to `--provision` and the new guide.
- [ ] T052 [P] [US6] PCR (FR-041). `docs/pcr/platform.md` gets a new **§17**. It records:
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
- [ ] T053 [US6] Docs key test in a new file `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` (after T050,
  T052; SC-008). It extracts the back-ticked keys matching `^(GameServer|Couchbase|FileStore):[A-Za-z0-9:]+$` from the
  new guide, PCR §17 and `README.md`. Each key must resolve, by reflection, to a bound property of `GameServerOptions`
  (nested), `CouchbaseOptions` or `FileStoreOptions`. Legacy keys listed in the mapping table are excluded explicitly.
  The test also catches `Limits:` written without `GameServer:`, as in data-model §4.
- [ ] T054 Pre-review full run: Release build; full suite in memory; full suite with `GAMESERVER_TEST_STORE=File`;
  Couchbase suite including T042 and T043. Record the counts.
- [ ] T055 Run tech-lead-reviewer on the slice. Fix Blocker and Major findings. Append the review summary to this file.
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
