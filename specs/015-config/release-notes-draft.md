# Release notes draft — v0.7.0 (slice 015 config)

Framework `0.7.0` (unreleased). Input of the `v0.7.0` annotated tag message (FR-090), merged with the notes of slices 013,
014 and 016 at the end of the round (one tag). Started in T002, extended by T028, T033, T040, T045, T052 and T064; final
pass in T065 (2026-11-04, framework `abfc32d`, after the first-consumer check T067). **The tag waits for** the device run
T058 (owner) and the reviewer walkthrough T066 (see "Manual checks").

**Fixed:** FG-018 (config content validated at startup, every catalog built before traffic), FG-023 (a startup check never
loses the combined report and reads config safely), FG-024 (the server config version hashes the client-visible set),
FG-019 (a client config version, compressed `GetConfig`, persistent SDK cache). **Breaking:** BC-1 to BC-10 (table below).
**Obsolete:** the 4-argument `GameConfig` constructor and `FileGameConfigLoader.ComputeVersion(label, files)` (BC-6).

**Unchanged:** no route, persisted document shape or GM API response shape changes; five additive proto fields
(`client_config_version` on `ErrorEnvelope`, `CommandResponse`, `SnapshotResponse`, `GetConfigResponse` and identity
`LoginResponse`); no existing field changes meaning (API-03); the Unity package stays `0.7.0`.

## Breaking changes

"How it surfaces" quotes the real lines (asserted literally by the slice 015 tests, or measured where noted).

| Id | Change | Who is affected | How it surfaces | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | Config content is checked at startup: framework rules for `resources.json` and `iap.json` (IAP grants must name a defined resource), sample rules, and catalogs built before traffic (FR-013..FR-016, FR-021) | Every deployment whose config was accepted only because nothing read the bad row before; test configs given to `TestHost`, `LocalFlavorFactory` and `TestGameConfig`-based hosts | Startup exit 1, one line per fault: `[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found "999". Fix: set the largest quantity a player may hold (ECO-07).` | Fix the file as the line says |
| BC-2 | A JSON object with a repeated property name in a config file or manifest is a loader fault (FR-002) | Configs that relied on "last one wins" (usually an unnoticed copy-paste) | Startup exit 1: `[config] '…/config/base/iap.json' has property 'google:gems_100' twice at $.products (line 4, byte 5). Fix: keep one property; the last one used to win silently (CFG-01).` | Keep one property |
| BC-3 | `FileGameConfigLoader.Load` reports every fault in one `GameServerStartupException` instead of throwing at the first one (FR-001) | Code or tests that call the loader and read the first violation only | Several `[config] …` lines in one exception (two missing files + one invalid JSON file = 3 lines); existing line texts unchanged | Read every violation |
| BC-4 | SDK semantics (PCR unity-sdk): five player messages gain `client_config_version` (additive, FR-043; `config_version` keeps its meaning). The SDK keys its cache, `ConfigOutdated` and `ConfigVersionChanged` on the client version when the server sends it (FR-046) | Game code that reads the `ConfigVersionChanged` argument or compares `Config.Version` with `ServerConfigVersion`; every client that cached a bundle reloads it once after the upgrade | No `ConfigVersionChanged` on server-only edits; the event argument and `Config.Version` hold the client version (`test-1+4c5423c2dad9`), `ServerConfigVersion` the server version (`test-1+9b2cd1009ff1`); one bundle download per client after deploy | Compare the cache with `ClientConfigVersion`; read `ServerConfigVersion` only to correlate with receipts |
| BC-5 | The server config version also hashes the `clientVisible` set (FR-040), so its value changes once at the upgrade | Dashboards or alerts keyed on a fixed `config_version_info{version}` value; receipts after the deploy carry the new value | New value in metric and receipts: SampleHost config `sample-1+0a58d1cc006c` (v0.6.0 formula) → server `sample-1+8a37f29ee2dc`, client `sample-1+9b25b8b6bc0b` | None (format unchanged); update pinned values |
| BC-6 | `IGameConfig` gains `ClientConfigVersion`; the `GameConfig` constructor computes both versions; `FileGameConfigLoader.ComputeVersion(label, files)` is obsolete (FR-042) | Custom `IGameConfig` implementations and direct `GameConfig`/`ComputeVersion` callers (game test support code) | `error CS0535: 'X' does not implement interface member 'GameServer.Config.IGameConfig.ClientConfigVersion'`; `error CS0618: 'GameConfig.GameConfig(string, string, IReadOnlyDictionary<string, byte[]>, IReadOnlySet<string>)' is obsolete: '…'` and `error CS0618: 'FileGameConfigLoader.ComputeVersion(string, IReadOnlyDictionary<string, byte[]>)' is obsolete: '…'` (under `TreatWarningsAsErrors`; both on one line in zh-pvp, T067) | Implement the member; use `TestGameConfig` or `new GameConfig(label, files, clientVisible)` |
| BC-7 | A client-visible bundle larger than `GameServer:Limits:ConfigBundleBytes` stops the host (FR-053); it was a 413 for every client | Deployments with an oversized bundle (already broken for clients) | Startup exit 1: `[config] the client-visible bundle needs a GetConfig response of up to 1747 bytes (4 files), over GameServer:Limits:ConfigBundleBytes = 1746. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).` (SampleHost, run 2026-11-04) | Raise the limit or make files server-only |
| BC-8 | `ResourceCatalog`, `IapCatalog`, `BattleCatalog` and `ShopCatalog` are registered from their declarations by the platform, no longer by the modules' `ConfigureServices` (FR-021, FR-024, FR-070) | Code that builds a service collection from a module's `ConfigureServices` alone (without `AddGameServer`) and resolves a catalog | `No service for type 'GameServer.Modules.Economy.ResourceCatalog' has been registered.` (services from `ConfigureServices` alone) or `ResourceCatalog is a declared config catalog: the platform builds it in UseGameServer. Fix: … (FG-018).` (host built without `UseGameServer`) | Register the catalog instance explicitly, or `ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)` |
| BC-9 | Provision mode never loads game config: `IGameConfig` cannot be resolved there, and a startup check that needs it is skipped with a **warning** (FR-032, D4) | Checks that read config in provision mode (today they silently load it) | stderr of `--provision`, exit code unaffected: `warning: [{check}] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).` | Take `GameConfigStatus` and skip when it is `NotLoaded` |
| BC-10 | Startup checks are created by the framework from their registration at each start, inside their own isolation (FR-030); they are no longer resolved as container singletons and are not disposed | A check type that is also resolved elsewhere (it gets a different instance there); disposable checks | None for a stateless check | Keep checks stateless and not disposable |

## zh-pvp-server migration checklist (in this order; verified in a temp clone, T067)

Checked 2026-11-04 in a temp clone of zh-pvp-server `7911f20` (framework `v0.6.0`) with `framework/` = this repository at
`abfc32d` (tag `v0.7.0` set in that clone only). Every file:line below matches the game files at `7911f20` and the client at
`research-and-development-games` `49df950ad`. The game is on `v0.6.0`, so the bump also carries the 013 and 014 breaking
changes; their checklists come first and their errors show first.

1. **Bump** the `framework/` submodule to `v0.7.0`, `GameServerFrameworkTag` in `Directory.Build.props`, and the tag named in
   `CLAUDE.md` and `README.md` (`RepositoryConsistencyTests` checks all three). Then the **013 and 014 steps**, in the order the
   build reports them (T067): 014 BC-3 (`CS0618` at `src/ZhPvp.Game.Adapters/GameModule.cs:28-30`: typed mappers with the four
   `Hidden` paths) and 014 BC-1 (the `account.init` declaration; without it the host exits 1); then 013 BC-4 (`CS2001` for the
   three `tests/Shared` links of `tests/ZhPvp.Tests/ZhPvp.Tests.csproj`: a `ProjectReference` to
   `framework/src/GameServer.TestKit` and `using GameServer.TestKit;` in six test files). The rest of the 013/014 checklists
   (RequestLog folder, run-local, `wiregen.json`, …) does not block the build or the suite.
2. **BC-6 / BC-1 / BC-2.** Build: `tests/ZhPvp.Tests/Support/TestGameConfig.cs:37` gives two `CS0618` (constructor and
   `ComputeVersion`): replace the line with `return new GameConfig(label, files, clientVisible);`. With the kit referenced,
   `TestGameConfig` is also ambiguous in `tests/ZhPvp.Tests/Game/ConfigDeliveryTests.cs` (`CS0104`, kit
   `GameServer.TestKit.TestGameConfig` vs the game's `ZhPvp.Tests.Support.TestGameConfig`): add
   `using TestGameConfig = ZhPvp.Tests.Support.TestGameConfig;` there (or rename the game helper). Then start each flavor once:
   the committed config gives **no** `[module …] config …` or `[config] … twice at …` line (T067, Local flavor):
   `Config zhpvp-1: version zhpvp-1+3d29d982d748, client version zhpvp-1+c2ba61ed994a, 10 files (7 client-visible, bundle 27860 B), 2 rules, 2 catalogs.`
3. **`GameConfigCheck` into declarations (FG-018, FG-023).** Delete `src/ZhPvp.Game.Adapters/GameConfigCheck.cs` (markers
   FG-018 line 11, FG-023 line 12), its registration at `src/ZhPvp.Game.Adapters/GameModule.cs:27` and the lazy
   `GameModule.cs:26` `TryAddSingleton(sp => GameCatalog.Load(…))`. `GameModuleDefinition.cs:23`
   `RequiredConfigFiles = GameCatalog.ConfigFiles` becomes `ConfigFiles` (one rule per table file, plus a game rule on
   `resources.json` for CASH/GOLD) and `ConfigCatalogs` (`GameCatalog` built through `TryLoad`; its files include
   `resources.json`, which `TryLoad` reads). The shape that passed the game suite in T067 (the rule reports the problems
   `TryLoad` finds in its own file; `ConfigTable.Error` strings are `[zhpvp.game] config {file}: [row {n}: ]{problem}[ Fix: {fix}]`):

   ```csharp
   public static IReadOnlyList<ConfigFileDeclaration> Files { get; } =
   [
       .. GameCatalog.ConfigFiles.Select(file => ConfigFileDeclaration.Required(file, new ConfigRule("zhpvp.tables", ReportTableProblems))),
       ConfigFileDeclaration.Required(ResourceCatalog.ConfigFile, new ConfigRule("zhpvp.resources", CheckWalletCurrencies)),
   ];

   public static IReadOnlyList<ConfigCatalogDeclaration> Catalogs { get; } =
   [
       ConfigCatalogDeclaration.Of(c => GameCatalog.TryLoad(c.Config).Catalog, [.. GameCatalog.ConfigFiles, ResourceCatalog.ConfigFile]),
   ];
   ```

   `ReportTableProblems` calls `context.Problems.Error(ConfigLocation.Row(n) or WholeFile, problem, fix)` for each `TryLoad`
   error of `context.File` (the module, file and row prefix removed: the framework adds them; `fix` is required).
   `CheckWalletCurrencies` returns when `ResourceCatalog.Load` throws (the economy rule reports that) and otherwise reports
   `resource 'GOLD' must exist with kind "currency"`. A game may instead pass the sink down into `ConfigTable`. Results:
   `GameConfigStartupTests.A_config_that_does_not_load_keeps_the_report_complete_and_the_game_check_adds_nothing` keeps
   passing unchanged (2 lines; drop its FG-023 comment). The other three facts still give exactly one line each, in the
   framework line format, so their expected texts change: `[module zhpvp.game] config ConfigGunChest.json row 2: duplicate GunId 'ar_0' (first at row 1). Fix: keep one row per gun.`,
   `[module zhpvp.game] config ConfigChestCard.json row 2: duplicate Id '1_Bronze' (first at row 1). Fix: keep one row per Id.`,
   `[module zhpvp.game] config ConfigChapter.json: 'table' must be "ConfigChapter". Fix: export the table again (02_design_p1.md §7.1).`
   (was `[zhpvp.game] config …: row 2: …`). The kit call `ConfigDeclarationCheck.Check(config, new IdentityModule(), new EconomyModule(), new GameModule())`
   passes for the committed config and gives `[module zhpvp.game] config resources.json: resource 'GOLD' must exist with kind "currency". Fix: add 'GOLD' with kind "currency".`
   for GOLD as an item. `GameCatalog.Load` is then used only by `tests/ZhPvp.Tests/Game/InitAccountHandlerTests.cs`.
4. **Version tests (FG-024, FG-019, BC-4).** `tests/ZhPvp.Tests/Game/ConfigDeliveryTests.cs`:
   `Making_a_file_client_visible_keeps_the_version` (marker line 136) fails by design (T067): invert it
   (`Making_a_file_client_visible_changes_both_versions`: `ConfigVersion` and `ClientConfigVersion` both differ) or delete it.
   `A_server_only_file_change_changes_the_version_the_client_sees` (marker line 119) still **passes** (the trap: it asserts the
   server version): rewrite it to assert `ClientConfigVersion` unchanged and `ConfigVersion` changed. Remove the label rule of
   `docs/pcr/game.md` lines 94–96 and update line 114. `Every_response_type_carries_the_version_of_the_committed_config` passes;
   asserting `ClientConfigVersion` on all seven responses too passes (T067).
5. **Client persistent cache (FG-019).** `Assets/MadProject/Scripts/Online/OnlineSession.cs:83` is
   `this.Client = new GameServerClient(options, transport);`: pass
   `config: UnityConfigCache.Create(cacheName)`, where `cacheName` is a plain folder name per server environment (`local`,
   `dev`, `beta`, `live`). `OnlineSettings` has only `BaseUrl`: add the name there; do not derive it from the URL (`:` is
   refused on Windows/Editor but not on Android/iOS). Drop the markers `ServerConfigInjector.cs:39` and `OnlineSession.cs:333`
   (and the remark at `:329`). `FetchConfigAsync` (`:335-347`) already applies the cached files at boot when `updated` is false
   (`appliedConfigVersion` starts null). It still sends one `GetConfig` per boot, answered `not_modified`; a
   `if (this.Client.ConfigOutdated)` gate as in KestrelDemo skips it. `OnConfigVersionChanged` (`OnlineSession.cs:179`) now
   receives the client version (BC-4); its log text says "Server config version". Compression needs no client change.

Then build Debug and Release (0 warnings) and run the game suite: in T067, 121 tests, 120 passed; the one failure was
`RepositoryConsistencyTests.Claude_md_and_readme_name_the_pinned_tag`, because the clone's `CLAUDE.md`/`README.md` still named
`v0.6.0` (step 1). SC-009 against the clone's host (scratch C# 9 client over `sdk/GameServer.Client`, `PersistentConfigCache`):
start 1 downloaded the 7 files under `zhpvp-1+c2ba61ed994a`; after a server-only `ConfigMission.json` edit (server version
`zhpvp-1+3d29d982d748` → `zhpvp-1+f9d52cfb7538`, client version unchanged) start 2 read the 7 files from the cache,
`ConfigOutdated` false, `RefreshConfigAsync` false (`not_modified`): **0 downloads**. The client snippet of step 5 compiles
with C# 9 against the package `Runtime` and the Unity 6000.3.22f1 `UnityEngine` modules (`UNITY_5_3_OR_NEWER`, warnings as errors).

Markers removable (SC-009): 4 on the server (FG-018 and FG-023 in `GameConfigCheck.cs`, FG-019 and FG-024 pinned in
`ConfigDeliveryTests.cs`) and 2 on the client (FG-019 in `ServerConfigInjector.cs` and `OnlineSession.cs`) = **6
markers**, plus the label rule of `docs/pcr/game.md`. The friction log entries FG-018, FG-019, FG-023 and FG-024 move to
`fixed-in-v0.7.0`.

### Removable vs stays

| FG | Removable after `v0.7.0` | Stays (game choice) |
| --- | --- | --- |
| FG-018 | `GameConfigCheck.cs` (marker `GameConfigCheck.cs:11`) and its registration (`GameModule.cs:27`); the lazy `GameModule.cs:26` factory (declared `GameCatalog` built with `TryLoad`); `RequiredConfigFiles` at `GameModuleDefinition.cs:23` (file declarations with rules); `GameCatalog.Load` once its test callers move to the kit | `ConfigTable.cs`, `ConfigTableChecks.cs`, `ClientEnums.cs`, `CatalogRows.cs` (game schema code, optionally reporting through the framework sink); `GameConfigStartupTests` facts 1–3 with the framework line texts |
| FG-023 | The marker at `GameConfigCheck.cs:12` with the file; the FG-023 comment of the 2-line test | The 2-line test as a plain regression test |
| FG-024 | `Making_a_file_client_visible_keeps_the_version` (marker `ConfigDeliveryTests.cs:136`; invert or delete); the label rule of `docs/pcr/game.md` lines 94–96 | — |
| FG-019 | The pinned `A_server_only_file_change_changes_the_version_the_client_sees` (marker `ConfigDeliveryTests.cs:119`; rewrite); client markers `ServerConfigInjector.cs:39`, `OnlineSession.cs:333` (+ remark `:329`); `docs/pcr/game.md` line 114 | `The_bundle_fits_the_client_visible_budget` (uncompressed bytes, still valid; 27 860 B in T067) |
| BC-4 / BC-6 | — | `Every_response_type_carries_the_version_of_the_committed_config` (valid once `TestGameConfig.Create` uses `new GameConfig(label, files, clientVisible)`; optionally assert `client_config_version`) |

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
`ConfigTable.cs`, `ConfigTableChecks.cs`, `ClientEnums.cs`, `CatalogRows.cs` stay (game schema code). Verified in T067
(checklist step 3): the game suite passes with the declared catalog and rules; the game's table lines then use the framework
format (`[module zhpvp.game] config ConfigGunChest.json row 2: …`), so three `GameConfigStartupTests` expected texts change.

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
comment can go once `GameConfigCheck` moves into declarations (checklist step 3; T067: still 2 lines after the move, and with
the declared `GameCatalog` not built no third line appears). A host or test that passes an
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
| BC-6 | `error CS0535: 'X' does not implement interface member 'GameServer.Config.IGameConfig.ClientConfigVersion'`; `error CS0618: 'GameConfig.GameConfig(string, string, IReadOnlyDictionary<string, byte[]>, IReadOnlySet<string>)' is obsolete: '…'` and `error CS0618: 'FileGameConfigLoader.ComputeVersion(string, IReadOnlyDictionary<string, byte[]>)' is obsolete: '…'` (under `TreatWarningsAsErrors`) | Implement the member; `new GameConfig(label, files, clientVisible)` or `TestGameConfig` |

zh-pvp rows (verified in T067): `tests/ZhPvp.Tests/Support/TestGameConfig.cs:37` needs the one-line fix `return new GameConfig(label, files,
clientVisible);` (two CS0618 otherwise; with the 013 kit, a `using` alias for the game's `TestGameConfig` in
`ConfigDeliveryTests.cs`, CS0104). In `ConfigDeliveryTests.cs`, `Making_a_file_client_visible_keeps_the_version` (marker
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

Consumer setup (first consumer, `Assets/MadProject/Scripts/Online/OnlineSession.cs:83`, today
`this.Client = new GameServerClient(options, transport);`):

```csharp
this.Client = new GameServerClient(options, transport, config: UnityConfigCache.Create(cacheName));
```

`cacheName` is a plain folder name per server environment kept in the client settings (`OnlineSettings` has only `BaseUrl`
today); `Create` refuses the platform's invalid file-name characters, which differ (`:` on Windows/Editor only), so it is not
derived from the URL. Client markers removable: `ServerConfigInjector.cs:39` and `OnlineSession.cs:333` (and the remark at
`:329`). `FetchConfigAsync` (`OnlineSession.cs:335-347`) already applies the cached files when `RefreshConfigAsync` returns
false. T067: the snippet compiles (C# 9, package `Runtime`, Unity 6000.3.22f1 `UnityEngine` modules), and a .NET client with
`PersistentConfigCache` downloaded 0 files on its second start after a server-only `ConfigMission.json` edit (SC-009).

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

All three are implemented as architect decisions and none blocks a task; each needs the owner's yes before the tag.

| # | Decision | Status | If the owner says no |
| --- | --- | --- | --- |
| 1 | Additive `client_config_version` (`config_version` unchanged), with the CFG-03/SDK-02 cache-key reading ("the client caches the bundle by the version of the client-visible part"); the owner may add `clientConfigVersion` to CFG-03 in the requirements | Applied as architect decision (spec D6, research R1), **owner confirmation pending** | The rejected draft default (redefine `config_version` on the player API) needs a new slice: API-03 forbids changing a published field's meaning |
| 2 | D3 strictness: duplicate property names fail startup (BC-2), `maxQuantity` a JSON integer ≥ 1, an integer `kind` refused (`kind` matched case-insensitively) | Applied as architect decision (research R4), **owner confirmation pending**; first consumer unaffected (T067: no BC-1/BC-2 line) | Relax the loader duplicate check or the `economy.resources` rule; lines and tests change |
| 3 | BC-9: provision mode never loads game config; a startup check that asks for it is skipped with a warning | Applied as architect decision (research R8), **owner confirmation pending** | Provision would load config again for such checks (config faults would then fail `--provision`) |

## Manual checks

| Task | Check | Status |
| --- | --- | --- |
| T008 | V8 UnityWebRequest `Accept-Encoding` and decoding on Editor (Windows) and Windows player | done 2026-11-04 (agent-run, Unity 6000.3.22f1): both send `Accept-Encoding: deflate, gzip` by default and decode gzip; `br` is neither sent nor decoded; no SDK code needed |
| T051 | KestrelDemo E2E with compression on (Editor) | done 2026-11-04 (agent-run, Unity 6000.3.22f1): `-CompileOnly` 0 `error CS`/`warning CS`; `-E2E` through a logging forwarder on 5092: request `Accept-Encoding: deflate, gzip`, `/api/v1/config/get` answered `Content-Encoding: gzip`, `Vary: Accept-Encoding`, 494 B; bundle parsed (`config downloaded sample-1+9b25b8b6bc0b`), exit 0 |
| T057 | KestrelDemo persistent cache in the Editor (V10 Mono) | done 2026-11-04 (agent-run): run 1 `config downloaded sample-1+9b25b8b6bc0b`, run 2 `config from cache sample-1+9b25b8b6bc0b` with no `GetConfig` request, run 3 after a client-visible `shop.json` edit `config downloaded sample-1+523717009cea` (the `File.Replace` path on Mono); exit 0 each; the cache folder holds only `gameserver-config.bin` |
| T058 | Android device run and iOS build/device (V8, V9, V10, V11) | **pending (owner); the tag waits for it (FR-091).** Steps below |
| T066 | Consumer-guide walkthrough by a fresh reviewer agent (FR-075, US6-2) | pending (not run yet; the tag waits for it) |
| T067 | First-consumer temp clone (zh-pvp-server), SC-009 | done 2026-11-04 (agent-run, temp clone of `7911f20`, framework `abfc32d`, deleted afterwards; `E:\ProjGits\*` untouched; host on 5097, never 5180): every step of the checklist above verified; game suite 120/121 (the failure is the game's own docs naming `v0.6.0`, bump step 1); SC-009: 0 client downloads after a server-only `ConfigMission.json` edit; doc fixes: this checklist, consumer guide §11/§12.2/§12.8, PCR unity-sdk, 013 BC-4 |

T058 steps (owner; Android on a device on the same network, iOS on macOS):

1. `tools/local/run-local.ps1 -Port 5080 -Lan` (SampleHost, local flavor, committed config).
2. Optional: a logging forwarder on 5092 → 5080 bound to all interfaces (the T051 scratch program or any logging proxy), to
   see `Accept-Encoding` and `Content-Encoding`.
3. KestrelDemo's serialized `serverUrl` is `http://localhost:5080/`: set it on the `SampleGameClient` object to
   `http://<PC LAN IP>:5080/` (or `:5092/`) and rebuild with
   `sdk/unity/ci/smoke-build.ps1 -Unity "D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe"` (IL2CPP arm64, stripping High).
   The APK the agent built on 2026-11-04 (`samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk`, 54 193 600 B) still has the
   localhost URL.
4. `adb install -r samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk`; start the app (run 1 → `config downloaded {v}`);
   close and start it again (run 2 → `config from cache {v}`, no `config/get` request); stop the host, restart it with
   `GameServer__ConfigPath` pointing at a **copy** of `samples/SampleHost/config` whose `shop.json` is edited (never the committed
   folder); start the app (run 3 → `config downloaded {w}`).
5. iOS: the same three starts from a macOS build (the iOS module is not installed on the agent's machine).
6. Record per platform: `Accept-Encoding` sent, response compressed or not, bundle parsed, the two cache lines, no crash
   (SHA-256 and `File.Replace` under IL2CPP). A platform that advertises gzip without decoding: set
   `GameServer:ConfigCompression=false` for the deployments serving it (V9 not needed while V8 holds).

## Known before the tag

- **Open before the tag:** T058 (owner device run), T066 (reviewer walkthrough), owner confirmations 1–3.
- V6 (T006, T050): gzip 9 / Brotli 6 measured 17.8–18.7 ms p95 at 1 MiB on the real path, within 10 % of the 20 ms budget;
  **gzip 6 / Brotli 4 ship** (p95 9.2 / 7.5 ms, gzip ratio ≤ 28.3 %). Android/iOS decoding is still T058.
- V11 (T009, T045): SC-006 (1 MiB load and save ≤ 50 ms) is met on the C: drive (save p95 11–15 ms) but not on D: (≈ 70 ms
  save p95, `Flush(true)`); `Flush(true)` is kept (durability over speed). Device numbers stay with T058.
- Fixed on the way (T035): a command whose request timeout fired after the commit wrote its `CommandResponse` body with the
  cancelled token before the 503 timeout envelope (two messages in one body under TestServer); `ProtobufBinding.WriteAsync`
  now writes nothing once the request token is cancelled.
- Provision mode treats every config-related unavailability (config or declared catalog) as the BC-9 warning, never an
  error (T029, T031).
- `UnityConfigCache.Create` checks the name with the running platform's `Path.GetInvalidFileNameChars()`, so the same name can
  be refused in the Windows Editor and accepted on Android/iOS (found in T067, reported for the review T070; the docs tell games
  to use plain names such as `local` or `live`).
