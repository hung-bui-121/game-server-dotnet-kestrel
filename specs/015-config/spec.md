# Feature Specification: Game config — content validated at startup, eager catalogs, config-aware startup checks, a client-visible config version, compressed and persistent delivery (slice 015)

**Feature Branch**: `015-config`

**Created**: 2026-11-04

**Status**: Draft

**Input**: Owner-approved improvement round (2026-11-03) from the first consumer game `E:\ProjGits\zh-pvp-server` (submodule
`framework/` pinned to tag `v0.6.0`; Unity client `E:\ProjGits\research-and-development-games`), friction log
`docs/framework-feedback/FRICTION_LOG.md` of that repo. Four sequential slices, one tag `v0.7.0` at the end of the round;
013 (group A) and 014 (group B) are merged into `main`. This slice is group C, in this priority order:

- **FG-018**: startup does not check the content of config files, and module catalogs load lazily, at the first command
  that needs them. A wrong field type or a duplicate id is a 500 at run time, not a startup error.
- **FG-023**: a startup check that takes `IGameConfig` in its constructor throws while it is resolved when the config does
  not load. The exception escapes `UseGameServer` and the combined startup report is lost.
- **FG-024**: `config_version` does not hash the `clientVisible` set. Making a file client-visible (or removing it) keeps
  the version, so a client that cached the bundle never gets the new file.
- **FG-019**: config is delivered to the client raw: the version hashes server-only files too (a server-only balance change
  makes every client download the unchanged bundle again), the bundle is not compressed, and the SDK cache is in memory
  only (every app start downloads the whole bundle).

FG-020 (hot reload) is not in this slice: the friction log proposes `game-only`, and the constitution says config is a set
of files in the deploy artifact.

Decisions taken by default in this spec are marked **[DEFAULT]** and listed again under Assumptions, so the owner can
override them.

## Scope note

- Verified on `main` = `c6b133f` (2026-11-04). The claims of the four entries still hold. Some lines have moved (see
  "Friction claims re-checked" at the end).
- **Already in place (P0–014):**
  - `FileGameConfigLoader.Load` (CFG-01): `config/base` overlaid by `config/env/<DeploymentEnvironment>`; manifest
    `{label, files, clientVisible}`; checks that the manifest exists and parses, that the label is not empty, that file
    names are plain, that listed files exist, that `.json` files parse, and that `clientVisible ⊂ files`. It throws at the
    **first** fault. `configVersion = "{label}+{hash12}"` over the sorted names and bytes of every effective file.
  - `ModuleDescriptor.RequiredConfigFiles`: a missing file fails startup, naming the module (`ModuleRegistry.Validate`).
  - One aggregated startup report (FR-009 of 011): `UseGameServer` collects registry, catalog, deployment, gate, request-log,
    startup-check and module-options faults, then throws one `GameServerStartupException`. `IStartupCheck.Check` calls are
    isolated one by one (`CheckAll`). The pre-serving endpoint check of 014 runs in `StartingAsync`, before Kestrel binds.
  - Provision mode (`--provision`) validates the registry without config (`Validate(options, config: null)`) and runs the
    startup checks. Export mode (`--export-wire-contract`) never loads config.
  - `POST /api/v1/config/get` (`PlatformRoutes.Config`, name `platform.config`, profile Player; CFG-03, SEC-09):
    `not_modified` when the request carries the current version, else every client-visible file. A bundle over
    `GameServer:Limits:ConfigBundleBytes` (1 MiB) → 413 at request time.
  - `config_version` is on six player messages (`ErrorEnvelope`, `CommandResponse`, `SnapshotResponse`, `ReceiptView`,
    `GetConfigResponse`, identity `LoginResponse`), on GM command responses, in every receipt (CFG-02), in
    `ICommandContext.ConfigVersion`, in the metric `config_version_info{version}` and in the readiness health data.
  - SDK (`com.gameserver.client` 0.7.0): `IConfigCache` injectable through the `GameServerClient` constructor, default
    `InMemoryConfigCache`; `ServerConfigVersion`, `ConfigOutdated`, `ConfigVersionChanged`, `RefreshConfigAsync`.
    `Runtime/Core` has no UnityEngine dependency; Unity-only code lives in `Runtime/Unity` under `#if UNITY_5_3_OR_NEWER`;
    `sdk/GameServer.Client` compiles `Runtime/**` as C# 9 / `netstandard2.1` outside Unity.
  - TestKit (013): `TestGameConfig.FromJson/FromDirectory` (version computed like the host), `CommandContextDouble.WithConfig`.
- **Gaps this slice closes:**
  - The loader reports only its first fault, so fixing a config takes one start per fault.
  - No module can declare rules for the content of its config files. `ResourceCatalog.Load` and `IapCatalog.Load` are lazy
    singleton factories (`EconomyModule.cs:50`, `IapModule.cs:31`); handlers are created per command
    (`CommandRunner.cs:296`), so a bad `resources.json` (duplicate id → `ToDictionary` throws) is a 500 on the first
    command. Sample game catalogs (`BattleCatalog`, `ShopCatalog`) have the same shape.
  - `CheckAll` resolves every check with `GetServices<IStartupCheck>()` outside the per-check `try`. There is no supported
    way for a check to read the config, or to learn that it failed, without throwing.
  - One version serves two purposes: what the server computed with (receipts, CFG-02) and what the client must hold
    (CFG-03). It ignores the `clientVisible` set and changes on server-only edits.
  - No response compression anywhere in `src/`. An oversized bundle is found only by the first client (413).
  - No persistent config cache in the SDK.
- **Not included:**
  - Slice 016: login and command robustness (FG-013, FG-015, FG-029, FG-016, FG-022).
  - FG-020 (hot reload, overlay patching), per-file diff download, config fetch without a session, packaging (FG-005,
    FG-008, FG-009) and FG-014, FG-017, FG-030, FG-031 (see Out of Scope).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A bad config value stops the host with the file and row, and every catalog is built before traffic (Priority: P1) — FG-018

A game or framework module declares, next to its other registrations, the config files it reads and the rules their content
must follow, and the catalogs it builds from them. When the host starts, the framework loads the config, runs every declared
rule of every registered module, and builds every declared catalog before it serves a request. A wrong type, a missing
field, a duplicate id or a reference to an unknown resource stops the host with one report line per fault, naming the
module, the file and the row (or key, or JSON path), together with all other startup faults.

**Why this priority**: a broken balance file is the most common deploy mistake of a live game, and today it surfaces as a
500 on the first player command, after the instance reported ready. The first consumer wrote 5 files of its own to avoid
it (`GameConfigCheck`, `GameCatalog`, `ConfigTable`, `ConfigTableChecks`, `ClientEnums`), and each new table of P2 adds rules.
Stories 2 and 3 build on the config phase this story defines.

**Independent Test**:
- Start the sample host (economy, IAP, sample game) with a valid config and check that every declared catalog was built
  once before the first request, and that the first command reads no config file.
- Start variants that each seed one fault and check that each fails startup with exactly one line naming module, file and
  location. The faults are:
  - `resources.json`: duplicate id, missing `maxQuantity`, `maxQuantity` as a string, unknown `kind`;
  - `iap.json`: a grant naming a resource that is not in `resources.json`, a grant quantity of 0, a product key without
    a store prefix, a duplicate product key;
  - a sample game file with a wrong type;
  - a game-module validator problem;
  - a catalog build that throws.
- Seed six faults across three files plus an unrelated violation (`GameServer:Gate:CacheSeconds=99`) and check that one
  start reports all seven lines.
- Seed two missing files and one invalid JSON file and check that one start reports the three loader lines and no
  validator or catalog line.
- From a consumer-shaped test, run a module's declared rules over a `TestGameConfig` through the test kit and check that
  the lines equal the host's.

**Acceptance Scenarios**:

1. **Given** the economy and IAP modules and a `resources.json` with a duplicate id, a row without `maxQuantity` and a row
   whose `maxQuantity` is a string, **When** the host starts, **Then** it fails before serving traffic, and the aggregated
   report has one line per fault. Each line names the module, the file, the 1-based row, the problem and the fix. No other
   startup fault of the same start is dropped.
2. **Given** an `iap.json` whose product grants a resource that `resources.json` does not define, **When** the host
   starts, **Then** startup fails with one line naming `iap.json`, the product key and the unknown resource. Today the same
   config starts and every purchase of that product is rejected with `UNKNOWN_ITEM`.
3. **Given** a game module that declares rules for its own files (row shape, unique keys, references to other files),
   **When** the host starts with a fault in one of them, **Then** the game's line appears in the same report, in the same
   format as the framework's lines.
4. **Given** a valid config, **When** the host starts, **Then** every catalog declared by a registered module is built
   exactly once before the first request, and every handler or service that asks for that catalog type gets that same
   instance. The first command does not parse a config file.
5. **Given** a catalog whose build reports a problem or throws, **When** the host starts, **Then** one line names the
   module, the catalog type and the cause, and every other rule and catalog still runs.
6. **Given** a rule that fails for a file, **When** the host starts, **Then** the catalogs reading that file are not built,
   and no second line repeats the same fault.
7. **Given** a config that does not load (two listed files missing, one file with invalid JSON), **When** the host starts,
   **Then** the report has one line per loader fault (not only the first one), and no module rule or catalog line.
8. **Given** a config JSON object with the same property name twice (for example a product key repeated in `iap.json`),
   **When** the host starts, **Then** startup fails, naming the file and the JSON path of the duplicate.
9. **Given** provision mode or export mode, **When** the host binary runs, **Then** config rules and catalogs do not run,
   as today: these modes do not load game config.
10. **Given** a consumer test with a set of modules and an `IGameConfig` built by `TestGameConfig`, **When** it asks the
    test kit to check the modules' config declarations, **Then** it gets exactly the lines the host would report, without
    a host or store.

---

### User Story 2 - A startup check never loses the combined report, and it can read the config safely (Priority: P2) — FG-023

A game or store package writes an `IStartupCheck` that needs the loaded config. It takes a framework config status, which
never throws, instead of `IGameConfig`. If a check is written the old way, or its construction fails for any other reason,
the report gets one line naming that check, and every other check and fault is still reported.

**Why this priority**: losing the report is how the first consumer found FG-023. A failed start that shows one fault and
hides the others costs one restart per hidden fault, and the trap is invisible until config breaks. Story 1 removes the
main reason to write such a check, but other checks (store, deployment, game rules over config and options) still need it.

**Independent Test**:
- Register four checks: one that takes `IGameConfig` in its constructor, one whose constructor throws, one that takes the
  config status, and one ordinary check.
- Start with a missing config file plus `GameServer:Gate:CacheSeconds=99`. Check that the report has the config line, the
  gate line, one line for the `IGameConfig` check saying it did not run because the config did not load, one line naming
  the throwing check and its exception, and the ordinary check's own lines. The config fault must not be repeated.
- Start with a valid config and check that the status check sees `Loaded` with the same `IGameConfig` instance the
  handlers get, and that the module config rules passed.
- Run provision mode and check that the status check sees `NotLoaded`.

**Acceptance Scenarios**:

1. **Given** a startup check whose constructor takes `IGameConfig`, a config that does not load, and an unrelated
   violation, **When** the host starts, **Then** the report keeps every config line once, keeps the unrelated violation,
   and adds one line naming the check and saying that it did not run because the game config did not load.
2. **Given** a startup check whose construction throws (a missing dependency, an exception in a factory), **When** the
   host starts (serve or provision mode), **Then** one line names the check's implementation type, or its registration
   when the type is not known, and the cause. Every other check is created and run.
3. **Given** a check that depends on the config status, **When** the config loaded, **Then** the status gives the loaded
   `IGameConfig` and says whether the module config rules and catalogs of story 1 passed. **When** the config failed,
   **Then** the status says so without throwing, and the config faults are already in the report. **When** the mode does
   not load config (provision), **Then** the status says the config is not loaded in this mode.
4. **Given** the startup sequence, **When** checks run, **Then** they run after the config load, the module config rules
   and the catalog builds, and their lines join the same report.
5. **Given** the `IStartupCheck` documentation (XML doc, PCR platform §17, consumer guide), **When** a developer writes a
   check that needs config, **Then** it shows the config status as the supported way, and says that a check must not throw.

---

### User Story 3 - Clients download config only when what they receive changes (Priority: P3) — FG-024, FG-019 (version)

The server computes two versions of the effective config: the server config version of CFG-01 (every file, used for
receipts and operations) and a client config version that covers only what a client receives (the client-visible files,
their names and bytes, and the label). Every player response keeps `config_version` (the server version, CFG-03) and
tells the client the client config version in an additive `client_config_version` field. `GetConfig` answers
`not_modified` when the client holds either current version. A server-only balance edit no longer makes every client
download the unchanged bundle. Making a file client-visible, or removing it from the set, always changes the client version.

**Why this priority**: FG-024 is a correctness bug that stays hidden while the SDK cache is in memory, and becomes real with
the persistent cache of story 4: a client would keep a stale bundle, or never get a new file. FG-019's version part is the
cost side: at the first consumer, editing one value of a server-only file made every client download 27 834 bytes again.

**Independent Test**:
- With `TestGameConfig` and with the host loader, run a version matrix and check both versions in each case. The cases are:
  - edit a server-only file;
  - edit a client-visible file;
  - add a file to `clientVisible` in the base manifest;
  - add a file to `clientVisible` in an env overlay;
  - remove a file from `clientVisible`;
  - change the label;
  - reorder the manifest lists.
- On the sample host, check that login, snapshot, a command (OK, rejected, replayed), an error envelope and `GetConfig`
  (full and `not_modified`) carry the server version in `config_version` and the client version in
  `client_config_version`, and that the receipt of the command records the server version.
- Send `GetConfig` with the current server version (what an SDK from before this slice holds) and check `not_modified`.
  Send it with a version computed by `v0.6.0` and check that the full bundle comes back.

**Acceptance Scenarios**:

1. **Given** a deployed config, **When** only a server-only file changes, **Then** the server config version changes (new
   receipts record it), and the client config version does not. A client holding the bundle gets `not_modified`, and the
   SDK raises no `ConfigVersionChanged`.
2. **Given** a deployed config, **When** a client-visible file changes, **Then** both versions change. Every player
   response carries the new client version, and a client holding the old one gets the full bundle.
3. **Given** a file listed in `files` but not in `clientVisible`, **When** it is added to `clientVisible` (in the base
   manifest or through an env overlay), **Then** the client config version changes and the client gets the file. **When**
   a file is removed from `clientVisible`, **Then** the client version changes and the next bundle no longer contains it.
   The server config version also changes in both cases.
4. **Given** a label change with identical files, **When** the host starts, **Then** both versions change. Both keep the
   format `{label}+{hash12}`.
5. **Given** a running host, **When** a client reads login, snapshot, command (OK, business rejection, replay), error
   envelope and `GetConfig` (full and `not_modified`) responses, **Then** every `client_config_version` field equals the
   client config version and every `config_version` field equals the server config version, as today. Receipts,
   `ReceiptView.config_version`, `ICommandContext.ConfigVersion`, GM command responses and the metric
   `config_version_info` keep the server config version. The readiness health data and the startup log show both.
6. **Given** a client that cached a bundle under a version computed before this slice (`v0.6.0` server version), **When**
   it calls `GetConfig` with that version, **Then** it gets the full bundle once (a client version never equals a server
   version, even for identical input, and the new server version differs from the `v0.6.0` one, BC-5). **Given** an SDK
   from before this slice that holds the current server version, **When** it calls `GetConfig`, **Then** it gets
   `not_modified`.
7. **Given** two instances loading identical config bytes, **When** they start, **Then** they report identical server and
   client versions, independent of the order of the manifest lists and of the machine. `TestGameConfig` computes both
   exactly like the host.

---

### User Story 4 - Client config survives app restarts (Priority: P4) — FG-019 (persistent cache)

A Unity game constructs the SDK client with the package's persistent config cache, pointing it at a folder of its own
(for example under `Application.persistentDataPath`). The client-visible files of the last version are on disk. At the next
app start they are available before login, and `RefreshConfigAsync` downloads nothing while the client config version is
unchanged. A crash while saving never leaves a mix of two versions, and a damaged cache file is treated as empty.

**Why this priority**: with story 3, this removes the download from almost every app start (today: every start, the whole
bundle). It is what the first consumer planned for P3 and the reason FG-024 had to be fixed first. It is SDK-only and
depends on story 3 for correctness.

**Independent Test**:
- In SDK tests outside Unity, start a client with a persistent cache in a temporary folder, log in and refresh config.
  Then create a second client on the same folder (simulated restart), log in, and check that its cache already holds the
  version and files and that refreshing downloads no file.
- Inject a failure at each step of a save and check that the folder then loads either the old or the new version.
- Load truncated, bit-flipped, empty, unknown-format and zero-length cache files and check that each gives an empty cache
  with a diagnostic and no exception.
- Make the folder unwritable and check that the client keeps working for the current run.
- Open the sample Unity client twice (manual, `smoke-build.ps1`) and check the second start's log.

**Acceptance Scenarios**:

1. **Given** a client created with the persistent cache on a folder that holds version V, **When** the app starts,
   **Then** `Config.Version` is V and `Config.Files` holds V's files before any network call. **When** the login response
   carries V as its client config version, **Then** `ConfigOutdated` is false and no `ConfigVersionChanged` is raised. A `RefreshConfigAsync` call gets
   `not_modified` and downloads no file.
2. **Given** a stored version V, **When** the server reports W and the client refreshes, **Then** the cache stores W, the
   next app start loads W, and V is gone from the folder (one version is kept).
3. **Given** a save in progress, **When** the process dies at any point of it, **Then** the next start loads either the
   complete previous version or the complete new one, never files of both and never a partial file.
4. **Given** a cache file that is truncated, corrupted, of an unknown format version or unreadable, **When** the cache is
   created, **Then** it is empty (no version), it does not throw, and it exposes the reason. The next refresh downloads the
   bundle and replaces the file.
5. **Given** a folder that cannot be written (full disk, permissions), **When** the client stores a new version, **Then**
   the new version is used in memory for the current run, the SDK call that stored it does not fail because of the disk,
   and the cache exposes the write failure.
6. **Given** a config file name sent by the server that looks like a path (`../x.json`, an absolute path), **When** it is
   stored, **Then** no file outside the cache folder is created or read: names are data inside the cache file, never paths.
7. **Given** the Unity package, **When** it is compiled outside Unity (C# 9, `netstandard2.1`), **Then** the persistent
   cache builds with no UnityEngine reference. A Unity-only helper, if provided, is in `Runtime/Unity` under
   `#if UNITY_5_3_OR_NEWER`.

---

### User Story 5 - The config bundle travels compressed, and an oversized bundle is found at startup (Priority: P5) — FG-019 (delivery)

A client whose HTTP stack accepts gzip or Brotli gets the `GetConfig` bundle compressed. Other responses are unchanged.
An operator whose client-visible bundle exceeds the bundle limit learns it when the host starts, not from the first client's
413.

**Why this priority**: JSON config compresses well, and the bundle is the largest response of the player API. It matters on
first install and after every client-visible change, which story 4 makes the only downloads left. The startup size check
turns a fleet-wide 413 into a deploy-time error. Both are independent of stories 1–4.

**Independent Test**:
- Send `GetConfig` with `Accept-Encoding: gzip`, with `br`, with `gzip;q=0`, with `identity` and without the header. Check
  the `Content-Encoding` and `Vary` headers, and check that the decoded body is byte-identical to the uncompressed one.
- Check that `not_modified`, login, snapshot, command, error and GM responses are never compressed, even when the request
  accepts gzip.
- Measure the compressed size and the added server time for the committed sample bundle, a bundle shaped like the first
  consumer's (7 tables, about 28 KB) and a synthetic 1 MiB bundle.
- Start hosts whose client-visible bundle is exactly at `ConfigBundleBytes` and one byte over it.
- On Unity Editor, Android and iOS (manual, ACC-18 run), check that the SDK receives a bundle it parses.

**Acceptance Scenarios**:

1. **Given** a `GetConfig` request that accepts gzip (or Brotli), **When** the response carries files, **Then** it is sent
   compressed with `Content-Encoding` and `Vary: Accept-Encoding`, and the decoded body is byte-identical to the
   uncompressed response.
2. **Given** a request without `Accept-Encoding`, with `identity`, or with every offered encoding at `q=0`, **When** the
   response is sent, **Then** it is uncompressed and byte-identical to today's response.
3. **Given** a `not_modified` response or a response below the compression threshold, **When** it is sent, **Then** it is
   not compressed.
4. **Given** any other player, GM or host endpoint, **When** its request accepts gzip, **Then** its response is not
   compressed (login and session responses carry secrets; BREACH).
5. **Given** a client-visible bundle whose `GetConfig` response would exceed `GameServer:Limits:ConfigBundleBytes`, **When**
   the host starts with the player API mapped, **Then** startup fails with one line naming the bundle size, the limit and
   the fix. A bundle exactly at the limit starts. The 413 at request time stays as a defense.
6. **Given** the SDK's Unity transport on a supported platform, **When** the server compresses the bundle, **Then** the
   SDK gets a body it parses. A transport that does not accept compression gets an uncompressed body.

---

### User Story 6 - The framework's own modules, samples and docs use the same path (Priority: P6) — FG-018, FG-019, FG-023, FG-024

Economy, IAP and the sample game declare their config rules and catalogs. The consumer fixture declares a rule and tests it
with the test kit. The sample Unity client uses the persistent cache. The guides and PCRs describe config declarations, the
config status, both versions, compression and the persistent cache. A game developer follows one documented path.

**Why this priority**: the framework modules are the reference examples, and using the path keeps it working. Docs must
describe shipped behaviour, so this story comes last.

**Independent Test**:
- Check that `EconomyModule`, `IapModule` and `SampleGameModule` register no lazy catalog factory, and that their catalogs
  are declared and built at startup (story 1).
- Run the consumer fixture's config test.
- Run `KestrelDemo` twice in Unity (manual) and check that the second start reuses the cached config.
- A reviewer follows the consumer guide's config section to add a game config file with a rule, a catalog and a
  client-visible flag. The reviewer records every missing or wrong step.

**Acceptance Scenarios**:

1. **Given** the economy, IAP and sample game modules, **When** the host starts, **Then** `ResourceCatalog`, `IapCatalog`,
   `BattleCatalog` and `ShopCatalog` are declared catalogs built at startup, and their files have declared rules.
   Command behaviour is unchanged for a valid config.
2. **Given** `docs/guides/consumer-guide.md`, `docs/guides/module-authoring.md`, `templates/module/README.md`,
   `docs/pcr/{platform,economy,iap,sample-game,unity-sdk}.md` and the Vietnamese pages `docs/html/01-gioi-thieu.html` and
   `02-them-command.html`, **When** a developer reads them, **Then** they describe the shipped behaviour of this slice and
   no page still recommends an `IStartupCheck` that takes `IGameConfig`, or changing the label when `clientVisible`
   changes.

---

### Edge Cases

- **Config fails to load**: no `IGameConfig` exists (all or nothing, CFG-01). Module rules, catalogs and the bundle size
  check do not run, so no line cascades from a missing file. Checks that need config learn it from the config status.
- **A required file is missing**: the existing `[module <name>] required config file '<file>' is missing` line reports it
  once. Rules declared for that file do not run, and no catalog reading it is built.
- **A rule reads another module's file** (IAP grants → `resources.json`): rules get read access to every effective file.
  If the other file has its own faults, the rule may report nothing about references into it. Each fault is reported once,
  by the rule that owns the file.
- **Optional files**: a module may declare a file as optional. Its rules run only when the file is in the config.
- **Two modules declare rules for the same file** (a game adds rules to the framework's `resources.json`, such as "CASH and
  GOLD must be currencies"): all rules run, and each line names the declaring module.
- **A rule or a catalog build is slow** (a pathological file): there is no timeout. Rules are synchronous and do no I/O
  beyond reading the loaded config; the cost is bounded by SC-007 for the framework rules.
- **A rule throws instead of reporting**: one line naming the module, the file, the exception type and its message; the
  other rules still run.
- **Unknown properties in a framework file** (a game adds a client-only `icon` field to `resources.json`, which is
  client-visible in the sample): accepted and ignored **[DEFAULT]**. Missing, null or wrongly typed required properties are
  faults.
- **Duplicate property names** in any JSON object of a config file or manifest: a fault naming the JSON path
  **[DEFAULT]**. Today the last one silently wins for objects, or deserialization throws at run time for dictionaries.
- **Non-JSON config files** (CFG-01 allows other formats through a loader the game registers; none exists today): the
  loader checks parse only for `.json`. Module rules may read any file's bytes.
- **Env overlay replaces a file**: rules run over the effective file (after the overlay). The line names the file, and the
  plan decides whether it can also name the overlay folder it came from.
- **A host or test registers its own instance of a catalog type** (`services.AddSingleton(new ResourceCatalog(...))` before
  the module): that instance is the one resolved and kept. Startup still resolves it eagerly. The declared build is not
  run for it, but the file rules still run.
- **Undeclared catalogs** (a game keeps `services.AddSingleton(sp => X.Load(sp.GetRequiredService<IGameConfig>()))`):
  still lazy, as today. The framework cannot see them, and the guide says to declare them. They keep working.
- **`TestHost` with config `Empty`** (013): modules that declare required files fail as today. Tests must give the files
  their modules declare, which is already required by `RequiredConfigFiles`. Content rules now also apply to test configs.
- **A startup check registered through a factory** (`AddSingleton<IStartupCheck>(sp => new X(...))`): when the factory
  throws, the line names the registration (implementation type when known, else the factory's declaring type).
- **A startup check that resolves `IGameConfig` lazily inside `Check`** (the first consumer's workaround): it keeps working.
  The `GameServerStartupException` it may still catch is unchanged.
- **Rolling deploy across this slice**: `v0.6.0` instances send only `config_version` (the old server version), `v0.7.0`
  instances also send `client_config_version`. A new SDK that alternates between them may download the bundle on each
  switch until the rollout ends. CFG-01 already accepts two versions during a rolling deploy; the release notes state it.
  Later deploys (both sides on 0.7.0) do not have this effect.
- **Old SDK, new server**: the SDK ignores the unknown field and keeps caching by `config_version`, so it works unchanged
  (and keeps today's download on server-only edits until it updates). `GetConfig` accepts the server version, so it gets
  `not_modified` while it holds the current one. Its cache reloads once after the upgrade because the server version
  value changed (BC-5).
- **New SDK, old server**: `client_config_version` is absent, so the SDK falls back to `config_version`: today's
  behaviour.
- **Two environments, one device cache folder** (a QA build pointing at beta then uat): the client version is a content
  hash, so a cached bundle is reused only when its content is identical. The guide still recommends one folder per
  environment.
- **A rooted device edits the cache file**: client-visible config is not secret and the server stays authoritative for
  every rule. The integrity check only detects corruption, not tampering.
- **Server sends an empty bundle** (no client-visible file): the cache stores version + zero files, and a restart loads it.
- **Compression and proxies**: `Vary: Accept-Encoding` keeps shared caches correct. Responses are `POST` and are not cached
  by HTTP caches anyway.
- **A client sends `Accept-Encoding: gzip` but its stack does not decode**: out of HTTP contract. Stacks that ask decode
  (HttpClient with automatic decompression, the native Unity stacks). The plan verifies the Unity platforms (research item,
  see Assumptions).
- **The bundle limit and compression**: `ConfigBundleBytes` limits the uncompressed response, because the client decodes
  and keeps the full bundle in memory **[DEFAULT]**.
- **Provision mode with checks that need config**: they see `NotLoaded` and can skip. Provision mode stays config-free.
- **Export mode**: unchanged. No config, no rules, no catalogs, no checks.

## Requirements *(mandatory)*

### Functional Requirements

**Config load report (FG-018; CFG-01, ARC-06, FR-009 of 011)**

- **FR-001** (FG-018, CFG-01): The config loader MUST report every fault it finds in one start, one line each, naming the
  path and the fix, instead of throwing at the first one. The faults are:
  - a missing base manifest;
  - invalid manifest JSON (base or env overlay);
  - an empty label;
  - a file name that is not a plain relative name;
  - a listed file that does not exist;
  - a `.json` file that does not parse (with line and position as the parser reports them);
  - a `clientVisible` name that is not in `files`.

  A config with at least one loader fault produces no config (all or nothing, CFG-01). Existing line texts are kept, so
  log matching keeps working.
- **FR-002** (FG-018) **[DEFAULT]**: A manifest or `.json` config file containing a JSON object with two properties of the
  same name (ordinal comparison) MUST be a loader fault naming the file and the JSON path of the second occurrence.
- **FR-003** (FG-018): The loader's faults MUST join the aggregated startup report with every other startup fault of the
  same start, as today for its single fault.

**Config file declarations and rules (FG-018; ARC-06, CFG-01, ARC-01)**

- **FR-010** (FG-018, ARC-06): `ModuleDescriptor` MUST let a module declare, for each config file it reads:
  - the file name;
  - whether it is required (default) or optional;
  - zero or more rules for its content.

  The declaration MUST be expressible in a module's business project, with no ASP.NET Core or Protobuf dependency
  (ARC-01, the business-project architecture rule stays green). `RequiredConfigFiles` stays accepted and means "required
  file without rules". A file named both ways is one declaration.
- **FR-011** (FG-018): A rule MUST get read access to the whole loaded config (every effective file, so cross-file rules
  are possible) and a problem sink, and MUST report problems through the sink instead of throwing. The sink MUST let a
  rule name the location as:
  - a 1-based row of a row array;
  - an object key;
  - a JSON path.

  Each line names the declaring module, the file, the location, the problem and the fix. The exact line format is
  documented in PCR platform and is the same for framework and game rules.
- **FR-012** (FG-018): A rule MAY report a problem as a warning. Warnings are logged once with the other startup warnings,
  and they do not stop the start.
- **FR-013** (FG-018, constitution II): Every declared rule of every registered module (service-only modules included)
  MUST run in one start, before traffic is served, in every deployment environment, with no setting to turn it off. A rule
  that throws MUST add one line naming the module, the file, the exception type and message, and the other rules MUST
  still run.
- **FR-014** (FG-018): Rules MUST run only when the config loaded (FR-001), and only for files present in it. A missing
  required file is reported once, by the existing required-file line. Provision mode and export mode MUST NOT run rules
  (they do not load config, as today).
- **FR-015** (FG-018, ECO-07): The economy module MUST declare rules for `resources.json`:
  - the root has a `resources` array;
  - each row has a non-empty `id`, unique across rows (ordinal; the line names the first row with that id);
  - `kind` is a string naming one of the kinds the catalog accepts (`currency`, `item`, case-insensitive like the
    catalog's reader); an integer `kind` is a fault;
  - `maxQuantity` is a JSON integer ≥ 1 **[DEFAULT]**;
  - `displayName` is a string.
- **FR-016** (FG-018, IAP-01): The IAP module MUST declare rules for `iap.json`:
  - the root has a `products` object;
  - each key is `{store}:{productId}`, where store is `google` or `apple` and productId is non-empty and at most 128
    characters (the purchase endpoint's limit);
  - each product has a non-empty `grants` array;
  - each grant names a resource defined in `resources.json`, with a JSON integer quantity from 1 to that resource's
    `maxQuantity`.
- **FR-017** (FG-018) **[DEFAULT]**: Framework rules MUST accept and ignore properties they do not know (games add
  client-only fields to client-visible files). A required property that is missing, null or of the wrong JSON type MUST be
  a fault naming the row (or key) and the property.
- **FR-018** (FG-018, FG-001 seam of 013): The consumer test kit (`GameServer.TestKit`) MUST provide a host-free call that
  runs the config declarations (rules and catalog builds) of a set of modules over an `IGameConfig` and returns the lines
  the host would report, with the same text. It MUST NOT depend on a unit-test framework (013 FR-010), a host or a store.

**Catalogs built at startup (FG-018)**

- **FR-020** (FG-018): A module MUST be able to declare a config catalog: a type, the files it reads, and a build function
  over the loaded config that returns the catalog or reports problems. The declaration MUST be expressible in the business
  project (FR-010). A build reads only the config. Cross-file consistency belongs to rules.
- **FR-021** (FG-018): Every catalog declared by a registered module MUST be built once at startup, after the rules and
  before traffic is served. The built instance MUST be what every handler and service resolving that type gets for the
  lifetime of the host. When the host or a test registered its own instance (or factory) of that type first, that
  registration MUST win and MUST still be resolved at startup.
- **FR-022** (FG-018): A catalog whose files have rule errors MUST NOT be built, and no line is added for it. A build that
  reports problems MUST add their lines. A build that throws MUST add one line naming the module, the catalog type, the
  exception type and the message. Every other catalog MUST still be built.
- **FR-023** (FG-018): Two declarations of the same catalog type MUST fail startup, naming both modules.
- **FR-024** (FG-018, ECO-07, IAP-01): `ResourceCatalog` and `IapCatalog` MUST become declared catalogs, replacing their lazy
  singleton factories in `EconomyModule` and `IapModule`. Their public types, their lookups and the command results
  for a valid config MUST NOT change. `ResourceCatalog.Load` and `IapCatalog.Load` stay public, and they MUST NOT throw on
  a config that passed the rules.
- **FR-025** (FG-018): Catalogs that a module registers without declaring them stay lazy. The module-authoring guide MUST
  say to declare them.

**Startup checks and the config status (FG-023; FR-009 of 011, PCR platform §17)**

- **FR-030** (FG-023): Each registered startup check MUST be created inside its own fault isolation, in serve and provision
  modes. A creation failure MUST add one line naming the check's implementation type, or its registration when the type
  is not known (factory registrations), and the cause. Every other check MUST still be created and run. The existing
  isolation of `Check` calls stays.
- **FR-031** (FG-023): When a check cannot be created because the game config did not load, its line MUST say that the
  check did not run because the game config did not load. The config faults already in the report MUST NOT be repeated.
- **FR-032** (FG-023): The framework MUST provide a config status that a check, or any startup code, can depend on and read
  without ever throwing. It holds one of:
  - `Loaded`, with the `IGameConfig` instance the host uses and whether the module rules and catalog builds of this start
    passed;
  - `Failed`: the config did not load, and its faults are in the report;
  - `NotLoaded`: the mode does not load config (provision). In that mode `IGameConfig` cannot be resolved, and a check
    that needs it is reported as a warning, not an error (BC-9).
- **FR-033** (FG-023): Startup checks MUST run after the config load, the module rules and the catalog builds of the same
  start.
- **FR-034** (FG-023, PCR-01): The `IStartupCheck` documentation (XML doc, PCR platform §17, consumer guide) MUST name the
  config status as the supported way for a check to read config, and MUST state that a check taking `IGameConfig` in its
  constructor is safe but reported as not run when config fails.

**Server and client config versions (FG-024, FG-019; CFG-01, CFG-02, CFG-03, PLY-03, ARC-08)**

- **FR-040** (FG-024, CFG-01): The loader MUST compute the server config version `{label}+{hash12}` over the label, the
  sorted names and bytes of every effective file, **and the sorted `clientVisible` set**. It stays the CFG-01
  `configVersion`: receipts (CFG-02), `ICommandContext.ConfigVersion`, metric `config_version_info`, readiness data,
  startup log.
- **FR-041** (FG-019, FG-024): The loader MUST compute a client config version `{label}+{hash12}` over the label and the
  sorted names and bytes of the client-visible files only. The hash input MUST be domain-separated from the server
  version's, so the two never coincide even when every file is client-visible. Both versions MUST be deterministic:
  independent of manifest list order, duplicates in the lists, the overlay order and the machine.
- **FR-042** (FG-019): `IGameConfig` MUST expose the client config version. `TestGameConfig` and every public version
  helper MUST compute both versions exactly like the host loader. A public helper that cannot (the current
  `FileGameConfigLoader.ComputeVersion(label, files)` has no visibility input) MUST be marked obsolete with the fix. The
  `GameConfig` constructor and any factory MUST take or compute both.
- **FR-043** (FG-019, CFG-03, API-03) **[architect decision, owner may override — see D6 under Assumptions]**: Every
  player-API `config_version` keeps its meaning: the server config version (CFG-01, CFG-03). `LoginResponse`,
  `SnapshotResponse`, `CommandResponse` (OK, business rejection, replay), `ErrorEnvelope` and `GetConfigResponse` MUST
  gain an additive field `client_config_version` carrying the client config version. `GetConfig` MUST answer
  `not_modified` exactly when the request's `config_version` equals the current client config version **or** the
  current server config version (ordinal), and the full bundle otherwise. No existing field is renamed, renumbered or
  given a new meaning (API-03). `ReceiptView` gets no new field.
- **FR-044** (CFG-02, PLY-03): Records of what the server computed MUST keep the server config version:
  - receipts and `ReceiptView.config_version` (the stored value: the config the command ran with);
  - `ICommandContext.ConfigVersion`;
  - GM command responses;
  - metric `config_version_info{version}`;
  - the startup log.

  The readiness health data MUST show both versions. A new metric MUST expose the client config version, and the
  existing metric's name and labels MUST NOT change.
- **FR-045** (FG-024): A change of server-only file content MUST NOT change the client config version. A change of
  client-visible bytes, of the `clientVisible` set (base manifest or env overlay) or of the label MUST change it.
- **FR-046** (SDK-03): The SDK MUST track, from every response, the client config version (`client_config_version`, or
  `config_version` when a server before this slice sends no client version). It MUST key its config cache on that
  value, and raise `ConfigVersionChanged` when it differs from the cache, so the event means "client-visible config
  changed". `ServerConfigVersion` keeps the server's `config_version`. A new additive member exposes the tracked client
  version. The documentation states both rules.
- **FR-047** (constitution VI): Automated tests MUST cover the version matrix (server-only edit, client-visible edit,
  visibility added and removed in base and env overlay, label change, list order permutations, all-files-visible
  separation), the carriage on every response kind of FR-043, and the server version in receipts.

**Compressed delivery and bundle limit (FG-019; CFG-03, API-06, SEC-09)**

- **FR-050** (FG-019) **[DEFAULT]**: A `GetConfig` response carrying files MUST be compressed when the request's
  `Accept-Encoding` accepts Brotli or gzip, honouring q-values including `q=0`. Server preference is `br`, then `gzip`.
  The response MUST carry `Content-Encoding` and `Vary: Accept-Encoding`, and its decoded body MUST be byte-identical to
  the uncompressed response.
- **FR-051** (FG-019, security): Only the config route MUST be compressed. A response below the threshold (default 1 KiB,
  which covers `not_modified`) MUST NOT be compressed. No other player, GM or host endpoint response is compressed:
  login and session responses carry secrets (BREACH).
- **FR-052** (FG-019, PERF-04): Compression MUST add at most the server time of SC-005 per request for a 1 MiB bundle. The
  compression level is documented in PCR platform §1. An operational setting MUST turn compression off (rollback switch,
  like `Couchbase:Compression`), with compression on by default **[DEFAULT]**. Config is immutable for the lifetime of
  the host, so the plan may precompute compressed content. The response's per-request server time must stay exact.
- **FR-053** (FG-019, API-06, CFG-03): `GameServer:Limits:ConfigBundleBytes` MUST keep limiting the **uncompressed**
  `GetConfig` response. When the player API is mapped and the config loaded, startup MUST compute the largest `GetConfig`
  response the loaded config can produce (per-request fields at their maximum size). When that size exceeds the limit,
  startup MUST fail with one line naming the size, the limit and the fix (raise the limit or make files server-only).
  The 413 at request time stays.
- **FR-054** (CFG-03, SEC-09, FG-026 of 014): `GetConfig` stays a Player endpoint with the same route, declaration name
  (`platform.config`) and messages (plus the additive field of FR-043). This slice adds no route.
- **FR-055** (FG-019): The SDK MUST receive a parseable bundle whether or not its transport accepts compression. The plan
  verifies how UnityWebRequest handles `Content-Encoding: gzip` on the Editor, Android and iOS (see Assumptions). If a
  supported platform advertises gzip without decoding, the SDK MUST decode gzip itself or keep that platform's requests
  uncompressed.
- **FR-056** (constitution VI): Automated tests MUST cover gzip, br, identity, no header, `q=0`, `not_modified`, the
  threshold, other routes not compressed, decoded-body equality, the rollback switch, and the startup size check at the
  limit and one byte over it.

**SDK persistent config cache (FG-019; SDK-01, SDK-02, SDK-03)**

- **FR-060** (FG-019, SDK-02): The Unity package MUST provide a `PersistentConfigCache : IConfigCache` in `Runtime/Core`,
  constructed with a folder path. It MUST be C# 9 / `netstandard2.1` with no UnityEngine dependency. `Runtime/Unity` MAY add
  a helper rooted at `Application.persistentDataPath`, under `#if UNITY_5_3_OR_NEWER`.
- **FR-061** (FG-019): On construction, the cache MUST load the last stored version and files, so `Version` and `Files` hold
  them before any network call. Loading MUST NOT throw. A missing, empty, truncated or corrupt file, an unknown format
  version, a failed integrity check or an unreadable file MUST give an empty cache (no version) and expose the reason.
- **FR-062** (FG-019): `Store` MUST be atomic. The cache keeps one versioned container (format version, client config
  version, files, integrity check), writes a temporary file and then replaces the previous one. A process death at any
  point leaves the previous or the new version loadable, never a mix and never a partial file. One version is kept.
  `Version` and `Files` MUST reflect the stored version in memory even when the write fails. A write failure MUST NOT
  throw into the SDK call that stored it, and the cache MUST expose the failure.
- **FR-063** (FG-019, security): Config file names received from the server MUST be data inside the container only. No
  file or folder is created, read or deleted outside the cache folder because of a name.
- **FR-064** (FG-019, PCR-01): The container format and its version rule MUST be documented in PCR unity-sdk: an unknown
  version is treated as empty, and a new format is a new version. The cache is not encrypted (client-visible data). The
  guide recommends one folder per server environment.
- **FR-065** (SDK-01, FR-034 of 014): `GameServerClient` MUST keep `InMemoryConfigCache` as its default **[DEFAULT]**.
  SDK changes MUST be additive to the public API, with no public type or member removed or changed in signature. The
  package stays `0.7.0`, and each new package file ships with a committed `.meta`.
- **FR-066** (FG-019): The cache MUST be usable from the thread the SDK runs on (the Unity main thread) and MUST meet the
  time budget of SC-006 for a 1 MiB bundle. Its XML doc states the threading rule.
- **FR-067** (constitution VI): SDK tests outside Unity MUST cover:
  - round trip;
  - simulated restart;
  - a fault injected at each save step;
  - each corrupt-file variant;
  - an unwritable folder;
  - path-like names;
  - a 1 MiB bundle;
  - an end-to-end flow against the sample host where a second client on the same folder downloads no file while the
    client version is unchanged.

**Framework modules, samples and fixture (dogfood)**

- **FR-070** (FG-018): The sample game MUST declare `battle.json` and `shop.json` rules and the `BattleCatalog` and
  `ShopCatalog` catalogs (FR-020), replacing its lazy factories. `tests/ConsumerFixture` MUST declare a rule for
  `notes.json` and test it with the test kit call of FR-018.
- **FR-071** (FG-019): The sample Unity client (`KestrelDemo`) MUST use the persistent cache and log whether config came
  from the cache or the server.

**Documentation (PCR-01; FG-012 continuation of 013)**

- **FR-075** (FG-018, FG-019, FG-023, FG-024): `docs/guides/consumer-guide.md` MUST gain a "Game config" section covering:
  - the manifest and overlay;
  - file declarations, rules and the line format;
  - catalogs;
  - the test kit call;
  - the config status for startup checks;
  - the two versions and what changes each;
  - compression and the bundle limit;
  - the persistent cache, with its Unity setup.
- **FR-076** (PCR-01): The following MUST record this slice:
  - PCR platform: a new section for config (loader faults, declarations, line format, catalogs, config status, both
    versions, compression, startup size check); the `GetConfig` paragraph of §9; `IStartupCheck` in §17; the additive
    `client_config_version` field and the breaking changes BC-1..BC-10 (ARC-08);
  - PCR economy and iap: their file rules;
  - PCR sample-game: its file rules;
  - PCR unity-sdk: the persistent cache and the meaning of `ConfigVersionChanged`;
  - `docs/guides/module-authoring.md` and `templates/module/README.md`: declarations;
  - `docs/html/01-gioi-thieu.html` (descriptor list, startup validation table) and `02-them-command.html`, where config,
    `RequiredConfigFiles` or startup checks appear.
- **FR-077** (FR-072 of 013): Pages and guides touched by this slice MUST state the framework version they match (`0.7.0`,
  "unreleased" until the tag).

**Release and cross-cutting (ARC-08, PCR-01, constitution VI)**

- **FR-090** (ARC-08): Slice 015 MUST write `specs/015-config/release-notes-draft.md` (merged into `docs/releases/v0.7.0.md`
  at round end), listing:
  - the fixed FG ids;
  - every breaking change with its fix;
  - the first-consumer migration checklist in order;
  - the rolling-deploy note of the edge cases.
- **FR-091** (constitution VI): `GameServer.slnx` MUST build in Debug and Release with 0 warnings, and the full test suite,
  Couchbase included, MUST pass. Every acceptance scenario maps to an automated test or a documented manual check. The
  manual checks are:
  - the reviewer walkthrough of story 6;
  - `KestrelDemo` opened twice in Unity (`smoke-build.ps1`);
  - the compressed bundle received by the Unity transport on Editor, Android and iOS (ACC-18 run).

### Key Entities

- **Config load report**: every loader fault of one start. A config with any fault is not produced.
- **Config file declaration**: a module's statement that it reads a file (required or optional) and the rules for its
  content.
- **Config rule**: a check over the loaded config that reports problems (errors or warnings) with a location in one file.
- **Problem line**: one report line naming module, file, location (row, key or JSON path), problem and fix.
- **Config catalog**: a typed object a module builds once from the config at startup and shares with its handlers.
- **Config status**: `Loaded` (config + whether rules and catalogs passed), `Failed` or `NotLoaded`; readable without
  exceptions.
- **Server config version**: CFG-01 `configVersion` over every effective file, the `clientVisible` set and the label;
  recorded by receipts and operations.
- **Client config version**: hash over the label and the client-visible files; carried in `client_config_version` of
  every player response next to `config_version` (the server version); `GetConfig` accepts it or the server version.
- **Config bundle**: the `GetConfig` response with files, possibly compressed in transit, limited by `ConfigBundleBytes`
  uncompressed.
- **Persistent config cache**: the SDK's on-device container of one client config version and its files.

## Breaking changes (to consumers of `v0.6.0` / `main` after 014)

| # | Change | Who is affected | How it is surfaced | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | Config content is checked at startup: framework rules for `resources.json` and `iap.json` (including IAP grants that must name a defined resource), sample rules, and catalogs built before traffic (FR-013..FR-016, FR-021) | Every deployment whose config was accepted only because nothing read the bad row before; test configs given to `TestHost`, `LocalFlavorFactory` and `TestGameConfig`-based hosts | Startup exit 1, one line per fault naming module, file, row/key and fix | Fix the file as the line says |
| BC-2 | A JSON object with a repeated property name in a config file or manifest is a loader fault (FR-002) | Configs that relied on "last one wins" (usually an unnoticed copy-paste) | Startup exit 1 naming file and JSON path | Keep one property |
| BC-3 | `FileGameConfigLoader.Load` reports every fault in one `GameServerStartupException` instead of throwing at the first one (FR-001) | Code or tests that call the loader and read the first violation only | More violations in the same exception; existing line texts unchanged | Read every violation |
| BC-4 | **SDK semantics (PCR unity-sdk):** five player messages gain `client_config_version` (additive, FR-043; `config_version` keeps its meaning). The SDK keys its cache, `ConfigOutdated` and `ConfigVersionChanged` on the client version when the server sends it (FR-046) | Game code that reads the `ConfigVersionChanged` argument or compares `Config.Version` with `ServerConfigVersion`; every client that cached a bundle reloads it once after the upgrade | Fewer `ConfigVersionChanged` events (none on server-only edits); one bundle download per client after deploy; release notes | Compare the cache with `ClientConfigVersion`; read `ServerConfigVersion` only to correlate with receipts |
| BC-5 | The server config version also hashes the `clientVisible` set (FR-040), so its value changes once at the upgrade | Dashboards or alerts keyed on a fixed `config_version_info{version}` value; receipts after the deploy carry the new value | New value in metric and receipts | None (format unchanged); update pinned values |
| BC-6 | `IGameConfig` gains the client config version member; the `GameConfig` constructor takes or computes both versions; `FileGameConfigLoader.ComputeVersion(label, files)` is obsolete (FR-042) | Custom `IGameConfig` implementations and direct `GameConfig`/`ComputeVersion` callers (game test support code) | Compile error (missing member), CS0618 = build error under `TreatWarningsAsErrors` | Implement the member; use `TestGameConfig` or the new helper |
| BC-7 | A client-visible bundle larger than `GameServer:Limits:ConfigBundleBytes` stops the host (FR-053); it was a 413 for every client | Deployments with an oversized bundle (already broken for clients) | Startup exit 1 naming size, limit and fix | Raise the limit or make files server-only |
| BC-8 | `ResourceCatalog`, `IapCatalog`, `BattleCatalog` and `ShopCatalog` are registered from their declarations by the platform, no longer by the modules' `ConfigureServices` (FR-021, FR-024, FR-070) | Code that builds a service collection from a module's `ConfigureServices` alone (without `AddGameServer`) and resolves a catalog | `InvalidOperationException` (no service registered) when resolving the catalog | Register the catalog instance explicitly, or use the test kit call of FR-018 (`ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)`) |
| BC-9 | Provision mode never loads game config: `IGameConfig` cannot be resolved there, and a startup check that needs it is skipped with a **warning** (FR-032, D4) | Checks that read config in provision mode (today they silently load it) | Warning line on stderr; provisioning does not fail because of it | Take the config status and skip when it is `NotLoaded` |
| BC-10 | Startup checks are created by the framework from their registration at each start, inside their own isolation (FR-030); they are no longer resolved as container singletons and are not disposed | A check type that is also resolved elsewhere (it gets a different instance there); disposable checks | None (behaviour) | Keep checks stateless and not disposable |

No route, persisted document shape or GM API response shape changes. Five additive proto fields (FR-043); no existing
field changes meaning (API-03). The config status, the rule and catalog declarations, the client config version member,
compression and the persistent cache are additive.

## First-consumer migration (zh-pvp-server and its Unity client after `v0.7.0`)

Based on the friction log entries and on the `FRAMEWORK-GAP` markers found in both repos on 2026-11-04. The game session
owns the removal. This slice makes it possible and lists it in the release notes (FR-090).

| FG | Marker / workaround today | Removable after `v0.7.0` | Stays (game choice) |
| --- | --- | --- | --- |
| FG-018 | `src/ZhPvp.Game.Adapters/GameConfigCheck.cs` (marker, line 11) registered at `GameModule.cs:27`; lazy `GameModule.cs:26` `TryAddSingleton(sp => GameCatalog.Load(…))`; the throwing `GameCatalog.Load` | `GameConfigCheck.cs` and its registration. The lazy factory: `GameCatalog` becomes a declared catalog whose build is `GameCatalog.TryLoad`. `GameModuleDefinition.cs:23` `RequiredConfigFiles = GameCatalog.ConfigFiles` becomes file declarations with rules. The table checks move into those rules, and the CASH/GOLD rule becomes a game rule on `resources.json`. `GameCatalog.Load` can go | The game's schema code: `ConfigTable.cs`, `ConfigTableChecks.cs`, `ClientEnums.cs`, `CatalogRows.cs` (optionally reporting through the framework sink). `GameConfigStartupTests` facts 1–3 stay, with their expected texts updated if the game adopts the framework line format |
| FG-023 | Marker `GameConfigCheck.cs:12` (resolve `IGameConfig` inside `Check`, catch `GameServerStartupException`) | Goes with the file. `GameConfigStartupTests.A_config_that_does_not_load_keeps_the_report_complete_and_the_game_check_adds_nothing` keeps passing unchanged (2 lines: the missing-file line and `[gate]`), and becomes a plain regression test (drop the FG-023 comment) | — |
| FG-024 | Pinned test `tests/ZhPvp.Tests/Game/ConfigDeliveryTests.cs` `Making_a_file_client_visible_keeps_the_version` (marker, line 136); rule "every `clientVisible` change bumps the label" in `docs/pcr/game.md` (lines 94–96) | The pinned test fails once the framework hashes the set (as designed): delete it or invert it (both versions change). The label rule in `docs/pcr/game.md` | — |
| FG-019 | Pinned test `ConfigDeliveryTests.A_server_only_file_change_changes_the_version_the_client_sees` (marker, line 119). Client markers `Assets/MadProject/Scripts/Online/ServerConfigInjector.cs:39` and `OnlineSession.cs:333` (+ remark at `:329`): in-memory cache, full download on every boot. `docs/pcr/game.md` line 114 | **Trap:** this pinned test asserts `IGameConfig.ConfigVersion`, which is the server version and still changes, so it does **not** fail by itself. Rewrite it to assert that the client config version is unchanged and the server version changed, and drop the marker. Client: pass `new PersistentConfigCache(<folder under Application.persistentDataPath>)` to `new GameServerClient(...)` (`OnlineSession.cs:83`) and drop both markers. `FetchConfigAsync` (`:335-347`) already applies cached files when `updated` is false. Compression needs no client change | The 256 KiB bundle budget test `The_bundle_fits_the_client_visible_budget` (measures uncompressed bytes, still valid) |
| BC-4 / BC-6 | `ConfigDeliveryTests.Every_response_type_carries_the_version_of_the_committed_config` expects `TestGameConfig.Create().ConfigVersion` on every player response; the game's `tests/ZhPvp.Tests/Support/TestGameConfig.cs` builds `new GameConfig(label, FileGameConfigLoader.ComputeVersion(label, files), files, clientVisible)` | — | `config_version` keeps the server version, so the test stays valid once `TestGameConfig.Create` uses `new GameConfig(label, files, clientVisible)` (BC-6: the old constructor and `ComputeVersion` are obsolete = build error, and the old value no longer matches the host, BC-5). Optionally add an assertion on `client_config_version` |

Markers removable by this slice: 4 on the server (FG-018 and FG-023 in `GameConfigCheck.cs`, FG-019 and FG-024 pinned in
`ConfigDeliveryTests.cs`) and 2 on the client (FG-019 in `ServerConfigInjector.cs` and `OnlineSession.cs`), covering four
FG ids. The friction log entries FG-018, FG-019, FG-023 and FG-024 can move to `fixed-in-v0.7.0`. Order for the game:
bump, fix BC-1/BC-2/BC-6 lines if any, move `GameConfigCheck` into declarations, update the version tests (BC-4, the
FG-019/FG-024 tests), then switch the client to the persistent cache.

## Dependencies and seams with other slices

- **014**: no new route. `platform.config` keeps its declaration, so the request log and consumer middleware keep matching
  it by name. The export format does not change. The SDK's generated `Core.cs` and `Identity.cs` gain the additive
  fields of FR-043 (regenerated with WireGen, same paths and `.meta`). The export could later list config file
  declarations; that is not part of this slice.
- **016**:
  - Login without snapshot (FG-013) keeps both version fields in `LoginResponse`, so a client with a
    persistent cache still knows at login whether to refresh.
  - An account-creation hook (FG-015) can use the catalogs, which are built before traffic.
  - FG-029 and FG-022 are unaffected.
- **013**: the test kit gains the config-declaration call (additive). `TestGameConfig` computes both versions, and
  `CommandContextDouble.WithConfig` is unchanged.
- **Deferred FG-020 (hot reload)**: catalogs and the config status are built once from an immutable config. A later reload
  would rebuild both and publish a new status. The guide tells modules to keep config-derived data in declared catalogs
  only, so a reload has one place to refresh.
- **Deferred per-file diff**: `ConfigFile` could gain a per-file hash later as an additive field. No field is added now.
- **Deferred pre-login fetch**: a session-less config fetch would be a new Public route, declared in the catalog and on the
  SEC-09 allowlist, and would need a requirement change (CFG-03 says `GetConfig` is a Player endpoint).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Each seeded config fault fails startup with exactly one report line naming module, file and location
  (row, key or JSON path). This holds in the framework suite for at least these classes:
  - `resources.json`: duplicate id, missing field, wrong type, unknown kind;
  - `iap.json`: unknown resource, quantity 0, key without store, duplicate key;
  - sample file wrong type;
  - game rule problem;
  - catalog build that throws;
  - duplicate property name;
  - missing file;
  - invalid JSON.

  Six faults across three files plus one unrelated violation give exactly 7 lines in one start. Two missing files and one
  invalid JSON file give exactly 3 lines.
- **SC-002**: With a valid config, each declared catalog is built exactly once, before the first request, and the first
  command performs 0 config file parses (counted in tests).
- **SC-003**: The FG-023 scenario of story 2 gives exactly these lines: the config line, the gate line, one not-run line
  for the `IGameConfig` check, one line for the throwing check, and the ordinary check's own lines. No line is duplicated.
  Every creation-failure kind gives exactly one line.
- **SC-004**: In the version matrix, 100% of server-only edits leave the client version unchanged, and 100% of
  client-visible edits, visibility changes (base and overlay) and label changes change it. All response kinds of FR-043
  carry the client version in `client_config_version` and the server version in `config_version`, receipts carry the
  server version, a request carrying the current server version gets `not_modified`, and a request carrying a `v0.6.0`
  version gets the full bundle.
- **SC-005**: For the committed sample bundle, a bundle shaped like the first consumer's (7 tables, about 28 KB) and a
  synthetic 1 MiB JSON bundle, the gzip response is at most 30% of the uncompressed size. Compression adds at most 20 ms
  p95 server time per request for the 1 MiB bundle on the reference development machine (opt-in perf test, as 014). No
  response of another route is compressed.
- **SC-006**: Persistent cache:
  - a restart with an unchanged client version downloads 0 config files;
  - a fault injected at every save step leaves the old or the new version loadable in 100% of cases;
  - every corrupt-file variant gives an empty cache with 0 exceptions;
  - load and save of a 1 MiB bundle each take at most 50 ms outside Unity on the reference machine.
- **SC-007**: The config phase (load, rules, catalogs, bundle size check) adds at most 20 ms to startup for the sample
  config, and at most 300 ms for 1 MiB of JSON row tables, on the reference machine.
- **SC-008**: The public API diff of the Unity package between `main` before and after this slice shows 0 removed or
  changed members. The package stays `0.7.0`, and every new file has a committed `.meta`.
- **SC-009**: After the game bumps to `v0.7.0`, the 6 markers and the label rule listed in "First-consumer migration" can
  be removed, and the game's rewritten test shows that an edit of `ConfigMission.json` (server-only) causes 0 client
  downloads.

## Assumptions

Decisions taken by default (owner may override; each is referenced where it applies):

- **D1 — declarations live in `ModuleDescriptor`**: config files, rules and catalogs are part of the module's single
  registration point (ARC-03, ARC-06), written in business code. Rules are code, not JSON Schema files. A schema
  language would need a validator dependency and could not express cross-file rules such as IAP grant → resource.
- **D2 — all or nothing**: a config with a loader fault produces no config, so no rule, catalog or size check runs, and
  nothing cascades (FR-001, FR-014).
- **D3 — strictness** (confirmed by the architect, plan research R4): duplicate property names are faults (FR-002).
  Unknown properties in framework files are ignored (FR-017). `maxQuantity` must be a JSON integer ≥ 1, because a
  resource no one can hold is a config error (FR-015). `kind` is a string matched case-insensitively, as the catalog reads
  it; an integer `kind` (accepted by the catalog today) is a fault. IAP stores are `google` and `apple` (FR-016).
  **First consumer checked 2026-11-04 (read-only):** every `.json` under `zh-pvp-server/src` and `tests` (18 files) has
  no duplicate property name and parses; its `resources.json` (CASH, GOLD, `currency`, `maxQuantity` 1 000 000 000) and
  `iap.json` (`{"products": {}}`) pass FR-015/FR-016. BC-1 and BC-2 do not break its current config. Its forced change
  is BC-6, in its own test helper.
- **D4 — eager build in serve mode only**: provision and export modes stay config-free, as today (FR-014). Startup checks
  see `NotLoaded` there.
- **D5 — the config status, not a changed `IStartupCheck` signature**: existing checks keep compiling. Checks that need
  config take the status as a dependency (FR-032).
- **D6 — architect decision, owner may override (plan research R1): `config_version` keeps its meaning, and an additive
  `client_config_version` field carries the client config version** (FR-043, FR-046, BC-4). It is on `LoginResponse`,
  `SnapshotResponse`, `CommandResponse`, `ErrorEnvelope` and `GetConfigResponse`. `GetConfig` accepts either current
  version. The SDK keys its cache on `client_config_version` when present, else on `config_version`. Reasons:
  - API-03 forbids changing the meaning of a published field. CFG-01, CFG-03, SMP-01 and ACC-35 name one
    `configVersion` ("Login returns the server's current `configVersion`, every response carries `configVersion`"),
    which stays literally true;
  - no reader of `config_version` (game tests, tools, logs, the SDK's `ServerConfigVersion`) changes meaning silently;
    the first consumer's response-version test keeps passing;
  - wire compatibility both ways: an old SDK ignores the field and behaves as today, and a new SDK falls back on an old
    server.

  **Rejected (the draft default):** make `config_version` on the player API the client config version. It needs no proto
  change and gives old SDKs the gain at once, but it redefines a published field and a Requirements term. Its cost
  argument ("a rule for which one to trust") is one line in the SDK.
- **D7 — the server version also hashes the visible set** (FR-040): it then identifies the full effective config,
  including what is delivered, and FG-024 is fixed for any tool that still reads it.
- **D8 — the label stays in the client version**: a label bump stays the operator's explicit "new config release" for
  clients too, and the `{label}+{hash12}` format stays readable.
- **D9 — compression**: Brotli and gzip, config route only, threshold 1 KiB, on by default, with a rollback switch
  (FR-050..FR-052). The limit applies to uncompressed bytes (FR-053).
- **D10 — an oversized bundle is a startup error**, not a warning (FR-053): with it, no client can get config at all.
- **D11 — the persistent cache is opt-in**: `GameServerClient` keeps the in-memory default, because `Runtime/Core` has no
  platform path to choose (FR-065). Games pass the cache explicitly. The Unity helper is optional.
- **D12 — pre-login config fetch is deferred** (Out of Scope): CFG-03 and SEC-09 say `GetConfig` is a Player endpoint.
  Guest login is automatic at boot, and with the persistent cache the client has its last config before login anyway. No
  strong reason to include it was found.
- **D13 — per-file diff download is deferred**: after D6, compression and the persistent cache, a download happens only
  when client-visible config changed, and a compressed bundle of the size seen today (about 28 KB raw) is a few KB.
  Per-file diff would add a request format and per-file hashes for little gain at this size.

Other assumptions:

- **Requirements wording**: CFG-01's `configVersion` is the server config version of this spec, and every surface that
  CFG-01..03 name keeps it (D6). One clause is read as a refinement: CFG-03/SDK-02 "the client caches the bundle by
  `configVersion` and calls `GetConfig` when the version differs" becomes "by the version of the client-visible part"
  (`client_config_version`). The requirements document is the owner's (local, not in the repo). This spec records the
  reading. The owner confirms it, and may add `clientConfigVersion` to CFG-03 (see Owner confirmations).
- **UnityWebRequest and gzip**: the server compresses only when the request explicitly accepts an encoding, and the SDK
  sets no `Accept-Encoding` itself. Unity's stacks may add the header on some platforms; they are expected to decode what
  they ask for. The plan verifies the Editor, Windows, Android and iOS behaviour (research V8), with a fallback: an SDK
  gzip decoder for the config response (V9), or the rollback switch for an affected platform. The ACC-18 manual run
  confirms it on devices. Brotli is expected only from browsers (WebGL) and `HttpClient`.
- `HttpClientTransport` uses the caller's `HttpClient`. That client asks for compression only when its handler decodes
  (`AutomaticDecompression`), so test and tool clients that do not opt in get uncompressed responses, as today.
- The reference machine is the one used for 013/014 measurements. The perf tests are opt-in (`GAMESERVER_PERF=1`).
- The first consumer's figures (7 client-visible tables, 27 834 B bundle, 2 server-only tables) come from its friction log
  (FG-019, measured 2026-11-02).

## Owner confirmations

No `[NEEDS CLARIFICATION]` remains; every open point has a default. The owner should confirm:

1. **D6**, the architect decision: an additive `client_config_version` field, with `config_version` unchanged (plan
   research R1). It replaces the draft default (semantic change of `config_version`). Confirm it together with the
   CFG-03/SDK-02 cache-key reading under Assumptions.
2. **D3** strictness: duplicate property names failing startup (BC-2), `maxQuantity ≥ 1` and integer `kind` refused.
   Checked against the first consumer: no breakage.
3. **BC-9**: provision mode no longer loads game config for a startup check that asks for it (warning instead).

## Out of Scope

- FG-020: hot reload, config watchers, a versioned config source in a store, JSON merge-patch overlays, removing base
  files or `clientVisible` entries through an overlay.
- Per-file diff or subset download, ETag or HTTP caching of the config route, a CDN or file endpoint (CFG-03 rules it out).
- A config fetch without a session (Public `GetConfig`). See D12.
- JSON Schema files or other declarative schema formats. Loaders for non-JSON config formats (`IConfigFileLoader` of
  CFG-01).
- Module declarations of file visibility (for example "this file must never be client-visible").
- Compression of request bodies or of responses other than `GetConfig`.
- Encrypting or signing the config bundle or the device cache.
- Making the persistent cache the SDK default, or Unity Editor tooling for it.
- Slice 016 scope (FG-013, FG-015, FG-029, FG-016, FG-022). Packaging (FG-005, FG-008, FG-009), FG-014, FG-017, FG-030,
  FG-031.
- Editing the game or client repositories (the game session removes its workarounds after the tag).

## Friction claims re-checked on `main` (`c6b133f`)

- **FG-018**:
  - `FileGameConfigLoader.cs:113` (`JsonDocument.Parse`) holds, and the loader still throws at the first fault (lines
    23, 57, 62, 91, 99, 105, 117).
  - `ResourceCatalog.Load` is now registered at `EconomyModule.cs:50` (log: 52). `IapModule.cs:31` holds.
  - `ActivatorUtilities.CreateInstance` is now at `CommandRunner.cs:296` (log: 276).
  - `ResourceCatalog`'s constructor uses `ToDictionary` (`EconomyModel.cs:37`): a duplicate id throws on the first
    command. `IapCatalog.Load` does the same (`IapModel.cs:57`).
  - **Also**: the sample game's `BattleCatalog`/`ShopCatalog` are lazy in the same way (`SampleGameModule.cs:30-31`).
  - **Also**: `iap.json` grants are never checked against `resources.json`. A mismatch is an `UNKNOWN_ITEM` rejection on
    every purchase of that product.
- **FG-023**:
  - `CheckAll` is now at `GameServerApplicationExtensions.cs:199-219` (log: 164). `GetServices<IStartupCheck>()` is still
    outside the `try` (line 202).
  - Provision mode calls the same `CheckAll` (`ProvisioningRunner.cs:33`), so the trap exists there too.
- **FG-024**: `FileGameConfigLoader.cs:65` and `:69` (`ComputeVersion(label, files)`, no `clientVisible`) and the overlay
  `UnionWith` at `:52` hold.
- **FG-019**:
  - The config route is now declared as `PlatformRoutes.Config` (`platform.config`, 014) and mapped at
    `CoreEndpoints.cs:56-82`. The behaviour is the same: all or nothing, `not_modified`, Player profile, 413 above the
    limit.
  - `ConfigBundleBytes` is now at `GameServerOptions.cs:196` (log: 186).
  - No `ResponseCompression` anywhere in `src/`.
  - `InMemoryConfigCache` is at `Abstractions.cs:82` (holds). The `IConfigCache? config` parameter is now at
    `GameServerClient.cs:98` (log: 96).
  - **Also**: `config_version` is set from the one version in six player messages (`CoreEndpoints.cs:43,64`,
    `IdentityModule.cs:301`, `CommandEndpointFactory.cs:222`, `ErrorResponseWriter.cs:55`; `ReceiptView` from the receipt)
    and in GM command responses (`GmCommandEndpointFactory.cs:197`).
- **FG-020** (not in scope): the `IGameConfig` singleton is now at `GameServerBuilder.cs:192` (log: 160).
