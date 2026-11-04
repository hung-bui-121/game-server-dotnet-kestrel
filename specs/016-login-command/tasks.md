# Tasks: Login and command robustness — new-player flag, account initializer, degraded reads of a broken aggregate, typed command-size rejections, caller-owned request ids (slice 016 login-command)

**Input**: [plan.md](plan.md) (design, spec defaults D1–D15, architect decisions A1–A12, narrowings N1–N12, breaking
changes BC-1..BC-11), [spec.md](spec.md) (US1–US6, FR-001..FR-092, BC-1..BC-11, SC-001..SC-010, assumptions D1–D15),
[research.md](research.md) (R1–R17, VERIFY V1–V12, guard tests R16), [data-model.md](data-model.md),
[contracts/public-api.md](contracts/public-api.md), [contracts/wire-changes.md](contracts/wire-changes.md),
[contracts/sdk-pending-store.md](contracts/sdk-pending-store.md),
[contracts/errors-and-report-lines.md](contracts/errors-and-report-lines.md). **Where this file and plan.md differ, this
file wins** (see Planning notes at the end).

**Architect decisions applied** (owner questions 1–6 of the plan stay open for the owner; the release notes track their
status, T002):
- **`IAccountWriter`** (`GameServer.Platform.Commands`) writes the profile of **every** login. Each transaction attempt
  decides create-or-update from what it read, with a fresh `CommandContext` per attempt. `LoginResponse.is_new_player`
  and the analytics `createdPlayer` come only from the committed attempt's `Created` (A1, A2).
- **Creation receipt** `identity.create_account`: written only when an initializer changed an aggregate, reserved a
  claim or added an outbox entry; actor System with `ActorId` = player id; a random UUID request id generated once per
  login call (A3).
- **Initializers** share one `CommandContext`; each runs through its own `InitializerContext` view limited to its
  declared touches (A4).
- **Broken aggregates**: `GameServerException` with the new codes `AGGREGATE_VERSION_AHEAD` (503, retryable 1000 ms) /
  `AGGREGATE_UNREADABLE` (500), no inner exception, a "reported" marker in `Exception.Data` so `ExceptionMiddleware`
  does not log twice. Non-throwing `TryLoad` / `ReadAvailableAsync` / `ReadAllAvailableAsync` are added; `Load` and the
  reader signatures stay. `SectionWriter` is the one mapping site; an unavailable section is listed and never sent with
  an empty payload. A broken `profile` fails login and bearer authentication with the typed code. The GM player view
  gets an `unreadable` list (A5, A6).
- **Oversize**: every commit rule, receipt size included, runs before the first staged write (runner and creation
  writer). `COMMAND_OUTPUT_TOO_LARGE` goes through the runner's rejection path (HTTP 200). `CommandResponse.details`
  (field 11) carries `part`/`bytes`/`limit` and the details of every HTTP 200 rejection, module codes included (A7, A8,
  BC-11).
- **Login store failures** answer `COMMIT_UNKNOWN_RETRY` / `CONCURRENT_UPDATE` instead of 500 (A9, BC-10).
- **SDK**: `SendCommandAsync(route, request, parser, string? requestId, ct = default)` with the 4th parameter required
  (null = generated); `GameServerClient.NewRequestId()`; `CommandResult.RequestId`; opt-in
  `GameServerClientOptions.PendingCommands` with a per-entry-file `PersistentPendingCommandStore` reusing `CacheFileOps`
  (A10, A11). A same-player re-login keeps the last good copy of sections it lists as unavailable (A12).
- **TestKit**: the receipt builder is shared with the runner (byte-identical parity); `Seed` gets a revision overload.
- **Sample seeding**: an optional server-only `account.json` that `samples/SampleHost/config` has and
  `tests/Shared/config` lacks (D12 changed).
- **Release note**: 016 writes `docs/releases/v0.7.0.md` as a **draft** merging the 013/014/015/016 drafts, cross-slice
  ids written `0NN/BC-n` (D13 changed). The release date, the restamp of the pages marked "chưa phát hành", the tag and
  any push are a separate round-close step and **not** tasks of this file.
- **WireGen**: regenerate and verify all three configs; only the SDK `Core.cs` and `Identity.cs` change.

**Tests**: mandatory (constitution VI). Each story phase writes its tests before or with the code they cover and ends
green. Couchbase tests skip with a message when `COUCHBASE_CONNSTR` is not set; the full Couchbase suite runs in the
final gate (T080). Opt-in suite `GAMESERVER_PERF=1`: first-login cost (SC-003), pending-store record/remove (SC-008).
Scenario ids (`US2-3`) are the spec's acceptance scenarios.

**Format**: `- [ ] T0NN [P] [USn] description with paths`, then `Done when:` (verifiable) and the FR/SC/BC ids covered.
`[P]` = different files and no open dependency. Record findings as `Note:` lines under the task.

**MVP = US1 (Phases 1–3).** The writer that decides create-or-update per attempt, the flag, the analytics fix and the
typed login store failures. US2 extends the same writer with initializers. US1 ends at a shippable checkpoint (T022).

**[VERIFY]**: a runtime or platform fact from research.md to confirm on the pinned versions (.NET SDK 10.0.403, ASP.NET
Core 10.0.14, `Google.Protobuf` 3.36.2, the pinned Couchbase .NET SDK, Unity 6000.3.22f1) **before** the task that
depends on it. Each check runs in a throw-away project under the session scratch folder (never committed), except the
Unity probes, which are temporary files in KestrelDemo deleted with their `.meta` afterwards. Record the result as a
`Note:`. When the fact is wrong, apply the stated fallback and record the deviation (contract, line text, docs).

| Item | Fact | Fallback | Check | Used by |
| --- | --- | --- | --- | --- |
| V1 | Unity 6000.3.22f1 binds `SendCommandAsync(r, q, p)`, `(r, q, p, default)`, `(r, q, p, ct)` to the existing overload and `(r, q, p, "id")`, `(r, q, p, null)` to the new one (as Roslyn of .NET SDK 10.0.403 at C# 9 does) | rename the new method `SendCommandWithIdAsync` (same parameters) | T003 (**gate**, Editor), T068 (Editor + Android IL2CPP compile with the real package) | T055 |
| V2 | A non-SDK exception thrown by the transaction body before any staged write (`CommandRejectedException`, typed read failure, `AccountInitializerFailedException`) is rethrown unchanged by the Couchbase, in-memory and File stores, and nothing commits | unwrap the inner-exception chain for our types in the store adapter | T006 (**gate**), T053 (final, Couchbase) | T025, T026, T047, T048 |
| V3 | 20 concurrent first logins + 5 lost-response retries of one credential on Couchbase end with 1 profile insert, 1 receipt, 1 ledger entry within 3 race rounds and the 10 s expiration | 5 race rounds for logins; exhaustion stays `CONCURRENT_UPDATE` (retryable) | T029 | T025 |
| V4 | Two sequential `GetManyAsync` calls in one attempt (profile, then anchor + touches) work on all three stores; a concurrent anchor removal after the anchor rewrite is a write-write conflict (010 R9) | one bundle read of profile + anchor + touches in every login attempt | T006 (**gate**), T028, T029 | T025 |
| V5 | An exception thrown by `PlayerBearerAuthenticationHandler.HandleAuthenticateAsync` reaches `ExceptionMiddleware` unchanged through `AuthenticateAsync` and `ProfileEnforcementMiddleware` | catch the marked exception in the handler and `Fail` with code and details (the envelope loses only the retry delay) | T005 (**gate**) | T040 |
| V6 | The loader raises for a damaged document exactly: STJ `JsonException`, `NotSupportedException`, `InvalidOperationException` (`GetValue<T>`, null state), `FormatException`/`OverflowException`, and whatever an upgrader throws | catch every exception except `OperationCanceledException`/`OutOfMemoryException` in the deserialize block | T004 (**gate**) | T034 |
| V7 | Runner and double give byte-identical receipts for the same inputs; the only variable-length inputs are those of research R14 | add the missing input to the double's configuration (e.g. `WithCorrelationId`) and its XML doc | T052 | T051 |
| V8 | Record (temp write + `Flush(true)` + `File.Move`) and remove (`File.Delete`) of a 4 KiB entry each ≤ 10 ms p95 outside Unity, on C: and D: | record with `Flush(false)`, documented in PCR unity-sdk | T007, T061 | T057 |
| V9 | `File.Move(tmp, final)` with `final` absent is a rename, and `Directory.EnumerateFiles(folder, "*.pending")` works, on Windows .NET, Unity Mono (Editor, Windows player), Android and iOS IL2CPP (stripping High) | one container file with `File.Replace` (the 015 shape) | T007 (.NET), T068 (Editor, Windows player), T069 (Android, iOS; owner) | T057 |
| V10 | `map<string,string> details` and `repeated UnavailableSection` regenerate with WireGen into C# 9 code on `Google.Protobuf` 3.36.2 | fix the generator config; `details` as `repeated Detail {key, value}` (still additive) | T012 | T012 |
| V11 | The GM OpenAPI generator emits `unreadable[]` of the player view and `details`/`unavailable` of the GM command response | a hand-written schema fragment registered for those types, documented in PCR gm | T008 (**gate**) | T043, T049 |
| V12 | The per-localId and per-IP login limits allow the SC-002 tests only with raised test settings; production defaults stay | none (test settings) | T009 | T027, T029, T031 |

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
`test --solution GameServer.slnx -c Release --no-build` (one class: `--project X --filter-class "*Name"`). Couchbase
variables as in `AgentMemories/features/p05-speckit/00_summary.md` ("Environment"; password from the user variable
`COUCHBASE_PW`, never printed). **Never grep recursively over `samples/`** (Unity `Library/` under KestrelDemo). Search
`src/`, `tests/`, `templates/`, `tools/`, `sdk/` and, by project folder only, `samples/SampleHost`,
`samples/SampleGame/SampleGame`, `samples/SampleGame/SampleGame.Adapters`. Open KestrelDemo files by exact path
(`samples/SampleGame/KestrelDemo/Assets/...`). Unity 6000.3.22f1 is
`D:\DevTools\Unity\Unity 6000.3.22f1\Editor\Unity.exe` (playback engines: Android, Windows standalone; **no iOS**). Unity
runs through `sdk/unity/ci/smoke-build.ps1 -Unity "<that path>"` (`-CompileOnly`, `-E2E [-ServerUrl]`, default = Android
APK build), only when no Editor has KestrelDemo open (no `Temp/UnityLockfile`, no `Unity.exe` process). Editor and
Windows-player checks are agent-run; Android and iOS IL2CPP **runs** are owner-only. SampleHost for Unity runs:
`tools/local/run-local.ps1 -Port 5080` (never `-Reset`). Port 5180 belongs to the owner and is never bound or probed.
Scratch probes and proxies use 5091/5092. `.specify/feature.json` already points to `specs/016-login-command`.
Repository docs are in Vietnamese, except the consumer guide and the release notes (English). Keys, field names,
commands and code stay literal. **Never edit `E:\ProjGits\*`** (first-consumer checks run in temp clones only). No commit
is part of any task (commits: no Claude signature lines, owner rule).

## Phase 1: Setup

- [x] T001 Baseline on branch `016-login-command` (base `5904b74`): Debug and Release build of `GameServer.slnx`; full
  suite in memory (no `COUCHBASE_*`) and with `GAMESERVER_TEST_STORE=File`. Record into the scratch folder (not
  committed):
  (a) SHA-256 of every file under `sdk/unity/com.gameserver.client/Runtime/Generated/` (input of T012: only
  `gameserver/core/v1/Core.cs` and `Identity.cs` may change);
  (b) the GUID of every `.meta` under `sdk/unity/com.gameserver.client/` (input of T056, T060, T062: existing GUIDs never
  change);
  (c) the public-API snapshot of `sdk/GameServer.Client` (Release, reflection, one sorted line per type and member, the
  014/015 procedure). Compare it with `tests/GameServer.Client.Tests/TestData/sdk-public-api.txt`: when the `5904b74`
  snapshot is a superset (015 additions such as `PersistentConfigCache`), replace the committed file with it, so SC-009
  compares against `main` before this slice (research R16);
  (d) the SampleHost server `config_version` (`samples/SampleHost/config`) and the client version, at the base (T063
  expects the server value to change once and the client value to stay);
  (e) total/passed/skipped per configuration and `git status` (only `specs/016-login-command/` and the owner's `tasks/`
  untracked).
  Done when: 0 warnings in both configurations, 0 failures; the counts, the scratch paths and the (c)/(d) values recorded
  as a Note (reference for every checkpoint and T079). (FR-046, FR-092, SC-009)
  Note (2026-11-04, `6054a04` = `5904b74` + docs): Debug and Release 0 warnings, 0 errors. In memory 2321 total / 2220
  passed / 101 skipped / 0 failed; `GAMESERVER_TEST_STORE=File` the same 2321 / 2220 / 101 / 0 (`COUCHBASE_*` unset).
  Scratch (session folder, not committed): `t001a-generated-sha256.txt` (15 files under `Runtime/Generated/`),
  `t001b-meta-guids.txt` (35 `.meta` GUIDs of the package), `t001c-sdk-public-api-5904b74.txt`, `t001-test-{mem,file}.log`.
  (c) the base snapshot (948 lines) is a strict superset of the committed `sdk-public-api.txt` (928 lines): +20 lines, 0
  removed — `PersistentConfigCache` (type + 8 members), `GameServerClient.ClientConfigVersion` and the five
  `ClientConfigVersion` proto members of 015. The committed file was replaced with the base snapshot (LF, as before).
  (d) SampleHost config at the base: label `sample-1`, server `sample-1+8a37f29ee2dc`, client `sample-1+9b25b8b6bc0b`
  (computed with `FileGameConfigLoader.Load` on `samples/SampleHost/config`). (e) `git status`: only the owner's
  `tasks/` untracked (plus the re-recorded snapshot).
- [x] T002 [P] Release notes draft `specs/016-login-command/release-notes-draft.md` (FR-090), created now and extended by
  every story checkpoint (T022, T032, T046, T054, T062, T075) before the final pass (T076). English, like the 013–015
  drafts. Skeleton:
  - summary: fixed FG-013, FG-015, FG-029, FG-016, FG-022; "no route and no persisted document shape changes; five
    additive proto fields and one message; additive GM JSON members; no existing field changes meaning (API-03); the Unity
    package stays 0.7.0";
  - BC-1..BC-11 table (change, who is affected, how it surfaces, fix), filled from the spec's breaking-changes table;
  - the first-consumer migration checklist in the order of plan "Breaking changes and migration" (bump →
    `InitAccountHandler` into an `IAccountInitializer` → reset dev data → kit-based tests → client boot on the login
    sections → request ids and the pending store for P2 commands → remove the six markers), with every zh-pvp file:line
    of the spec's "First-consumer migration" table (`InitAccountHandler.cs:16`, `:17`, `GameModuleDefinition.cs:25`,
    `GameModule.cs:20,34-38`, `AccountInitTests.cs:124`, `docs/pcr/game.md` lines 41-57, 66-69, 78, 79; client
    `OnlineSession.cs:83`, `:200`, `:214-217`, `:276`, `:279-320`, `PlayerReadModel.cs:24`; client markers written without
    the colon);
  - the rolling-deploy note (version-ahead degradation, deploy order rule kept) and the compatibility table of
    contracts/wire-changes.md §5;
  - "Decisions awaiting owner override": plan owner questions 1–6 (A3/N4, A8/BC-11, D12, D13, A9/BC-10, A12), each
    "applied as architect decision, owner confirmation pending";
  - manual checks table (T003, T064, T068, T069, T077, T078), each "pending";
  - "Known before the tag".
  Done when: the file exists; every FG id and every BC id appears; the checklist lists the six markers. (FR-090, SC-010)
  Note: skeleton written with a section per FG id (filled at the checkpoints), BC-1..BC-11 from the spec table ("how it
  surfaces" marked pending per checkpoint), the 7-step checklist with every zh-pvp file:line, the rolling-deploy note and
  compatibility table, the six owner decisions, a "Platform facts checked before coding" table with the V1, V2, V4, V5,
  V6, V8, V9, V11, V12 results, the manual checks table and "Known before the tag" (single-scheme hosts, T005; 015 T058).
  BC-5 gains the state-not-an-object finding of T004; BC-7 names the optional `correlationId` of Planning note 8.
- [x] T003 **[VERIFY] V1 — decision gate for the SDK overload (agent-run Unity Editor)**: a **temporary** editor script
  `samples/SampleGame/KestrelDemo/Assets/Editor/Probe016.cs` (created for this run, deleted with its `.meta` afterwards,
  never committed) declares a stub class with the two overloads of public-api §7.1 (`SendCommandAsync<T>(string, IMessage,
  MessageParser<T>, CancellationToken = default)` and `SendCommandAsync<T>(string, IMessage, MessageParser<T>, string?
  requestId, CancellationToken = default)`, each recording which one ran) and `Probe016.Run` calls the five shapes of V1
  and logs the overload chosen per call. Run `smoke-build.ps1 -Unity "<path>" -CompileOnly` (0 `error CS`/`warning CS`
  in `compile.log`), then `Unity.exe -batchmode -nographics -projectPath samples/SampleGame/KestrelDemo -executeMethod
  Probe016.Run -logFile <scratch>/probe016-editor.log` (editor update loop, as `SmokeBuild.EndToEnd`).
  Done when: a Note records the binding of each shape, any `CS0121`, and the **decision** for T055 (new overload as in
  public-api §7.1, or the V1 fallback name); the probe and its `.meta` are gone and `git status samples/` shows no change.
  If no free Editor is available, the task stays open with these commands and T055 waits. (FR-040, FR-046)
  Note (2026-11-04, no Editor open: no `Temp/UnityLockfile`, no `Unity.exe`): `smoke-build.ps1 -CompileOnly` exit 0 in
  66 s, 0 `error CS`/`warning CS` in `compile.log` (`Assembly-CSharp-Editor.rsp` has `-langversion:9.0`). `Probe016.Run`
  (exit 0) logged: `(r, q, p)` → existing; `(r, q, p, default)` → existing; `(r, q, p, ct)` → existing; `(r, q, p, "id")`
  → new (`requestId = id`); `(r, q, p, null)` → new (`requestId = null`); no `CS0121`. **Decision for T055: the new
  overload exactly as public-api §7.1 (no `SendCommandWithIdAsync` rename).** Probe and `.meta` deleted; `git status
  samples/ sdk/` clean (no tracked settings file changed); the project stays on Android (`UNITY_ANDROID` defines). PS 5.1
  note: `smoke-build.ps1` needs `-ProjectPath` passed explicitly when run through `powershell -File` (its `$PSScriptRoot`
  default is empty there); calling it with `&` from the repository root works.
- [x] T004 [P] **[VERIFY] V6 — decision gate for the read-failure classification** in a throw-away test project
  referencing `src/GameServer.Platform` (session scratch): call today's `AggregateDocumentSerializer.Load` for a
  `PlayerAggregateDescriptor` with one upgrader, on documents with: state of the wrong shape, wrong primitive types,
  `schemaVersion` as a string, `rev` as an object, `createdAtUtc` as a number, `state` missing, `state` as an array,
  `state: null`, an absurd number (`1e400`, `-9223372036854775809`), an upgrader throwing `KeyNotFoundException`, a
  missing upgrader step, `schemaVersion` above the code's.
  Done when: a Note records per document the exception type (and whether it is wrapped), and the **decision** for T034:
  an explicit type list (V6 holds) or the catch-all except `OperationCanceledException`/`OutOfMemoryException`
  (fallback). (FR-020, FR-021)
  Note (scratch console on the Release `GameServer.Platform`, descriptor schema 3 with upgraders 1→2, 2→3; state
  `{gold:int, items:string[], kind:enum}`): wrong shape (`gold` object) → `JsonException` (inner
  `InvalidOperationException`); `items` object → `JsonException`; `gold:"abc"` → `JsonException` (inner `FormatException`);
  `gold:true`, `items:[1,2]` → `JsonException` (inner `InvalidOperationException`); unknown enum → `JsonException`;
  `gold:"12"` loads (Web defaults read numbers from strings: not damage); `schemaVersion:"3"` → `InvalidOperationException`;
  `schemaVersion:3000000000` → `OverflowException`; `schemaVersion:0` → `InvalidOperationException` (no upgrader from 0);
  `rev:{}` → `FormatException`; `rev:"x"` → `InvalidOperationException`; `createdAtUtc:5` → `InvalidOperationException`;
  `rev:1e400` → `FormatException`; `rev:-9223372036854775809` → `OverflowException`; `gold:1e400` and
  `gold:-9223372036854775809` → `JsonException` (inner `FormatException`); upgrader throwing → its own
  `KeyNotFoundException` unwrapped; upgrader returning null → `InvalidOperationException`; missing upgrader step →
  `InvalidOperationException` (`First`); `schemaVersion` 4 > 3 → `GameServerException(INTERNAL)`. `NotSupportedException`
  never seen. **Finding:** `state` missing, `state:null`, `state:[1,2]` and `state:"x"` do **not** throw today: the loader's
  `content["state"] as JsonObject ?? []` reads them as the default state (a later write would overwrite the damaged
  document). **Decision for T034: V6 holds for the types** — explicit list (`JsonException`, `NotSupportedException`,
  `InvalidOperationException`, `FormatException`, `OverflowException`) for the envelope fields and the deserialize block;
  catch-all except `OperationCanceledException`/`OutOfMemoryException` for the upgrade chain (an upgrader may throw
  anything, research R5); **plus** an explicit check that a present document's `state` is a JSON object, else `unreadable`
  (data-model §2.1 "null state"; recorded in BC-5 of the release notes).
- [x] T005 [P] **[VERIFY] V5 — decision gate for the broken-profile bearer path** in a throw-away web project referencing
  `src/GameServer.Platform.Web` (scratch port 5091): an authentication handler that throws a `GameServerException(503,
  retry-after 1000 ms, Data marker)` from `HandleAuthenticateAsync`, behind the real `ExceptionMiddleware` and
  `ProfileEnforcementMiddleware` order of `UseGameServer`; record the HTTP status, the envelope code, `retry_after_ms`,
  `details` and the log lines.
  Done when: a Note records the result and the **decision** for T040: let the typed exception propagate (V5 holds), or
  catch it in `PlayerBearerAuthenticationHandler` and `Fail` with code and details (fallback). (FR-023)
  Note: run in-process (`TestHost` from the TestKit = the real `UseGameServer` pipeline over TestServer; no port bound, so
  5091 was not needed). The `PlayerBearer` scheme's handler type was swapped for a handler throwing `GameServerException(24,
  503, retryable) { RetryAfterMs = 1000, Details = {aggregate=profile} }` with a `Data` marker; `POST
  /api/v1/player/snapshot` with a bearer token. (a) Identity + test authentication, and (b) Identity + GM (no test
  authentication, the zh-pvp shape): HTTP 503, `Retry-After: 1`, envelope `AGGREGATE_VERSION_AHEAD`/24, `retryable = true`,
  `retry_after_ms = 1000`, details `aggregate=profile`; one `ExceptionMiddleware` Error line `Request failed with
  AGGREGATE_VERSION_AHEAD: …` carrying the same exception instance with its marker, plus the request-log line. (c)
  Identity only, no test authentication (**exactly one** scheme): ASP.NET makes the single scheme the default and
  `WebApplication` adds its own `UseAuthentication` in front of the whole pipeline, so the handler runs there: raw HTTP 500
  `text/plain` from `DeveloperExceptionPageMiddleware`, no envelope, no `ExceptionMiddleware` or request-log line. **V5 holds
  only when `ProfileEnforcementMiddleware` is the first to authenticate. Decision for T040: the fallback** — the handler
  catches the marked read failure (`AggregateReadFailure.TryGet`) and returns `Fail(code, details)`; `ErrorResponseWriter`
  uses 1000 ms for a retryable code without an explicit delay, so the envelope keeps the same retry delay; and nothing is
  logged a second time. The single-scheme behaviour for other handler exceptions is pre-existing and out of scope (release
  notes "Known before the tag").
- [x] T006 [P] **[VERIFY] V2 and V4 — decision gate for "rules before staging" and the second read** in a throw-away test
  project referencing `src/GameServer.Platform`, `src/GameServer.Platform.Couchbase` and `src/GameServer.Platform.Testing`,
  against the in-memory store, the File store and Couchbase (transactions on; skipped with a message without
  `COUCHBASE_CONNSTR`):
  - (V2) a transaction body that throws a private exception type (a) before any staged write, (b) after one staged
    insert; record the exception type and inner chain the caller sees and the documents committed;
  - (V4) a body with two sequential `GetManyAsync` calls (one key, then two keys including an "anchor" document); a
    concurrent transaction removing the anchor after the body rewrote it; record the outcome per store.
  Done when: a Note records the results per store and the **decisions** for T025/T047 (no unwrap needed, or the unwrap
  fallback in the store adapter) and for T025 (sequential second read, or the single bundle read fallback). (FR-012,
  FR-013, FR-014, FR-031)
  Note (scratch console over `InMemoryDocumentStore`, `FileDocumentStore` and `CouchbaseDocumentStore` on local Couchbase
  EE 7.6.12, own scope provisioned and dropped): **V2** — on all three stores, a private exception and a
  `GameServerException` thrown (a) before any staged write and (b) after one staged insert reach the caller as the **same
  instance** (`Exception.Data` marker kept, no inner exception), the body ran once, and nothing was committed. **Decision
  for T025/T047: no unwrap in the store adapter.** **V4** — a body with `GetManyAsync([profile])` then
  `GetManyAsync([anchor, touch])`, anchor rewrite and two inserts commits in one attempt on all three stores. A concurrent
  transaction removing the anchor after the body rewrote it: in-memory and File → the remover commits first (optimistic),
  the login's commit conflicts, the re-run reads no anchor and throws the anchor-missing exception (nothing written);
  Couchbase → the remover hits the staged write (write-write conflict, 3 attempts), waits for the login to commit, then
  removes the rewritten anchor (profile committed, anchor gone; the purge then removes the profile as in 010 R9). In both
  cases no creation commits over a removal it did not see. **Decision for T016/T025: sequential second read** (no bundle
  read of profile + anchor + touches for returning logins).
- [x] T007 [P] **[VERIFY] V8 and V9 outside Unity** in a scratch netstandard2.1 / C# 9 library and a net10.0 console:
  per-entry record (write `{id}.pending.tmp`, `Flush(true)`, `File.Move` to `{id}.pending`) and remove (`File.Delete`)
  of a 4 KiB entry, 200 iterations after warm-up, on C: (`%TEMP%`, where `persistentDataPath` lives on Windows) and D:
  (repository drive); `File.Move` when the destination exists (exception type); `Directory.EnumerateFiles(folder,
  "*.pending")` does not return `*.pending.tmp`; a child process killed between flush and move leaves a complete temp
  file and no final file.
  Done when: a Note records p50/p95 per drive and operation, the semantics, the exception types and the fallback chosen
  for T057 (none, or `Flush(false)`); SC-008's reference drive is stated. Unity Mono rides on T068, devices on T069.
  (FR-045, FR-047, SC-008)
  Note (library netstandard2.1 / `LangVersion 9.0` / warnings as errors, 0 warnings; console net10.0 Release; 20 warm-up +
  200 iterations of a 4 KiB entry): C: (`%TEMP%`) `Flush(true)` record p50 3.08 ms / p95 6.09 ms (max 8.41), remove p50
  0.48 / p95 1.52 ms; `Flush(false)` record p95 1.69 ms. D: (`D:\MyProjects\AI\framework`, outside the repository)
  `Flush(true)` record p50 24.64 / **p95 32.24 ms** (max 42.40), remove p95 0.46 ms; `Flush(false)` record p95 0.37 ms.
  Semantics: `File.Move(tmp, final)` with `final` absent is a rename; onto an existing final `System.IO.IOException`
  (HResult 0x800700B7); `Directory.EnumerateFiles(folder, "*.pending")` returns neither `*.pending.tmp` nor `x.pendingx`, but
  returns `y.PENDING` (Windows names are case-insensitive: T057's loader must check the name is a lowercase UUID +
  `.pending` before parsing); a child killed after `Flush(true)` and before the move leaves a complete 4096-byte temp file
  and no final file. **Decision for T057: no fallback (`Flush(true)` kept)**; SC-008's reference drive is **C:**, the drive
  of `persistentDataPath` on Windows (`%USERPROFILE%\AppData\LocalLow`); the D: figure (a repository drive no player has)
  is recorded only, as 015 did for its cache save.
- [x] T008 [P] **[VERIFY] V11 — decision gate for the GM OpenAPI shapes** in a throw-away test referencing
  `src/GameServer.Platform.Web`: run the GM OpenAPI generator (`src/GameServer.Platform.Web/OpenApi/GmOpenApi.cs`) on a
  scratch GM endpoint returning a record shaped like `GmPlayerView` with an init property `IReadOnlyList<GmUnreadableAggregate>`
  and like `GmCommandResponse` with init properties `IReadOnlyDictionary<string, string> Details` and
  `IReadOnlyList<GmUnavailableAggregate> Unavailable` (wire-changes §4).
  Done when: a Note records the emitted schema of each member and the **decision** for T043/T049 (generator output as
  is, or a hand-written fragment). (FR-025, FR-030)
  Note (scratch app with `GmOpenApi.Add` + `MapOpenApi(GmOpenApi.Route)` and four `/gm/v1/probe/*` endpoints returning
  `Results.Json`): with `.Produces<T>()` the generator emits `unreadable` as `{"type":"array","items":{"$ref":
  ".../ProbeUnreadableAggregate"}}` (members `id`, `code`, `reason` strings, `storedSchemaVersion`/`codeSchemaVersion`
  int32, all required), `details` as `{"type":"object","additionalProperties":{"type":"string"}}`, `unavailable` as an
  array of `$ref` `{id, code}`; init properties are not in `required`. **Without** `Produces` (how the GM player view
  `GET /gm/v1/players/{playerId}` and every `MapGmCommand` endpoint are mapped today) the operation has only `200: OK` and
  no schema: `docs/api/gm-openapi.json` describes neither response now. **Decision for T043/T049: generator output as is**,
  adding `.Produces<GmPlayerView>()` to the player view route (T043) and `.Produces<GmCommandResponse>()` in
  `MapGmCommandCore` (T043, so T049 only regenerates); no hand-written fragment. Consequence to record in T043: the first
  regeneration adds the full schemas of both responses (every GM command operation gains a `200` schema), not only the
  new members.
- [x] T009 [P] **[VERIFY] V12 and the test-expectation inventory** (read-only; never grep `samples/` as a root):
  - (V12) the login limits in `src/GameServer.Modules.Identity.Adapters/IdentityOptions.cs` (`LoginPerLocalIdPerMinute`
    = 10, `FailedLoginPerIpPerMinute` = 60, `LoginPerIpWarnPerMinute` = 600) and the settings the SC-002 tests set;
  - tests that pin today's behaviour and must change: `tests/GameServer.Platform.Tests/Aggregates/AggregateBudgetAndUpgradeTests.cs`
    `Newer_stored_schema_than_code_is_an_error` (`INTERNAL`) → T034; `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs`
    `Oversized_receipt_is_a_programming_error_and_rolls_back` and `Oversized_result_compact_is_a_programming_error`
    (`InvalidOperationException`) → T048; `tests/GameServer.Platform.Web.Tests/CommandContextConformanceTests.cs`
    `The_result_size_check_is_the_runners` (old message) → T048; any other test asserting `INTERNAL`,
    `JsonException` or `InvalidOperationException` from `Load`/`PlayerSnapshotReader` or a 500 for an oversized command
    (`grep` over `tests/`);
  - tests that start the **real SampleHost config** through `LocalFlavorFactory` (`tests/Sample.Counter.Tests/SampleHostSmokeTests.cs`,
    `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs`,
    `tests/GameServer.Platform.Web.Tests/{ConfigCompressionTests,WireCatalogTests,LateEndpointValidationTests}.cs`,
    `tests/SampleGame.Tests/CatalogCompositionTests.cs`, `tests/GameServer.Architecture.Tests/*`, `tests/GameServer.Client.Tests/SdkRouteTests.cs`):
    for each, whether it logs in a new player and asserts gold, ledger rows, receipt counts or revisions (these see the
    seed once `account.json` lands, T063) → T064;
  - consumer-fixture tests that assert a new player's `notes` or `card` state or receipt counts after login
    (`FixtureCatalogTests.cs:75` asserts an empty card title) → T065;
  - construction sites of `LoginResult`, `CommandExecution` (3 sites), `GmCommandResponse`, `GmPlayerView` and
    `LoginCompletion.CompleteAsync` callers (`GuestLoginService.cs:66`, `ProviderLoginService.cs:75`);
  - docs that state a 500 for a newer schema or an oversized receipt (`docs/guides/consumer-guide.md:347`,
    `docs/pcr/platform.md` §3, §6 and line 1061, `docs/html/02-them-command.html` §6b) → T070, T072, T075.
  Done when: a Note lists every item with the task that changes it; items not listed above are added to that task's Note.
  (FR-012, FR-050, FR-092, SC-002)
  Note (V12): `IdentityOptions.cs:29` `LoginPerLocalIdPerMinute = 10` (guest login only: `GuestLoginService.cs:46`, a fixed
  1-minute window; the test `FakeClock` does not advance it), `:32` `FailedLoginPerIpPerMinute = 60` (failures only,
  `LoginCompletion.cs:30`), `:35` `LoginPerIpWarnPerMinute = 600` (log only, `LoginCompletion.cs:31`). No test sets
  `LoginPerLocalIdPerMinute` today (only `LoginLimitAndSchemeTests.cs:69` warn = 50, `ProviderIdentityTests.cs:260,281`
  failures = 2). **V12 holds:** the 25 logins of SC-002 exceed 10, so T027/T029/T031 set `Identity:LoginPerLocalIdPerMinute`
  (e.g. 100) in their test settings; production defaults stay.
  Note (pins → task): `AggregateBudgetAndUpgradeTests.cs:97-103` `Newer_stored_schema_than_code_is_an_error` (INTERNAL) →
  T034. `CommandRunnerTests.cs:283` `Oversized_receipt_is_a_programming_error_and_rolls_back` and `:292`
  `Oversized_result_compact_is_a_programming_error` (`InvalidOperationException`) → T048.
  `CommandContextConformanceTests.cs:119` `The_result_size_check_is_the_runners` → T048. Nothing else in `tests/` asserts
  `INTERNAL`, `JsonException` or `InvalidOperationException` from `Load`/`PlayerSnapshotReader` or a 500 for an oversized
  command: the other `INTERNAL`/`InvalidOperationException` asserts are unrelated (`CommandRunnerTests.cs:128`
  `FailBeforeCommit` stays INTERNAL for commands; `:237`, `:246`, `:303-344` handler throw / undeclared write / commit-code
  rules; `CommitRulesTests.cs:26,94,116`; `ErrorContractTests.cs:95` and `RequestOutcomeTests.cs:106-120` injected
  exceptions; `UncataloguedCommitCodeTests.cs:44`). All raw seeds of `pa_*` documents in tests carry a `state` object
  (`GmLookupTests.cs:131`, `AggregateBudgetAndUpgradeTests.cs:99`, `AccountPurgeWriterTests.cs:273`), so T004's new
  state-not-an-object rule changes no existing test.
  Note (real SampleHost config → T064): `Sample.Counter.Tests/SampleHostSmokeTests.cs` logs in new players and asserts the
  `counter` rev (`:59`, `:83`, not seeded), receipts folder non-empty (`:63`, still true), the purchase receipt found by id
  (`:174-177`), and the analytics file count `:131` (2) and payment lines `:139` (2): a gold grant adds only a ledger
  outbox entry (`EconomyOperations.cs:109`), no analytics record, so no change is expected — T064 runs it. The others do
  not log in a player: `Couchbase.IntegrationTests/SampleHostFlavorTests.cs`, `Platform.Web.Tests/{ConfigCompressionTests,
  WireCatalogTests,LateEndpointValidationTests,LocalFlavorFactoryStartupFailureTests,ConfigPhasePerformanceTests}.cs`,
  `SampleGame.Tests/{CatalogCompositionTests,SampleHostConfigurationFileTests}.cs` (route/file checks),
  `Architecture.Tests/FrameworkModuleCoverageTests.cs`, `Client.Tests/SdkRouteTests.cs`. Two items for T063/T064 beyond
  the list: `SampleGame.Tests/SampleConfigRulesTests.cs:91-101` runs the sample's declarations over
  `samples/SampleHost/config` (must stay error-free with the new `account.json` rule) and
  `ConfigCompressionTests.cs:235` builds the SampleHost bundle (unchanged: `account.json` is server-only).
  Note (fixture → T065): `FixtureCatalogTests.cs:66-75` logs in a new player and asserts an empty card title; no fixture
  test asserts `notes` after a login or counts receipts (`SaveNoteHandlerTests` seeds its own state host-free).
  Note (construction sites): `LoginResult` only `LoginCompletion.cs:103`; `CommandExecution` at `CommandRunner.cs:213`
  (rejection), `:260` (commit), `:471` (replay); `GmCommandResponse` only `GmCommandEndpointFactory.cs:191-192`
  (`BuildResponse`, target-typed `new(`); `GmPlayerView` `GmPlayerEndpoints.cs:180` (deleted player) and `:186`;
  `LoginCompletion.CompleteAsync` callers `GuestLoginService.cs:66`, `ProviderLoginService.cs:75`.
  `IPlayerAggregateWriter` stays in use by tests that edit profiles directly (`AccountStateTests.cs:22`,
  `LoginAnalyticsTests.cs:72`, `ProviderIdentityTests.cs:231,242`, `AccountDeletionTests.cs:165`, `DeletionGmTests.cs:211`,
  `ProviderClientTests.cs:91`, `CouchbaseLeaseAndPurgeTests.cs:388`): unaffected (T018 changes only `LoginCompletion`).
  Note (docs → task): `docs/guides/consumer-guide.md:347` (receipt size "runner: INTERNAL") → T070; `docs/pcr/platform.md:73`
  (§3 "vượt = lỗi lập trình (500)"), `:94` (§6 "schemaVersion mới hơn code → 500 INTERNAL"), `:1061` (§19 double has no
  receipt check) → T072; `docs/html/02-them-command.html` §6b and the troubleshooting rows `:1113-1114` ("500, log Result of
  … / Receipt of …") → T075.

**Checkpoint**: baseline counts known; V1, V2/V4, V5, V6 and V11 decided with their consequences; V8, V9 (.NET) and V12
confirmed or their fallbacks recorded; the test-expectation inventory assigns every changed test to a task; release
notes skeleton in place.

## Phase 2: Foundational (behaviour-neutral)

Nothing in this phase changes what a host does: the new codes, members and proto fields exist but nothing raises, reads
or fills them yet, and the runner commits byte-identical receipts. Each task ends with the full suite green with the T001
counts plus the new tests.

- [x] T010 [P] Error codes (after T001; public-api §1.3, errors-and-report-lines §1): `src/GameServer.Core/Errors/CoreErrorCodes.cs`
  gains `CommandOutputTooLarge` (23, `COMMAND_OUTPUT_TOO_LARGE`, 200, not retryable), `AggregateVersionAhead` (24, 503,
  retryable) and `AggregateUnreadable` (25, 500), with the safe messages of §1, all listed in `All`. The SDK
  `sdk/unity/com.gameserver.client/Runtime/Core/GameServerError.cs` `ErrorCodes` gains the three string constants.
  Tests: a new `tests/GameServer.Platform.Tests/Registry/CoreErrorCodeCatalogTests.cs` (number, name, HTTP status,
  retryable flag and message of the three codes asserted literally; numbers unique in `All`; inside 0–999);
  `tests/GameServer.Architecture.Tests/ErrorCodeBandTests.cs` green unchanged (core codes outside the game band);
  `SdkPublicApiTests` re-recorded with exactly three added lines.
  Done when: green; nothing raises the codes yet. (FR-020, FR-030, BC-4)
  Note: codes 23/24/25 added after `MethodNotAllowed` with XML docs and listed last in `All`; SDK `ErrorCodes` gains the
  three constants. `CoreErrorCodeCatalogTests` (4 tests) green; `ErrorCodeBandTests` (3) green unchanged. The snapshot was
  re-recorded once for T010 + T012 together: the three `ErrorCodes` lines plus 30 generated lines (T012 Note), 0 removed.
- [x] T011 [P] Core initializer API (after T001; public-api §1.1, §1.2, data-model §1.1): new
  `src/GameServer.Core/Accounts/AccountInitializers.cs` (`IAccountInitializer`, `AccountInitializerRegistration.Of<T>(name,
  touches, maxClaimMarkers = 0)` with `Name`, `InitializerType`, `Touches` (copied, order kept), `MaxClaimMarkers`;
  `ArgumentNullException` for null name or touches; every value rule is a startup line, T023, not an exception), and
  `src/GameServer.Core/Modules/ModuleDescriptor.cs` `AccountInitializers` (default `[]`, XML doc of §1.2).
  Tests in a new `tests/GameServer.Platform.Tests/Accounts/AccountInitializerRegistrationTests.cs`: `Of<T>` keeps name,
  type, touches and claim count; touches list is a copy; null arguments throw; a descriptor without initializers has an
  empty list. `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` ARC-01 rule green unchanged (Core free of
  ASP.NET Core and Protobuf).
  Done when: green; nothing reads the member yet. (FR-010, FR-018)
  Note: `AccountInitializerRegistration` has a private constructor (only `Of<T>` creates it); `Touches` is copied with
  `ToArray()`. `AccountInitializerRegistrationTests` 5 tests green (incl. "values are not checked here": an invalid name,
  `profile` twice and 9 claims are kept for T023 to report). ARC-01 rule green (Core still only references DI abstractions).
- [x] T012 Proto fields and regeneration (after T001; wire-changes §1, research R17, **[VERIFY] V10**):
  `proto/gameserver/core/v1/core.proto` (message `UnavailableSection {id = 1, code = 2, code_number = 3}`,
  `SnapshotResponse.unavailable_sections = 5`, `CommandResponse.unavailable_sections = 10`, `CommandResponse.details =
  11` as `map<string, string>`) and `src/GameServer.Modules.Identity.Adapters/Protos/identity.proto`
  (`LoginResponse.is_new_player = 8`, `unavailable_sections = 9`). Run `tools/GameServer.WireGen` `generate` then `verify`
  for `sdk/GameServer.Client/wiregen.json`, `samples/SampleGame/wiregen.json` and
  `tests/ConsumerFixture/tools/wiregen.json`.
  Done when: Release build 0 warnings (server projects through Grpc.Tools, `sdk/GameServer.Client` C# 9 /
  netstandard2.1); compared with T001 (a), only `Runtime/Generated/gameserver/core/v1/Core.cs` and
  `Runtime/Generated/Identity.cs` changed, their `.meta` GUIDs unchanged; the sample and fixture configs show no diff (an
  unexpected diff is a Note, not committed blindly); `tests/GameServer.WireGen.Tests/CommittedOutputTests.cs` green for
  all three; `git diff main -- proto src/*/Protos` shows only the five new field numbers and the new message;
  `SdkPublicApiTests` re-recorded with only added lines (generated members). A Note records the V10 result (the map
  generates like `ErrorEnvelope.details`). (FR-001, FR-022, FR-024, FR-030, FR-046, SC-009)
  Note: `generate` — SDK config "2 written, 4 unchanged" (`Runtime/Generated/Identity.cs`, `gameserver/core/v1/Core.cs`),
  sample "0 written, 4 unchanged", fixture "0 written, 3 unchanged"; `verify` exit 0 for all three (6, 4 and 3 files up to
  date). SHA-256 against T001 (a): only those two files differ; the 35 package `.meta` GUIDs are unchanged. `git diff main
  -- proto src/*/Protos`: only the new message `UnavailableSection` (1-3) and fields `SnapshotResponse` 5,
  `CommandResponse` 10/11, `LoginResponse` 8/9. **V10 holds:** `map<string,string> details` generates as
  `MapField<string,string> Details { get; }` like `ErrorEnvelope.details`, the repeated message as `RepeatedField<…>`;
  `sdk/GameServer.Client` (C# 9, netstandard2.1) and the server projects build with 0 warnings. `sdk-public-api.txt`
  re-recorded: +33 lines (3 `ErrorCodes` constants of T010; `UnavailableSection` type + 19 members; `IsNewPlayer`,
  `UnavailableSections` and their field numbers on `LoginResponse`, `SnapshotResponse`, `CommandResponse`, and
  `CommandResponse.Details`), 0 removed. `CommittedOutputTests` green in the full suite.
- [x] T013 [P] `ReceiptBuilder` extraction (after T001; research R14, public-api §2.1 internals): new internal
  `src/GameServer.Platform/Commands/ReceiptBuilder.cs` (`static ReceiptDocument Build(in ReceiptInputs inputs)`) and
  `ReceiptInputs` (readonly record struct: receipt id, game, environment, actor id/type, player, operation, request id,
  body hash, result code, result compact bytes, config version, schema versions, aggregates with new revs, claim keys,
  outbox, correlation id, committed-at). `src/GameServer.Platform/Commands/CommandRunner.cs` `BuildReceipt` (389-426)
  becomes a call to it; nothing else moves.
  Tests in a new `tests/GameServer.Platform.Tests/Commands/ReceiptBuilderTests.cs`: for three runner commands of the
  `RunnerHarness` probe module (no aggregate, one write with a claim, an outbox entry), the receipt JSON read back from
  the store equals `ReceiptBuilder.Build` of the same inputs byte for byte and `ReceiptDocument.SizeOf` agrees; field
  order unchanged. Every existing `CommandRunnerTests`/`CommitRulesTests` green unchanged.
  Done when: green; the runner's receipts are byte-identical to the base (one golden receipt from T001's suite compared
  in the Note). (FR-034, SC-005)
  Note: `ReceiptInputs` takes the schema aggregates as `IEnumerable<PlayerAggregateDescriptor>` (Touches ∪ Reads in
  order), the written aggregates as `ReceiptAggregate`s, claim keys as `DocumentKey`s and the outbox entries; the builder
  formats claim keys `collection/key`, the actor type and the times as before. `ReceiptBuilderTests` (4) green: for
  `probe.nochange`, `probe.claim`, `probe.outboxflood` the stored JSON equals the builder output byte for byte, `SizeOf`
  agrees, and the field order is the base's — which includes the computed `committedAt` serialized last (pre-existing,
  kept). Golden check: the same four runs (`probe.nochange`, `probe.claim`, `probe.outboxflood`, `probe.grow`, fixed
  request id, inline relay off) dumped from a scratch `git worktree` of the base and from this tree give identical files
  (SHA-256 `458053A7A59C5E83E1ADF82EBF7A96A0ED70406E059C0C0443C1E44D87EA8C5B`); worktree and dump test removed.
- [x] T014 [P] `CommitViolation` and the receipt-size rule (after T010; research R8, errors-and-report-lines §5):
  `src/GameServer.Platform/Commands/CommitRules.cs` gains internal `CommitViolation(ErrorCode Code,
  IReadOnlyDictionary<string, string> Details, string Line)` and `CheckReceiptSize(string operation, int receiptBytes,
  CommandSettings settings) → CommitViolation?` (code `COMMAND_OUTPUT_TOO_LARGE`, details `part=receipt`, `bytes`,
  `limit`; line = the §5 receipt line; "greater than" rule). `CheckResultSize` is unchanged until T047; no caller of the
  new rule yet.
  Tests in a new `tests/GameServer.Platform.Tests/Commands/CommitRulesSizeTests.cs`: at the limit → null; limit + 1 →
  violation with the three details and the literal §5 line; the initializer form of the operation label
  (`identity.create_account (initializer sample.starting_gold)`) kept verbatim.
  Done when: green. (FR-031, FR-034)
  Note: `CommitViolation` lives at the end of `CommitRules.cs` with `PartDetail`/`BytesDetail`/`LimitDetail` constants and
  the `OutputTooLarge(part, bytes, limit, line)` factory (details formatted with the invariant culture), so T048 builds the
  result violation the same way; `CommitRules.OutputPartReceipt = "receipt"`. `CommitRulesSizeTests` (4) green.
- [x] T015 [P] Shared test support (after T011; used by US1–US4): new files linked into every project that compiles
  `tests/Shared/*.cs` (Client, Gm, Observability, Couchbase, Platform.Web tests):
  - `tests/Shared/CapturedLog.cs`: an `ILoggerProvider` recording (category, level, message, exception type) for a host's
    `services` callback, with `Lines(category)` and `Count(level, prefix)` (the plan's "`CapturedLog`" does not exist
    yet; Planning note 6);
  - `tests/Shared/StartingStateModule.cs`: a test module (business + adapters halves in the file) with a client-visible
    aggregate `starter` (wire mapper declared, 014) and an initializer `test.starting_state` (touches `wallet`, `starter`)
    whose behaviour is chosen per host through a DI-registered `StartingStateBehaviour` (`Grant(amount)` through
    `EconomyOperations.Grant` with ledger source `account_init`, `Nothing`, `Reject(code)`, `Throw`, `WriteUndeclared`,
    `AddAudit`, `OversizeAggregate`, `FloodOutbox(n)`, `Claim(n)`), and an invocation counter;
  - `tests/Shared/BrokenDocuments.cs`: seeds one broken document per reason of data-model §2.1 for a player:
    version ahead (`schemaVersion` = code + 1), unreadable (state of the wrong shape), upgrade failed (a `broken_probe`
    test aggregate at schema 2 whose 1→2 upgrader throws for a marker value), mapper failed (a client-visible test
    aggregate whose mapper throws for a marker value); plus the undamaged twin of each.
  Tests: a new `tests/GameServer.Platform.Web.Tests/SharedTestSupportTests.cs` — the module starts in a host with
  Identity + Economy (`StartingStateBehaviour.Nothing`), the broken seeds are readable as raw documents, `CapturedLog`
  sees a startup line.
  Done when: green in every linking project (the module is registered only where a test adds it). (test support for
  FR-011..FR-014, FR-020..FR-026, FR-031)
  Note (deviation, naming only): the TestKit already has a public record `GameServer.TestKit.CapturedLog` (013) and
  `TestHost.LogEntries`, globally imported in `tests/Shared/GlobalUsings.cs` — Planning note 6 missed it, and a second
  type named `CapturedLog` would be ambiguous (CS0104) in every test file that uses both namespaces. So the file is
  `tests/Shared/CapturedLogSink.cs`: `CapturedLogSink : ILoggerProvider` records into the kit's `CapturedLog` record
  (category, level, event id, message, properties, exception) for hosts a test composes itself, and `CapturedLogQueries`
  adds `Lines(category)` and `Count(level, prefix)` to any `IEnumerable<CapturedLog>` (so `host.LogEntries.Count(…)` works
  too). `tests/Shared/StartingStateModule.cs`: module `test` (range 21000-21099, depends on `economy`), aggregates
  `starter` (`StarterState`, hidden `Padding`, HardBytes 8 KiB, mapper → `sample.counter.v1.CounterState` that throws for
  `Padding = "mapper-fail"`) and `broken_probe` (schema 2, upgrader 1→2 throws for `fail: true`), claim collection
  `starting_state_claims`, initializer `test.starting_state` (touches `wallet`, `starter`; 2 claims);
  `StartingStateBehaviour` factories `Nothing/Grant(n)/Reject(code)/Throw/WriteUndeclared/AddAudit/OversizeAggregate/
  FloodOutbox(n)/Claim(n)`, `Become(next)` to switch a running host, `Invocations`; the module `TryAdd`s `Nothing`.
  `tests/Shared/BrokenDocuments.cs`: `BrokenReason` + `Document(reason, player, damaged)`/`Seed(store, …)` (rev 3, twin
  value 7). `SharedTestSupportTests` (6) green. Phase 2 checkpoint (after T015): Release 0 warnings; in memory and File
  2344 total / 2243 passed / 101 skipped / 0 failed each (2321 + 23 new).

**Checkpoint**: Release build 0 warnings; full suite green with the T001 counts plus the new tests; the only generated
diff is `Core.cs` and `Identity.cs`; receipts byte-identical to the base.

## Phase 3: US1 — The login tells the client the player is new (P1, FG-013) MVP

**Goal**: one writer for every login, the create-or-update decision taken per attempt, `is_new_player` and
`isNewUser` from the committed attempt, typed login store failures.

**Independent test**: the SC-001 login matrix (T019) plus the racing first logins (T018); the SDK reads the flag (T020).

- [x] T016 [US1] `IAccountWriter` without initializers (after T012, T013, **T006** decision V4; research R1,
  public-api §2.1, data-model §1.2): new `src/GameServer.Platform/Commands/AccountWriter.cs` (public `IAccountWriter`,
  `AccountLoginWrite<TProfile>`, `AccountWriteResult<TProfile>`; internal sealed `AccountWriter`) and
  `src/GameServer.Platform/Commands/AccountCreation.cs` (public static `AccountCreation.Operation =
  "identity.create_account"`). Each attempt: bundle-read the profile → **present**: `Mutate`, replace when changed,
  `Created = false`; **absent**: second read of the creation anchor (V4 decision), anchor missing or naming another
  player → `AggregateAnchorMissingException` (unchanged), `Mutate(profile.CreateDefault())`, stage anchor rewrite and
  profile insert, `Created = true`. The decision is returned from the lambda (never captured outside an attempt); the
  outer race loop keeps 3 rounds on `DocumentExistsException`/`StaleReadException`. The request token is used only
  before the transaction (PERF-02); expiration from `GameServerOptions.Transactions.ExpirationSeconds`. Register it in
  `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs` (singleton, root provider).
  Tests in a new `tests/GameServer.Platform.Tests/Commands/AccountWriterTests.cs` (in-memory store of `RunnerHarness`):
  returning profile → one read, one replace, `Created = false`, no anchor read; absent → anchor + profile insert,
  `Created = true`, rev 1; anchor missing → `AggregateAnchorMissingException`, 0 documents; a body run twice by the store
  (forced optimistic conflict between attempts) decides from the second read; a racing insert makes the next round a
  normal login (`Created = false`); `Mutate` throwing `GameServerException` propagates and writes nothing.
  Done when: green; `LoginCompletion` still uses `IPlayerAggregateWriter` (T018 switches it). (FR-001, FR-012, US1-1,
  US1-2, US1-3)
  Note: the attempt branches on the profile read first: present → `Mutate` + replace (no anchor read); absent → read the
  anchor (V4: sequential second read), `Mutate(CreateDefault())`, then stage anchor rewrite + profile insert. A mutation
  that leaves the state unchanged writes nothing (`Created = false`), as `PlayerAggregateWriter` did. A `TProfile` that is
  not the registered `profile` state type is an `InvalidOperationException` before any I/O. `CreationReceiptId` is always
  null until T025. `aggregate_size_bytes` is recorded after the commit from the committed attempt (the old writer recorded
  it inside the body). `AccountWriterTests` (in-memory store of `RunnerHarness` plus a module declaring `identities`, and a
  recording `ITransactionalDocumentStore` decorator): first login = `read pa_profile`, `read identities`, `replace
  identities`, `insert pa_profile`, rev 1; returning = `read pa_profile`, `replace pa_profile`; missing anchor and anchor of
  another player → `AggregateAnchorMissingException`, nothing staged; a competing profile committed after this attempt
  staged its insert → the store runs the body twice and the second run is a normal login (`Created = false`, epoch 6 over
  5); a competing profile committed between the reads → the insert reports "exists", the next round is a normal login;
  a `GameServerException` from `Mutate` propagates as the same instance for a first and a returning login, nothing written.
- [x] T017 [US1] Typed login store failures in the writer (after T016; research R1 "Exceptions → login answer", BC-10):
  `AccountWriter` maps `TransactionCommitAmbiguousException` → `GameServerException(COMMIT_UNKNOWN_RETRY, retry-after
  1000 ms)`, `TransactionConflictExpiredException` and exhausted race rounds → `CONCURRENT_UPDATE` (retry-after 200 ms),
  `TransactionFailedException` → `INTERNAL` (as the runner); `AggregateAnchorMissingException` and
  `GameServerException` pass through.
  Tests in `AccountWriterTests` with `StoreFaultInjector` (`FailBeforeCommit`, `AmbiguousBeforeCommit`, the conflict
  faults): each maps to its code with 0 documents written, for a returning and for a first login; 4 consecutive insert
  races → `CONCURRENT_UPDATE`.
  Done when: green; the codes and retry delays equal research R1's table. (FR-014, BC-10)
  Note: mapped in `AccountWriter` around the store call, with the inner store exception kept: ambiguous →
  `COMMIT_UNKNOWN_RETRY` 1000 ms, expired → `CONCURRENT_UPDATE` 200 ms, failed → `INTERNAL`; round 3 of an insert race or
  stale read → `CONCURRENT_UPDATE` 200 ms (inner `DocumentExistsException`). No transaction metrics yet (the creation's
  `txn_*` with operation `identity.create_account` come in T025). Tests: `FailBeforeCommit` and `AmbiguousBeforeCommit` for a
  first and a returning login (profile document unchanged); an expired transaction (expiration 0 s + `ConflictOnce`) for
  both; a `ConflictOnce` inside the expiration is retried by the store (body twice, created); 4 faked insert races →
  `CONCURRENT_UPDATE` after 3 body runs, 0 profiles; 2 faked races → created in round 3. `AccountWriterTests` 18 green.
- [x] T018 [US1] `LoginCompletion` on the writer (after T017; research R1, public-api §4):
  `src/GameServer.Modules.Identity.Adapters/LoginCompletion.cs` takes `IAccountWriter` instead of
  `IPlayerAggregateWriter`; `WriteEpochAsync` passes the same `mutate` lambda (epoch + 1, identity key, late access
  check) in an `AccountLoginWrite` with the creation anchor `identities/{mappingKey}` and the correlation id;
  `LoginResult` gains init property `IsNewPlayer` (= `Created`); `analytics.LoggedIn(…, createdPlayer: result.Created)`;
  `CompleteAsync` gains a trailing optional `string? correlationId = null` (Planning note 8) that
  `src/GameServer.Modules.Identity.Adapters/GuestLoginService.cs` and `ProviderLoginService.cs` fill from the request's
  correlation id. `ProfileCache.Set` unchanged.
  Tests: `tests/GameServer.Platform.Web.Tests/LoginAnalyticsTests.cs` gains the T3 regression — two racing first logins
  of one credential (released together behind `AfterMappingRead`) give exactly one record with `isNewUser = true`; the
  existing purge-race test (`tests/GameServer.Platform.Web.Tests/AccountDeletionTests.cs`, US2-5 of 010) and
  `IdentityLoginTests`, `ProviderIdentityTests`, `LoginLimitAndSchemeTests` green unchanged.
  Done when: green; `grep IPlayerAggregateWriter src/GameServer.Modules.Identity.Adapters` finds nothing. (FR-001,
  FR-002, US1-4, BC-7)
  Note: the pre-read stays for the access check only; `createdPlayer` and `IsNewPlayer` come from `result.Created`. A null
  `correlationId` stores an empty correlation id. **Deviation (additive):** to "fill from the request's correlation id",
  `GuestLoginService.LoginAsync` and `ProviderLoginService.LoginAsync` (public members too) also gain a trailing optional
  `string? correlationId = null`; `IdentityModule`'s login endpoints pass `CorrelationMiddleware.GetCorrelationId(http)`.
  Listed in BC-7 of the release notes. `LoginAnalyticsTests` gains `Two_racing_first_logins_of_one_credential_report_one_new_user`
  (both logins held behind `AfterMappingRead` until both arrived: one record with `isNewUser = true`, one response with
  `IsNewPlayer`). Green unchanged: `AccountDeletionTests` (12, incl. the purge race), `IdentityLoginTests` (11),
  `ProviderIdentityTests` (10), `LoginLimitAndSchemeTests` (4), `AccountPurgeTests` (32), `AccountStateTests` (4). `grep
  IPlayerAggregateWriter src/GameServer.Modules.Identity.Adapters` finds nothing.
- [x] T019 [US1] Flag on the wire and the SC-001 matrix (after T018; wire-changes §2):
  `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs` `LoginResponse(...)` sets `IsNewPlayer =
  result.IsNewPlayer` (sections unchanged until T039).
  Tests in a new `tests/GameServer.Platform.Web.Tests/NewPlayerFlagTests.cs`: new guest (true), returning guest (false),
  new provider identity (true), returning provider identity (false), confirmed `switch_account` into a new account
  (true), `switch_account` not confirmed (`SWITCH_ACCOUNT`, nothing created), takeover from a second device (false),
  lost first-login response (server committed, response dropped, re-login → false, epoch + 1); in every row the
  captured analytics `isNewUser` equals the flag.
  Done when: green; 100 % of rows match SC-001. (FR-001, FR-002, US1-1..US1-4, SC-001)
  Note: `NewPlayerFlagTests` (5 tests, 8 rows): new/returning guest (true, false), new/returning provider identity (true,
  false), unconfirmed `switch_account` (409 `SWITCH_ACCOUNT`, profile count unchanged) then confirmed into a new account
  (true, +1 profile), takeover from a second device (false; the first session gets `SESSION_REPLACED`), lost first-login
  response (response dropped, retry false, profile section epoch 2). In every row the player's analytics `isNewUser`
  sequence equals the flags. 8/8 rows match SC-001 (the T018 race row is in `LoginAnalyticsTests`).
- [x] T020 [P] [US1] SDK reads the flag (after T019; FR-004): `tests/GameServer.Client.Tests/ClientTests.cs` gains: the
  `LoginResponse` returned by `LoginGuestAsync` / `LoginWithProviderAsync` carries `IsNewPlayer` (true then false
  against the sample host); a fake transport answering a login without field 8 reads `false`; the login sections are
  applied as today. No SDK source change (generated message).
  Done when: green; `SdkPublicApiTests` green. (FR-004, US1-5)
  Note: `Guest_and_provider_logins_carry_the_new_player_flag` (guest true then false from a second client; provider true
  then false, profile rev 2) and `A_login_answer_without_the_flag_reads_false_and_its_sections_still_apply` (the
  `ScriptedTransport` rewrites the real answer with `IsNewPlayer = false`, which proto3 never writes: no field 8, as an old
  server; session, player id and the profile section rev 1 still applied). No SDK source change. `ClientTests` 25 and
  `SdkPublicApiTests` 2 green.
- [x] T021 [P] [US1] Architecture guards (after T018; research R16):
  `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` — new `Login_writes_accounts_only_through_the_account_writer`
  (reflection over `LoginCompletion`'s constructor and fields: depends on `IAccountWriter`, not on
  `IPlayerAggregateWriter`); `Rules_engine_detects_real_dependencies` gains the positive control "`AccountWriter`
  depends on `ITransactionContext`"; the comment of
  `Inside_platform_only_the_transactional_write_paths_use_transaction_contexts` names the creation writer;
  `Only_platform_writes_through_transactions` green unchanged.
  Done when: green; each new assertion fails when its target is reverted (checked once, recorded in a Note). (FR-011,
  constitution I)
  Note: `Login_writes_accounts_only_through_the_account_writer` checks `LoginCompletion`'s constructor parameters and
  fields (helper `LoginWriteDependencyViolations`) and a NetArchTest rule (no dependency on `IPlayerAggregateWriter`);
  `Rules_engine_detects_real_dependencies` gains the `AccountWriter` → `ITransactionContext` control; the comment of
  `Inside_platform_only_…` names the creation writer. Revert check (temporary test file, deleted): a stand-in
  `RevertedLoginCompletion(IPlayerAggregateWriter)` gives both violation lines, and the control's rule run on a type
  without the dependency (`AccountCreation`) passes, so the control fails if `AccountWriter` lost it. `ArchitectureTests`
  14 and the project's 68 tests green (`Only_platform_writes_through_transactions` unchanged).
- [x] T022 [US1] US1 checkpoint (after T019–T021): release notes draft — FG-013 section (flag definition, lost
  response = `false`, `isNewUser` equals the flag), BC-7 and BC-10 rows with "how it surfaces" from the real codes;
  full suite in memory and File.
  Done when: green with T001 counts plus the new tests. (FR-090)
  Note (2026-11-04): Debug and Release 0 warnings. In memory and `GAMESERVER_TEST_STORE=File` each 2371 total / 2270
  passed / 101 skipped / 0 failed (T001's 2321 + 23 Phase 2 + 27 US1 tests); the two known-flaky TelemetryTests passed in
  both runs. Release notes: FG-013 section written; BC-7 now names the optional `correlationId` of the three login
  methods; BC-10 "how it surfaces" from the writer's real codes and log line. Extra check on the changed login path: the
  Couchbase integration project alone (local EE 7.6.12) 142 total / 140 passed / 2 skipped / 0 failed (incl. the purge
  and login races of `CouchbaseLeaseAndPurgeTests`); the full Couchbase gate stays T080.

**Checkpoint (MVP)**: every login goes through `IAccountWriter`; the flag is true exactly for the creating login and
equals `isNewUser`; two racing first logins report one new user; login store failures answer typed codes.

## Phase 4: US2 — A new account gets its starting state in the transaction that creates it (P2, FG-015)

**Goal**: registered initializers run inside the creation attempt of `IAccountWriter` over one shared context, under the
command commit rules, with one receipt when they had effects; startup checks the declarations; the kit runs an
initializer host-free.

**Independent test**: SC-002 on the in-memory and File stores (T027) and on Couchbase (T029); startup lines (T023); the
kit call (T030).

- [x] T023 [US2] Startup checks of initializer declarations (after T011; research R4, errors-and-report-lines §4.1,
  data-model §1.1): new partial `src/GameServer.Platform/Registry/ModuleRegistryAccounts.cs` with
  `ValidateAccountInitializers` (joins `ModuleRegistry.Validate` after the command block: invalid name (data-model
  pattern), duplicate name across modules, unknown touched aggregate, duplicate touch, `profile` touched, per-initializer
  claim count outside 0..2, `[deployment]` creation budget `profile` + union > `CommandDescriptor.MaxTouches`,
  `[deployment]` claim sum > `MaxClaimMarkersLimit`, a module command named `identity.create_account`), internal
  `ModuleRegistry.AccountInitializers` (module registration order, then list order) and internal
  `AccountCreation.Describe(registry)` (creation descriptor: operation `identity.create_account`, touches = union in
  first-seen order, `maxClaimMarkers` = sum, Player profile, audit expects 0).
  Tests in a new `tests/GameServer.Platform.Tests/Registry/AccountInitializerValidationTests.cs`: each line of §4.1 alone
  (texts literal); two initializers declaring the same aggregate are one budget entry; all lines plus one unrelated module
  fault give one report with every line; a host started in provision mode reports the same lines
  (`tests/GameServer.Platform.Web.Tests/GameServerCommandLineTests.cs` gains one case).
  Done when: green; the order of blocks equals errors-and-report-lines §4. (FR-015, SC-006, US2-5, BC-9)
  Note: new partial `ModuleRegistryAccounts.cs` (`ValidateAccountInitializers` after `ValidateCommands`, internal
  `AccountInitializers` as `RegisteredAccountInitializer(Module, Registration)`, internal `CreationTouches`) and
  `AccountCreation.Describe(registry)` plus `AccountCreation.InitializerOperation(name)` (the rule-line label). Name pattern =
  data-model §1.1 (`^[a-z][a-z0-9_]*\.[a-z][a-z0-9_.]*$`, so `seed.starting.gold` is valid). A touch listed twice gives only the
  "twice" line (not a second unknown/profile line); the budget union includes unknown ids (they already have their line). The
  claim-sum line lists only initializers with claims. `AccountInitializerValidationTests` (16, in `GameServer.Platform.Tests`,
  over the pure-Platform `SeedModules` of `Infrastructure/SeedModules.cs`): each §4.1 line literally, one budget entry for a
  shared aggregate, a combined report whose order is aggregates → initializer block → reserved operation → error codes.
  `GameServerCommandLineTests.An_invalid_account_initializer_is_reported_in_serve_and_provision_mode`: the same line in
  serve (startup exception) and provision (exit 1, provisioner not called).
- [x] T024 [P] [US2] Per-initializer view (after T011; research R3): new internal
  `src/GameServer.Platform/Commands/InitializerContext.cs` (`ICommandContext` over the shared `CommandContext`):
  `Get`/`Set` outside the initializer's touches → `InvalidOperationException` with the runner's DAT-09/DAT-11 message
  naming the initializer; successful claims counted against the initializer's `MaxClaimMarkers`; everything else
  forwarded (actor, clock, config, `NewId`, `NewSeed`, `AddOutbox`, claims).
  Tests in a new `tests/GameServer.Platform.Tests/Commands/InitializerContextTests.cs`: undeclared get and set refused
  with the literal message; a claim over the initializer's own limit refused while the shared limit still has room; two
  views over one context see each other's writes in registration order; forwarded members equal the inner context's.
  Done when: green. (FR-010, FR-013, US2-6)
  Note: `InitializerContext` wraps the concrete `CommandContext` (it needs `Outbox`/`TryGetNewState`) and throws the
  internal `AccountCreationRuleException : InvalidOperationException`, so the writer tells a view rule ("rule") from the
  initializer's own exception ("threw"). It records the initializer that set an aggregate last (labels an oversize line) and
  the outbox entries added through it (the audit rule per initializer). `InitializerContextTests` (4).
- [x] T025 [US2] Creation path with initializers (after T014, T016, T023, T024; **T006** decisions V2/V4; research R1,
  R2, R7, data-model §1.2, §1.3): `src/GameServer.Platform/Commands/AccountWriter.cs` absent branch:
  - second read = creation anchor + every creation touch (V4 decision); touches loaded with `serializer.Load`;
  - a request id (lowercase UUID `D`) generated **once per `WriteLoginAsync` call**, outside the attempts; the system
    actor (`ActorId` = player id, `ActorType` = System, game/environment of the deployment, no permissions, the login
    correlation id); receipt id = `ReceiptIds.Compute(player, player, identity.create_account, requestId)`;
  - per attempt a **fresh** `CommandContext` over the creation descriptor; each initializer created per attempt with
    `ActivatorUtilities.CreateInstance(rootServices, type)` and run in `ModuleRegistry.AccountInitializers` order through
    its `InitializerContext`; then `Mutate(profile.CreateDefault())`;
  - every commit rule **before the first staged write**: per touch change detection + `BuildDocument` + hard-size check,
    `CheckAudit` (0 entries), receipt built with `ReceiptBuilder` (operation `identity.create_account`, SHA-256 of the
    empty body, `OK`, empty result, schema versions = touches, changed touches with their new revs, claims, outbox) and
    `CheckReceiptSize` — the receipt only when writes ∪ claims ∪ outbox ≠ ∅;
  - stage: anchor rewrite, profile insert, changed touches, claims, receipt; return `Created = true` with
    `CreationReceiptId`;
  - after the commit: the inline outbox relay (as the runner), `account_creations_total{result="created"}`,
    `receipt_size_bytes`/`aggregate_size_bytes`/`txn_*` with operation `identity.create_account`, the Information line of
    errors-and-report-lines §2 (`receipt none` without receipt).
  New counters in `src/GameServer.Platform/Diagnostics/PlatformMetrics.cs` (`account_creations_total`,
  `account_initializer_failures_total`).
  Tests in `AccountWriterTests` (with `StartingStateModule`): the initializer gets DI services; shared-context order;
  receipt fields per data-model §1.3; no receipt for `Nothing` (profile alone, as today); same receipt id and same
  `NewId`/`NewSeed` values across a forced re-run and across race rounds; an existing profile never creates or runs the
  initializer (counter 0); a staging spy over `ITransactionContext` shows no insert/replace before the last rule ran.
  Done when: green. (FR-010, FR-011, FR-012, FR-013, FR-016, US2-1, US2-2, US2-3)
  Note: `AccountWriter` is now a partial (`AccountWriter.cs`: loop, store mapping, returning path, metrics, failure report;
  `AccountWriterCreation.cs`: the creation attempt) with constructor `(registry, store, serializer, IGameConfig, clock,
  options, IOutboxRelay, metrics, IServiceProvider, ILoggerFactory)`; log category `GameServer.Accounts`. The creation plan
  (descriptor, touch descriptors, initializers, receipt label) is built once, lazily. The receipt size label is
  `identity.create_account (initializer {all names, ", "})` (= the kit's label with one initializer). `txn_attempts/retries`,
  `txn_duration`, `txn_ambiguous/expired` are recorded with operation `identity.create_account` only for attempts that took
  the creation branch (a flag that labels metrics, never a decision). **Deviation (tests):** `GameServer.Platform.Tests`
  does not compile `tests/Shared` (no Economy, no Platform.Web), so the writer tests use a pure-Platform `SeedModules`
  (`seed.first` over `seed_a`/`seed_b`, `seed.second` over `seed_a`, DI-injected `SeedScript`) in a new
  `AccountWriterCreationTests` next to `AccountWriterTests`; the `StartingStateModule` paths run in the host tests (T027).
  `RecordingStore` moved to `Infrastructure/`; `RunnerHarness.CreateAccountWriter` and `Logs` added; `MetricCollector` takes
  an optional `PlatformMetrics` to listen to one instance (`PlatformMetrics.Meter` internal). Tests: DI services and shared
  order; receipt fields of data-model §1.3 and the staged order (`read pa_profile`, `read identities,pa_seed_a,pa_seed_b`,
  `read seed_claims`, then `replace identities`, `insert pa_profile`, `insert pa_seed_a`, `insert seed_claims`,
  `insert receipts`); no receipt for no effects; same receipt id and `NewId`/`NewSeed` across a faked insert race and a
  store re-run (3 body runs); an existing profile never runs the initializers.
- [x] T026 [US2] Creation failures (after T025; research R1 table, D15, errors-and-report-lines §2/§3): internal
  `AccountInitializerFailedException` (initializer, module, code or exception type, or rule line) in
  `src/GameServer.Platform/Commands/AccountWriter.cs`; a rejection, a throw (`OperationCanceledException` excluded) or a
  rule violation (undeclared write, audit entry, aggregate size, receipt size) fails the attempt before staging, is
  logged **once** outside the attempt with the §2 line of its kind (category `GameServer.Accounts`, exception type only,
  never a message), counted in `account_initializer_failures_total{initializer, result=rejected|threw|rule}` and
  `account_creations_total{result="failed"}`, and answers `INTERNAL`. A receipt-size violation also counts
  `command_output_too_large_total{operation="identity.create_account", part="receipt"}` and uses the rule line only
  (Planning note 11). A typed read failure of a touched aggregate keeps its own code (T034 onward).
  Tests in `AccountWriterTests` for each `StartingStateBehaviour` failure: 0 documents (profile, aggregates, receipt,
  claims), the exact log line, the metric labels, and the next call with `Grant` creates everything.
  Done when: green. (FR-013, FR-014, FR-031, US2-4, US2-6)
  Note: `AccountInitializerFailedException` (kinds rejected/threw/rule, factories `Rejected`, `Threw`, `Rule`,
  `ReceiptTooLarge`) is caught in `WriteLoginAsync` outside the attempts (V2: same instance), reported once and turned into
  `GameServerException(INTERNAL, "Account creation of player {id} failed ({result}, initializer {name}).")` without inner
  exception. Rule lines: undeclared write/read and claim limit = the view's runner messages; audit = `CommitRules.CheckAudit`
  for the initializer's label; aggregate size = `CommitRules.CheckAggregateSize` labelled with the initializer that set it;
  receipt = `CheckReceiptSize(...).Line`, `initializer="*"`, plus `command_output_too_large_total`. Store failures of a
  creation attempt also count `account_creations_total{result="failed"}`. A typed read failure of a touched aggregate
  propagates with its own code (the loader of T034). `AccountWriterCreationTests` theory (8 failures: rejected, threw,
  undeclared write, undeclared read, audit, oversize, claim over its own limit, receipt over 1024): 0 documents in every
  collection, no insert/replace staged, the exact §2 line once (never the thrown message), the metric labels, and the next
  login creates everything. The middleware still logs the resulting 500 once (`Request failed with INTERNAL: …`; release
  notes "Known before the tag").
- [x] T027 [US2] Host tests of the creation (after T026; **T009** V12 settings; SC-002 in-memory and File): new
  `tests/GameServer.Platform.Web.Tests/AccountCreationTests.cs` with Identity + Economy + `StartingStateModule`:
  - `Grant(100)`: one first login commits profile, `wallet`, `starter`, one ledger entry (source `account_init`, System
    actor id) and one receipt (store document-count delta); receipt `operation`, `actorType = system`, `actorId` = player;
    the login sections show `wallet` and `starter` at rev 1; the receipt is readable by the player through
    `GET /api/v1/player/receipts/{id}` with the id from the Information line, and listed by the GM receipt list;
  - `Nothing`: profile alone, no receipt, sections at rev 0 (edge case "initializer wrote nothing");
  - returning login: initializer counter unchanged;
  - 20 concurrent first logins of one credential + 5 retries after a dropped first response (login limits raised in
    test settings) → 1 receipt, 1 ledger entry, balance 100, exactly one `is_new_player = true`;
  - faults at each step (`FailBeforeCommit`, `AmbiguousBeforeCommit`, a conflict, the anchor removed between mapping read
    and creation through `AfterMappingRead`) and each failing behaviour → 0 documents, the R1 codes in the error
    envelope, and the next login creates everything.
  Run once more with `GAMESERVER_TEST_STORE=File`.
  Done when: green on both stores; 100 % of fault rows give "everything or nothing". (FR-011, FR-012, FR-014, FR-016,
  SC-002, US2-1..US2-4)
  Note: `AccountCreationTests` (15): `Grant(100)` creation (+1 profile, wallet, starter, receipt, ledger entry; ledger
  `payload.sourceType = account_init`, `actorId` = player, `operation = identity.create_account`; sections rev 1; receipt
  readable at `GET /api/v1/player/receipts/{id}` with the id parsed from the Information line); `Nothing` → profile alone,
  `receipt none`; returning login never runs it; 20 racing first logins + 5 retries → 1 receipt, 1 ledger entry, balance
  100, one `is_new_player`; store faults (`FailBeforeCommit` → 500 `INTERNAL`, `AmbiguousBeforeCommit` → 503
  `COMMIT_UNKNOWN_RETRY`, `ConflictOnce` → created once), anchor removed through `AfterMappingRead` → 401 `ACCOUNT_DELETED`,
  and the seven failing behaviours (`FloodOutbox(40)`, `Claim(3)`) → 500 `INTERNAL`, one §2 line without exception; every
  fault row writes nothing and the next login creates everything. Green in memory and with `GAMESERVER_TEST_STORE=File`.
  The GM receipt list check is in `GmBrokenAggregateTests` (T043: Platform.Web.Tests has no GM staff host).
- [x] T028 [P] [US2] Purge and creation (after T027): `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs` gains: a
  player created with `Grant(100)` is purged → seeded `wallet`/`starter` removed, the creation receipt anonymized with
  its key kept (010 B1), its ledger entries handled as any other; a login racing the purge with an initializer →
  `ACCOUNT_DELETED` and no initializer write survives.
  Done when: green; existing purge tests unchanged. (FR-011, edge cases "account purge", "login racing the purge")
  Note: `AccountPurgeTests` gains an `initializer` parameter on its host and two tests (both stores): a seeded player is
  purged — `pa_wallet`/`pa_starter` removed, the creation receipt anonymized in place (key kept, `actorId`/`playerId` the
  anonymous id, `requestId`/`correlationId` empty), its ledger entry under the anonymous id, no reproducible receipt key;
  a login paused until the purge completed answers `ACCOUNT_DELETED` and never runs the initializer (anchor checked
  first). Existing purge tests unchanged (36 green).
- [x] T029 [US2] Couchbase creation (after T027; **[VERIFY] V3**, V4 final): new
  `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseAccountCreationTests.cs` (transactions on): the SC-002
  race (20 concurrent first logins + 5 lost-response retries) → 1 profile insert, 1 receipt, 1 ledger entry, balance =
  start within 3 rounds (V3; fallback 5 rounds for logins, Note); an initializer rejection and a receipt over the limit →
  0 documents and `INTERNAL` (V2); a concurrent anchor removal → `ACCOUNT_DELETED` (V4).
  Done when: green against local Couchbase (skips with a message without `COUCHBASE_CONNSTR`); a Note records the
  rounds used in the race. (FR-012, FR-092, SC-002)
  Note (local Couchbase EE 7.6.12, transactions on): `CouchbaseAccountCreationTests` (5) green: 20 concurrent first logins
  + 5 lost-response retries → 1 receipt, 1 ledger entry (`ledger::{receiptId}:0` present, `:1` absent), balance 100, one
  `is_new_player`, all 200; **V3 holds within 3 rounds:** 20 creation attempts, 0 store retries — each race loser saw
  "document exists" and became a normal login in its second round (no 5-round fallback). Rejection and receipt over the
  limit → 500 `INTERNAL`, no profile/wallet/starter (V2), then `Grant` creates. Anchor race (4 staggered rounds): winners
  login, login, removal, removal; in every round profile, wallet and starter exist exactly when the creation committed
  (V4). Mapping removed via `AfterMappingRead` → `ACCOUNT_DELETED`, initializer never ran.
- [x] T030 [US2] Kit call for initializers (after T025; research R14, public-api §6): `src/GameServer.TestKit/CommandContextDouble.cs`
  becomes `partial`; new `src/GameServer.TestKit/AccountCreationCheck.cs` with `ForAccountInitializer(initializerName,
  params IGameModule[] modules)` (touches at default state rev 0 unless seeded, creation system actor for the default
  player, operation `identity.create_account`, the initializer's `InitializerContext` view; `ArgumentException` when no
  module registers the initializer or one of its aggregates) and `CheckAccountCreation()` (aggregate sizes, audit = 0,
  receipt size with an empty result through `ReceiptBuilder`; empty when the creation would commit). Extend
  `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs` `AllowedInternalTypes`/`Members` with exactly the
  internals used (`ReceiptBuilder`, `ReceiptInputs`, `InitializerContext`, `AccountCreation.Describe`,
  `ModuleRegistry.AccountInitializers`, `CommitViolation`).
  Tests in a new `tests/GameServer.Platform.Web.Tests/AccountInitializerDoubleTests.cs`: the `StartingStateModule`
  initializer host-free (`Written` wallet and starter, one ledger outbox entry); unknown name → `ArgumentException`; an
  undeclared write throws the writer's message; `CheckAccountCreation` lines equal the writer's rule lines for audit,
  aggregate size and receipt size (same behaviours as T026); `The_kit_references_no_test_framework_…` green.
  Done when: green. (FR-017, FR-034, US2-8)
  Note: `CommandContextDouble` is partial; the main file keeps the command double with `_descriptor` and a nullable
  `_registration`, and every `Get`/`Set`/claim/outbox call goes through `Surface` (the writer's `InitializerContext` for an
  initializer double). `AccountCreationCheck.cs` holds `ForAccountInitializer` (system actor `p_test`, descriptor
  `identity.create_account` over the initializer's own touches and claim limit) and `CheckAccountCreation()` (the writer's
  rule order; receipt measured with `ReceiptBuilder` from the real empty-body hash). `CheckCommit` on an initializer double
  and `CheckAccountCreation` on a command double throw `InvalidOperationException`. Kit allowlist: `InitializerContext`,
  `ReceiptBuilder`, `ReceiptInputs`, `CommitViolation`, `RegisteredAccountInitializer`,
  `ModuleRegistry::get_AccountInitializers`, `AccountCreation::InitializerOperation` (instead of `Describe`: the double needs
  one initializer's touches, not the union). `AccountInitializerDoubleTests` (7): host-free grant, argument errors, the
  writer's undeclared-write message, and for audit, oversize and receipt size the kit line equals the writer's logged rule
  line byte for byte (same actor, config, time). `TestKitArchitectureTests` 4 green.
- [x] T031 [P] [US2] Opt-in first-login cost (after T029; SC-003): new
  `tests/GameServer.Platform.Couchbase.IntegrationTests/AccountCreationPerformanceTests.cs` (`GAMESERVER_PERF=1` and
  Couchbase): 200 first logins each with an initializer touching three aggregates and making one grant (test file
  initializer over `wallet`, `starter` and one more test aggregate) and without initializer, after warm-up → p95 delta ≤
  20 ms; 200 returning logins with and without the initializer registered → equal within noise; the absolute returning
  p95 compared with a scratch `git worktree` of `main` (Note, never committed).
  Done when: a Note records p50/p95 of each series and the delta; skipped without the variables. (SC-003)
  Note (`GAMESERVER_PERF=1`, local Couchbase EE 7.6.12, 20 warm-up + 200 logins per series through TestServer): first
  login without initializer p50 6.31 / p95 9.09 ms; with `perf.seed` (wallet + 2 test aggregates, one grant) p50 11.58 / p95
  14.36 ms → **delta p95 +5.27 ms** (≤ 20 ms). Returning logins p95 4.85 ms without and 5.50 ms with the initializer
  registered (p50 4.21 / 4.19). **Not done:** the absolute returning p95 against a scratch worktree of `main` (left open).
- [x] T032 [US2] US2 checkpoint (after T027–T031): release notes draft — FG-015 section (initializer API, budget,
  receipt only with effects, failure = `INTERNAL`, no backfill D6), BC-6 and BC-9 rows; `tests/GameServer.Observability.Tests/TelemetryTests.cs`
  metric-name list gains `account_creations_total` and `account_initializer_failures_total` (recorded directly through
  `PlatformMetrics`, as 015 did for its fallback counter); full suite in memory and File.
  Done when: green. (FR-016, FR-090)
  Note: release notes FG-015 section written; BC-6 and BC-9 "how it surfaces" from the real lines. `TelemetryTests` lists
  `account_creations_total` (recorded by the test's own first login) and `account_initializer_failures_total` (recorded
  directly). Suite counts: T046 Note.

**Checkpoint**: a first login seeds the account once, in one transaction, on all three stores; every failure leaves
nothing and the next login retries; bad declarations stop startup with one line each; the kit runs an initializer.

## Phase 5: US3 — One broken aggregate does not take down the snapshot or the login (P3, FG-029)

**Goal**: a typed read failure reported once; five degrading sites (snapshot, login, replay, post-commit mapping, GM
view) leave the section out and list it; every other reader answers the typed code; the SDK marks unavailable sections.

**Independent test**: the SC-004 matrix (T044); broken profile on login and bearer requests (T039, T040); SDK marks
(T045).

- [x] T033 [US3] Failure value and reporter (after T010; research R5, R6, public-api §2.3, errors-and-report-lines §2/§3):
  new `src/GameServer.Platform/Aggregates/AggregateReadFailure.cs` (`AggregateReadFailureReason`, `AggregateReadFailure`
  with `Code`, `ReasonName`, `ToException()` — `GameServerException(code, safe detail)` with `Details = {aggregate}`,
  `RetryAfterMs = 1000` for `VersionAhead`, **no inner exception**, a marker in `Exception.Data` carrying the failure —
  and `TryGet(exception, out failure)`) and `src/GameServer.Platform/Aggregates/AggregateReadFailures.cs` (`Report`: one
  log entry in category `GameServer.Aggregates`, Warning for `version_ahead`, Error otherwise, template of §2; one
  increment of `aggregate_read_failures_total{aggregateId, reason}` through a new `PlatformMetrics` member).
  Tests in a new `tests/GameServer.Platform.Tests/Aggregates/AggregateReadFailureTests.cs`: code and name per reason;
  exception shape (code, details only `aggregate`, retry delay, `InnerException` null); `TryGet` true for
  `ToException()`, false for any other `GameServerException`; one log line per report with the literal template and no
  state content; metric labels.
  Done when: green; no caller yet. (FR-020, FR-021, FR-026)
  Note: `AggregateReadFailure` stores itself in `Exception.Data["GameServer.AggregateReadFailure"]`; `TryGet` is true only for
  a `GameServerException` carrying it. **Deviation (signature):** `AggregateReadFailures(PlatformMetrics, ILoggerFactory)`
  instead of `ILogger<AggregateReadFailures>`: the contract's category `GameServer.Aggregates` is not the type name, so the
  reporter creates its logger by category (public `LogCategory`). Internal `CountOnly(metrics)` for the two-argument
  serializer. `PlatformMetrics.AggregateReadFailed`, plus `CommandOutputTooLarge`, `AccountCreation`,
  `AccountInitializerFailed` (T025/T026). `AggregateReadFailureTests`: codes, names, exception shape, `TryGet`, literal
  Warning/Error lines, metric labels.
- [x] T034 [US3] Loader (after T033, **T004** decision V6; research R5): `src/GameServer.Platform/Aggregates/AggregateDocumentSerializer.cs`
  gains `TryLoad(descriptor, playerId, document, out loaded, out failure)` (classification of data-model §2.1 in loader
  order; the V6 decision for the deserialize block; never catches `OperationCanceledException`/`OutOfMemoryException`;
  reports each failure once and returns false) and the constructor `(GameServerOptions, PlatformMetrics,
  AggregateReadFailures)`; the existing two-argument constructor builds a count-only reporter. `Load` keeps its
  signature and throws `failure.ToException()` (BC-5). `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`
  registers `AggregateReadFailures` and uses the three-argument constructor.
  Tests in `AggregateReadFailureTests`: the T004 corruption list → reason per document, exactly one report, `TryLoad`
  never throws, `Load` throws the typed exception; success path reports nothing; cancellation propagates; a failed read
  writes nothing to the store. `tests/GameServer.Platform.Tests/Aggregates/AggregateBudgetAndUpgradeTests.cs`
  `Newer_stored_schema_than_code_is_an_error` now expects `AGGREGATE_VERSION_AHEAD` (T009 inventory; renamed
  `Newer_stored_schema_than_code_is_version_ahead`); every other T009-listed pin updated here.
  Done when: green; the only changed expectations are the T009 ones. (FR-020, FR-021, FR-026, BC-5)
  Note: `TryLoad` classifies in loader order: envelope fields (explicit V6 list → `unreadable`, stored 0) → version ahead →
  state not a JSON object (`unreadable`, exception type `System.Text.Json.JsonException`) → upgrade chain (catch-all except
  OCE/OOM → `upgrade_failed`; a missing step and a null upgrader result are `InvalidOperationException`) → deserialize
  (explicit list → `unreadable`). `Load` = `TryLoad` or throw `failure.ToException()`. Registered as `AggregateReadFailures`
  + the three-argument serializer factory in `GameServerBuilder`. The T004 matrix (23 cases on re-parsed documents) gives
  exactly the T004 types; one report per read; cancellation in an upgrader propagates. The only changed pin:
  `AggregateBudgetAndUpgradeTests.Newer_stored_schema_than_code_is_version_ahead` (was `…_is_an_error`, INTERNAL).
- [x] T035 [US3] Degrading readers (after T034; public-api §2.3): `src/GameServer.Platform/Aggregates/PlayerSnapshotReader.cs`
  gains `PlayerSnapshot(Sections, Unavailable)`, `ReadAvailableAsync(playerId, ids, ct)` and
  `ReadAllAvailableAsync(playerId, ct)` (one parallel KV read as today, PLY-02; `TryLoad` per document; unknown or
  hidden ids still `BAD_REQUEST`; failures ordered by id). `ReadAsync`/`ReadAllAsync`/`ReadOneAsync` keep their
  signatures and throw the typed exception.
  Tests in a new `tests/GameServer.Platform.Tests/Aggregates/PlayerSnapshotReaderTests.cs`: one broken of three →
  two sections + one failure; all broken → empty sections, all listed; explicit list with a broken id; unknown id →
  `BAD_REQUEST`; the throwing members give the typed code; one store read per call.
  Done when: green. (FR-021, FR-022)
  Note: `ReadAvailableAsync`/`ReadAllAvailableAsync` share the descriptor selection and the one parallel read of the throwing
  members; sections keep read order (explicit list = caller order, as `ReadAsync`), failures are ordered by id.
  `PlayerSnapshotReaderTests` (7, `ReadProbeAggregates` test module, `RecordingStore.Reads` = 1 per call).
- [x] T036 [P] [US3] No second log line (after T033; research R5 "Middleware"): `src/GameServer.Platform.Web/Middleware/ExceptionMiddleware.cs`
  skips its Error line when `AggregateReadFailure.TryGet(ex, …)`; it still writes the envelope with `RetryAfterMs` and
  `Details`.
  Tests in a new `tests/GameServer.Platform.Web.Tests/ExceptionMiddlewareTests.cs` (the plan calls it existing; it is not,
  Planning note 7): a marked exception → no log line from the middleware, envelope 503 with `retry_after_ms = 1000` and
  `details.aggregate`; an unmarked 5xx `GameServerException` → one Error line as today; a marked exception never
  produces "Unhandled exception.".
  Done when: green. (FR-026)
  Note: `ExceptionMiddleware` skips both its Error and Information line for a marked exception and still writes the envelope
  (`RetryAfterMs`, `Details`). `ExceptionMiddlewareTests` (2): marked → 503, `Retry-After: 1`, `retry_after_ms = 1000`,
  details `aggregate`, no middleware line, no "Unhandled exception."; unmarked `AGGREGATE_UNREADABLE` → one Error line.
- [x] T037 [US3] `SectionWriter` (after T035; research R7, public-api §3): new
  `src/GameServer.Platform.Web/Aggregates/SectionWriter.cs` (`WriteSections`, `WriteUpdates`; each mapper call in a `try`;
  any exception except `OperationCanceledException` → `AggregateReadFailure(…, MapperFailed, …)` reported once and listed;
  loader failures listed as given; never a section with an empty payload; `unavailable` ordered by id), registered in
  `GameServerBuilder.cs`.
  Tests in a new `tests/GameServer.Platform.Web.Tests/SectionWriterTests.cs`: mapper throws for one state → that
  section absent and listed `AGGREGATE_UNREADABLE`, one report; the others byte-identical to a direct mapping; loader
  failures merged and ordered; a section never appears in both lists.
  Done when: green; no endpoint uses it yet. (FR-022, FR-024, FR-026)
  Note: `SectionWriter(AggregateWireMapperRegistry, AggregateReadFailures)` as in public-api §3; any mapper exception except
  OCE/OOM becomes `mapper_failed` (OOM also excluded, as the loader). For `WriteUpdates` the schema versions of a mapper
  failure are 0 (an `AggregateChange` has no descriptor; the versions appear on no wire and no line). A section already
  listed by the loader is never written. `SectionWriterTests` (4).
- [x] T038 [US3] Snapshot endpoint (after T037; wire-changes §2): `src/GameServer.Platform.Web/Endpoints/CoreEndpoints.cs`
  snapshot → `ReadAvailableAsync` + `SectionWriter.WriteSections` → `SnapshotResponse.unavailable_sections`.
  Tests in `tests/GameServer.Platform.Web.Tests/SnapshotTests.cs`: each reason of `BrokenDocuments` → 200, the broken
  section absent and listed with code and number, the other sections byte-identical to the undamaged twin player;
  explicit list naming the broken section → same; every section broken → 200 with empty `sections`; unknown id → 400 as
  today.
  Done when: green. (FR-022, US3-1, US3-2, US3-8)
  Note: the snapshot uses `ReadAvailableAsync` + `SectionWriter.WriteSections`. Tests in a new sibling
  `SnapshotUnavailableSectionsTests.cs` (the existing `SnapshotTests` host has no test module) over the new
  `BrokenAggregateHost` (Identity + Economy + `StartingStateModule` + test commands touching/reading/leaving `starter` and
  `broken_probe`): each reason, all and explicit list → 200, listed with code and number, the other sections equal to the
  twin's; every requested section broken → empty `sections`; unknown id → 400. 6 green.
- [x] T039 [US3] Login degradation and broken profile at login (after T019, T038): `src/GameServer.Modules.Identity.Adapters/LoginCompletion.cs`
  reads `snapshots.ReadAvailableAsync(playerId, null)` after the session; `LoginResult.Unavailable`;
  `src/GameServer.Modules.Identity.Adapters/IdentityModule.cs` `LoginResponse` writes sections and
  `unavailable_sections` through `SectionWriter`. A profile load failure (pre-read through `ProfileCache` or inside the
  writer) propagates typed: no epoch write.
  Tests in `tests/GameServer.Platform.Web.Tests/IdentityLoginTests.cs`: login sections = every client-visible section of
  an undamaged player (FR-003); broken `wallet` → 200, session created, epoch + 1 exactly once, `wallet` listed; profile
  mapper failure → listed like any section (wire-changes §2 rule); broken profile (version ahead, unreadable) → error
  envelope with the typed code (503 with retry delay / 500), epoch unchanged.
  Done when: green. (FR-003, FR-023, US3-2, US3-6, BC-3)
  Note: `LoginResult.Unavailable` (init, `[]`); `LoginCompletion` reads `ReadAvailableAsync` after the session; the login
  response goes through `SectionWriter`. `IdentityLoginDegradationTests` (5, appended to `IdentityLoginTests.cs`): login
  sections = every client-visible aggregate; broken `starter` → listed, epoch 2 after the second login, session works; a
  profile mapper failure (a `ReplaceWireMapper` that throws for a marker name) → `profile` listed, login 200; broken profile
  (version ahead / state array) → 503 with 1000 ms / 500, details `aggregate=profile`, document unchanged, one read-failure
  line, no middleware line. (The broken non-profile aggregate is `starter`, the `BrokenDocuments` aggregate, not `wallet`.)
- [x] T040 [US3] Broken profile on every authenticated request (after T036, T039, **T005** decision V5):
  `src/GameServer.Modules.Identity.Adapters` bearer authentication (`PlayerBearerAuthenticationHandler`) per the V5
  decision (propagate, or `Fail` with code and details), provider login `EnsureAccessibleAsync`/`CurrentPlayerAsync`, and
  `src/GameServer.Modules.Gm.Adapters/GmTargetValidator.cs` answer the typed code; `ProfileCache` never stores a broken
  profile.
  Tests in a new `tests/GameServer.Platform.Web.Tests/BrokenProfileTests.cs`: a bearer request (snapshot, command) for a
  player whose profile is broken → typed envelope (retry delay per V5 decision); a provider login for it → typed;
  repairing the document makes the next request succeed (nothing cached).
  Done when: green; 0 `INTERNAL`. (FR-021, FR-023, US3-6)
  Note (V5 fallback): `PlayerBearerAuthenticationHandler` catches a marked read failure from `ProfileCache.GetAsync` and
  returns `Fail(failure.Code, ex.Details)`; `ErrorResponseWriter` gives the retryable code its 1000 ms. Provider login
  (`EnsureAccessibleAsync`/`CurrentPlayerAsync`) and `GmTargetValidator` needed no change: `ProfileCache.ReadFreshAsync` and
  `ReadOneAsync` throw the typed exception, and `ProfileCache` stores only loaded states. `BrokenProfileTests` (3): bearer
  snapshot and command → typed envelope (retry delay for version ahead), one read-failure line per request, repaired
  document → 200 (cache expired with the fake clock); provider login → typed.
- [x] T041 [US3] Runner: refuse on execute, degrade on replay (after T034, T035; research R5 table):
  `src/GameServer.Platform/Commands/CommandRunner.cs` — execute: the typed exception of `Load` (Touches ∪ Reads) leaves
  the attempt before the handler and is counted by `ExecuteAsync` in `command_results_total` (no `INTERNAL` catch-all);
  replay (fast path and in-attempt, `ReplayAsync` 431-472): `TryLoad`, a broken aggregate omitted from `Updates` and added
  to the new `CommandExecution.Unavailable` (`src/GameServer.Platform/Commands/CommandModels.cs`, init property).
  Tests in `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs`: a touching and a reads-only command on a
  broken aggregate → typed code, counting handler 0 calls, nothing staged; a command touching neither → OK; the replay of
  a command committed before the damage → same result and code, the aggregate in `Unavailable`, not in `Updates`.
  Done when: green. (FR-024, US3-3, US3-4)
  Note: execute needed no code: `Load` throws the typed exception inside the attempt, the store rethrows it (V2) and
  `ExecuteAsync`'s `catch (GameServerException)` counts the code. Replay uses `TryLoad`; failures go to the new init property
  `CommandExecution.Unavailable` (ordered by id). Tests in a new sibling `CommandRunnerReadFailureTests` (4): touching
  (`probe.grow`) and reading (`probe.badwrite` reads `profile`) on a broken aggregate → typed code, handler never ran, no
  receipt, `command_results_total{code}` typed and no `INTERNAL`; untouched → OK; replay after the damage → same result,
  `Updates` without it, `Unavailable` with it.
- [x] T042 [US3] Command response (after T037, T041; research R7 "Post-commit"): `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`
  `BuildResponse` maps updates through `SectionWriter.WriteUpdates` with `execution.Unavailable` →
  `CommandResponse.unavailable_sections`; a mapper failure after the commit keeps the commit: 200 with code, receipt id,
  result and the section listed; `SetOutcome` and `command_results_total` unchanged.
  Tests in a new `tests/GameServer.Platform.Web.Tests/CommandUnavailableSectionsTests.cs`: mapper failure after commit →
  200, receipt stored, section listed, not in `updates`; the retry with the same request id replays the same response;
  the replay of a broken aggregate lists it; an envelope for a touching command carries `details.aggregate`.
  Done when: green; today's 500-after-commit path is gone. (FR-024, US3-3, US3-4)
  Note: `BuildResponse(execution, playerId, clientConfigVersion, SectionWriter, clock)` (internal) uses
  `WriteUpdates(…, execution.Unavailable, …)`. `CommandUnavailableSectionsTests` (3): mapper failure after commit → 200 OK,
  receipt stored, state committed, section listed, retry replays the same; replay of an aggregate broken since → listed
  `AGGREGATE_VERSION_AHEAD`; touching command on a broken aggregate → 503 envelope with `details.aggregate`.
- [x] T043 [US3] GM view and GM command (after T035, T041, **T008** decision V11; wire-changes §4):
  `src/GameServer.Modules.Gm.Adapters/GmPlayerEndpoints.cs` `LookupAsync` → `ReadAllAvailableAsync`; `GmPlayerView` gains
  init property `Unreadable` (`GmUnreadableAggregate(Id, Code, Reason, StoredSchemaVersion, CodeSchemaVersion)`), always
  present, ordered by id; a broken profile is listed and gives `deletion = null`.
  `src/GameServer.Platform.Web/Commands/GmCommandEndpointFactory.cs` `GmCommandResponse` gains init property
  `Unavailable` (`GmUnavailableAggregate(Id, Code)`, from `execution.Unavailable`). Regenerate `docs/api/gm-openapi.json`
  (`UPDATE_OPENAPI=1`, per the V11 decision) and review the diff (additive members only).
  Tests in a new `tests/GameServer.Gm.Tests/GmBrokenAggregateTests.cs`: GM view with each reason; broken profile view;
  GM command touching a broken aggregate → GM JSON error with the typed code; GM target validator typed;
  `tests/GameServer.Gm.Tests/OpenApiTests.cs` green with the committed copy.
  Done when: green. (FR-025, US3-5)
  Note: `GmPlayerView.Unreadable` (init, `[]`) with `GmUnreadableAggregate`; `LookupAsync` uses `ReadAllAvailableAsync`; a
  player with only broken documents is not "purged"; a broken profile gives `deletion = null`. `GmCommandResponse.Unavailable`
  with `GmUnavailableAggregate(Id, Code)`. `.Produces<GmPlayerView>()` on the player view **and** the by-mapping route (both
  return the view) and `.Produces<GmCommandResponse>()` in `MapGmCommandCore`. `docs/api/gm-openapi.json` regenerated
  (`UPDATE_OPENAPI=1`, written LF): +358/−7, the 7 removed lines are the bare `"description": "OK"` of the 2 view routes and
  the 5 GM command routes, now with their schemas (V11 consequence); `GmUnreadableAggregate` and `GmUnavailableAggregate`
  added; nothing else changed. `GmBrokenAggregateTests` (9): empty list, each reason with versions, mapper-only state
  readable for GM, broken profile, target validator 503 typed, GM command on a broken wallet 500 typed with nothing written,
  and the creation receipt in the GM receipt list (T027). `OpenApiTests` green.
- [x] T044 [US3] SC-004 matrix (after T038–T043): new `tests/GameServer.Platform.Web.Tests/BrokenAggregateMatrixTests.cs`
  over `BrokenDocuments`: the four reasons × {snapshot all, explicit list, login, touching command, reading command,
  untouched command, replay, GM view, `ReadOneAsync`} (mapper failure applies where a mapper runs; a touching command
  with a mapper failure commits and lists it, T042) → 0 responses `INTERNAL`; 200 where degrading with every other
  section byte-identical to the twin; typed code and 0 writes for touching/reading commands; exactly 1 log entry
  (`CapturedLog`) and 1 metric increment per failed read; epoch + 1 exactly once per successful login; "Unhandled
  exception." never logged. `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs` gains: broken profile → the purge
  step fails as reported today; a broken non-profile aggregate is removed.
  Done when: green; every cell asserted. (FR-020, FR-021, FR-022, FR-023, FR-024, FR-026, SC-004)
  Note: `BrokenAggregateMatrixTests` (4 reasons) walks snapshot all, explicit list, login, touching, reading and untouched
  command, replay, GM view and `ReadOneAsync` for one damaged player and an undamaged twin with the same history: no
  `INTERNAL`, degraded answers equal the twin's other sections, typed refusals write nothing, the read-failure log count
  moves by exactly 1 per failed read (0 where nothing fails, e.g. the GM view and `ReadOneAsync` of a mapper-only failure)
  and equals the metric total, epoch 2 after the login, no "Unhandled exception.". A mapper failure on a touching command
  commits and lists the section (T042). `AccountPurgeTests.A_broken_profile_fails_the_purge_begin_and_a_broken_aggregate_is_removed`
  (both stores): the begin step's existing line and `account_purge_step_failures_total{step="begin"}`; the broken `starter`
  of another leaver is removed and that purge completes.
- [x] T045 [US3] SDK unavailable sections (after T012, T010; research R12, public-api §7.3):
  `sdk/unity/com.gameserver.client/Runtime/Core/SectionStore.cs` (`UnavailableSectionState`, `Unavailable`,
  `IsUnavailable`, event `AvailabilityChanged`; `Set`/`Apply` clear the mark of their section; `Clear()` clears all marks)
  and `GameServerClient.cs` (marks from login, snapshot and command responses; `ApplyLogin` keeps the copies of the
  sections a same-player login lists as unavailable and clears for any other player; the client remembers whose sections
  it holds). No new retry rule: `AGGREGATE_VERSION_AHEAD` is retried by the retryable flag.
  Tests in a new `tests/GameServer.Client.Tests/UnavailableSectionTests.cs`: mark, keep the last copy, clear on a later
  read, event per change; same-player re-login keeps copies, another player's login clears; a version-ahead envelope
  retried with the same bytes and request id; an old-shape response (no field) changes nothing. `SdkPublicApiTests`
  re-recorded with only added lines.
  Done when: green; `sdk/GameServer.Client` builds C# 9 with 0 warnings. (FR-027, BC-8)
  Note: `SectionStore`: `UnavailableSectionState` (public constructor), `Unavailable`, `IsUnavailable`, `AvailabilityChanged`
  (raised on mark and on clear-by-read, not by `Clear()`; handler exceptions go to `HandlerFailed`), internal
  `MarkUnavailable` and `ClearExcept`. `Set` and an applied `Apply` clear the section's mark. `GameServerClient` remembers the
  player whose sections the store holds (`ClearSections()` replaces the four `Sections.Clear()` calls): a same-player login
  keeps the copies it lists, any other clears; login, snapshot and command answers mark. No new SDK file (no `.meta`).
  `UnavailableSectionTests` (5). `sdk-public-api.txt` re-recorded: +8 lines (`UnavailableSectionState` type, constructor and
  3 properties; `SectionStore` `Unavailable`, `IsUnavailable`, `AvailabilityChanged`), 0 removed. `sdk/GameServer.Client`
  (C# 9) builds with 0 warnings.
- [x] T046 [US3] US3 checkpoint (after T044, T045): release notes draft — FG-029 section (two codes, degradation sites,
  broken profile, one log + one metric, deploy-order rule kept), BC-3, BC-4, BC-5, BC-8 rows, the rolling-deploy note;
  `TelemetryTests` metric list gains `aggregate_read_failures_total`; full suite in memory and File.
  Done when: green. (FR-028 groundwork, FR-090)
  Note: release notes FG-029 section, BC-3, BC-4, BC-5, BC-8 "how it surfaces" and the rolling-deploy alert; `TelemetryTests`
  lists `aggregate_read_failures_total`. Checkpoint (2026-11-04, after T023–T046): Debug and Release 0 warnings, 0
  errors (non-incremental). In memory and with `GAMESERVER_TEST_STORE=File` each 2530 total / 2423 passed / 107
  skipped / 0 failed (T022's 2371 + 159 new; +6 skipped = the 5 Couchbase creation tests and the opt-in perf test
  without `COUCHBASE_*`); the two known-flaky TelemetryTests passed in both runs. One earlier in-memory run had
  `IapAnalyticsTests.A_slow_sink_never_holds_the_purchase_beyond_the_bound…` fail on its time bound under full-suite
  load; it passed alone 3/3 and in both final runs. Couchbase integration project alone (local EE 7.6.12): 148 total /
  145 passed / 3 skipped / 0 failed (incl. `CouchbaseAccountCreationTests`, `CouchbaseRunnerTests` and the purge/login
  races on the typed read path).

**Checkpoint**: a broken aggregate degrades snapshot, login, replay and GM view and is refused typed everywhere else;
exactly one log line and one metric per failed read; the SDK keeps the last good copy and marks the section.

## Phase 6: US4 — Command limits are documented settings, and going over one is a typed rejection (P4, FG-016)

**Goal**: every commit rule before the first staged write, `COMMAND_OUTPUT_TOO_LARGE` with details on the wire, range
checks of the five settings, the kit's receipt measure equal to the runner's.

**Independent test**: SC-005 (T049, T052) and SC-006 (T050).

- [x] T047 [US4] Rules before staging in the runner (after T013, T014, T041, **T006** decision V2; research R8 "Order"):
  `src/GameServer.Platform/Commands/CommandRunner.cs` `RunAttemptAsync`: handler → code → result size → per touch change
  detection + document build + `AGGREGATE_TOO_LARGE` → audit → receipt build (`ReceiptBuilder`) → receipt size →
  **then** stage aggregates, claims, receipt (insert or replace of an expired one). Failure types and messages unchanged
  in this task.
  Tests in `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs`: a staging spy (`ITransactionContext`
  decorator in `RunnerHarness`) shows no insert/replace for a result over the limit, a receipt over the limit, an audit
  violation and an oversized aggregate; receipts of committing commands byte-identical to T013's golden receipts.
  Done when: green; existing tests unchanged. (FR-031, constitution I)
  Note: `RunAttemptAsync` now runs code → result size → per-touch change detection + document build + `AGGREGATE_TOO_LARGE` →
  audit → receipt build → receipt size, and only then stages aggregates, claims and the receipt (insert, or replace of an
  expired one). Receipt inputs are unchanged, so receipts are byte-identical (`ReceiptBuilderTests` green: stored JSON =
  builder output for `probe.nochange`/`probe.claim`/`probe.outboxflood`). New `tests/GameServer.Platform.Tests/Commands/
  CommandRunnerRulesBeforeStagingTests.cs` (the staging-spy tests live there to keep `CommandRunnerTests.cs` under ~450
  lines): `RunnerHarness.CreateRunner(store)` over a `RecordingStore` shows only `read …` operations for a result over the
  limit, a receipt over the limit, an audit violation and an oversized aggregate; a committing claim run stages
  `insert pa_probe`, `insert probe_claims`, `insert receipts` in that order. **Deviation (DI signature):** `CommandRunner`'s
  last constructor parameter is now `ILoggerFactory` (was `ILogger<CommandRunner>`) so the output-limit line uses the
  contract category `GameServer.Commands` while the replay warning keeps `GameServer.Platform.Commands.CommandRunner`. The
  runner is DI-created (`GameServerBuilder`) and built by hand only in `RunnerHarness` (`QueueLoggerFactory`).
- [x] T048 [US4] Typed size rejection (after T047; research R8 "Rejection path", errors-and-report-lines §2/§3/§5):
  `src/GameServer.Platform/Commands/CommitRules.cs` `CheckResultSize` returns `CommitViolation` (§5 result line, details
  `part=result`, `bytes`, `limit`); the runner throws its private `CommandRejectedException(COMMAND_OUTPUT_TOO_LARGE,
  details)` for both parts; in the `catch (CommandRejectedException)` the Error line of §2 (operation, part, bytes,
  limit, setting name) and `command_output_too_large_total{operation, part}` are emitted once per request, outside the
  attempt; `CommandExecution.Details` (new init property) carries the details of **every** HTTP 200 rejection, module
  codes included.
  Tests: `CommandRunnerTests` `Oversized_receipt_is_a_programming_error_and_rolls_back` and
  `Oversized_result_compact_is_a_programming_error` rewritten as `…_is_command_output_too_large` (code, details, 0
  documents, no receipt, metric, one Error line); a retry with the same request id executes again and gets the same code;
  a module `Reject(code, details)` keeps its details in `CommandExecution.Details`; `CommitRulesSizeTests` gains the
  result rule; `tests/GameServer.Platform.Web.Tests/CommandContextConformanceTests.cs` `The_result_size_check_is_the_runners`
  compares the runner's rejection code and details with the kit's line (T009 inventory).
  Done when: green; nothing reaches the `INTERNAL` catch-all for a size violation. (FR-030, FR-031, US4-1..US4-3, BC-1)
  Note: `CommitRules.CheckResultSize` returns `CommitViolation` (part `result`, §5 result line);
  `CommitRules.OutputPartResult` and `OutputLimitSetting(part)` added. The runner throws its private
  `CommandRejectedException(violation)` for both parts; the catch reports once per request (Error line in
  `GameServer.Commands` + `command_output_too_large_total{operation, part}`) and returns `CommandExecution(code, …) { Details }`.
  `CommandExecution.Details` (init, default empty, internal `EmptyDetails`) carries every HTTP 200 rejection's details,
  module codes included. Rewritten pins: `CommandRunnerTests.Oversized_receipt_is_command_output_too_large` and
  `Oversized_result_compact_is_command_output_too_large` (code, details, 0 documents, no receipt, metric 1, exactly one Error
  line asserted literally); `CommandContextConformanceTests.The_result_size_check_is_the_runners` compares the runner's code
  and details with the kit's new line. New tests: a retry with the same request id executes again with the same code and
  details (metric 2, 0 receipts); a raised `ReceiptMaxBytes` commits the same command; a module `Reject(code, details)` keeps
  `{reason}` in `CommandExecution.Details` (new probe command `probe.reject_details` in `TestModules`); a rejection without
  details and a commit have empty details. `CommitRulesSizeTests` +3 (result at/over the limit, setting names).
- [x] T049 [US4] Details on the wire (after T042, T043, T048, **T008** decision V11; wire-changes §2/§4):
  `CommandEndpointFactory.cs` fills `CommandResponse.details` (11) from `execution.Details`; `GmCommandResponse` gains
  init property `Details` filled the same way; `src/GameServer.Core/Commands/ICommandHandler.cs` XML doc of
  `ICommandOutcome.Details` per public-api §1.4 (BC-11); `docs/api/gm-openapi.json` regenerated and reviewed (additive).
  Tests in a new `tests/GameServer.Platform.Web.Tests/CommandOutputLimitTests.cs` (test module commands): result 1 byte
  over and exactly at `ResultCompactMaxBytes`; receipt 1 byte over and exactly at `ReceiptMaxBytes` (many outbox
  entries) → over: HTTP 200, `COMMAND_OUTPUT_TOO_LARGE`, details `part`/`bytes`/`limit`, 0 documents, metric, Error line;
  at: commits; raised `GameServer:Commands:ReceiptMaxBytes` → the same command commits; a GM command over the limit → 200
  GM response with the code and `details`; a module rejection with details → details in the response (BC-11); a
  creating login whose receipt is over the limit → `INTERNAL` and the creation rule line (T026).
  Done when: green; `OpenApiTests` green. (FR-030, FR-031, SC-005, US4-1, US4-2, US4-3, BC-11)
  Note: `CommandEndpointFactory.BuildResponse` copies `execution.Details` into `CommandResponse.details`;
  `GmCommandResponse.Details` (init, sorted copy, empty for a commit). `ICommandOutcome.Details` and
  `CommandOutcome.Reject` XML docs per public-api §1.4. `docs/api/gm-openapi.json` regenerated with `UPDATE_OPENAPI=1`
  (rewritten as LF): +6 lines, only `details` (`object`, string `additionalProperties`) on `GmCommandResponse`;
  `OpenApiTests` green. New `tests/GameServer.Platform.Web.Tests/CommandOutputLimitTests.cs` (module `output` with
  `output.result`, `output.flood`, GM `output.gm_flood`, `output.reject`): result 512 commits / 513 refused (HTTP 200, code
  23, details, counter not written, 0 receipts, metric, literal Error line); receipt measured with a probe run, then exactly
  4096 commits and 4097 is refused; `GameServer:Commands:ReceiptMaxBytes = 16384` commits the same command; the GM command
  over the limit answers 200 JSON with `code`, `codeNumber` 23, `receiptId: null`, `details`; a module rejection's details
  reach `CommandResponse.details` (BC-11); a creating login with `FloodOutbox(40)` answers 500 `INTERNAL` with the creation
  rule line containing the §5 receipt text.
- [x] T050 [US4] Command settings ranges (after T023; research R9, errors-and-report-lines §4.2):
  `src/GameServer.Platform/GameServerOptions.cs` `CommandSettings.Validate()` (one line per violation; the cross rule
  `4·⌈ResultCompactMaxBytes/3⌉ + 1024 ≤ ReceiptMaxBytes`); `ModuleRegistry.Validate` calls it after the initializer
  block (serve, provision, kit); when `MaxAggregatesPerGame` is out of range only its range line is written and the count
  check uses 16.
  Tests: a new `tests/GameServer.Platform.Tests/CommandSettingsValidationTests.cs` (defaults valid; each bound −1/+1 per
  setting with the literal line; the cross rule at equality and one over); a new
  `tests/GameServer.Platform.Web.Tests/CommandSettingsStartupTests.cs` (every settings line + one initializer line + one
  unrelated fault in one start → one report with all lines, exit 1; provision mode the same lines).
  Done when: green. (FR-032, SC-006, US4-4, BC-2)
  Note: `CommandSettings.Validate()` (range constants, cross rule `4·⌈r/3⌉ + 1024`) and `EffectiveMaxAggregatesPerGame`
  (16 when out of range); `ModuleRegistry.Validate` adds the lines right after `ValidateAccountInitializers`, and the
  aggregate count uses the effective maximum. **Finding:** the cross rule makes 1112 the smallest `ReceiptMaxBytes` that can
  start (`ResultCompactMaxBytes` ≥ 64 needs 4·22 + 1024 = 1112), so a value in 1024..1111 is in its own range but always gets
  the cross line; both texts are contracts and were kept (release notes "Known before the tag", for PCR §22 in T072).
  Consequence: `AccountWriterCreationTests`' receipt-too-large fixture used `ReceiptMaxBytes = 1024` and asserts that the
  registry validates, so it now uses 1112 with `ResultCompactMaxBytes = 64` (the expected rule line says 1112). New
  `tests/GameServer.Platform.Tests/CommandSettingsValidationTests.cs` (defaults valid; each bound −1/+1 per setting with the
  literal line; the receipt/result bounds with a matching other value; the cross rule at equality 1708 and one under; order
  of the lines; registry placement; out-of-range maximum = only its range line; in-range maximum still limits the count) and
  `tests/GameServer.Platform.Web.Tests/CommandSettingsStartupTests.cs` (every settings line + one initializer line + an
  invalid GameId in one start: serve throws one `GameServerStartupException`, `ServeAsync` exits 1, provision exits 1 with
  the same lines and the provisioner is never called; the initializer line comes before the settings lines).
- [x] T051 [US4] Kit receipt check (after T030, T048; research R14, public-api §6): `src/GameServer.TestKit/AccountCreationCheck.cs`
  / `CommandContextDouble.cs`: `CheckCommit(outcome, encodeResult)` adds the §5 receipt line measured with `ReceiptBuilder`
  from the R14 inputs (64-character body-hash placeholder; configured actor, player, request id, config version, `At`);
  the result line is the §5 text; new `Seed<TState>(aggregateId, state, rev)` (receipt `newRev` = rev + 1; the existing
  `Seed` keeps its revision); XML doc lists the placeholders; `CheckCommit(outcome)` without encoder keeps "no result → no
  receipt check" (documented).
  Tests in `tests/GameServer.Platform.Web.Tests/CommandContextDoubleTests.cs`: receipt over the limit → one line; at
  the limit → empty; `Seed(…, rev: 9)` changes the measure by the digit-length difference only;
  `tests/SampleGame.Tests/BuyItemHandlerTests.cs` and the fixture `SaveNoteHandlerTests` green unchanged;
  `TestKitArchitectureTests` green.
  Done when: green. (FR-034, US4-6)
  Note: the receipt measure lives in `AccountCreationCheck.cs` (Planning note 15): private `MeasureReceipt(operation,
  bodyHash, resultCode, resultBytes, writes)` builds with `ReceiptBuilder` from the configured actor, request id, receipt id,
  config version, `At`, Touches ∪ Reads, written aggregates (`newRev` = loaded rev + 1), claims and outbox; used by
  `CheckCommit(outcome, encodeResult)` (64-character `'0'` body-hash placeholder, runner's order: code, result, aggregates,
  audit, receipt) and by `CheckAccountCreation()` (empty-body hash). `Seed<TState>(id, state, rev)` (rev ≥ 1, else
  `ArgumentOutOfRangeException`); `Seed(id, state)` delegates with rev 1; the seeded document stores the rev. XML docs list
  the configured inputs and the placeholder; `CheckCommit(outcome)` documents "no encoder → neither size checked".
  `CommandContextDoubleTests` +4: receipt one byte over → the literal line, at the limit → empty; `Seed(…, rev: 9)` and
  `rev: 99` change the measure by +1/+2 bytes (newRev "2" → "10" → "100"); rev 0 refused; no encoder → no receipt line.
  `BuyItemHandlerTests`, fixture `SaveNoteHandlerTests` and `TestKitArchitectureTests` green in the full suite.
- [x] T052 [US4] Receipt parity (after T051; **[VERIFY] V7**): new `tests/GameServer.Platform.Web.Tests/ReceiptParityTests.cs`:
  for the sample `battle.end` and `shop.buy` (`GameHttp.FullGame`) and the `StartingStateModule` creation, the receipt
  bytes read from the store equal the double's `ReceiptBuilder` output for the same actor, player, request id, config
  version and time (0 bytes difference); a longer actor id or config label changes both by its length difference.
  The sample initializer row is added in T064, the fixture command in T065.
  Done when: green; a Note records the V7 result (or the input added to the double). (FR-034, SC-005)
  Note (V7 holds, no input added to the double): new `tests/GameServer.Platform.Web.Tests/ReceiptParityTests.cs` (inline
  relay off so the stored receipt is the committed bytes): `shop.buy` and `battle.end` on `GameHttp.FullGame` and a
  `StartingStateModule` creation with `Grant(250)` — the host's handler (or initializer) runs in the double seeded with the
  state read before the command and configured with the stored receipt's actor ids, correlation id, request id, receipt
  id, committed time and the host config, with a 1-byte limit so the line reports the measure: stored size = measured size
  for all three (0 bytes difference). A config version longer by `-longer` changes the measure by 7 bytes. Finding: a
  first try with `+longer` gave +12 — the JSON writer escapes `+` as `\u002B` (6 bytes); it applies to runner and double
  alike (sample config versions contain `+`), so parity holds; recorded in the release notes.
- [x] T053 [P] [US4] Couchbase typed paths (after T048; **[VERIFY] V2** final): `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseRunnerTests.cs`
  gains: result over the limit and receipt over the limit → `COMMAND_OUTPUT_TOO_LARGE`, 0 documents; exactly at the limit
  commits; a broken aggregate in `Touches` → typed code, 0 documents.
  Done when: green against local Couchbase; skips with a message otherwise. (FR-031, FR-092)
  Note: `CouchbaseRunnerTests` +3, green against local Couchbase EE 7.6.12 (12/12 in the class): result 513 →
  `COMMAND_OUTPUT_TOO_LARGE` with part `result` and no receipt, 512 commits; receipt probed, then limit + 1 refused (nothing
  in `pa_probe`) and exactly at the limit commits with its receipt; a counter stored with `schemaVersion` = code + 1 →
  `GameServerException(AGGREGATE_VERSION_AHEAD)` with `aggregate=counter` and the document unchanged (rev 3, value 7). **V2
  final: holds** (the typed rejection from the attempt body reaches the runner's catch unchanged on Couchbase).
- [x] T054 [US4] US4 checkpoint (after T049–T053): release notes draft — FG-016 section (limits, rejection, kit check),
  BC-1, BC-2, BC-11 rows (owner question 2 and its one-line fallback filter); `TelemetryTests` metric list gains
  `command_output_too_large_total`; full suite in memory and File.
  Done when: green. (FR-090)
  Note: release notes FG-016 section, BC-1/BC-2/BC-11 "how it surfaces" filled with the asserted texts (BC-11 names its
  one-line fallback filter), V7/V8 rows. `TelemetryTests.Obs01_metrics_are_recorded_without_player_or_request_labels`
  records and expects `command_output_too_large_total`. Checkpoint (after T047–T053): Release 0 warnings; full suite in
  memory 2587 total / 2477 passed / 110 skipped / 0 failed (baseline 2530/2423/107 + 57 new tests, +3 skips = the new
  Couchbase tests without `COUCHBASE_CONNSTR`). The File-store run of the same code is part of the T062 checkpoint run
  (US5 only adds SDK files and client tests).

**Checkpoint**: an oversized result or receipt is a 200 rejection with details and nothing staged; settings out of
range stop startup with one line each; the kit measures the runner's receipt byte for byte.

## Phase 7: US5 — A command sent before the app died is replayed, not run twice, after the restart (P5, FG-022)

**Goal**: caller-owned request ids, an opt-in pending-command store (one atomic file per entry), listing and resend after
login, a Unity helper; everything additive and C# 9 / netstandard2.1.

**Independent test**: the four app deaths of SC-007 against the sample host (T061); every save-step fault and corrupt
file (T056, T057).

All files are under `sdk/unity/com.gameserver.client/`; tests under `tests/GameServer.Client.Tests/`. Every new package
file gets a hand-written `.meta` (fresh GUID, the 015 shape) before any Unity run.

- [x] T055 [US5] Caller request ids (after T045, **T003** decision V1; research R10, public-api §7.1, data-model §4.1):
  `Runtime/Core/GameServerClient.cs` — the overload of the T003 decision (`SendCommandAsync<TResult>(route, request,
  resultParser, string? requestId, CancellationToken cancellationToken = default)`, `requestId` required; null =
  generated; otherwise a UUID in format `D`, any case, normalized to lowercase; anything else → `ArgumentException`
  before any I/O); static `NewRequestId()`; the existing overload delegates with null; `SendAsync` takes the id instead of
  `Guid.NewGuid()` (line 419) and sends it on every attempt with the same bytes. `CommandResult<TResult>` gains
  `RequestId` and the five-argument constructor (the old one sets `string.Empty`). `Runtime/Core/GameServerError.cs`
  gains `Client(code, message, retryable, requestId)` (the existing three-parameter `Client` keeps its binding) and every
  SDK-made error of a call (network, `INVALID_RESPONSE`) carries the call's request id.
  Tests: a new `RequestIdTests.cs` (a fake transport records the header per attempt: caller id on every retry;
  `CommandResult.RequestId`; `GameServerError.RequestId` on a network error; invalid ids — empty, `N` format, braces,
  37 characters — → `ArgumentException` and 0 transport calls; an uppercase id normalized; `NewRequestId()` format; the
  same id and bytes in a later call replay against the sample host, CMD-02); a new `OverloadBindingTests.cs` (the five V1
  call shapes compile in the test project and each sends exactly one request; the caller shape sends its id; the C# 9 /
  Unity compiler check is T003 and T068, Planning note 12).
  Done when: green; `SendCommandAsync(r, q, p, default)` still binds to the existing overload (no CS0121). (FR-040,
  FR-041, US5-1)
  Note: new overload exactly as public-api §7.1 (T003 decision) in the new partial
  `Runtime/Core/GameServerClient.PendingCommands.cs` (`GameServerClient` is now `sealed partial`); the existing overload
  delegates with `null`. `requestId` is validated (`Guid.TryParseExact(…, "D")`, 36 characters) and normalized before the
  token check, the body, the store or the transport. `SendAsync` takes the id (fresh `NewRequestId()` for login, logout,
  snapshot, config, time); `NETWORK_ERROR`, malformed-response and HTTP-shape `INVALID_RESPONSE` errors and "command
  response without a code" carry it. `CommandResult` five-argument constructor + `RequestId` (old one → `""`);
  `GameServerError.Client(code, message, retryable, requestId)`. New `RequestIdTests` (15: caller id on every retry with the
  same bytes, generated id returned by both overloads, uppercase normalized, 5 invalid ids → `ArgumentException` with 0
  transport calls and 0 records, request id on network and invalid-response errors, `NewRequestId()` format, the old
  constructor and three-parameter `Client` keep their behaviour, replay of the same id and bytes against the sample host)
  and `OverloadBindingTests` (the five V1 shapes, one request each; only the string shape sends the caller's id).
- [x] T056 [P] [US5] Entry types and container format (after T001; sdk-pending-store §2, public-api §7.2): new
  `Runtime/Core/PendingCommandStore.cs` (`PendingCommand`, `PendingCommandStatus`, `IPendingCommandStore`,
  `InMemoryPendingCommandStore`) and internal `Runtime/Core/PendingCommandFormat.cs` (container v1: magic `GSPC`, version,
  reserved, payload length, payload, SHA-256; every refusal of §2), each with its `.meta`.
  Tests in a new `PendingCommandFormatTests.cs`: round trip; truncated header, payload and hash; wrong magic; version 2;
  bad UTF-8; id not a lowercase UUID; id ≠ file name; lengths past their bounds; trailing bytes; hash flip → refused with
  its reason, 0 exceptions; a body of 1 MiB at the bound.
  Done when: green; `UnityPackageMetaTests` green; existing `.meta` GUIDs equal T001 (b). (FR-045, FR-046)
  Note: `Runtime/Core/PendingCommandStore.cs` (`PendingCommand` validates a lowercase UUID id, non-empty player and route,
  copies the body; `PendingCommandStatus`; `IPendingCommandStore`; `InMemoryPendingCommandStore`; internal
  `PendingCommandList` keeps the recorded-time-then-id order) and internal `Runtime/Core/PendingCommandFormat.cs` (container
  v1 per sdk-pending-store §2, reader with bounded lengths, strict UTF-8, `IsLowercaseUuid`). Hand-written `.meta` files with
  fresh GUIDs (`PendingCommandStore.cs.meta` c1c7cbd0…, `PendingCommandFormat.cs.meta` 1f2a40e2…), added to
  `unity-package-guids.txt`; the 35 recorded GUIDs unchanged. `PendingCommandFormatTests` (21): round trip, 1 MiB body
  bound (and 1 MiB + 1 not serialized), empty body, truncated header/payload/hash, wrong magic, version 2, trailing bytes,
  hash flip, invalid UTF-8, uppercase and non-UUID ids, id ≠ file name, id/player/route past their bounds, a length beyond
  the payload, empty player/route, constructor rules, body copy — each refusal with its reason and no exception.
- [x] T057 [US5] Persistent store (after T056, **T007** decision V8/V9; sdk-pending-store §1, §3):
  `Runtime/Core/CacheFileOps.cs` gains virtual `EnumerateFiles(folder, pattern)`; `PersistentPendingCommandStore(folder)`
  in `Runtime/Core/PendingCommandStore.cs`: load in the constructor (create folder, delete `*.pending.tmp`, read and
  validate each `*.pending`, order by recorded time then id; a damaged entry skipped, deleted and named in
  `LoadProblem`; an enumeration failure → empty store with `unreadable: {ExceptionType}`; never throws), `Record`
  (memory first; temp + write + flush (V8 decision) + `Move`, `Replace` when the final name exists; `WriteProblem` on
  failure; never throws), `Remove` (memory first; `Delete` without flush). File names come only from the validated id.
  Tests in a new `PersistentPendingCommandStoreTests.cs` (a `CacheFileOps` subclass failing one step): a fault at each of
  open, write, flush, move, replace, delete → the old or the new entry loadable by a new store, `WriteProblem` set, no
  exception; leftover temp deleted at load; read-only folder (`ReadOnlyFolder` helper of 015); two valid entries and one
  damaged → two loaded and the reason; the session token never appears in any file (byte scan).
  Done when: green. (FR-045, SC-007)
  Note: `CacheFileOps` gains virtual `EnumerateFiles(folder, pattern)` and the shared `IsFileProblem` (the config cache now
  calls it). `PersistentPendingCommandStore(folder)`: load creates the folder, deletes exact `{uuid}.pending.tmp` leftovers,
  reads each `*.pending` whose name is an exact lowercase entry name (V9: anything else, e.g. `X.PENDING`, uppercase ids or
  `notes.pending`, is named in `LoadProblem` as `not an entry file name` and **left on disk**, since it is not a file the
  store wrote — a deliberate narrowing of "skipped, deleted"), deletes and names damaged entries, and turns an enumeration
  failure into an empty store with `unreadable: {Type}`. `Record`: memory first, then temp + write + `Flush(true)` (V8) +
  `Move`, or `Replace` when the entry file exists; `WriteProblem = "not saved: …"`, temp deleted. `Remove`: memory first,
  `Delete` without flush, `WriteProblem = "not removed: …"` on failure. `PersistentPendingCommandStoreTests` (23, with
  `FaultyPendingOps` in the new `PendingCommandTestSupport.cs`): faults at open/write/flush/move (new entry: kept in memory,
  no file) and open/write/flush/replace (same id again: old file kept), delete (file stays, next store lists it), a later
  success clears the problem, leftover temp deleted, two valid + one damaged, a copied entry under another id, three
  not-our-name files left alone, enumeration failure, read-only folder (`ReadOnlyFolder`), an entry too large to store; no
  exception anywhere. The session-token byte scan is in `PendingCommandClientTests` (the store never sees the token; the
  client is what could leak it).
- [x] T058 [US5] Client send flow with a store (after T055, T057; sdk-pending-store §4): `Runtime/Core/GameServerClientOptions.cs`
  gains `PendingCommands` (default null) and `PendingCommandMaxAge` (default 23 h; `Validate()` requires (0, 168 h]); the
  store logic lives in a new partial `Runtime/Core/GameServerClient.PendingCommands.cs` (+ `.meta`; `GameServerClient`
  becomes `partial`, Planning note 15): with a store, `SendCommandAsync` records `(id, session player, route, exact
  body bytes, ServerNow)` **before** the first attempt; removes on a `CommandResponse` with any code or an envelope whose
  code is not retryable; keeps on no answer, `INVALID_RESPONSE`, a retryable last answer (`COMMIT_UNKNOWN_RETRY`,
  `AGGREGATE_VERSION_AHEAD`) and cancellation after the record. Login, logout, snapshot, config and time calls never touch
  the store. Without a store, nothing is written (with or without a caller id).
  Tests in a new `PendingCommandClientTests.cs` (fake transport): each remove and keep case; the record happens before
  the first transport call; the five non-command calls never touch the store; `IDEMPOTENCY_PAYLOAD_MISMATCH` removes the
  entry and throws; a disk failure still sends the command and exposes `WriteProblem`; without a store the transport
  sees exactly today's requests.
  Done when: green. (FR-042, FR-044, FR-045, US5-2, US5-3, US5-6)
  Note: `GameServerClientOptions.PendingCommands` (null) and `PendingCommandMaxAge` (23 h; `Validate()` requires (0, 168 h]).
  With a store, the overload records `(id, session player, route, body bytes, ServerNow)` before the first attempt (only when
  the session has a player id; without one the entry could never be resent, `StatusOf` = `OtherPlayer`), removes on any
  `CommandResponse` and on a final envelope (`CodeNumber ≥ 0` and not retryable: SDK-made errors use -1), keeps otherwise.
  `PendingCommandClientTests`: final answers (OK, business code, non-retryable envelope, `IDEMPOTENCY_PAYLOAD_MISMATCH`)
  remove; network, invalid response, `COMMIT_UNKNOWN_RETRY` and `AGGREGATE_VERSION_AHEAD` as last answers and cancellation
  keep (entry with id, player, route and exact body); the record is visible at the first transport call; the entry records
  the server clock (not the device's); guest login, snapshot, config, time and logout make 0 store calls;
  a flush failure still sends (3 attempts) and shows `WriteProblem`; without a store the request is the same (method, URL,
  body, headers); the session token never appears in an entry file.
- [x] T059 [US5] Listing and resend (after T058; public-api §7.1, data-model §4.2): `PendingCommands`, `StatusOf`
  (`OtherPlayer` for another player or no session, `Expired` at `ServerNow − RecordedAtServerUtc ≥ PendingCommandMaxAge`,
  else `Resendable`), `ResendPendingAsync<TResult>(command, parser, ct)` (stored bytes, stored id, stored route; the same
  remove/keep rules; `InvalidOperationException` when not `Resendable`), `DiscardPendingCommand(requestId)` (unknown ids
  ignored). No automatic resend.
  Tests in `PendingCommandClientTests`: each status, the expiry on the server clock (a device clock change does not move
  it), resend refused for each non-resendable status, resend sends byte-identical bodies, discard.
  Done when: green. (FR-043, US5-3, US5-4)
  Note: `PendingCommands`, `StatusOf`, `ResendPendingAsync` (status must be `Resendable` **and** the entry must be in the
  store, else `InvalidOperationException`; same remove/keep rules), `DiscardPendingCommand` (only ids in the store reach
  `Remove`). Tests: Resendable/OtherPlayer/Expired with the device clock moved 30 days (no effect) and the monotonic clock
  moved to 23 h − 1 s (Resendable) and 23 h (Expired), no session → OtherPlayer; max-age validation; resend sends the
  stored bytes, id and route and removes on the answer; a resend that is lost again keeps the entry; resend refused for
  another player's, an expired and an unknown entry with 0 transport calls; discard of unknown and known ids.
- [x] T060 [P] [US5] Unity helper (after T057; public-api §7.4): new `Runtime/Unity/UnityPendingCommandStore.cs` (+ `.meta`)
  under `#if UNITY_5_3_OR_NEWER`: `Create(environmentName)` → `persistentDataPath/gameserver-pending/{name}` with the 015
  name rule (`PersistentConfigCache.FolderNameProblem`), the iOS no-backup flag on the folder under `#if UNITY_IOS &&
  !UNITY_EDITOR`, XML doc of the threading rule (one client per folder, Unity main thread).
  Done when: a scratch C# 9 project compiling the package `Runtime` against Unity 6000.3.22f1's `UnityEngine.CoreModule`
  with and without `UNITY_IOS` builds with 0 warnings (the 015 m8 procedure; Note); `UnityPackageMetaTests` green.
  (FR-046, FR-047, US5-7)
  Note: `Runtime/Unity/UnityPendingCommandStore.cs` (+ `.meta` b0a6215d…; `GameServerClient.PendingCommands.cs.meta`
  0c6e8678…), `Create(environmentName)` → `persistentDataPath/gameserver-pending/{name}` with
  `PersistentConfigCache.FolderNameProblem(name, "pending command store")` (new overload naming the store kind; the
  one-argument form keeps the config-cache message), iOS no-backup flag on the folder under `#if UNITY_IOS && !UNITY_EDITOR`,
  XML doc of the threading rule. The 015 m8 check (scratch `unitycheck` project: netstandard2.1, C# 9, warnings as errors,
  `UNITY_5_3_OR_NEWER`, references Unity 6000.3.22f1 `UnityEngine.CoreModule` and `UnityEngine.UnityWebRequestModule`)
  builds the package `Runtime` with 0 warnings / 0 errors both without and with `UNITY_IOS`. `UnityPackageMetaTests` green
  (full suite).
- [x] T061 [US5] End to end and cost (after T059; SC-007, SC-008, **[VERIFY] V8** final): new
  `PendingCommandEndToEndTests.cs` (sample host through `SdkHarness`, a persistent store in a temp folder): the five rows
  of the sdk-pending-store §5 crash table (temp only; record then the request dropped; committed then the response
  dropped; response then a failing delete; after remove) each followed by a new client on the same folder, login and
  resend → exactly one execution (ledger count), `replayed = true` with the original receipt id after a commit; an entry
  of another player is refused. New opt-in `PendingCommandStorePerformanceTests.cs` (`GAMESERVER_PERF=1`): record and
  remove of a 4 KiB body, 200 iterations after warm-up, on the T007 reference drive → p95 ≤ 10 ms each.
  Done when: green; a Note records the p95 values. (FR-042, FR-043, FR-047, SC-007, SC-008, US5-3, US5-5)
  Note: new `PendingCommandEndToEndTests` (6, sample host through `SdkHarness`; `SdkHarness.NewClient` gains an optional
  `pending` store; purchases = `shop.buy` receipts of the player, gold = ledger sum): temp only (record never reached its
  rename, then a leftover temp) → nothing listed, folder empty, 0 purchases; request dropped before the server → resend
  executes once (`Replayed = false`, gold debited once); response dropped after the commit → resend `Replayed = true` with the
  original receipt id, 1 purchase; response received but the delete failed → the next client lists it and the resend
  replays the first receipt; after a remove → nothing listed; another player's entry → `OtherPlayer` and resend refused.
  New opt-in `PendingCommandStorePerformanceTests` (`GAMESERVER_PERF=1`, 20 warm-up + 200 iterations, 4 KiB, `%TEMP%` on C:):
  record p50 2.29 / **p95 5.56 ms**, remove p50 0.20 / **p95 0.26 ms** (≤ 10 ms; V8 final holds, `Flush(true)` kept).
- [x] T062 [US5] SDK guards and US5 checkpoint (after T055–T061): `SdkPublicApiTests` against the T001 baseline → 0
  removed or changed lines, the added lines listed in a Note; `sdk/GameServer.Client` C# 9 / netstandard2.1 0 warnings;
  `UnityPackageMetaTests` covers the four new files; the package version stays `0.7.0`; release notes draft — FG-022
  section with the client snippet (`NewRequestId`, `Options.PendingCommands`, listing and resend after login); full
  suite in memory and File.
  Done when: green. (FR-046, FR-047, SC-009, FR-090)
  Note: `SdkPublicApiTests` green; `sdk-public-api.txt` re-recorded from the Release SDK (LF): 0 removed or changed lines,
  +44 added — `CommandResult` five-argument constructor and `RequestId`; `GameServerClient.NewRequestId`, the
  `SendCommandAsync(…, string requestId, …)` overload, `PendingCommands`, `StatusOf`, `ResendPendingAsync`,
  `DiscardPendingCommand`; `GameServerClientOptions.PendingCommands`/`PendingCommandMaxAge`; `GameServerError.Client(…,
  requestId)`; types `PendingCommand` (constructor + 5 properties), `PendingCommandStatus` (3 values), `IPendingCommandStore`
  (5 members), `InMemoryPendingCommandStore` and `PersistentPendingCommandStore` (constructor, members, `Folder`).
  `EntrySuffix` was made internal so the surface is exactly public-api §7. `sdk/GameServer.Client` (C# 9, netstandard2.1)
  0 warnings; `UnityPackageMetaTests` covers the four new `.meta` files (GUID list +4 lines); package version stays
  `0.7.0`. Release notes FG-022 section with the client snippet. Checkpoint: Debug and Release 0 warnings; full suite in
  memory 2680 total / 2569 passed / 111 skipped / 0 failed and with `GAMESERVER_TEST_STORE=File` the same 2680 / 2569 / 111 /
  0 (`COUCHBASE_*` unset; +150 tests and +4 skips over the 2530/2423/107 baseline: 3 Couchbase tests, 1 opt-in perf test).

**Checkpoint**: a command whose outcome is unknown survives a restart and replays (or executes once); every save fault
and corrupt file leaves loadable entries; a client without a store behaves exactly as before.

## Phase 8: US6 — The samples, fixture and docs use the same paths (P6, FG-013, FG-015, FG-016, FG-022, FG-029)

**Goal**: the reference sample, fixture, template and Unity client use the shipped paths, and the guides and PCRs
describe them.

### Dogfood

- [ ] T063 [US6] Sample initializer (after T025, T030; research R13, errors-and-report-lines §4.3): new
  `samples/SampleGame/SampleGame/SampleAccountInitializer.cs` (`sample.starting_gold`, touches `wallet`, `ResourceCatalog`
  by constructor; reads `account.json` only when `context.Config.HasFile` is true; grants `startingGold` gold through
  `EconomyOperations.Grant` with ledger source `account_init`; changes nothing when the file is absent);
  `samples/SampleGame/SampleGame/SampleGameConfigRules.cs` rule `sample.account` (the §4.3 line; integer 0..gold
  `maxQuantity`); `samples/SampleGame/SampleGame/ShopHandler.cs` `SampleGameModuleDefinition` declares
  `ConfigFileDeclaration.Optional("account.json", rule)` and `AccountInitializers = [Of<SampleAccountInitializer>(…)]`;
  new `samples/SampleHost/config/base/account.json` (`{ "startingGold": 100 }`) listed in `manifest.json` `files`, not in
  `clientVisible`. `tests/Shared/config` gets no file.
  Tests: a new `tests/SampleGame.Tests/SampleAccountInitializerTests.cs` (a host on `SeededConfig` with `account.json`:
  balance 100, one ledger entry `account_init`, one creation receipt, `is_new_player = true`; concurrent first logins →
  one grant; without the file → no write, no receipt; rule lines for a negative value, a string and a value over
  `maxQuantity`; the kit run through `ForAccountInitializer`); `tests/SampleGame.Tests/ConfigDeclarationParityTests.cs`
  covers `account.json` (host vs kit same lines); `SampleConfigRulesTests.cs` gains the rule cases.
  Done when: green; the full-game suites on `tests/Shared/config` pass unchanged. (FR-050, US6-1)
- [ ] T064 [US6] Hosts on the real SampleHost config (after T052, T063; T009 inventory): run the T009-listed `LocalFlavorFactory`
  suites (`Sample.Counter.Tests`, `SampleGame.Tests/CatalogCompositionTests`, the Platform.Web, Architecture and Client
  tests, Couchbase `SampleHostFlavorTests`); update only assertions on a new player's gold, ledger rows, receipts or
  revisions, each with a comment naming `account.json`; record the new SampleHost server `config_version` (changed once
  vs T001 (d)) and the unchanged client version. `ReceiptParityTests` gains the sample initializer row.
  Done when: green; a Note lists every changed assertion (expected few or none, Planning note 5). (FR-050, FR-034,
  SC-005)
- [ ] T065 [P] [US6] Consumer fixture (after T030, T051; Planning note 9): new
  `tests/ConsumerFixture/src/Fixture.Game/FixtureWelcomeInitializer.cs` (`fixture.welcome`, touches the hidden `notes`
  aggregate, sets `NotesState { Text = welcome text cut to notes.json maxLength, Saves = 0 }`) registered in
  `FixtureModuleDefinition.cs`; new `tests/ConsumerFixture/tests/Fixture.Tests/FixtureInitializerTests.cs` (kit
  `ForAccountInitializer`, no host: `Written` notes, `CheckAccountCreation` empty; a 1-character `maxLength` cuts the text)
  and `FixtureReceiptSizeTests.cs` (`CheckCommit(outcome, encode)` of `fixture.save` → no line at the defaults; with
  `WithCommandSettings` a receipt limit the outcome exceeds → the §5 receipt line; host receipt bytes of `fixture.save`
  equal the kit measure, the fixture row of SC-005).
  Done when: the fixture suites green (`FixtureCatalogTests` card title still empty, `FixtureHostSmokeTests` unchanged).
  (FR-051, SC-005)
- [ ] T066 [P] [US6] Template (after T011): `templates/module/Template.Module/TemplateModuleDefinition.cs` gains a
  commented initializer example (registration, touches, purity, the budget) next to the existing commented
  declarations; nothing registered.
  Done when: template-based tests (`WireMapperRegistrationTests`, `WireConformanceTests`) green unchanged. (FR-061)
- [ ] T067 [US6] KestrelDemo (after T059, T060; FR-052): edit only
  `samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs` and
  `samples/SampleGame/KestrelDemo/Assets/Editor/SmokeBuild.cs` (opened by exact path): `CreateClient` sets
  `Options.PendingCommands = UnityPendingCommandStore.Create("local")`; `RunAsync` logs `new player: {IsNewPlayer}`
  after login, keeps the login sections as the boot read (no snapshot call; it has none today), lists `PendingCommands`
  (log `pending {id} {status}`), resends the `Resendable` ones (log `resent {id}: replayed={Replayed}`) and discards the
  others, and sends battle end with `GameServerClient.NewRequestId()` (an unknown outcome logs `EndBattle → pending {id}`
  instead of failing the run); `SmokeBuild.EndToEnd` accepts `-e2eDevice <name>` to keep one guest device across runs in
  `PlayerPrefs` (the scene client's rule; Planning note 18).
  Done when: `git diff` of those two files shows only these changes; `tests/GameServer.Client.Tests/SdkRouteTests.cs`
  (literal scan) green. The MonoBehaviour compiles only in Unity (T068). (FR-052)
- [ ] T068 [US6] **Agent-run Unity** (after T067; **[VERIFY] V1** final, **V9** Editor and Windows player; FR-092 manual
  check): no Editor holds KestrelDemo.
  1. `smoke-build.ps1 -Unity "<path>" -CompileOnly` → 0 `error CS`/`warning CS` (V1 with the real overload).
  2. SampleHost on 5080 (`run-local.ps1 -Port 5080`); a scratch dropping proxy on 127.0.0.1:5092 (session scratch,
     never committed) that forwards to 5080 and resets the connection after forwarding a battle-end request.
     Run A: `-E2E -ServerUrl http://127.0.0.1:5092/` with `-e2eDevice t068` → logs `new player: True`, gold seeded from
     `account.json`, `EndBattle → pending {id}`; the Editor's `persistentDataPath/gameserver-pending/local` holds one
     `{id}.pending`. Run B: `-E2E -e2eDevice t068` (5080) → `new player: False`, `pending {id} Resendable`,
     `resent {id}: replayed=True`, the folder empty, one battle reward in the ledger (GM ledger or store count).
  3. V9 Windows player: a **temporary** runtime probe (`Assets/Probe016/…`, script + scene + `.meta`, deleted afterwards)
     built as a Win64 player into the scratch folder records, lists, removes three entries with
     `PersistentPendingCommandStore` and logs the folder listing; afterwards restore the tracked files a Windows build
     changes (015 T008: `Assets/Settings/PC_RPAsset.asset`, `UniversalRenderPipelineGlobalSettings.asset`,
     `ProjectSettings/ProjectSettings.asset`) with `git checkout` and reopen once with `-buildTarget Android -quit`.
  4. Default `smoke-build.ps1` (Android APK, IL2CPP arm64, stripping High) → the package compiles under IL2CPP;
     `samples/SampleGame/KestrelDemo/Build/KestrelDemo.apk` (gitignored) is the input of T069.
  Done when: a Note records each run (exit codes, log lines, folder listings); `git status sdk/unity samples` shows only
  the two T067 files (Unity created no package `.meta`); SampleHost and the proxy stopped, 5080 and 5092 free. If the
  agent cannot start Unity, the task stays open with these commands for the owner and the release notes mark it
  pending. (FR-052, FR-092, V1, V9)
- [ ] T069 [US6] **Manual / owner** device session, combined with 015 T058 (after T068; V9 on devices, plus 015's V8,
  V10, V11): one session covers both slices. Android — the agent provides the T068 APK and starts SampleHost with
  `tools/local/run-local.ps1 -Port 5080 -Lan` plus the 015 T051 forwarder; the **owner** installs the APK on a device on
  the same network and runs five starts:
  1. fresh install → `new player: True`, `config downloaded {v}` (015), gold = seed + reward, no crash;
  2. second start → `new player: False`, `config from cache {v}` (015), no pending entry;
  3. the agent points the forwarder at the T068 dropping proxy → `EndBattle → pending {id}` (016, `File.Move` under
     IL2CPP);
  4. the agent points the forwarder back at SampleHost → `pending {id} Resendable`, `resent {id}: replayed=True` (016,
     `EnumerateFiles` under stripping High);
  5. the agent restarts SampleHost on a scratch copy of the config with a client-visible edit (`GameServer__ConfigPath`;
     never edit `samples/SampleHost/config`) → `config downloaded {w}` (015 replace path).
  Record per start: `Accept-Encoding` sent and response compressed (015 V8), cache and pending folder behaviour, no
  crash. iOS — the **owner** builds on macOS (no iOS module on this machine) and runs the same five starts.
  Done when: a Note per platform here and the 015 results copied under 015 T058; or the release notes (016 and
  `docs/releases/v0.7.0.md`) list the session as pending with these exact steps (the tag waits for it). (FR-052,
  FR-092, V9)

### Documentation (Vietnamese except the consumer guide; pages state "framework v0.7.0, chưa phát hành / unreleased")

- [ ] T070 [US6] Consumer guide `docs/guides/consumer-guide.md` (after T022, T032, T046, T054, T062): new
  `## 13. Login, account creation and command robustness` with one subsection per FR-060 bullet: 13.1 the login flow
  (flag, "the login sections are the boot read", lost response = `false`, durable onboarding in game state); 13.2
  account initializers (registration, the 4-aggregate budget, purity, failure = `INTERNAL` with log and metric, legacy
  accounts D6 and the two ways out, the kit call from `FixtureInitializerTests`); 13.3 degraded reads (codes, the
  unavailable list, SDK marks, deploy order); 13.4 command limits (which to raise and what it costs, the rejection, the
  kit receipt check from `FixtureReceiptSizeTests`); 13.5 request ids and the pending store (`NewRequestId`,
  `Options.PendingCommands`, Unity setup, resend after login, one client per folder, max age vs `ReplayWindowHours`).
  §11 bump checklist gains the 0.7.0 steps; line 347 ("runner: INTERNAL", "kit: no") is rewritten; §10 points to §13.5;
  cross-slice BC ids written `016/BC-n`.
  Done when: every FR-060 bullet has a subsection; the examples compile (copied from tests); the stamp is present.
  (FR-004, FR-060, FR-062)
- [ ] T071 [P] [US6] `docs/guides/module-authoring.md` and `templates/module/README.md` (after T066): initializer
  registration, touches budget, purity rule, failure mode, the kit test.
  Done when: both describe the T066 example; stamp present. (FR-061, FR-062)
- [ ] T072 [P] [US6] PCR platform `docs/pcr/platform.md` (after T054): §2 (creation transaction, rules before staging),
  §3 (the size rejection replaces "vượt = lỗi lập trình (500)"), §4 (snapshot degradation, the API-10 exception, BC-3),
  §5 (the creation counts against the budget), §6 (version ahead → typed codes and degradation; deploy rule kept), §8 (the
  three codes), line 1061 (the kit now checks the receipt); new `## 22.` (limits table: five settings, two constants, the
  64 KiB budget, the player body limit, each with default, setting or constant, range, cost, failure mode; the worked
  example **measured** with `ReceiptBuilder` for the sample grant: base + per-entry bytes and the entry count at 4096 and
  16384; the four metrics; the startup lines; BC-1..BC-11). `tests/SampleGame.Tests/DocumentedConfigurationKeysTests.cs`
  reads `## 22.` too and asserts the five `GameServer:Commands:*` keys there.
  Done when: green; no section still says a newer document or an oversized receipt is a 500. (FR-028, FR-033, FR-061,
  US4-5)
- [ ] T073 [P] [US6] PCR `docs/pcr/identity.md`, `economy.md`, `sample-game.md`, `gm.md` (after T046, T063): identity —
  the flag definition, lost response, relation to `isNewUser`, creation through `IAccountWriter`, initializers, broken
  profile, login error mapping (BC-10); economy — grants from an initializer (source type, System actor in the ledger
  row); sample-game — the initializer, `account.json`, its rule; gm — `unreadable[]`, `details` and `unavailable` of the
  GM command response.
  Done when: each file has its section and stamp. (FR-005, FR-061, FR-062)
- [ ] T074 [P] [US6] PCR `docs/pcr/unity-sdk.md` (after T062, T068): request ids (`NewRequestId`, the overload,
  `CommandResult.RequestId`, `GameServerError.RequestId`); the pending store (container v1 and its version rule, one file
  per entry, the crash table, threading rule, the V8 numbers with their drive, the 23 h max age); unavailable sections
  (marks, the same-player rule); the three codes; "the login sections are a full snapshot"; manual check rows (T068,
  T069).
  Done when: every item present; stamp present. (FR-004, FR-045, FR-047, FR-061, FR-062)
- [ ] T075 [US6] HTML pages, stamps and the US6 release-notes pass (after T070–T074): `docs/html/01-gioi-thieu.html`
  (login, starting state, limits), `docs/html/02-them-command.html` §6b (schema version: typed codes and degradation,
  not 500) and its limits text, `docs/html/03-deploy.html` where it lists `GameServer:Commands` settings, startup lines or
  metrics (Planning note 10); stamp "0.7.0 (chưa phát hành)" on every page and guide this slice touched; release notes
  draft — manual checks table and migration checklist updated.
  Done when: `grep` over `docs/` finds no touched page that still says a newer document or an oversized receipt answers
  500 (Note with the grep); every touched page carries the stamp. (FR-028, FR-061, FR-062, US6-2, FR-090)

**Checkpoint**: the sample seeds from `account.json`, the fixture tests an initializer with the kit, KestrelDemo logs
the flag and resends a pending command; the guides and PCRs describe the shipped behaviour.

## Phase 9: Release notes, verification, review and close

- [ ] T076 Release notes final pass `specs/016-login-command/release-notes-draft.md` (after T068, T069 status, T075;
  FR-090): summary; one section per fixed FG in round order (FG-013, FG-015, FG-029, FG-016, FG-022); the BC-1..BC-11
  table with "how it surfaces" taken from the real lines and codes; the first-consumer migration checklist in the plan's
  order with every zh-pvp file:line of the spec table (T002 list); the "removable vs stays" table (six markers, FG-029
  without marker); the rolling-deploy note and the compatibility table; decisions awaiting owner override (plan owner
  questions 1–6) with their status; the manual checks (T003, T068, T069, T077, T078) with their status; "no route and no
  persisted document shape changes; five additive proto fields and one message; additive GM JSON members; the Unity
  package stays 0.7.0"; "Known before the tag".
  Done when: every FG and BC id appears; every zh-pvp file:line of the spec table is referenced (grep counts in a Note).
  (FR-090, SC-010)
- [ ] T077 Reviewer walkthrough (story 6, FR-092 manual; after T070–T076): a fresh agent (no context beyond the docs)
  follows consumer guide §13 and `docs/guides/module-authoring.md` in a **temp copy** of the repository: add an account
  initializer to the consumer fixture with a kit test; add a command with many outbox entries, raise
  `GameServer:Commands:ReceiptMaxBytes` and check it with the kit receipt check; with the SDK (scratch C# 9 console on
  `sdk/GameServer.Client`) against the fixture host on a free port (never 5180: override `Urls`), send a command with a
  pending store, drop the response, create a new client on the folder, log in and resend.
  Done when: the agent's list of missing or wrong steps is recorded as a Note and each item is fixed in the docs (or
  answered); the temp copy is deleted; nothing of it is committed. (FR-060, FR-092, US6-2)
- [ ] T078 **Manual / owner-assisted** first-consumer check (SC-010; never edit `E:\ProjGits\zh-pvp-server` or
  `E:\ProjGits\research-and-development-games`): a temp clone of the game repository with its `framework/` submodule
  replaced by a clone of this branch's head (uncommitted slice changes copied in). In the clone only:
  1. build → record every compile result (BC-7 expects none);
  2. move `InitAccountHandler` (`src/ZhPvp.Game/Account/InitAccountHandler.cs`) into an `IAccountInitializer` (touches
     `progress`, `wallet`, `armory`; the same grant with source `account_init`) as the release notes say; start the host
     on a free port → no BC-9 line (budget exactly 4);
  3. rewrite `tests/ZhPvp.Tests/Game/AccountInitTests.cs` as "a first login seeds the account once" (concurrent logins,
     lost response) and `InitAccountHandlerTests.cs` with `ForAccountInitializer`; replace the FG-016 file-store
     measurement (`AccountInitTests.cs:124`) by the kit receipt check; run the game suite;
  4. the client snippet of the release notes (login flag, `Sections.Unavailable`, `NewRequestId`,
     `Options.PendingCommands`, resend after login) compiles against `sdk/GameServer.Client` in a scratch C# 9 project
     (the game files need Unity);
  5. each of the six markers is removable as the release notes say (list file:line and the replacement).
  If the game's tests do not build because of an earlier slice's BC (013–015), apply those release-notes steps in the
  clone only and record them.
  Done when: a Note records each step and any doc fix (release notes, consumer guide), or — if the owner prefers the
  game session to run it after the tag — the release notes list it as pending with the exact commands; temp clones
  deleted; `E:\ProjGits\*` untouched; 5180 never bound. (SC-010, FR-092)
- [ ] T079 Final verification (after T076–T078): Debug and Release build of `GameServer.slnx`; full suite in memory and
  with `GAMESERVER_TEST_STORE=File`; the consumer-fixture suites; opt-in `GAMESERVER_PERF=1` run of
  `PendingCommandStorePerformanceTests` and of the 015 performance tests (`ConfigPhasePerformanceTests` and the perf
  sections of `ConfigCompressionTests`, `PersistentConfigCacheTests`; budgets unchanged); WireGen `verify` for the three
  configs; `git diff main -- proto src/*/Protos` shows only the five fields and the new message; the
  `docs/api/gm-openapi.json` diff is additive only; `SdkPublicApiTests` 0 removed or changed; `git status` lists only
  intended files (no probe, proxy, scratch output, Unity `Library` or `ProjectSettings` change).
  Done when: 0 warnings, 0 failures; counts against T001 and the per-story checkpoints recorded. (FR-092, SC-008,
  SC-009)
- [ ] T080 Couchbase full suite (after T079; run through PowerShell in the background, variables from the resume file,
  password never printed), including `CouchbaseAccountCreationTests`, the typed paths of `CouchbaseRunnerTests` and, as a
  separate opt-in run, `AccountCreationPerformanceTests` (`GAMESERVER_PERF=1`).
  Done when: 0 failures; total/passed/skipped and duration recorded; the SC-003 numbers recorded; a known flaky class is
  rerun alone before reporting. (FR-092, SC-002, SC-003)
- [ ] T081 Tech-lead review (after T079): `tech-lead-reviewer` gets the spec, the plan, this file and the list of changed
  files (`git diff --stat main`); focus: the create-or-update decision inside each attempt and the flag source (T2, T3);
  fresh context per attempt, initializer purity and the view's enforcement; receipt only with effects, request id once
  per call, system actor; every rule before the first staged write (runner and writer) and no size or read failure in
  the `INTERNAL` catch-all; read-failure classification, one report per failure, the marker, no inner exception, no state
  content or game exception message in logs or envelopes (API-04); `SectionWriter` as the only mapping site, never an
  empty payload, the post-commit path; broken profile typed on every path; BC-10 mapping; settings ranges; kit parity;
  SDK overload binding, id validation, store atomicity, corruption handling, path safety, remove/keep rules, no token
  stored, threading doc; additive SDK API and `.meta`; docs accuracy.
  Done when: findings recorded (Blocker/Major/Minor with file:line). (FR-092)
- [ ] T082 Review fixes (after T081): fix every finding the orchestrator accepts, each with a test that fails without the
  fix where practical; update docs, the release notes draft and PCRs touched by a fix; rerun the affected classes, then
  T079 and T080. Append `## Review (T081/T082, …)` to this file in the 015 format (finding, fix, test, docs;
  deviations; test delta).
  Done when: suites green; review section complete. (FR-092)
- [ ] T083 Round release note `docs/releases/v0.7.0.md` as a **draft** (after T082, so the BC list is final; research
  R15, FR-091): new folder `docs/releases/`; header "v0.7.0 — draft, finalized at tag"; merged from
  `specs/013-consumer-readiness/release-notes-draft.md`, `specs/014-wire-contract/release-notes-draft.md`,
  `specs/015-config/release-notes-draft.md` and `specs/016-login-command/release-notes-draft.md`: one list of fixed FG
  ids; one breaking-change table per slice with ids written `0NN/BC-n`; one ordered first-consumer checklist (013 → 016);
  the architect/orchestrator decisions awaiting owner override, per slice, with status; the open manual checks (the
  combined device session 015 T058 + 016 T069, T078 if deferred, the Couchbase re-run before the tag); the
  compatibility notes; a closing section "Round-close step (not part of slice 016)": set the release date, restamp every
  page carrying "chưa phát hành"/"unreleased" (`grep` again at that time), the annotated tag `v0.7.0`, any push — owner.
  Done when: every FG id and every BC id of the four drafts appears (`0NN/BC-n`; grep counts per draft in a Note); no
  date is set, no page is restamped, no tag exists. (FR-091, US6-3)
- [ ] T084 Resume update `AgentMemories/features/p05-speckit/00_summary.md` (after T083): 016 row DONE with the task
  count and review result; "Technical notes from 016" (writer and per-attempt decision, creation receipt and system
  actor, initializer view, read-failure marker and reporter, `SectionWriter`, rules order and the rejection path,
  `CommandResponse.details`, settings ranges, kit parity inputs, the pending-store format and client flow, test counts);
  open items (the combined device session, owner questions 1–6, T078 if deferred, any V fallback); next step: the
  round-close step (orchestrator, after the owner's gates), then the owner.
  Done when: the file names the round-close step and how to resume. (resume DoD)

## Dependencies

```text
Phase 1  T001 ─┬─ T002 (release notes skeleton) ──────────────► T022, T032, T046, T054, T062, T075, T076
               ├─ T003 (V1 GATE, Unity Editor) ───► T055
               ├─ T004 (V6 GATE) ─────────────────► T034
               ├─ T005 (V5 GATE) ─────────────────► T040
               ├─ T006 (V2/V4 GATE) ──────────────► T016, T025, T047
               ├─ T007 (V8, V9 .NET) ─────────────► T057
               ├─ T008 (V11 GATE) ────────────────► T043, T049
               └─ T009 (V12 + inventory) ─────────► T027, T034, T048, T064, T065
Phase 2  T010 ∥ T011 ∥ T012 ∥ T013 ; T010 → T014 ; T011 → T015
US1      T016 (T006,T012,T013) → T017 → T018 → T019 → T020 ∥ T021 → T022
US2      T023 (T011) ∥ T024 (T011) → T025 (T014,T016,T023,T024) → T026 → T027 → T028 ∥ T029 → T031 ;
         T030 (T025) ; T032
US3      T033 (T010) → T034 (T004) → T035 → T037 → T038 → T039 (T019) → T040 (T005,T036) ; T036 (T033) ;
         T041 (T034,T035) → T042 (T037) ; T043 (T008,T035,T041) ; T044 (T038–T043) ; T045 (T010,T012) ; T046
US4      T047 (T006,T013,T014,T041) → T048 → T049 (T042,T043) ; T050 (T023) ; T051 (T030,T048) → T052 ; T053 (T048) ; T054
US5      T056 (T001) → T057 (T007) ; T055 (T003,T045) → T058 (T057) → T059 → T061 ; T060 (T057) ; T062
US6      T063 (T025,T030) → T064 (T052) ; T065 (T030,T051) ; T066 (T011) ; T067 (T059,T060) → T068 → T069 (manual) ;
         T070..T075 (docs, after their stories)
Close    T076 → T077 → T078 (manual) → T079 → T080 (Couchbase, background) → T081 → T082 → T083 → T084
```

- Phase 1 blocks everything; each V-check blocks only the tasks named in the [VERIFY] table. **T003, T004, T005, T006
  and T008 are gates**: the SDK overload name, the classification list, the bearer path, the "rules before staging"
  unwrap and the second read, and the GM OpenAPI shapes follow their decisions.
- **US1 needs only Phase 2.** US2 extends the US1 writer. US3 needs only Phase 2 except T039 (after T019, same
  `LoginCompletion.cs`/`IdentityModule.cs`); with two implementers it can run as a lane beside US2. US4 follows US3
  because both edit `CommandRunner.cs` (T041 before T047). US5's store files (T056, T057, T060) can start right after
  Phase 1; its client tasks wait for T045. US6 waits for the stories it documents or dogfoods.
- Shared files that force order (no `[P]` between these tasks):
  - `src/GameServer.Platform/Commands/CommandRunner.cs`: T013 → T041 → T047 → T048.
  - `src/GameServer.Platform/Commands/CommitRules.cs`: T014 → T048. `CommandModels.cs`: T041 → T048.
  - `src/GameServer.Platform/Commands/AccountWriter.cs`: T016 → T017 → T025 → T026.
  - `src/GameServer.Platform/Diagnostics/PlatformMetrics.cs`: T025 → T033 → T048.
  - `src/GameServer.Platform/Registry/ModuleRegistry*.cs`: T023 → T050.
  - `src/GameServer.Platform.Web/Hosting/GameServerBuilder.cs`: T016 → T034 → T037.
  - `src/GameServer.Modules.Identity.Adapters/LoginCompletion.cs`: T018 → T039; `IdentityModule.cs`: T019 → T039.
  - `src/GameServer.Platform.Web/Commands/CommandEndpointFactory.cs`: T042 → T049; `GmCommandEndpointFactory.cs` and
    `docs/api/gm-openapi.json`: T043 → T049.
  - `src/GameServer.TestKit/CommandContextDouble.cs`, `AccountCreationCheck.cs`,
    `tests/GameServer.Architecture.Tests/TestKitArchitectureTests.cs`: T030 → T051.
  - `tests/GameServer.Observability.Tests/TelemetryTests.cs`: T032 → T046 → T054.
  - `tests/GameServer.Platform.Tests/Commands/CommandRunnerTests.cs`: T041 → T047 → T048.
  - `tests/GameServer.Platform.Web.Tests/AccountPurgeTests.cs`: T028 → T044; `ReceiptParityTests.cs`: T052 → T064.
  - SDK `Runtime/Core/GameServerClient.cs`: T045 → T055 → T058 (partial file T058 → T059); `GameServerError.cs`:
    T010 → T055; `TestData/sdk-public-api.txt`: T001 → T010 → T012 → T045 → T055..T062.
  - `samples/SampleGame/SampleGame/*`, `samples/SampleHost/config/base/*`: T063 only. KestrelDemo files: T067 only.
  - `specs/016-login-command/release-notes-draft.md`: T002 → T022 → T032 → T046 → T054 → T062 → T075 → T076 → T082.
  - Docs: `docs/guides/consumer-guide.md` T070 → T077 → T082; `docs/pcr/platform.md` T072 → T082;
    `docs/releases/v0.7.0.md` T083 only.
- Parallel examples: T002 ∥ T004 ∥ T005 ∥ T006 ∥ T007 ∥ T008 ∥ T009 (T003 needs a free Unity); T010 ∥ T011 ∥ T012 ∥
  T013; T020 ∥ T021; T023 ∥ T024; T028 ∥ T029; T036 beside T034–T035; T053 beside T049–T052; T056 → T057 as an SDK lane
  from Phase 1; T065 ∥ T066; T071 ∥ T072 ∥ T073 ∥ T074.

**Critical path** (one implementer): T001 → T006 → T012 → T016 → T017 → T018 → T019 → T025 → T026 → T027 → T029 →
T032 → T033 → T034 → T035 → T037 → T038 → T039 → T040 → T041 → T042 → T044 → T046 → T047 → T048 → T049 → T054 → T055
→ T058 → T059 → T061 → T062 → T067 → T068 → T070 → T075 → T076 → T077 → T079 → T080 → T081 → T082 → T083 → T084. T069
and T078 are manual (owner-assisted) and off the critical path, but their status must be in the release notes before
T076 closes and in `docs/releases/v0.7.0.md` (T083).

## Implementation strategy

1. **MVP = US1 (Phases 1–3).** One writer for every login, the flag, the analytics fix, typed login store failures;
   shippable at T022.
2. **US2** (initializers, receipt, startup lines, kit call) on the US1 writer, then the Couchbase race.
3. **US3** (typed read failures and degradation) and **US4** (rules before staging, typed size rejection, settings,
   kit parity), in that order because of `CommandRunner.cs`.
4. **US5** (SDK request ids and pending store); its store files can be built as a lane from Phase 1.
5. **US6** (sample, fixture, template, KestrelDemo, Unity runs, docs), then the close (release notes, walkthrough, first
   consumer, full suites incl. Couchbase, review, `v0.7.0.md` draft, resume).

With one implementer run the phases in order. With two: lane 1 = US1 → US2 → US4; lane 2 = US3 (T033–T038, T041–T045;
T039/T040 after T019) then US5; merge before US6.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The create-or-update decision leaks out of the attempt in a later edit (T2) | High (double grants in races) | decision returned from the lambda; T016/T025 forced re-run tests; race tests on three stores (T027, T029) |
| Couchbase wraps a body exception (V2) | High (size or read failure becomes `INTERNAL`) | T006 gate; rules before staging (T025, T047); T053 |
| 20-way creation race exhausts 3 rounds on Couchbase (V3) | Medium (flaky SC-002, `CONCURRENT_UPDATE` on double taps) | T029 records rounds; fallback 5 rounds for logins; the code is retryable |
| The classifier misses an exception type (V6) | Medium (500 instead of a typed code) | T004 gate; catch-all fallback; corruption matrix (T034) |
| A read failure logged twice or not at all | Medium (FR-026) | marker (T033), middleware skip (T036), "exactly one" with `CapturedLog` (T044) |
| Tests pinning today's behaviour turn red in bulk | Medium | T009 inventory; each pin updated in the task that changes it (T034, T048) |
| The real SampleHost config seeds new players in `LocalFlavorFactory` tests | Medium (unexpected red suites after T063) | T009 inventory; T064 runs and fixes them |
| The bearer path swallows or rewraps the typed exception (V5) | Medium (500 or no retry delay) | T005 gate; `Fail` fallback |
| Unity binds the overload differently (V1) | High for games (CS0121 or wrong overload) | T003 gate before T055; T068 with the real package; rename fallback |
| `File.Move`/`EnumerateFiles` differ under IL2CPP (V9) | Medium (store unusable on a device) | T069 device session; single-container fallback |
| Device flush cost on D: (V8) | Low (frame hitch) | T007 per drive; `Flush(false)` fallback documented |
| Unity writes a random-GUID `.meta` into the `file:` package | Medium (GUID churn) | hand-written `.meta` in T056, T058, T060 before T068; `UnityPackageMetaTests`; `git status sdk/unity` |
| A Windows player build changes tracked project files | Low | T068 restores the 015 list and reopens on Android |
| Module rejection details now reach clients (BC-11) | Low | release notes; owner question 2; one-line fallback filter |
| BC ids drift after the review | Low | `v0.7.0.md` written after review fixes (T083) |
| Device session not possible before the tag | Medium (FR-092 manual check open) | T069 pending with exact steps in both release notes; the tag waits |
| zh-pvp tests in the temp clone fail for an earlier slice's BC | Low | apply the 013–015 release-notes steps in the clone only, record |
| Couchbase suite slow or flaky; known flaky telemetry tests | Low | Couchbase only in T080 (background); rerun the flaky class alone |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T012, T016, T018, T019 |
| FR-002 | T018, T019 |
| FR-003 | T039 |
| FR-004 | T020, T070, T074 |
| FR-005 | T073 |
| FR-010 | T011, T024, T025 |
| FR-011 | T021, T025, T027, T028 |
| FR-012 | T009, T016, T025, T027, T029 |
| FR-013 | T006, T024, T025, T026 |
| FR-014 | T017, T026, T027 |
| FR-015 | T023 |
| FR-016 | T025, T027, T032 |
| FR-017 | T030, T063, T065 |
| FR-018 | T011 (descriptor `createDefault` unchanged; nothing to build) |
| FR-020 | T010, T033, T034, T044 |
| FR-021 | T033, T034, T035, T040, T044 |
| FR-022 | T012, T035, T037, T038, T044 |
| FR-023 | T005, T039, T040, T044 |
| FR-024 | T012, T041, T042, T044 |
| FR-025 | T008, T043 |
| FR-026 | T033, T034, T036, T037, T044 |
| FR-027 | T045 |
| FR-028 | T072, T075 |
| FR-030 | T010, T012, T048, T049 |
| FR-031 | T014, T026, T047, T048, T049, T053 |
| FR-032 | T050 |
| FR-033 | T072 |
| FR-034 | T013, T014, T030, T051, T052, T064, T065 |
| FR-040 | T003, T055 |
| FR-041 | T055 |
| FR-042 | T058, T061 |
| FR-043 | T059, T061 |
| FR-044 | T058 |
| FR-045 | T007, T056, T057, T058, T074 |
| FR-046 | T003, T012, T056, T060, T062 |
| FR-047 | T007, T060, T061, T062, T074 |
| FR-050 | T009, T063, T064 |
| FR-051 | T065 |
| FR-052 | T067, T068, T069 |
| FR-060 | T070, T077 |
| FR-061 | T066, T071, T072, T073, T074, T075 |
| FR-062 | T070, T071, T072, T073, T074, T075 |
| FR-090 | T002, T022, T032, T046, T054, T062, T075, T076 |
| FR-091 | T083 (status inputs from T069, T078, T080) |
| FR-092 | T001, T029, T053, T068, T069, T077, T079, T080, T081, T082 |

Every FR of the spec (FR-001..FR-005, FR-010..FR-018, FR-020..FR-028, FR-030..FR-034, FR-040..FR-047, FR-050..FR-052,
FR-060..FR-062, FR-090..FR-092) has at least one task.

| Success criterion | Tasks |
| --- | --- |
| SC-001 | T019 |
| SC-002 | T027 (in-memory, File), T029, T080 (Couchbase) |
| SC-003 | T031, T080 |
| SC-004 | T044 |
| SC-005 | T049, T052, T064, T065 |
| SC-006 | T023, T050 |
| SC-007 | T057, T061 |
| SC-008 | T007, T061, T079 |
| SC-009 | T001, T012, T062, T079 |
| SC-010 | T002, T076, T078 (the removal itself is owned by the game session after the tag) |

Scenarios:
- US1: US1-1, US1-2, US1-3 → T016, T019; US1-4 → T018, T019; US1-5 → T020.
- US2: US2-1, US2-2 → T025, T027; US2-3 → T025, T027, T029; US2-4 → T026, T027; US2-5 → T023; US2-6 → T024, T026;
  US2-7 → T025 (never runs for an existing profile), T070 (guide); US2-8 → T030, T065.
- US3: US3-1 → T038; US3-2 → T038, T039; US3-3, US3-4 → T041, T042; US3-5 → T043; US3-6 → T039, T040; US3-7 → T033,
  T044; US3-8 → T037, T038.
- US4: US4-1, US4-2, US4-3 → T048, T049; US4-4 → T050; US4-5 → T072; US4-6 → T051.
- US5: US5-1 → T055; US5-2 → T058; US5-3 → T058, T059, T061; US5-4 → T059; US5-5 → T057, T061; US5-6 → T058;
  US5-7 → T060.
- US6: US6-1 → T063; US6-2 → T070..T075, T077; US6-3 → T083.

Breaking changes: BC-1 → T048, T049, T054; BC-2 → T050; BC-3 → T038, T039, T072; BC-4 → T040, T041, T043;
BC-5 → T034; BC-6 → T025, T027, T063; BC-7 → T018; BC-8 → T045; BC-9 → T023; BC-10 → T017; BC-11 → T048, T049.

[VERIFY] items: V1 → T003, T055, T068; V2 → T006, T025, T047, T053; V3 → T029; V4 → T006, T016, T025, T028, T029;
V5 → T005, T040; V6 → T004, T034; V7 → T052; V8 → T007, T057, T061; V9 → T007, T057, T068, T069; V10 → T012;
V11 → T008, T043, T049; V12 → T009, T027, T029, T031.

Manual or owner-assisted tasks:
- T003 and T068: agent-run Unity (Editor; T068 also the Windows player and the Android APK build). The owner runs them
  only if an Editor holds the project.
- T069: owner. Android device and iOS build and device, as one session with the pending 015 T058.
- T077: fresh reviewer agent.
- T078: first-consumer check in a temp clone. The owner may defer it to the game session.

## Planning notes (gaps between spec, plan and code, resolved here)

1. **Decision names.** The spec's D1–D15 (Assumptions) and the plan's A1–A12 (architect) and N1–N12 (narrowings) are
   separate numbering spaces. Research numbers the plan traps T1–T11 (= spec "Plan traps" 1–11). D12 and D13 are changed
   by the architect. Plan owner questions 1–6 stay open and are tracked in the release notes (T002, T076, T083). They
   block no task.
2. **Story order instead of the plan's layer order.** The plan runs foundations → read failures → rules/runner → web
   degradation → creation → kit → SDK → dogfood. Here the phases follow story priority (US1 … US6), as requested.
   - To make that possible, the behaviour-neutral prerequisites moved to Phase 2: core codes with the SDK constants
     (T010), the initializer API (T011), proto + WireGen (T012), `ReceiptBuilder` (T013), `CommitViolation` +
     `CheckReceiptSize` (T014) and the shared test support (T015).
   - The writer starts in US1 without initializers (its creation is today's anchor + profile, T016). It gains them in
     US2 (T025).
   - The kit work is split: `ForAccountInitializer`/`CheckAccountCreation` in US2 (FR-017), the receipt check and
     `Seed(…, rev)` in US4 (FR-034).
3. **`CommandRunner.cs` order is inverted.** The plan says "do 3 first, then the replay part of 2". Here the replay
   (T041, US3) comes before the reorder (T047) and the typed rejection (T048). The edits are sequential; T047's tests run
   against the post-T041 file.
4. **`CheckResultSize` changes shape in T048, not in T014.** This way the runner message, the kit line and the
   conformance pin all change in one task. Until then the creation writer uses only `CheckReceiptSize` and the aggregate
   and audit rules, because a creation has an empty result.
5. **Tests and docs that pin today's behaviour** (the plan lists none of them):
   - `AggregateBudgetAndUpgradeTests.Newer_stored_schema_than_code_is_an_error` (INTERNAL);
   - the two `CommandRunnerTests.Oversized_*` tests (`InvalidOperationException`);
   - `CommandContextConformanceTests.The_result_size_check_is_the_runners` (old message);
   - consumer guide line 347;
   - PCR platform §3, §6 and line 1061.

   T009 confirms the list and assigns each item to T034, T048, T070 and T072.

   R13's "the 34 full-game test files keep their expectations" holds only for hosts on `tests/Shared/config`.
   `LocalFlavorFactory<SampleHost Program>` loads `samples/SampleHost/config` (SampleHost `appsettings.json` `ConfigPath =
   "config"`). So `Sample.Counter.Tests/SampleHostSmokeTests`, Couchbase `SampleHostFlavorTests` and the other
   local-flavor tests see seeded new players and one creation receipt each after T063. A first read finds no assertion
   on new-player gold or receipt counts there. T064 runs and fixes whatever T009 finds.

   The SampleHost server `config_version` changes once. The client version and the KestrelDemo versions recorded in the
   docs do not change, because `account.json` is server-only.
6. **Missing test helpers.** The plan's `CapturedLog`, a test initializer module and broken-document seeds do not exist.
   T015 creates them in `tests/Shared`, compiled into the Client, Gm, Observability, Couchbase and Platform.Web test
   projects.
7. **`ExceptionMiddlewareTests.cs` does not exist** (the plan marks it "existing"). T036 creates it.
8. **Correlation id for the creation receipt.** `LoginCompletion.CompleteAsync` is a public member of a public class, so
   adding a required parameter would break source compatibility for any caller. T018 adds a trailing optional `string?
   correlationId = null`. The guest and provider services pass the request's id. The release notes list it as additive.
9. **Fixture initializer.** The plan says "writes a welcome note from `notes.json`", but `notes.json` holds only
   `maxLength`.
   - T065 writes a constant welcome text cut to `maxLength`, so it still reads the file.
   - It touches the hidden `notes` aggregate, not `card`: `FixtureCatalogTests.cs:75` asserts an empty card title after
     login.
   - Every new fixture player now gets one creation receipt. T009 confirms that no fixture test counts receipts.
10. **Docs scope is wider than FR-061.**
    - PCR platform records BC-1..BC-11. FR-061 says BC-1..BC-9 because it was written before BC-10 and BC-11.
    - `docs/pcr/gm.md` is added, as in the plan's docs list (the GM JSON changes).
    - `docs/html/03-deploy.html` is updated where it lists command settings, startup lines or metrics (BC-2 and the new
      metrics), as 015 did (T075).
11. **A creation over a limit.**
    - D15 makes it `INTERNAL`, and data-model §5 counts `command_output_too_large_total` for `identity.create_account`.
      T026 writes one Error line: the creation-rule line, whose `{RuleLine}` is the §5 receipt text. It increments both
      `account_initializer_failures_total{result="rule"}` and `command_output_too_large_total`.
    - A rule that belongs to no single initializer (receipt size) uses the label `initializer="*"`. An undeclared write,
      an audit entry or an oversized aggregate is labelled with the initializer whose view made it.
12. **V1 is split.** The .NET probe was done in planning, and binding is a compile-time fact.
    - T003 gates with a stub, because the overload does not exist yet. T068 confirms with the real package: an Editor
      compile and an Android IL2CPP build. No device run is needed for V1.
    - `OverloadBindingTests` compile in the test project, which uses the default language version, not C# 9. The C# 9
      checks are the Unity compile and the `sdk/GameServer.Client` build (`LangVersion 9.0`).
13. **V9 on devices** is owner-only and rides on the pending 015 T058 session. T069 lists the five combined starts; iOS
    needs macOS.
14. **The GM command response changes in two stories.** `Unavailable` comes in T043 (US3) and `Details` in T049 (US4), so
    `docs/api/gm-openapi.json` is regenerated twice. T049 follows T043.
15. **File sizes and partials.**
    - `GameServerClient.cs` has 642 lines. The pending-store flow goes into a new partial
      `Runtime/Core/GameServerClient.PendingCommands.cs` with its `.meta`. That makes four new SDK files: the three of
      public-api §7 plus this one. Making the class `partial` changes no public API.
    - `CommandContextDouble.cs` has 369 lines. The plan's partial `AccountCreationCheck.cs` holds both kit additions.
16. **Guard tests the plan does not list:**
    - `TelemetryTests` metric-name list (T032, T046, T054);
    - `DocumentedConfigurationKeysTests`, which reads only PCR §13/17/18/19/21, so §22 is added (T072);
    - `CommittedOutputTests` (T012);
    - `OpenApiTests` (T043, T049);
    - `SdkRouteTests`, the literal scan of the KestrelDemo client (T067);
    - `ErrorCodeBandTests` (T010).
17. **Close has nine tasks** (015 had eight). `docs/releases/v0.7.0.md` is written after the review fixes (R15: the BC
    list must be final), so T083 sits between T082 and the resume update. The release date, the page restamp, the tag
    and the push are the round-close step, not tasks.
18. **KestrelDemo.**
    - `RunAsync` already reads no snapshot at boot, so FR-052's "no second snapshot" is checked, not changed.
    - `SmokeBuild.EndToEnd` creates a new device per run (`NewDevice()`), so a resend across two E2E runs would be
      `OtherPlayer`. T067 adds `-e2eDevice <name>` to that Editor script, a second KestrelDemo file beyond the plan.
    - Killing the Editor at the right moment is not reproducible, so T068 and T069 produce the unknown outcome with a
      scratch proxy that drops the battle-end response.
19. **SDK `ErrorCodes` constants in Phase 2** (T010), not in US3 or US4. The change is additive and needs one API
    re-record.
20. **`GameServerError.Client` already exists** as `(code, message, retryable = false)`. The four-parameter overload is
    additive and binds only with four arguments (T055).
21. **V12.** `LoginPerLocalIdPerMinute = 10` is below SC-002's 25 logins of one credential. T027, T029 and T031 raise the
    limits in test settings only.
22. **Initializer names are checked against the pattern only**, as operations are, with no "prefix = module name" rule.
    So `sample.starting_gold` in module `sample.game` is valid.
23. **FR-016 is narrowed by plan N3**: the creation receipt id is not on the wire. T027 reads it from the Information log
    line and the GM receipt list.
24. **SC-010** (removing the six markers) belongs to the game session after the tag. T078 only shows, in a temp clone,
    that each marker can be removed.
25. **US1-5** (an old SDK against this server) is a proto3 property. T020 covers the reverse direction (this SDK against
    an old server). The old-SDK direction is covered by API-03 and the proto diff check of T012 (only new numbers).
26. **Task count.** 84 tasks against the plan's estimate of ~88. The plan counted per layer. Here some per-layer tasks
    merge into story tasks, and six are added: T009 inventory, T015 test support, T031 perf, T064 real-config hosts,
    T069 combined device session and T083 round release note.
