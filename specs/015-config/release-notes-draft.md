# Release notes draft — v0.7.0 (slice 015 config)

Framework `0.7.0` (unreleased). Input of the `v0.7.0` annotated tag message (FR-090), merged with the notes of slices 013,
014 and 016 at the end of the round (one tag). Started in T002, extended by T028, T033, T040, T045, T052 and T064; final
pass in T065.

**Fixed:** FG-018 (config content validated at startup, every catalog built before traffic), FG-023 (a startup check never
loses the combined report and reads config safely), FG-024 (the server config version hashes the client-visible set),
FG-019 (a client config version, compressed `GetConfig`, persistent SDK cache). **Breaking:** BC-1 to BC-10 (table below).
**Obsolete:** the 4-argument `GameConfig` constructor and `FileGameConfigLoader.ComputeVersion(label, files)` (BC-6).

**Unchanged:** no route, persisted document shape or GM API response shape changes; five additive proto fields
(`client_config_version` on `ErrorEnvelope`, `CommandResponse`, `SnapshotResponse`, `GetConfigResponse` and identity
`LoginResponse`); no existing field changes meaning (API-03); the Unity package stays `0.7.0`.

## Breaking changes

| Id | Change | Who is affected | How it surfaces | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | Config content is checked at startup: framework rules for `resources.json` and `iap.json` (IAP grants must name a defined resource), sample rules, and catalogs built before traffic (FR-013..FR-016, FR-021) | Every deployment whose config was accepted only because nothing read the bad row before; test configs given to `TestHost`, `LocalFlavorFactory` and `TestGameConfig`-based hosts | Startup exit 1, one line per fault naming module, file, row/key and fix | Fix the file as the line says |
| BC-2 | A JSON object with a repeated property name in a config file or manifest is a loader fault (FR-002) | Configs that relied on "last one wins" (usually an unnoticed copy-paste) | Startup exit 1 naming file and JSON path | Keep one property |
| BC-3 | `FileGameConfigLoader.Load` reports every fault in one `GameServerStartupException` instead of throwing at the first one (FR-001) | Code or tests that call the loader and read the first violation only | More violations in the same exception; existing line texts unchanged | Read every violation |
| BC-4 | SDK semantics (PCR unity-sdk): five player messages gain `client_config_version` (additive, FR-043; `config_version` keeps its meaning). The SDK keys its cache, `ConfigOutdated` and `ConfigVersionChanged` on the client version when the server sends it (FR-046) | Game code that reads the `ConfigVersionChanged` argument or compares `Config.Version` with `ServerConfigVersion`; every client that cached a bundle reloads it once after the upgrade | Fewer `ConfigVersionChanged` events (none on server-only edits); one bundle download per client after deploy | Compare the cache with `ClientConfigVersion`; read `ServerConfigVersion` only to correlate with receipts |
| BC-5 | The server config version also hashes the `clientVisible` set (FR-040), so its value changes once at the upgrade | Dashboards or alerts keyed on a fixed `config_version_info{version}` value; receipts after the deploy carry the new value | New value in metric and receipts | None (format unchanged); update pinned values |
| BC-6 | `IGameConfig` gains `ClientConfigVersion`; the `GameConfig` constructor computes both versions; `FileGameConfigLoader.ComputeVersion(label, files)` is obsolete (FR-042) | Custom `IGameConfig` implementations and direct `GameConfig`/`ComputeVersion` callers (game test support code) | Compile error (missing member), CS0618 = build error under `TreatWarningsAsErrors` | Implement the member; use `TestGameConfig` or `new GameConfig(label, files, clientVisible)` |
| BC-7 | A client-visible bundle larger than `GameServer:Limits:ConfigBundleBytes` stops the host (FR-053); it was a 413 for every client | Deployments with an oversized bundle (already broken for clients) | Startup exit 1 naming size, limit and fix | Raise the limit or make files server-only |
| BC-8 | `ResourceCatalog`, `IapCatalog`, `BattleCatalog` and `ShopCatalog` are registered from their declarations by the platform, no longer by the modules' `ConfigureServices` (FR-021, FR-024, FR-070) | Code that builds a service collection from a module's `ConfigureServices` alone (without `AddGameServer`) and resolves a catalog | `InvalidOperationException` (no service registered) when resolving the catalog | Register the catalog instance explicitly, or `ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)` |
| BC-9 | Provision mode never loads game config: `IGameConfig` cannot be resolved there, and a startup check that needs it is skipped with a **warning** (FR-032, D4) | Checks that read config in provision mode (today they silently load it) | Warning line on stderr; provisioning does not fail because of it | Take `GameConfigStatus` and skip when it is `NotLoaded` |
| BC-10 | Startup checks are created by the framework from their registration at each start, inside their own isolation (FR-030); they are no longer resolved as container singletons and are not disposed | A check type that is also resolved elsewhere (it gets a different instance there); disposable checks | None (behaviour) | Keep checks stateless and not disposable |

## zh-pvp-server migration checklist (in this order)

1. **Bump** the `framework/` submodule to `v0.7.0`.
2. **BC-1 / BC-2 / BC-6 lines, if any.** Start each flavor once; fix every `[module …] config …` or `[config] … twice at …`
   line (none expected: the current config passes FR-015/FR-016 and has no duplicate property, checked 2026-11-04).
   `tests/ZhPvp.Tests/Support/TestGameConfig.cs` builds `new GameConfig(label, FileGameConfigLoader.ComputeVersion(label,
   files), files, clientVisible)`: use `new GameConfig(label, files, clientVisible)` (CS0618 = error otherwise).
3. **`GameConfigCheck` into declarations (FG-018, FG-023).** Delete `src/ZhPvp.Game.Adapters/GameConfigCheck.cs` (markers
   FG-018 line 11, FG-023 line 12) and its registration at `src/ZhPvp.Game.Adapters/GameModule.cs:27`. Replace the lazy
   `GameModule.cs:26` `TryAddSingleton(sp => GameCatalog.Load(…))` with a declared catalog in `GameModuleDefinition`
   whose build is `GameCatalog.TryLoad`. `GameModuleDefinition.cs:23` `RequiredConfigFiles = GameCatalog.ConfigFiles`
   becomes file declarations with rules; the table checks move into those rules and the CASH/GOLD rule becomes a game
   rule on `resources.json`. `GameConfigStartupTests.A_config_that_does_not_load_keeps_the_report_complete_and_the_game_check_adds_nothing`
   keeps passing (2 lines) and drops its FG-023 comment.
4. **Version tests (FG-024, FG-019, BC-4).** `tests/ZhPvp.Tests/Game/ConfigDeliveryTests.cs`:
   `Making_a_file_client_visible_keeps_the_version` (marker line 136) fails by design: delete or invert it (both versions
   change). `A_server_only_file_change_changes_the_version_the_client_sees` (marker line 119) does **not** fail by itself
   (it asserts the server version): rewrite it to assert the client version is unchanged and the server version changed.
   Remove the label rule of `docs/pcr/game.md` lines 94–96 and update line 114.
   `Every_response_type_carries_the_version_of_the_committed_config` stays valid; optionally assert `client_config_version`.
5. **Client persistent cache (FG-019).** `Assets/MadProject/Scripts/Online/OnlineSession.cs:83`:
   `new GameServerClient(options, new UnityWebRequestTransport(), config: UnityConfigCache.Create(environmentName))`; drop
   the markers `ServerConfigInjector.cs:39` and `OnlineSession.cs:333` (and the remark at `:329`). `FetchConfigAsync`
   (`:335-347`) already applies cached files when `updated` is false. Compression needs no client change.

Markers removable (SC-009): 4 on the server (FG-018 and FG-023 in `GameConfigCheck.cs`, FG-019 and FG-024 pinned in
`ConfigDeliveryTests.cs`) and 2 on the client (FG-019 in `ServerConfigInjector.cs` and `OnlineSession.cs`) = **6
markers**, plus the label rule of `docs/pcr/game.md`. The friction log entries FG-018, FG-019, FG-023 and FG-024 move to
`fixed-in-v0.7.0`.

## Rolling deploy

`v0.6.0` instances send only `config_version` (the old server version); `v0.7.0` instances also send
`client_config_version`. A new SDK that alternates between them may download the bundle on each switch until the rollout
ends (CFG-01 already accepts two versions during a rollout). Later deploys (both sides on 0.7.0) do not have this effect.

| Client | Server | Behaviour |
| --- | --- | --- |
| SDK ≤ pre-015 0.7.0 | 015 | Reads `config_version` (server), ignores field 5. Caches by server version. `GetConfig` with the current server version → `not_modified`. Downloads again on server-only edits (as today) until it updates. One full download after the upgrade (BC-5 value change) |
| SDK 015 | 015 | Caches by client version: no download on server-only edits |
| SDK 015 | v0.6.0 / pre-015 | Field 5 absent → falls back to `config_version`: today's behaviour |
| Mixed rollout | — | The effective version may alternate between instances: at most one download per switch until the rollout ends |
| HTTP client without decompression | 015 | Sends no `Accept-Encoding` (HttpClient without `AutomaticDecompression`) → identity |

## FG-018 — config content validated at startup, catalogs built before traffic

A module declares the config files it reads, their content rules and the typed catalogs built from them, in its
`ModuleDescriptor` (Core, `GameServer.Config`):

```csharp
ConfigFiles = [ConfigFileDeclaration.Required(ResourceCatalog.ConfigFile, EconomyConfigRules.Resources)],
ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => ResourceCatalog.Load(c.Config), ResourceCatalog.ConfigFile)],
```

- The config phase is the first block of `UseGameServer`: load (every loader fault in one report, BC-3), registry checks
  (the required-file line now also covers declared files and catalog files), every module rule, then every catalog, before
  anything resolves a service. One line per fault, naming module, file and row / key / JSON path, and the fix.
- Declared catalogs are built once and registered by the platform (BC-8): `ResourceCatalog`, `IapCatalog`, `BattleCatalog`
  and `ShopCatalog` are no longer lazy `TryAddSingleton` factories. A catalog whose file is missing or has a rule error is not
  built and adds no second line. A host or test registration of the type (before or after `AddModule`) wins and is resolved
  once in the phase.
- Rules ship for `resources.json` (`economy.resources`), `iap.json` (`iap.products`, grants checked against the well-formed
  rows of `resources.json`), and the sample's `battle.json`/`shop.json`. A game adds its own rules to any file (also the
  framework's `resources.json`); each line names the declaring module.
- Rules are written with `ConfigJson` (shared wording: `'{property}' is missing`, `… is null`, `… must be an integer of at
  least 1, found "999"`) and report through `ConfigProblems`; a rule or build that throws gives one line naming it.
- Test kit: `ConfigDeclarationCheck.Check(config, modules)` gives exactly the host's lines (`Errors`, `Warnings`,
  `ThrowIfFailed()`, `Catalog<T>()`, `AddCatalogsTo(services)`).

Real line texts (slice 015 tests, asserted literally):

```text
[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found "999". Fix: set the largest quantity a player may hold (ECO-07).
[module economy] config resources.json row 4: id 'gold' is already defined in row 1. Fix: give every resource a unique id (ECO-07).
[module iap] config iap.json key 'gems_100': the key must be "google:{productId}" or "apple:{productId}" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).
[module iap] config iap.json at $.products['google:gems_100'].grants[0]: 'quantity' must be an integer from 1 to 1000000 (maxQuantity of 'gem'), found 0. Fix: correct the grant (IAP-01).
[module iap] config iap.json at $.products['google:starter_pack'].grants[1]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01).
[module sample.game] config battle.json row 2: 'xp' must be an integer of at least 0, found "70". Fix: give every stage an integer xp of at least 0.
[config] '…/config/base/iap.json' has property 'google:gems_100' twice at $.products (line 4, byte 5). Fix: keep one property; the last one used to win silently (CFG-01).
[catalog ResourceCatalog] is declared by economy and game. Fix: declare it in one module; the other takes it from DI (FG-018).
[module economy] catalog ResourceCatalog: the instance registered by the host could not be created: InvalidOperationException: no file. Fix: correct the registration or remove it to use the declared build (FG-018).
```

Line and byte of the duplicate line are 1-based (the line of the repeated name, the byte of its opening quote). A config
file under `config/env/<env>` is named `resources.json (config/env/Staging)`.

BC rows with the real texts:

| Id | Line seen by the operator | Fix |
| --- | --- | --- |
| BC-1 | `[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found "999". Fix: …` | Correct the row |
| BC-2 | `[config] '{path}' has property '{name}' twice at {jsonPath} (line {l}, byte {b}). Fix: keep one property; the last one used to win silently (CFG-01).` | Keep one property |
| BC-3 | Several `[config] …` lines in one `GameServerStartupException` (two missing files + one invalid JSON file = 3 lines) | Read `Violations`, not the first line |
| BC-8 | `No service for type 'GameServer.Modules.Economy.ResourceCatalog' has been registered.` (services built from `ConfigureServices` alone) or `ResourceCatalog is a declared config catalog: the platform builds it in UseGameServer. Fix: … ConfigDeclarationCheck.Check(config, modules) and call report.AddCatalogsTo(services) (FG-018).` (host built without `UseGameServer`) | `ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)` or register the instance |

Consumer impact (FG-018 rows of the first-consumer table): `src/ZhPvp.Game.Adapters/GameConfigCheck.cs` and its
registration (`GameModule.cs:27`) can go; the lazy `GameModule.cs:26` `TryAddSingleton(sp => GameCatalog.Load(…))` becomes a
declared catalog built with `GameCatalog.TryLoad`; `GameModuleDefinition.cs:23` `RequiredConfigFiles = GameCatalog.ConfigFiles`
becomes file declarations with rules (the table checks), and the CASH/GOLD check becomes a game rule on `resources.json`.
`ConfigTable.cs`, `ConfigTableChecks.cs`, `ClientEnums.cs`, `CatalogRows.cs` stay (game schema code).

## FG-023 — a startup check never loses the combined report and reads config safely

- Every `IStartupCheck` is created from its registration inside its own isolation, in serve and provision mode, then run
  inside another (BC-10). A constructor or factory that throws adds one line naming the check (its implementation type;
  for a factory registration the type that declares the factory, compiler closures skipped) and every other check still
  runs. Checks are created once per start, not resolved as container singletons, and never disposed.
- A check that needs the config or a declared catalog that is unavailable gets one "did not run" line; the cause is already
  in the report and is not repeated.
- New `GameConfigStatus` (`GameServer.Platform`): `State` (`NotLoaded` / `Loaded` / `Failed`), `Config` (the instance
  handlers get), `DeclarationsPassed` (no required-file, rule or catalog line in this start), `TryGetConfig`. It never
  throws and is published once by the config phase, before the checks run. This is the supported way for a check to read
  config.
- Provision mode never loads game config (BC-9): `IGameConfig` and declared catalogs cannot be resolved there, the status
  stays `NotLoaded`, and a check that needs them is skipped with a warning on stderr; provisioning goes on.

```csharp
internal sealed class GameRulesCheck(GameConfigStatus status) : IStartupCheck
{
    public void Check(StartupCheckResult result)
    {
        if (!status.TryGetConfig(out var config) || !status.DeclarationsPassed)
        {
            return; // NotLoaded (provision) or Failed: the report already has the cause.
        }

        // read config or declared catalogs here
    }
}
```

Real lines (slice 015 tests, asserted literally; SC-003 = a missing `battle.json`, `GameServer:Gate:CacheSeconds=99` and
four checks):

```text
[config] file '…/config/base/battle.json' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.
[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).
[NeedsConfigCheck] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).
[ThrowingCheck] the startup check could not be created: InvalidOperationException: boom. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).
status: Failed
plain ran
[CatalogCheck] the startup check did not run: catalog ChecksCatalog was not built (see the lines above). Fix: take GameConfigStatus and read the catalog only when DeclarationsPassed (FG-023).
warning: [NeedsConfigCheck] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).
```

| Id | Line seen by the operator | Fix |
| --- | --- | --- |
| BC-9 | `warning: [{check}] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).` (stderr of `--provision`; exit code unaffected) | Take `GameConfigStatus` and skip when it is `NotLoaded` |
| BC-10 | None for a stateless check. A check type also resolved elsewhere gets a different instance there; a disposable check is not disposed | Keep checks stateless and not disposable |

Consumer impact (FG-023 row): `GameConfigStartupTests.A_config_that_does_not_load_keeps_the_report_complete_and_the_game_check_adds_nothing`
keeps passing with its 2 lines (the zh-pvp `GameConfigCheck` resolves `IGameConfig` inside `Check` and catches; the same
shape is tested here, `StartupCheckHostTests.A_check_that_resolves_the_config_itself_and_catches_adds_nothing`). Its FG-023
comment can go once `GameConfigCheck` moves into declarations (checklist step 3). A host or test that passes an
`IGameConfig` instance (`TestHost` `config:`) bypasses the loader, so such a check still runs in provision mode there.

## FG-024 and FG-019 (version) — the server version hashes the client-visible set; a client config version

- The server config version (`config_version`, receipts, `ICommandContext.ConfigVersion`, metric `config_version_info`,
  readiness `configVersion`) now hashes the label, every effective file **and the `clientVisible` set** (FG-024). Its
  meaning is unchanged; its value changes once at the upgrade (BC-5).
- New client config version `{label}+{hash12}` over the label and the client-visible files only (FR-041). The two hash
  inputs are domain-separated, so the two values never coincide, even when every file is client-visible. A server-only
  edit changes the server version only; a client-visible edit, a change of the `clientVisible` set (base or env overlay)
  or a label change changes both; list order and duplicates change neither.
- `IGameConfig.ClientConfigVersion` (BC-6), `GameConfigVersions.Compute(label, files, clientVisible)`, `new
  GameConfig(label, files, clientVisible)`; the 4-argument `GameConfig` constructor and `FileGameConfigLoader.ComputeVersion`
  are `[Obsolete]`. `TestGameConfig` computes both versions exactly like the host.
- Five additive proto fields `client_config_version` (`ErrorEnvelope` 10, `CommandResponse` 9, `SnapshotResponse` 4,
  `GetConfigResponse` 5, identity `LoginResponse` 7) on every player response, middleware errors included. GM responses
  and `ReceiptView` are unchanged. New gauge `client_config_version_info{version}` and readiness data
  `clientConfigVersion`.
- `GetConfig` answers `not_modified` when the request holds **either** current version (ordinal), so SDKs before 0.7.0 keep
  their `not_modified`. Any other value (an older version, the v0.6.0-format value, garbage, empty) gets the full bundle.
- SDK (BC-4): `GameServerClient.ClientConfigVersion` (the client version, or `config_version` when the server sends none);
  `ConfigOutdated`, `ConfigVersionChanged` and the cache key follow it; `ServerConfigVersion` keeps the server version.
  `RefreshConfigAsync` stores the files under the response's client version.

| Id | Value / line | Fix |
| --- | --- | --- |
| BC-4 | No `ConfigVersionChanged` on server-only edits; the event argument and `Config.Version` hold the client version (`test-1+4c5423c2dad9`), `ServerConfigVersion` the server version (`test-1+9b2cd1009ff1`) | Compare the cache with `ClientConfigVersion`; read `ServerConfigVersion` only to correlate with receipts |
| BC-5 | SampleHost config: `sample-1+0a58d1cc006c` (v0.6.0 formula) → server `sample-1+8a37f29ee2dc`, client `sample-1+9b25b8b6bc0b`. tests/Shared config: server `test-1+9b2cd1009ff1`, client `test-1+4c5423c2dad9` | Update pinned values (dashboards, alerts, tests) |
| BC-6 | `error CS0535: 'X' does not implement interface member 'GameServer.Config.IGameConfig.ClientConfigVersion'`; `error CS0618: 'GameConfig.GameConfig(string, string, IReadOnlyDictionary<string, byte[]>, IReadOnlySet<string>)' is obsolete: '…'` (under `TreatWarningsAsErrors`) | Implement the member; `new GameConfig(label, files, clientVisible)` or `TestGameConfig` |

zh-pvp rows: `tests/ZhPvp.Tests/Support/TestGameConfig.cs` needs the one-line fix `return new GameConfig(label, files,
clientVisible);` (CS0618 otherwise). In `ConfigDeliveryTests.cs`, `Making_a_file_client_visible_keeps_the_version` (marker
line 136) fails by design: both versions change now (delete or invert it). `A_server_only_file_change_changes_the_version_the_client_sees`
(marker line 119) does **not** fail by itself, because it asserts the server version, which still changes: rewrite it to
assert that `client_config_version` is unchanged and `config_version` changed. `Every_response_type_carries_the_version_of_the_committed_config`
stays valid (optionally assert `client_config_version` too). The label rule of `docs/pcr/game.md` (lines 94–96, 114) can go.

Checked against the tests: an SDK of this slice against a server without the field (stub stripping it from login,
snapshot and `GetConfig`) falls back to `config_version` and behaves as before; this server answers `not_modified` for the
server version an older SDK holds (`ConfigVersionCarriageTests`, `ClientConfigVersionTests`). The rolling-deploy note and
the compatibility table above hold.

## FG-019 (persistent cache) — client config survives app restarts

- New `PersistentConfigCache : IConfigCache` (SDK, `Runtime/Core`, no UnityEngine dependency, opt-in): one container file
  `gameserver-config.bin` per folder (format v1: magic, format version, payload length, payload, SHA-256), atomic saves
  (temp file, `Flush(true)`, `File.Replace`/`File.Move`), a damaged or unknown-format file reads as empty with
  `LoadProblem`, a failed save keeps the new version in memory with `WriteProblem`; it never throws for file problems.
  Config file names stay data inside the container (never paths). Not thread-safe (main thread), not encrypted.
- Unity: `UnityConfigCache.Create(environmentName)` = `Application.persistentDataPath/gameserver-config/{environmentName}`,
  one folder per server environment.
- With it, a second app start has the config before login; while the client config version is unchanged (also across
  server-only edits) nothing is downloaded.
- Budget (SC-006, `GAMESERVER_PERF=1`): 1 MiB save p95 11–15 ms, load p95 5–9 ms on the C: drive (`%TEMP%`, where
  `persistentDataPath` lives on Windows). On the repository's D: drive a save took ~70 ms p95 (T009), dominated by
  `Flush(true)`; the flush is kept on purpose (a crash must leave the old or the new version, never a short file).

Consumer setup (first consumer, `Assets/MadProject/Scripts/Online/OnlineSession.cs:83`):

```csharp
var client = new GameServerClient(options, new UnityWebRequestTransport(), config: UnityConfigCache.Create(environmentName));
```

Client markers removable: `ServerConfigInjector.cs:39` and `OnlineSession.cs:333` (and the remark at `:329`).
`FetchConfigAsync` (`OnlineSession.cs:335-347`) already applies the cached files when `RefreshConfigAsync` returns false.

## FG-019 (delivery) — compressed `GetConfig`, the bundle checked at startup

- `GetConfig` is the only compressed route. Brotli (quality 4) or gzip (level 6) per request, only when the request's
  `Accept-Encoding` accepts it (q-values honoured, `q=0` excludes, `*` covers a candidate not listed, a tie goes to `br`),
  the response carries files and is at least **1024 bytes**, and the result is smaller. `not_modified` and small bodies are
  never compressed. `Vary: Accept-Encoding` on every `GetConfig` response while compression is on; `Content-Length` = bytes on
  the wire; the decoded body is byte-identical to the uncompressed one. No ASP.NET response-compression middleware: login,
  session, GM, health and error responses never carry `Content-Encoding` (BREACH). Request bodies are never decompressed.
- Rollback switch `GameServer:ConfigCompression=false` (default `true`): today's bytes and headers, no `Vary`.
- The bundle is serialized once at startup (files sorted by name + the server version); each full response adds only the
  per-request tail (server time, client version), byte-identical to serializing the whole message.
- `GameServer:Limits:ConfigBundleBytes` keeps limiting the **uncompressed** response. When the player API is mapped, startup
  computes the largest response the config can produce and stops when it is over the limit (BC-7; equal to the limit starts):
  `[config] the client-visible bundle needs a GetConfig response of up to {size} bytes ({files} files), over GameServer:Limits:ConfigBundleBytes = {limit}. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).`
  The request-time 413 stays.
- Startup log: one Information line after `Starting …`:
  `Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount} client-visible, bundle {BundleBytes} B), {RuleCount} rules, {CatalogCount} catalogs.`
  (`bundle not served` when the player API is not mapped). The SampleHost local flavor logs
  `Config sample-1: version sample-1+8a37f29ee2dc, client version sample-1+9b25b8b6bc0b, 5 files (4 client-visible, bundle 1747 B), 4 rules, 4 catalogs.`
  for its committed config.
- **Levels (V6, measured on the real path, T050):** the T006 rule picked gzip 9 / Brotli 6, whose p95 at 1 MiB is
  18.2–18.7 ms (gzip) and 17.8–17.9 ms (Brotli), within 10 % of the 20 ms budget (SC-005). The lower levels ship: gzip 6 p95
  9.2 ms, ratio 14.0 %; Brotli 4 p95 7.5 ms, 14.2 %. Gzip ratios at level 6: sample bundle (1 743 B) 28.3 %, first-consumer-shaped
  bundle (27 643 B) 15.7 %, 1 MiB 14.0 % (all ≤ 30 %). Config phase (SC-007): sample config median 1.8 ms (budget 20 ms), 1 MiB
  `resources.json` 86.8 ms (budget 300 ms).
- **Unity (V8):** the Editor and the Windows player send `Accept-Encoding: deflate, gzip` and decode gzip before
  `downloadHandler.data` (T008); KestrelDemo E2E through a logging forwarder got `Content-Encoding: gzip` (494 B for the
  sample bundle) and parsed it (T051). Brotli is neither sent nor decoded by UnityWebRequest, so Unity never gets it. Android and
  iOS: pending (T058). **No client change is needed**; the SDK has no decoder of its own. A platform that advertised gzip without
  decoding would need `GameServer:ConfigCompression=false` for the deployments serving it.
- `HttpClient` without `AutomaticDecompression` sends no `Accept-Encoding` and gets the plain body.

| Id | Line seen by the operator | Fix |
| --- | --- | --- |
| BC-7 | `[config] the client-visible bundle needs a GetConfig response of up to 1747 bytes (4 files), over GameServer:Limits:ConfigBundleBytes = 1746. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).` (SampleHost, local flavor, `GameServer__Limits__ConfigBundleBytes=1746`, run 2026-11-04) | Raise the limit or make files server-only |

## Framework repository (samples, fixture, template, docs)

- KestrelDemo (`samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`) uses the persistent cache
  (`config: UnityConfigCache.Create("local")`) and logs `config from cache {version}` / `config downloaded {version}`.
- Consumer fixture: `notes.json` is declared with the rule `fixture.notes` (`maxLength` integer ≥ 1) instead of
  `RequiredConfigFiles`; `FixtureConfigTests` shows the kit call (`ConfigDeclarationCheck.Check`).
- Module template: `ConfigFileDeclaration.Optional("template.json", TemplateConfigRules.Points)` (rule `template.points`) and a
  commented `ConfigCatalogs` example.
- New architecture test `ConfigCatalogArchitectureTests`: no framework, sample or template module registers a declared catalog
  in `ConfigureServices`.
- Docs: consumer guide §12 "Game config" (and §5, §10, §11); `docs/guides/module-authoring.md` (config declarations, kit call);
  `templates/module/README.md`; PCR platform §21 (new) and §1, §9, §13, §17; PCR economy, iap, sample-game (file rules and their
  lines, declared catalogs, BC-1); PCR unity-sdk (persistent cache, `ClientConfigVersion`, compression per platform, new package
  files); HTML pages `01-gioi-thieu`, `02-them-command`, `03-deploy`, `index` (stamped "framework v0.7.0 (chưa phát hành)",
  branch `015-config`, 2026-11-04).

## Owner confirmations

| # | Decision | Status |
| --- | --- | --- |
| 1 | Additive `client_config_version` (`config_version` unchanged), with the CFG-03/SDK-02 cache-key reading ("by the version of the client-visible part") | Applied as architect decision, owner confirmation pending |
| 2 | D3 strictness: duplicate property names fail startup (BC-2), `maxQuantity ≥ 1`, integer `kind` refused | Applied as architect decision, owner confirmation pending |
| 3 | BC-9: provision mode no longer loads game config for a startup check that asks for it (warning instead) | Applied as architect decision, owner confirmation pending |

## Manual checks

| Task | Check | Status |
| --- | --- | --- |
| T008 | V8 UnityWebRequest `Accept-Encoding` and decoding on Editor (Windows) and Windows player | done 2026-11-04 (agent-run, Unity 6000.3.22f1): both send `Accept-Encoding: deflate, gzip` by default and decode gzip; `br` is neither sent nor decoded; no SDK code needed |
| T051 | KestrelDemo E2E with compression on (Editor) | done 2026-11-04 (agent-run, Unity 6000.3.22f1): `-CompileOnly` 0 `error CS`/`warning CS`; `-E2E` through a logging forwarder on 5092: request `Accept-Encoding: deflate, gzip`, `/api/v1/config/get` answered `Content-Encoding: gzip`, `Vary: Accept-Encoding`, 494 B; bundle parsed (`config downloaded sample-1+9b25b8b6bc0b`), exit 0 |
| T057 | KestrelDemo persistent cache in the Editor (V10 Mono) | done 2026-11-04 (agent-run): run 1 `config downloaded sample-1+9b25b8b6bc0b`, run 2 `config from cache sample-1+9b25b8b6bc0b` with no `GetConfig` request, run 3 after a client-visible `shop.json` edit `config downloaded sample-1+523717009cea` (the `File.Replace` path on Mono); exit 0 each; the cache folder holds only `gameserver-config.bin` |
| T058 | Android device run and iOS build/device (V8, V9, V10, V11) — owner | **pending (owner)**. APK built by the agent 2026-11-04 (`samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk`, IL2CPP arm64, stripping High, 54 193 600 B). Steps: (1) `tools/local/run-local.ps1 -Port 5080 -Lan`; (2) optional logging forwarder (scratch, see tasks.md T058 Note) to see `Accept-Encoding`/`Content-Encoding`; (3) set `serverUrl` of the `SampleGameClient` object to `http://<PC LAN IP>:5080/` (rebuild the APK) or use a build with that URL; install with `adb install -r Build/KestrelDemo.apk`; start the app three times: first start → `config downloaded {v}`, second start → `config from cache {v}`, then restart the host with `GameServer__ConfigPath` pointing at a copy of `samples/SampleHost/config` with `shop.json` edited and start the app → `config downloaded {w}`; record `Accept-Encoding`, compressed or not, parse OK, no crash (SHA-256 and `File.Replace` under IL2CPP). iOS: same three starts from a macOS build. A platform that advertises gzip without decoding → `GameServer:ConfigCompression=false` for it. The tag waits for this run (FR-091) |
| T066 | Consumer-guide walkthrough by a fresh reviewer agent | pending |
| T067 | First-consumer temp clone (zh-pvp-server) — owner may defer to the game session | pending |

## Known before the tag

- (final list by T065)
- V6 (T006, T050): gzip 9 / Brotli 6 measured 17.8–18.7 ms p95 at 1 MiB on the real path, within 10 % of the 20 ms budget;
  **gzip 6 / Brotli 4 ship** (p95 9.2 / 7.5 ms, gzip ratio ≤ 28.3 %). Android/iOS decoding is still T058.
- V11 (T009, T045): SC-006 (1 MiB load and save ≤ 50 ms) is met on the C: drive (save p95 11–15 ms) but not on D: (≈ 70 ms
  save p95, `Flush(true)`); `Flush(true)` is kept (durability over speed). Device numbers stay with T058.
- Fixed on the way (T035): a command whose request timeout fired after the commit wrote its `CommandResponse` body with the
  cancelled token before the 503 timeout envelope (two messages in one body under TestServer); `ProtobufBinding.WriteAsync`
  now writes nothing once the request token is cancelled.
- Provision mode treats every config-related unavailability (config or declared catalog) as the BC-9 warning, never an
  error (T029, T031).
