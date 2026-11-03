# Startup report lines — 015 config

Every line joins the one aggregated `GameServerStartupException` (FR-009 of 011) unless marked *warning* (logged once,
category `GameServer.Startup`, before the startup line). Texts in `code` are exact templates. `{…}` are values. Lines
named "existing" keep their current text (log matching, FR-001).

## 1. Order of a serve-mode start

1. Loader lines (`[config] …`), when the config does not load. **Nothing in 2b–4 runs then** (all or nothing, D2).
2. Registry lines (existing), including the required-file line (2a), then module rules (2b) and catalogs (2c).
3. Bundle size line (FR-053), when the player API is mapped.
4. Wire catalog, services, deployment, gate, request-log lines (unchanged).
5. Startup-check lines (creation, not-run, own lines).
6. Module options and endpoint lines (unchanged, 014).

Within 2b and 2c the order is: module registration order, then declaration order, then the order the rule reported.

## 2. Loader (`FileGameConfigLoader`, also in `TestGameConfig.FromDirectory`)

| Fault | Line |
| --- | --- |
| Missing base manifest (existing) | `[config] '{path}' not found. Fix: create config/base/manifest.json with label, files and clientVisible (CFG-01).` |
| Manifest not valid JSON (existing) | `[config] '{path}' is not valid JSON: {parser message}. Fix: correct the manifest (CFG-01).` |
| Empty label (existing) | `[config] manifest 'label' is empty. Fix: set a label in config/base/manifest.json (CFG-01).` |
| File name not plain (existing) | `[config] file name '{name}' is not allowed. Fix: use a plain relative file name.` |
| Listed file missing (existing) | `[config] file '{path}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.` |
| `.json` not valid (existing) | `[config] '{path}' is not valid JSON: {parser message}. Fix: correct the file (CFG-01).` |
| `clientVisible` not in `files` (existing) | `[config] clientVisible file '{name}' is not listed in 'files'. Fix: add it to manifest 'files'.` |
| **Duplicate property (new, BC-2)** | `[config] '{path}' has property '{name}' twice at {jsonPath} (line {line}, byte {byte}). Fix: keep one property; the last one used to win silently (CFG-01).` |

Example (SC-001 "two missing files and one invalid JSON file" = exactly these 3 lines):

```text
[config] file 'config/base/battle.json' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.
[config] file 'config/base/shop.json' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.
[config] 'config/base/iap.json' is not valid JSON: '}' is invalid after a property name. Expected a ':'. LineNumber: 3 | BytePositionInLine: 20.. Fix: correct the file (CFG-01).
```

## 3. Required files (existing text; extended set: `RequiredConfigFiles` ∪ required `ConfigFiles` ∪ catalog files)

`[module {module}] required config file '{file}' is missing. Fix: add it to config/base/manifest.json 'files' and to config/base (CFG-01).`

The line appears once per (module, file). No rule of that file runs, and no catalog reading it is built (no second line).

## 4. Module rules

Template: `[module {module}] config {file}[ (config/env/{env})]{location}: {problem}. Fix: {fix}`. Here `{location}` is
`""`, `" row {n}"`, `" key '{key}'"` or `" at {jsonPath}"`.

Framework rule lines (exact `{problem}` / `{fix}` texts; property names in quotes):

| Rule | Fault | Line |
| --- | --- | --- |
| `economy.resources` | no `resources` array | `[module economy] config resources.json: 'resources' must be an array, found {kind}. Fix: put the resource rows in a "resources" array (ECO-07).` |
| | row not an object | `[module economy] config resources.json row {n}: the row must be an object, found {kind}. Fix: write each resource as an object (ECO-07).` |
| | missing / null / wrong type | `[module economy] config resources.json row {n}: '{property}' is missing. Fix: …` / `… is null` / `… must be a {type}, found {kind}` |
| | duplicate id | `[module economy] config resources.json row {n}: id '{id}' is already defined in row {first}. Fix: give every resource a unique id (ECO-07).` |
| | unknown kind | `[module economy] config resources.json row {n}: 'kind' is '{value}'. Fix: use "currency" or "item" (ECO-07).` |
| | maxQuantity < 1 / not an integer | `[module economy] config resources.json row {n}: 'maxQuantity' must be an integer of at least 1, found {value}. Fix: set the largest quantity a player may hold (ECO-07).` |
| `iap.products` | no `products` object | `[module iap] config iap.json: 'products' must be an object, found {kind}. Fix: map "{store}:{productId}" keys to products (IAP-01).` |
| | key format | `[module iap] config iap.json key '{key}': the key must be "google:{productId}" or "apple:{productId}" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).` |
| | grants missing / empty | `[module iap] config iap.json key '{key}': 'grants' must be a non-empty array. Fix: list what the product grants (IAP-01).` |
| | unknown resource | `[module iap] config iap.json at $.products['{key}'].grants[{i}]: resource '{resource}' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01).` |
| | quantity out of range | `[module iap] config iap.json at $.products['{key}'].grants[{i}]: 'quantity' must be an integer from 1 to {maxQuantity} (maxQuantity of '{resource}'), found {value}. Fix: correct the grant (IAP-01).` |
| any | rule threw | `[module {module}] config {file}: rule '{rule}' threw {ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018).` |

The sample game and fixture rules use the same `ConfigJson` wording (data-model §2.2). A warning has the same text and
is logged, not added to the report.

Example (SC-001 "six faults across three files plus an unrelated violation" = exactly 7 lines; resources.json has a
duplicate id and a string `maxQuantity`, iap.json an unknown resource, a quantity 0 and a key without store, battle.json
an `xp` of type string, and `GameServer:Gate:CacheSeconds=99`):

```text
[module economy] config resources.json row 3: 'maxQuantity' must be an integer of at least 1, found "999". Fix: set the largest quantity a player may hold (ECO-07).
[module economy] config resources.json row 4: id 'gold' is already defined in row 1. Fix: give every resource a unique id (ECO-07).
[module iap] config iap.json key 'gems_100': the key must be "google:{productId}" or "apple:{productId}" with a product id of 1 to 128 characters. Fix: correct the key (IAP-01).
[module iap] config iap.json at $.products['google:gems_100'].grants[0]: 'quantity' must be an integer from 1 to 1000000 (maxQuantity of 'gem'), found 0. Fix: correct the grant (IAP-01).
[module iap] config iap.json at $.products['google:starter_pack'].grants[1]: resource 'shield' is not defined in resources.json. Fix: add it to resources.json or correct the grant (ECO-07, IAP-01).
[module sample.game] config battle.json row 2: 'xp' must be an integer of at least 0, found "70". Fix: give every stage an integer xp of at least 0.
[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).
```

(No catalog line: `ResourceCatalog`, `IapCatalog` and `BattleCatalog` are not built because their files have rule
errors. `ShopCatalog` is built.)

## 5. Catalogs

| Fault | Line |
| --- | --- |
| Build reported a problem | `[module {module}] catalog {Type}[ ({file}{location})]: {problem}. Fix: {fix}` |
| Build threw | `[module {module}] catalog {Type}: the build threw {ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018).` |
| Build returned null, no problem | `[module {module}] catalog {Type}: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018).` |
| Declared twice | `[catalog {Type}] is declared by {moduleA} and {moduleB}. Fix: declare it in one module; the other takes it from DI (FG-018).` |
| Host-registered instance fails | `[module {module}] catalog {Type}: the instance registered by the host could not be created: {ExceptionType}: {message}. Fix: correct the registration or remove it to use the declared build (FG-018).` |

## 6. Bundle size (FR-053, BC-7)

`[config] the client-visible bundle needs a GetConfig response of up to {size} bytes ({files} files), over GameServer:Limits:ConfigBundleBytes = {limit}. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).`

Runs only when the config loaded and `GameServer:Hosting:MapPlayerApi=true`. `{size}` is `MaxResponseBytes`. A size
equal to the limit passes.

## 7. Startup checks (FG-023)

| Case | Mode | Kind | Line |
| --- | --- | --- | --- |
| Creation threw | serve, provision | error | `[{check}] the startup check could not be created: {ExceptionType}: {message}. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).` |
| Needs `IGameConfig`, config failed (creation or `Check`) | serve | error | `[{check}] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).` |
| Needs a declared catalog that was not built | serve | error | `[{check}] the startup check did not run: catalog {Type} was not built (see the lines above). Fix: take GameConfigStatus and read the catalog only when DeclarationsPassed (FG-023).` |
| Needs `IGameConfig` | provision | **warning** | `[{check}] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).` |
| `Check` threw a `GameServerStartupException` (unmarked) | any | errors | its `Violations` (existing behaviour) |
| `Check` threw anything else | any | error | `[{check}] the startup check failed: {ExceptionType}: {message}` (existing) |

`{check}` = implementation type name. For a factory registration it is the declaring type of the factory, skipping
compiler-generated closure classes.

Example (SC-003: missing `battle.json` + `GameServer:Gate:CacheSeconds=99`, four checks `NeedsConfigCheck(IGameConfig)`,
`ThrowingCheck` whose constructor throws, `StatusCheck(GameConfigStatus)` that adds `status: Failed`, and
`PlainCheck` that adds `plain ran`):

```text
[config] file '…/config/base/battle.json' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.
[gate] GameServer:Gate:CacheSeconds must be 0..10 (PERF-08: a maintenance toggle applies within 10 s).
[NeedsConfigCheck] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).
[ThrowingCheck] the startup check could not be created: InvalidOperationException: boom. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).
status: Failed
plain ran
```

## 8. Information lines (not faults)

After the 013 startup line (`Starting {GameId} …`), once:

`Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount} client-visible, bundle {BundleBytes} B), {RuleCount} rules, {CatalogCount} catalogs.`
