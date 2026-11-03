# Research — 015 config

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent. Items marked **[VERIFY Vn]** are runtime or
platform facts the implementer confirms on the pinned versions (.NET SDK 10.0.403, ASP.NET Core 10.0.14,
`Google.Protobuf` 3.36.2, Unity 6000.3.22f1) **before** coding against them. Each has a fallback. The list is at the end.

## Code facts this research relies on (verified 2026-11-04 on `015-config`, base `c6b133f`)

- **Loader** (`src/GameServer.Platform/Config/FileGameConfigLoader.cs`): static `Load(root, env)` throws a
  `GameServerStartupException` at the first fault (lines 23, 57, 62, 91, 99, 105, 117). The manifest is read with
  `JsonSerializer.Deserialize<Manifest>(…, GameServerJson.Options)`, `.json` files are parsed with `JsonDocument.Parse` and
  discarded. The env overlay replaces files by name and unions `clientVisible` (line 52). `ComputeVersion(label, files)`
  hashes `name + "\n" + bytes` per file, ordinal order, and ignores `clientVisible` (FG-024). `GameConfig` (same file) is
  public, with the 4-argument constructor `(label, configVersion, files, clientVisible)`.
- **`IGameConfig`** (Core, `GameServer.Config`): `ConfigVersion`, `Label`, `FileNames`, `ClientVisibleFiles`, `HasFile`,
  `GetFileBytes`, `GetFileJson` (parses on each call), `GetFile<T>` (STJ on each call).
- **Registration** (`GameServerBuilder.cs:192-197`): `AddSingleton<IGameConfig>(sp => FileGameConfigLoader.Load(…))`. A
  factory that throws is not cached by MS DI, so every resolve runs the loader again. `TestHost.Compose`
  (`TestHost.cs:186-193`) replaces the registration with an instance unless the test sets `GameServer:ConfigPath`.
- **Startup sequence** (`GameServerApplicationExtensions.UseGameServer`, lines 31-125): resolve `IGameConfig` (catch →
  violations) → `registry.Validate(options, config)` (required files only when `config` is not null,
  `ModuleRegistry.cs:359-367`) → `WireCatalog.Validate` → `ValidateServices` (resolves `IOutboxRelay`, which builds every
  `IOutboxConsumer`) → deployment/secret/shutdown/DataProtection/gate/request-log checks → `RunStartupChecks` →
  `ValidateModuleOptions` → map → `errors.Count > 0` ⇒ endpoint validators over the endpoints mapped so far and throw;
  else hand-over to the pre-serving endpoint check (014 R8). Provision mode (`ProvisioningRunner.cs:26-38`) calls
  `registry.Validate(options, config: null)`, `WireCatalog.Validate`, secret sources and the same `CheckAll`.
- **`CheckAll`** (`GameServerApplicationExtensions.cs:199-219`) resolves every check with `GetServices<IStartupCheck>()`
  outside the per-check `try` (FG-023). `IStartupCheck` and `StartupCheckResult` are in `GameServer.Platform`
  (`src/GameServer.Platform/StartupChecks.cs`). `GameServerStartupException` is a **sealed** public class with
  `Violations`.
- **Lazy catalogs**: `EconomyModule.cs:50` and `IapModule.cs:31` `TryAddSingleton(sp => X.Load(sp.GetRequiredService<IGameConfig>()))`,
  same for `BattleCatalog`/`ShopCatalog` (`SampleGameModule.cs:30-31`). Handlers are created per command with
  `ActivatorUtilities.CreateInstance` (`CommandRunner.cs:296`). `ResourceCatalog` uses `ToDictionary` (duplicate id
  throws), STJ positional records (a missing `maxQuantity` reads as 0, a missing `id` as null), and
  `JsonStringEnumConverter(camelCase)`, which reads enum names case-insensitively and also accepts integers.
- **No framework code builds services from a module alone and resolves a catalog.** `RunnerHarness` (Platform tests)
  registers identity/counter/probe only; `WireConformance` (TestKit) resolves mappers only. The first consumer resolves
  `ResourceCatalog` only through `ResourceCatalog.Load(TestGameConfig.Create())` in a unit test.
- **Version carriage** (all from `IGameConfig.ConfigVersion` unless noted): `SnapshotResponse` (`CoreEndpoints.cs:43`),
  `GetConfigResponse` (`:64`, `not_modified` compares ordinally at `:66`), `LoginResponse` (`IdentityModule.cs:301`),
  `CommandResponse` (`CommandEndpointFactory.cs:222`, from `CommandExecution.ConfigVersion` = the instance's config),
  `ErrorEnvelope` (`ErrorResponseWriter.cs:55`), `ReceiptView` (stored receipt value, `CoreEndpoints.cs:103`), GM command
  responses (`GmCommandEndpointFactory.cs:197`), metric `config_version_info` (`PlatformMetrics.cs:74`), readiness data
  `configVersion` (`HealthChecks.cs:38`).
- **Wire** (`proto/gameserver/core/v1/core.proto`): `ErrorEnvelope.config_version = 8` (last field 9),
  `CommandResponse.config_version = 6` (last 8), `SnapshotResponse.config_version = 2` (last 3), `GetConfigResponse`
  `config_version = 1`, `not_modified = 2`, `files = 3`, `server_time_utc_ms = 4`; `GetConfigRequest.config_version = 1`.
  `identity.proto` `LoginResponse.config_version = 4` (last 6). API-03: "do not change the meaning of, or reuse, a
  published field number".
- **SDK** (`sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs`): `TrackConfig(version)` on login,
  snapshot, command and error envelope; `ServerConfigVersion` ("last config version announced by the server");
  `ConfigOutdated => ServerConfigVersion != _config.Version`; `RefreshConfigAsync` sends `_config.Version`, stores
  `response.ConfigVersion`. `IConfigCache { Version; Files; Store(version, files) }`, default `InMemoryConfigCache`.
  `TransportResponse` has status, content type and body only (no headers). `UnityWebRequestTransport` sets no
  `Accept-Encoding`. The package has no `InternalsVisibleTo`.
- **.NET 10.0.14 reference pack**: `JsonDocumentOptions.AllowDuplicateProperties` and
  `JsonSerializerOptions.AllowDuplicateProperties` exist; `ZLibCompressionOptions.CompressionLevel` (0–9) and
  `BrotliCompressionOptions.Quality` (0–11, default 4) exist.
- **No response compression** anywhere in `src/` (no `AddResponseCompression`/`UseResponseCompression`).
- **First consumer** (`E:\ProjGits\zh-pvp-server`, read-only): see R4.

## R1 — The client config version travels in a new field; `config_version` keeps its meaning (FR-043, BC-4) — **architect decision, owner may override**

**Decision.** Reject spec default D6. `config_version` stays the server config version (CFG-01) on every message. Five
player messages gain an additive `string client_config_version` field:

| Message | New field |
| --- | --- |
| `ErrorEnvelope` | `client_config_version = 10` |
| `CommandResponse` | `client_config_version = 9` |
| `SnapshotResponse` | `client_config_version = 4` |
| `GetConfigResponse` | `client_config_version = 5` |
| `identity.LoginResponse` | `client_config_version = 7` |

`ReceiptView` gets no new field: it shows the stored server version of a past command. `GetConfigRequest` is unchanged.
Its `config_version` is still "the version the client holds", and the server answers `not_modified` when it equals
**either** the current client config version **or** the current server config version (ordinal). Both are exact: the
server version determines the client bundle, and the two hashes are domain-separated, so neither can be confused with a
version of another config (R2). The SDK tracks `client_config_version` when it is present and falls back to
`config_version` otherwise (R14), and keys its cache on that value.

**Rationale.**
1. **Requirements and constitution.** API-03 forbids changing the meaning of a published field. D6 would do exactly that
   to `config_version` in six messages. CFG-01, CFG-02, CFG-03, SMP-01, ACC-35 and the glossary use one `configVersion`
   ("Login returns the server's current `configVersion` and every response carries `configVersion`"). Under R1 that
   sentence stays literally true. The client version is an added concept, not a redefinition. One clause still needs an
   owner reading: CFG-03/SDK-02 "the client caches the bundle by `configVersion`" becomes "by the version of its
   client-visible part" (see the spec's Assumptions). That is a refinement of the cache key. D6 would need the same
   reading **plus** a redefinition of the field.
2. **First consumer, right after v0.7.0.** R1 does not break zh-pvp. Its response-version test keeps comparing
   `config_version` with `IGameConfig.ConfigVersion` (once its `TestGameConfig` uses the new `GameConfig` constructor,
   which BC-6 forces anyway). No game or tool that reads `config_version` changes meaning silently. The FG-019 gain comes
   with the SDK of the same 0.7.0 package, which the game takes in the same bump.
3. **Protobuf compatibility, both directions.**

   | Pair | R1 (additive) | D6 (semantic) |
   | --- | --- | --- |
   | Old SDK → new server | Unknown field skipped (proto3 keeps it as unknown). Behaves as today: caches by server version, `GetConfig` with the current server version gets `not_modified`. One reload after the upgrade (BC-5 value change) | No wire change. Caches by client version without knowing it. One reload |
   | New SDK → old server | `client_config_version` empty → falls back to `config_version`: today's behaviour | No wire change. Works |
   | Rolling deploy 0.6 ↔ 0.7 | The SDK alternates between the old server version and the client version until the rollout ends: possible download per switch (CFG-01 accepts two versions in a rollout) | Same |
   | Tools and tests reading `config_version` | Unchanged meaning | Silent change of meaning (a value that no longer matches receipts or the metric) |

4. **Names stay true.** The SDK's public `ServerConfigVersion` keeps meaning "the server's version". Under D6 it would
   hold the client version.

**Rejected: D6** (semantic change of `config_version`). Its advantages are no proto change and an immediate gain for old
SDKs. The costs: it breaks API-03, it redefines a Requirements term, and it changes meaning silently for every reader of
the field. Its argument that "the SDK needs a rule for which one to trust" amounts to one line (`client` when present,
else `server`) at one place (R14).

**Trade-offs.** Five additive fields, both set at six server sites. `GetConfig` accepts two values. The SDK has a fallback
branch. Old SDKs keep the pre-slice download behaviour until they update.

## R2 — Version computation (FR-040..FR-042, FR-045, BC-5, BC-6)

**Decision.** One public static helper in Platform `GameServer.Platform.Config.GameConfigVersions.Compute(label, files,
clientVisible)` returns both versions. Each is `{label}+{hex12}`, where `hex12` is the first 12 lower-case hex digits of
SHA-256 over:

- **server**: `"gameserver.config.server/1\n"`, `"label\n{label}\n"`; for each effective file in ordinal name order:
  `"file\n{name}\n{byteLength}\n"` + bytes; then `"visible\n"` + `"{name}\n"` per client-visible name in ordinal order.
- **client**: `"gameserver.config.client/1\n"`, `"label\n{label}\n"`; for each client-visible file in ordinal name order:
  `"file\n{name}\n{byteLength}\n"` + bytes.

Strings are UTF-8 and lengths are decimal. Lists are deduplicated first: the effective `files` is a dictionary and
`clientVisible` is a set, so duplicates and list order cannot change the result. The overlay is applied before hashing,
so only the effective content counts.

- `GameConfig` gains `GameConfig(string label, IReadOnlyDictionary<string, byte[]> files, IEnumerable<string>
  clientVisible)`, which computes both versions. The old 4-argument constructor becomes `[Obsolete]`. It still works, and
  it computes the client version from its inputs. `FileGameConfigLoader.ComputeVersion(label, files)` becomes
  `[Obsolete]` (FR-042). `IGameConfig` gains `string ClientConfigVersion { get; }` (BC-6). It is a regular member, not a
  default interface member: a default would recompute the hash on every response.
- `TestGameConfig.FromJson` and `FromDirectory` use the new constructor or the loader, so they match the host by
  construction.

**Rationale.** Length prefixes remove the `name\nbytes` concatenation ambiguity of today's input. The value changes once
anyway (BC-5), so this is the moment to fix it. The domain tags make the two hashes differ for identical input
(all files visible). Two given values then share 48 bits with probability 2⁻⁴⁸. The version-matrix tests assert
inequality. The label is both in the hash and in the prefix: a label change always changes both versions (D8, FR-045).

**Alternatives.** Client version = server version of a filtered config (rejected: the two would coincide when every file
is visible, which breaks "a server version never equals a client version", story 3 scenario 6). A distinct format such as
`{label}+c{hash11}` (rejected: the spec keeps `{label}+{hash12}` for both; the domain tag is enough).

## R3 — The loader reports every fault, including duplicate properties (FR-001..FR-003, BC-2, BC-3)

**Decision.** `FileGameConfigLoader.Load` collects violations into a list and throws one `GameServerStartupException`
when the list is not empty. Checks that are independent run even after a fault:

1. Base manifest exists (else that line only: there is no file list to check).
2. Parse each manifest (base, and env when present), first as a `JsonDocument` with `AllowDuplicateProperties = false`,
   then deserialized. A parse fault adds a line, and that manifest's file list is skipped.
3. For every file of every manifest that parsed: name plain → exists → read → for `.json`, parse with
   `AllowDuplicateProperties = false` (one line per file).
4. Label empty (when the base manifest parsed). `clientVisible ⊂ files` (when both manifests parsed: one line per
   name).

The existing line texts are kept. The new duplicate line and the parse line give the JSON path and position
(data-model §5.1). The loader also records, per effective file, whether it came from `config/env/<env>`. An internal
`GameConfig.OverlaidFiles` set lets report lines name the overlay. [VERIFY V1]

**Rationale.** SC-001 needs "two missing files and one invalid JSON give exactly 3 lines". All or nothing (D2) stays:
no `GameConfig` exists when the list is not empty.

**Alternatives.** Own `Utf8JsonReader` walk for duplicates (kept as the V1 fallback: about 40 lines, path stack + one
name set per open object).

## R4 — Strictness (D3, BC-1, BC-2) and the first consumer

**Decision (confirms D3, with two precisions).**
- A duplicate property name in any JSON object of a manifest or `.json` file is a loader fault (BC-2).
- Framework rules ignore unknown properties (FR-017).
- `maxQuantity` must be a JSON integer ≥ 1 (FR-015).
- IAP stores are `google` and `apple` (FR-016).
- **Precision 1, `kind`**: a JSON string matching `currency` or `item` case-insensitively, which is what the catalog's
  converter accepts. An integer `kind`, which the catalog accepts today, is a fault: an enum ordinal in a balance file is
  a mistake.
- **Precision 2, integers**: "JSON integer" means a number token readable as `Int64` (`JsonElement.TryGetInt64`).
  `1.0`, `1e3` and strings are faults. The catalog's STJ binding refuses them too, so `Load` stays at most as strict as
  the rules (FR-024).

**First consumer check (2026-11-04, read-only).** `E:\ProjGits\zh-pvp-server`:
- `src/ZhPvp.Host/config/base` holds 10 files, label `zhpvp-1`, 7 client-visible, no env overlay.
- A scan of every `.json` under `src/` and `tests/` (18 files, `bin/`/`obj/` excluded) with a duplicate-detecting parser
  finds **no duplicate property name and no parse error**.
- `resources.json`: `CASH`, `GOLD`, `kind: "currency"`, `maxQuantity: 1000000000`, string `displayName`. That passes
  FR-015.
- `iap.json` is `{"products": {}}`. That passes FR-016: an empty products object is valid.
- Inline test configs in `GameCatalogTests.cs` use `maxQuantity` 10 and 1000. They go through `GameCatalog.TryLoad`, not
  a host.

**Verdict: neither BC-1 nor BC-2 breaks the first consumer's current config.** Its only forced changes are BC-6 (its
`tests/ZhPvp.Tests/Support/TestGameConfig.cs` calls `new GameConfig(label, FileGameConfigLoader.ComputeVersion(label,
files), files, clientVisible)`, which becomes CS0618 = error under `TreatWarningsAsErrors`) and the tests that pin FG-024
and FG-019 (spec "First-consumer migration").

## R5 — Declarations are data and code in Core (FR-010, FR-011, FR-020, D1, ARC-01)

**Decision.** In `GameServer.Core`, namespace `GameServer.Config` (signatures in
[public-api.md §1](contracts/public-api.md#1-core-gameservercore)):

- `ModuleDescriptor.ConfigFiles: IReadOnlyList<ConfigFileDeclaration>` and
  `ModuleDescriptor.ConfigCatalogs: IReadOnlyList<ConfigCatalogDeclaration>`.
- `ConfigFileDeclaration.Required(name, params ConfigRule[])` and `.Optional(name, params ConfigRule[])`.
- `ConfigRule(string name, ConfigRuleCheck check)`, with `delegate void ConfigRuleCheck(ConfigContext context)`.
- `ConfigCatalogDeclaration.Of<T>(Func<ConfigContext, T?> build, params string[] files)`.
- `ConfigContext`: `Config`, `File` (the rule's file, or null in a catalog build), `Problems`, and `Json(file)`, which
  returns the parsed root of a loaded `.json` file. Each file is parsed once per start and shared. The element is valid
  only during the call.
- `ConfigProblems`: `Error(at, problem, fix)` and `Warning(at, problem, fix)`, `ErrorCount`.
- `ConfigLocation`: `WholeFile`, `Row(n)`, `Key(k)`, `Path(p)`, `.InFile(f)`.
- `ConfigJson`: a small set of helpers with uniform problem texts, used by the framework, the sample and the fixture.

`ConfigContext` and `ConfigProblems` have internal constructors. `GameServer.Core.csproj` gains `InternalsVisibleTo
GameServer.Platform`, the only framework assembly that runs declarations (TestKit goes through Platform's existing IVT).
`RequiredConfigFiles` stays and means `Required(name)` without rules. A file named in both lists is one declaration per
module.

**Rationale.** D1: declarations sit in the module's single registration point and live in business code. Core already
references `System.Text.Json` (`IGameConfig.GetFileJson`), so `JsonElement` adds no dependency. ARC-01 stays green. A
named rule gives the "rule threw" line a readable name (a lambda's method name is not readable). One `ConfigContext` type
serves rules and builds: one thing to learn. `ConfigJson` exists because the economy, IAP, sample-game and fixture rules
all need "required string / integer in range / array / object" with the same wording (FR-017). Without it, four copies
would drift.

**Alternatives.** JSON Schema (rejected in D1). An `IConfigRule` interface (rejected: a delegate plus a name is enough;
no state). A public constructor for `ConfigContext` (rejected: the test kit call covers consumer tests; it would freeze
an internal shape). `IConfigProblemSink` interface (rejected: one implementation).

## R6 — The declaration engine, shared by the host and the test kit (FR-011..FR-014, FR-018, FR-020..FR-023)

**Decision.** `internal sealed class ConfigDeclarationRunner` in `GameServer.Platform.Config`:

```csharp
internal static ConfigDeclarationResult Run(
    IReadOnlyList<RegisteredModule> modules,
    IGameConfig config,
    Func<Type, CatalogBinding> binding);   // Declared (platform builds) or Provided (host/test registration, resolved)
```

Steps, in module registration order:

1. **Parse cache.** A lazy `JsonDocument` per `.json` file on first `Json(file)`. Disposed at the end of `Run`.
2. **Rules.** For every `ConfigFileDeclaration` whose file is present (`config.HasFile`), run each rule with a
   `ConfigContext` bound to (module, file). Exceptions are caught and give one line naming the rule. Each file's error
   count is recorded. Absent optional files are skipped. Absent required files are skipped silently: the registry
   reports them (FR-014).
3. **Catalogs.** A second declaration of a type that is already declared gives one line naming both modules (FR-023).
   Only the first is used. For each declaration: skip when any listed file is absent, or has ≥ 1 rule error from any
   module (FR-022, no line). Otherwise, by binding:
   - *Declared*: `Build(context)` in `try`. Reported errors, a null result without errors, or an exception each give
     lines, and the catalog is marked failed. Otherwise it is stored.
   - *Provided*: resolve the type from DI in `try`. A failure gives one line. The rules still ran (spec edge case).
4. **Result.** Error lines, warning lines, built catalogs (`Type → object`), and failed catalog types.

`ModuleRegistry.ValidateConfig` stays the only source of the required-file line. Its set becomes, per module:
`RequiredConfigFiles` ∪ required `ConfigFiles` ∪ (files of `ConfigCatalogs` not declared optional by that module). The
line text is unchanged.

**Rationale.** One function produces the lines for both the host and `ConfigDeclarationCheck` (R17), so FR-018 "the same
text" holds by construction. Rules first, then catalogs, gives FR-022's "rules before builds" and its "no second line".
Parsing each file once keeps SC-007 (about 1 ms per 100 KB of JSON).

**Alternatives.** Run rules per file in dependency order and expose "this file has errors" to cross-file rules
(rejected: the IAP rule reads `resources.json` leniently and checks references only against well-formed rows, which the
spec allows; no extra API). Parallel rules (rejected: the cost is small, and determinism matters for the report order).

## R7 — Where declared catalogs are registered (FR-021, FR-024, BC-8; architect item 4)

**Decision.** `GameServerBuilder.AddModule`, right after `module.ConfigureServices`, adds for each catalog declaration
`TryAdd(ServiceDescriptor.Singleton(type, sp => sp.GetRequiredService<ConfigCatalogStore>().Get(type)))`. It records that
exact descriptor in an internal `DeclaredCatalogBindings` singleton. That singleton also captures the `IServiceCollection`
(the pattern of `AnalyticsActivation`), so after `Build()` it can tell, per type, whether the **last** non-keyed
descriptor of that type is the platform's (*Declared*) or someone else's (*Provided*: the host or a test, registered
before or after the module).

`ConfigCatalogStore.Get(type)`:
- built → the instance (one per host lifetime);
- the phase ran and the catalog failed → a `GameServerStartupException` with one line and the "unavailable" marker (R8);
- the phase never ran (no `UseGameServer`, and not provision) → `InvalidOperationException`: "{Type} is a declared config
  catalog: the platform builds it in UseGameServer. Fix: start the host with UseGameServer/RunGameServerAsync/TestHost,
  or in a test without a host build it with ConfigDeclarationCheck.Check(config, modules) and call
  report.AddCatalogsTo(services) (FG-018)."

**Code that builds services from a module alone** (a consumer test calling `module.ConfigureServices(services, ctx)`)
gets no registration for the catalog. Resolving the catalog then fails with the DI message "No service for type
'ResourceCatalog' has been registered" (or "Unable to resolve service for type … while attempting to activate …Handler").
That message names the type. The release notes (BC-8) and the guide give the one-call fix:
`ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)`, or an explicit instance
registration. No framework code path is in this situation (code facts).

**Rationale.** The platform registers from the declaration, so no module can forget to and a catalog is never lazy.
`TryAdd` after the module's own `ConfigureServices` lets "the host or a test registered its own first" win (FR-021).
Last-descriptor inspection covers registrations made after `AddModule` too, so an override is never silently
replaced by a build. A clear error was chosen over a lazy fallback for hosts that skip `UseGameServer`. A fallback build
would bypass the rules and recreate FG-018 for exactly the paths that are hardest to see.

**Alternatives.**
1. Modules keep registering a lazy factory as a fallback (rejected: then the platform cannot tell a module's fallback
   from a host override, and a bad config is resolved twice and gives two lines).
2. The platform builds lazily on first resolve when the phase never ran (rejected: rules skipped, see above).
3. A Core extension `services.AddConfigCatalogs(descriptor)` that modules call (rejected: it is the "module must
   remember" pattern this slice removes, and Core would need the build runner).

## R8 — Config load, config status, provision and export modes (FR-014, FR-030..FR-033, D4, D5; architect item 5)

**Decision.**
- **`GameConfigLoad`** (internal singleton, Platform) runs the loader once (thread-safe `Lazy`) and keeps either the
  `GameConfig` or the violation list. The default `IGameConfig` registration becomes `sp =>
  sp.GetRequiredService<GameConfigLoad>().GetOrThrow()`. On failure it throws a **new**
  `GameServerStartupException(violations)` on each resolve, with `Data["GameServer.Unavailable"] = "game config"`. The
  violations are the same as today, so code that catches it today (zh-pvp `GameConfigCheck`) keeps working. A host or
  test override of `IGameConfig` (TestHost) bypasses `GameConfigLoad` entirely, as today.
- **Mode.** `GameConfigLoad.Mode` is `Serve` by default. `ProvisioningRunner` sets `NotLoaded` before anything else. In
  that mode `GetOrThrow()` never touches the disk. It throws `GameServerStartupException("[config] the game config is
  not loaded in provision mode …")` with the marker value `"provision"`. Export mode never resolves `IGameConfig`
  (unchanged, 014 V4).
- **`GameConfigStatus`** (public, `GameServer.Platform`, next to `IStartupCheck`) is one singleton instance registered by
  `AddGameServer`. The config phase publishes it once (internal `Publish`, a volatile reference to an immutable
  snapshot). Reading never throws:

  | State | When | `Config` | `DeclarationsPassed` |
  | --- | --- | --- | --- |
  | `NotLoaded` | before the phase, and in provision mode | null | false |
  | `Loaded` | `IGameConfig` resolved | the DI instance | true when no required-file, rule or catalog error |
  | `Failed` | `IGameConfig` resolution threw | null | false |

- **Checks in provision mode** see `NotLoaded`. A check that needs `IGameConfig` (constructor or inside `Check`) gets a
  **warning** line "did not run: provision mode does not load the game config" instead of an error. Provision does not
  validate config (D4), so it does not fail on this. See BC-9 in plan.md.

**Rationale.** Caching the load makes "every loader fault once" true even when many services resolve `IGameConfig`. It
also stops re-reading the disk on each failed resolve. Marking exceptions with `Exception.Data` lets `CheckAll` and
`ValidateServices` recognize "unavailable because config/catalog failed" without changing the sealed public exception
type and without the cost of reference tricks. A single published instance avoids the DI trap of a factory status
cached as `NotLoaded` by an early resolve. Blocking `IGameConfig` in provision mode makes D4 ("config-free") true: today
a check that resolves it there silently loads config.

**Alternatives.**
1. Unseal `GameServerStartupException` and derive an internal `GameConfigUnavailableException` (equivalent; rejected
   to keep the public type unchanged, and kept as the fallback if a reviewer objects to `Data`).
2. A status factory registration (rejected: DI caches the first value).
3. Keep loading config in provision when a check asks (rejected: contradicts D4 and the status).

## R9 — Startup checks are created one by one (FR-030, FR-031, FR-034, SC-003)

**Decision.** `AddGameServer` registers an internal `StartupCheckRegistrations`, which captures the `IServiceCollection`.
`CheckAll(services, mode)` enumerates, after `Build()`, the non-keyed descriptors with `ServiceType ==
typeof(IStartupCheck)`, in registration order, and creates each inside its own `try`:

| Descriptor | Creation | Name in lines |
| --- | --- | --- |
| `ImplementationInstance` | the instance | its runtime type name |
| `ImplementationType` | `ActivatorUtilities.CreateInstance(services, type)` | the type name |
| `ImplementationFactory` | `factory(services)` | the factory's declaring type, skipping compiler-generated closures (`<>c…`) |

Then `Check(result)` runs in its own `try`, as today. Failure classification, for both creation and `Check`, uses the
marker of R8 found anywhere in the exception's `InnerException` chain:

| Exception | Serve mode | Provision mode |
| --- | --- | --- |
| marker `game config` | error "did not run: the game config did not load" (the config lines are already in the report) | — |
| marker `catalog T` | error "did not run: catalog T was not built" | — |
| marker `provision` | — | **warning** "did not run: provision mode does not load the game config" |
| other `GameServerStartupException` during `Check` | its violations (today's behaviour) | same |
| other exception during creation | error "could not be created: {type}: {message}" | same |
| other exception during `Check` | error "failed: {type}: {message}" (today's line, unchanged) | same |

Instances are not disposed. An instance descriptor is owned by the container, and a factory may return a shared
singleton. The XML doc states that a check is not disposable and must not throw. [VERIFY V2, V3, V4]

**Rationale.** MS DI has no public API to resolve one registration of a multi-registered service. Creating from the
descriptor is the only way to isolate one constructor from the others. `ActivatorUtilities` uses the same
"longest satisfiable constructor" rule as DI for the framework's single-constructor checks (V3). The framework never
resolves `IEnumerable<IStartupCheck>` anywhere else, so no instance is created twice.

**Alternatives.** Resolve `GetServices` first and fall back to per-descriptor creation only on failure (rejected:
elements created before the failure stay cached in the container, then get created again, which is two code paths for
one job). Wrap each check in a lazy proxy at registration time (rejected: needs every registration to go through a
framework method; `AddSingleton<IStartupCheck, X>()` is the documented form).

## R10 — The startup sequence keeps one report and builds the endpoint set once (FR-003, FR-013, FR-033, 014 R8/V16)

**Decision.** In `UseGameServer` the config phase replaces the first block (internal `ConfigStartup.Run(services,
options, registry, errors)`, Platform.Web):

1. Resolve `IGameConfig` (catch → `errors` += violations, status `Failed`). Set both version metrics.
2. `registry.Validate(options, config)`: unchanged call, with the required-file set extended (R6).
3. When the config loaded: `ConfigDeclarationRunner.Run` → errors and warnings. The warnings join the startup warnings
   logged by `RunStartupChecks`.
4. When the config loaded and `Hosting:MapPlayerApi`: build `ConfigBundle` (R11) and check its size (FR-053).
5. Publish `GameConfigStatus`.

Then everything else as today: `WireCatalog.Validate`, `ValidateServices` (the relay can now resolve catalogs; a marked
exception there is skipped, because its cause is already reported), deployment checks, `RunStartupChecks` (FR-033: after
the phase), `ValidateModuleOptions`, mapping, and the endpoint handling of 014. **Nothing in the phase enumerates
endpoints.** On a passing configuration the endpoint set is still built once, by the pre-serving check. On a failing one,
`UseGameServer` still adds the endpoint lines and throws one report. Provision runs `ModuleRegistry.Validate(options,
null)` + `WireCatalog.Validate` + secret sources + `CheckAll(mode: Provision)`: no rules, no catalogs, no bundle (FR-014,
D4). Export runs none of it.

The startup information line (013 FR-054) is unchanged. `LogStartup` adds a second line after it (data-model §5.6) with
both versions, file counts and the bundle size, so operators see the client version without the readiness endpoint.
[VERIFY V12]

**Rationale.** Catalogs must exist before `ValidateServices` resolves the outbox relay, because a game consumer may take
a catalog. Checks must see the final status (FR-033). Keeping the phase free of endpoint work preserves the 014 V16
budget.

## R11 — The bundle is serialized once; the size check runs at startup (FR-052, FR-053)

**Decision.** `ConfigBundle` (internal, Platform.Web) is built in the phase from the loaded config:

- `Prefix`: the serialized `GetConfigResponse { config_version = server, files = clientVisible sorted ordinal }`, i.e.
  fields 1 and 3, written once.
- Per full response: `Prefix` + `{ server_time_utc_ms = 4, client_config_version = 5 }` serialized as a small
  `GetConfigResponse` (fields 4 and 5 only). Protobuf merges concatenated messages, and Google.Protobuf writes fields in
  number order, so the bytes are identical to serializing the whole message (field 2 is absent when false).
  [VERIFY V5]
- `MaxResponseBytes = Prefix.Length + tail with server_time = long.MinValue (10-byte varint)`. When it exceeds
  `Limits.ConfigBundleBytes`, startup fails with one line (FR-053, BC-7). "Exactly at the limit" starts. The request-time
  413 stays, comparing the actual length with the limit.
- `not_modified` responses are built per request as today (a few dozen bytes).

**Rationale.** Config is immutable for the host lifetime. Today every full response copies every file into a new
`ByteString` and serializes about 1 MiB. Now it is one buffer copy. The startup check uses the same bytes the endpoint
sends, so "at the limit" is exact. The per-request server time stays exact (FR-052). The variable fields come last on
the wire (field numbers 4 and 5), which also keeps a gzip splice possible later (R12, alternative 2).

## R12 — Compression: per-request, owned by the config endpoint (FR-050..FR-052, FR-056; architect item 3)

**Decision.** No ASP.NET `ResponseCompression`. An internal `ConfigResponseCompression` in Platform.Web is called by the
`GetConfig` handler only:

1. **Eligible** when `GameServer:ConfigCompression` is true (default) **and** the response carries files **and** its
   length ≥ 1024 bytes (constant `MinCompressBytes`, PCR platform §1).
2. **Negotiate** with `HttpRequest.GetTypedHeaders().AcceptEncoding` (`StringWithQualityHeaderValue`). The candidates
   are `br` and `gzip`, with q-values honoured: `q=0` excludes, `*` with q > 0 covers whichever candidate is not listed,
   the highest q wins, and a tie goes to `br`. An absent or unparseable header means no encoding. [VERIFY V7]
3. **Compress** the full response bytes into a pooled buffer: Brotli quality and zlib level from V6 (expected `br` 4,
   gzip 6). When the result is not smaller than the input, send it uncompressed.
4. **Headers.** `Content-Encoding: br|gzip` when compressed. `Vary: Accept-Encoding` on **every** `GetConfig` response
   while compression is enabled, so a shared cache can never serve a compressed body to a client that did not ask.
   `Content-Length` = bytes sent.
5. **Never** touches any other route. Other routes do not call it, and no middleware exists that could.

`ConfigBundleBytes` limits the uncompressed bytes (FR-053). The rollback switch `GameServer:ConfigCompression=false`
sends today's exact bytes (FR-052).

**Rationale.** Compression runs only when a client's version differs: first install and the herd after a client-visible
deploy (story 4 removes the rest). For the bundles that exist (sample about 2 KB, first consumer about 28 KB), it costs
well under 1 ms. For the 1 MiB maximum, the expected cost is a few ms at the chosen levels, inside SC-005's 20 ms p95
(V6). Owning the 40 lines in the endpoint gives exact control of threshold, q-values, headers and the never-larger rule.
It changes nothing for any other route by construction (FR-051) and for consumer hosts that may add `ResponseCompression`
themselves.

**Rejected: ASP.NET `ResponseCompression` scoped to the route** (`UseWhen(path) + UseResponseCompression`). It has no
minimum size, so `not_modified` would be compressed and grow (FR-051). Opting out per response works only for HTTPS
(`IHttpsCompressionFeature`), and behind a TLS-terminating proxy Kestrel sees HTTP. It would need a replaced global
`IResponseCompressionProvider` (it conflicts with a consumer's own registration) and `EnableForHttps`. It would also
duplicate the route template in a path predicate.

**Rejected for now: a cached compressed static part.** The variable fields are last, so a gzip body could be spliced:
`gzip header + precompressed raw deflate of Prefix ending with a sync flush + a final stored block holding the tail +
CRC-32 (continued over the tail) + ISIZE`. This is a valid single-member stream that any zlib decodes. It needs
hand-written deflate framing and CRC arithmetic on the one path whose bug would stop **every** client from parsing
config, and Brotli has no comparable cheap splice. It is the documented fallback if V6 shows per-request compression
over budget.

## R13 — Unity transports and compression (FR-055; architect item 3)

**Decision (safe default).** The server compresses **only** when the request explicitly accepts an encoding (R12). The
SDK sets no `Accept-Encoding` and adds no decoder. It relies on the transport: `HttpClientTransport` decodes only when the
caller's handler has `AutomaticDecompression` (and only then sends the header). For UnityWebRequest, [VERIFY V8] decides:

- **V8 holds** (each supported platform either sends no `Accept-Encoding`, or sends one and decodes transparently): no SDK
  code. PCR unity-sdk records the observed headers per platform.
- **V8 fails on a platform that advertises gzip without decoding**: Runtime/Core adds an internal
  `ConfigBody.Decode(byte[])` used by `RefreshConfigAsync` only. A body starting with the gzip magic `1F 8B` is
  decompressed with `GZipStream`, with a decoded-size cap of 64 MiB so a broken body cannot exhaust memory. A protobuf `GetConfigResponse` cannot start with `0x1F`: its first byte is a field tag `0x0A`
  (field 1, length-delimited) or `0x10`/`0x1A`/`0x20`/`0x2A`, so the sniff is unambiguous. Requires [VERIFY V9].
- **V9 also fails** (no usable `GZipStream` on that IL2CPP target): document `GameServer:ConfigCompression=false` for
  deployments serving that platform, and open a framework item. Never silently corrupt.
- Brotli is expected only from browsers (WebGL is not a supported target of this slice) and `HttpClient`. The SDK never
  decodes Brotli itself (`BrotliStream` is not in `netstandard2.1`).

## R14 — SDK version tracking under R1 (FR-046, FR-065)

**Decision.** `GameServerClient`:
- `TrackConfig(server, client)` is called with both fields of login, snapshot, command and error envelope. The
  *effective* version is `client` when it is not empty, else `server`.
- `ServerConfigVersion` = the last non-empty `config_version` (unchanged meaning).
- **New** `ClientConfigVersion` = the last effective version.
- `ConfigOutdated => ClientConfigVersion != null && ClientConfigVersion != Config.Version`.
- `ConfigVersionChanged(string)` is raised with the effective version when it differs from the cache and from the last
  announced one (today's dedupe).
- `RefreshConfigAsync` sends `Config.Version` and stores the files under the effective version of the response.

Additive: one new property. Two members change meaning, both documented in PCR unity-sdk: `ConfigOutdated` and the
event argument now follow the client version **when the server sends it**. Against an older server they are unchanged.

## R15 — Persistent config cache (FR-060..FR-067, SC-006)

**Decision.** `PersistentConfigCache : IConfigCache` in `Runtime/Core` (C# 9, `netstandard2.1`, no UnityEngine). It uses
one container file `gameserver-config.bin` in the given folder, with format v1 in [sdk-cache.md](contracts/sdk-cache.md):
magic, format version, payload length, payload (version + sorted files), and a SHA-256 of the payload.

- **Load (constructor):** read the main file, else a complete temp file (crash recovery). Validate magic, format,
  lengths against the file size, UTF-8, unique names, and the hash. Any failure gives an empty cache with `LoadProblem`
  set. It never throws.
- **Store:** set `Version`/`Files` in memory first. Then write `gameserver-config.bin.tmp`
  (`FileStream`, `Flush(true)`), and `File.Replace(tmp, main, null)` when the main file exists, else `File.Move(tmp,
  main)`. Any I/O exception is caught and sets `WriteProblem`. A success clears it. One version is kept.
- Names are strings inside the container, never paths (FR-063).
- **Fault injection:** an internal `CacheFileOps` seam (open-temp, write, flush, replace/move, read) plus
  `[assembly: InternalsVisibleTo("GameServer.Client.Tests")]` in a new `Runtime/Core/AssemblyInfo.cs` (+ `.meta`).
- **Unity helper:** `Runtime/Unity/UnityConfigCache.cs` (`#if UNITY_5_3_OR_NEWER`), `UnityConfigCache.Create(string
  environmentName)` → `PersistentConfigCache(Path.Combine(Application.persistentDataPath, "gameserver-config",
  environmentName))`. One folder per environment, as the guide recommends.
- **Threading:** synchronous I/O on the calling (main) thread. A 1 MiB load or save takes ≤ 50 ms outside Unity (SC-006,
  V11). Not thread-safe, like the client.

[VERIFY V10, V11]

**Rationale.** One file plus replace-by-rename is the only atomic multi-file update that every target offers. The
load-time temp recovery covers the platforms where `File.Replace` is emulated as delete + move. SHA-256 is in the
`netstandard2.1` BCL on every Unity backend. It detects corruption only; tampering is out of scope (spec edge case).

**Alternatives.** One file per config file + manifest (rejected: no atomic multi-file replace; names would become paths).
JSON container (rejected: base64 inflates 1 MiB by a third and costs parse time). `PlayerPrefs` (rejected: size limits,
Unity-only).

## R16 — Observability (FR-044)

- `PlatformMetrics.SetClientConfigVersion(string)` and gauge `client_config_version_info{version}`. The existing
  `config_version_info{version}` is unchanged in name and label and keeps the server version.
- Readiness data of `ConfigHealthCheck` adds `clientConfigVersion` next to `configVersion`.
- Startup config line (data-model §5.6).
- No new log category.

## R17 — Test kit call (FR-018, FR-070)

**Decision.** `GameServer.TestKit.ConfigDeclarationCheck.Check(IGameConfig config, params IGameModule[] modules) →
ConfigDeclarationReport`. It builds a `ModuleRegistry` from the modules (public API), takes the required-file lines from
`ModuleRegistry.ValidateConfig` (internal, via the existing IVT), and runs `ConfigDeclarationRunner.Run` with every
catalog bound as *Declared*. The report carries `Errors`, `Warnings`, `Passed`, `Catalog<T>()`, `TryGetCatalog<T>`,
`ThrowIfFailed()` (returns the report) and `AddCatalogsTo(IServiceCollection)` (BC-8 fix). It has no unit-test framework
dependency, no host and no store (013 FR-010).

## R18 — Framework modules, samples, fixture and docs (story 6, FR-070, FR-071, FR-075..FR-077)

- Economy: `ConfigFiles = [Required(resources.json, EconomyConfigRules.Resources)]` and
  `ConfigCatalogs = [Of<ResourceCatalog>(c => ResourceCatalog.Load(c.Config), resources.json)]`. `RequiredConfigFiles`
  is removed from the descriptor (now redundant).
- IAP: `Required(iap.json, IapConfigRules.Products)` and `Of<IapCatalog>(…)`. Cross-file grant checks read
  `resources.json` leniently.
- Sample game: `battle.json` and `shop.json` rules plus two catalogs. Their `ConfigureServices` lose the lazy factories.
- Fixture: `Required(notes.json, maxLength integer ≥ 1)` and a `FixtureConfigTests` using the kit call.
- KestrelDemo: `new GameServerClient(…, config: UnityConfigCache.Create("local"))`, plus a log line "config from cache
  {version}" / "config downloaded {version}".
- Docs: consumer guide §12 "Game config" (new; §11 bump checklist gains three steps), module-authoring,
  `templates/module/README.md`, PCR platform §21 (new) + §1 + §9 + §17, PCR economy/iap/sample-game/unity-sdk,
  `docs/html/01-gioi-thieu.html`, `02-them-command.html` (+ version stamp).

## Facts to confirm before coding (VERIFY)

| # | Fact | How to confirm | Fallback |
| --- | --- | --- | --- |
| V1 | `JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false })` throws `JsonException` on a repeated property at any depth (object in array, nested object), ordinally (`"a"` and `"A"` are distinct); the exception carries `Path` (or the message names the property) plus `LineNumber`/`BytePositionInLine`; `JsonSerializerOptions { AllowDuplicateProperties = false }` behaves the same for the manifest | Unit test on 6 shapes (root, nested, in array, escaped name `"\u0061"` vs `"a"`, case variants, manifest) | Own `Utf8JsonReader` walk (path stack + `HashSet<string>` per object, unescaped names) producing the same line |
| V2 | A check whose constructor needs `IGameConfig`, created with `ActivatorUtilities.CreateInstance` or a factory, surfaces the factory's `GameServerStartupException` (marker in `Data` intact), unwrapped or as an inner exception, also through one intermediate service | Unit test with a throwing factory, direct and one level deep | Classification already walks `InnerException`; if `Data` is lost on wrapping, match on `ReferenceEquals(ex.Violations, load.Violations)` |
| V3 | `ActivatorUtilities.CreateInstance(sp, type)` chooses the same constructor as DI for every framework, sample, fixture and zh-pvp check type (primary constructors, internal classes, optional parameters) | Unit test over the registered check types of SampleHost and Fixture.Host | `ActivatorUtilities.CreateFactory(type, [])` with the constructor DI would choose (most parameters satisfiable via `IServiceProviderIsService`) |
| V4 | The `IServiceCollection` captured in `AddGameServer` lists, after `Build()`, the descriptors added later by modules and the host (as `AnalyticsActivation` relies on); a lambda factory's `Method.DeclaringType` is the closure class nested in the registering type | Unit test on SampleHost composition + zh-pvp-shaped factory registration | Name a factory registration `factory #{index}` with its `ServiceType` |
| V5 | `Prefix(fields 1,3)` + `Tail(fields 4,5)` bytes = `GetConfigResponse.ToByteArray()` of the same values on Google.Protobuf 3.36.2, for 0, 1 and 50 files, an empty client version, and a negative server time | Byte-equality test | Serialize the whole message per request, wrapping precomputed file bytes with `UnsafeByteOperations.UnsafeWrap` (no copy) |
| V6 | Per-request cost and ratio on the reference machine for the sample bundle, a 28 KB zh-pvp-shaped bundle and a synthetic 1 MiB bundle: gzip level 1/6/9 and Brotli quality 1/4/6 → pick the highest levels with p95 ≤ 20 ms at 1 MiB and ratio ≤ 30% for all three | Opt-in perf test (`GAMESERVER_PERF=1`), 200 iterations after warm-up | Lower level; if even level 1 misses 20 ms, implement the gzip splice of R12 (alternative 2) for gzip and keep Brotli at quality 1 |
| V7 | `GetTypedHeaders().AcceptEncoding` parses `gzip;q=0`, `br;q=0.5, gzip`, `*`, `GZIP`, `identity`, empty and malformed values without throwing; q defaults to 1 | Unit test | Own parser of the RFC 9110 `Accept-Encoding` grammar (token + optional `;q=`) |
| V8 | UnityWebRequest (Unity 6000.3.22f1) on Editor (Windows), Windows player, Android IL2CPP and iOS IL2CPP: which `Accept-Encoding` it sends without being told; whether `SetRequestHeader("Accept-Encoding", …)` is accepted; whether `gzip` (and `br`) responses are decoded before `downloadHandler.data` | Scratch Kestrel probe (not port 5180) that logs request headers and answers a gzip body + a `br` body; KestrelDemo E2E logs; ACC-18 device run for Android/iOS | R13 fallbacks: SDK gzip sniff + `GZipStream` (needs V9); else `GameServer:ConfigCompression=false` for that platform's deployments |
| V9 | `System.IO.Compression.GZipStream` decompresses on Unity 6 IL2CPP Android and iOS with stripping High (managed + native zlib present; `link.xml` entry if needed) | Device run of a test scene decoding a known gzip blob | Keep that platform uncompressed (R13) |
| V10 | `File.Replace(tmp, dest, null)` on Windows (.NET + Unity Mono Editor), Android and iOS (IL2CPP) replaces an existing file atomically (rename semantics); `File.Move` works when `dest` is absent; `FileStream.Flush(true)` is available | Unit test outside Unity; device run of the cache test scene | Delete + move (the load-time temp recovery keeps "old or new") |
| V11 | Load and save of a 1 MiB bundle ≤ 50 ms each outside Unity (`SHA256.Create()` + file I/O); SHA-256 survives IL2CPP stripping High | Opt-in perf test; device run | Managed CRC-32 table (integrity only) with format v1 unchanged except the checksum field size (decide before the first release) |
| V12 | No framework singleton resolves a declared catalog type during `Build()` or before the phase (economy, IAP, GM, analytics consumers); the outbox relay's consumers resolve after the phase | Grep + startup test with a consumer taking `ResourceCatalog` | Move the phase to the first statement of `UseGameServer` (it has no dependency on the later checks) |
| V13 | `TestHost` with `GameServer:ConfigPath` uses the real loader (`useLoader`), so the FG-023 scenario test runs on a missing file | Existing `TestKitHostTests` pattern | `LocalFlavorFactory` with a temp content root |
