# Review decisions — 015 config (T070 findings, T066 gaps, T067 item)

Architect decisions for T071. Base: `015-config` at `519ca56` (code = `abfc32d`). Each item: decision, rationale, exact
behaviour and texts, tests, BC impact, doc changes. Doc changes are given as text here: `release-notes-draft.md`,
`docs/` and the 015 contracts are **not** edited by this file. Line texts in `code` are exact templates.

Summary:

| Item | Decision | New BC | Deferred |
| --- | --- | --- | --- |
| M1 | (a) build runs when only optional files are absent + backstop line | — | — |
| M2 | Loader refuses case-variant sibling names (OrdinalIgnoreCase), manifest included | none; BC-2 widened | strict case-sensitive binding rejected |
| M3 | Startup probe per encoding + process-wide compression gate (identity when busy) | — | gzip splice, per-player rate limit |
| G1 | `GameConfigStatus.TryGetCatalog<T>`; constructor injection stays safe | — | — |
| G2 | TestKit `TestGameConfigStatus` (`NotLoaded`, `Failed`, `From(report)`) | — | — |
| G3 | Fix: `LocalFlavorFactory` throws `GameServerStartupException` | — (0.7.0-only type) | — |
| G4 | No TestKit API; docs + fixture allow-list + one fixture SDK test | — | — |
| U1 | One cross-platform cache-name rule, lowercase ASCII | — (unreleased API) | — |
| m1–m11 | Confirmed, some adjusted (below) | BC-6 sentence added | Autofac detection |

---

## M1 — Catalogs over an optional file fail lazily

**Decision: option (a), plus a backstop line.** A declared catalog whose listed files include an optional file that is
absent is still built. The build decides what absence means (usually an empty catalog). If the build cannot cope, the
host fails at startup with a line. Option (b) is rejected.

**Rationale.** A catalog over an optional file is a real need: the template's file is `Optional` so hosts without it still
start, and its commented catalog reads that file. With (b) the module would have to make the file `Required` (which breaks
hosts without it) or leave the catalog undeclared. An undeclared catalog is the lazy FG-018 path this slice exists to
remove. With (a), "every declared catalog is built before traffic or the host does not start" holds without exceptions.
That invariant is also what makes the `ValidateServices` skip of marked exceptions sound.

**Behaviour** (`ConfigDeclarationRunner.BuildCatalog`). The skip condition becomes, for the declaring module `m`:

```text
skip (no line, FailedCatalogs += T) when any listed file f has rule errors (any module),
                                     or f is absent and f ∈ ModuleRegistry.RequiredConfigFiles(m)   // registry already has the line
otherwise run the binding (Declared: Build; Provided: resolve), even when some listed optional files are absent
```

Build results keep their lines (`CatalogThrew`, `CatalogNull`, problem lines). Only the "threw" case gains a variant. When
the build throws and at least one listed optional file is absent, the line names the first absent optional file (in the
declaration's file order) instead of using `CatalogThrew`:

`[module {module}] catalog {Type}: the build threw {ExceptionType}: {message}; optional config file '{file}' is absent. Fix: build an empty catalog when Config.HasFile("{file}") is false, or declare the file Required (FG-018).`

A null result is still an error (`CatalogNull`). "No catalog" would be lazy again.

**Backstop** (`ConfigStartup.Run`, and the same check in `ConfigDeclarationCheck` so the kit matches). After the runner,
for each type in `result.FailedCatalogs` it checks whether the report explains the failure. It is explained when
`required`, `result.Errors` or the config loader lines are non-empty for that start. If none of them is, it adds:

`[catalog {Type}] was not built and no other line explains why (framework fault). Fix: report it with this config; the catalog would fail at its first use (FG-018).`

The check can be coarse: "any error line exists" is enough, because startup fails then anyway. It exists so the lazy 500
can never come back silently.

**Template** (`templates/module/Template.Module/TemplateModuleDefinition.cs:90-92`). Replace the two comment lines with:

```csharp
// A typed catalog built once from the files before traffic; handlers take it from DI. Declare it here, never register
// it in ConfigureServices (an undeclared catalog stays lazy and its faults surface on the first request). The file is
// optional, so the build runs without it too and must return an empty catalog then:
// ConfigCatalogs = [ConfigCatalogDeclaration.Of(c => c.Config.HasFile(ConfigFile) ? TemplateCatalog.Load(c.Config) : TemplateCatalog.Empty, ConfigFile)],
```

**Tests** (each fails today):
- `ConfigDeclarationRunnerTests`:
  - Optional file absent, and the build checks `HasFile` and returns an empty catalog: built, no line.
  - Optional absent, and the build calls `context.Json(file)`: exactly the new line, type in `FailedCatalogs`.
  - Optional absent, build returns null: `CatalogNull`.
  - Required absent: still no runner line (registry line only, unchanged).
  - Optional present with a rule error: skipped, no line (unchanged).
  - Provided binding with an absent optional file: resolved.
- Invariant test (runner, property style over the existing runner fixtures): `FailedCatalogs.Count > 0` ⇒ the runner errors
  or the registry `ValidateConfig` lines are not empty.
- Backstop unit test: a hand-built `ConfigDeclarationResult` with one failed type and no lines gives the backstop line.
- Host test (`Platform.Web.Tests`): a probe module declares `Optional(probe.json)` and a catalog over it, and `probe.json` is
  absent. The host starts, and a handler that takes the catalog gets the empty instance (200, not 500). With a build that
  throws, the host fails with the new line.
- Kit: the same two configs through `ConfigDeclarationCheck.Check` give the same lines (FR-018).

**Doc changes.**
- `docs/guides/module-authoring.md` §4 "Config" first bullet. Replace `file của catalog là bắt buộc trừ khi module khai nó
  `Optional`.` with: `file của catalog là bắt buộc trừ khi module khai nó `Optional`; file `Optional` vắng thì catalog **vẫn
  được build**: build tự quyết catalog rỗng (`c.Config.HasFile(file)` false → catalog rỗng). Build ném hoặc trả null → một dòng
  lỗi, host không start (không bao giờ lazy).` Also update the quoted template comment to the new text above.
- `docs/guides/consumer-guide.md` §12.3, after "only when its files are present and free of rule errors,": insert `(an
  absent file declared `Optional` does not stop the build: the build must then return an empty catalog, and a build that
  throws stops the host)`.
- `docs/pcr/platform.md` "Dòng luật và catalog": add the two new templates after the `CatalogThrew` line.
- `specs/015-config/contracts/startup-report.md` §5: add rows "Build threw, optional file absent" and "Not built, no line
  (framework fault)" with the texts above.
- `release-notes-draft.md` FG-018 section: one bullet, `A catalog over an optional file is built when the file is absent
  (its build returns an empty catalog); a build that cannot cope stops the host with one line. No declared catalog is ever
  built on first use.`

---

## M2 — Case-variant duplicates

**Decision: report case-variant sibling names as a loader fault, in every `.json` config file and in both manifests.** The
detector compares names `StringComparer.OrdinalIgnoreCase` after unescaping. This widens BC-2 and adds no BC-11.
**Rejected:** case-sensitive deserialization of config.

**Rationale.**
- Every framework reader of config is case-insensitive: `GameConfig.GetFile<T>` and the manifest use `GameServerJson.Options`,
  which has Web defaults, and STJ matches with `OrdinalIgnoreCase`. So a case-variant pair is exactly as ambiguous as an
  exact duplicate. It needs the same loud fault, decided once in the loader for every reader.
- Strict case-sensitive binding (BC-11) would change the public `IGameConfig.GetFile<T>` silently. A game file whose keys
  differ in case from its C# record (e.g. PascalCase JSON bound to camelCase-named properties) would read default values
  (`0`, `null`) with no line. That failure mode is worse than the one being fixed. The rules (`ConfigJson`, case-sensitive)
  stay at least as strict as `Load`: a file with only `MaxQuantity` gets "'maxQuantity' is missing" from the rule
  (FR-024 holds).
- Cost to legitimate data: two keys of one object that differ only in case become invalid, e.g. product keys
  `google:Gems` and `google:gems` in one `products` map. Store product ids that differ only in case are a defect in
  practice. A clear startup line is acceptable.

**First consumer (checked 2026-11-04, read-only, zh-pvp `7911f20`).** A token-level scan of the 11 committed config files
(`src/ZhPvp.Host/config/base`, including the manifest) found **no sibling names equal ignoring case**. The game's tables
use PascalCase keys (`Chapter`, `Fee`, …) read by its own `ConfigTable.RowOptions` (`PropertyNameCaseInsensitive =
false`, no naming policy). `resources.json` and `iap.json` are camelCase. So **neither option breaks zh-pvp**. Strict
binding would leave its tables untouched too, because it does not use `GetFile<T>` for them. The decision rests on the
silent-default risk for other games. The framework's own configs (`samples/**/config`, `tests/**/config`,
`templates/**`) also have no case-variant pair.

**Behaviour** (`JsonDuplicateProperties`, also covers m11):
- Per object, the first spelling of each name is kept (`Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)`,
  created lazily, see m11). `DuplicateProperty` gains `FirstName` (the first spelling).
- An exact repeat keeps today's line unchanged:
  `[config] '{path}' has property '{name}' twice at {jsonPath} (line {line}, byte {byte}). Fix: keep one property; the last one used to win silently (CFG-01).`
- A case variant (`FirstName != Name` ordinally) gives a new line, where `{line}`/`{byte}` are the position of the second
  name:
  `[config] '{path}' has properties '{firstName}' and '{name}' at {jsonPath} (line {line}, byte {byte}), which differ only in case. Fix: keep one property; config is read ignoring case, so the last one used to win silently (CFG-01).`
- The manifest goes through the same `AddDuplicates` call, so `"label"` + `"Label"` is covered.

**Tests:**
- `FileGameConfigLoaderTests.Case_variants_are_different_properties` (line 174) is **inverted** into
  `Case_variants_are_reported_as_one_line`: `maxQuantity`/`MaxQuantity` in a row gives the new line with the right path and
  position, and no `GameConfig`.
- Same for the manifest (`label`/`Label`).
- An escaped variant (`"Max"` vs `"max"`) is detected.
- A file with an exact duplicate and a case variant gives two lines, in document order.
- Regression: today's exact-duplicate tests are unchanged.
- An end-to-end proof of the bug: before the fix, a row with `maxQuantity: 5, MaxQuantity: 0` passes the rule and the
  catalog holds 0. After the fix, the host fails with the loader line.

**Doc changes.**
- `docs/pcr/platform.md` §21 "Loader". Replace `(BC-2; so ordinal, tên escape được unescape, `"a"`/`"A"` hợp lệ)` with
  `(BC-2; so không phân biệt hoa thường — `OrdinalIgnoreCase`, như reader của framework — sau khi unescape: `"a"`/`"A"` trong
  cùng một object là lỗi)`, and add the case-variant template after the "twice at" template.
- `startup-report.md` §2: row "Case-variant property (new, BC-2)" with the template.
- `docs/guides/consumer-guide.md` §12.1, after "…is a fault (BC-2): `…(CFG-01).`" add: `Two names that differ only in case
  (`maxQuantity`, `MaxQuantity`) are the same fault, because config is read ignoring case: `[config] '{path}' has properties
  '{first}' and '{name}' at {jsonPath} (line {line}, byte {byte}), which differ only in case. Fix: …`.`
- `release-notes-draft.md` BC-2 row, column "Change": append `; names that differ only in case count as repeated (config
  is read ignoring case)`. Column "How it surfaces": append `, or `… has properties 'maxQuantity' and 'MaxQuantity' at
  $.resources[0] (line 5, byte 7), which differ only in case. …``.

---

## M3 — `GetConfig` recompresses the whole bundle per full response

**Decision: (a) a per-encoding startup probe, plus a process-wide compression gate that falls back to identity.** The gzip
splice (b) and a per-player rate limit are **deferred**.

**Rationale.**
- The probe removes the pure waste: an incompressible bundle is never compressed per request.
- The gate bounds the CPU of the 1 MiB case regardless of request rate or the number of accounts. When it is busy, the
  response is the identity body that every client already handles. That body is exactly the v0.6.0 response and the
  rollback-switch response, so the worst case is "as before this slice", never wrong bytes.
- No new framing code touches the path whose bug would break config for every client.
- The splice would cut gzip CPU per request to about a tail's worth, but it is hand-written deflate/CRC framing, and Brotli
  has no equivalent. Real bundles are 1.7 KB (sample) and 27.9 KB (zh-pvp, 0.23 ms gzip), so its gain is theoretical today.
- A per-player limit bounds nothing global, since guest accounts are free. No per-player rate limiter exists outside the
  command queue, so it would be new infrastructure (state, settings, PCR).

**Behaviour.**

1. **Probe** (startup, in `ConfigStartup` step 4, only when `ConfigCompression` is true and a bundle is built). For each of
   `br` and `gzip`: `Compress(encoding, Prefix, Tail(long.MinValue), capacity: MaxResponseBytes - 1)`. The encoding is
   *offered* when the result fits. It is not probed and not offered when `FileCount == 0` or `MaxResponseBytes <
   MinCompressBytes`. Cost: one compression per encoding at startup, about 17 ms for 1 MiB at the shipped levels, inside
   SC-007.
2. **Negotiation** considers only offered encodings: `Negotiate(HttpRequest request, bool offerBrotli, bool offerGzip)`.
   A client that accepts `br, gzip` gets gzip when only gzip is offered. Neither offered → identity with **no**
   compression attempt.
3. **Gate.** At most `MaxConcurrentCompressions = Math.Max(1, Environment.ProcessorCount / 4)` responses are compressed at
   the same time, process-wide (`SemaphoreSlim.Wait(0)`, never waits). If no permit is free, the response is sent identity
   (`Content-Length`, `Vary: Accept-Encoding`, no `Content-Encoding`). The permit is released in `finally` after the
   compression, before the write (the write does not hold CPU).
4. Unchanged: the never-larger rule per request, `Vary` on every `GetConfig` response while the switch is on, the rollback
   switch, `not_modified` never compressed, and the 1024 B threshold.

**Shape** (internal, Platform.Web):

```csharp
/// <summary>How GetConfig responses of this host are compressed: switch, encodings worth offering, concurrency gate.</summary>
internal sealed class ConfigCompressor
{
    /// <param name="gate">Permits = concurrent compressions; the host passes new SemaphoreSlim(MaxConcurrentCompressions).</param>
    public static ConfigCompressor Create(ConfigBundle bundle, bool enabled, SemaphoreSlim gate);
    public bool Enabled { get; }
    public bool OffersBrotli { get; }
    public bool OffersGzip { get; }
    public Task WriteFullAsync(HttpContext context, ConfigBundle bundle, ReadOnlyMemory<byte> tail);
    public Task WriteNotModifiedAsync(HttpContext context, ReadOnlyMemory<byte> body);
}
```

`ConfigResponseCompression` keeps the constants, `Negotiate` (with the two offer flags) and `Compress` as static helpers.
`ConfigPhaseSummary.Values` gains `ConfigCompressor? Compressor`, non-null exactly when `Bundle` is non-null. The handler
uses `summary.Current.Compressor!`. `ConfigStartup` computes `MaxConcurrentCompressions` once and passes the semaphore,
so a test can own the gate.

**Tests:**
- Probe: a bundle whose client-visible file is 8 KiB of random bytes is offered neither encoding. A request with
  `Accept-Encoding: gzip, br` gets the identity bytes, equal to `Prefix ++ Tail(t)`, with `Vary` and no
  `Content-Encoding`.
- `Negotiate` table: the existing rows with both offered, plus `br, gzip` with only gzip offered → gzip, and nothing
  offered → null.
- Gate: the test creates `new SemaphoreSlim(1)` and passes it to `Create`, takes its only permit (`Wait(0)`), then calls
  `WriteFullAsync` on a `DefaultHttpContext` with `Accept-Encoding: gzip` over a compressible 4 KiB bundle. The result is
  identity bytes with `Vary` and no `Content-Encoding`. After `Release()`, the same call is gzip. No test-only member is
  needed.
- `Create` with a gate whose `CurrentCount` is 0 at creation → `ArgumentOutOfRangeException` (a gate must allow one).
- The existing `ConfigCompressionTests` table stays green, as does the endpoint sweep (only `platform.config` may carry
  `Content-Encoding`).
- Perf (opt-in): the T050 numbers unchanged for a single request. No new budget.

**Doc changes.**
- `docs/pcr/platform.md` line 24 (§1 row "Nén `GetConfig`"): column 2 becomes `ngưỡng 1024 B; gzip level 6, Brotli quality
  4; encoding không nhỏ hơn ở lần thử lúc start thì không dùng; tối đa max(1, số CPU / 4) response nén cùng lúc, còn lại gửi
  không nén`.
- `docs/pcr/platform.md` compression table (around line 1440): add rows `encoding không được chọn ở lần thử lúc start
  (bundle không nén nhỏ hơn được) | không nén` and `đã có max(1, CPU/4) response đang nén | không nén (body như khi tắt
  nén, + `Vary`)`.
- `docs/guides/consumer-guide.md` §12.7, after "…the response carries files and is at least 1024 bytes;": insert `an
  encoding that does not shrink the bundle (checked once at startup) is never used, and at most max(1, CPUs / 4) responses
  are compressed at the same time (others are sent uncompressed, so a burst of full downloads cannot take the CPU);`.
- `release-notes-draft.md` "FG-019 (delivery)": one bullet with the same two facts. "Known before the tag": `Per-request
  compression cost is bounded by a gate (max(1, CPU/4) concurrent); the gzip splice (research R12 alt. 2) is deferred
  until a real bundle approaches the 1 MiB limit.`

**Deferred**, with triggers:
- Gzip splice: a consumer bundle above about 256 KiB **and** herd measurements showing that gate fallbacks cost bandwidth
  that matters.
- Per-player `GetConfig` rate limit: with FG-013 (login without snapshot, slice 016) or a general per-player request
  limiter.

---

## G1 — A startup check cannot get a declared catalog

**Decision: add `GameConfigStatus.TryGetCatalog<T>`.** It is the supported way for a check to read a declared catalog.
Constructor injection of a catalog stays safe and documented (one "did not run" line when it is unavailable), but it is
not recommended. Rebuilding with `Load` stays wrong.

**Rationale.** The documented story is "a startup check reads config-derived state through `GameConfigStatus`". Its lines
already tell authors to read the catalog through the status, which is impossible today. One read-only member makes the
story true. It also gives a check that can skip silently when the catalog is missing, like `TryGetConfig`. The status
already lives in Platform next to the store, so this needs no new dependency.

**Shape** (public, additive):

```csharp
public sealed class GameConfigStatus
{
    // existing members unchanged

    /// <summary>
    /// The declared catalog T built in this start (by its declaration or from a host/test registration of T); false when the
    /// config is not Loaded, T is not a declared catalog, or it was not built. Never throws.
    /// </summary>
    public bool TryGetCatalog<T>([NotNullWhen(true)] out T? catalog) where T : class;
}
```

The internal `PublishLoaded(IGameConfig config, bool declarationsPassed, IReadOnlyDictionary<Type, object> catalogs)`
takes `result.Catalogs` from `ConfigStartup`. The snapshot keeps a read-only copy. `PublishFailed` and `NotLoaded` have an
empty map.

**Line text change** (`ConfigReportLines.CheckNotRunCatalog`):

`[{check}] the startup check did not run: catalog {Type} was not built (see the lines above). Fix: take GameConfigStatus and read the catalog with TryGetCatalog (FG-023).`

(was `… Fix: take GameConfigStatus and read the catalog only when DeclarationsPassed (FG-023).`)

**Tests:**
- Loaded with the catalog built → true, and the instance is the same one handlers get.
- The catalog failed → false.
- Provision → false.
- Undeclared type → false.
- A host-registered (Provided) instance → true, that instance.
- A host test whose check uses `TryGetCatalog` on a failing catalog gives **no** extra line.
- `ConfigReportLinesTests` and the two check-isolation tests that pin the old fix text are updated.

**Doc changes.** Replace the old fix text everywhere it appears (`docs/pcr/platform.md`,
`contracts/startup-report.md` §7, `data-model.md`, `release-notes-draft.md`). `docs/guides/consumer-guide.md` §12.5:
- List item: add `- `TryGetCatalog<T>(out catalog)`: a declared catalog built in this start (false otherwise).`
- Last paragraph: replace `…and reads `config` or a declared catalog otherwise.` with `…and otherwise reads `config`, or a
  declared catalog with `status.TryGetCatalog<MyCatalog>(out var catalog)`. Never rebuild a catalog with its `Load` in a
  check (the build already ran once).`
- `contracts/public-api.md` §2 `GameConfigStatus` block: add the member.
- Module authoring §4 last-but-one bullet: `Startup check … đọc config qua `GameConfigStatus` (`TryGetConfig`,
  `TryGetCatalog<T>`, `DeclarationsPassed`)…`.

---

## G2 — A game cannot unit-test its startup check

**Decision: add a TestKit helper.** `GameConfigStatus` keeps its internal constructor, so production code can never fake
a status.

```csharp
namespace GameServer.TestKit;

/// <summary>A <see cref="GameConfigStatus"/> for unit tests of a startup check, in the state the host would publish.</summary>
public static class TestGameConfigStatus
{
    /// <summary>Provision mode, or before the config phase.</summary>
    public static GameConfigStatus NotLoaded();

    /// <summary>The config did not load.</summary>
    public static GameConfigStatus Failed();

    /// <summary>
    /// Loaded with the report's config: DeclarationsPassed = report.Passed, catalogs = the catalogs the report built
    /// (TryGetCatalog). Exactly what the host publishes for that config and those modules.
    /// </summary>
    public static GameConfigStatus From(ConfigDeclarationReport report);
}
```

`ConfigDeclarationReport` gains an internal `IGameConfig Config`, set by `ConfigDeclarationCheck.Check`. That rules out a
mismatch between the config and the report. A check that reads only config uses
`TestGameConfigStatus.From(ConfigDeclarationCheck.Check(config))` (no modules: passes, no catalogs). `StartupCheckResult`
already has a public constructor.

Consumer test shape (for the guide §12.5):

```csharp
var report = ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(dir), new IdentityModule(), new GameModule());
var result = new StartupCheckResult();

new RewardsCheck(TestGameConfigStatus.From(report)).Check(result);

result.Errors.ShouldBeEmpty();
```

**Tests:** kit tests for each factory (state, `Config` identity, `DeclarationsPassed`, `TryGetCatalog` equal to
`report.TryGetCatalog`), and one fixture test (`FixtureStartupCheckTests`) on a fixture check that takes the status.
`TestKitArchitectureTests`: add `GameServer.Platform.GameConfigStatus::.ctor`, `::PublishLoaded` and `::PublishFailed` to
`AllowedInternalMembers`.

**Docs:** `consumer-guide.md` §12.5: add the snippet above and the sentence `In a unit test build the status with
`TestGameConfigStatus.NotLoaded()`, `.Failed()` or `.From(report)`.` `contracts/public-api.md` §5: add the type.

---

## G3 — `LocalFlavorFactory.StartServer()` does not throw on a startup failure

**Decision: fix it.** Under `LocalFlavorFactory`, a startup failure throws the `GameServerStartupException`. Both kinds
throw: `UseGameServer` report lines, and a late fault (store open, the pre-serving endpoint check). This applies to
`StartServer()`, `CreateClient()`, `Services` and any other first-use member. Documenting the current behaviour instead is
rejected: a test that gets 404s or an unstarted host instead of the report wastes exactly the time the report exists to
save.

**Behaviour.**
- New internal marker `GameServer.Platform.Web.Hosting.StartupFailureMode` (sealed class, one static instance `Throw`).
  `LocalFlavorFactory.ConfigureWebHost` registers it with
  `builder.ConfigureServices(s => s.AddSingleton(StartupFailureMode.Throw))`.
- `GameServerRunExtensions.ServeAsync`, both catches: when `app.Services.GetService<StartupFailureMode>()` is not null,
  rethrow the `GameServerStartupException` (the one found by `FindStartupException` in the second catch), skip the stderr
  write and return no exit code. Without the marker, behaviour is unchanged (stderr + exit 1). Real hosts and a consumer's
  own `WebApplicationFactory` are unaffected.
- `LocalFlavorFactory` overrides `CreateHost(IHostBuilder)`: `try { return base.CreateHost(builder); } catch (Exception ex)
  when (FirstStartupException(ex) is { } s) { throw s; }`. It walks `InnerException` and `AggregateException`, so the
  caller always sees the `GameServerStartupException` itself, however the factory plumbing wraps the entry point's
  exception.
- Disposing a factory whose start failed must not throw. `DisposeAsync` catches `ObjectDisposedException` from
  `base.DisposeAsync()` and still deletes its own root. This replaces the test helper `DisposeAfterFailedStartAsync`.
- `TestKitArchitectureTests.AllowedWebInternalTypes`: add `GameServer.Platform.Web.Hosting.StartupFailureMode`.

**BC:** none. `LocalFlavorFactory` is new in 0.7.0 (slice 013, absent at `v0.6.0`). Mention it in the 013 notes' TestKit
section when the round's notes are merged.

**Tests:**
- `LateEndpointValidationTests.A_web_application_factory_start_fails_on_a_late_fault_and_starts_without_it`: expect
  `GameServerStartupException` whose violations contain the route line (was `InvalidOperationException`).
- New `LocalFlavorFactoryStartupFailureTests`: an early fault (missing required config file through
  `LocalFlavorOptions.Settings` pointing `GameServer:ConfigPath` to a temp config without it) → `StartServer()` throws
  `GameServerStartupException` with exactly the report lines; nothing written to stderr (redirect `Console.Error` in a
  non-parallel collection); `DisposeAsync` does not throw and the root is deleted.
- `A_real_host_exits_1_with_the_report_on_stderr_and_never_binds_its_port` stays green (no marker).

**Docs:** `consumer-guide.md` (LocalFlavorFactory paragraph) and `docs/guides/environments-and-local-run.md`: `A startup
failure throws GameServerStartupException from StartServer()/CreateClient() with the report as Violations (the real host
prints it and exits 1).`

---

## G4 — Game tests that call the SDK need an aliased reference

**Decision: TestKit offers nothing. Docs, plus a widened fixture allow-list and one fixture test that proves the pattern.**

**Rationale.** The SDK compiles the same generated protocol types as the server (`GameServer.Protocol.*`), so one project
that references both needs an extern alias (CS0433 otherwise). If TestKit referenced the SDK, the SDK would flow
transitively into every game test project, and the conflict would appear in all of them, including those that never call
the SDK. The alias is a property of the consuming project and cannot be packaged.

**Changes:**
- `tests/ConsumerFixture/tests/Fixture.Tests/Fixture.Tests.csproj`: add
  `<ProjectReference Include="$(GameServerRoot)sdk/GameServer.Client/GameServer.Client.csproj" Aliases="sdk" />`.
- `FixtureProjectTests`:
  - The allowed set adds `$(GameServerRoot)sdk/GameServer.Client/GameServer.Client.csproj`.
  - New assertion: that reference carries `Aliases="sdk"` ("the SDK's generated types collide with the server's without an
    alias").
- New `FixtureSdkConfigTests` (`extern alias sdk;`):
  - Start `LocalFlavorFactory<Program>`.
  - Create `new sdk::GameServer.Client.GameServerClient(options, new sdk::GameServer.Client.HttpClientTransport(factory.CreateClient()))`.
  - Log in as a guest, call `RefreshConfigAsync()`, and assert that `Config.Version == ClientConfigVersion` and that the
    files are exactly the fixture's client-visible files.
  - This is the T066 path, with no copy of the framework's SDK harness.
- `consumer-guide.md` §8 (tests) or §12.4: new paragraph "Calling the SDK from a game test":

  ```xml
  <ProjectReference Include="$(GameServerRoot)sdk/GameServer.Client/GameServer.Client.csproj" Aliases="sdk" />
  ```

  ```csharp
  extern alias sdk;
  using sdk::GameServer.Client;
  ```

  Followed by: `The alias is required: the SDK and the server compile the same generated protocol types. Use
  HttpClientTransport over factory.CreateClient().`

---

## U1 — `UnityConfigCache.Create` validates the name per platform (T067)

**Decision: one rule, identical on every platform, and it never consults `Path.GetInvalidFileNameChars()`.**

**The rule.** `environmentName` is valid when **all** of these hold:
1. It is 1 to 64 characters.
2. Every character is in `a-z`, `0-9`, `.`, `_` or `-` (ASCII, lowercase only).
3. It does not start or end with `.`. That also excludes `.` and `..`.
4. Its part before the first `.` is not a Windows device name: `con`, `prn`, `aux`, `nul`, `com1`–`com9`, `lpt1`–`lpt9`.

`null` → `ArgumentNullException(nameof(environmentName))`. Any other invalid value → `ArgumentException(message,
nameof(environmentName))` with:

`'{environmentName}' is not a valid config cache name. Fix: use 1 to 64 lowercase letters, digits, '.', '_' or '-', not starting or ending with '.' and not a Windows device name (con, nul, com1, …), for example 'local', 'live' or 'localhost-5080'.`

**Rationale.**
- The same name behaves the same in the Editor and on devices. That is the bug.
- Lowercase only removes the remaining cross-platform difference: case-insensitive Windows/macOS file systems against
  case-sensitive Android. `Live` and `live` would otherwise be one folder in the Editor and two on a device.
- Device names and a trailing `.` are refused because the Windows Editor cannot create them.
- The API is unreleased, so strict now is free. Relaxing later is additive, tightening later would break.
- `localhost:5080` is refused everywhere, with a suggestion in the message.

**Placement.** The check moves into `Runtime/Core` as `internal static string? FolderNameProblem(string name)` in
`PersistentConfigCache.cs`. It returns null or the message. No new file, so no new `.meta`. It is the same assembly as
`Runtime/Unity` (one asmdef), and it is testable outside Unity through the existing IVT. `UnityConfigCache.Create` calls it
first. `PersistentConfigCache(folder)` itself keeps accepting any folder path (non-Unity callers choose full paths).

**Tests** (`GameServer.Client.Tests`, outside Unity):
- Accepted: `local`, `live`, `localhost-5080`, `dev.eu_1`, `a`, 64 × `a`.
- Refused with the message: `""`, `" "`, `.`, `..`, `.cache`, `live.`, `Live`, `localhost:5080`, `a/b`, `a\b`, `con`,
  `NUL` (uppercase, refused by rule 2 anyway), `nul.x`, `com1`, `lpt9.log`, 65 × `a`, `café`, `live ` (trailing space),
  `a\u0001`.
- `null` → `ArgumentNullException`.
- Architecture: `sdk/unity/com.gameserver.client/Runtime/**/*.cs` does not contain `GetInvalidFileNameChars`.

**Doc changes.**
- `consumer-guide.md` §12.8, last-but-one paragraph. Replace from "Do not derive it from the URL: `Create` refuses path
  separators and the platform's invalid file-name characters (`Path.GetInvalidFileNameChars`), which differ between
  platforms: `:` is invalid on Windows, so `localhost:5080` throws in the Windows Editor, while .NET on Android and iOS
  refuses only `/` and NUL." with: ``Create` accepts 1 to 64 lowercase letters, digits, `.`, `_` and `-` (not starting or
  ending with `.`, not a Windows device name such as `con` or `nul`), the same on every platform, and throws
  `ArgumentException` otherwise: `localhost:5080` is refused everywhere (use `localhost-5080`).`
- `docs/pcr/unity-sdk.md` line 83–86. Replace `(không rỗng, không `.`/`..`, không dấu phân cách hay ký tự cấm)` … through
  `Android và iOS chỉ cấm `/` và NUL):` with `(1–64 ký tự `a-z`, `0-9`, `.`, `_`, `-`; không bắt đầu hay kết thúc bằng `.`;
  không phải tên thiết bị Windows `con`, `prn`, `aux`, `nul`, `com1`–`com9`, `lpt1`–`lpt9`; **cùng một luật trên mọi nền
  tảng**, sai → `ArgumentException`, `null` → `ArgumentNullException`):`.
- `release-notes-draft.md`:
  - Checklist step 5: replace `(`:` is refused on Windows/Editor but not on Android/iOS)` with `(`:` is refused; names
    are 1–64 lowercase letters, digits, `.`, `_`, `-`, the same on every platform)`.
  - FG-019 persistent-cache section: replace `; `Create` refuses the platform's invalid file-name characters, which differ
    (`:` on Windows/Editor only), so it is not derived from the URL.` with `; `Create` accepts only 1–64 lowercase letters,
    digits, `.`, `_` and `-` on every platform, so it is not derived from the URL.`
  - "Known before the tag": delete the `UnityConfigCache.Create checks the name with the running platform's…` bullet. Add
    under FG-019 (persistent cache): `Fixed in review (T071): one cache-name rule on every platform (it was the running
    platform's invalid file-name characters, found in T067).`
- `contracts/public-api.md` §6 `UnityConfigCache.Create` doc comment: the rule above.

---

## Minors

**m1 — Loader I/O exceptions become lines. Confirmed.**
- `ReadManifest` and `ReadConfigFile` catch `IOException` and `UnauthorizedAccessException` around `File.ReadAllBytes` and
  add:
  `[config] '{path}' cannot be read: {message} Fix: check that the host process can read the file (CFG-01).`
  (house style of the `[export] … cannot be written: {message} Fix: …` line).
- A manifest that cannot be read skips its file list, like a parse fault.
- Why: today the exception escapes the loader, `GameConfigLoad`'s `Lazy` caches it, `ConfigStartup` catches only
  `GameServerStartupException`, and the host dies with a stack trace instead of a report.
- Tests: a file locked with `FileShare.None` (Windows) and an unreadable mode 000 file (Unix, skipped on Windows) give the
  line. With two such files and one missing file, there are 3 lines.

**m2 — Null or empty manifest names become one line. Confirmed.** An entry of `files` or `clientVisible` that is `null`,
empty or whitespace only gives:

`[config] '{manifestPath}' lists an empty name in '{files|clientVisible}'. Fix: remove the entry or name a file (CFG-01).`

The entry is then ignored. It is not added to `listed`, `files` or `clientVisible`, so no "does not exist" or "not
listed" line follows from it. Today `null` throws `NullReferenceException` out of the loader, and `""` reports the
directory as a missing file. Tests: `[null]`, `[""]`, `["  "]` in each list, base and env manifest.

**m3 — Version-hash injectivity: reject control characters (not length-prefixing).**
- The loader refuses any file name that contains a `char.IsControl` character, with the existing line
  `[config] file name '{name}' is not allowed. Fix: use a plain relative file name.` `{name}` is rendered with each
  control character as `\uXXXX` (uppercase hex), so the report stays one line per fault.
- `GameConfigVersions.Compute` throws
  `ArgumentException("Config file name '{escaped}' contains a control character.", nameof(files))` for such a key (the
  visible names are a subset of the keys).
- The `<remarks>` sentence becomes `Names never contain a control character (the loader refuses them, Compute throws), so
  each name ends at its line feed; contents are length-prefixed by byte count, so no two configs share an input.`
- Why: the only ambiguity is a name containing `\n`. Refusing it keeps today's version values (no third BC-5 change in
  one slice), and also keeps log lines and paths sane. Labels need nothing: the label is also the literal prefix of the
  version, so two different labels never give equal versions.
- Tests: the loader with `"a\nb.json"` (JSON-escaped in the manifest) gives the line with `a\u000Ab.json`. `Compute`
  throws. Pinned version values are unchanged.

**m4 — A constructor that throws `GameServerStartupException`: violations verbatim. Confirmed.** In `CheckAll`'s creation
`catch`, after `ReportUnavailable`, an unmarked `GameServerStartupException` adds `ex.Violations` (no `CheckNotCreated`
line), in serve and provision mode alike, like the `Check` path. Test: a check whose constructor throws
`new GameServerStartupException("[mycheck] bad setting")` gives exactly that line, and the other checks still run. Doc:
`startup-report.md` §7 row "Creation threw a GameServerStartupException (unmarked) | any | errors | its Violations".

**m5 — `StartupCheckRegistrations`. Confirmed, with the naming adjusted. Autofac is documented only.**
- A factory that returns null now throws inside `Create`, giving
  `[{name}] the startup check could not be created: InvalidOperationException: the registration's factory returned null. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).`
  Today the line carries a `NullReferenceException`.
- Naming order for factories: (1) `ImplementationFactory.Method.ReturnType` when it is a non-abstract class that
  implements `IStartupCheck`. This covers `AddSingleton<IStartupCheck, X>(sp => …)` and method groups returning `X`.
  (2) Otherwise today's declaring-type rule. (3) Otherwise `factory #{index} (IStartupCheck)`. Instance and type
  descriptors are unchanged.
- Tests: the three factory forms give the names `X`, `X`, and the declaring type. A null factory gives the line above.
- Autofac and other third-party containers: checks are read from the `IServiceCollection` given to `AddGameServer`. A
  check registered only in a third-party container builder (`ConfigureContainer`) is not seen and does not run. Document
  in PCR platform §17 and consumer guide §12.5 (`Register every IStartupCheck in IServiceCollection; a registration made
  only in a third-party container builder is not seen and never runs.`), and add it to the BC-10 row, column "Who is
  affected". No detection (deferred; zh-pvp uses no third-party container).

**m6 — `ProtobufBinding.cs:56-57`, comment only. Confirmed.** Replace the two comment lines with:

```csharp
// The token is cancelled when the request timeout fired or the client left. Write nothing then: on a timeout the timeout
// policy writes the error envelope, and a body started here first would share the response with it (seen under
// TestServer, T035). A cancellation during the writes below still stops them through the same token.
```

**m7 — BC-6: the obsolete constructor throws `ArgumentException`. Keep it; document it.** The obsolete constructor
validates like the new one, which is consistent, and a visible name that is not a file is a test-setup bug.
- XML doc of the obsolete constructor: add `<exception cref="ArgumentException">A client-visible name is not one of the
  files (also thrown by the 3-argument constructor).</exception>`.
- Release notes BC-6, column "How it surfaces", append: `; both constructors throw ArgumentException ("Client-visible file
  '{name}' is not one of the config files.") when a client-visible name is not a file — a test helper that removes a
  client-visible file must also drop it from clientVisible`.
- zh-pvp: `TestGameConfig.Create` removes only `ConfigSingleParams.json` (server-only) in its tests, so it is unaffected.

**m8 — iOS backup exclusion. Confirmed.**
- `UnityConfigCache.Create`: after constructing the cache (which creates the folder), under
  `#if UNITY_IOS && !UNITY_EDITOR`, call `UnityEngine.iOS.Device.SetNoBackupFlag(folder)` when `Directory.Exists(folder)`.
- The flag goes on the **folder**, not the file: `File.Replace` creates a new file, which would lose a per-file flag, and
  the folder flag covers its contents.
- Why: the cache is re-downloadable, and Apple's storage guidelines exclude such data from iCloud backup.
- Verification is T058 (no iOS build support on this machine). Add a T058 step: "after a save, `gameserver-config/<env>`
  has `NSURLIsExcludedFromBackupKey` (Xcode device container or a log of `NSURL resourceValues`)".
- Docs: PCR unity-sdk and consumer guide §12.8: `On iOS the folder is excluded from iCloud/iTunes backup.`

**m9 — Docs drift. Confirmed.**
- `contracts/sdk-cache.md` §4 row `| delete-then-move fallback (V10), between delete and move | tmp (new version, valid) |`
  becomes `| a runtime whose File.Replace is delete + move (none seen: Windows .NET, Mono Editor; devices T058), between
  the two | tmp (new version, valid), by the load-time recovery |`. The SDK has no delete-then-move code.
- BC numbering across slices: inside a slice's own files (`specs/0NN-*`, the slice's section of a PCR), `BC-n` stays. In
  every text that spans slices (consumer guide, the `v0.7.0` tag message, PCR prose outside a slice section), write
  `0NN/BC-n` (e.g. `015/BC-6`, `014/BC-1`). The merged tag message groups breaking changes under one heading per slice
  with qualified ids.
  - Consumer guide §11 step 5 becomes `… (an undeclared endpoint fails startup, 014/BC-1) … (the one-type overload is
    CS0618, 014/BC-3) … (CS0618, 015/BC-6) … (015/BC-1, 015/BC-2, 015/BC-7)`.
  - §12 uses `015/BC-n` throughout (15 occurrences in the guide; `grep -n "BC-[0-9]" docs/guides/consumer-guide.md`).
  - Line 674 becomes `015/BC-4`, line 748 `014/BC-5`.
  - PCR platform lines 1309, 1310, 1422 (prose in §21) stay unqualified only if they sit inside the slice-015 section;
    otherwise they are qualified.

**m10 — Dead or test-only code. Remove three, reword one.**
- `CatalogBinding.IsDeclared`: remove. The tests assert `Binding(app).Resolve.ShouldBeNull()` / `ShouldNotBeNull()`.
- `ConfigBundle.Full`: remove. A test helper `ConfigBundleTestExtensions.Full(this ConfigBundle, long)` in
  `Platform.Web.Tests` concatenates `Prefix` and `Tail(t)`. V5 byte equality is still asserted against
  `GetConfigResponse.ToByteArray()`.
- `GameConfigLoad.LoadCount` and `_loads`: remove. `GameConfigLoadTests` count through the internal
  `GameConfigLoad(Func<GameConfig>)` constructor with a counting delegate.
- `CatalogHostFailed`, new text: the registration is a host's or a test's, never the module's, and it may be a factory.

  `[catalog {Type}] the registration that replaces the declaration of module {module} could not be resolved: {ExceptionType}: {message}. Fix: correct that registration (host or test), or remove it so the platform builds the declared catalog (FG-018).`

  Update `ConfigDeclarationRunnerTests:154`, `ConfigReportLinesTests:62`, `SampleGame.Tests/ConfigCatalogHostTests:96`,
  PCR platform line 1360, `startup-report.md` §5, `data-model.md` (cat-host) and release notes line 112.

**m11 — `JsonDuplicateProperties` performance. Confirmed.**
- **Lazy set.** A `Frame` keeps the first property name in a field and creates the
  `Dictionary<string, string>(OrdinalIgnoreCase)` only at the second property. Arrays never allocate one.
- **Incremental position.** The walker keeps `(scannedTo, line, lineStart)`. Duplicates are found in document order, so
  `Position` counts `\n` only in `json[scannedTo..index]`. That is O(n) total instead of O(n × duplicates).
- Tests: the existing duplicate tests (positions unchanged), and a 1 MiB file with 10 000 duplicates that is reported
  within the always-on 5× margin of the sample phase test, or opt-in under `GAMESERVER_PERF=1` if that is too slow for
  always-on.

---

## Not changed (confirmed against the code)

- `ValidateServices` keeps skipping marked exceptions. After M1, a marked catalog exception always has its cause in the
  report (M1 invariant test).
- Constructor injection of `IGameConfig` or a catalog into a check stays supported and safe (one "did not run" line).
  `GameConfigStatus` is the recommended way.
- No new BC. BC-2 is widened (M2) and BC-6/BC-10 gain one sentence each (m7, m5). The `LocalFlavorFactory` change (G3)
  and the cache-name rule (U1) touch 0.7.0-only APIs.

## Order for T071

1. m10, m6 (no behaviour change).
2. M2 with m11 (one file, `JsonDuplicateProperties`), then m1, m2, m3 (loader).
3. M1 (runner, phase backstop, kit), G1 (status member, line text), G2 (kit helper), m4, m5 (checks).
4. M3 (compressor, probe, gate).
5. G3 (run extensions, factory), G4 (fixture).
6. U1, m8 (SDK). The `.meta` files are unchanged because there is no new file.
7. Docs per the sections above, m7 and m9 texts, and the release notes.
8. Rerun the affected classes, then T068/T069.
