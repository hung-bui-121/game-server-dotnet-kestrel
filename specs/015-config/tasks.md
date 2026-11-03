# Tasks: Game config — content validated at startup, eager catalogs, config-aware startup checks, a client config version, compressed and persistent delivery (slice 015 config)

**Input**: [plan.md](plan.md) (design, decisions D1–D17, narrowings N1–N11, migration notes BC-1..BC-10),
[spec.md](spec.md) (US1–US6, FR-001..FR-091, BC-1..BC-10, SC-001..SC-009, assumptions D1–D13), [research.md](research.md)
(R1–R18, VERIFY V1–V13), [data-model.md](data-model.md), [contracts/public-api.md](contracts/public-api.md),
[contracts/wire-changes.md](contracts/wire-changes.md), [contracts/sdk-cache.md](contracts/sdk-cache.md),
[contracts/startup-report.md](contracts/startup-report.md). **Where this file and plan.md differ, this file wins** (see
Planning notes at the end).

**Architect decisions applied** (spec Owner confirmations 1–3 stay open for the owner; T002 records their status):
- `config_version` keeps its server meaning everywhere. An **additive** `client_config_version` goes on `ErrorEnvelope`
  (10), `CommandResponse` (9), `SnapshotResponse` (4), `GetConfigResponse` (5) and identity `LoginResponse` (7). This is
  spec D6 as edited by the architect (research R1). It is not the rejected draft default.
- D3 strictness: a duplicate JSON property name fails startup, an integer `kind` is refused, `kind` matches
  case-insensitively.
- Compression happens per request inside the `GetConfig` handler, only when the request sends `Accept-Encoding`. ASP.NET
  `ResponseCompression` is rejected.
- The platform registers declared catalogs in `AddModule` unless the type is already registered (BC-8, R7). A host
  registration made after the module also wins (plan N4).
- The config phase is the first block of `UseGameServer` (R10). Startup checks are created one by one, each in its own
  `try` (BC-10, R9). Provision mode never loads config: checks there see `NotLoaded` and a check that needs config
  gets a warning (BC-9, R8).

**Tests**: mandatory (constitution VI). Each story phase writes its tests before or with the code they cover and ends
green. Couchbase tests skip with a message when `COUCHBASE_CONNSTR` is not set; the full Couchbase suite runs in the
final gate (T069). Opt-in suite `GAMESERVER_PERF=1`: config phase (SC-007), compression (SC-005), cache (SC-006).
Scenario ids (`US1-3`) are the spec's acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USn] description with paths`, then `Done when:` (verifiable) and the FR/SC/BC ids covered.
`[P]` = different files and no open dependency. Record findings as `Note:` lines under the task.

**MVP = US1 (Phases 1–3).** Loader report, declarations, rules and eager catalogs. US1 is the root of FG-018 and the
config phase that US2, US3 and US5 extend. It ends at a shippable checkpoint (T028).

**[VERIFY]**: a runtime or platform fact from research.md to confirm on the pinned versions (.NET SDK 10.0.403, ASP.NET
Core 10.0.14, `Google.Protobuf` 3.36.2, Unity 6000.3.22f1) **before** the task that depends on it. Each check runs in a
throw-away project under the session scratch folder (never committed). Record the result as a `Note:`. When the fact is
wrong, apply the stated fallback and record the deviation (contract, line text, docs).

| Item | Fact | Fallback | Check | Used by |
| --- | --- | --- | --- | --- |
| V1 | `JsonDocumentOptions/JsonSerializerOptions.AllowDuplicateProperties = false` throws on a repeated property at any depth, ordinally, escaped names unescaped, with path/line/byte | own `Utf8JsonReader` walk (path stack + name set per object), same line | T003 (**gate**) | T016 |
| V2 | The `Exception.Data` marker of a throwing `IGameConfig` factory survives `ActivatorUtilities`/factory creation, also one service deep (unwrapped or as inner exception) | match on `ReferenceEquals(ex.Violations, load.Violations)` | T004 (**gate**) | T029 |
| V3 | `ActivatorUtilities.CreateInstance` picks the constructor DI would pick for every framework, sample, fixture and zh-pvp-shaped check | `ActivatorUtilities.CreateFactory` with DI's rule (`IServiceProviderIsService`) | T004 | T029 |
| V4 | The `IServiceCollection` captured in `AddGameServer` lists, after `Build()`, descriptors added later by modules and the host; a lambda factory's `Method.DeclaringType` is (or nests in) the registering type | name the registration `factory #{index} ({ServiceType})` | T004 | T020, T029 |
| V5 | `Prefix(fields 1,3)` ++ `Tail(fields 4,5)` = `GetConfigResponse.ToByteArray()` on Google.Protobuf 3.36.2 (0/1/50 files, empty client version, negative time) | serialize per request with `UnsafeByteOperations.UnsafeWrap` file bytes | T005 (**gate**) | T046, T047 |
| V6 | gzip level / Brotli quality with p95 ≤ 20 ms at 1 MiB and ratio ≤ 30 % for the sample, a 28 KB zh-pvp-shaped and a 1 MiB bundle | lower level; gzip splice (R12 alt. 2) if level 1 misses | T006 (scratch), T050 (final) | T048 |
| V7 | `GetTypedHeaders().AcceptEncoding` parses the wire-changes §5 values, `GZIP`, empty and malformed values without throwing; q defaults to 1 | own RFC 9110 `Accept-Encoding` parser | T006 | T048 |
| V8 | UnityWebRequest (6000.3.22f1) on Editor (Windows), Windows player, Android IL2CPP, iOS IL2CPP: default `Accept-Encoding`, `SetRequestHeader("Accept-Encoding")` accepted, gzip/br decoded before `downloadHandler.data` | SDK gzip sniff (needs V9), else `GameServer:ConfigCompression=false` for that platform | T008 (Editor, Windows; **gate**), T058 (Android, iOS; owner) | T049 |
| V9 | `GZipStream` decompresses on Unity 6 IL2CPP Android/iOS with stripping High | keep that platform uncompressed | T058, only when V8 fails | T049 |
| V10 | `File.Replace(tmp, dest, null)` replaces atomically (rename semantics) on Windows .NET, Unity Mono Editor, Android, iOS; `File.Move` when dest is absent; `Flush(true)` available | delete + move (load-time temp recovery keeps "old or new") | T009 (.NET), T057 (Editor), T058 (devices) | T042 |
| V11 | 1 MiB load and save ≤ 50 ms each outside Unity; SHA-256 survives IL2CPP stripping High | managed CRC-32 (decide before the first release; format v1 checksum field) | T009, T045, T058 | T042 |
| V12 | No framework singleton, outbox consumer, hosted service or GM/analytics component resolves a declared catalog during `Build()` or before the first block of `UseGameServer` | the config phase is already the first block (R10); move any offender behind the phase | T007 | T020, T021 |
| V13 | `SampleTestHost`/`TestHost` with `GameServer:ConfigPath` use the real loader (`TestHost.cs:188`), so a test can point them at a seeded temp folder; `config:` replaces `IGameConfig` with an instance | `LocalFlavorFactory` with a temp content root | T007 | T014, T026, T031, T032 |

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
`test --solution GameServer.slnx -c Release --no-build` (one class: `--project X --filter-class "*Name"`). Couchbase
variables as in `AgentMemories/features/p05-speckit/00_summary.md` ("Environment"; password from the user variable
`COUCHBASE_PW`, never printed). **Never grep recursively over `samples/`** (Unity `Library/` under KestrelDemo). Search
`src/`, `tests/`, `templates/`, `tools/`, `sdk/` and, by project folder only, `samples/SampleHost`,
`samples/SampleGame/SampleGame`, `samples/SampleGame/SampleGame.Adapters`. Open KestrelDemo files by exact path
(`samples/SampleGame/KestrelDemo/Assets/...`). Unity 6000.3.22f1 is
`D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe` (playback engines installed: Android, Windows standalone; **no
iOS**). Unity runs through `sdk/unity/ci/smoke-build.ps1 -Unity "<that path>"` (`-CompileOnly`, `-E2E [-ServerUrl]`,
default = Android APK build). Only when no Editor has KestrelDemo open (no `Temp/UnityLockfile`, no `Unity.exe` process).
SampleHost for Unity runs: `tools/local/run-local.ps1 -Port 5080` (never `-Reset`). Port 5180 belongs to the owner and is
never bound or probed. Scratch probes use 5091/5092. `.specify/feature.json` already points to `specs/015-config`.
Repository docs are in Vietnamese, except the consumer guide (English). Keys, field names, commands and code stay
literal. **Never edit `E:\ProjGits\*`** (first-consumer checks run in temp clones only). No commit is part of any task
(commits: no Claude signature lines, owner rule).

## Phase 1: Setup

- [x] T001 Baseline on branch `015-config` (base `c6b133f`): Debug and Release build of `GameServer.slnx`; full suite in
  memory (no `COUCHBASE_*`) and with `GAMESERVER_TEST_STORE=File`. Record into the scratch folder (not committed):
  (a) SHA-256 of every file under `sdk/unity/com.gameserver.client/Runtime/Generated/` (input of T038);
  (b) the GUID of every `.meta` under `sdk/unity/com.gameserver.client/` (input of T041);
  (c) the public-API snapshot of `sdk/GameServer.Client` (Release, reflection, one sorted line per type and member, the
  014 procedure). Compare it with `tests/GameServer.Client.Tests/TestData/sdk-public-api.txt`: when the c6b133f snapshot
  is a superset (014 additions), replace the committed file with it, so SC-008 compares against `main` before this slice;
  (d) the byte length of today's full `GetConfig` response for `samples/SampleHost/config` and `tests/Shared/config`
  (SampleTestHost, `GetConfigRequest{}`), and the raw bytes of their client-visible files (input of T046, T047, T050);
  (e) today's `config_version` of both configs (the BC-5 "before" value, input of T034, T040).
  Done when: 0 warnings in both configurations, 0 failures; total/passed/skipped, the scratch paths, the (d)/(e) values
  and `git status` (only `specs/015-config/` and the owner's `tasks/` untracked) recorded as a Note (reference for every
  checkpoint and T068). (FR-091, SC-008)
  Note (2026-11-04): base e06ae05 (= c6b133f + docs). Debug and Release builds 0 warnings. Full suite in memory 1866 total / 1770 pass / 96 skip / 0 fail; `GAMESERVER_TEST_STORE=File` the same (COUCHBASE_PW unset). Scratch root `C:\Users\CPU124~1\AppData\Local\Temp\claude\89c3b1457009b599\37a82586-bda6-4849-96d3-fe5091807bc6\scratchpad`.
  Note: (a) SHA-256 of the 15 files under `Runtime/Generated/` → `t001/generated-sha256.txt`; (b) GUIDs of the 30 `.meta` under `sdk/unity/com.gameserver.client/` → `t001/meta-guids.txt`; (c) public-API snapshot at the base: 928 lines, a superset of the committed 875-line file (+53 014 lines, 0 removed) → `tests/GameServer.Client.Tests/TestData/sdk-public-api.txt` replaced (LF, ordinal order).
  Note: (d) full `GetConfig` response (TestHost FakeClock time 1792972800000): SampleHost config 1720 B (client-visible files 1614 B), tests/Shared config 2265 B (2161 B); files and response bytes in `t001/bundle-out/{samplehost,shared}/`. Computed by a scratch program that loads the config with `FileGameConfigLoader` and builds the response exactly like the handler (same ordering), not over HTTP. (e) `config_version`: SampleHost `sample-1+0a58d1cc006c`, tests/Shared `test-1+223d18ea9fa1` (before T015 edits shop.json). `git status` at the start: only the owner's `tasks/` untracked.
- [x] T002 [P] Release notes draft `specs/015-config/release-notes-draft.md` (FR-090), created now and extended by every
  story (T028, T033, T040, T045, T052, T064) before the final pass (T065). Skeleton:
  - summary: fixed FG-018, FG-023, FG-024, FG-019; "no route, persisted document or GM API response shape changes; five
    additive proto fields; no existing field changes meaning (API-03); the Unity package stays 0.7.0";
  - BC-1..BC-10 table (change, who is affected, how it surfaces, fix), filled from the spec's breaking-changes table;
  - the first-consumer migration checklist in the spec's order (bump → BC-1/BC-2/BC-6 → `GameConfigCheck` into
    declarations → version tests FG-019/FG-024 → client persistent cache), with every zh-pvp file:line of the spec's
    "First-consumer migration" table;
  - the rolling-deploy note (spec edge case) and the compatibility table of contracts/wire-changes.md §6;
  - "Owner confirmations": 1 (additive `client_config_version`, CFG-03/SDK-02 cache-key reading), 2 (D3), 3 (BC-9),
    each "applied as architect decision, owner confirmation pending";
  - manual checks table (T008, T051, T057, T058, T066, T067), each "pending";
  - "Known before the tag".
  Done when: the file exists; every FG id and every BC id appears; the checklist lists the 6 markers and the label rule
  of SC-009. (FR-090, SC-009)
  Note: `specs/015-config/release-notes-draft.md` created (English, like the 014 draft): summary, BC-1..BC-10 table, migration checklist with every zh-pvp file:line of the spec, the 6 markers + label rule (SC-009), rolling-deploy note + wire-changes §6 table, owner confirmations 1–3 (pending), manual checks table, "Known before the tag". T008 row already filled (done).
- [x] T003 [P] **[VERIFY] V1 — decision gate for the duplicate-property line** in a throw-away test project referencing
  `src/GameServer.Platform` (for `GameServerJson.Options`): `JsonDocument.Parse(bytes, new JsonDocumentOptions {
  AllowDuplicateProperties = false })` on six shapes: root, nested object, object inside an array, escaped `"\u0061"` vs
  `"a"`, case variants `"a"`/`"A"` (must pass), a manifest-shaped document. Then the same manifest through
  `JsonSerializer.Deserialize<T>` with a copy of `GameServerJson.Options` and `AllowDuplicateProperties = false`.
  Done when: a Note records per shape the exception type, `Message`, `Path`, `LineNumber`, `BytePositionInLine`, and
  whether the escaped duplicate is detected; and the **decision** for T016: STJ option (primary) or the own
  `Utf8JsonReader` walk (fallback), with the source of `{name}`, `{jsonPath}`, `{line}` and `{byte}` of the
  startup-report §2 duplicate line. (FR-002, BC-2)
  Note: V1 **fails on the position**: .NET 10.0.14 `JsonDocument.Parse(…, AllowDuplicateProperties = false)` throws `JsonException` "Duplicate property 'a' encountered during deserialization." for root, nested, in-array, escaped `"\u0061"` vs `"a"` (detected, unescaped), dictionary keys and deep arrays, and accepts `"a"`/`"A"` — but `Path`, `LineNumber` and `BytePositionInLine` are all null. `JsonSerializer.Deserialize<Manifest>` with a copy of `GameServerJson.Options` + `AllowDuplicateProperties = false` gives `Path=$.files Line=4 Byte=21` (0-based line, byte after the value) for the manifest only.
  Note: **Decision for T016 = fallback**: own `Utf8JsonReader` walk (`src/GameServer.Platform/Config/JsonDuplicateProperties.cs`, frame stack + ordinal name set per object) after the unchanged `JsonDocument.Parse` (so the existing parse line keeps the STJ message). `{name}` = `reader.GetString()` (unescaped); `{jsonPath}` = path of the containing object via `ConfigJson.PathOf` (`$`, `$.products`, `$.rows[1]`); `{line}`/`{byte}` = 1-based line of the repeated name and 1-based byte of its opening quote in that line, computed from `TokenStartIndex`. Same line text as startup-report §2.
- [x] T004 [P] **[VERIFY] V2, V3 and V4 — decision gate for check creation** in a throw-away web project referencing
  `src/GameServer.Platform.Web`:
  - (V2) an `IGameConfig` factory throwing a `GameServerStartupException` with `Data["GameServer.Unavailable"] = "game
    config"`; create a check taking `IGameConfig` with `ActivatorUtilities.CreateInstance`, with a lambda factory, and
    through one intermediate service (`Check(IFoo)`, `Foo(IGameConfig)`); record the top exception type and whether the
    marker is found by an `InnerException` walk;
  - (V3) for the check types registered by the SampleHost and Fixture.Host compositions (`DeploymentIdentityCheck`,
    `TestKitReferenceCheck(GameServerOptions, IServiceProvider)`, `ConsoleSinkCheck`, the factory-registered
    `FileStoreCheck` and `CouchbaseSettingsCheck`) plus a primary-constructor internal class, a class with two
    constructors and one with an optional parameter: the constructor `ActivatorUtilities` picks equals DI's;
  - (V4) a singleton registered first in the collection that captures `IServiceCollection` (the `AnalyticsActivation`
    pattern, `src/GameServer.Platform/Analytics/AnalyticsOptions.cs`): after `Build()`, descriptors added later by
    modules and the host are listed; `Method.DeclaringType` of (a) a static lambda in a static extension class (the
    `FileStoreServiceCollectionExtensions` shape), (b) a closure capturing a local, (c) a method group.
  Done when: a Note records each result and the **decisions** for T029 (creation strategy, factory naming rule that
  skips `<>c…` closure classes, marker detection) and T020 (last non-keyed descriptor rule). (FR-030, FR-031, BC-10)
  Note: V2 holds: a throwing `IGameConfig` factory's `GameServerStartupException` reaches the caller **unwrapped** with its `Data` marker for `ActivatorUtilities.CreateInstance`, `CreateFactory`, a lambda factory and one intermediate service (`NeedsFoo(IFoo)`, `Foo(IGameConfig)`); a throwing factory is not cached (second resolve throws again). A missing dependency gives `InvalidOperationException` without marker.
  Note: V3 holds: DI and `ActivatorUtilities.CreateInstance` pick the same constructor for two constructors (`()`/`(IFoo)` → `(IFoo)`), an unsatisfiable longer one (→ `(IFoo)`), an optional parameter (default used) and an internal primary-constructor class. `DeploymentIdentityCheck`, `TestKitReferenceCheck(GameServerOptions, IServiceProvider)`, `ConsoleSinkCheck` have one public constructor each and are created by `ActivatorUtilities` in a real `AddGameServer` + Observability composition; `FileStoreCheck`/`CouchbaseSettingsCheck` are factory registrations (no constructor choice).
  Note: V4 holds: the collection captured first is the same object after `Build()` (read-only) and lists every later module/host descriptor (9 checks counted). `Method.DeclaringType`: static or non-capturing lambda → `X+<>c`, closure → `X+<>c__DisplayClassN_0`, method group → `X`. **Decisions for T029**: create from the descriptor with `ActivatorUtilities.CreateInstance` (type) / the factory / the instance; marker found by `Unavailable.Find` (InnerException walk, V2 shows depth 0); factory name = first declaring type that is not `[CompilerGenerated]` and not `<…`-named (`FileStoreServiceCollectionExtensions`, `CouchbaseServiceCollectionExtensions`, `Registrations`). **Decision for T020**: Declared iff the last non-keyed descriptor of the type is reference-equal to the platform's recorded descriptor (`DeclaredCatalogBindings`).
- [x] T005 [P] **[VERIFY] V5 — decision gate for `ConfigBundle`** in a throw-away project referencing
  `src/GameServer.Platform.Web` (generated `GetConfigResponse` with today's fields 1–4 plus a local copy of the proto
  carrying field 5, compiled in the scratch project): `Prefix{1,3}.ToByteArray()` ++ `Tail{4,5}.ToByteArray()` vs
  `Full{1,3,4,5}.ToByteArray()` for 0, 1 and 50 files, an empty client version, server time −1, 0 and `long.MinValue`;
  parse the concatenation back and compare messages.
  Done when: a Note records byte equality per case, the maximum tail length (expected: 10-byte varint for the time) and
  the **decision** for T046 (prefix + tail, or the per-request fallback). (FR-052, FR-053)
  Note: V5 holds on Google.Protobuf 3.36.2: `Prefix{1,3}.ToByteArray()` ++ `Tail{4,5}.ToByteArray()` is byte-equal to `Full{1,3,4,5}` and parses back equal for 0/1/50 files, empty and non-empty server and client versions, time −1, 0, `long.MinValue`, `long.MaxValue`, 1762000000000. Today's generated type (fields 1–4) parses the 5-field bytes (field 5 kept unknown) and gives today's bytes when the client version is empty. Tail = 11 B for a negative time (tag + 10-byte varint) + 2 + len(client) for a client version under 128 chars (32 B for `test-1+0123456789ab`). **Decision for T046: prefix + tail.**
- [x] T006 [P] **[VERIFY] V6 and V7** in a scratch console (net10.0) and a scratch test referencing
  `src/GameServer.Platform.Web`:
  - (V6) three bundles serialized as `GetConfigResponse` bytes: (a) the SampleHost client-visible set (T001 d), (b) a
    synthetic zh-pvp-shaped bundle of 7 JSON row tables, about 28 KB (synthesized from the friction-log figures; never
    read `E:\ProjGits`), (c) a synthetic 1 MiB bundle of JSON row tables. gzip at levels 1, 6 and 9
    (`ZLibCompressionOptions`), Brotli at quality 1, 4 and 6 (`BrotliCompressionOptions`). 200 iterations after warm-up
    on the reference machine: p50, p95 and ratio;
  - (V7) `HttpRequest.GetTypedHeaders().AcceptEncoding` on a `DefaultHttpContext` for every row of
    contracts/wire-changes.md §5 plus `GZIP`, an empty value, `gzip;q=abc`, `;;` and `*;q=0`.
  Done when: a Note has the V6 table and the chosen `BrQuality` and `GzipLevel` (highest levels with p95 ≤ 20 ms at
  1 MiB and ratio ≤ 30 % for all three bundles: the constants of T048 and PCR platform §1, T061), and the V7 parse
  result per value or the fallback parser decision for T048. (FR-050..FR-052, SC-005)
  Note: V6 (Release, 200 iterations after 20 warm-up, idle machine; ms p50/p95, ratio): sample 1720 B: gzip1 .007/.008 34.6 %, gzip6 .017/.017 27.7 %, gzip9 .025/.027 27.4 %, br1 .009/.009 32.5 %, br4 .243/.290 28.4 %, br6 .547/.673 25.2 %. zh-pvp-shaped 27 623 B: gzip1 .063/.073 24.5 %, gzip6 .222/.232 15.7 %, gzip9 .409/.435 14.5 %, br1 .056/.067 20.0 %, br4 .405/.485 15.2 %, br6 1.268/1.449 13.9 %. Synthetic 1 MiB (1 045 221 B): gzip1 2.1/2.5 23.0 %, gzip6 8.6/8.9 14.0 %, gzip9 17.5/18.5 12.9 %, br1 2.4/2.6 16.0 %, br4 7.2/7.6 14.2 %, br6 17.4/18.3 12.9 % (two reruns: gzip9 p95 18.1, br6 p95 18.1/18.3).
  Note: The documented rule (highest levels with p95 ≤ 20 ms at 1 MiB and ratio ≤ 30 % for all three) gives **GzipLevel = 9, BrQuality = 6** (level 1/quality 1 miss 30 % on the sample bundle), not the expected 6/4. Margin to the 20 ms budget is under 10 %, and gzip 6 / br 4 cost half the time for ~1 point of ratio: T048/T050 should re-measure on the real path and take the documented fallback (lower level) if p95 exceeds 20 ms; recorded here for the owner/T048 decision.
  Note: V7 holds: `GetTypedHeaders().AcceptEncoding` never threw for the §5 rows, `GZIP`, empty, `gzip;q=abc`, `;;`, `*;q=0`, `gzip;q=1.5`, `gzip, , br`, `"gzip"`. q absent = `null` (treat as 1); names keep their case (compare `OrdinalIgnoreCase`). Malformed elements are not dropped but re-tokenised (`gzip;q=abc` → `abc`, `gzip;q=1.5` → `1.5`, `gzip;level=1` → `1`, `"gzip"` → none), so they never name br/gzip and read as identity, as §5 wants. **Decision for T048: no own parser.**
- [x] T007 [P] **[VERIFY] V12 and V13**:
  - (V12) read every constructor in `src/`, `templates/`, `samples/SampleGame/SampleGame`,
    `samples/SampleGame/SampleGame.Adapters` and `samples/SampleHost` (by project folder, never `samples/` as a root)
    that takes `ResourceCatalog`, `IapCatalog`, `BattleCatalog` or `ShopCatalog`; for each, record how it is registered
    and whether it can be resolved during `Build()` or before the first block of `UseGameServer` (`ValidateServices`
    resolves `IOutboxRelay`, which builds every `IOutboxConsumer`);
  - (V13) confirm in `tests/Shared/SampleTestHost.cs` and `src/GameServer.TestKit/TestHost.cs:186-193` that
    `GameServer:ConfigPath` selects the real loader, that a test can point it at a temp folder, and that `config:`
    replaces the registration with an instance (so it bypasses `GameConfigLoad`, R8).
  Done when: a Note lists every catalog consumer with its lifetime (expected: handlers created per command, no eager
  resolver) and the consequences for T014, T020, T021, T026, T031 and T032. (FR-021, FR-033)
  Note: V12 holds. Every catalog consumer in `src/`, `templates/`, `samples/SampleGame/SampleGame`, `SampleGame.Adapters`, `samples/SampleHost` is a command handler created per command with `ActivatorUtilities.CreateInstance` (`CommandRunner.cs:296`): `AdjustResourceHandler`, `CheatGrantHandler` (ResourceCatalog), `IapPurchaseHandler` (IapCatalog, ResourceCatalog), `StartBattleHandler`, `EndBattleHandler`, `SetProgressionHandler` (BattleCatalog [+ ResourceCatalog]), `BuyItemHandler` (ShopCatalog, ResourceCatalog). `EconomyOperations` takes the catalog as a parameter. No singleton, hosted service, outbox consumer, GM or analytics component takes one; the templates have none. Consequences: T020/T021 need no extra ordering; T021 still tests an outbox consumer taking a catalog (V12 fallback not needed).
  Note: V13 holds: `SampleTestHost` sets `GameServer:ConfigPath` before the test's settings callback (`WithSampleConfig`), `TestHost.Compose` uses the loader when `config` is null and the key is set (`useLoader`), else `RemoveAll<IGameConfig>()` + instance (bypasses `GameConfigLoad`). A test points the host at a temp folder with `settings: s => s["GameServer:ConfigPath"] = path` (T014 `SeededConfig.Apply`); T031 tests BC-9 through the loader path.
- [x] T008 **[VERIFY] V8 on Editor and Windows player — decision gate for FR-055 (agent-run Unity)**: a scratch Kestrel
  probe (session scratch folder, port 5091) that logs every request's headers and answers `/plain`, `/gzip` (gzip body,
  `Content-Encoding: gzip`) and `/br` (Brotli body) with a known 64 KB payload, plus `/echo` returning the received
  `Accept-Encoding`. A **temporary** editor script `samples/SampleGame/KestrelDemo/Assets/Editor/Probe015.cs` (created
  for this run, deleted with its `.meta` afterwards, never committed): `Probe015.Run` sends `UnityWebRequest` GETs to each
  URL without and then with `SetRequestHeader("Accept-Encoding", "gzip")`, and logs the response headers and the length
  and SHA-256 of `downloadHandler.data`; `Probe015.BuildWindows` builds a Win64 player of a one-scene probe into the
  scratch folder. Run `"D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe" -batchmode -nographics -projectPath
  samples/SampleGame/KestrelDemo -executeMethod Probe015.Run -logFile <scratch>/probe-editor.log` (editor update loop,
  as `SmokeBuild.EndToEnd`), then the Windows player with `-batchmode -nographics -logFile`.
  Done when: a Note records, for Editor and Windows player: the default `Accept-Encoding`; whether `SetRequestHeader` is
  accepted; whether gzip and br bodies arrive decoded (SHA-256 equal to the payload); and the **decision** for T049:
  V8 holds on both → no SDK code; a platform advertises an encoding without decoding → R13 fallback (SDK gzip sniff,
  V9 on devices in T058) or the rollback switch. The probe, its `.meta` and the scratch build are gone; `git status
  samples/` shows no change. (FR-055, US5-6)
  Note (2026-11-04, agent-run, Unity 6000.3.22f1, no Editor had KestrelDemo open): probe server on **127.0.0.1:5092** (not 5091: session rule "5080 or 5092"), 64 KB payload (sha256 1d5d2d8f…f42b, gzip 16 903 B, br 15 827 B). Temporary files `Assets/Editor/Probe015.cs` and, for the player, `Assets/Probe015/{Probe015Runner.cs,Probe015Player.cs,Probe015.unity}` (a runtime script and scene are needed for a player build; task text named only the editor script).
  Note: **Editor (Windows)** and **Windows player (Mono, development build, `-batchmode -nographics`)** give the same results: default `Accept-Encoding: deflate, gzip` (libcurl 8.20.0-DEV, `X-Unity-Version: 6000.3.22f1`); `SetRequestHeader("Accept-Encoding", "gzip")` accepted (sent as `gzip`); `/gzip` decoded before `downloadHandler.data` (65 536 B, SHA-256 equal, `Content-Encoding: gzip` still in the headers); `/br` → `result=ConnectionError`, `error=Unrecognized content-encoding` (not advertised, so the server never sends it). **Decision for T049: V8 holds on both → no SDK code**; Unity clients on these platforms get gzip from the server by default. Android/iOS stay T058.
  Note: cleanup: probe files, their `.meta`, the scene and the folder deleted; the Windows build in scratch deleted; the probe server stopped. The Windows build changed 3 tracked files (`Assets/Settings/PC_RPAsset.asset`, `UniversalRenderPipelineGlobalSettings.asset`, `ProjectSettings/ProjectSettings.asset` Standalone batching) → restored with `git checkout`; the project was reopened once with `-buildTarget Android -quit` so `Library` is back on the Android target. `git status samples/` shows no change. Logs: scratch `v8/probe-editor.log`, `v8/probe-player.log`, `v8/probe-server.log`.
- [x] T009 [P] **[VERIFY] V10 and V11 outside Unity** in a scratch console (net10.0) and a scratch netstandard2.1 library
  with C# 9: `File.Replace(tmp, main, null)` with `main` present (content and timestamps after), `File.Replace` with
  `main` absent (exception type), `File.Move(tmp, main)`, `FileStream.Flush(flushToDisk: true)`; a child process killed
  between flush and replace leaves `tmp` complete and `main` old; `SHA256.Create()` plus read and write of a 1 MiB
  container, 50 iterations, p95.
  Done when: a Note records the semantics, the exception types and the timings (expected ≤ 50 ms each), and the
  fallbacks chosen for T042. The Unity Mono Editor part rides on T057, devices on T058. (FR-062, FR-066, SC-006)
  Note: V10 holds on Windows .NET 10 (netstandard2.1 / C# 9 library `CacheOps`): `File.Move(tmp, main)` when main is absent; `File.Replace(tmp, main, null)` with main present replaces content, removes tmp, keeps main's creation time and takes tmp's last-write time; `File.Replace` with main absent throws `System.IO.FileNotFoundException`; `FileStream.Flush(flushToDisk: true)` available. A child process killed after flush and before replace leaves tmp complete (hash valid, new version) and main old and valid. Fallbacks for T042: none needed on Windows .NET (Mono Editor in T057, devices in T058).
  Note: V11 (1 MiB + SHA-256 trailer, 50 iterations after 5 warm-up): on C: (user profile / `%TEMP%`, where `persistentDataPath` lives on Windows) save p95 11.5 ms (Flush(true) + Replace), load p95 2.8 ms; on D: (repository drive) save p50 52.9 / p95 70.2 ms (dominated by `Flush(true)`), load p95 5.1 ms. SC-006 (≤ 50 ms) holds on C:, not on D:: T045 must state the drive of the reference measurement. SHA-256 on IL2CPP stays T058.

**Checkpoint**: baseline counts known; V1, V2–V4, V5 and V8 (Editor, Windows) decided with their consequences; V6, V7,
V9–V13 confirmed or their fallbacks recorded; release notes skeleton in place.

## Phase 2: Foundational (behaviour-neutral)

Nothing in this phase changes what a host accepts: no module declares a file or a catalog yet, the loader still stops
at its first fault, and versions are unchanged. Each task ends with the full suite green with the T001 counts plus the
new tests (T015 moves one test case, Note).

- [x] T010 Core declarations in a new file `src/GameServer.Core/Config/ConfigDeclarations.cs` (research R5,
  contracts/public-api.md §1.2): `ConfigFileDeclaration` (`Required`/`Optional`, `Name`, `IsOptional`, `Rules`),
  `ConfigRuleCheck` delegate, `ConfigRule` (`Name`, `Check`), `ConfigCatalogDeclaration.Of<T>` (`CatalogType`, `Files`,
  `Build`), `ConfigContext` and `ConfigProblems` with internal constructors (`ConfigProblems` keeps the ordered entries
  `(location, problem, fix, isWarning)` for the platform formatter), `ConfigLocationKind`, `ConfigLocation`
  (`WholeFile`, `Row(n ≥ 1)`, `Key`, `Path`, `InFile`, `Equals`). Argument checks: a plain non-empty file name (no `/`,
  `\`, `..`) → `ArgumentException`; a non-empty rule name; `Of<T>` needs at least one file. Edit
  `src/GameServer.Core/Modules/ModuleDescriptor.cs` (`ConfigFiles`, `ConfigCatalogs`, default `[]`, XML doc of
  public-api §1.1) and `src/GameServer.Core/GameServer.Core.csproj` (`InternalsVisibleTo` `GameServer.Platform` and
  `GameServer.Platform.Tests`, Planning note 6).
  Tests in a new file `tests/GameServer.Platform.Tests/Config/ConfigDeclarationsTests.cs`: factories and flags, rule
  order kept, `Of<T>().CatalogType == typeof(T)`, `Build` calls the delegate with the context, `Row(0)` throws,
  `ConfigLocation` equality and `InFile`, `ErrorCount` counts errors and not warnings.
  `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` ARC-01 rule green unchanged (Core free of ASP.NET Core and
  Protobuf).
  Done when: green; nothing reads the new members yet. (FR-010, FR-011, FR-012, FR-020)
  Note: `src/GameServer.Core/Config/ConfigDeclarations.cs` (declarations, `ConfigContext`/`ConfigProblems` with internal constructors, internal `ConfigProblem` entries, `ConfigLocation`, internal `ConfigNames` plain-name rule), `ModuleDescriptor.ConfigFiles`/`ConfigCatalogs`, Core IVT to `GameServer.Platform` and `GameServer.Platform.Tests`. `Of<T>` deduplicates its file list. ARC-01 unchanged. `ConfigDeclarationsTests`: 13 tests. **T034**: the test builds a `GameConfig` with the 4-argument constructor (add to the caller list).
- [x] T011 `src/GameServer.Core/Config/ConfigJson.cs` (after T010; public-api §1.2): `TryGetObject`, `TryGetArray`,
  `RequiredString`, `RequiredInteger`, `OptionalInteger`, `RequireObject`, `PathOf`. Each value helper takes a trailing
  optional `string? fix = null` (Planning note 7); a missing fix gives the default "set '{property}' to {a type}". Texts
  are the startup-report §4 fragments: `'{property}' is missing`, `'{property}' is null`, `'{property}' must be a {type},
  found {kind}`, integer range texts ("must be an integer of at least {min}", "from {min} to {max}"). "JSON integer" =
  `TryGetInt64` (`1.0`, `1e3`, `"5"` are problems, research R4).
  Tests in a new file `tests/GameServer.Platform.Tests/Config/ConfigJsonTests.cs`: each helper × (missing, null, wrong
  kind, `1.0`, `1e3`, `"5"`, below min, above max, ok); unknown properties never reported; `PathOf` for `$.a.b`,
  `$.items['key with.dot']`, `$.rows[2]` and a key with a quote; texts asserted literally.
  Done when: green; the texts equal the startup-report §4 fragments. (FR-011, FR-017)
  Note: `ConfigJson` with the trailing `string? fix = null` on every value helper **and** on `TryGetObject`, `TryGetArray`, `RequireObject` (they report too). Texts: `'p' is missing`, `'p' is null`, `'p' must be {an array|an object|a string}, found {object|array|string|number|boolean}`, `'p' must not be empty` (non-empty strings, not in §4), integers `'p' must be an integer[ of at least N| of at most N| from A to B], found {raw JSON}` (a string quoted, `1.0`, `1e3`, objects/arrays by kind, values over 40 chars cut), row `the row must be an object, found {kind}`; default fix `set 'p' to {type}`. A non-object owner reads as missing. 33 tests.
- [x] T012 [P] Report lines and the unavailability marker (after T010): new internal
  `src/GameServer.Platform/Config/ConfigReportLines.cs` (every template of data-model §5.3–§5.5 and §5.4 check lines; it
  strips one trailing `.` from `{problem}` and `{fix}`; `fileRef` adds ` (config/env/{env})` for an overlaid file) and
  `src/GameServer.Platform/Config/Unavailable.cs` (`Mark(exception, value)`, `Find(exception)` over the whole
  `InnerException` chain; values `game config`, `catalog {TypeName}`, `provision`; data-model §3.4).
  Tests in new files `tests/GameServer.Platform.Tests/Config/ConfigReportLinesTests.cs` (each template once with the
  literal text of contracts/startup-report.md §4–§7; trailing-dot rule; overlay reference) and `UnavailableTests.cs`
  (marker at depth 0, 1, 2; no marker → null; unrelated `Data` keys ignored).
  Done when: green; no caller yet. (FR-011, FR-022, FR-031, FR-053)
  Note: `ConfigReportLines` (all §5.3–§5.5 templates + the check lines; the not-run-c line takes the type name from the marker) and `Unavailable` (`Mark`, `Find`, `Catalog(type)`, `CatalogTypeName`). 7 + 3 tests.
- [x] T013 `GameConfigLoad` (after T012; research R8, data-model §3.1): new internal
  `src/GameServer.Platform/Config/GameConfigLoad.cs` (thread-safe `Lazy` over `FileGameConfigLoader.Load(root, env)`;
  `Mode` `Serve` (default) | `NotLoaded`; `GetOrThrow()`: Loaded → config; Failed → a **new**
  `GameServerStartupException(violations)` marked `game config` on every call; NotLoaded → marked `provision`, no disk
  access). `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`: register `GameConfigLoad` and change the
  `IGameConfig` registration (`GameServerBuilder.cs:192-197`) to `sp => sp.GetRequiredService<GameConfigLoad>().GetOrThrow()`.
  The `TestHost` instance override (`TestHost.cs:192`) is unchanged.
  Tests in a new file `tests/GameServer.Platform.Tests/Config/GameConfigLoadTests.cs`: one loader run across 10 resolves
  on 4 threads (counted through a temp folder access probe or an internal load counter); a failing load gives the same
  violations and the marker on each resolve; `NotLoaded` with a missing folder gives the provision text, not a loader
  line. `StartupValidationTests` and `TestKitHostTests` green unchanged.
  Done when: green; startup report texts unchanged. (FR-001 "once per start", FR-032 groundwork)
  Note: `GameConfigLoad` (`Lazy` ExecutionAndPublication, internal `LoadCount`, only `GameServerStartupException` is turned into violations; other exceptions keep today's behaviour). Provision text: `[config] the game config is not loaded in provision mode. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).` **Deviation (needed by the plan's layering):** `GameServer.Platform.csproj` gains `InternalsVisibleTo` `GameServer.Platform.Web` (the config phase uses the Platform internals) and `GameServer.Platform.Web.Tests` (T020 binding tests). 3 tests; startup texts unchanged.
- [x] T014 [P] Seeded config helper for host tests (after T007; V13): new file `tests/Shared/SeededConfig.cs` (compiled
  into the 7 test projects that link `tests/Shared/*.cs`, Couchbase and Observability included, so it uses BCL types only): copies
  `{AppContext.BaseDirectory}/config` to a fresh temp folder; `With(file, json)`, `Without(file)`,
  `Manifest(Func<JsonObject, JsonObject>)`, `EnvOverlay(env, files, manifest)`; `Path` for `GameServer:ConfigPath`;
  `Dispose` deletes the folder.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/SeededConfigTests.cs`: an unchanged seeded copy starts
  `SampleTestHost` with the same `config_version` as the default folder; `Without("battle.json")` fails with the
  existing missing-file line; the temp folder is gone after dispose.
  Done when: green in every project that links `tests/Shared` (full Release build). (test support for FR-001, SC-001,
  SC-003, SC-004)
  Note: `tests/Shared/SeededConfig.cs` (`With`, `Without`, `Manifest`, `EnvOverlay`, `Path`, `BasePath`, `Apply(settings)`), BCL only; full Release build of the 7 projects green. `SeededConfigTests`: 3 tests.
- [x] T015 [P] Shared test config ready for content rules (Planning note 5): remove the `broken` item (grant resource
  `unknown_item`) from `tests/Shared/config/base/shop.json`, which the `sample.shop` rule of T024 refuses. Move the
  `("broken", 1, "UNKNOWN_ITEM")` case of `tests/SampleGame.Tests/ShopTests.cs`
  (`Invalid_purchases_are_rejected_without_side_effects`) to a host-free test in a new file
  `tests/SampleGame.Tests/BuyItemHandlerTests.cs`: `BuyItemHandler(ShopCatalog.Load(c), ResourceCatalog.Load(c))` over
  a `TestGameConfig.FromJson` config holding the broken item, run with `CommandContextDouble` → `UNKNOWN_ITEM`, no
  writes. Check every other file of `tests/Shared/config/base` against data-model §2.2 by hand (resources, iap incl. the
  128-character `analytics_size_probe_…` product id, battle, shop) and record the result.
  Done when: green; `ShopTests` keeps its other five cases; the Note lists each shared file as "passes the 015 rules" or
  the edit made. (FR-015, FR-016, FR-070 groundwork, BC-1 in our own suite)
  Note: `broken` removed from `tests/Shared/config/base/shop.json`; `ShopTests` keeps its other five cases. `BuyItemHandlerTests` (host-free, `CommandContextDouble`): rejected with `UNKNOWN_ITEM`, `CheckCommit` empty, no inventory write; the double records the staged wallet debit (the runner discards staged changes of a rejection), so "no writes" is asserted as "rejected, nothing committed".
  Note: shared files vs data-model §2.2: `resources.json` passes (4 unique ids, kinds, maxQuantity ≥ 1, displayName); `iap.json` passes (4 keys, the probe product id is exactly 128 chars, grants gem/gold/sword within maxQuantity); `battle.json` passes (maxStage 3, battleHours 24, stages 1–3 once each, rewards/drops valid); `shop.json` passes after the edit (`huge` price 4611686018427387904 is an integer ≥ 1); `counter.json` has no rule. Phase 2 checkpoint: 1928 total / 1832 pass / 96 skip / 0 fail (+62: 59 Platform foundation tests, 3 SeededConfig, the moved case).

**Checkpoint**: Release build 0 warnings; full suite green with the T001 counts plus the new tests (minus the moved
case); startup behaviour unchanged.

## Phase 3: US1 — A bad config value stops the host with the file and row; every catalog is built before traffic (P1, FG-018) MVP

Goal: the loader reports every fault; modules declare files, rules and catalogs; one engine runs them in the first block
of `UseGameServer` and in the test kit with the same lines; economy, IAP and the sample game use it; no lazy catalog
factory is left in the framework or the sample.
Independent test: T026 (seeded faults, 7-line and 3-line reports), T027 (catalogs once, 0 parses), T028 (kit = host).
Depends on: Phase 2.

- [x] T016 [US1] Loader collects every fault (after T003, T013; research R3, contracts/startup-report.md §2):
  `src/GameServer.Platform/Config/FileGameConfigLoader.cs` — `Load` collects violations and throws one
  `GameServerStartupException` when any exist (BC-3). Private helpers return violations instead of throwing and keep
  their texts. Manifests and `.json` files are parsed once with duplicates refused (T003 decision); the manifest is bound
  from the parsed document. A manifest parse fault skips only that manifest's file list. The new duplicate line and the
  parse line carry path, line and byte. New internal `GameConfig.OverlaidFiles` (names whose bytes came from
  `config/env/<env>`). `ComputeVersion` and the `GameConfig` constructor are **not** touched here (US3, T034).
  Tests in `tests/GameServer.Platform.Tests/Config/FileGameConfigLoaderTests.cs`: every existing test green with
  unchanged texts; two missing files + one invalid JSON file = exactly the 3 lines of startup-report §2; base and env
  manifest faults in one start; `clientVisible` not in `files` = one line per name; duplicate property in the manifest,
  the root, a nested object, an array element and an escaped name (per V1), case variants accepted; overlay origin
  recorded; `TestGameConfig.FromDirectory` gives the same lines.
  Done when: green; no existing loader line text changed (`git diff` of the test file shows additions only). (FR-001,
  FR-002, FR-003, US1-7, US1-8, BC-2, BC-3)
  Note: loader collects every fault (one exception); a manifest fault (parse or duplicate) skips its file list; label and `clientVisible ⊂ files` are checked against the listed names (a listed file that failed to read keeps one line); `clientVisible` lines are distinct and in manifest order. New internal `GameConfig.OverlaidFiles` and `OverlayEnvironment` (env name for the `(config/env/{env})` reference). `ComputeVersion` and the constructor untouched. 12 tests added (existing 6 unchanged); `TestGameConfigTests.From_directory_reports_every_loader_line_like_the_host_loader` added.
- [x] T017 [P] [US1] Required files from declarations (after T010; data-model §2.1):
  `src/GameServer.Platform/Registry/ModuleRegistry.cs` `ValidateConfig` uses the effective required set
  `RequiredConfigFiles` ∪ required `ConfigFiles` ∪ files of `ConfigCatalogs` not declared optional by that module; one
  line per (module, file) with the existing text; a file named in both lists is one declaration. Add an internal
  accessor of the per-module required set for the engine and the kit.
  Tests in `tests/GameServer.Platform.Tests/Registry/ModuleRegistryTests.cs`: each source of a required file, the union
  without duplicates, an optional declaration of a catalog file → no line, the text unchanged.
  Done when: green. (FR-010, FR-014, ARC-06)
  Note: `ModuleRegistry.RequiredConfigFiles(descriptor)` (internal static) and internal `ValidateConfig(config)` returning the lines (used by `Validate` and the kit); a file declared optional by the module is not required through its catalog; `RequiredConfigFiles` still wins over an optional declaration. 4 tests added.
- [x] T018 [US1] Declaration engine (after T011, T012, T017; research R6): new internal files
  `src/GameServer.Platform/Config/ConfigDeclarationRunner.cs`, `ConfigDeclarationResult.cs` and `CatalogBinding.cs`
  (`Declared` | `Provided(resolve)`). Parse cache (one `JsonDocument` per `.json` file on first `Json(file)`, disposed at
  the end); rules per module and declaration order for present files (absent optional → skipped; absent required →
  skipped silently, the registry reports it); a throwing rule → rule-throw line, the others run; per-file error count;
  catalogs: duplicate type → cat-dup line, first kept; skip (no line) when a listed file is absent or has a rule error
  from any module; Declared → build in `try` (problems → catalog lines; null without problems → cat-null; throw →
  cat-throw); Provided → resolve in `try` (cat-host line). Lines through `ConfigReportLines` (T012), overlay reference
  from `GameConfig.OverlaidFiles`.
  Tests in a new file `tests/GameServer.Platform.Tests/Config/ConfigDeclarationRunnerTests.cs` with test modules: rule
  order; optional absent → no run; required absent → no rule line; throwing rule → one line, others run; a warning is not
  an error; two modules on one file → both lines naming each module; catalog skipped on a rule error (no line); build
  problem / throw / null → one line each, other catalogs built; duplicate type → one line naming both modules; Provided
  resolved and its failure line; each file parsed once (count through a wrapper `IGameConfig`); `Json(file)` of an
  absent file → `KeyNotFoundException` caught as rule-throw.
  Done when: green. (FR-011, FR-012, FR-013, FR-014, FR-020, FR-022, FR-023, US1-5, US1-6)
  Note: engine as R6. A problem located `InFile(other)` names and counts against that file; a rule that throws after reporting keeps its problems plus the throw line; a skipped catalog (file absent or rule error) is added to `FailedCatalogs` (store gives the marked exception). `ConfigDeclarationResult.RuleCount` counts rules run, `CatalogCount` the first declaration of each type. 12 tests.
- [x] T019 [US1] Test kit call (after T018; research R17, contracts/public-api.md §5): new files
  `src/GameServer.TestKit/ConfigDeclarationCheck.cs` and `src/GameServer.TestKit/ConfigDeclarationReport.cs`
  (`Check(config, params IGameModule[] modules)`: a `ModuleRegistry` of the modules, the required-file lines of
  `ValidateConfig`, the engine with every catalog Declared; `Errors`, `Warnings`, `Passed`, `Catalog<T>`,
  `TryGetCatalog<T>`, `ThrowIfFailed()`, `AddCatalogsTo(services)`). Extend the internal allow-list of
  `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` with exactly the engine types and the registry member
  the kit uses (Planning note 8).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/ConfigDeclarationCheckTests.cs`: test modules with a rule
  error, a warning, a built catalog and a failed one; `ThrowIfFailed` message lists every error; `Catalog<T>` of a failed
  catalog throws naming its lines; `AddCatalogsTo` then resolve returns the built instance; a missing required file gives
  the registry line; no unit-test framework, host or store referenced (existing kit architecture rules green).
  Done when: green. (FR-018, BC-8 fix)
  Note: `ConfigDeclarationCheck`/`ConfigDeclarationReport`; `Catalog<T>()` lists every error line when not built. `TestKitArchitectureTests` allow-list extended with exactly `ConfigDeclarationRunner`, `ConfigDeclarationResult`, `CatalogBinding` and `ModuleRegistry::ValidateConfig` (members list replaces the single-member constant). 5 tests.
- [x] T020 [US1] Catalog registration and store (after T004, T007, T010; research R7, data-model §3.3): new internal
  `src/GameServer.Platform/Config/ConfigCatalogStore.cs` (`Get(type)`: built → the instance; the phase ran and the
  catalog failed → `GameServerStartupException` with one line, marked `catalog {Type}`; the phase never ran →
  `InvalidOperationException` with the R7 text) and `src/GameServer.Platform/Config/DeclaredCatalogBindings.cs`
  (captures the `IServiceCollection`; after `Build()`, per type, Declared when the last non-keyed descriptor is the
  platform's, else Provided; T004 V4 decision). `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`: register both
  in `AddGameServer`; in `AddModule`, right after `module.ConfigureServices`, `TryAdd` one singleton descriptor per
  catalog declaration (`sp => store.Get(type)`) and record it.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/ConfigCatalogBindingTests.cs` (composition only, no
  `UseGameServer`): Declared when only the module registers; Provided when the host registers an instance before
  `AddModule` and when it registers one after; resolving a Declared catalog before the phase → the R7
  `InvalidOperationException` text; a `ServiceCollection` built from `module.ConfigureServices` alone → the DI "No service
  for type" error naming the type (BC-8 surfacing).
  Done when: green. (FR-021, BC-8)
  Note: `ConfigCatalogStore` (states not-run / config-failed / published; failed-catalog text `[catalog {Type}] was not built: its config files are missing or have errors (see the [module …] lines). Fix: correct the lines reported for its files (FG-018).` marked `catalog {Type}`; config-failed text marked `game config`) and `DeclaredCatalogBindings` (registers the platform descriptor only when no non-keyed descriptor of the type exists = TryAdd, records the first per type). 5 tests (both host orders, R7 text, DI "No service for type").
- [x] T021 [US1] The config phase (after T013, T016, T018, T020; research R10, contracts/startup-report.md §1): new
  internal `src/GameServer.Platform.Web/Hosting/ConfigStartup.cs` called as the **first block** of `UseGameServer` in
  `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs` (replacing lines 38-49): (1) resolve
  `IGameConfig` (catch → violations; `SetConfigVersion` as today), (2) `registry.Validate(options, config)` unchanged,
  (3) when the config loaded: the engine with the bindings of T020, built catalogs into `ConfigCatalogStore`, failed
  types marked, Provided types resolved once; warnings handed to `RunStartupChecks` so they are logged once with the other
  startup warnings. Step (4) bundle and size check comes in T046/T047, step (5) status in T030. `ValidateServices`: an exception carrying the
  marker (`Unavailable.Find`) is skipped (its cause is already reported). Provision and export modes do not call the
  phase.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/ConfigStartupPhaseTests.cs` (test modules on `SampleTestHost`;
  a `modules` callback that calls `SampleTestHost.DefaultModules` first, the 014 trap): a rule error and
  `GameServer:Gate:CacheSeconds=99` → both lines in one report; a warning → logged once, the host starts; a config that
  does not load → loader lines only (no rule or catalog line); an outbox consumer test double taking a failed catalog →
  no extra line from `ValidateServices` (V12). `LateEndpointValidationTests` and the 014 endpoint-check tests green
  (the passing start still builds the endpoint set once).
  Done when: green. (FR-003, FR-013, FR-014, FR-021, FR-022, US1-1, US1-5, US1-7)
  Note: `ConfigStartup.Run` is the first block of `UseGameServer`; warnings join the host warnings logged by `RunStartupChecks`; `ValidateServices` skips a marked `GameServerStartupException` from the relay (also removes today's duplicate config lines for a consumer taking `IGameConfig`). 5 tests (rule + gate in one report, warning logged once, loader lines only, consumer on a failed catalog adds no line, consumer gets the phase's instance); `LateEndpointValidationTests` and the 014 endpoint tests green in the full suite.
- [x] T022 [P] [US1] Economy rules and catalog (after T019, T021; data-model §2.2, startup-report §4): new file
  `src/GameServer.Modules.Economy/EconomyConfigRules.cs` (`EconomyConfigRules.Resources`, rule `economy.resources`,
  through `ConfigJson`); `src/GameServer.Modules.Economy/EconomyModuleDefinition.cs` declares
  `Required(resources.json, EconomyConfigRules.Resources)` and `Of<ResourceCatalog>(c => ResourceCatalog.Load(c.Config),
  resources.json)` and drops `RequiredConfigFiles`; `src/GameServer.Modules.Economy.Adapters/EconomyModule.cs:50` drops
  the lazy factory. `ResourceCatalog.Load` unchanged (FR-024).
  Tests in a new file `tests/GameServer.Platform.Web.Tests/EconomyConfigRulesTests.cs` through `ConfigDeclarationCheck`
  with the economy module: every economy row of startup-report §4 (no array, row not an object, missing / null / wrong
  type per property, duplicate id on the later row naming the first, unknown kind, integer kind, `"CURRENCY"` accepted,
  `maxQuantity` 0 / −1 / `1.0` / `"5"`, unknown property accepted, the zh-pvp shape CASH/GOLD passes).
  Done when: green; the full suite green (the shared config passes). (FR-015, FR-017, FR-024, BC-1)
  Note: `EconomyConfigRules.Resources` (`economy.resources`) and a public `ResourceReferences` helper (lenient read of `resources.json`, reused by the IAP and sample rules; not in contracts/public-api.md, additive). Descriptor declares the file and `Of<ResourceCatalog>`; `EconomyModule` lazy factory removed. `EconomyConfigRulesTests`: 20 tests (fix texts not given by §4 chosen: id `give every resource a non-empty id (ECO-07)`, displayName `give every resource a display name (ECO-07)`).
- [x] T023 [P] [US1] IAP rules and catalog (after T019, T021): new file `src/GameServer.Modules.Iap/IapConfigRules.cs`
  (`iap.products`; reads `resources.json` leniently: references are checked only against well-formed rows, and nothing
  when that file is absent); the descriptor in `src/GameServer.Modules.Iap/IapModel.cs` (`Describe`, :121-126) declares
  `Required(iap.json, IapConfigRules.Products)` and `Of<IapCatalog>(…)`, drops `RequiredConfigFiles`;
  `src/GameServer.Modules.Iap.Adapters/IapModule.cs:31` drops the lazy factory.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/IapConfigRulesTests.cs` (kit, economy + IAP modules): key
  without store, unknown store, product id of 128 (passes) and 129 characters, empty product id, `grants` missing /
  empty, unknown resource (path line), quantity 0, `1.0`, above `maxQuantity`, a malformed `resources.json` row → no
  reference line for that resource, `{"products": {}}` passes (zh-pvp).
  Done when: green; the full suite green. (FR-016, FR-017, FR-024, US1-2, BC-1)
  Note: `IapConfigRules.Products` (`iap.products`, `MaxProductIdLength = 128`, stores case-sensitive `google`/`apple`); key line and grant lines both reported for one product; product not an object / grant not an object have their own lines. Descriptor + `Of<IapCatalog>`, lazy factory removed. 17 tests.
- [x] T024 [P] [US1] Sample game rules and catalogs (after T015, T019, T021; Planning note 4): new file
  `samples/SampleGame/SampleGame/SampleGameConfigRules.cs` (`sample.battle`, `sample.shop`, data-model §2.2; the battle
  `xp` text of the startup-report §4 example); `SampleGameModuleDefinition.Describe` in
  `samples/SampleGame/SampleGame/ShopHandler.cs` (:71-77) declares both files with their rules and
  `Of<BattleCatalog>`/`Of<ShopCatalog>`, drops `RequiredConfigFiles`;
  `samples/SampleGame/SampleGame.Adapters/SampleGameModule.cs:30-31` drops the two lazy factories.
  Tests in a new file `tests/SampleGame.Tests/SampleConfigRulesTests.cs` (kit, economy + sample game): battle
  (`maxStage` < 1, `battleHours` absent / 169, duplicate or out-of-range stage, missing stage row, `xp` as a string,
  reward with an unknown resource, quantity 0) and shop (`items` not an object, `amount` 0, grant with an unknown
  resource — the old `broken` item, grant quantity above `maxQuantity`); `samples/SampleHost/config` and
  `tests/Shared/config` pass.
  Done when: green; `ShopTests`, `BattleTests` and `SampleGameEndToEndTests` green unchanged. (FR-070, US1-3, US6-1)
  Note: `SampleGameConfigRules` (`sample.battle`, `sample.shop`; maxStage/stage capped at `int.MaxValue` like the catalog's `int`; more than 10 missing stages give one summary line so a mistyped maxStage cannot flood the report — not in data-model §2.2). Descriptor declares both files and catalogs; `SampleGameModule` lazy factories removed. `SampleConfigRulesTests`: 13 tests incl. `samples/SampleHost/config` and `tests/Shared/config` passing; `ShopTests`, `BattleTests`, `SampleGameEndToEndTests` green.
- [x] T025 [US1] Rules never weaker than `Load` (after T022–T024; FR-024, plan risk "rules diverge from Load"): new
  file `tests/SampleGame.Tests/CatalogLoadAgreementTests.cs`: 500 generated configs per file (fixed seed, recorded),
  valid ones and single-mutation variants (type swaps, removals, extremes); for every config the kit reports with 0
  errors, `ResourceCatalog.Load`, `IapCatalog.Load`, `BattleCatalog.Load` and `ShopCatalog.Load` do not throw and the
  kit's catalog is built.
  Done when: green; a deliberately weakened rule in a scratch copy makes the test fail (Note). (FR-024)
  Note: seed 15025 (+ file-name length per file), 500 configs per file, 1 in 5 valid, others one mutation (removal, renamed property, null/0/−1/long.MaxValue/int.MaxValue+1/1.5/1.0/1e3/"5"/""/unknown id/true/[]/{}, duplicated array element). **Precision:** judged on the rule (and required-file) lines only — a `Load` that throws already shows as a catalog line, so "kit passed" alone would be tautological (first weakening run proved it). Weakened rule (duplicate-id check disabled, working-tree edit, restored byte-identical) → the test fails on `resources.json` with the duplicated `gem` row; restored → green.
- [x] T026 [US1] Host report matrix (after T014, T021–T024): new file `tests/SampleGame.Tests/ConfigStartupReportTests.cs`
  (sample game composition, `SeededConfig`): each seeded fault gives exactly one line — `resources.json` duplicate id,
  missing `maxQuantity`, `maxQuantity` as a string, unknown kind; `iap.json` unknown resource, quantity 0, key without
  store, duplicate product key (the loader line, Planning note 11); `battle.json` wrong type; a game-module rule problem
  and a catalog build that throws (test modules); duplicate property; missing file; invalid JSON. The six faults over
  three files plus `GameServer:Gate:CacheSeconds=99` give exactly the 7 lines of startup-report §4; two missing files and
  one invalid JSON file give exactly the 3 lines of §2 and no rule or catalog line; a game rule on `resources.json`
  ("CASH and GOLD must be currencies" shape) and the economy rule both report, each naming its module; provision mode
  (the `GameServerCommandLineTests.RunProvisionAsync` pattern) and export mode with a seeded rule fault report no rule
  line.
  Done when: green; every expected line is asserted literally. (FR-001..FR-003, FR-013, FR-014, SC-001, US1-1, US1-2,
  US1-3, US1-5, US1-6, US1-7, US1-8, US1-9)
  Note: `ConfigStartupReportTests` (19 tests, every line literal) + `SeededFaults` (14 seeded faults shared with T028). The "two missing files + invalid JSON" lines follow the manifest order of the shared config (shop.json before battle.json; the contract example lists battle first). The 7-line test uses a seeded resources.json with 5 rows (row 3 `"999"`, row 4 duplicate `gold`, sword kept at row 5 so no reference line appears) and an iap.json with `gems_100`, `google:gems_100` (quantity 0), `google:starter_pack` (`shield`). Provision (fake provisioner) and export modes with a seeded rule fault: exit 0, no rule line on stderr.
- [x] T027 [US1] Catalogs before traffic (after T026): new file `tests/SampleGame.Tests/ConfigCatalogHostTests.cs`:
  with a valid config every declared catalog is built exactly once before the first request (a counting test catalog;
  the `ResourceCatalog` instance seen by two handlers in two requests is the same object); the first command performs 0
  `GetFile*`/`GetFileJson`/`GetFileBytes` calls (a counting `IGameConfig` wrapper passed as `config:`); a host-registered
  `ResourceCatalog` instance, before and after `AddModule`, is the one handlers get, a throwing host factory gives the
  cat-host line, and a seeded `resources.json` fault is still reported; a second module declaring `ResourceCatalog` gives
  the cat-dup line naming both modules.
  Done when: green. (FR-021, FR-023, SC-002, US1-4)
  Note: `ConfigCatalogHostTests` (7 tests): counting catalog built once before the first request; a spy handler (`ReplaceHandler` of `cheat.grant`) sees the same `ResourceCatalog` instance in two requests; a counting `IGameConfig` (`config:`) sees 0 file reads for a cheat grant and a shop buy after start; host instance before/after `AddModule` is what handlers get; throwing host factory → exactly the cat-host line; host instance with a seeded fault → the rule line; second module → the cat-dup line.
- [x] T028 [US1] Kit = host, release notes, checkpoint (after T019, T026, T027): new file
  `tests/SampleGame.Tests/ConfigDeclarationParityTests.cs`: for every non-loader seeded fault of T026,
  `ConfigDeclarationCheck.Check(TestGameConfig.FromDirectory(seeded), modules).Errors` equals the host's lines in the same
  order. Release notes: FG-018 section, BC-1, BC-2, BC-3 and BC-8 rows with the real line texts, FG-018 rows of the
  consumer-impact table. Full suite in memory and with `GAMESERVER_TEST_STORE=File`.
  Done when: green; counts recorded against T001. (FR-018, FR-090, SC-001, US1-10)
  Note: `ConfigDeclarationParityTests` (11 tests: 10 rule/catalog seeded faults + one 4-file order test): kit `Errors` = host lines, same order. Release notes: FG-018 section, BC-1/BC-2/BC-3/BC-8 rows with real texts, FG-018 consumer rows.
  Note: checkpoint: Debug and Release builds 0 warnings. Full suite in memory 2063 total / 1966 pass / 96 skip / 1 fail = the known flaky `TelemetryTests.A_command_is_one_trace_from_http_to_the_runner_with_the_request_id` (passed 3/3 rerun alone); `GAMESERVER_TEST_STORE=File` 2063 / 1967 / 96 / 0. vs T001: +197 tests, skips unchanged.

**Checkpoint (MVP)**: a bad config value stops the host with module, file and location; every declared catalog is built
once before traffic; the kit reports the host's lines; no lazy catalog factory left in economy, IAP and the sample game.

## Phase 4: US2 — A startup check never loses the combined report, and it reads the config safely (P2, FG-023)

Goal: every check is created in its own isolation; checks that need config learn its state from `GameConfigStatus`;
provision mode never loads config.
Independent test: T032 (the four-check scenario of SC-003), T031 (provision `NotLoaded`).
Depends on: US1 (T021: the phase publishes the status; same file `GameServerApplicationExtensions.cs`).

- [x] T029 [US2] Checks created one by one (after T004, T012, T021; research R9, contracts/startup-report.md §7): new
  internal `src/GameServer.Platform.Web/Hosting/StartupCheckRegistrations.cs` (captures the `IServiceCollection` in
  `AddGameServer`); `CheckAll(IServiceProvider, StartupCheckMode)` in `GameServerApplicationExtensions.cs` (internal enum
  `Serve` | `Provision`) enumerates the non-keyed `IStartupCheck` descriptors in registration order and creates each in
  its own `try` (instance / `ActivatorUtilities.CreateInstance` / factory; T004 decisions), then runs `Check` in its own
  `try`. Classification by the marker (`Unavailable.Find`): `game config` → not-run line; `catalog {Type}` → not-run-c
  line; `provision` → the provision **warning**; other creation exception → created line; `Check` throwing an unmarked
  `GameServerStartupException` → its violations; anything else → the existing failed line. Checks are not disposed.
  `RunStartupChecks` passes `Serve`.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/StartupCheckIsolationTests.cs` (provider built from a
  collection, no host): each creation kind (instance, type, lambda factory, method-group factory, static lambda in a
  static extension class) gives the expected name on failure; one throwing constructor does not hide the other checks;
  each marker kind gives exactly one line of the right kind; each check is created once per `CheckAll`.
  `GameServerCommandLineTests.A_throwing_startup_check_joins_the_report_in_serve_and_provision_mode` green unchanged.
  Done when: green. (FR-030, FR-031, SC-003 "every creation-failure kind gives exactly one line", US2-2, BC-10)
  Note: `StartupCheckRegistrations` (captured in `AddGameServer`, non-keyed `IStartupCheck` descriptors in registration order; creation instance / `ActivatorUtilities.CreateInstance` / factory, a `TargetInvocationException` is unwrapped; name = instance or implementation type, factory = first declaring type that is neither `[CompilerGenerated]` nor `<…`-named, fallback `factory #{index} (IStartupCheck)`). `CheckAll(services, StartupCheckMode)`: creation and `Check` each in their own `try`; a marked exception gives the not-run / not-run-c line through `ConfigReportLines`, an unmarked `GameServerStartupException` from `Check` its violations, else the created / failed line. Checks are not disposed.
  Note: **Decision beyond the table:** in `Provision` mode every marked exception is the provision **warning** (not only the `provision` marker), so provision never fails because of config (D4). `StartupCheckIsolationTests`: 15 tests (type, missing dependency, static and capturing lambda, method group, static extension lambda, instance, unmarked violations, each marker at creation and in `Check`, provision mode, created once per run, keyed checks ignored). `A_throwing_startup_check_joins_the_report_in_serve_and_provision_mode` green unchanged.
- [x] T030 [US2] Config status (after T021, T029): new public `src/GameServer.Platform/GameConfigStatus.cs`
  (`GameConfigStatus`, `GameConfigState`, `TryGetConfig`; internal `Publish` of an immutable snapshot through a volatile
  reference; contracts/public-api.md §2) registered as one instance in `AddGameServer`; `ConfigStartup` step 5 publishes
  `Loaded(config, declarationsPassed)` (no required-file, rule or catalog error line in this start; warnings do not
  count) or `Failed` (data-model §3.2).
  Tests in a new file `tests/GameServer.Platform.Tests/GameConfigStatusTests.cs`: `NotLoaded` before publication, no
  member throws in any state, `TryGetConfig` per state, a second `Publish` is refused (`InvalidOperationException`).
  Done when: green; the host-level assertions are in T032. (FR-032, US2-3)
  Note: public `GameConfigStatus` / `GameConfigState` in `src/GameServer.Platform/GameConfigStatus.cs` (internal constructor, internal `PublishLoaded(config, declarationsPassed)` / `PublishFailed()`, one volatile immutable snapshot, second publication → `InvalidOperationException`), registered as one instance in `AddGameServer`. `ConfigStartup` step 5: `Failed` when `IGameConfig` threw, else `Loaded` with `DeclarationsPassed` = no required-file line and no rule/catalog line; to know the required-file lines the phase now calls `registry.Validate(options, config: null)` then `registry.ValidateConfig(config)` (same order and texts as before). `GameConfigStatusTests`: 5 tests.
- [x] T031 [US2] Provision mode never loads config (after T029, T030; research R8, BC-9):
  `src/GameServer.Platform.Web/Hosting/ProvisioningRunner.cs` sets `GameConfigLoad.Mode = NotLoaded` before anything
  else and calls `CheckAll(services, Provision)`; provision warnings stay `warning: …` lines on stderr.
  Tests in `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs` (provision part, `RunProvisionAsync`
  pattern, `SampleTestHost` uses the loader path, V13): a check taking `IGameConfig` → the provision warning on stderr
  and the exit code is unaffected (`FakeProvisioner` called); a status check sees `NotLoaded`; a missing config folder
  gives no loader line in provision; with a `config:` instance (TestHost override) the check runs (R8 bypass, recorded
  for PCR §17).
  Done when: green. (FR-014, FR-032, US2-3, BC-9)
  Note: `ProvisioningRunner` first sets `GameConfigLoad.Mode = NotLoaded` and **also** `ConfigCatalogStore.NotLoaded()` (gap filled: without it a check taking a declared catalog got the R7 `InvalidOperationException`, a creation error that failed provisioning; now `Get` throws marked `provision`). Then `CheckAll(services, Provision)`. 3 tests in `GameServerCommandLineTests` (check taking `IGameConfig` → exactly the warning line on stderr, exit 0, provisioner called; status check sees `NotLoaded` with a missing config folder and stderr empty; `config:` instance of the test host bypasses the loader and the check runs = R8 bypass for PCR §17) + 1 in `StartupCheckHostTests` (check taking a declared catalog → provision warning, exit 0).
- [x] T032 [US2] FG-023 host scenarios (after T014, T029–T031): new file
  `tests/GameServer.Platform.Web.Tests/StartupCheckHostTests.cs` on `SampleTestHost` with `SeededConfig`:
  - SC-003: `Without("battle.json")` + `GameServer:Gate:CacheSeconds=99` + `NeedsConfigCheck(IGameConfig)`,
    `ThrowingCheck` (constructor throws `InvalidOperationException("boom")`), `StatusCheck(GameConfigStatus)` (adds
    `status: {State}`) and `PlainCheck` (adds `plain ran`) → exactly the six lines of startup-report §7 in that order,
    none duplicated;
  - a valid config: `StatusCheck` sees `Loaded`, `Config` is the instance `IGameConfig` resolves to
    (`ReferenceEquals`), `DeclarationsPassed` true; with a seeded rule error (test module) it is false;
  - a check taking a declared catalog that failed → the not-run-c line;
  - a zh-pvp-shaped check (resolves `IGameConfig` inside `Check`, catches `GameServerStartupException`, returns) with a
    missing file → exactly the loader line and `[gate]` (the zh-pvp `GameConfigStartupTests` 2-line shape);
  - order probe: a check observes that the catalogs are built and the status is published when it runs.
  Done when: green; lines asserted literally. (FR-030..FR-033, SC-003, US2-1, US2-2, US2-3, US2-4)
  Note: `StartupCheckHostTests` (8 tests, lines literal): SC-003 six lines in the §7 order; valid config → `Loaded`, same `IGameConfig` instance, `DeclarationsPassed` true; seeded rule error → `Loaded` + false; a warning keeps it true; check taking a failed declared catalog → rule line + not-run-c; zh-pvp shape (resolve + catch inside `Check`) → loader line + `[gate]` only; order probe sees the catalog instance and `Loaded`; provision + catalog → warning.
- [x] T033 [US2] Docs in code, release notes, checkpoint (after T032): `src/GameServer.Platform/StartupChecks.cs` XML doc
  of `IStartupCheck` per contracts/public-api.md §2 (created and run in its own isolation, must not throw, not
  disposable, reads config through `GameConfigStatus`; a check taking `IGameConfig` is safe but reported as not run
  when config fails); XML docs of `GameConfigStatus`. Release notes: FG-023 section, BC-9 and BC-10 rows, the zh-pvp
  FG-023 row (`GameConfigStartupTests…keeps_the_report_complete…` keeps passing with 2 lines). Full suite.
  Done when: green; counts recorded. (FR-034 code part, FR-090, US2-5 code part)
  Note: XML doc of `IStartupCheck` (created and run in its own isolation, once per start, never disposed, must not throw, reads config through `GameConfigStatus`; a check taking `IGameConfig` or a catalog is safe but reported as not run, and skipped with a warning in provision) and of `GameConfigStatus`/`GameConfigState`. Release notes: FG-023 section, BC-9/BC-10 rows with the real lines, zh-pvp FG-023 row. Checkpoint (run after T029–T034 while the proto was being edited): 2106 total, the only failure = WireGen `verify` reading the proto under edit (green after T038).

**Checkpoint**: the four-check scenario gives exactly its lines; provision mode is config-free with warnings; the status
is published once and never throws.

## Phase 5: US3 — Clients download config only when what they receive changes (P3, FG-024, FG-019 version)

Goal: two versions computed like the host everywhere; five additive `client_config_version` fields; `GetConfig`
accepts either version; the SDK keys its cache on the client version.
Independent test: T034 (version matrix), T036 (carriage), T039 (SDK).
Depends on: Phase 2 and T016 (same file `FileGameConfigLoader.cs`). Independent of US2.

- [x] T034 [US3] Two versions (after T016; research R2, data-model §1.3–§1.4, contracts/public-api.md §2): new public
  `src/GameServer.Platform/Config/GameConfigVersions.cs` (`GameConfigVersionPair`, `Compute`, domain tags, length
  prefixes, label hashed); in `src/GameServer.Platform/Config/FileGameConfigLoader.cs` the new `GameConfig(label,
  files, clientVisible)` constructor computing both, `ClientConfigVersion`, `[Obsolete]` on the 4-argument constructor
  (still works, computes the client version from its inputs) and on `ComputeVersion`, `Load` on the new constructor
  (keeps `OverlaidFiles`); `src/GameServer.Core/Config/IGameConfig.cs` gains `ClientConfigVersion` (regular member).
  Migrate every caller of the obsolete API **in the same change** (CS0618 = error): `src/GameServer.TestKit/TestGameConfig.cs:39`,
  `tests/GameServer.Platform.Tests/Infrastructure/RunnerHarness.cs:49`,
  `tests/GameServer.Platform.Tests/Registry/ModuleRegistryTests.cs:176`,
  `tests/GameServer.Platform.Web.Tests/DevProfileMiddlewareTests.cs:212`, and the `IGameConfig` test wrappers of
  T018/T027 (add the member). `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs:42` asserts
  `harness.Config.ConfigVersion` instead of the literal `test+000000000000` (Planning note 9). Only the obsolete-path
  tests use a local `#pragma warning disable CS0618`.
  Tests in a new file `tests/GameServer.Platform.Tests/Config/GameConfigVersionsTests.cs`: the data-model §1.4 matrix
  with `TestGameConfig` **and** the loader on temp folders (base + env overlay): server-only edit, client-visible edit,
  visibility added in base, added through the overlay, removed, label change, list permutations and duplicates, two loads
  of the same bytes, all-visible separation (server ≠ client); a known-answer vector computed in the test from the §1.3
  formula; `TestGameConfig` = loader for the same folder; the obsolete constructor gives the same client version.
  `tests/GameServer.Platform.Web.Tests/TestGameConfigTests.cs` updated.
  Done when: green; a search of `src`, `tests`, `templates` for `ComputeVersion(` and the 4-argument `new GameConfig(`
  finds only the pragma'd obsolete-path tests. (FR-040, FR-041, FR-042, FR-045, FR-047, SC-004, US3-1..US3-4, US3-7,
  BC-5, BC-6)
  Note: public `GameConfigVersions.Compute` + `GameConfigVersionPair` (`src/GameServer.Platform/Config/GameConfigVersions.cs`, data-model §1.3 byte for byte; a client-visible name that is not a file → `ArgumentException`); `GameConfig(label, files, clientVisible)` computes both; the 4-argument constructor is `[Obsolete]` (keeps the given server version, computes the client version) and so is `ComputeVersion`; `IGameConfig.ClientConfigVersion` (regular member). Loader and `TestGameConfig` use the new constructor. Migrated: `RunnerHarness.cs:49`, `ModuleRegistryTests.cs:19` (static `EmptyConfig`, target-typed `new(...)`, not in the task list) and `:178`, `ConfigDeclarationsTests.cs:129`, `DevProfileMiddlewareTests.cs:212`; `CommandRunnerTests.cs:42` asserts `_h.Config.ConfigVersion`; wrappers `ConfigDeclarationRunnerTests.CountingConfig` and `ConfigCatalogHostTests.CountingConfig` gained the member.
  Note: `GameConfigVersionsTests` (12 tests: §1.4 matrix with `GameConfig` and the loader on temp folders incl. visibility added through the env overlay and the same bytes overlaid, list order/duplicates, separation, length-prefix ambiguity, known-answer vector from the §1.3 formula, refused visible name, loader = constructor, obsolete path under a local `#pragma warning disable CS0618`). `TestGameConfigTests` also assert the client version (= loader, = host). Search: `ComputeVersion(` and the 4-argument constructor appear only in the loader definition and the pragma'd test. New values (BC-5): SampleHost `sample-1+0a58d1cc006c` → server `sample-1+8a37f29ee2dc`, client `sample-1+9b25b8b6bc0b`; tests/Shared server `test-1+9b2cd1009ff1`, client `test-1+4c5423c2dad9`.
- [x] T035 [US3] Proto fields and server sites (after T034; contracts/wire-changes.md §1–§3):
  `proto/gameserver/core/v1/core.proto` (`ErrorEnvelope` 10, `CommandResponse` 9, `SnapshotResponse` 4,
  `GetConfigResponse` 5, comments of wire-changes §1) and `src/GameServer.Modules.Identity.Adapters/Protos/identity.proto`
  (`LoginResponse` 7). Server sites: `src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs` (snapshot `:43`; config
  `:64` and the `not_modified` comparison `:66` against either version, ordinal);
  `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs:301` (every login response: guest, provider, link);
  `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs:222` (OK, rejection, replay; the instance's client
  version next to `execution.ConfigVersion`); `src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs:55` (every
  player error, middleware ones included). GM responses, receipts and `ICommandContext` unchanged.
  Done when: Release build 0 warnings; the existing suite green (fields are additive). (FR-043, FR-044, FR-054)
  Note: five fields with the wire-changes §1 comments (`GetConfigRequest`/`GetConfigResponse` comments updated). Sites: login (`IdentityModule.LoginResponse`, every login path), snapshot and `GetConfig` (`not_modified` = request equals the client **or** the server version, ordinal), `CommandEndpointFactory.BuildResponse(execution, clientConfigVersion, …)` (OK / rejection / replay; the public `CommandExecution` record is unchanged), `ErrorResponseWriter` (every player error). GM, receipts, `ICommandContext` unchanged.
  Note: **Root-cause fix outside the task list:** `CommandPipelineTests.Timeout_after_commit_returns_503…` failed after the field change. The 503 body was a `CommandResponse` followed by the `ErrorEnvelope`: the handler wrote its body with the already-cancelled `RequestAborted` token, TestServer buffered it, then the timeout policy appended the envelope. The new field 9 `client_config_version` collides with `ErrorEnvelope.details` (9), so the parse failed where it used to pass by luck. `ProtobufBinding.WriteAsync` now calls `context.RequestAborted.ThrowIfCancellationRequested()` first, so a timed-out request gets the timeout envelope only. The existing test fails without the fix.
- [x] T036 [US3] Carriage tests (after T035): new file `tests/SampleGame.Tests/ConfigVersionCarriageTests.cs` (sample
  game composition): guest login, snapshot, command OK, business rejection (`INSUFFICIENT_FUNDS`), replay, a player
  error envelope from a handler and one from middleware (no session), `GetConfig` full and `not_modified` → every
  `config_version` = server version and every `client_config_version` = client version; the receipt document and
  `ReceiptView.config_version` and the GM command response `configVersion` = server version; `GetConfig` with the client
  version → `not_modified`, with the server version → `not_modified`, with the T001 (e) value (v0.6.0 format), empty and
  garbage → full bundle. Update only the literal or byte expectations of `tests/GameServer.Platform.Web.Tests/SnapshotTests.cs`,
  `IdentityLoginTests.cs` and `tests/SampleGame.Tests/ConfigAndCheatTests.cs` that the new field changes.
  Done when: green. (FR-043, FR-044, FR-047, SC-004, US3-5, US3-6)
  Note: `ConfigVersionCarriageTests` (8 tests): login, snapshot, command OK / replay / `INSUFFICIENT_FUNDS`, error envelope from an endpoint (`receipts/unknown`, 404) and from middleware (no session, 401), `GetConfig` full / by client / by server → both fields right; receipt document and `ReceiptView.config_version` = server; T001 (e) value, `garbage`, `TEST-1+0000` → full bundle (empty is the `full` case). **Deviation (location):** the GM command response check (`configVersion` = server, no `clientConfigVersion`) is `GmAdjustTests.A_gm_command_response_carries_the_server_config_version_only` in `GameServer.Gm.Tests`, where the GM host fixture lives. `SnapshotTests`, `IdentityLoginTests` and `ConfigAndCheatTests` needed no change (no literal or byte expectation touched the new fields).
- [x] T037 [P] [US3] Metric and readiness (after T035; research R16): `src/GameServer.Platform/Diagnostics/PlatformMetrics.cs`
  `SetClientConfigVersion` and gauge `client_config_version_info{version}` (set by `ConfigStartup` next to
  `SetConfigVersion`); `src/GameServer.Platform.Observability/HealthChecks.cs` readiness data `clientConfigVersion` next
  to `configVersion`.
  Tests: `tests/GameServer.Observability.Tests/TelemetryTests.cs` metric-name list gains the gauge (Planning note 8) and
  checks its value; readiness data in `tests/GameServer.Observability.Tests/HealthAndShutdownTests.cs` has both keys;
  `config_version_info` keeps its name, label and server value.
  Done when: green. (FR-044, US3-5)
  Note: `PlatformMetrics.SetClientConfigVersion` + gauge `client_config_version_info{version}`, set by `ConfigStartup` next to `SetConfigVersion`; `ConfigHealthCheck` data `clientConfigVersion`. `TelemetryTests` metric list has the gauge and checks both gauge values (server / client); `HealthAndShutdownTests.The_config_check_data_names_the_server_and_the_client_config_version` (GM health detail data).
- [x] T038 [US3] SDK generated code (after T035; 014 seam): `"$HOME/.dotnet10/dotnet.exe" run --project
  tools/GameServer.WireGen -c Release -- generate --config sdk/GameServer.Client/wiregen.json`, then `verify` with the
  same config, and `verify` for `samples/SampleGame/wiregen.json` and `tests/ConsumerFixture/tools/wiregen.json`.
  Done when: only `sdk/unity/com.gameserver.client/Runtime/Generated/gameserver/core/v1/Core.cs` and
  `Runtime/Generated/Identity.cs` differ from T001 (a) (one property per new field); every `.meta` unchanged; the three
  `verify` runs exit 0; `tests/GameServer.WireGen.Tests/CommittedOutputTests.cs` green. (FR-043 SDK part, FR-065)
  Note: `generate` wrote only `Runtime/Generated/Identity.cs` and `Runtime/Generated/gameserver/core/v1/Core.cs` (2 written, 4 unchanged); `sha256sum -c` against T001 (a): exactly those two differ, every `.meta` unchanged; `verify` exit 0 for `sdk/GameServer.Client/wiregen.json`, `samples/SampleGame/wiregen.json` and `tests/ConsumerFixture/tools/wiregen.json`; `CommittedOutputTests` green in the full suite.
- [x] T039 [US3] SDK tracks the client version (after T038; research R14, contracts/public-api.md §6):
  `sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs`: `TrackConfig(server, client)` at login, snapshot,
  command and error envelope (effective = client when not empty, else server); `ServerConfigVersion` unchanged; new
  `ClientConfigVersion`; `ConfigOutdated` and `ConfigVersionChanged` follow the effective version (today's dedupe);
  `RefreshConfigAsync` sends `Config.Version` and stores the files under the response's effective version; XML docs.
  Tests in a new file `tests/GameServer.Client.Tests/ClientConfigVersionTests.cs`: against the sample host
  `ClientConfigVersion` = host client version and `ServerConfigVersion` = server version; refresh stores under the client
  version, a second refresh → `not_modified`; a stub transport replaying responses without the new fields (old server) →
  falls back to `config_version`, behaviour as today; event raised once per new version; a cache filled from host A
  given to a client of host B with only a server-only file edited (`SeededConfig`) → no `ConfigVersionChanged`, refresh
  `not_modified`. `tests/GameServer.Client.Tests/SdkPublicApiTests.cs` green (additive only); `sdk/GameServer.Client`
  builds as C# 9 / netstandard2.1 with 0 warnings.
  Done when: green. (FR-046, FR-065, SC-004, SC-008, US3-1, US3-2, BC-4)
  Note: `TrackConfig(server, client)` at login, snapshot, command and error envelope (effective = client when not empty); `ServerConfigVersion` (last non-empty `config_version`), new `ClientConfigVersion`, `ConfigOutdated` and `ConfigVersionChanged` on the effective version (dedupe kept); `RefreshConfigAsync` stores under the effective version of the response. **Addition:** on `not_modified` with a cache held under another version (e.g. the server version, which the server also accepts) the cache is re-keyed once with its own files (`Store(version, Config.Files)`, no download); without it `ConfigOutdated` would stay true for good. XML docs of the members and `IConfigCache`. `SdkHarness.StartAsync(settings)` and `NewClient(config:)` added.
  Note: `ClientConfigVersionTests` (4 tests: both versions, store under the client version, second refresh `not_modified`, one event; old-server stub (field stripped from login/snapshot/config) → falls back to the server version; cache from host A + host B with only `iap.json` (server-only) edited → no event, not outdated, refresh false; re-key without download). `SdkPublicApiTests` green (additive); the SDK builds as C# 9 / netstandard2.1 with 0 warnings.
- [x] T040 [US3] Release notes and checkpoint (after T036–T039): FG-024 and FG-019 (version) sections; BC-4, BC-5 (with
  the T001 (e) value and the new one) and BC-6 rows; zh-pvp rows: FG-024 pinned test fails by design (delete or invert),
  FG-019 pinned test does **not** fail by itself (rewrite: client version unchanged, server version changed),
  `tests/ZhPvp.Tests/Support/TestGameConfig.cs` one-line fix `return new GameConfig(label, files, clientVisible);`;
  rolling-deploy note and compatibility table checked against T036/T039. Full suite.
  Done when: green; counts recorded. (FR-090, SC-009)
  Note: release notes: FG-024 and FG-019 (version) sections, BC-4 / BC-5 (before/after values of both configs) / BC-6 rows, zh-pvp rows (FG-024 pinned test fails by design; FG-019 pinned test does not fail by itself; `TestGameConfig.cs` one-line fix), rolling-deploy note and compatibility table checked against `ConfigVersionCarriageTests` and `ClientConfigVersionTests` (old-server stub; `GetConfig` accepts the server version). Counts: see T045.

**Checkpoint**: both versions identical between host and kit; every player response carries both; old and new SDKs keep
working against this server.

## Phase 6: US4 — Client config survives app restarts (P4, FG-019 persistent cache)

Goal: `PersistentConfigCache` keeps one client config version on disk, atomically, never throws for file problems;
a Unity helper roots it under `Application.persistentDataPath`.
Independent test: T042 (format, faults, corruption), T044 (second client downloads nothing).
Depends on: US3 (T039: `ClientConfigVersion` is the cache key). Can run in parallel with US5.

- [x] T041 [US4] SDK seam and `.meta` files (after T039; research R15): new files
  `sdk/unity/com.gameserver.client/Runtime/Core/AssemblyInfo.cs` (`[assembly: InternalsVisibleTo("GameServer.Client.Tests")]`)
  and `Runtime/Core/CacheFileOps.cs` (internal seam: open temp, write, flush, replace, move, read, exists, delete;
  default = real file system), each with a **hand-written** `.meta` (fresh GUID, the importer block of the existing
  `Runtime/Core/*.cs.meta`); the `.meta` of `PersistentConfigCache.cs` and `UnityConfigCache.cs` are written with their
  files (T042, T043). Never let Unity create a package `.meta` (014 trap: `file:` package).
  Tests: `tests/GameServer.Architecture.Tests/UnityPackageMetaTests.cs` green (add the new GUIDs where the test records
  them; T001 (b) GUIDs unchanged); the test project reaches the internals through its `extern alias sdk` reference.
  Done when: green; SDK builds as C# 9 / netstandard2.1 with 0 warnings. (FR-065, FR-067 seam, SC-008)
  Note: `Runtime/Core/AssemblyInfo.cs` (`InternalsVisibleTo("GameServer.Client.Tests")`) and `Runtime/Core/CacheFileOps.cs` (internal; virtual create-folder / exists / read / open-temp / write / flush(true) / replace / move / delete; `Real` default) with hand-written `.meta` (importer block of `GameServerClient.cs.meta`, fresh `uuid4` GUIDs). `TestData/unity-package-guids.txt` gains the 5 new GUIDs (015 section); the T001 (b) GUIDs are unchanged; `UnityPackageMetaTests` green.
  Note: **Deviation (planning note 10):** a fifth new file `Runtime/Core/ConfigCacheFormat.cs` (+ `.meta`) holds the container format v1 (serialize / strict parse), so `PersistentConfigCache` keeps only load/store (one class, one job; together they were ~330 lines). GUIDs: AssemblyInfo `3e6695fab49449a29555de642e16ecf5`, CacheFileOps `bf7415ae02eb4819bcbd4c71c53c1c09`, ConfigCacheFormat `6c6977f541934d66b0d8df4f353e1eb8`, PersistentConfigCache `b40d338fb610404aaa389a71317a5e30`, UnityConfigCache `861c6bc8a2c54ea788e6dd388633d9d8`.
- [x] T042 [US4] `PersistentConfigCache` (after T009, T041; contracts/sdk-cache.md §2–§6, data-model §6): new file
  `Runtime/Core/PersistentConfigCache.cs` (+ `.meta`): `FileName`, constructor loading per §3 (main file, temp recovery,
  every validation of §2, `LoadProblem`, never throws for I/O or format problems), `Store` per §4 (memory first, temp
  write + `Flush(true)`, replace or move per the T009 decision, `WriteProblem` set or cleared, identical store skipped,
  temp deleted best effort), `Folder`, `Version`, `Files`; XML doc with the threading rule. C# 9 and netstandard2.1 only,
  no UnityEngine.
  Tests in a new file `tests/GameServer.Client.Tests/PersistentConfigCacheTests.cs` (the sdk-cache §7 list): round trip;
  empty bundle; restart (second instance on the folder); a fault through `CacheFileOps` at open-temp, at each write, at
  flush, at replace and at move; the temp file truncated at every byte offset of a small container; corrupt main file
  (zero length, truncated at every offset, one flipped bit in header, payload and hash, unknown format version, wrong
  magic, trailing bytes, duplicate names, invalid UTF-8, a locked file) → empty, `LoadProblem` set, 0 exceptions;
  unwritable folder (read-only attribute / ACL) → `Store` does not throw, values in memory, `WriteProblem` set;
  path-like names (`../x.json`, `/etc/passwd`, `C:\x`, `a/b.json`) round-trip as data and the folder holds only the two
  known names.
  Done when: green. (FR-060..FR-064, FR-066, FR-067, SC-006, US4-2..US4-6)
  Note: `PersistentConfigCache` per sdk-cache §2–§6: load the main file, else a valid temp file (main absent or unusable); `LoadProblem` = reason of the main file (kinds `truncated`, `trailing bytes`, `not a config cache file`, `unknown format version {n}`, `integrity check failed`, `invalid UTF-8`, `duplicate file name`, `invalid content`, `unreadable: {type}`). Store: memory first (own snapshot of the dictionary), temp written in 3 parts + `Flush(true)`, `File.Replace` when main exists else `File.Move` (T009), `WriteProblem` set/cleared, temp deleted best effort; an identical store is skipped only when the main file holds the version (a version recovered from the temp file is written again = promoted). A version or name the format cannot hold (empty, > 256 / > 1024 bytes, lone surrogate) stays in memory with a `WriteProblem`. Lengths are checked against the bytes left (a 2 GiB length allocates nothing). Only `IOException`, `UnauthorizedAccessException`, `NotSupportedException` and `SecurityException` are caught.
  Note: `PersistentConfigCacheTests` (31 incl. the opt-in perf case): round trip, empty bundle, restart, one version kept, identical store skipped; fault at open-temp / each of the 3 writes / flush / replace (old on disk, new in memory, temp gone) and at the first move (then the next store succeeds); temp cut at every byte with and without a main file; main cut at every byte; one flipped bit at every byte (reserved bytes 6–7 ignored, as specified); wrong magic, format 2, trailing byte, hash, zero length; duplicate names, invalid UTF-8, empty name; huge length; locked file (`unreadable: IOException`); read-only main file; folder with an ACL deny (Unix: mode 0555; helper `tests/GameServer.Client.Tests/ReadOnlyFolder.cs`); path-like names round-trip and the folder holds only `gameserver-config.bin`; the layout equals a hand-built v1 container.
- [x] T043 [P] [US4] Unity helper (after T042): new file `Runtime/Unity/UnityConfigCache.cs` (+ `.meta`) under
  `#if UNITY_5_3_OR_NEWER`: `Create(environmentName)` → `new PersistentConfigCache(Path.Combine(Application.persistentDataPath,
  "gameserver-config", environmentName))`, refusing a name that is not plain.
  Done when: `sdk/GameServer.Client` builds outside Unity with 0 warnings and no UnityEngine reference in its assembly
  metadata (Note); the Unity compile is checked in T057. (FR-060, US4-7)
  Note: `Runtime/Unity/UnityConfigCache.cs` (+ `.meta`) under `#if UNITY_5_3_OR_NEWER`: `Create(environmentName)` refuses empty, `.`, `..`, path separators and invalid file-name characters. Outside Unity, `GameServer.Client.dll` (Release, netstandard2.1) references only `netstandard` and `Google.Protobuf` and contains no `UnityConfigCache` type (scratch metadata reader). The Unity compile stays in T057.
- [x] T044 [US4] End to end against the sample host (after T039, T042): new file
  `tests/GameServer.Client.Tests/ConfigCacheEndToEndTests.cs` (counting transport): client A with a
  `PersistentConfigCache` on a temp folder logs in and refreshes (one download); client B on the same folder has
  `Config.Version` and `Files` before any call, logs in → `ConfigOutdated` false, no `ConfigVersionChanged`,
  `RefreshConfigAsync` returns false, 0 files received; a second host whose only edit is `iap.json` (server-only in the
  shared config, `SeededConfig`) → still 0 downloads; a client-visible edit → one download and the folder holds only the
  new version; an unwritable folder → refresh returns true, the client uses the new files, `WriteProblem` set.
  Done when: green. (FR-061, FR-062, FR-067, SC-006, US4-1, US4-2, US4-5)
  Note: `ConfigCacheEndToEndTests` (3 tests, counting the files carried by `GetConfig` responses through `ScriptedTransport`): A downloads once; B on the same folder has version and files before any call, login → not outdated, no event, refresh false, 0 files; a host with only `iap.json` edited → 0 files; a host with `shop.json` edited → one download and the folder holds only `gameserver-config.bin` with the new version; denied folder → refresh true, the client uses the files, `WriteProblem` set, nothing on disk.
- [x] T045 [US4] Budgets, API, release notes, checkpoint (after T043, T044): opt-in (`GAMESERVER_PERF=1`) case in
  `PersistentConfigCacheTests`: 1 MiB load and save, p95 of each ≤ 50 ms (V11). `SdkPublicApiTests` green and the new
  public members listed in a Note (`ClientConfigVersion`, `PersistentConfigCache` members). Release notes: FG-019
  persistent-cache part, the consumer setup line for `OnlineSession.cs:83`, the two client markers to drop
  (`ServerConfigInjector.cs:39`, `OnlineSession.cs:333`, remark `:329`). Full suite.
  Done when: green; perf numbers recorded. (FR-066, SC-006, SC-008, FR-090)
  Note: opt-in `PersistentConfigCacheTests.One_mib_loads_and_saves_within_the_budget` (16 × 64 KiB random files, 50 iterations after 5 warm-up) on **drive C:** (`%TEMP%`, where `persistentDataPath` lives on Windows): save p95 11.2 / 15.0 ms, load p95 4.9 / 8.5 ms (two runs), so SC-006 holds on C:. Not re-measured on D:; T009 measured ~70 ms save p95 there, dominated by `Flush(true)`, which is **kept** (durability wins); stated in the release notes.
  Note: new public SDK members (additive; `SdkPublicApiTests` green; the baseline file is unchanged on purpose, SC-008 compares with `main` before the slice): `GameServerClient.ClientConfigVersion`; `PersistentConfigCache` (`FileName`, `.ctor(string)`, `Folder`, `Version`, `Files`, `LoadProblem`, `WriteProblem`, `Store`); generated `ClientConfigVersion` + `ClientConfigVersionFieldNumber` on `ErrorEnvelope`, `CommandResponse`, `SnapshotResponse`, `GetConfigResponse`, `LoginResponse`; `UnityConfigCache.Create` (Unity only). Release notes: FG-019 persistent-cache part, the consumer setup line for `OnlineSession.cs:83`, the client markers. Checkpoint: Debug and Release 0 warnings; full suite in memory **2154 total / 2057 pass / 97 skip / 0 fail**, `GAMESERVER_TEST_STORE=File` the same (COUCHBASE_PW unset); vs T028: +91 tests, +1 skip (the new opt-in cache perf case).

**Checkpoint**: a second client on the same folder downloads nothing while the client version is unchanged; every
injected fault and corrupt file gives "old, new or empty" with no exception.

## Phase 7: US5 — The config bundle travels compressed; an oversized bundle is found at startup (P5, FG-019 delivery)

Goal: the bundle is serialized once, its size checked at startup, and compressed per request for clients that ask.
Independent test: T048 (negotiation table, endpoint sweep), T047 (limit and limit + 1), T051 (Unity).
Depends on: US3 (T035: field 5 is part of the bundle). Can run in parallel with US4.

- [x] T046 [US5] `ConfigBundle` (after T005, T035; research R11, data-model §4.1): new internal
  `src/GameServer.Platform.Web/Config/ConfigBundle.cs` (`Prefix` once, `Full(t)` = prefix ++ tail, `NotModified(t)`,
  `MaxResponseBytes`; or the T005 fallback); built in `ConfigStartup` step 4 when the config loaded and
  `Hosting:MapPlayerApi` is true, and held by an internal singleton for the handler. The `GetConfig` handler in
  `src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs` (`:56-82`): parse → either version ? `NotModified(now)` :
  `Full(now)` → length > `ConfigBundleBytes` ? 413 (kept) : write.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/ConfigBundleTests.cs`: `Full(t)` equals
  `GetConfigResponse{1,3,4,5}.ToByteArray()` for 0, 1 and 50 files, an empty client version and a negative time;
  `MaxResponseBytes` ≥ every `Full(t)`; the request-time 413 still fires when the limit is below the size (unit); the
  host's `GetConfig` body for the shared config equals a whole-message serialization.
  Done when: green; `ConfigAndCheatTests` and `ClientTests` config cases green. (FR-052, FR-053, FR-054)
  Note: `src/GameServer.Platform.Web/Config/ConfigBundle.cs` (`Prefix` = fields 1+3 serialized once with the file bytes wrapped, not copied; `Tail(t)` = fields 4+5; `Full(t)`; `NotModified(t)`; `IsCurrent(version)` = client or server version, ordinal; `FileCount`; `MaxResponseBytes` = prefix + tail at `long.MinValue`). Held by the new internal singleton `ConfigPhaseSummary` (`Platform.Web/Config`, published once by `ConfigStartup` step 4 with the config, rule and catalog counts and the bundle, or no bundle when `MapPlayerApi=false`; registered in `AddGameServer`). The handler (`CoreEndpoints`) writes `Prefix` and the tail as two parts (no per-request copy of the files) through the new internal `ProtobufBinding.WriteBytesAsync` (same cancelled-token rule as `WriteAsync`, which now delegates to it); the request-time 413 compares prefix + tail with the limit.
  Note: `ConfigBundleTests` (T046 part): prefix + tail = whole-message serialization for 0/1/50 files, empty client version, times 0, −1, `long.MinValue`, `long.MaxValue` (theory, 5 shapes); `MaxResponseBytes` ≥ every `Full(t)` and = `Full(long.MinValue)`; `NotModified` + `IsCurrent`; host body = whole message; request-time 413 with the limit lowered after the start (mutating the `GameServerOptions` singleton). **Test changed:** `ConfigAndCheatTests.GetConfig_requires_a_player_and_enforces_the_bundle_limit` set `ConfigBundleBytes=200` at start, which now stops the host (BC-7); it lowers the limit after the start instead (same assertions). `ConfigVersionCarriageTests`, `ClientTests` config cases green.
- [x] T047 [US5] Startup size check and startup config line (after T046; contracts/startup-report.md §6, §8):
  `ConfigStartup` adds the bundle-size line when `MaxResponseBytes > Limits.ConfigBundleBytes`;
  `GameServerApplicationExtensions.LogStartup` (`:131`) logs the data-model §5.6 Information line after the 013 line
  (both versions, file counts, bundle bytes or "bundle not served", rule and catalog counts; plan N8).
  Tests in `tests/GameServer.Platform.Web.Tests/ConfigBundleTests.cs`: `ConfigBundleBytes = MaxResponseBytes` starts;
  `MaxResponseBytes − 1` fails with exactly the §6 line; `MapPlayerApi=false` → no size check and "bundle not served";
  the config line is logged once with both versions (log capture).
  Done when: green. (FR-044 startup log, FR-053, US5-5, BC-7)
  Note: `ConfigStartup` adds `ConfigReportLines.BundleTooLarge(MaxResponseBytes, FileCount, limit)` after the rule/catalog lines (startup-report §1 step 3) when the player API is mapped. `LogStartup` logs the §5.6 line after `Starting …` through `ConfigPhaseSummary` (two templates: `bundle {BundleBytes} B` / `bundle not served`); `{BundleBytes}` = `MaxResponseBytes` (the value checked against the limit). SampleHost (local flavor, committed config): `Config sample-1: version sample-1+8a37f29ee2dc, client version sample-1+9b25b8b6bc0b, 5 files (4 client-visible, bundle 1747 B), 4 rules, 4 catalogs.`; with `GameServer__Limits__ConfigBundleBytes=1746` it exits with exactly `[config] the client-visible bundle needs a GetConfig response of up to 1747 bytes (4 files), over GameServer:Limits:ConfigBundleBytes = 1746. Fix: …`.
  Note: `ConfigBundleTests` (T047 part, 3 tests): limit = largest response starts and serves; largest − 1 → exactly the §6 line; `MapPlayerApi=false` + limit 1 starts with `bundle not served` and no bundle; FullGame host logs exactly two Startup Information lines, the config line second (`4 rules, 4 catalogs`, structured `ConfigVersion`/`ClientConfigVersion`/`BundleBytes`). **Test changed:** `TestKitHostTests.A_started_host_logs_one_startup_line…` now picks the `Starting ` line (there are two Information lines).
- [x] T048 [US5] Compression (after T006, T046; research R12, contracts/wire-changes.md §5, data-model §4.2–§4.3):
  `src/GameServer.Platform/GameServerOptions.cs` `ConfigCompression` (default true, XML doc); new internal
  `src/GameServer.Platform.Web/Config/ConfigResponseCompression.cs` (eligible when the switch is on, the response carries
  files and is ≥ 1024 B; negotiation with typed headers or the T006 fallback; `BrQuality`/`GzipLevel` from T006; pooled
  buffer; never larger; `Vary: Accept-Encoding` on every `GetConfig` response while on; `Content-Length` = bytes sent);
  called by the `GetConfig` handler only. No `ResponseCompression` anywhere.
  Tests in a new file `tests/GameServer.Platform.Web.Tests/ConfigCompressionTests.cs`: wire-changes §5 row by row
  (headers, decoded body byte-identical to the uncompressed body); `not_modified` and a body < 1024 B never compressed
  (with `Vary`); an incompressible bundle sent uncompressed; switch off → today's bytes and headers, no `Vary`; endpoint
  sweep over every route of the sample host's `WireCatalog` plus health and time with `Accept-Encoding: gzip, br` → only
  `platform.config` may carry `Content-Encoding`; a request with `Content-Encoding` is processed as today.
  Done when: green; a search of `src/` for `ResponseCompression` finds nothing. (FR-050, FR-051, FR-052, FR-054, FR-056,
  US5-1, US5-2, US5-3, US5-4)
  Note: `GameServerOptions.ConfigCompression` (default true, XML doc). New internal `src/GameServer.Platform.Web/Config/ConfigResponseCompression.cs`: `MinCompressBytes = 1024`, `GzipLevel = 6`, `BrQuality = 4` (T050 decision), `Negotiate` over `GetTypedHeaders().AcceptEncoding` (V7: no own parser; q absent = 1, max q per name, `*` covers an unlisted candidate, tie → br), `Compress` into an `ArrayPool` buffer of capacity `length − 1` through a private bounded write-only stream (overflow = not smaller → identity, no exception), `WriteFullAsync`/`WriteNotModifiedAsync` (`Vary: Accept-Encoding` on every success response while on; `Content-Length` = bytes sent; switch off = today's write path, no `Content-Length`, no `Vary`). Called by the `GetConfig` handler only. **Deviation (wording):** the class itself is named `ConfigResponseCompression` as the task says, so a literal search of `src/` for `ResponseCompression` finds it; a search for `AddResponseCompression|UseResponseCompression|Microsoft.AspNetCore.ResponseCompression` finds nothing. `Vary` is not on error responses of the route (`ErrorResponseWriter` clears headers; errors are never compressed).
  Note: `ConfigCompressionTests` (always-on): 17 negotiation rows (wire-changes §5 + `deflate, gzip`, `GZIP`, empty, `gzip;q=abc`, `;;`) with headers and decoded body = plain body; `not_modified` and a < 1024 B bundle never compressed, with `Vary`; 16 KiB random client-visible file → identity for gzip and br; switch off → `Full(t)` bytes, no `Content-Encoding`, no `Vary`; a request with `Content-Encoding: gzip` (plain body) processed as today; endpoint sweep (FullGame + GM + Observability: every `WireCatalog` route and method with a player, `Accept-Encoding: gzip, br`, plus `/health/live`, `/health/ready`, `/v1/time`) → only `platform.config` carries `Content-Encoding` (`br`).
- [x] T049 [US5] The SDK always gets a parseable bundle (after T008, T048; research R13): new file
  `tests/GameServer.Client.Tests/ConfigCompressionClientTests.cs`: the SDK over a transport whose handler sends
  `Accept-Encoding: gzip` and decodes (a delegating handler over the test server), and over the default transport (no
  header → identity): `RefreshConfigAsync` stores identical files in both. **Only if** T008 (or later T058) shows a
  supported platform that advertises an encoding without decoding: new internal `Runtime/Core/ConfigBody.cs` (+
  hand-written `.meta`; gzip magic `1F 8B` sniff, `GZipStream`, 64 MiB decoded cap) used only by `RefreshConfigAsync`,
  with tests (gzip body, plain body, truncated gzip → clear error, cap).
  Done when: green; a Note records which branch was taken (input of PCR unity-sdk, T063). (FR-055, US5-6)
  Note: **Branch taken: no SDK code** (T008: Editor and Windows player decode gzip; T051 confirmed with KestrelDemo; Android/iOS stay T058). No `ConfigBody.cs`. `tests/GameServer.Client.Tests/ConfigCompressionClientTests.cs`: an SDK client over a delegating handler that sends `Accept-Encoding: gzip` and decodes (it saw `Content-Encoding: gzip` on `config/get`) and one over the default transport (no `Accept-Encoding`, identity) store identical files, equal to the host's bytes. `SdkHarness.NewClient` gained an optional `http` parameter.
- [x] T050 [US5] Budgets (after T047, T048): new file `tests/GameServer.Platform.Web.Tests/ConfigPhasePerformanceTests.cs`
  and an opt-in section of `ConfigCompressionTests` (`GAMESERVER_PERF=1`): compression of the sample, the 28 KB
  zh-pvp-shaped and the 1 MiB bundles: gzip ratio ≤ 30 %, p95 added server time ≤ 20 ms at 1 MiB (SC-005, V6 final);
  config phase (load, rules, catalogs, bundle) ≤ 20 ms for the sample config (always-on with a 5× margin) and ≤ 300 ms
  for 1 MiB of row tables (opt-in) (SC-007).
  Done when: green on the reference machine; numbers recorded; on a miss the V6 fallback (lower level, then gzip splice)
  is applied and recorded. (FR-052, SC-005, SC-007)
  Note: `tests/GameServer.Platform.Web.Tests/ConfigPhasePerformanceTests.cs`: runs `ConfigStartup.Run` on a freshly built FullGame host per start (1 warm-up + 3 measured, median). Sample config (`samples/SampleHost/config`, always-on, bound 5 × 20 ms): median 1.8 ms. Opt-in 1 MiB `resources.json` (1 048 614 B, ~13 000 rows through the economy rule and `ResourceCatalog`, limit raised to 4 MiB): median 86.8 ms (≤ 300 ms). Opt-in section of `ConfigCompressionTests` (real `ConfigResponseCompression.Compress` + pool rent/return, 200 iterations after 20 warm-up, 2026-11-04, Release).
  Note: **V6 final / level decision:** gzip 9 / Brotli 6 (the T006 rule) on this path, two runs: 1 MiB gzip p95 18.66 / 18.21 ms, br p95 17.93 / 17.82 ms — within 10 % of the 20 ms budget, so the **lower levels gzip 6 / Brotli 4** ship: 1 MiB (1 045 241 B) gzip p50 8.50 / p95 9.20 ms ratio 14.0 %, br p50 7.05 / p95 7.53 ms 14.2 %; first-consumer-shaped (27 643 B) gzip p95 0.233 ms 15.7 %, br p95 0.559 ms 15.3 %; sample (1 743 B) gzip p95 0.021 ms 28.3 %, br p95 0.092 ms 28.9 %. All gzip ratios ≤ 30 %; no gzip splice needed.
- [x] T051 [US5] **Agent-run Unity** check of the bundle on Editor (and Windows player when T008 showed it advertises an
  encoding) (after T008, T048, T049): SampleHost from this branch (`tools/local/run-local.ps1 -Port 5080`); a scratch
  logging forwarder on 5092 → 5080 (logs request `Accept-Encoding` and response `Content-Encoding`/`Vary` per path);
  `sdk/unity/ci/smoke-build.ps1 -Unity "D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe" -CompileOnly`, then
  `-E2E -ServerUrl http://localhost:5092/`.
  Done when: a Note records the forwarder lines for `/api/v1/config/get`, the E2E exit code and its `Config …` line;
  SampleHost and the forwarder are stopped; `git status sdk/unity samples` shows no change. If the agent cannot start
  Unity (an Editor holds the project), the task stays open with these commands for the owner. (FR-055, FR-091, US5-6)
  Note: Agent-run 2026-11-04 (no Editor had KestrelDemo open). SampleHost `tools/local/run-local.ps1 -Port 5080`; scratch logging forwarder `localhost:5092 → 127.0.0.1:5080` (scratch `t051fwd`, no decompression, logs request `Accept-Encoding` and response `Content-Encoding`/`Vary`/bytes). `smoke-build.ps1 -CompileOnly`: exit 0, 0 `error CS`/`warning CS` in `compile.log`. `-E2E -ServerUrl http://localhost:5092/`: exit 0; forwarder `POST /api/v1/config/get req Accept-Encoding=[deflate, gzip] -> 200 Content-Encoding=[gzip] Vary=[Accept-Encoding] bytes=494` (every other route: no `Content-Encoding`, no `Vary`); E2E line `config downloaded sample-1+9b25b8b6bc0b: battle.json, counter.json, resources.json, shop.json`, `result=OK`. SampleHost and forwarder stopped (5080/5092 free); `git status sdk/unity samples` shows only the T056 edit (no `.meta`, no settings change). Logs: scratch `t051/`.
- [x] T052 [US5] Release notes and checkpoint (after T050, T051): FG-019 delivery section (br/gzip on `GetConfig` only,
  threshold 1024 B, levels, rollback switch `GameServer:ConfigCompression=false`, BC-7, the Unity results so far,
  "no client change needed"). Full suite.
  Done when: green; counts recorded. (FR-090)
  Note: Release notes: new section "FG-019 (delivery)" (GetConfig only, 1024 B threshold, levels gzip 6 / br 4 with the measured numbers and why, rollback switch `GameServer:ConfigCompression=false`, BC-7 with the real SampleHost line, startup config line, Unity results Editor/Windows done and Android/iOS pending, no client change needed); manual-checks rows T051/T057 done, T058 pending with steps; "Known before the tag" V6 bullet updated. Full suite: Debug and Release 0 warnings; in memory **2201 total / 2102 pass / 99 skip / 0 fail**, `GAMESERVER_TEST_STORE=File` the same (COUCHBASE_PW unset); vs T045: +47 tests, +2 skips (the two new opt-in perf cases); WireGen `verify` exit 0 for the three configs.

**Checkpoint**: the bundle is compressed only for clients that ask and only on `GetConfig`; an oversized bundle stops the
host with one line; the SDK parses the bundle with and without compression.

## Phase 8: US6 — The framework's own modules, samples and docs use the same path (P6, FG-018, FG-019, FG-023, FG-024)

Goal: the fixture and the template show declarations; KestrelDemo uses the persistent cache; guides, PCRs and HTML
pages describe the shipped behaviour. (Economy, IAP and sample game declarations landed in US1, Planning note 4.)
Independent test: T053 (fixture), T055 (no lazy factory), T057 (KestrelDemo twice), T066 (reviewer walkthrough).
Depends on: US1–US5 (docs describe shipped behaviour).

- [x] T053 [P] [US6] Consumer fixture rule (after T019): `tests/ConsumerFixture/src/Fixture.Game/FixtureModuleDefinition.cs`
  declares `ConfigFileDeclaration.Required(RulesFile, FixtureConfigRules.Notes)` (rule `fixture.notes`: `maxLength`
  integer ≥ 1 through `ConfigJson`) instead of `RequiredConfigFiles`; new file
  `tests/ConsumerFixture/tests/Fixture.Tests/FixtureConfigTests.cs` (public API only, no host): the committed
  `notes.json` passes; `maxLength` 0 and `"40"` each give the exact line through `ConfigDeclarationCheck`.
  Done when: `dotnet test --project tests/ConsumerFixture/tests/Fixture.Tests/Fixture.Tests.csproj -c Release` green,
  including `FixtureHostSmokeTests`. (FR-070, FR-018)
  Note: `FixtureModuleDefinition` declares `ConfigFileDeclaration.Required(RulesFile, FixtureConfigRules.Notes)` instead of `RequiredConfigFiles`; `FixtureConfigRules` (rule `fixture.notes`, `ConfigJson.RequiredInteger(… "maxLength" … min: 1, fix: "set the longest note a player may save, at least 1")`) lives in the same file. `FixtureConfigTests` (3: the committed `src/Fixture.Host/config` passes; `0` and `"40"` give exactly `[module fixture] config notes.json: 'maxLength' must be an integer of at least 1, found {0|"40"}. Fix: set the longest note a player may save, at least 1.`). Fixture suite green (29 tests incl. `FixtureHostSmokeTests`).
- [x] T054 [P] [US6] Template module (after T011; Planning note 15):
  `templates/module/Template.Module/TemplateModuleDefinition.cs` declares
  `ConfigFileDeclaration.Optional("template.json", TemplateConfigRules.Points)` with one small rule, and a commented
  `ConfigCatalogs` example. Optional, so the hosts of `WireMapperRegistrationTests` and `WireConformanceTests` that
  register the template without that file keep starting.
  Done when: the template builds; the full suite green. (FR-076 template part)
  Note: `TemplateModuleDefinition`: `ConfigFile = "template.json"`, `ConfigFiles = [ConfigFileDeclaration.Optional(ConfigFile, TemplateConfigRules.Points)]` (rule `template.points`: `maxPoints` integer ≥ 1) and a commented `ConfigCatalogs` example. The template builds; `WireMapperRegistrationTests`/`WireConformanceTests` (template without `template.json`) green in the full suite.
- [x] T055 [US6] No lazy catalog factory left (after T022–T024): new file
  `tests/GameServer.Architecture.Tests/ConfigCatalogArchitectureTests.cs`: for `EconomyModule`, `IapModule`,
  `SampleGameModule` and the template module, `ConfigureServices` into a fresh collection registers no descriptor whose
  service type is a catalog type declared by any framework or sample descriptor; a seeded test module with a lazy
  `TryAddSingleton(sp => X.Load(...))` is caught.
  Done when: green. (FR-024, FR-025, FR-070, US6-1)
  Note: `tests/GameServer.Architecture.Tests/ConfigCatalogArchitectureTests.cs` (2 tests): every concrete module with a parameterless constructor in the `GameServer.*`, `SampleGame*`, `Sample.*`, `Template.*` assemblies (asserted to include Economy, IAP, SampleGame, Template) runs `ConfigureServices` into a fresh collection; no descriptor's service type is a declared catalog type (`BattleCatalog`, `IapCatalog`, `ResourceCatalog`, `ShopCatalog`); a seeded module with `TryAddSingleton(sp => ProbeCatalog.Load(…))` is named (`LazyCatalogModule registers ProbeCatalog`).
- [x] T056 [US6] KestrelDemo uses the persistent cache (after T043; research R18): edit only
  `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs` (opened by exact path): `SampleGameFlow.CreateClient`
  passes `config: UnityConfigCache.Create("local")`; `RunAsync` logs `config from cache {version}` when the login leaves
  `ConfigOutdated` false with a cached version, and `config downloaded {version}` after a refresh.
  Done when: `git diff` of that file shows only these changes; `tests/GameServer.Client.Tests/SdkRouteTests.cs` (literal
  scan of this file) green. The MonoBehaviour compiles only in Unity (T057). (FR-071)
  Note: `SampleGameClient.cs` only: `config: UnityConfigCache.Create("local")` in `CreateClient`; `RunAsync` logs `config downloaded {version}: {files}` after a refresh that downloaded and `config from cache {version}: {files}` when the login leaves `ConfigOutdated` false with a cached version (replaces the old `Config {version}: …` line). `git diff` shows only these hunks; `SdkRouteTests` green. Compiled in Unity in T051/T057.
- [x] T057 [US6] **Agent-run Unity**: KestrelDemo opened twice (after T051, T056; FR-091 manual check, V10 on the Mono
  Editor): `smoke-build.ps1 -Unity "<path>" -CompileOnly` (0 `error CS`/`warning CS` in `compile.log`); SampleHost on
  5080; `-E2E` twice: run 1 logs `config downloaded {v}`, run 2 logs `config from cache {v}` and fetches no bundle; then
  SampleHost restarted with `GameServer__ConfigPath` set to a scratch copy of `samples/SampleHost/config` whose
  `shop.json` was edited (never edit `samples/SampleHost/config` itself) and run 3 logs `config downloaded {w}` (the
  replace path). Check the cache folder under the Editor's `Application.persistentDataPath/gameserver-config/local`
  holds only `gameserver-config.bin`.
  Done when: a Note records the three runs (exit codes, log lines, versions), the cache folder listing, and an empty
  `git status sdk/unity` (Unity created no package `.meta`); SampleHost stopped, 5080 free. If the agent cannot start
  Unity, the task stays open with these commands for the owner and the release notes mark it pending. (FR-071, FR-091,
  US6 independent test, V10)
  Note: Agent-run 2026-11-04, Unity 6000.3.22f1 Editor (Mono), through the T051 forwarder. `-CompileOnly` 0 `error CS`/`warning CS`. Run 1 (exit 0): `config downloaded sample-1+9b25b8b6bc0b`, forwarder shows the gzip `config/get`. Run 2 (exit 0): `config from cache sample-1+9b25b8b6bc0b`, **no** `config/get` request. SampleHost restarted with `GameServer__ConfigPath` = scratch copy of `samples/SampleHost/config` with `potion_pack` price 60 → 50 (startup line client version `sample-1+523717009cea`); run 3 (exit 0): `New config sample-1+523717009cea available.`, `config downloaded sample-1+523717009cea`, `Buy → OK` (new price), forwarder gzip `config/get` 487 B — the `File.Replace` path on Mono (V10). Cache folder `%USERPROFILE%\AppData\LocalLow\DefaultCompany\KestrelDemo\gameserver-config\local` holds only `gameserver-config.bin` (1 765 B). `git status sdk/unity` empty; `samples/` only the T056 edit; SampleHost and forwarder stopped, 5080 free.
- [ ] T058 [US6] **Manual / owner-assisted** device run, ACC-18 (after T057; V8 and V10/V11 on devices, V9 only when
  needed): Android — the agent builds `KestrelDemo/Build/KestrelDemo.apk` with `smoke-build.ps1 -Unity "<path>"` (IL2CPP
  arm64, stripping High) and starts SampleHost with `tools/local/run-local.ps1 -Port 5080 -Lan` plus the T051 forwarder;
  the **owner** installs the APK on a device on the same network and runs it three times as in T057 (first start,
  second start, after a client-visible edit). iOS — the **owner** builds on macOS (the iOS module is not installed on this
  machine) and runs the same three starts. Record per platform: `Accept-Encoding` sent, response compressed or not,
  bundle parsed, `config from cache` on the second start, `config downloaded` after the edit, no crash (SHA-256 and
  `File.Replace` under IL2CPP). If a platform advertises an encoding without decoding: check the T049 `ConfigBody` path
  on that device (V9), else record `GameServer:ConfigCompression=false` for deployments serving it.
  Done when: a Note per platform, or the release notes list the run as pending with these exact steps (the tag waits
  for it, FR-091). (FR-055, FR-091, US5-6, V8, V9, V10, V11)
  Note: Not done (owner). The agent built the APK 2026-11-04: `samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk` (`smoke-build.ps1 -Unity …`, IL2CPP arm64, stripping High, `Build Finished, Result: Success`, 54 193 600 B, SHA-256 prefix `A6108AC5BFDD287A`); no tracked file changed. The agent did **not** leave SampleHost or the forwarder running (session rule). Owner steps: (1) `tools/local/run-local.ps1 -Port 5080 -Lan`; (2) optional forwarder: build the scratch program of T051 (or any logging proxy) on port 5092 → 5080 to see `Accept-Encoding`/`Content-Encoding`; (3) KestrelDemo's `serverUrl` is serialized `http://localhost:5080/` — set it on the `SampleGameClient` object to `http://<PC LAN IP>:5080/` (or `:5092/` via the forwarder, bound to all interfaces) and rebuild with `sdk/unity/ci/smoke-build.ps1 -Unity "D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe"`; (4) `adb install -r samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk`, start the app (run 1 → `config downloaded {v}`), close and start again (run 2 → `config from cache {v}`, no `config/get`), stop the host, restart it with `GameServer__ConfigPath` = a copy of `samples/SampleHost/config` whose `shop.json` is edited (never the committed folder), start the app (run 3 → `config downloaded {w}`); (5) iOS: same three starts from a macOS build. Record per platform: `Accept-Encoding` sent, compressed or not, parsed, cache lines, no crash. A platform that advertises gzip without decoding → V9 / `GameServer:ConfigCompression=false`. Release notes list it as pending (the tag waits, FR-091).

### Documentation (FR-075..FR-077; Vietnamese except the consumer guide; pages state "framework v0.7.0, chưa phát hành / unreleased")

- [x] T059 [US6] Consumer guide `docs/guides/consumer-guide.md` (after T028, T033, T040, T045, T052): new
  `## 12. Game config` with one subsection per FR-075 bullet: manifest and overlay; file declarations, rules and the
  line format (with the economy example); catalogs (declare, never register in `ConfigureServices`; the BC-8 fix);
  the test kit call (from `FixtureConfigTests`); `GameConfigStatus` for startup checks (from T032); the two versions and
  what changes each (data-model §1.4); compression and the bundle limit (rollback switch); the persistent cache with its
  Unity setup (`UnityConfigCache.Create(environmentName)`, one folder per server environment). §5 points to the kit call;
  §10 gains `ClientConfigVersion` and the new meaning of `ConfigVersionChanged`; §11 bump checklist gains the 015 steps
  (BC-6 one-line fix, fix BC-1/BC-2 lines, move config checks to declarations or `GameConfigStatus`). Version stamp.
  Done when: every FR-075 bullet has a paragraph (checklist in a Note); no text recommends an `IStartupCheck` taking
  `IGameConfig` or bumping the label when `clientVisible` changes; every code sample is from real code;
  `DocumentedConfigurationKeysTests` green. (FR-034 docs, FR-075, FR-077, US2-5, US6-2)
  Note: Consumer guide (English): new `## 12. Game config` — 12.1 manifest and overlay (fixture manifest, duplicate line), 12.2 declarations, rules and line format (economy declaration, `FixtureConfigRules`, economy lines), 12.3 catalogs (declare, never register; BC-8 fix), 12.4 kit call (from `FixtureConfigTests`), 12.5 `GameConfigStatus` (members; sample `StatusCheck` from `StartupCheckHostTests`), 12.6 two versions and what changes each (no label bump for visibility), 12.7 compression and the bundle limit (rollback switch, BC-7 line, startup line), 12.8 persistent cache with the KestrelDemo setup. §5 points to the kit call; §10 gains `ClientConfigVersion` and the new `ConfigVersionChanged` meaning; §11 step 5 gains the 015 steps (BC-6 one-line fix, BC-1/BC-2/BC-7 lines, config checks to declarations or `GameConfigStatus`, lazy factories to `ConfigCatalogs`, pinned versions). FR-075 checklist: manifest+overlay 12.1, declarations+rules+line format 12.2, catalogs 12.3, kit call 12.4, status 12.5, two versions 12.6, compression+bundle limit 12.7, persistent cache+Unity setup 12.8. No text recommends a check taking `IGameConfig` or a label bump on `clientVisible` changes; code samples are copied from real files. Version stamp already 0.7.0 (unreleased). `DocumentedConfigurationKeysTests` green.
- [x] T060 [P] [US6] `docs/guides/module-authoring.md` and `templates/module/README.md` (after T054): declarations
  (`ConfigFiles`, `ConfigCatalogs`, `RequiredConfigFiles` = required without rules), "declare every catalog; an
  undeclared catalog stays lazy" (FR-025), rules through `ConfigJson`, the kit call in the module's tests, keep
  config-derived data in declared catalogs only (FG-020 seam). Version stamp.
  Done when: both pages name the template's example; no page shows a lazy catalog factory. (FR-025, FR-076, FR-077)
  Note: `docs/guides/module-authoring.md`: new `### Config: file, luật và catalog` under §4 (the template's `ConfigFiles`/`ConfigCatalogs` and `TemplateConfigRules`, `RequiredConfigFiles` = required without rules, "declare every catalog; an undeclared catalog stays lazy", rules through `ConfigJson`, config-derived data in declared catalogs only (FG-020 seam), checks through `GameConfigStatus`) and the kit call in §8 (from `FixtureConfigTests`). `templates/module/README.md`: new step 5 (config declarations naming the template's example), steps renumbered, PCR step mentions config files and rules. Neither page shows a lazy catalog factory.
- [x] T061 [P] [US6] PCR platform `docs/pcr/platform.md` (after T048): new `## 21.` (loader faults incl. the duplicate
  line, declarations, line format, catalogs and their DI binding incl. host override, config status, both versions and
  their inputs, compression with levels and threshold, the startup size check, the startup config line, the provision
  rule); §1 (compression constants, 1024 B threshold, `ConfigBundleBytes` limits uncompressed bytes); §9 `GetConfig`
  paragraph (either version, additive field); §13 metric `client_config_version_info` and readiness
  `clientConfigVersion`; §17 `IStartupCheck` creation, isolation, `GameConfigStatus`, provision warning; the BC-1..BC-10
  table (ARC-08). `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` `Documents()` gains `## 21.` (Planning
  note 8). Version stamp.
  Done when: every line template of contracts/startup-report.md appears verbatim once (texts taken from
  `ConfigReportLines` and the rules); `DocumentedConfigurationKeysTests` green with `GameServer:ConfigCompression`
  resolved. (FR-034 docs, FR-076)
  Note: PCR platform: new `## 21.` (start order, every loader line, declarations, required-file line, rule and catalog lines, catalog/DI binding incl. host override in both orders, config status, startup-check lines (provision = warning), both versions with the formula and change matrix, bundle + size line, compression table with levels/threshold and the measured numbers, startup config line, BC-1..BC-10 table); §1 rows (bundle limit uncompressed, 1024 B threshold, gzip 6 / br 4, `GameServer:ConfigCompression`); §9 `GetConfig` paragraph (either version, field 5, startup check, only compressed route); §13 metric `client_config_version_info` and GM health data `clientConfigVersion`; §17 check creation/isolation, `GameConfigStatus`, provision warning, R8 bypass; header version list. Every startup-report line template appears verbatim once in §21 (texts compared with `ConfigReportLines` and the loader). `DocumentedConfigurationKeysTests.Documents()` gains `## 21.` (`ConfigPcrSection`); green with `GameServer:ConfigCompression` resolved.
- [x] T062 [P] [US6] PCR `docs/pcr/economy.md`, `iap.md`, `sample-game.md` (after T022–T024): the file rules of
  data-model §2.2 with their exact lines, the declared catalogs, BC-1 per module. Version stamp.
  Done when: each rule of `EconomyConfigRules`, `IapConfigRules` and `SampleGameConfigRules` appears once. (FR-076)
  Note: PCR economy (`## Kiểm resources.json khi khởi động`), iap (`## Kiểm iap.json khi khởi động`), sample-game (`## Kiểm battle.json và shop.json khi khởi động`): declarations and declared catalogs, one row per fault with the exact line (texts from `EconomyConfigRules`, `IapConfigRules`, `ResourceReferences`, `SampleGameConfigRules`), BC-1 per module. Each rule name (`economy.resources`, `iap.products`, `sample.battle`, `sample.shop`) appears once in its PCR. Stamps already 0.7.0 (chưa phát hành).
- [x] T063 [P] [US6] PCR `docs/pcr/unity-sdk.md` (after T045, T049, T051): the persistent cache (container v1, version
  rule: unknown = empty, new format = new version, not encrypted, one folder per environment, threading and budget),
  `ClientConfigVersion`, `ConfigOutdated` and `ConfigVersionChanged` semantics (BC-4), the compression result per
  platform (T008, T051, T058; pending rows marked), new package files with their `.meta`, package `0.7.0`. Version
  stamp.
  Done when: the section matches `PersistentConfigCache` and the T049 Note. (FR-064, FR-076)
  Note: PCR unity-sdk: package table (new Core files, `UnityConfigCache`), Config behaviour bullet rewritten (`ClientConfigVersion`, `ConfigOutdated`/`ConfigVersionChanged` on the client version, BC-4, re-key on `not_modified`), compression bullet (SDK sets no header, no decoder), new `## Cache config bền` (container v1, load/store rules, unknown = empty, new format = new version, not encrypted, one folder per environment, threading, budget, path safety), "Nén theo nền tảng" table (Editor/Windows done T008/T051; Android/iOS pending T058; T049 branch: no `ConfigBody.cs`), new package files with their GUIDs, package `0.7.0`; acceptance row for the T057 KestrelDemo runs. Matches `PersistentConfigCache` and the T049 Note.
- [x] T064 [P] [US6] HTML pages (after T059, T061): `docs/html/01-gioi-thieu.html` (descriptor list: `ConfigFiles`,
  `ConfigCatalogs`; startup validation table: loader, rule, catalog, check and size lines), `docs/html/02-them-command.html`
  (every place where config, `RequiredConfigFiles` or startup checks appear), `docs/html/03-deploy.html` (provision mode
  BC-9, readiness `clientConfigVersion`, the new metric, the rollback switch, BC-7; Planning note 14),
  `docs/html/index.html` stamp; meta lines name branch `015-config` and the date; stamps "framework v0.7.0 (chưa phát
  hành)". Keep the hand-maintained structure and ids. Release notes: framework-repository section (samples, fixture,
  template, docs list).
  Done when: a link-check script (every `#id` exists, every relative `href` exists, no duplicate id) reports 0 problems
  on the four pages (Note). (FR-076, FR-077)
  Note: HTML: `01-gioi-thieu.html` (descriptor list gains `ConfigFiles`, `ConfigCatalogs`; new startup-validation table: loader, rule, catalog, check and size lines; strengths row mentions config rules), `02-them-command.html` (config paragraph after the `ICommandContext` list: declarations, catalogs from DI, `ConfigVersion` = server, checks via `GameConfigStatus`; checklist row 14; common-errors row for a rule line), `03-deploy.html` (provision BC-9 bullet in 5.5, GM health `configVersion`/`clientConfigVersion` in 7.1, metric row in 9, rollback rows for `GameServer__ConfigCompression=false` and BC-7 in 15.2), `index.html` stamp. Meta lines name branch `015-config` and 2026-11-04; footers 2026-11-04; stamps "framework v0.7.0 (chưa phát hành)"; ids and structure kept (no TOC change). Link check (scratch `links.py`: every `#id` exists, every relative `href` exists) 471 links, 0 problems; duplicate-id check 0 on the four pages. Release notes: "Framework repository" section.

**Checkpoint**: KestrelDemo, the fixture and the template use the documented path; guides and PCRs describe the
shipped behaviour.

## Phase 9: Release notes, verification, review and close

- [x] T065 Release notes final pass `specs/015-config/release-notes-draft.md` (after T052, T058 status, T064; FR-090):
  summary; one section per fixed FG in round order (FG-018, FG-023, FG-024, FG-019); the BC-1..BC-10 table with "how it
  surfaces" taken from the real lines; the first-consumer migration checklist in the spec's order with every zh-pvp
  file:line of the spec table (`GameConfigCheck.cs:11`, `:12`, `GameModule.cs:26`, `:27`, `GameModuleDefinition.cs:23`,
  `ConfigDeliveryTests.cs:119`, `:136`, `docs/pcr/game.md` lines 94–96 and 114, `tests/ZhPvp.Tests/Support/TestGameConfig.cs`,
  client `ServerConfigInjector.cs:39`, `OnlineSession.cs:83`, `:329`, `:333`, `:335-347`); the "removable vs stays" table;
  the rolling-deploy note and the compatibility table; owner confirmations 1–3 with their status; the manual checks
  (T008, T051, T057, T058, T066, T067) with their status; "no route, persisted document or GM API response shape changes;
  five additive proto fields; the Unity package stays 0.7.0"; "Known before the tag".
  Done when: every FG and BC id appears; every zh-pvp file:line of the spec table is referenced (grep counts in a
  Note). (FR-090, SC-009)
  Note (2026-11-04, after T067): final pass of `specs/015-config/release-notes-draft.md`: summary (tag waits for T058 and T066); BC-1..BC-10 table with "how it surfaces" quoted from the real lines (BC-6 now names both CS0618 texts); zh-pvp checklist in the spec's order, verified by T067 (013/014 steps first and the order their errors appear, BC-6 two CS0618 + CS0104 alias, measured startup line, the declaration shape for step 3 and the three changed `GameConfigStartupTests` texts, version tests, client line with `transport` and a settings-held cache name, SC-009 result); new "Removable vs stays" table; rolling-deploy note and compatibility table unchanged; per-FG sections in round order (FG-018, FG-023, FG-024 + FG-019 version, FG-019 cache, FG-019 delivery) with the T067 results; owner confirmations 1–3 pending, each with "if the owner says no"; manual checks T008/T051/T057 done, T058 pending (owner) with the six exact steps, T066 pending, T067 done; "no route, persisted document or GM API response shape changes; five additive proto fields; the Unity package stays 0.7.0" kept; "Known before the tag" final (open items, V6, V11, T035 fix, provision warning, the `UnityConfigCache` name finding for T070).
  Note: grep counts (`grep -o "ID\b"`): FG-018 12, FG-023 17, FG-024 7, FG-019 10; BC-1 7, BC-2 5, BC-3 4, BC-4 8, BC-5 4, BC-6 6, BC-7 3, BC-8 3, BC-9 5, BC-10 4. zh-pvp file:line (`grep -cF`, lines): `GameConfigCheck.cs:11` 1, `GameConfigCheck.cs:12` 1, `GameModule.cs:26` 3, `GameModule.cs:27` 3, `GameModuleDefinition.cs:23` 3, `ConfigDeliveryTests.cs:119` 1 (+ "marker line 119" 3), `ConfigDeliveryTests.cs:136` 1 (+ "marker line 136" 2), `docs/pcr/game.md` 5 (lines 94–96: 3, line 114: 2), `tests/ZhPvp.Tests/Support/TestGameConfig.cs` 2, `ServerConfigInjector.cs:39` 3, `OnlineSession.cs:83` 2, `:329` 3, `OnlineSession.cs:333` 3, `:335-347` 2. Every FG and BC id and every file:line of the spec table is referenced.
- [ ] T066 Reviewer walkthrough (story 6, FR-091 manual; after T059–T064): a fresh agent (no context beyond the docs)
  follows consumer guide §12 and `docs/guides/module-authoring.md` in a **temp copy** of the repository: add a
  client-visible `rewards.json` to the consumer fixture with a rule, a declared catalog read by a handler, a kit test, a
  startup check that takes `GameConfigStatus`; start the fixture host on a free port (never 5180: override `Urls`),
  fetch config with the SDK test harness, then edit a server-only file and check that `client_config_version` is
  unchanged.
  Done when: the agent's list of missing or wrong steps is recorded as a Note and each item is fixed in the docs (or
  answered); the temp copy is deleted; nothing of it is committed. (FR-075, FR-091, US6-2)
- [x] T067 **Manual / owner-assisted** first-consumer check (FR-091, SC-009; never edit `E:\ProjGits\zh-pvp-server` or
  `E:\ProjGits\research-and-development-games`): a temp clone of the game repository with its `framework/` submodule
  replaced by a clone of this branch's head (uncommitted slice changes copied in). In the clone only:
  1. build → the expected BC-6 CS0618 in `tests/ZhPvp.Tests/Support/TestGameConfig.cs`; apply the one-line fix;
  2. start the host with its committed config (free port, never 5180) → no BC-1/BC-2 line (research R4 expectation);
  3. `GameConfigStartupTests` green, the 2-line test unchanged;
  4. `ConfigDeliveryTests`: `Making_a_file_client_visible_keeps_the_version` fails (by design);
     `A_server_only_file_change_changes_the_version_the_client_sees` still passes (the trap); rewrite both as the
     release notes say; `Every_response_type_carries_the_version_of_the_committed_config` passes;
  5. move `GameConfigCheck` into declarations as the release notes say (`GameCatalog` declared through `TryLoad`, the
     CASH/GOLD rule on `resources.json`) and run the game suite;
  6. the client snippet of the release notes compiles against `sdk/GameServer.Client` in a scratch C# 9 project (the game
     file itself needs Unity); an edit of `ConfigMission.json` (server-only) gives 0 client downloads through the SDK
     against the clone's host (SC-009).
  If the game's tests do not build because of an earlier slice's BC (013/014), apply those release-notes steps in the
  clone only and record them.
  Done when: a Note records each step and any doc fix (release notes, consumer guide), or — if the owner prefers the
  game session to run it after the tag — the release notes list it as pending with the exact commands; temp clones
  deleted; `E:\ProjGits\*` untouched; 5180 never bound. (FR-091, SC-009)
  Note (2026-11-04): temp clone of `E:\ProjGits\zh-pvp-server` (`main` `7911f20`, framework pinned `v0.6.0` = `2ba963f`) in the session scratchpad (`t067`); `framework/` = a clone of this repository at `abfc32d` (all slice changes committed; tag `v0.7.0` set in that clone only, `GameServerFrameworkTag` bumped in the clone). `E:\ProjGits\zh-pvp-server` and `E:\ProjGits\research-and-development-games` were only read (no edit, checkout, build or git state command; zh-pvp status clean at `7911f20` afterwards). Host on 127.0.0.1:5097 (`--urls`; the game's Local flavor sets `Urls` to 5180, never bound or probed). Temp clone, scratch snippet projects and the short data folder `D:\zt067` deleted.
  Note: step 0 (earlier slices, as allowed): the game is on `v0.6.0`, so the first build stops at 014 BC-3 (CS0618 `GameModule.cs:28-30`) → typed mappers with the four `Hidden` paths + 014 BC-1 `account.init` declaration (`GameRoutes`, `Routes`, `MapPlayerCommand(GameRoutes.InitAccount, …)`); then 013 BC-4 (CS2001 for the three `tests/Shared` links) → kit `ProjectReference` + `using GameServer.TestKit;` in six test files. The rest of the 013/014 checklists (RequestLog folder, run-local, wiregen) does not block build or suite.
  Note: step 1: the build gives the expected BC-6 errors at `tests/ZhPvp.Tests/Support/TestGameConfig.cs:37` — **two** CS0618 on the same line (the 4-argument `GameConfig` constructor and `FileGameConfigLoader.ComputeVersion`) — plus 7 × CS0104 in `ConfigDeliveryTests.cs`: `TestGameConfig` ambiguous between the game's `ZhPvp.Tests.Support.TestGameConfig` and the 013 kit's `GameServer.TestKit.TestGameConfig` (a 013 BC-4 interaction, undocumented). One-line fix `return new GameConfig(label, files, clientVisible);` + `using TestGameConfig = ZhPvp.Tests.Support.TestGameConfig;` → Debug and Release 0 warnings, 0 errors.
  Note: step 2: Local flavor with the committed config starts (`/health/ready` Healthy), **no BC-1/BC-2 line** (research R4 holds); startup line `Config zhpvp-1: version zhpvp-1+3d29d982d748, client version zhpvp-1+c2ba61ed994a, 10 files (7 client-visible, bundle 27860 B), 2 rules, 2 catalogs.`; the only Warning is the Data Protection one. First attempt stopped with `[filestore] FileStore:Folder '…\scratchpad\t067\.local\data' is too long for Windows … 289 characters (limit 259)` (deep scratch path, existing check, no config line); rerun with `--FileStore:Folder D:/zt067/data` (+ analytics/log folders).
  Note: step 3: `GameConfigStartupTests` 4/4 green, the 2-line test unchanged. Full suite at this point 119: 117 pass; failures = `Making_a_file_client_visible_keeps_the_version` (by design, step 4) and `RepositoryConsistencyTests.The_checked_out_framework_tag_is_the_one_the_host_was_built_for` (untagged temp commit; passes once `v0.7.0` is tagged in the clone); `Claude_md_and_readme_name_the_pinned_tag` then fails only because the game's CLAUDE.md/README still name `v0.6.0` (bump step 1, game work).
  Note: step 4: `Making_a_file_client_visible_keeps_the_version` fails (server version `zhpvp-1+3d29d982d748` → `zhpvp-1+f5fb5b005897`); `A_server_only_file_change_changes_the_version_the_client_sees` passes (the trap); `Every_response_type_carries_the_version_of_the_committed_config` passes. Rewritten as the release notes say: `Making_a_file_client_visible_changes_both_versions` (both differ), `A_server_only_file_change_keeps_the_client_version` (client unchanged, server changed), and `Every_response_type…` also asserts `ClientConfigVersion` on all 7 responses (≠ server version) → `ConfigDeliveryTests` 6/6, both FRAMEWORK-GAP markers gone.
  Note: step 5: `GameConfigCheck.cs` and `GameModule.cs:26-27` deleted; `GameModuleDefinition` `ConfigFiles` = one rule `zhpvp.tables` per `GameCatalog.ConfigFiles` file (reports the `TryLoad` errors of its own file through `ConfigProblems`, `ConfigLocation.Row(n)` or `WholeFile`, problem/fix split at ` Fix: `) + rule `zhpvp.resources` on `resources.json` (CASH/GOLD currencies; returns when `ResourceCatalog.Load` throws); `ConfigCatalogs` = `ConfigCatalogDeclaration.Of(c => GameCatalog.TryLoad(c.Config).Catalog, [.. GameCatalog.ConfigFiles, "resources.json"])`. Builds 0 warnings. Suite: the 2-line FG-023 test passes unchanged; the three other facts still give exactly one line but in the framework format (`[module zhpvp.game] config ConfigGunChest.json row 2: duplicate GunId 'ar_0' (first at row 1). Fix: keep one row per gun.`, `… ConfigChestCard.json row 2: duplicate Id '1_Bronze' …`, `… ConfigChapter.json: 'table' must be "ConfigChapter". …`), so their expected texts change (not in the release notes before: fixed). Clone-only kit test: `ConfigDeclarationCheck.Check(config, Identity, Economy, Game)` passes and builds `GameCatalog`; GOLD as an item gives exactly `[module zhpvp.game] config resources.json: resource 'GOLD' must exist with kind "currency". Fix: add 'GOLD' with kind "currency".` and no catalog. Final suite Debug 121 / Release 121: 120 pass, 1 fail (`Claude_md_and_readme_name_the_pinned_tag`, game docs).
  Note: step 6: (a) the release-notes client line compiles with C# 9 (netstandard2.1, warnings as errors) against the package `Runtime/**` with `UNITY_5_3_OR_NEWER` and the Unity 6000.3.22f1 `UnityEngine.CoreModule`/`UnityWebRequestModule` (types `UnityConfigCache`, `UnityWebRequestTransport` present in the output). (b) SC-009 with a C# 9 console over `sdk/GameServer.Client` (`HttpClientTransport`, `PersistentConfigCache`, `OnlineSession.FetchConfigAsync` shape): start 1 against the clone's host → login `outdated=True`, `RefreshConfigAsync` true, 7 files cached under `zhpvp-1+c2ba61ed994a` (27 877 B file); host restarted with `GameServer:ConfigPath` = scratch copy with `ConfigMission.json` `RewardCash` 3200 → 3201 (startup: server `zhpvp-1+f9d52cfb7538`, client `zhpvp-1+c2ba61ed994a`); start 2 → cache loaded 7 files, `outdated=False`, `RefreshConfigAsync` false (`not_modified`), **0 downloads** (one `config/get` because the snippet refreshes unconditionally; a `ConfigOutdated` gate skips it).
  Note: client file:line (read only, `research-and-development-games` `49df950ad`): `ServerConfigInjector.cs:39` and `OnlineSession.cs:333` markers, remark `:329`, `FetchConfigAsync` `:335-347` (applies cached files at boot: `appliedConfigVersion` starts null) all match. **Mismatch:** `OnlineSession.cs:83` is `this.Client = new GameServerClient(options, transport);` (a `transport` variable) and `OnlineSettings` has no environment name (only `BaseUrl`); `OnConfigVersionChanged` (`:179`) logs "Server config version" but now receives the client version (BC-4).
  Note: doc fixes: release notes 015 checklist rewritten as verified (013/014 order, two CS0618 + CS0104, measured startup line, the step-3 declaration shape and the three changed line texts, the client line with `transport` and a settings-held cache name, the `ConfigOutdated` gate, BC-4 log text, SC-009 result), "Removable vs stays" table; consumer guide §11 step 5 (line-format change of moved checks, CS0104 alias), §12.2 (porting text-line checks to `ConfigProblems`), §12.8 (cache name rule, `not_modified` round trip without the gate); PCR unity-sdk (platform-dependent invalid characters, name from settings); 013 release notes BC-4 step 3 (kit type names clash with game helpers). **Framework finding (not fixed, for T070):** `UnityConfigCache.Create` validates with the running platform's `Path.GetInvalidFileNameChars()`, so a name such as `localhost:5080` throws in the Windows Editor but is accepted on Android/iOS; a portable rule would refuse the same set everywhere. No other framework defect found.
- [ ] T068 Final verification (after T065–T067): Debug and Release build of `GameServer.slnx`; full suite in memory and
  with `GAMESERVER_TEST_STORE=File`; opt-in `GAMESERVER_PERF=1` run of `ConfigPhasePerformanceTests`, the perf sections
  of `ConfigCompressionTests` and `PersistentConfigCacheTests`, and the 014 endpoint-check performance test (budget
  unchanged); WireGen `verify` for the three configs; `git diff --stat main -- proto src/*/Protos` shows only the five
  fields; `docs/api/gm-openapi.json` unchanged (no GM API change); `git status` lists only intended files (no probe,
  scratch output or Unity `Library` change).
  Done when: 0 warnings, 0 failures; counts against T001 and the per-story checkpoints recorded. (FR-091, SC-005, SC-006,
  SC-007)
- [ ] T069 Couchbase full suite (after T068; run through PowerShell in the background, variables from the resume file,
  password never printed).
  Done when: 0 failures; total/passed/skipped and duration recorded; a known flaky class is rerun alone before
  reporting. (FR-091)
- [ ] T070 Tech-lead review (after T068): `tech-lead-reviewer` gets the spec, the plan, this file and the list of
  changed files (`git diff --stat main`); focus: loader all-or-nothing and unchanged line texts; rules never weaker than
  `Load`; catalog binding (no lazy fallback, host override in both orders, module-alone error); check isolation and
  marker classification; provision config-free; version formula, determinism and API-03 (no field changes meaning);
  compression scope (only `GetConfig`, `Vary`, never larger, BREACH); bundle byte equality and the startup size check;
  SDK cache atomicity, corruption handling and path safety; SDK public API additive only; package `.meta`; docs
  accuracy.
  Done when: findings recorded (Blocker/Major/Minor with file:line). (FR-091)
- [ ] T071 Review fixes (after T070): fix every finding the orchestrator accepts, each with a test that fails without the
  fix where practical; update docs, release notes and PCRs touched by a fix; rerun the affected classes, then T068 and
  T069. Append `## Review (T070/T071, …)` to this file in the 014 format (finding, fix, test, docs; deviations; test
  delta).
  Done when: suites green; review section complete. (FR-091)
- [ ] T072 Resume update `AgentMemories/features/p05-speckit/00_summary.md` (after T071): 015 row DONE with the task count
  and review result; "Technical notes from 015" (config phase order, declarations and the kit call, `GameConfigLoad` /
  `GameConfigStatus` / markers, check creation, version formula and the five fields, `ConfigBundle` and compression
  constants, persistent cache format, `SeededConfig`, test counts); open items (T058/T067 if pending, owner
  confirmations 1–3, any V fallback).
  Done when: the file names the next slice (016 login-command) and how to resume. (resume DoD)

## Dependencies

```text
Phase 1  T001 ─┬─ T002 (release notes skeleton) ────────────────────────────► T028, T033, T040, T045, T052, T064, T065
               ├─ T003 (V1 GATE) ───────────► T016
               ├─ T004 (V2–V4 GATE) ────────► T020, T029
               ├─ T005 (V5 GATE) ───────────► T046
               ├─ T006 (V6, V7) ────────────► T048, T050
               ├─ T007 (V12, V13) ──────────► T014, T020, T021
               ├─ T008 (V8 Editor/Win GATE) ► T049, T051
               └─ T009 (V10, V11) ──────────► T042
Phase 2  T010 → T011 ; T010 → T012 → T013 ; T014 ∥ ; T015 ∥
US1      T016 (T003,T013) ∥ T017 (T010) → T018 (T011,T012) → T019 ; T020 (T004,T007)
         → T021 (T016,T018,T020) → T022 ∥ T023 ∥ T024 (T015) → T025 ; T026 (T014) → T027 → T028
US2      T029 (T004,T012,T021) → T030 → T031 → T032 (T014) → T033
US3      T034 (T016) → T035 → T036 ; T037 ∥ ; T038 → T039 → T040
US4      T041 (T039) → T042 (T009) → T043 ∥ T044 → T045                  [lane beside US5]
US5      T046 (T005,T035) → T047 → T048 (T006) → T049 (T008) → T050 → T051 (T008) → T052
US6      T053 (T019) ∥ T054 (T011) ∥ T055 (T022–T024) ; T056 (T043) → T057 (T051) → T058 (manual) ; T059..T064 (docs)
Close    T065 → T066 → T067 (manual) → T068 → T069 (Couchbase, background) → T070 → T071 → T072
```

- Phase 1 blocks everything; each V-check blocks only the tasks named in the [VERIFY] table. **T003, T004, T005 and
  T008 are gates**: the loader duplicate line, the check-creation strategy, the bundle construction and the SDK
  compression branch follow their decisions.
- **US1 needs only Phase 2.** US2 follows US1 because it extends `ConfigStartup` (status) and the same
  `GameServerApplicationExtensions.cs`. US3 needs only T016 and Phase 2 and could run as a lane beside US1/US2 (then
  T034 adds `ClientConfigVersion` to the test `IGameConfig` wrappers written later, Note). US4 and US5 both need US3
  and run as parallel lanes. US6 waits for US1–US5 (docs describe shipped behaviour).
- Shared files that force order (no `[P]` between these tasks):
  - `src/GameServer.Platform.Web/Hosting/GameServerApplicationExtensions.cs`: T021 → T029 → T047.
  - `src/GameServer.Platform.Web/Hosting/ConfigStartup.cs`: T021 → T030 → T037 → T046 → T047.
  - `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`: T013 → T020 → T029 → T030.
  - `src/GameServer.Platform/Config/FileGameConfigLoader.cs`: T016 → T034.
  - `src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs`: T035 → T046 → T048.
  - `src/GameServer.Platform.Web/Hosting/ProvisioningRunner.cs`: T029 → T031.
  - `src/GameServer.Core/Config/IGameConfig.cs`, `src/GameServer.TestKit/TestGameConfig.cs`: T034 only.
  - `sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs`: T039 → T049 (conditional).
  - `tests/Shared/config/base/*`: T015 only (later tasks seed through `SeededConfig`).
  - `tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs`: T029 (unchanged) → T031;
    `ConfigBundleTests.cs`: T046 → T047; `ConfigCompressionTests.cs`: T048 → T050.
  - `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs`: T056 only.
  - `specs/015-config/release-notes-draft.md`: T002 → T028 → T033 → T040 → T045 → T052 → T064 → T065 → T071.
  - Docs: `docs/guides/consumer-guide.md` T059 → T066 → T071; `docs/pcr/platform.md` T061 → T071.
- Parallel examples: T002 ∥ T003 ∥ T004 ∥ T005 ∥ T006 ∥ T007 ∥ T009 (T008 needs a free Unity); T012 ∥ T011; T014 ∥ T015;
  T016 ∥ T017; T022 ∥ T023 ∥ T024; T036 ∥ T037; US4 lane (T041–T045) ∥ US5 lane (T046–T052); T053 ∥ T054 ∥ T055;
  T060 ∥ T061 ∥ T062 ∥ T063 ∥ T064.

**Critical path**: T001 → T003 → T010 → T011 → T012 → T013 → T016 → T018 → T021 → T024 → T026 → T027 → T028 → T029 →
T030 → T031 → T032 → T033 → T034 → T035 → T038 → T039 → T046 → T047 → T048 → T049 → T050 → T051 → T056 → T057 → T059 →
T065 → T066 → T068 → T069 → T070 → T071 → T072 (the US4 lane T041–T045 runs beside T046–T051 and must end before T056).
T058 and T067 are manual (owner-assisted) and off the critical path, but their status must be in the release notes
before T065 closes.

## Implementation strategy

1. **MVP = US1 (Phases 1–3).** Loader report, declarations, rules, eager catalogs, kit call; shippable at T028.
2. **US2** (check isolation, status, provision) on the phase US1 created.
3. **US3** (versions and the five fields, SDK tracking), then **US4** (SDK cache) and **US5** (bundle, size check,
   compression) as two lanes.
4. **US6** (fixture, template, KestrelDemo, Unity runs, docs), then the close (release notes, walkthrough, first
   consumer, full suites incl. Couchbase, review, resume).

With one implementer run the phases in order. With two: lane 1 = US1 → US2 → US5; lane 2 (after T016) = US3 → US4;
merge before US6.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The shared test config breaks a new rule (`broken` shop item) | High (every host test red) | T015 before T024; T022–T024 run the full suite; `SeededConfig` for seeded faults |
| `AllowDuplicateProperties` gives no usable path or position (V1) | Medium (duplicate line without location) | T003 gate; own `Utf8JsonReader` walk, same line |
| `[Obsolete]` on the 4-argument constructor before every caller migrates | Medium (build red, CS0618) | T034 migrates all five callers in the same change; pragma only in obsolete-path tests |
| Pinned version values in our own tests (`test+000000000000`) | Low | T034 asserts the computed value (Planning note 9) |
| A hidden eager catalog resolution (V12) | Medium (`InvalidOperationException` instead of a line) | T007 inventory; T021 consumer test; the phase is the first block |
| `ActivatorUtilities` picks another constructor (V3) or the marker is lost (V2) | Medium (a check created differently; "did not run" degraded) | T004 gate; `CreateFactory`; `ReferenceEquals(Violations)` fallback |
| Test hosts lose default modules when a `modules` callback adds test modules | Medium (tests silently test less) | T021: callbacks call `SampleTestHost.DefaultModules` first |
| Rules weaker than `Load` | Medium (a catalog line instead of a rule line) | T025 property-style agreement test |
| Docs/metric list tests fail on new keys and metrics | Low | T037 (`TelemetryTests`), T061 (`DocumentedConfigurationKeysTests` §21) |
| Per-request compression over SC-005 (V6) | Medium | T006 measures first; T050 fallback: lower level, gzip splice |
| A Unity platform advertises gzip without decoding (V8) | High for that platform | T008 gate; T049 conditional sniff; rollback switch; T058 before the tag |
| Unity writes a random-GUID `.meta` into the `file:` package | Medium (GUID churn) | hand-written `.meta` in T041–T043 (and T049) before T051/T057; `UnityPackageMetaTests`; `git status sdk/unity` |
| Device run not possible before the tag | Medium (FR-091 open) | T058 pending in the release notes with exact steps; the tag waits |
| zh-pvp tests in the temp clone fail for an earlier slice's BC | Low | apply the 013/014 release-notes steps in the clone only, record |
| Couchbase suite slow or flaky | Low | Couchbase only in T069; rerun the flaky class alone |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T013, T016, T026 |
| FR-002 | T003, T016, T026 |
| FR-003 | T016, T021, T026 |
| FR-010 | T010, T017 |
| FR-011 | T010, T011, T012, T018 |
| FR-012 | T010, T018, T021 |
| FR-013 | T018, T021, T026 |
| FR-014 | T017, T018, T021, T026, T031 |
| FR-015 | T022, T062 |
| FR-016 | T023, T062 |
| FR-017 | T011, T022, T023 |
| FR-018 | T019, T028, T053 |
| FR-020 | T010, T018 |
| FR-021 | T020, T021, T027 |
| FR-022 | T012, T018, T021 |
| FR-023 | T018, T027 |
| FR-024 | T022, T023, T025, T055 |
| FR-025 | T055, T060 |
| FR-030 | T004, T029, T032 |
| FR-031 | T012, T029, T032 |
| FR-032 | T013, T030, T031, T032 |
| FR-033 | T032 |
| FR-034 | T033, T059, T061 |
| FR-040 | T034 |
| FR-041 | T034 |
| FR-042 | T034 |
| FR-043 | T035, T036, T038 |
| FR-044 | T035, T036, T037, T047 |
| FR-045 | T034 |
| FR-046 | T039 |
| FR-047 | T034, T036 |
| FR-050 | T006, T048 |
| FR-051 | T048 |
| FR-052 | T005, T046, T048, T050 |
| FR-053 | T046, T047 |
| FR-054 | T035, T046, T048 |
| FR-055 | T008, T049, T051, T058 |
| FR-056 | T048 |
| FR-060 | T042, T043 |
| FR-061 | T042, T044 |
| FR-062 | T009, T042, T044 |
| FR-063 | T042 |
| FR-064 | T042, T063 |
| FR-065 | T038, T039, T041 |
| FR-066 | T042, T045 |
| FR-067 | T041, T042, T044 |
| FR-070 | T015, T024, T053, T055 |
| FR-071 | T056, T057 |
| FR-075 | T059, T066 |
| FR-076 | T054, T060, T061, T062, T063, T064 |
| FR-077 | T059, T060, T064 |
| FR-090 | T002, T028, T033, T040, T045, T052, T064, T065 |
| FR-091 | T001, T051, T057, T058, T066, T067, T068, T069, T070, T071 |

Every FR of the spec (FR-001..FR-003, FR-010..FR-018, FR-020..FR-025, FR-030..FR-034, FR-040..FR-047, FR-050..FR-056,
FR-060..FR-067, FR-070, FR-071, FR-075..FR-077, FR-090, FR-091) has at least one task.

| Success criterion | Tasks |
| --- | --- |
| SC-001 | T016, T026, T028 |
| SC-002 | T027 |
| SC-003 | T029, T032 |
| SC-004 | T034, T036, T039 |
| SC-005 | T006, T050, T068 |
| SC-006 | T009, T042, T044, T045 |
| SC-007 | T050, T068 |
| SC-008 | T001, T039, T041, T045 |
| SC-009 | T002, T040, T065, T067 (the removal itself is owned by the game session) |

Scenarios:
- US1: US1-1 → T021, T026; US1-2 → T023, T026; US1-3 → T024, T026; US1-4 → T027; US1-5 → T018, T021, T026;
  US1-6 → T018, T026; US1-7 → T016, T021, T026; US1-8 → T016, T026; US1-9 → T026; US1-10 → T028.
- US2: US2-1 → T032; US2-2 → T029, T032; US2-3 → T030, T031, T032; US2-4 → T032; US2-5 → T033, T059, T061.
- US3: US3-1, US3-2 → T034, T039; US3-3, US3-4, US3-7 → T034; US3-5 → T036, T037; US3-6 → T036.
- US4: US4-1 → T044; US4-2 → T042, T044; US4-3, US4-4, US4-6 → T042; US4-5 → T042, T044; US4-7 → T043, T057.
- US5: US5-1..US5-4 → T048; US5-5 → T047; US5-6 → T008, T049, T051, T058.
- US6: US6-1 → T024, T055; US6-2 → T059..T064, T066.

Breaking changes: BC-1 → T015, T022, T023, T024, T026, T028; BC-2 → T003, T016, T028; BC-3 → T016, T028;
BC-4 → T039, T040; BC-5 → T001, T034, T040; BC-6 → T034, T040, T067; BC-7 → T047, T052; BC-8 → T019, T020, T028;
BC-9 → T031, T033; BC-10 → T029, T033.

[VERIFY] items: V1 → T003, T016; V2, V3 → T004, T029; V4 → T004, T020, T029; V5 → T005, T046; V6 → T006, T048, T050;
V7 → T006, T048; V8 → T008, T049, T051, T058; V9 → T058, T049; V10 → T009, T042, T057, T058; V11 → T009, T042, T045,
T058; V12 → T007, T021; V13 → T007, T014, T031.

Manual or owner-assisted tasks: T008 and T051 and T057 (agent-run Unity; owner only if an Editor holds the project),
T058 (Android device and iOS build/device: owner), T066 (fresh reviewer agent), T067 (first-consumer temp clone; owner
may defer it to the game session).

## Planning notes (gaps between spec, plan and code, resolved here)

1. **Decision names.** The spec's D1–D13 (Assumptions) and the plan's D1–D17 (Design decisions) are two numbering
   spaces. Spec D6 has been rewritten to the additive `client_config_version` (research R1); the plan still says
   "overrides spec D6" and "spec D6 rejected", which refers to the old draft default. Tasks implement the additive
   field. Owner confirmations 1–3 stay open and are tracked in the release notes (T002, T065); they do not block a task.
2. **Versions moved from plan phase 1 to US3 (T034).** Only US3, US4 and US5 consume `ClientConfigVersion`, and the
   change carries BC-5 (new server value) and BC-6 (obsolete API). Keeping it out of the foundations keeps US1 and US2
   free of pinned-value churn and keeps the obsolete mark and every caller migration in one change.
3. **`GameConfigLoad` and the unavailability marker moved to the foundations (T012, T013).** The plan puts them in its
   US2 phase, but the US1 config phase needs "one load per start" and the catalog marker (`ValidateServices` skip,
   `ConfigCatalogStore`). Provision `Mode = NotLoaded` is still set in US2 (T031).
4. **Sample game declarations in US1 (T024).** Spec FR-070 lists them under story 6, but story 1's independent test
   ("Start the sample host (economy, IAP, sample game)"), its "sample game file with a wrong type" fault and the 7-line
   SC-001 example need the `battle.json` rule. US6 keeps the fixture, the template, the architecture test, KestrelDemo
   and the docs.
5. **Shared test config conflicts with the new rules.** `tests/Shared/config/base/shop.json` has an item `broken` whose
   grant resource `unknown_item` is not in `resources.json`. It is used by `ShopTests` (`UNKNOWN_ITEM` case). The
   `sample.shop` rule (data-model §2.2) refuses it, so every host test in the 7 projects that link `tests/Shared/config`
   would fail at startup once T024 lands. T015 removes the item first and moves the case to a host-free handler test.
   The other shared files pass (the 128-character `analytics_size_probe_…` product id is at the limit, not over it).
6. **Core `InternalsVisibleTo`.** The plan grants Core's internals to `GameServer.Platform` only. `ConfigProblems` and
   `ConfigContext` have internal constructors, and the foundation tests of `ConfigJson` and the declarations need
   instances before the engine exists. T010 also adds `GameServer.Platform.Tests`, as every `src` project already does
   for its test project.
7. **`ConfigJson` fix text.** contracts/public-api.md gives the value helpers no `fix` parameter, while every line needs
   a rule-specific fix (startup-report §4: "Fix: set the largest quantity a player may hold (ECO-07)"). T011 adds a
   trailing optional `string? fix = null` to each value helper (additive to the contract) with a generic default.
8. **Guard tests the plan does not list.** `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` restricts
   which Platform internals the kit may use (T019 extends its allow-list).
   `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs` reads only PCR §13/§17/§18/§19, so the new §21 must be
   added for `GameServer:ConfigCompression` to be checked (T061). `tests/GameServer.Observability.Tests/TelemetryTests.cs`
   holds the metric-name list (T037).
9. **Callers of the obsolete API.** The plan lists `RunnerHarness` and `TestGameConfig`. The code also has
   `ModuleRegistryTests.cs:176` and `DevProfileMiddlewareTests.cs:212`, plus `FileGameConfigLoader.cs:65` itself, and
   `CommandRunnerTests.cs:42` pins `test+000000000000` from `RunnerHarness`. T034 migrates all of them and asserts the
   computed value.
10. **New package files.** contracts/public-api.md names three new SDK files (`PersistentConfigCache.cs`,
    `AssemblyInfo.cs`, `UnityConfigCache.cs`); the plan also has `CacheFileOps.cs`. Four new files, each with a
    hand-written `.meta` (T041–T043), five if the R13 fallback `ConfigBody.cs` is needed (T049).
11. **A duplicate product key is a loader fault, not a rule fault.** SC-001 and story 1 list "duplicate key" among the
    `iap.json` faults that name "module, file and location", but data-model §2.2 and FR-002 make a repeated property a
    loader fault: one `[config] '…/iap.json' has property '{key}' twice at $.products …` line, no module, and (all or
    nothing) no rule line. T026 asserts that line.
12. **The startup config line lands in US5 (T047).** Data-model §5.6 includes the client version and the bundle size, so
    the line is written once both exist (plan N8), not in US1 or US3.
13. **Provision and the `config:` override.** R8 lets a host or test `IGameConfig` instance bypass `GameConfigLoad`, so
    in provision mode with `TestHost`'s `config:` a check that takes `IGameConfig` still runs. T031 tests BC-9 through
    the loader path (`SampleTestHost` uses `GameServer:ConfigPath`, V13) and records the bypass for PCR §17.
14. **`docs/html/03-deploy.html`.** FR-076 lists only `01` and `02`, but `03-deploy.html` documents provision mode,
    readiness and operator keys, which BC-9, the readiness `clientConfigVersion`, the new metric, the rollback switch
    and BC-7 change. T064 updates it.
15. **Template declaration is optional.** `WireMapperRegistrationTests` and `WireConformanceTests` start hosts with the
    template module and no `template.json`; a required declaration would fail them. T054 uses `Optional`.
16. **SC-007 is measured in US5 (T050).** The budget covers load, rules, catalogs **and** the bundle size check; the last
    step exists only after T047.
17. **Device checks need the persistent cache in KestrelDemo.** V10/V11 on devices are checked with the KestrelDemo app,
    so the device run (T058) sits after T056/T057 in US6, not in US5. V8 on devices rides on the same run.
18. **`TryAdd` and host registrations made later.** The architect decision ("registered in `AddModule` if not already
    registered") and plan N4 ("a host registration after the module also wins") both hold: `TryAdd` covers "before",
    `DeclaredCatalogBindings` (last non-keyed descriptor) covers "after". T020 and T027 test both orders.
19. **Unity platforms on this machine.** The Unity install has Android and Windows standalone support only, no iOS, and
    `smoke-build.ps1` has no Windows-player switch. The Windows player in T008 is built by a temporary probe script
    that is deleted afterwards; iOS is owner-only (T058).
20. **Where the host report tests live.** The plan puts them in `Platform.Web.Tests` on `SampleTestHost`, whose defaults
    are Identity + Counter; the 7-line example needs economy, IAP and the sample game, which `tests/SampleGame.Tests`
    already composes (`IapStartupValidationTests`). T026–T028 and T036 live there; module-agnostic phase tests stay in
    `Platform.Web.Tests` (T021, T032).
21. **SDK public-API baseline.** `TestData/sdk-public-api.txt` was recorded at the 014 base. SC-008 compares with `main`
    before this slice, so T001 re-records it at `c6b133f` (a superset).
22. **Plan phase order.** The plan runs its US2 phase in parallel with the loader/engine phase. Here US2 follows US1,
    because the status is published by the phase US1 creates and both edit `GameServerApplicationExtensions.cs`. US3 can
    still run as a lane after T016.
23. **Task count.** 72 tasks, matching the plan's estimate (~72), distributed per story instead of per plan phase.
