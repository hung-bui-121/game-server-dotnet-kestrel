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
| T051 | KestrelDemo E2E with compression on (Editor) | pending |
| T057 | KestrelDemo persistent cache in the Editor (V10 Mono) | pending |
| T058 | Android device run and iOS build/device (V8, V9, V10, V11) — owner | pending |
| T066 | Consumer-guide walkthrough by a fresh reviewer agent | pending |
| T067 | First-consumer temp clone (zh-pvp-server) — owner may defer to the game session | pending |

## Known before the tag

- (final list by T065)
- V6 (T006): the documented level rule picks gzip 9 / Brotli 6 with p95 ≈ 18.5 ms at 1 MiB (budget 20 ms); T048/T050
  decide the constants on the real path (fallback: lower level).
- V11 (T009): a 1 MiB cache save is ≈ 11 ms p95 on the C: profile drive but ≈ 70 ms p95 on D: (`Flush(true)`); T045 states
  the reference drive of SC-006.
