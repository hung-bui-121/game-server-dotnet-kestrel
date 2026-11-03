# Data model — 015 config

Entities, state, formats and report lines of [spec.md](spec.md). Decisions are in [research.md](research.md). C#
signatures are in [contracts/public-api.md](contracts/public-api.md). No Couchbase collection, document, index or receipt
shape changes in this slice.

## 1. Effective config and versions

### 1.1 Manifest (unchanged schema, stricter parse)

`config/base/manifest.json` and optional `config/env/<DeploymentEnvironment>/manifest.json`:
`{ "label": string, "files": string[], "clientVisible": string[] }`. Unknown members are ignored, as today. A repeated
property name at any depth is a fault (BC-2).

### 1.2 `GameConfig` (Platform, immutable, one per host)

| Member | Source |
| --- | --- |
| `Label` | env label when not blank, else base label |
| files (`name → bytes`) | base files, replaced by name by env files |
| `ClientVisibleFiles` | base ∪ env `clientVisible`, as a set |
| `ConfigVersion` (server) | §1.3 |
| `ClientConfigVersion` (new) | §1.3 |
| `OverlaidFiles` (internal) | names whose effective bytes came from `config/env/<env>` (report lines name the overlay) |

### 1.3 Version inputs (R2)

```text
server = label + "+" + hex12(SHA-256(
           "gameserver.config.server/1\n" +
           "label\n" + label + "\n" +
           for name in sort_ordinal(files):        "file\n" + name + "\n" + len(bytes) + "\n" + bytes
           "visible\n" +
           for name in sort_ordinal(clientVisible): name + "\n"))

client = label + "+" + hex12(SHA-256(
           "gameserver.config.client/1\n" +
           "label\n" + label + "\n" +
           for name in sort_ordinal(clientVisible): "file\n" + name + "\n" + len(bytes) + "\n" + bytes))
```

Strings are UTF-8, `len` is decimal ASCII and `hex12` is the first 12 lower-case hex digits.

### 1.4 What changes which version (FR-045, SC-004 matrix)

| Change | Server version | Client version |
| --- | --- | --- |
| Server-only file bytes | changes | **same** |
| Client-visible file bytes | changes | changes |
| File added to `clientVisible` (base or env overlay) | changes | changes |
| File removed from `clientVisible` | changes | changes |
| Label | changes | changes |
| Order or duplicates of manifest lists | same | same |
| Same bytes on another machine or OS | same | same |
| All files visible (separation) | ≠ client version | ≠ server version |
| Upgrade from v0.6.0 (same files) | changes once (BC-5) | new value |

### 1.5 Where each version goes

| Consumer | Value |
| --- | --- |
| Receipts, `ReceiptView.config_version`, `ICommandContext.ConfigVersion`, GM command responses | server (stored or instance) |
| `config_version` of `LoginResponse`, `SnapshotResponse`, `CommandResponse` (OK, rejection, replay), `ErrorEnvelope`, `GetConfigResponse` | server (unchanged meaning, R1) |
| `client_config_version` of the same five messages (new) | client |
| `GetConfig` `not_modified` | request `config_version` ∈ {client, server} (ordinal) |
| Metric `config_version_info{version}` | server (unchanged) |
| Metric `client_config_version_info{version}` (new) | client |
| Readiness data | `configVersion` (server), `clientConfigVersion` (new) |
| Startup config line (§5.6) | both |
| SDK cache key, `ConfigOutdated`, `ConfigVersionChanged` | client when present, else server (R14) |

## 2. Declarations (Core, immutable, owned by the declaring module)

### 2.1 Per module

| Descriptor member | Meaning |
| --- | --- |
| `RequiredConfigFiles` (existing) | required, no rules |
| `ConfigFiles` (new) | `ConfigFileDeclaration { Name, IsOptional, Rules[] }` |
| `ConfigCatalogs` (new) | `ConfigCatalogDeclaration { CatalogType, Files[], build }` |

**Effective required set of a module** = `RequiredConfigFiles` ∪ {d.Name | d ∈ ConfigFiles, !d.IsOptional} ∪
{f ∈ catalog.Files | the module does not declare f optional}. A missing file in that set gives the existing registry line
once per (module, file).

### 2.2 Framework, sample and fixture declarations (FR-015, FR-016, FR-070)

| Module | File | Rule (name) | Checks (each fault = one line) |
| --- | --- | --- | --- |
| `economy` | `resources.json` (required) | `economy.resources` | root object; `resources` array; per row (1-based): object; `id` non-empty string, unique (ordinal, line on the **later** row naming the first); `kind` string ∈ {currency, item} case-insensitive; `maxQuantity` integer ≥ 1; `displayName` string. Unknown properties ignored |
| `iap` | `iap.json` (required) | `iap.products` | root object; `products` object; per key: `{store}:{productId}`, store ∈ {google, apple}, productId 1..128 chars; value object; `grants` non-empty array; per grant: `resource` string defined in `resources.json` (well-formed rows only), `quantity` integer 1..that resource's `maxQuantity` |
| `sample.game` | `battle.json` (required) | `sample.battle` | `maxStage` integer ≥ 1; `battleHours` absent or integer 1..168; `stages` array; per row: `stage` integer 1..maxStage, unique; `xp` integer ≥ 0; `rewards`, `drops` arrays of `{resource ∈ resources.json, quantity ≥ 1}`; every stage 1..maxStage has a row |
| `sample.game` | `shop.json` (required) | `sample.shop` | `items` object; per key non-empty; `price { resource ∈ resources.json, amount integer ≥ 1 }`; `grant { resource ∈ resources.json, quantity integer 1..maxQuantity }` |
| fixture | `notes.json` (required) | `fixture.notes` | `maxLength` integer ≥ 1 |

Duplicate product or item keys inside one JSON object are loader faults (BC-2, FR-002), not rule faults: the rule never
sees two.

| Module | Catalog | Files | Build |
| --- | --- | --- | --- |
| `economy` | `ResourceCatalog` | `resources.json` | `ResourceCatalog.Load(c.Config)` |
| `iap` | `IapCatalog` | `iap.json` | `IapCatalog.Load(c.Config)` |
| `sample.game` | `BattleCatalog` | `battle.json` | `BattleCatalog.Load(c.Config)` |
| `sample.game` | `ShopCatalog` | `shop.json` | `ShopCatalog.Load(c.Config)` |

## 3. Startup state

### 3.1 `GameConfigLoad` (internal, Platform)

```text
Mode: Serve (default) | NotLoaded (set by ProvisioningRunner first)
State: NotRun → Loaded(GameConfig) | Failed(violations)     (one load per host, thread-safe)
GetOrThrow(): NotLoaded mode → throw marked "provision" (no disk access)
              Loaded → config;  Failed → new GameServerStartupException(violations), marked "game config"
```

### 3.2 `GameConfigStatus` (public, one published snapshot)

```text
NotLoaded ──phase: IGameConfig resolved──► Loaded(config, declarationsPassed)
    │
    └──────phase: resolution threw─────────► Failed
(provision mode: the phase never runs → stays NotLoaded)
```

`declarationsPassed` = no required-file line, no rule error, no catalog line in this start. Warnings do not count.

### 3.3 Catalog bindings and store

| Binding of type T (decided after `Build()`, last non-keyed descriptor of T) | Phase action | What DI returns for T |
| --- | --- | --- |
| *Declared*: the platform's descriptor | build once if files present and rule-clean | the built instance; failed → marked exception; phase never ran → `InvalidOperationException` (R7) |
| *Provided*: a host or test descriptor (before or after the module) | resolve T once in `try` | the host's registration (unchanged DI behaviour) |
| Second declaration of T by another module | line (FR-023); ignored | the first module's |

### 3.4 Marker on unavailability exceptions

`Exception.Data["GameServer.Unavailable"]` = `"game config"` | `"catalog {TypeName}"` | `"provision"`. It is set only by
`GameConfigLoad.GetOrThrow` and `ConfigCatalogStore.Get`, and read by `CheckAll` and `ValidateServices` (anywhere in the
`InnerException` chain).

## 4. Config bundle and delivery

### 4.1 `ConfigBundle` (internal, Platform.Web, built once when the player API is mapped and the config loaded)

```text
Prefix   = GetConfigResponse{ config_version = server (1), files = clientVisible sorted ordinal (3) }.ToByteArray()
Full(t)  = Prefix ++ GetConfigResponse{ server_time_utc_ms = t (4), client_config_version = client (5) }.ToByteArray()
           (= GetConfigResponse{1,3,4,5}.ToByteArray(), V5)
MaxResponseBytes = Prefix.Length + |tail with t = long.MinValue|     (startup check vs Limits.ConfigBundleBytes)
NotModified(t) = GetConfigResponse{ 1 = server, 2 = true, 4 = t, 5 = client }   (per request)
```

### 4.2 Compression decision (per `GetConfig` request, R12)

| Condition | Result |
| --- | --- |
| `GameServer:ConfigCompression = false` | uncompressed, no `Vary` (today's bytes and headers) |
| `not_modified`, or body < 1024 B | uncompressed + `Vary: Accept-Encoding` |
| no `Accept-Encoding`, `identity`, every candidate at `q=0`, unparseable | uncompressed + `Vary` |
| `br` acceptable (q > 0, explicit or by `*`) and q(br) ≥ q(gzip) | Brotli + `Content-Encoding: br` + `Vary` |
| else `gzip` acceptable | gzip + `Content-Encoding: gzip` + `Vary` |
| compressed length ≥ uncompressed | uncompressed + `Vary` |

Levels: Brotli quality `BrQuality`, zlib level `GzipLevel`, constants set from V6 (expected 4 and 6) and recorded in PCR
platform §1.

### 4.3 Options

| Key | Type / default | Validation |
| --- | --- | --- |
| `GameServer:ConfigCompression` | bool, `true` | — (rollback switch) |
| `GameServer:Limits:ConfigBundleBytes` (existing) | int, 1 MiB | > 0 (unchanged); now also checked against `MaxResponseBytes` at startup |

## 5. Startup report lines

One line per fault, every line names the fix. The full catalogue with examples is in
[startup-report.md](contracts/startup-report.md). Summary:

### 5.1 Loader (`[config]`, existing texts kept)

Missing base manifest · invalid manifest JSON · empty label · file name not plain · listed file missing · `.json` parse
error · `clientVisible` not in `files` · **new:** duplicate property (`… has property '{name}' twice at {path} (line
{l}, byte {b}). Fix: keep one property (CFG-01).`).

### 5.2 Required file (existing registry text, extended set §2.1)

`[module {module}] required config file '{file}' is missing. Fix: add it to config/base/manifest.json 'files' and to
config/base (CFG-01).`

### 5.3 Rules and catalogs

```text
location  := "" | " row {n}" | " key '{key}'" | " at {jsonPath}"
fileRef   := "{file}" [ " (config/env/{env})" when overlaid ]
rule      := "[module {module}] config {fileRef}{location}: {problem}. Fix: {fix}"
rule-warn := same text, logged as a startup warning
rule-throw:= "[module {module}] config {fileRef}: rule '{rule}' threw {ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018)."
catalog   := "[module {module}] catalog {Type}[ ({file}{location})]: {problem}. Fix: {fix}"
cat-throw := "[module {module}] catalog {Type}: the build threw {ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018)."
cat-null  := "[module {module}] catalog {Type}: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018)."
cat-dup   := "[catalog {Type}] is declared by {moduleA} and {moduleB}. Fix: declare it in one module; the other takes it from DI (FG-018)."
cat-host  := "[module {module}] catalog {Type}: the instance registered by the host could not be created: {ExceptionType}: {message}. Fix: correct the registration or remove it to use the declared build (FG-018)."
```

`{problem}` and `{fix}` come from the rule. The formatter strips one trailing `.` from each, so every line ends exactly
once.

### 5.4 Startup checks (R9)

```text
created   := "[{check}] the startup check could not be created: {ExceptionType}: {message}. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023)."
not-run   := "[{check}] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023)."
not-run-c := "[{check}] the startup check did not run: catalog {Type} was not built (see the lines above). Fix: take GameConfigStatus and read the catalog only when DeclarationsPassed (FG-023)."
provision := (warning) "[{check}] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023)."
failed    := "[{check}] the startup check failed: {ExceptionType}: {message}"      (existing text, unchanged)
```

### 5.5 Bundle size (FR-053)

`[config] the client-visible bundle needs a GetConfig response of up to {size} bytes ({files} files), over
GameServer:Limits:ConfigBundleBytes = {limit}. Fix: raise the limit or remove files from 'clientVisible' (CFG-03,
API-06).`

### 5.6 Startup config line (Information, category `GameServer.Startup`, after the 013 startup line)

`Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount}
client-visible, bundle {BundleBytes} B), {RuleCount} rules, {CatalogCount} catalogs.` `BundleBytes` is absent ("bundle
not served") when the player API is not mapped.

## 6. SDK persistent cache (format in [sdk-cache.md](contracts/sdk-cache.md))

| State | `Version` | `Files` | `LoadProblem` | `WriteProblem` |
| --- | --- | --- | --- | --- |
| No file | null | empty | null | null |
| Loaded | stored version | stored files | null | null |
| Unusable file (any reason) | null | empty | reason text | null |
| After `Store` success | new | new | unchanged | null |
| After `Store` write failure | new (memory) | new (memory) | unchanged | reason text |

One container per folder. One version is kept.
