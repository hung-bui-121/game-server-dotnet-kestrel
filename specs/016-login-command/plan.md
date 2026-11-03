# Implementation Plan: Login and command robustness (slice 016 login-command)

**Branch**: `016-login-command` | **Date**: 2026-11-04 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/016-login-command/spec.md`

## Summary

Group D, the last group of the improvement round from the first consumer (zh-pvp-server, framework `v0.6.0`): FG-013,
FG-015, FG-029, FG-016 and FG-022, before tag `v0.7.0`.

- **New player and account creation (FG-013, FG-015).**
  - Every login writes the profile through one new Platform service, `IAccountWriter` (`GameServer.Platform.Commands`).
  - Each transaction attempt decides *create or update* from what it read. A creation runs every module's account
    initializers over a fresh `CommandContext`, applies the command commit rules before staging, and commits anchor +
    profile + seeded aggregates + claims + one receipt (only when the initializers had effects).
  - The returned `Created` is the only source of `LoginResponse.is_new_player` and of the analytics `isNewUser`. This
    also fixes the race where two first logins both report a new user.
- **Broken aggregate (FG-029).**
  - The loader reports a typed `AggregateReadFailure` (version ahead / upgrade failed / unreadable; mapper failed from the
    web layer) once, as one log line and one metric.
  - `Load` throws it as a `GameServerException` with `AGGREGATE_VERSION_AHEAD` (503, retryable) or
    `AGGREGATE_UNREADABLE` (500). Every reader that cannot degrade therefore answers a typed code instead of 500.
  - Snapshot, login, replay, post-commit mapping and the GM view degrade: the section is left out and listed in a new
    additive `unavailable_sections`.
- **Command limits (FG-016).**
  - The five `GameServer:Commands` settings get ranges checked at startup.
  - Result and receipt size become the business rejection `COMMAND_OUTPUT_TOO_LARGE` (HTTP 200, nothing committed). All
    commit rules now run before the first staged write. A new `CommandResponse.details` carries `part`/`bytes`/`limit`.
  - The TestKit double measures the receipt with the runner's own builder.
- **Caller request ids (FG-022).**
  - `SendCommandAsync(route, request, parser, requestId)` (required string; the probe shows no CS0121).
  - `CommandResult.RequestId`.
  - An opt-in `PersistentPendingCommandStore` (one atomic file per command), listing and resend after login, and a Unity
    helper.
- **Dogfood and docs.**
  - The sample game seeds starting gold from an optional server-only `account.json` (present in SampleHost, absent from
    test config, so the test suite is unchanged).
  - The fixture tests an initializer with the kit.
  - KestrelDemo uses the flag and the pending store.
  - Guides, PCRs, HTML pages.
  - The round's release note `docs/releases/v0.7.0.md` (draft until the tag; the release act is a separate round-close
    step).

Decisions: [research.md](research.md) (R1-R17, VERIFY V1-V12); entities, documents and states:
[data-model.md](data-model.md); contracts: [public-api.md](contracts/public-api.md),
[wire-changes.md](contracts/wire-changes.md), [sdk-pending-store.md](contracts/sdk-pending-store.md),
[errors-and-report-lines.md](contracts/errors-and-report-lines.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403, ASP.NET Core 10.0.14); Unity package C# 9 / `netstandard2.1`

**Primary Dependencies**: unchanged. `Google.Protobuf` 3.36.2 / `Grpc.Tools` 2.84.0 (regeneration through WireGen),
Couchbase .NET SDK (pinned), `System.Text.Json`, `System.Security.Cryptography` (SHA-256 in the SDK)

**Storage**: no Couchbase shape change. The creation writes ordinary `pa_*`, `receipts`, claim documents. New on-device
files in the SDK: `{requestId}.pending` (format v1), one per command whose outcome is unknown.

**Testing**:
- xUnit v3 + Shouldly; `TestHost`, `SampleTestHost`, `GameHttp`; Couchbase integration tests (transactions on);
- SDK tests outside Unity; consumer fixture; architecture tests;
- opt-in perf (`GAMESERVER_PERF=1`);
- manual: KestrelDemo (`smoke-build.ps1`) and the reviewer walkthrough.

**Target Platform**: Linux containers; Windows/macOS/Linux dev machines; Unity 6 clients (Editor, Windows, Android
IL2CPP, iOS IL2CPP)

**Project Type**: web-service framework + Unity SDK + consumer test kit

**Performance Goals**:
- first login with a 3-aggregate initializer and one grant ≤ +20 ms p95 on local Couchbase; returning logins unchanged
  (SC-003);
- pending record/remove of 4 KiB ≤ 10 ms p95 (SC-008);
- the snapshot keeps one parallel KV read (PLY-02).

**Constraints**:
- API-03 (additive proto only), ARC-01 (initializer API in Core, no ASP.NET/Protobuf), ARC-06, ARC-08, DAT-04, DAT-09,
  CMD-02..07, ECO-04, ECO-06, API-04, API-05, API-10, API-11, PCR-01, SDK-01..03;
- constitution I (one transaction, writes only through the Platform write paths), II, V, VI, VII;
- the Unity package stays `0.7.0` with additive public API; `MaxTouches` and the claim limit stay constants.

**Scale/Scope**:
- about 30 new public types or members (Core 5, Platform 12, Platform.Web 2, Identity/Gm 4, TestKit 3, SDK 14);
- 5 proto fields + 1 message;
- 3 error codes; 4 metrics;
- 11 breaking changes (BC-1..BC-11), all with a mechanical fix.

## Overview

```text
 Core (business, ARC-01)                Platform                                          Platform.Web / modules
 IAccountInitializer                    Commands/                                         Identity.Adapters
 AccountInitializerRegistration ──────► ├ AccountWriter : IAccountWriter ◄──────────────── LoginCompletion (every login)
 ModuleDescriptor.AccountInitializers   │   attempt: read profile ─ exists → epoch+1          │ IsNewPlayer = Created
 CoreErrorCodes 23/24/25                │            └ absent → anchor+touches, initializers  │ analytics createdPlayer = Created
                                        │              (InitializerContext per initializer),  ▼
                                        │              CommitRules, ReceiptBuilder, stage    IdentityModule LoginResponse
                                        ├ CommandRunner (rules before staging; size →             ─► SectionWriter
                                        │   COMMAND_OUTPUT_TOO_LARGE; replay TryLoad)      CoreEndpoints snapshot ─► SectionWriter
                                        ├ CommitRules (+ CheckReceiptSize) ◄── TestKit     CommandEndpointFactory ─► SectionWriter
                                        └ ReceiptBuilder (shared)            CommandContextDouble   (post-commit mapper failure)
                                        Aggregates/                                       GmPlayerEndpoints (ReadAllAvailable)
                                        ├ AggregateDocumentSerializer.TryLoad/Load          ExceptionMiddleware (no 2nd log)
                                        ├ AggregateReadFailure(+ToException) ─► AggregateReadFailures (1 log + 1 metric)
                                        └ PlayerSnapshotReader.ReadAvailableAsync → PlayerSnapshot(Sections, Unavailable)
                                        Registry/ModuleRegistry: initializer + settings lines (serve, provision, kit)

 Wire: LoginResponse 8 is_new_player, 9 unavailable_sections · SnapshotResponse 5 · CommandResponse 10, 11 details
 SDK:  SendCommandAsync(…, requestId) · CommandResult.RequestId · Options.PendingCommands → PersistentPendingCommandStore
       (one file per command) · PendingCommands / StatusOf / ResendPendingAsync / Discard · SectionStore.Unavailable
```

Style: the modular monolith with explicit registration stays as it is.
- Initializers are declared data plus pure code in the business layer.
- The platform owns the transaction, ordering, commit rules, reporting and the wire degradation.
- The SDK keeps transport concerns and adds one opt-in device store.

## Module breakdown

| Module (assembly / namespace) | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Core `GameServer.Accounts` (`IAccountInitializer`, `AccountInitializerRegistration`) + `ModuleDescriptor.AccountInitializers` | Describe what a module seeds in a new account | `ICommandContext`, `ErrorCode` | when or in which transaction it runs |
| Core `CoreErrorCodes` (23, 24, 25) | Catalog entries | — | where they are raised |
| Platform `Commands.AccountWriter` (+ `InitializerContext`, `AccountCreation`) | The login write of the profile: create-or-update per attempt, initializers, commit rules, receipt, relay, metrics | registry, store, serializer, `CommandContext`, `CommitRules`, `ReceiptBuilder`, relay | profile semantics (epoch, access checks: the `mutate` of Identity), session, sections |
| Platform `Commands.CommandRunner` | Commands as today, with every rule before staging, typed size rejection, replay that tolerates broken aggregates | as today + `ReceiptBuilder` | wire mapping |
| Platform `Commands.CommitRules` + `ReceiptBuilder` (internal) | One definition of commit checks and of the receipt bytes, shared with the kit | `ReceiptDocument` | when checks run |
| Platform `Aggregates.AggregateDocumentSerializer` | Classify read failures (`TryLoad`), throw them typed (`Load`) | `AggregateReadFailures` | degradation policy |
| Platform `Aggregates.AggregateReadFailure` / `AggregateReadFailures` | The failure value, its code and exception form; one log + one metric per failure | `PlatformMetrics`, logging | which reader degrades |
| Platform `Aggregates.PlayerSnapshotReader` | Throwing reads (unchanged signatures) and degrading reads (`ReadAvailableAsync`, `ReadAllAvailableAsync`) | serializer | wire |
| Platform `Registry.ModuleRegistry` + `CommandSettings.Validate` | Startup lines for initializers, the creation budget, the reserved operation and the command settings | descriptors, options | runtime |
| Platform.Web `Aggregates.SectionWriter` | The only place sections/updates are mapped; mapper failure → unavailable entry | mapper registry, `AggregateReadFailures` | reading aggregates |
| Platform.Web endpoints (snapshot, command, GM command), `ExceptionMiddleware` | Carry `unavailable_sections` and `details`; no second log for read failures | `SectionWriter` | failure classification |
| Identity.Adapters `LoginCompletion`, `IdentityModule` | Login flow; flag and unavailable list into `LoginResponse` | `IAccountWriter`, `PlayerSnapshotReader`, `SectionWriter` | the creation transaction |
| Gm.Adapters `GmPlayerEndpoints` | GM view with `unreadable[]` | `ReadAllAvailableAsync` | — |
| TestKit `CommandContextDouble` | Host-free initializer run and receipt-size check with the runner's builder | Platform internals (IVT, allow-listed) | unit-test framework |
| SDK `Runtime/Core` (`GameServerClient`, `PendingCommand*`, `SectionStore`, `CacheFileOps`) | Caller ids, pending store, unavailable marks | BCL | Unity paths |
| SDK `Runtime/Unity/UnityPendingCommandStore` | Folder under `persistentDataPath` | UnityEngine | file format |
| Sample game (`SampleAccountInitializer`, `account.json` rule) | Dogfood the path | Economy | test expectations |

No circular dependency: Core ← Platform ← Platform.Web ← modules' adapters ← TestKit. The SDK depends on nothing
server-side. `AccountWriter` sits in `Platform.Commands`, a namespace the transaction arch test already allows.

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative, one transaction per command | The account creation is one transaction holding anchor, profile, ≤ 3 seeded aggregates, claims and one receipt (DAT-04, CMD-03). Initializers are pure over `ICommandContext` and re-run safely. All `pa_*`/`rcpt::*` writes stay in Platform write paths (arch tests). Ledger entries travel in the receipt outbox (ECO-06) | PASS (strengthened: rules before staging) |
| II Explicit registration | Initializers declared in `ModuleDescriptor`; startup fails for unknown/duplicate/`profile`/over-budget declarations and for the reserved operation; no discovery | PASS |
| III Security deny-by-default | No new route. Read failures send no state content or exception message (API-04). A broken profile cannot be bypassed (login and every authenticated request fail typed). The pending store never holds the token | PASS |
| IV Async, bounded load | Command limits validated with numeric ranges (Appendix B defaults kept). The creation is bounded by the command budget. SDK file I/O bounded per command (O(1)) | PASS |
| V Stable contract | API-03: five additive fields + one message, no meaning change. PCR records the codes, the degradation (§4, §6), limits, the creation transaction. Breaking changes listed (ARC-08) | PASS |
| VI Multi-layer tests | Unit (classification, rules, store format), host (matrices of SC-001/002/004/005/006), Couchbase (creation race, rules before staging), kit, fixture, SDK end to end, opt-in perf, manual Unity | PASS |
| VII Observability | One log line + one metric per read failure; creation, initializer and size metrics; `Unhandled exception.` never reached by a read failure; correlation id in the creation receipt | PASS |

Requirements check (constitution: "do not add requirements contrary to the document"):
- IDN-07 allows "token and snapshot". A degraded snapshot is still a snapshot "with `rev`" for each listed section.
- API-10's "snapshot = every client-visible aggregate" now has the documented exception of an unreadable one. This is a
  semantic PCR change, recorded as BC-3 for owner confirmation (spec Owner confirmations 3).
- IDN-01 ("an absent document is the default state") still holds: initializers are additive.
- API-05 lists 5xx other than the named ones as infrastructure errors. 503/500 for read failures fit that.

Re-check after design: unchanged.

## Design decisions

Spec defaults (D1-D15) and the decisions this plan adds (A1-A12). **Every row marked "architect" is an architect
decision; the owner may override it.** Fallbacks are in research.

| # | Decision | Status | Research |
| --- | --- | --- | --- |
| D1 | Flag = "this login created the player"; `false` after a lost first-login response | confirmed | R1 |
| D2 | Login keeps the full snapshot; no skip/known-revisions option | confirmed | — |
| D3 | Initializers inside the creation transaction | confirmed | R1 |
| D4 | Creation shares the command budget (`profile` + union ≤ 4, claims ≤ 2) | confirmed | R4 |
| D5 | No config-derived `createDefault` | confirmed | — |
| D6 | No initializer for existing accounts (no backfill) | confirmed | R4 |
| D7 | Two codes: `AGGREGATE_VERSION_AHEAD` 503 retryable (1000 ms), `AGGREGATE_UNREADABLE` 500; broken section left out and listed, never empty | confirmed | R5, R7 |
| D8 | `COMMAND_OUTPUT_TOO_LARGE` HTTP 200 business rejection, one code with `part` | confirmed | R8 |
| D9 | Command setting ranges as in FR-032 | confirmed | R9 |
| D10 | No automatic resend | confirmed | R11 |
| D11 | Pending max age 23 h on the server clock | confirmed | R11 |
| D12 | Sample dogfoods the initializer: **changed (architect)** — seeding is driven by an optional server-only `account.json` that SampleHost has and the test config lacks, so test hosts stay unseeded | changed | R13 |
| D13 | Round release note: **changed (architect)** — merged in 016 as a draft; date, page restamp and tag are a round-close step outside the slice | changed | R15 |
| D14 | Pending store opt-in: confirmed; it is set through `GameServerClientOptions.PendingCommands`, not a constructor parameter (architect) | confirmed + shape | R11 |
| D15 | Initializer failure answers `INTERNAL` | confirmed | R1 |
| A1 | **Architect.** One `IAccountWriter.WriteLoginAsync` for every login, in `GameServer.Platform.Commands`; create-or-update decided per attempt; fresh context per attempt; outer race rounds kept (T1, T2) | new | R1 |
| A2 | **Architect.** Flag and analytics `createdPlayer` come only from the committed attempt's `Created` (T3) | new | R1 |
| A3 | **Architect.** Creation receipt: operation `identity.create_account` (reserved), actor System with `ActorId = playerId`, random request id per login (B1 of 010), written only when initializers had effects | new | R2 |
| A4 | **Architect.** Per-initializer `InitializerContext` view over one shared context | new | R3 |
| A5 | **Architect.** Read failure = `GameServerException` with the typed code, no inner exception, marker in `Exception.Data`; `Load`/`ReadAsync` signatures kept; `TryLoad`/`ReadAvailableAsync` added (BC-5 narrowed, BC-7 removed) | new | R5 |
| A6 | **Architect.** `SectionWriter` is the one mapping site; post-commit mapper failure keeps the commit and lists the section (T5) | new | R7 |
| A7 | **Architect.** Every commit rule runs before the first staged write, in the runner and the creation writer; size violations throw the runner's rejection type and are logged/counted outside the attempt (T4) | new | R8 |
| A8 | **Architect.** `CommandResponse.details` (11) carries the details of every HTTP 200 rejection, module ones included (BC-11) | new | R8 |
| A9 | **Architect.** Login store failures map to typed codes (`COMMIT_UNKNOWN_RETRY`, `CONCURRENT_UPDATE`) instead of 500 (BC-10) | new | R1 |
| A10 | **Architect.** SDK overload with a **required** `string? requestId` (probe: no CS0121); `NewRequestId()`; `CommandResult.RequestId` (T7) | new | R10 |
| A11 | **Architect.** Pending store = one file per command (record = temp + flush + rename, remove = delete), reusing `CacheFileOps` + `EnumerateFiles`; damaged entries skipped one by one (T8) | new | R11 |
| A12 | **Architect.** A same-player login keeps the last good copy of sections it lists as unavailable | new | R12 |

## Design per user story

### US1 — The login tells the client the player is new (FR-001..FR-005)

- `LoginCompletion.CompleteAsync`:
  - pre-read and access check unchanged;
  - `accounts.WriteLoginAsync(new AccountLoginWrite<ProfileState>(playerId, mutate, anchor, correlationId))` (the
    same `mutate` lambda as today), then `profiles.Set`;
  - session, then `snapshots.ReadAvailableAsync(playerId, null)`;
  - `analytics.LoggedIn(…, createdPlayer: result.Created)`;
  - `new LoginResult(…) { IsNewPlayer = result.Created, Unavailable = snapshot.Unavailable }`.
- The correlation id comes from the caller (`GuestLoginService`/`ProviderLoginService` already have the `HttpContext`
  through the endpoint). It is passed down; this is not public API.
- `IdentityModule.LoginResponse`: `IsNewPlayer`, then `SectionWriter.WriteSections(…)` for sections and
  `unavailable_sections`.
- The SDK exposes the flag through the generated `LoginResponse.IsNewPlayer`.
- Docs: PCR identity (definition, lost response, relation to `isNewUser`), consumer guide §13.1 ("the login sections are
  the boot read").

### US2 — Starting state in the creation transaction (FR-010..FR-018)

- Core API: public-api §1.1/1.2.
- `AccountWriter`: research R1/R2, data-model §1.2.
  - Registered by `AddGameServer` (singleton, root `IServiceProvider` for `ActivatorUtilities`, like the runner).
  - Uses `GameServerOptions.Transactions.ExpirationSeconds`, and the request token only before the transaction
    (PERF-02).
- `InitializerContext`: research R3.
- Startup: `ModuleRegistry.ValidateAccountInitializers` (errors-and-report-lines §4.1).
- Kit: `CommandContextDouble.ForAccountInitializer` + `CheckAccountCreation` (research R14).
- Economy needs no change: `EconomyOperations.Grant` works over any `ICommandContext`, and the ledger row records the
  System actor's id and the source type the initializer passes.

### US3 — One broken aggregate does not take the player down (FR-020..FR-028)

- Serializer `TryLoad`/`Load` + `AggregateReadFailure` + `AggregateReadFailures` (research R5, R6).
- Readers: `PlayerSnapshotReader.ReadAvailableAsync`/`ReadAllAvailableAsync`.
- Sites:
  - `CoreEndpoints` snapshot → `ReadAvailableAsync` + `SectionWriter`;
  - login → US1;
  - `CommandRunner.ReplayAsync` → `TryLoad`, failures into `CommandExecution.Unavailable`;
  - `CommandEndpointFactory.BuildResponse` → `SectionWriter.WriteUpdates` with `execution.Unavailable`;
  - `GmPlayerEndpoints.LookupAsync` → `ReadAllAvailableAsync`, `unreadable[]`;
  - `ExceptionMiddleware` → skip the Error line for a marked exception.
- Runner execute path: no code beyond the typed exception (it propagates as a `GameServerException`). A test proves
  nothing is staged.
- SDK: `SectionStore` marks, `ApplyLogin` same-player rule, three `ErrorCodes` constants (research R12).
- Docs: PCR platform §4/§6/§8 rewritten where they say "newer than code → 500"; `02-them-command.html` §6b,
  `01-gioi-thieu.html`.

### US4 — Documented limits and typed size rejection (FR-030..FR-034)

- `CoreErrorCodes.CommandOutputTooLarge`.
- `CommitRules.CheckResultSize`/`CheckReceiptSize` → `CommitViolation`.
- `ReceiptBuilder`.
- Runner attempt reordered (research R8).
- `CommandExecution.Details`; `CommandResponse.details`; GM response `details`.
- `CommandSettings.Validate` (research R9).
- PCR platform §22 limits table with the measured worked example (data-model §3.3); `docs/guides/consumer-guide.md`
  §13.4; `GameServer:Commands:*` in the documented-keys list.
- Kit receipt check (research R14).

### US5 — Pending commands survive the app (FR-040..FR-047)

- SDK client and options (public-api §7.1), store (§7.2, sdk-pending-store.md), Unity helper (§7.4), `.meta` files.
- `CacheFileOps.EnumerateFiles`. `AssemblyInfo` IVT unchanged (same test assembly).
- `SdkPublicApiTests` baseline re-recorded at the 016 base before any SDK change (T001).

### US6 — Samples, fixture, docs, release note (FR-050..FR-062, FR-090, FR-091)

- **Sample game:**
  - `samples/SampleGame/SampleGame/SampleAccountInitializer.cs` (touches `wallet`, `ResourceCatalog` by constructor,
    reads `account.json`);
  - rule in `SampleGameConfigRules.cs`; descriptor (`ShopHandler.cs`, `SampleGameModuleDefinition`) declares
    `Optional(account.json, rule)` and the initializer;
  - `samples/SampleHost/config/base/account.json` + manifest `files`.
- **Fixture:**
  - `Fixture.Game/FixtureWelcomeInitializer.cs` (touches the fixture notes aggregate, writes a welcome note from
    `notes.json`);
  - `Fixture.Tests/FixtureInitializerTests.cs` (kit call);
  - `FixtureReceiptSizeTests.cs` (kit receipt check).
- **KestrelDemo** `Assets/SampleGame/SampleGameClient.cs` (opened by exact path):
  - log `new player: {IsNewPlayer}`;
  - no snapshot at boot;
  - `Options.PendingCommands = UnityPendingCommandStore.Create("local")`;
  - after login, list pending → log and resend the resendable ones, discard the others;
  - battle end with `NewRequestId()`.
- **Docs:** see Project Structure. Release note: research R15.

## Where the plan narrows or changes the spec

| # | Spec says | Plan does | Why |
| --- | --- | --- | --- |
| N1 | BC-5: compile error where the snapshot reader's result type changed | Signatures kept; typed `GameServerException` thrown; additive `ReadAvailableAsync`/`TryLoad`. **Spec updated** (BC-5) | Same capability, no compile break; non-degrading callers get typed codes for free |
| N2 | BC-7: `LoginResult` positional record gains values (compile error) | `IsNewPlayer`, `Unavailable` are init properties. **Spec updated** (BC-7 now "none at compile time") | Additive; nothing constructs it outside `LoginCompletion` |
| N3 | FR-016: creation receipt readable through the player's lookup | Readable when its id is known: `ActorId` = player id. The id is in the login log line and the GM receipt list, not on the wire | No wire field for an id the client has no use for |
| N4 | FR-011: "one receipt" | One receipt **when the initializers had effects**; none otherwise. **Spec updated** (FR-011, BC-6) | No empty receipts; games without initializers unchanged |
| N5 | FR-030: details `part`, `bytes`, `limit` | Carried in a new `CommandResponse.details` (11), filled for every HTTP 200 rejection. **Spec updated** (FR-030, BC-11) | A 200 rejection had no details on the wire |
| N6 | FR-045: "a damaged … file gives an empty store with a reason" | Per-entry files: a damaged entry is skipped (and deleted) with a reason; the others load. **Spec updated** (FR-045) | Trap T8 (write cost); never loses valid entries |
| N7 | FR-040: "overload that takes the request id" | `SendCommandAsync(route, request, parser, string? requestId, ct = default)`, `requestId` required, null = generated; `NewRequestId()` | Probe R10: binding unchanged for every existing call shape |
| N8 | FR-042: "a persistent implementation constructed with a folder path; a client uses a store only when the game passes one" | Passed through `GameServerClientOptions.PendingCommands` | A constructor parameter would change or overload the constructor (CS0121 for `new GameServerClient(o, t, null)`) |
| N9 | FR-050/D12: sample seeds a starting state | Seeded from optional `account.json`; test config has none. **Spec updated** (FR-050, D12) | Trap T9: no churn in 34 full-game test files |
| N10 | FR-091/D13: 016 merges the drafts | 016 writes `docs/releases/v0.7.0.md` as a draft; the release act (date, restamp, tag) is a round-close step. **Spec updated** (FR-091, D13) | The act depends on owner gates (T058, Couchbase re-run) outside the slice |
| N11 | FR-014 store failures "the existing retryable codes" | Today the login has none (raw 500). The writer maps them like the runner (BC-10). **Spec updated** (BC-10) | Same codes as commands; the SDK already handles them |
| N12 | FR-027 "a section listed as unavailable keeps its last good copy" | Also across a same-player login; another player's login clears as today | `ApplyLogin` clears the store today; same player = same data |

## Data flow

**First login (guest or provider).**
1. Mapping insert (unchanged, outside the transaction).
2. `CompleteAsync`: pre-read profile (absent) → `WriteLoginAsync`.
3. In the attempt:
   - read profile (absent) → read anchor + touches → initializers → profile from default + `mutate` → rules;
   - stage anchor rewrite, profile insert, seeded aggregates, claims, receipt → commit.
4. Inline relay (ledger projection).
5. `Created = true` → session → `ReadAvailableAsync` → analytics `createdPlayer = true` → `LoginResponse
   { is_new_player = true, sections (seeded), unavailable_sections }`.

**Race.**
- Both logins find no profile.
- Attempt A commits.
- Attempt B: its insert conflicts. The store re-runs the body, or the round loop catches `DocumentExistsException`. The
  next attempt reads the profile → epoch + 1 → `Created = false`.
- One receipt, one ledger entry, two sessions (the second revokes the first: single session, IDN-10).

**Returning login.** `WriteLoginAsync` attempt: read profile (exists) → `mutate` → replace → `Created = false`. Same
transaction as today.

**Snapshot with a broken `wallet`.** `ReadAvailableAsync`: parallel KV read → `TryLoad` per document (wallet fails →
reported) → `SectionWriter` (mappers; a throw → reported, listed) → 200 `{ sections: [others], unavailable_sections:
[wallet] }`.

**Command touching a broken aggregate.** Attempt: bundle read → receipt? (replay path) : `Load` throws typed → store
rethrows → `ExecuteAsync` counts the code → endpoint → `ExceptionMiddleware` writes the envelope (no second log) → 503/500.

**Command over the receipt limit.** Attempt: handler → result size OK → writes computed → audit → receipt built → size
\> limit → `CommandRejectedException(COMMAND_OUTPUT_TOO_LARGE, part=receipt)` before staging → catch: Error line, metric,
`CommandExecution { Details }` → 200 `{ code, details }`.

**SDK resend after restart.**
1. New client with `PersistentPendingCommandStore(folder)` → load entries.
2. Login.
3. `PendingCommands` → `StatusOf` → `ResendPendingAsync` → server replay (committed) or execution (never arrived) →
   `Remove`.

**State ownership.** data-model §6.

## Performance

- **Returning login:** identical transaction (one read, one replace). The extra cost is one `Created` field and the
  `ReadAvailableAsync` bookkeeping (no extra I/O).
- **First login:** + one in-transaction read (anchor + ≤ 3 touches; the anchor read exists today) + ≤ 3 inserts + claims +
  1 receipt insert + inline relay (≈ 2 KV ops after commit) + initializer CPU. Budget SC-003: ≤ +20 ms p95 on local
  Couchbase (opt-in perf test). Hot path allocations: one `CommandContext` and one view per initializer per attempt; the
  initializer instance per attempt (as handlers).
- **Read failures:** the exception is created only on failure. `TryLoad` adds no allocation on the success path.
  `SectionWriter` wraps each mapper call in a `try` (no cost without a throw).
- **Size checks before staging:** the receipt is built once per attempt as today. Only its position moves.
- **SDK:** one small file write + one device flush per command with a store (V8), one delete on the final answer. Without
  a store, no change.

## Seams for later work and extension points

- **Versioned initializers / backfill (deferred, D6).** The registration can gain a version later. A backfill would need
  a per-account marker aggregate and its own command. Nothing in this slice blocks it.
- **Declarative starting balances (deferred).** A framework initializer driven by `resources.json` would be one more
  registration in Economy.
- **Login without sections / known revisions (D2, deferred).** `LoginResponse` stays additive. The SDK's unavailable marks
  and same-player rule are the first step toward a persistent section cache.
- **`MapPlayerQuery` (FG-014, deferred).** Reuses `ReadAvailableAsync` and `SectionWriter`.
- **Export (014).** The catalog export could list initializers (additive member) later.
- **Not designed now:** repair tooling for broken documents, automatic resend, a server-side pending-command API,
  configurable `MaxTouches`.

## Project Structure

### Documentation (this feature)

```text
specs/016-login-command/
├── spec.md                                # updated: plan traps, BC-5/6/7 narrowed, BC-10/11, FR-011/030/045/050/091, D12/D13, owner confirmations
├── plan.md                                # this file
├── research.md                            # R1..R17, VERIFY V1..V12, code facts
├── data-model.md                          # creation, receipt, failures, limits, SDK states, metrics, ownership
├── contracts/public-api.md                # Core, Platform, Platform.Web, Identity, Gm, TestKit, SDK signatures
├── contracts/wire-changes.md              # proto fields, values per response, HTTP status, GM JSON, compatibility
├── contracts/sdk-pending-store.md         # folder layout, container v1, algorithms, client flow, crash table, tests
├── contracts/errors-and-report-lines.md   # codes, log lines, metrics, startup lines, kit lines
├── release-notes-draft.md                 # implementation phase (FR-090)
└── tasks.md                               # next stage (/speckit-tasks)
```

### Source code (files to change)

```text
proto/gameserver/core/v1/core.proto                                    # UnavailableSection; SnapshotResponse 5; CommandResponse 10, 11
src/GameServer.Core/
├── Accounts/AccountInitializers.cs                                    # NEW IAccountInitializer, AccountInitializerRegistration
├── Modules/ModuleDescriptor.cs                                        # + AccountInitializers
├── Errors/CoreErrorCodes.cs                                           # + 23, 24, 25 (in All)
└── Commands/ICommandHandler.cs                                        # XML doc of Details (BC-11)
src/GameServer.Platform/
├── Commands/AccountWriter.cs · AccountCreation.cs · InitializerContext.cs · ReceiptBuilder.cs   # NEW
├── Commands/CommandRunner.cs                                          # rules before staging, typed size rejection, replay TryLoad, ReceiptBuilder
├── Commands/CommitRules.cs                                            # CommitViolation, CheckReceiptSize, CheckResultSize shape
├── Commands/CommandModels.cs                                          # CommandExecution.Details/Unavailable
├── Aggregates/AggregateReadFailure.cs · AggregateReadFailures.cs      # NEW
├── Aggregates/AggregateDocumentSerializer.cs                          # TryLoad, Load throws typed, 3-arg ctor
├── Aggregates/PlayerSnapshotReader.cs                                 # ReadAvailableAsync, ReadAllAvailableAsync, PlayerSnapshot
├── GameServerOptions.cs                                               # CommandSettings.Validate
├── Registry/ModuleRegistry.cs · Registry/ModuleRegistryAccounts.cs (NEW partial)   # initializer + settings lines, reserved op
└── Diagnostics/PlatformMetrics.cs                                     # 4 new metrics
src/GameServer.Platform.Web/
├── Hosting/GameServerBuilder.cs                                       # register AccountWriter, AggregateReadFailures, SectionWriter, serializer ctor
├── Aggregates/SectionWriter.cs                                        # NEW
├── Endpoints/CoreEndpoints.cs                                         # snapshot: ReadAvailableAsync + SectionWriter
├── Commands/CommandEndpointFactory.cs · GmCommandEndpointFactory.cs   # SectionWriter, details, unavailable
└── Middleware/ExceptionMiddleware.cs                                  # no second log for read failures
src/GameServer.Modules.Identity.Adapters/
├── Protos/identity.proto                                              # LoginResponse 8, 9
├── LoginCompletion.cs                                                 # IAccountWriter, flag, Unavailable, analytics
├── IdentityModule.cs                                                  # LoginResponse via SectionWriter + flag
└── GuestLoginService.cs · ProviderLoginService.cs                     # pass correlation id
src/GameServer.Modules.Gm.Adapters/GmPlayerEndpoints.cs                # unreadable[]
src/GameServer.TestKit/CommandContextDouble.cs · AccountCreationCheck.cs (NEW partial, keeps the file < 400 lines)
samples/SampleGame/SampleGame/SampleAccountInitializer.cs (NEW) · SampleGameConfigRules.cs · ShopHandler.cs (descriptor)
samples/SampleHost/config/base/account.json (NEW) · manifest.json
samples/SampleGame/KestrelDemo/Assets/SampleGame/SampleGameClient.cs   # flag, pending store, resend, NewRequestId
templates/module/Template.Module/TemplateModuleDefinition.cs · README.md   # commented initializer example
tests/ConsumerFixture/src/Fixture.Game/FixtureWelcomeInitializer.cs (NEW) · FixtureModuleDefinition.cs
tests/ConsumerFixture/tests/Fixture.Tests/FixtureInitializerTests.cs · FixtureReceiptSizeTests.cs (NEW)
sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs · GameServerClientOptions.cs · GameServerError.cs · SectionStore.cs · CacheFileOps.cs
sdk/unity/com.gameserver.client/Runtime/Core/PendingCommandStore.cs · PendingCommandFormat.cs (+ .meta)   # NEW
sdk/unity/com.gameserver.client/Runtime/Unity/UnityPendingCommandStore.cs (+ .meta)                       # NEW
sdk/unity/com.gameserver.client/Runtime/Generated/gameserver/core/v1/Core.cs · Runtime/Generated/Identity.cs   # regenerated (WireGen)
docs/api/gm-openapi.json                                               # GM view + GM command response fields
```

Tests (new unless marked):

```text
tests/GameServer.Platform.Tests/
├── Aggregates/AggregateReadFailureTests.cs        # V6 corruption matrix, classification, one report per failure
├── Commands/CommitRulesSizeTests.cs               # at limit / +1, violations, order before staging
├── Commands/AccountWriterTests.cs                 # attempt decision, fresh context, rules, receipt-when-effects, anchor, error mapping
├── Registry/AccountInitializerValidationTests.cs  # every line of errors §4.1 + combined report
└── CommandSettingsValidationTests.cs              # every line of errors §4.2
tests/GameServer.Platform.Web.Tests/
├── NewPlayerFlagTests.cs                          # SC-001 matrix
├── AccountCreationTests.cs                        # SC-002 in-memory + File, faults at each step, initializer reject/throw
├── BrokenAggregateMatrixTests.cs                  # SC-004 (4 reasons × 9 readers)
├── CommandOutputLimitTests.cs                     # SC-005 host side, metric, log, retry executes again, GM command
├── ReceiptParityTests.cs                          # SC-005 parity, V7
├── CommandSettingsStartupTests.cs                 # SC-006 combined report
├── ExceptionMiddlewareTests.cs (existing)         # + read failure never "Unhandled exception."
├── IdentityLoginTests.cs · SnapshotTests.cs (existing)   # updated expectations (unavailable list empty, flag)
└── AccountPurgeTests.cs (existing)                # + seeded aggregates and creation receipt purged
tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseAccountCreationTests.cs   # SC-002 on Couchbase, V2, V3, V4
tests/GameServer.Gm.Tests/GmBrokenAggregateTests.cs                    # GM view, GM command typed code
tests/SampleGame.Tests/SampleAccountInitializerTests.cs · ConfigDeclarationParityTests.cs (existing, + account.json) · DocumentedConfigurationKeysTests.cs (existing)
tests/GameServer.Client.Tests/RequestIdTests.cs · PendingCommandFormatTests.cs · PersistentPendingCommandStoreTests.cs · PendingCommandClientTests.cs · PendingCommandEndToEndTests.cs · UnavailableSectionTests.cs · OverloadBindingTests.cs (C# 9 call shapes, V1) · SdkPublicApiTests.cs (baseline)
tests/GameServer.Architecture.Tests/ArchitectureTests.cs · TestKitArchitectureTests.cs · ErrorCodeBandTests.cs · UnityPackageMetaTests.cs (existing, extended: research R16)
tests/GameServer.WireGen.Tests (CommittedOutputTests, existing)
```

Docs:

```text
docs/guides/consumer-guide.md            # §13 new: login flow, initializers, degraded reads, limits, request ids + pending store; §11 bump checklist
docs/guides/module-authoring.md · templates/module/README.md   # initializer registration, budget, purity
docs/pcr/platform.md                     # §2/§3 (creation txn, size rejection), §4, §5, §6, §8 (3 codes), §22 new (limits table, metrics, BC-1..BC-11)
docs/pcr/identity.md                     # flag, creation, initializers, broken profile, login error mapping
docs/pcr/economy.md                      # grants from an initializer (source type, System actor in ledger)
docs/pcr/sample-game.md                  # sample initializer, account.json
docs/pcr/unity-sdk.md                    # request ids, pending store format v1, unavailable sections, new codes
docs/pcr/gm.md                           # unreadable[], details/unavailable in GM command responses
docs/html/01-gioi-thieu.html · 02-them-command.html   # login, defaults, schema version (§6b), limits; stamp "0.7.0 (chưa phát hành)"
specs/016-login-command/release-notes-draft.md        # FR-090
docs/releases/v0.7.0.md                               # FR-091 (draft until the tag)
```

**Structure Decision**: no new project. Core gets one folder (`Accounts`). Platform adds four files to `Commands` and
two to `Aggregates`. Platform.Web adds `SectionWriter`. The SDK adds three runtime files.

## Test strategy (per FR and SC)

| FR / SC | Tests |
| --- | --- |
| FR-001, FR-002, SC-001 | `NewPlayerFlagTests`: new guest, returning guest, new provider identity, returning provider identity, confirmed `switch_account` into a new account, takeover from a second device, lost first-login response (drop response, re-login) → flag per row; captured analytics `isNewUser` equals the flag in every row; two racing first logins → exactly one `true` (T3 regression) |
| FR-003 | `IdentityLoginTests`: login sections = every client-visible section (undamaged) |
| FR-004 | SDK `ClientTests`: `LoginGuestAsync(...).IsNewPlayer`; fake server without field 8 → `false` |
| FR-005, FR-028, FR-033, FR-060, FR-061 | Docs review in the reviewer walkthrough (story 6); `DocumentedConfigurationKeysTests` for the five keys |
| FR-010, FR-013 | `AccountWriterTests`: initializer created per attempt with DI services (catalog); undeclared `Set` → rule line; audit entry → rule line; aggregate over `HardBytes` → rule line; analytics entry allowed |
| FR-011, FR-016 | `AccountCreationTests`: profile + seeded wallet + ledger entry + receipt in one commit (store document count delta); receipt `operation`, `actorType = system`, `actorId = player`; ledger source type; receipt lookup by the player with the logged id; GM receipt list; no receipt when the initializer changed nothing |
| FR-012, SC-002 | `AccountCreationTests` (in-memory, File) and `CouchbaseAccountCreationTests`: 20 concurrent first logins + 5 lost-response retries (raised login limits, V12) → 1 creation receipt, 1 ledger entry, balance = start; existing player → initializer never invoked (counting initializer) |
| FR-014, SC-002 | Faults at each step (`StoreFault.FailBeforeCommit`, `AmbiguousBeforeCommit`, conflict, anchor removed), initializer reject, initializer throw → 0 documents (profile, aggregates, receipt, claims, ledger) and the next login creates everything; error codes per research R1 table; log line and `account_initializer_failures_total` labels |
| FR-015, SC-006, BC-9 | `AccountInitializerValidationTests`: each line of errors §4.1 alone; all of them + one settings line + one unrelated module fault → one report with every line; provision mode reports the same lines |
| FR-017, FR-051 | Kit: `CommandContextDouble.ForAccountInitializer` with the sample and fixture initializers; `CheckAccountCreation` lines equal the writer's; fixture `FixtureInitializerTests` (no host) |
| FR-018 | No test (no change); descriptor API unchanged (`SdkPublicApiTests` is SDK only; Core API review) |
| FR-020, FR-021, FR-026, SC-004 | `AggregateReadFailureTests` (V6 matrix) + `BrokenAggregateMatrixTests`: 4 reasons × {snapshot all, explicit list, login, touching command, reading command, untouched command, replay, GM view, `ReadOneAsync`} → 0 `INTERNAL`; 200 where degrading with other sections byte-identical to an undamaged twin player; typed code + 0 writes for touching/reading; exactly 1 log entry (`CapturedLog`) and 1 metric increment per failed read; epoch + 1 exactly once per successful login; "Unhandled exception." never logged |
| FR-022 | `SnapshotTests`: absent section never sent with an empty payload; every section broken → 200 with empty `sections`; unknown id still 400 |
| FR-023 | `BrokenAggregateMatrixTests`: broken profile → login and a bearer request answer the typed code (503 with retry-after for version ahead, V5) |
| FR-024 | Runner tests: touching/reading command refused before the handler (counting handler = 0 calls); replay lists the broken aggregate; post-commit mapper throw → 200 with result, receipt, `unavailable_sections`, commit stands, retry replays the same |
| FR-025 | `GmBrokenAggregateTests` + `GmOpenApiTests` (V11) |
| FR-027, BC-8 | SDK `UnavailableSectionTests`: mark/keep last copy/clear on later read; event; same-player login keeps copies, other-player login clears; version-ahead envelope retried with same bytes |
| FR-030, FR-031, SC-005 | `CommitRulesSizeTests` (at limit commits, +1 rejects, part/bytes/limit details); `CommandOutputLimitTests`: sample command with a result 1 byte over and a receipt 1 byte over (many ledger entries via a test module), and both exactly at the limit; 0 documents written; `command_output_too_large_total{operation, part}`; Error line; retry with the same id executes again and gets the same code; GM command over the limit → 200 GM response with the code; creation over the receipt limit → `INTERNAL` + rule line; raised `ReceiptMaxBytes` → commits |
| FR-032, SC-006, BC-2 | `CommandSettingsValidationTests` (each bound −1/+1, cross rule) + `CommandSettingsStartupTests` (all in one report) |
| FR-034, SC-005 | `ReceiptParityTests` (V7): host receipt bytes from the store = double's measure for sample battle end, shop buy, fixture `fixture.save`, sample initializer (0 bytes difference) |
| FR-040, FR-041 | SDK `RequestIdTests`: caller id on every attempt (fake transport records headers); `CommandResult.RequestId`; `GameServerError.RequestId` on network error; invalid id → `ArgumentException`, 0 transport calls; `NewRequestId()` format; `OverloadBindingTests` (C# 9 file compiling the five call shapes, V1) |
| FR-042..FR-045, SC-007 | sdk-pending-store §7 list |
| FR-046, SC-009 | SDK project builds C# 9 / netstandard2.1 with 0 warnings; `UnityPackageMetaTests`; `SdkPublicApiTests` (0 removed/changed); proto diff review (only new numbers + new message) |
| FR-047, SC-008 | Opt-in perf `PendingCommandStorePerformanceTests` (V8); XML doc threading rule (review) |
| FR-050 | `SampleAccountInitializerTests`: sample host with `account.json` → balance 100, one ledger entry `account_init`, receipt, flag; without the file → nothing written, no receipt; `account.json` rule lines (bad value, over maxQuantity) |
| FR-052, FR-092 manual | KestrelDemo smoke run: log of the flag, no boot snapshot, pending resend after an app kill (manual checklist in tasks) |
| FR-062 | Stamp check on touched pages (review) |
| FR-090, FR-091 | Review of `release-notes-draft.md` and `docs/releases/v0.7.0.md` against the four drafts (every FG id, every `0NN/BC-n`, ordered checklist) |
| FR-092 | Debug + Release 0 warnings; full suite in-memory, File, Couchbase; WireGen `verify` of all three configs |
| SC-003 | Opt-in `AccountCreationPerformanceTests` on local Couchbase: first login with/without the 3-aggregate initializer, 200 logins each, p95 delta ≤ 20 ms; returning logins compared with `main` |
| SC-010 | Release-note migration checklist + first-consumer walk in a temp clone after the tag (outside the slice; the game session owns it) |

## Breaking changes and migration (feeds the release notes, FR-090)

| BC | What breaks | Steps for a game (zh-pvp) |
| --- | --- | --- |
| BC-1 | Result/receipt over its limit = `COMMAND_OUTPUT_TOO_LARGE` (200) instead of 500 | Treat as a design bug; raise `GameServer:Commands:ReceiptMaxBytes` (P2 chests) or return less. zh-pvp: delete the FG-016 measurement in `AccountInitTests.cs:124` |
| BC-2 | `GameServer:Commands:*` ranges checked at startup | Start each flavor once; zh-pvp uses defaults |
| BC-3 | Snapshot/login may omit an unreadable section and list it (PCR §4, API-10) | Client: treat a missing section as "unavailable" (zh-pvp `PlayerReadModel` already must stop assuming `progress` exists) |
| BC-4 | Commands/replays touching a broken aggregate and a broken profile answer `AGGREGATE_VERSION_AHEAD`/`AGGREGATE_UNREADABLE` instead of `INTERNAL`; PCR §6 changes | Match the new codes; deploy rule unchanged |
| BC-5 | `Load`/`PlayerSnapshotReader` throw `GameServerException` with the typed codes instead of `INTERNAL`/`JsonException`/`InvalidOperationException` (no signature change) | Code catching the old types catches `GameServerException` (or uses `TryLoad`/`ReadAvailableAsync`). zh-pvp: none found |
| BC-6 | A module that registers an initializer with effects gives new accounts seeded aggregates, one `identity.create_account` receipt and ledger entries | zh-pvp: move `account.init` into an initializer; rewrite `AccountInitTests` as "first login seeds once"; reset dev data |
| BC-7 | `LoginResult` gains init properties `IsNewPlayer`, `Unavailable` | None at compile time |
| BC-8 | SDK: unavailable sections keep their last copy and are marked; `AGGREGATE_VERSION_AHEAD` is retried | Read `Sections.Unavailable`; handle the codes |
| BC-9 | Initializer declarations validated at startup | Fix reported lines |
| BC-10 | Login store failures answer `COMMIT_UNKNOWN_RETRY` (503) / `CONCURRENT_UPDATE` (409) instead of 500 | Clients already handle both; dashboards counting login 500s see the codes |
| BC-11 | `CommandResponse.details` (and GM `details`) now carry the details of every HTTP 200 rejection, module codes included; `ICommandOutcome.Details` doc changes | Modules that pass details to a 200 `Reject` send them to the client; make sure they are safe (they already are by contract). zh-pvp passes none |

zh-pvp order after the bump (after the 013-015 steps):
1. bump;
2. move `InitAccountHandler` into `IAccountInitializer` (touches `progress`, `wallet`, `armory`; same grant), remove the
   `account.init` route/command/code or keep it for pre-bump accounts (D6);
3. reset dev data;
4. rewrite the two test files with the kit call;
5. client boot: login → config → apply login sections (drop `RefreshSectionsAsync`, `InitAccountAsync`);
6. P2 commands: `NewRequestId()` + `Options.PendingCommands = UnityPendingCommandStore.Create(env)` at
   `OnlineSession.cs:83`, resend after login;
7. remove the six markers.

## Implementation phases (input for tasks.md)

1. **Foundations and VERIFY (blocking).**
   - V1 (SDK test file with the five call shapes), V5, V6 as spikes/tests.
   - Re-record `sdk-public-api.txt` at the base.
   - Core codes + initializer API.
   - Proto fields + WireGen regenerate/verify for all three configs.
2. **Read failures (US3 core).** `AggregateReadFailure`, reporter, serializer `TryLoad`/`Load`, reader members,
   middleware marker. Unit tests.
3. **Commit rules and runner (US4).** `CommitViolation`, `ReceiptBuilder`, rules before staging, typed size rejection,
   `CommandExecution.Details/Unavailable`, replay `TryLoad`, settings validation. Unit + host tests. V2 on Couchbase.
4. **Web degradation (US3 web).** `SectionWriter`, snapshot, command response, GM command response, GM view, OpenAPI
   (V11). Matrix tests (SC-004).
5. **Account creation (US1 + US2).** `AccountWriter`, `InitializerContext`, registry lines, `LoginCompletion`/`IdentityModule`,
   analytics. SC-001, SC-002 in-memory/File, then Couchbase (V3, V4). Perf opt-in (SC-003).
6. **TestKit (US2/US4).** `ForAccountInitializer`, `CheckAccountCreation`, receipt check in `CheckCommit`, `Seed(…, rev)`,
   allow-list. Parity tests (V7).
7. **SDK (US5 + US3 client).** Request id overload, `CommandResult.RequestId`, errors, store + format + `CacheFileOps`,
   client flow, `SectionStore` marks, Unity helper, `.meta`. SDK tests, perf (V8), C# 9 build.
8. **Dogfood (US6).** Sample initializer + `account.json` + rule, fixture initializer + tests, KestrelDemo (manual, V1
   Unity part, V9 with the device session), template.
9. **Docs and close.** Guides, PCRs, HTML (+ stamp), release-notes draft, `docs/releases/v0.7.0.md` draft, reviewer
   walkthrough, full suite (in-memory, File, Couchbase), tech-lead review, resume file. The round-close step (R15) is
   **not** a slice task.

Dependencies:
- Phase 1 blocks all. 2 → 4.
- 3 needs 1. It can run in parallel with 2, except `CommandRunner.cs`: do 3 first, then the replay part of 2.
- 5 needs 2 and 3. 6 needs 3 and 5. 7 needs 1 and can run in parallel with 2-6. 8 needs 5-7. 9 last.

**Estimated tasks: ~88** (foundations 10, read failures 9, rules/runner 11, web degradation 9, creation 14, kit 6, SDK 15,
dogfood 7, docs/close 7).

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| The create-or-update decision leaks out of the attempt in a later edit (T2) | Double grants in races | Architecture of R1 (decision returned from the lambda); SC-002 race tests on all three stores; a unit test that runs the body twice with a conflict in between |
| Couchbase wraps a body exception differently after staged writes (V2) | A size rejection or read failure becomes `INTERNAL` | Rules run before staging (A7); store adapter unwrap fallback; Couchbase test of each typed path |
| 20-way creation race exhausts 3 rounds on Couchbase (V3) | Flaky SC-002, `CONCURRENT_UPDATE` for real players in a double-tap | Couchbase lambda retries internally first; fallback 5 rounds; the error is retryable and the SDK user retries login |
| Upgraders or mappers throw exceptions the classifier misses (V6) | 500 instead of a typed code | Catch-all except OCE/OOM in the deserialize block and around mappers; matrix test |
| Typed failure logged twice (reporter + middleware) or not at all | FR-026 broken, log noise | Marker in `Exception.Data`; `CapturedLog` assertion "exactly one" in the matrix |
| A broken profile blocks account purge | Deletion SLA missed for that player | Purge job reports the step as failed (existing); PCR platform §16 runbook: repair the profile document, purge retries; listed in release notes |
| Module details now sent for 200 rejections (BC-11) | Information a module did not expect to send | `Reject(code, details)` has always defined details as safe; release note; fallback filter to framework codes only (owner choice) |
| Pending-store flush cost on slow disks (V8, D: in 015) | Frame hitch on the main thread for a command | O(1) per-entry writes; fallback `Flush(false)`; perf test on C: and D: |
| `Directory.EnumerateFiles`/`File.Move` behave differently under IL2CPP (V9) | Store unusable on a device | Device run with the 015 T058 session; fallback single container (015 shape) |
| Sample `account.json` changes SampleHost's server config version | A test pinning the value fails | No test pins it (checked: comparisons use `IGameConfig`); grep at implementation time |
| Unity compiler binds the overload differently (V1) | Compile error or wrong overload in games | Probe in KestrelDemo before merge; rename fallback |
| `CommandContextDouble.cs` grows past maintainable size (≈ 370 lines today) | Review cost | Split the initializer part into `AccountCreationCheck.cs` (partial) |
| Release note drafted before the 016 review changes BC ids | Wrong ids in `v0.7.0.md` | Write it as the last docs task after the review fixes (R15) |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| `IAccountWriter` used for every login (create-or-update per attempt) | T2: the creation decision must belong to the attempt; one method serves both outcomes of a race | Branch on the pre-read (wrong in races); reuse `CommandRunner` (cannot express insert-if-absent + no receipt for returning logins) |
| `InitializerContext` view | FR-013 per-initializer declared writes, with shared visibility between initializers | Union-only enforcement (silent cross-module writes); one context per initializer (duplicate buffering) |
| `AggregateReadFailure` + reporter + marker | One log + one metric per failure, typed code at every reader, no double logging, through a sealed exception type | New exception type (would fall into `INTERNAL` wherever not handled); logging at each catch site (duplicates) |
| `SectionWriter` | One mapping rule for three sites, including the post-commit one | `TryToPayload` at each site (three places to remember reporting) |
| `ReceiptBuilder` extracted | Byte parity runner ↔ kit (SC-005) | Re-implementing the receipt in the kit (drifts) |
| `CommandResponse.details` | FR-030 details for a 200 rejection | Log-only details (client cannot tell result from receipt) |
| Per-entry pending files + `EnumerateFiles` seam | T8 write cost O(1), atomic per entry | Single container rewritten twice per command |
| Options property for the store | Additive without constructor overload ambiguity | Constructor parameter (signature change or CS0121) |

## Owner questions (also in the release notes, "decisions awaiting owner override")

1. **A3 / N4**: creation receipt only when initializers had effects (none for a game without initializers). OK, or always
   one receipt per new account?
2. **A8 / BC-11**: `CommandResponse.details` carries module rejection details too. OK, or framework codes only?
3. **D12 changed (research R13)**: sample seeding driven by an optional `account.json` (SampleHost seeded, test hosts not). OK?
4. **D13 changed / R15**: 016 writes `docs/releases/v0.7.0.md` as a draft; the date, the page restamp and the tag are a
   separate round-close step after T058 and the Couchbase re-run. OK?
5. **A9 / BC-10**: login store failures answer the command codes instead of 500. OK?
6. **A12**: a same-player re-login keeps the last good copy of unavailable sections. OK?
7. Spec confirmations still open from the spec (D2, D4/D6, D7/BC-3/BC-4, D8/BC-1): this plan confirms all of them.
