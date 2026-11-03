# Research — 016 login-command

Decisions for [spec.md](spec.md). Requirement IDs refer to `Requirements/Game_Server_System_Requirements_v1.2.md`.
Signatures are contracts for the implementer; bodies are deliberately absent. Items marked **[VERIFY Vn]** are runtime or
platform facts the implementer confirms on the pinned versions (.NET SDK 10.0.403, ASP.NET Core 10.0.14,
`Google.Protobuf` 3.36.2, Couchbase .NET SDK as pinned, Unity 6000.3.22f1) **before** coding against them. Each has a
fallback. The list is at the end. Decisions marked **architect decision, owner may override** are listed again in
[plan.md](plan.md) "Design decisions" and in the release notes draft.

The spec has no "Traps for the plan" section on disk. The brief that ordered this plan numbered eleven traps; this file
uses that numbering (T1..T11) and the spec now carries the list (spec "Plan traps").

## Code facts this research relies on (verified 2026-11-04 on `016-login-command`, base `5904b74`)

- **Login** (`src/GameServer.Modules.Identity.Adapters/LoginCompletion.cs`):
  - `CompleteAsync` reads the profile with `ProfileCache.ReadFreshAsync` (line 85), checks access, then calls
    `IPlayerAggregateWriter.UpdateAsync<ProfileState>(…, creationAnchor: identities/{mappingKey})` (110), creates the
    session (96), reads `PlayerSnapshotReader.ReadAsync(playerId, null)` (97), and records analytics with
    `createdPlayer: profile is null` (102).
  - `LoginResult` is a positional record `(Token, PlayerId, ExpiresAt, Sections)`, built only at line 103 (no test
    constructs it).
  - Store transaction exceptions (`TransactionCommitAmbiguousException`, `…ConflictExpired…`, `…Failed…`) are not mapped:
    they reach `ExceptionMiddleware` as "Unhandled exception." (500).
- **Aggregate writer** (`src/GameServer.Platform/Aggregates/PlayerAggregateWriter.cs`):
  - One aggregate, one transaction, no receipt.
  - Outer race loop: up to 3 rounds on `DocumentExistsException`/`StaleReadException` (74-85).
  - Inside the attempt: bundle read of the aggregate, `mutate`, anchor rewrite only when the document is absent, insert
    or replace.
- **Transaction stores**:
  - `ITransactionalDocumentStore.RunTransactionAsync` re-runs the body on conflicts and rethrows body exceptions
    unchanged.
  - Couchbase (`CouchbaseDocumentStore.cs:33-73`) captures the body exception and rethrows it when it is not an SDK
    exception.
  - In-memory (`InMemoryDocumentStore.cs:151-195`) re-runs the whole body when the optimistic commit conflicts (an insert
    whose key appeared meanwhile, a replace of a changed version), and throws `DocumentExistsException` from `InsertAsync`
    when the key is already committed.
- **Command runner** (`src/GameServer.Platform/Commands/CommandRunner.cs`):
  - Attempt order (263-369): bundle read (aggregates + receipt) → replay if a receipt is in the window → `serializer.Load`
    per aggregate → handler → code check → **result size → `InvalidOperationException` (306-309)** → per touch: change
    detection, document build, `AGGREGATE_TOO_LARGE` rejection → **stage** inserts/replaces → claims → audit check →
    build receipt → **receipt size → `InvalidOperationException` (352-356)** → insert receipt.
  - Both size failures are therefore thrown *after* staging and surface as 500 `INTERNAL`
    (`ExecuteAsync` catch-all line 50, `ExceptionMiddleware` line 53).
  - `CommandRejectedException` (private) carries code + details. An HTTP 200 rejection becomes
    `CommandExecution(code, null, [], [], false, …)`: details are dropped.
  - `ReplayAsync` (431-472) loads the current state of every client-visible aggregate of the receipt with
    `serializer.Load` (throws for a broken one).
  - `BuildReceipt` (389-426) is private.
- **Commit rules** (`CommitRules.cs`) are internal and shared with the TestKit (`CheckCode`, `CheckAudit`,
  `ChangesState`, `ExceedsHardBytes`, `CheckAggregateSize`, `CheckResultSize`). There is no receipt-size rule.
- **TestKit double** (`src/GameServer.TestKit/CommandContextDouble.cs`):
  - Wraps the runner's `CommandContext` over `DetachedTransaction`.
  - Receipt id = `ReceiptIds.Compute(actorId, playerId, operation, requestId)`; default request id
    `00000000-0000-4000-8000-000000000001`, actors `test`/`test`.
  - Checks code, result size (only with an encoder), aggregate sizes and audit. **No receipt size** (doc comment
    264-265).
- **Serializer** (`AggregateDocumentSerializer.Load`, 70-118) throws:
  - `GameServerException(INTERNAL)` for `schemaVersion` newer than code (83-88);
  - whatever an upgrader throws (93-94);
  - `JsonException`/`NotSupportedException`/`InvalidOperationException` from STJ and `GetValue<string>` (103-117).
- **Readers that call `Load`**:
  - `PlayerSnapshotReader.ReadAsync/ReadAllAsync/ReadOneAsync`;
  - runner execute and replay;
  - `PlayerAggregateWriter`;
  - `AccountPurgeWriter.cs:79`;
  - indirectly `ProfileCache` (bearer authentication, login, provider login `CurrentPlayerAsync`), `GmTargetValidator`
    and the GM player view (`GmPlayerEndpoints.LookupAsync`).
- **Wire mapping**: `AggregateWireMapperRegistry.ToPayload` is called in three places: snapshot
  (`CoreEndpoints.cs:51-56`), login (`IdentityModule.cs:307-312`) and the command response
  (`CommandEndpointFactory.BuildResponse`). The command call runs after the commit. GM responses use state JSON, not
  mappers.
- **`GameServerException`** is a sealed Core class with `Code`, `Detail` (logs only), `RetryAfterMs`, `Details`.
  `ExceptionMiddleware` logs every `GameServerException` with status ≥ 500 at Error *with the exception*, plus its
  `Detail`.
- **Bearer authentication** (`PlayerBearerAuthenticationHandler.cs:50`) calls `ProfileCache.GetAsync` and catches
  nothing. `ProfileEnforcementMiddleware` writes the reported code with details but no retry delay (line 109-111).
- **Protos**:
  - `LoginResponse` fields 1-7;
  - `SnapshotResponse` 1-4;
  - `CommandResponse` 1-9, no `details`. The XML doc of `ICommandOutcome.Details` says business codes are "not sent";
  - `ErrorEnvelope` 1-10 with `details`.
- **Core error codes** 0-22 used (`CoreErrorCodes.cs`), range 0-999.
- **Command settings** (`GameServerOptions.cs:146-159`) bound by `GetSettings<GameServerOptions>`
  (`GameServerBuilder.cs:162`, binder errors → exit 1 since 011). No range checks. `MaxAggregatesPerGame` is read by
  `ModuleRegistry.ValidateAggregates`.
- **Receipt lookup** (`CoreEndpoints` Receipt route) matches `receipt.ActorId == actor.ActorId`.
  `ReceiptIds.Compute` hashes `actorId|playerId|operation|requestId`. The purge rewrites receipts in place and keeps
  their keys (010 B1: a receipt key computable from the player id would re-link the anonymous id).
- **Architecture guards**:
  - `ArchitectureTests.Inside_platform_only_the_transactional_write_paths_use_transaction_contexts` allows
    `GameServer.Platform.{Commands,Aggregates,Persistence,Privacy}`.
  - `Only_platform_writes_through_transactions` forbids every other assembly.
  - `TestKitArchitectureTests.AllowedInternalTypes/Members` lists the Platform internals the kit may use.
  - `SdkPublicApiTests` compares against `TestData/sdk-public-api.txt`.
  - `UnityPackageMetaTests` requires a `.meta` for every package file.
- **SDK** (`sdk/unity/com.gameserver.client/Runtime/Core/GameServerClient.cs`):
  - `SendCommandAsync<TResult>(string route, IMessage request, MessageParser<TResult> resultParser, CancellationToken
    cancellationToken = default)` (308).
  - The request id is `Guid.NewGuid()` inside `SendAsync` (419). Client-made errors carry no request id.
  - `ApplyLogin` clears `Sections` at every login.
  - The constructor has six optional parameters after `(options, transport)`.
  - `CacheFileOps` (internal, IVT to `GameServer.Client.Tests`) offers create-directory, exists, read-all, open-temp,
    write, flush (`Flush(true)`), replace, move and delete.
- **Test hosts**:
  - `SampleTestHost` default modules are Identity + Sample.Counter, with config from `tests/Shared/config` (label
    `test-1`, no sample-game-only file beyond battle/shop).
  - 34 test files start the full sample game (`GameHttp.FullGame`/`StartGameAsync`, `GmTestHost`, `SdkHarness`,
    `CouchbaseGameHost`, `ObservedHost`).
  - At least `BattleTests` (3 balance asserts), `GmAdjustTests` (5), `ShopTests`, `IapAnalyticsTests` (ledger and
    payment counts), `AccountPurgeTests` (receipts per player) and the SDK harness tests assert a new player's balances,
    revisions, receipt or ledger counts.
- **First consumer** (`E:\ProjGits\zh-pvp-server` @ `7911f20`, read-only):
  - `InitAccountHandler` touches `progress`, `wallet`, `armory` (+ `profile` = 4, exactly the budget) and makes one
    `EconomyOperations.Grant` (source `account_init`).
  - Constructor dependencies `GameCatalog` (declared catalog, 015) and `ResourceCatalog`. Empty result.
  - Its receipt as an initializer ≈ 894 B base + 278 B per ledger entry (friction log FG-016) ≈ 1.2 KiB < 4 KiB.

## R1 — Account creation runs in one Platform transaction for every login (FR-010..FR-016, D3; traps T1, T2, T3) — **architect decision, owner may override**

**Decision.** A new public Platform service `IAccountWriter` (`GameServer.Platform.Commands`, implementation
`AccountWriter`) replaces the login's `IPlayerAggregateWriter.UpdateAsync` call. `LoginCompletion` calls it for **every**
login. The decision "create or update" is taken **inside each transaction attempt**:

1. Bundle-read `pa_profile/{playerId}` and load it. A load failure throws the typed read failure (R5): login is refused.
2. **Profile present** (returning player, or the loser of a creation race): apply the identity module's `mutate`
   (epoch + 1, identity key, late access check). Replace when changed. Return `Created = false`. No initializer, no
   receipt. This is the same work as today's writer.
3. **Profile absent**:
   - Second read in the same attempt: the creation anchor `identities/{mappingKey}` plus every initializer touch.
   - Anchor missing or owned by another player → `AggregateAnchorMissingException` (unchanged, → `ACCOUNT_DELETED`).
   - Load the touches; a load failure throws the typed failure.
   - Build **one fresh** `CommandContext` for this attempt (system actor, R2) over the union of initializer touches.
   - Run every initializer in module-registration order, each through its own `InitializerContext` view (R3).
   - Apply `mutate` to `profile.CreateDefault()`.
   - Run the commit rules (R7) **before staging anything**.
   - Stage: anchor rewrite, profile insert, changed aggregates, claims, and the receipt when the creation has effects
     (R2).
   - Return `Created = true`.
4. The outer race loop of today's writer stays: up to 3 rounds on `DocumentExistsException`/`StaleReadException`. A
   round that finds the profile is a normal login. When the rounds run out the login fails with `CONCURRENT_UPDATE`
   (retryable, 200 ms) instead of a raw exception.
5. After the commit, only when created:
   - inline outbox relay (as the runner does);
   - metrics `account_creations_total{result="created"}` and the aggregate/receipt metrics;
   - one Information log line "Account {PlayerId} created: {n} initializers, receipt {ReceiptId|none}".

`AccountWriteResult<TProfile>(Profile, Rev, Created, ReceiptId?)` is the **only** source of the new-player flag
(FR-001), of `LoginResult.IsNewPlayer` and of the analytics `createdPlayer` argument. This fixes trap T3: today two racing
first logins both report `isNewUser = true`, because the flag comes from the pre-read.

**Exceptions → login answer.**

| Source | Answer |
| --- | --- |
| initializer returned a code | `INTERNAL` (D15); Error log; `account_initializer_failures_total{initializer, result="rejected"}` |
| initializer threw | `INTERNAL`; Error log with the exception type, without the message; `…{result="threw"}` |
| commit rule violated (undeclared write, audit entry, aggregate or receipt size) | `INTERNAL`; Error log with the rule line; `…{result="rule"}` |
| typed read failure (R5) | its code (`AGGREGATE_VERSION_AHEAD` 503 / `AGGREGATE_UNREADABLE` 500) |
| `AggregateAnchorMissingException` | `ACCOUNT_DELETED` (unchanged) |
| `TransactionCommitAmbiguousException` | `COMMIT_UNKNOWN_RETRY` (503, 1000 ms) |
| `TransactionConflictExpiredException`, race rounds exhausted | `CONCURRENT_UPDATE` (409, 200 ms) |
| `TransactionFailedException` | `INTERNAL` (as the runner) |

The last three rows also change the returning-player login (today 500). This is listed as BC-10. The SDK never retries a
login after an unknown outcome (`RetryMode.RejectedOnly`), so a client sees an error code it already handles.

**Why here (T1).**
- Constitution I: every write to `pa_*`/`rcpt::*` goes through the transaction runner.
- The arch test confines `ITransactionContext` to `GameServer.Platform.{Commands,Aggregates,Persistence,Privacy}`.
- The creation is a receipt-writing, outbox-carrying, commit-rule-checked transaction: command-runner territory. It
  therefore lives in `GameServer.Platform.Commands`, next to `CommandRunner`, and reuses `CommandContext`, `CommitRules`
  and the receipt builder (R7).
- The Identity module keeps what it owns (`ProfileState`, the `mutate` lambda, access checks, the mapping key). It still
  opens no transaction.

**Why one method for every login (T2).** The store re-runs the body on conflicts (both stores). The writer loop re-runs
the whole attempt on insert races. A "created" decision taken outside the attempt (from the pre-read, or from a variable
captured by an earlier attempt) is wrong in exactly the race the spec cares about (SC-002). Taking the decision from what
*this* attempt read, and returning it from the lambda, is the only rule that holds for:
- the Couchbase lambda retries;
- the in-memory re-runs;
- the outer rounds.

The fresh `CommandContext` per attempt means no outbox entry, claim or staged state survives a discarded attempt.

**Why the second read.** A returning player (the common case) keeps exactly today's transaction: one document read, one
replace (SC-003 "later logins unchanged"). Only a first login reads the anchor and the touches. Sequential reads inside
one attempt are allowed (V23 of 001: "call them sequentially"). **[VERIFY V4]**

**Alternatives.**
- (a) Run creation as a framework command through `CommandRunner` (rejected):
  - the runner cannot express "insert the profile only if absent, else epoch + 1 without a receipt";
  - it would write a receipt for every login;
  - it has no anchor rewrite;
  - it would need a non-player actor that `EnsureActorAllowed` refuses.
- (b) Keep `writer.UpdateAsync` for returning players and branch on the pre-read (rejected): the branch is outside the
  attempt (T2). A race loser would run the creation path, find the profile and need the update path anyway, so the
  single method is simpler *and* correct.
- (c) Extend `IPlayerAggregateWriter` (rejected): its contract is "one aggregate, no receipt". A creation writes up to
  four aggregates, claims and a receipt.
- (d) Run the initializers in a second transaction after the creation, keyed by a receipt (rejected by spec D3): a window
  where the account exists unseeded, plus a permanent marker.

**Trade-offs.**
- A returning player's login error mapping changes (BC-10, an improvement).
- The creation transaction holds up to 4 aggregates + anchor + claims + receipt: the largest transaction the framework
  writes. It stays within DAT-04 because the startup budget (R4) caps it like a command.

[VERIFY V3, V4, V5]

## R2 — Creation receipt, actor and request id (FR-011, FR-016) — **architect decision, owner may override**

**Decision.**
- **Operation** `identity.create_account` (spec default), a Platform constant `AccountCreation.Operation`. A module
  command with that operation fails startup (one line, R4).
- **Actor**: `ActorContext(ActorId = playerId, ActorType = System, PlayerId = playerId, GameId, EnvironmentId,
  Permissions = ∅, CorrelationId = login correlation id)`.
  - `ActorId = playerId` keeps the creation receipt readable through the player's receipt lookup (CMD-09 matches
    `ActorId`).
  - `ActorType = System` records "framework, not a player request" in the receipt (`actorType = "system"`) and in the
    ledger rows.
- **Request id**: a new UUID (`D`) generated **once per `IAccountWriter` call, outside the attempt**, so every attempt and
  round of one login has the same receipt id and the same deterministic `NewId`/`NewSeed`. The receipt key is then not
  computable from the player id, which keeps the 010 B1 property (an anonymised receipt cannot be re-linked).
  - At most one creation per player is guaranteed by the profile insert, not by the receipt key.
- **Body hash**: SHA-256 of the empty body (constant). `resultCode = OK`, `resultCompact = ""`.
- **Receipt only when the creation has effects.** The receipt is written when at least one initializer changed an
  aggregate, reserved a claim or added an outbox entry. A creation without initializers, or whose initializers changed
  nothing, writes the profile alone, exactly as today. Consequences:
  - games without initializers and the framework test hosts keep their receipt counts;
  - BC-6 applies only to games whose initializers write something;
  - the creation metric still counts every creation.

**Rationale.**
- A receipt is the idempotency entry and the outbox carrier (CMD-03). A creation is never replayed (the profile decides),
  so its receipt is needed only to carry the outbox and to document what was granted.
- An empty receipt is storage and purge work with no reader.

**Alternatives.**
- Deterministic request id `"create_account"` (rejected: re-links purged receipts, B1 of 010).
- The login's `X-Request-Id` as request id (rejected: any string is accepted for logins, and a client retry with the
  same header after a lost response would give two creations of *different* players the same id format; a server UUID
  is simpler).
- Always write a receipt (rejected: test churn and storage for nothing).
- `ActorType = Player` (rejected: a creation is not a player request; GM receipt lists should show the difference).

**Trade-offs.** The player's client does not learn the creation receipt id (no wire field). It is in the login log line
and in the GM receipt list of the player. FR-016 is narrowed accordingly (plan N3).

## R3 — Initializer API and per-initializer scope (FR-010, FR-013, FR-015, FR-017)

**Decision.** Core (business layer, ARC-01) gains:
- `IAccountInitializer.InitializeAsync(ICommandContext) → ValueTask<ErrorCode?>`; null = OK.
- `AccountInitializerRegistration.Of<TInitializer>(name, touches, maxClaimMarkers = 0)`.
- `ModuleDescriptor.AccountInitializers`.

The initializer is created per attempt with `ActivatorUtilities.CreateInstance(rootServices, type)`, exactly as handlers
(`CommandRunner.cs:296`): catalogs and options by constructor.

All initializers of one creation share **one** `CommandContext` (union of touches, claim limit = sum). Each is called
through an `InitializerContext : ICommandContext` view (Platform, internal). The view:
- refuses `Get`/`Set` of an aggregate outside *this* initializer's touches, with the runner's DAT-09/DAT-11 message
  naming the initializer;
- counts this initializer's successful claims against its own `maxClaimMarkers`;
- forwards everything else.

Two initializers that declare the same aggregate see each other's writes in order (spec edge case), because the
underlying context is shared.

**Rationale.** FR-013 ("writes only to declared aggregates") is per initializer. The shared context gives order and
visibility for free. The view is about 40 lines and reuses the runner's messages.

**Alternatives.**
- One context per initializer chained by copying staged state (rejected: duplicates the context's buffering).
- Enforce only the union (rejected: an initializer could write another module's aggregate unnoticed).
- A `CommandOutcome`-like result type (rejected: the result is only OK/code; the code is logged, never sent).

## R4 — Startup checks for initializers and the 4-aggregate budget (FR-015, D4, D6; BC-9) — **D4/D6 confirmed**

**Decision.** `ModuleRegistry.ValidateAccountInitializers` runs inside `ModuleRegistry.Validate`. It is therefore part of
the aggregated report in serve **and** provision mode and in `ConfigDeclarationCheck`'s registry path. One line per fault
([errors-and-report-lines.md §4](contracts/errors-and-report-lines.md#4-startup-report-lines)):
- invalid name;
- duplicate name across modules;
- unknown touched aggregate;
- duplicate touch;
- `profile` touched;
- `profile` + union of touches > `CommandDescriptor.MaxTouches`;
- sum of `maxClaimMarkers` > `MaxClaimMarkersLimit`;
- a module command named `identity.create_account`.

**D4 confirmed: the creation shares the command budget.**
- DAT-04/DAT-09 bound every state-changing transaction. The creation is one.
- The first consumer fits exactly (`profile` + `progress`, `wallet`, `armory`).
- A larger budget would need a different transaction class with its own Couchbase cost analysis. Out of scope with
  `MaxTouches` constant (spec Out of Scope).

**D6 confirmed: no backfill.**
- A backfill would need a per-account marker and would double-grant accounts that a game command already initialized:
  the first consumer's `account.init` sets `InitVersion` and grants CASH.
- The guide documents the two ways out (keep the game command, or reset pre-launch data).

## R5 — Typed read failures (FR-020, FR-021, FR-023, FR-026; D7; traps T4, T5, T6) — **D7 confirmed, architect decision on the shape**

**Decision.**
- **Codes (D7 confirmed).**
  - `AGGREGATE_VERSION_AHEAD` = 24, HTTP 503, retryable, retry-after 1000 ms;
  - `AGGREGATE_UNREADABLE` = 25, HTTP 500, not retryable;
  - (and `COMMAND_OUTPUT_TOO_LARGE` = 23, R8).
  - Details: `aggregate` = aggregate id. Nothing else reaches the client (API-04).
- **Failure value.** `AggregateReadFailure(AggregateId, PlayerId, Reason, ExceptionType, StoredSchemaVersion,
  CodeSchemaVersion)` with `Reason ∈ {VersionAhead, UpgradeFailed, Unreadable, MapperFailed}` (wire/log names
  `version_ahead`, `upgrade_failed`, `unreadable`, `mapper_failed`) and `Code` derived from the reason.
- **Loader.** `AggregateDocumentSerializer.TryLoad(descriptor, playerId, document, out LoadedAggregate?, out
  AggregateReadFailure?)` classifies:
  - newer version;
  - any exception of the upgrade chain (including a missing upgrader);
  - any exception of state deserialization and envelope parsing (STJ `JsonException`, `NotSupportedException`,
    `InvalidOperationException` of `GetValue`, null state; **[VERIFY V6]**).
  - It never catches `OperationCanceledException` or `OutOfMemoryException`.
  - It reports each failure once (log + metric, R6) and returns false.
- **`Load` keeps its signature** and throws `failure.ToException()`: a `GameServerException(code, safeDetail) { Details =
  {aggregate}, RetryAfterMs }`.
  - The exception has **no inner exception** (an upgrader's message may contain state content).
  - It carries a marker in `Exception.Data` (the 015 pattern), because `GameServerException` is sealed.
- **No compile break.** `PlayerSnapshotReader.ReadAsync/ReadAllAsync/ReadOneAsync` keep their signatures. They throw the
  typed exception instead of `INTERNAL`/`JsonException`. New members `ReadAvailableAsync(playerId, ids, ct)` and
  `ReadAllAvailableAsync(playerId, ct)` return `PlayerSnapshot(Sections, Unavailable)` and never throw for a read
  failure.
- **Where each reader lands.**

| Reader | Behaviour |
| --- | --- |
| Snapshot endpoint (all or explicit list) | `ReadAvailableAsync`; broken sections omitted and listed; 200 |
| Login | `ReadAvailableAsync` after the epoch write; listed in `LoginResponse`; never fails for a non-profile aggregate |
| `ProfileCache` (login pre-read, provider `EnsureAccessibleAsync`/`CurrentPlayerAsync`, bearer authentication) | typed exception propagates → error envelope with the typed code (FR-023). A broken profile is never cached (the cache only stores a loaded state) |
| Command execute (Touches ∪ Reads) | load inside the attempt throws the typed exception → rethrown unchanged by the store → `ExecuteAsync` counts the code → envelope. Before the handler, nothing staged (FR-024) |
| Command replay (fast path and in-attempt) | `TryLoad`; broken aggregate omitted from `Updates` and added to `CommandExecution.Unavailable` |
| GM player view | `ReadAllAvailableAsync`; readable aggregates as today + `unreadable[]` (wire-changes §4); a broken profile gives `deletion = null` |
| GM target validator, GM commands | typed exception → GM JSON error with the code |
| Account purge (`AccountPurgeWriter` profile load) | typed exception → the purge job reports the step as failed (existing path); other aggregates are removed raw, so a broken non-profile aggregate never blocks a deletion |
| `PlayerAggregateWriter` (other framework callers) | typed exception |

- **Middleware (T4).** `ExceptionMiddleware` skips its own Error line for a marked exception: the read was already logged
  once with aggregate, player, reason and exception type. It still writes the envelope with `RetryAfterMs` and
  `Details`. Result: "Unhandled exception." is never reached by a read failure, and there is exactly one log entry per
  failed read (FR-026).
- **Bearer authentication (T6).** The typed exception propagates out of `HandleAuthenticateAsync` to `ExceptionMiddleware`,
  so the envelope keeps the retry delay. **[VERIFY V5]**

**Rationale.**
- A typed `GameServerException` turns *every* reader that does not degrade into a typed error with no extra code: purge,
  validators, writer, and consumer code that calls `ReadOneAsync`.
- Only the five degrade sites change behaviour.
- Keeping signatures removes the compile break the spec had listed (BC-5 narrowed, BC-7 removed; see plan N1/N2).

**Alternatives.**
- A new exception type (impossible to derive from the sealed `GameServerException` without a public API change in Core;
  a parallel type would fall into the `INTERNAL` catch-all everywhere it is not handled).
- Change `ReadAsync` to return `PlayerSnapshot` (rejected: compile break for no gain over an additive member).
- One code for all failures (rejected by D7: retries hopeless cases or never retries the rolling deploy).

**Trade-offs.**
- An upgrader's exception message is never logged (only its type). The developer reproduces with the document from the
  GM view (GM sees `storedSchemaVersion`) or the store.
- The profile cannot degrade (D7, FR-023): it holds the epoch, the ban and the deletion state.

## R6 — Observability of read failures (FR-026)

**Decision.**
- New public Platform singleton `AggregateReadFailures` with `Report(AggregateReadFailure)`. It is used by
  `AggregateDocumentSerializer.TryLoad` and by `SectionWriter` (R7) for mapper failures.
- Each report writes:
  - **one log entry**, category `GameServer.Aggregates`. Warning for `version_ahead` (a deploy-order state), Error for the
    three others. Template in [errors-and-report-lines.md §2](contracts/errors-and-report-lines.md#2-log-lines). The
    request id comes from the `CorrelationMiddleware` scope;
  - **one increment** of `aggregate_read_failures_total{aggregateId, reason}`.
- `AggregateDocumentSerializer` gains a second constructor `(GameServerOptions, PlatformMetrics, AggregateReadFailures)`.
  The existing two-argument constructor stays: it builds a reporter that only counts, for direct constructions in tests.

**Rationale.** One reporter, called at the two places a failure is detected, gives "exactly one log and one metric per
failed read" by construction (SC-004). Inside a transaction a typed failure ends the attempt (no store retry on a body
exception), so a command reports once.

## R7 — One mapping site for sections; post-commit mapper failures (FR-022, FR-024; trap T5) — **architect decision, owner may override**

**Decision.** New public Platform.Web singleton `SectionWriter` used by the snapshot endpoint, the login response
(`IdentityModule`) and `CommandEndpointFactory.BuildResponse`:
- `WriteSections(playerId, loaded, failures, sections, unavailable)`;
- `WriteUpdates(playerId, changes, failures, updates, unavailable)`.

For each aggregate it calls the mapper inside a `try`. Any exception except `OperationCanceledException` becomes an
`AggregateReadFailure(…, MapperFailed, …)` reported once (R6) and listed as unavailable. The section is **left out**,
never sent with an empty payload (D7: an old SDK would parse an empty payload as a valid default state).
`unavailable` is ordered by aggregate id.

Post-commit (T5):
- `BuildResponse` runs after the commit. A mapper failure there leaves the commit standing: the response carries code,
  receipt id, result and `unavailable_sections` (HTTP 200).
- `SetOutcome` and `command_results_total` are unchanged (the command succeeded).
- The replay of the same request maps the same way.

**Rationale.** Three call sites, one rule, one reporter. The command response path is the only place where a failure
used to turn a *committed* command into a 500 that every retry repeats.

**Alternatives.** A `TryToPayload` on `AggregateWireMapperRegistry` called by each site (rejected: three places to
remember the report). Mapping before the commit (rejected: the runner works on domain state, the mapper is a web
concern, and a mapper failure must not roll back game state).

## R8 — Oversized result or receipt is the business rejection `COMMAND_OUTPUT_TOO_LARGE` (FR-030, FR-031, D8; trap T4) — **D8 confirmed**

**Decision.**
- **Code** `COMMAND_OUTPUT_TOO_LARGE` = 23, HTTP 200, not retryable. Details `part` (`result`|`receipt`), `bytes`,
  `limit`.
- **Shared rules** (`CommitRules`):
  - `CheckResultSize(operation, bytes, settings)` returns `CommitViolation(Code, Details, Line)` instead of a string;
  - new `CheckReceiptSize(operation, receiptBytes, settings)` → same;
  - the runner, the creation writer and the TestKit double call both.
- **Order inside the attempt (runner and creation writer).** Every commit rule runs **before the first staged write**:
  handler/initializers → code → result size → per touch change detection + document build + aggregate size → audit →
  receipt build (it needs only the writes list, claims and outbox, all known by then) → receipt size → **then** stage
  aggregates, claims, receipt.
- **Rejection path (T4).** A size violation throws the runner's private `CommandRejectedException(code, details)`:
  - it is rethrown unchanged by both stores (body exception, not an SDK exception);
  - it is caught at `catch (CommandRejectedException)`;
  - HTTP 200 ⇒ `CommandExecution(code, null, [], [], false, …) { Details = details }`;
  - nothing is committed and no receipt is written.
  - The Error log line (operation, part, size, limit) and `command_output_too_large_total{operation, part}` are emitted
    **in that catch**, once per request, not inside the attempt.
  - `ExecuteAsync` counts the code in `command_results_total`. Nothing passes through the `INTERNAL` catch-all.
- **Details on the wire.** `CommandResponse` gains `map<string,string> details = 11`. It is filled from the rejection's
  details for **every** HTTP 200 rejection (framework or module, `CommandOutcome.Reject(code, details)`). The GM command
  response gains `details` the same way. The XML doc of `ICommandOutcome.Details` changes from "not sent" to "sent in the
  command response". **Architect decision, owner may override** (listed as BC-11; fallback = send only framework codes'
  details, a one-line filter in `BuildResponse`).
- **Creation.** The same two checks apply to the account creation. A violation there is a module bug: `INTERNAL` (D15).
- **Replays and receipt lookups** never check the limits (spec edge case).

[VERIFY V2]

**Rationale.**
- D8 holds: the size depends on the run (number of rewards), nothing commits, the client gets a code. Same family as
  `AGGREGATE_TOO_LARGE`.
- Running every check before staging means a rejection never depends on the store's rollback of staged writes.
- The `details` field is needed because a 200 rejection had no way to carry the `part`/`bytes`/`limit` that FR-030
  requires. Sending module details too keeps one rule ("a rejection's details reach the client") instead of a
  framework-only exception. `Reject(code, details)` already defines those details as safe (they travel in envelopes for
  non-200 codes).

**Alternatives.**
- Details only in the log (rejected: FR-030, and the client could not tell result from receipt).
- Two codes (rejected by D8).
- Keep the check after staging (rejected: relies on the store's rollback of a half-staged attempt, which V2 would have to
  prove for every store).

## R9 — Command settings ranges (FR-032, D9; BC-2) — **D9 confirmed**

**Decision.** `CommandSettings.Validate() → IEnumerable<string>`, called from `ModuleRegistry.Validate` (serve, provision,
kit), one line per violation:

| Setting | Range |
| --- | --- |
| `ReplayWindowHours` | 1..168 |
| `PerPlayerQueueLimit` | 0..16 |
| `ReceiptMaxBytes` | 1024..65536 |
| `ResultCompactMaxBytes` | 64..16384, and `4·⌈ResultCompactMaxBytes/3⌉ + 1024 ≤ ReceiptMaxBytes` |
| `MaxAggregatesPerGame` | 1..16 |

Defaults unchanged (24, 2, 4096, 512, 16). `MaxTouches` and `MaxClaimMarkersLimit` stay constants.

**Rationale.** D9 as written: every Appendix B default is valid, a receipt stays below a 64 KiB document, the largest
result fits its own receipt, DAT-09 caps aggregates at 16. Binder type errors are already exit 1 (011), so only ranges
are new.

## R10 — SDK: caller request id API shape (FR-040, FR-041; trap T7) — **architect decision, owner may override**

**Probe done for this plan** (scratch project, .NET SDK 10.0.403 Roslyn, `LangVersion 9`, `netstandard2.1` and `net10.0`):

| Call | Binds to |
| --- | --- |
| `SendCommandAsync(r, q, p)` | existing overload |
| `SendCommandAsync(r, q, p, default)` | **existing** overload: no CS0121 |
| `SendCommandAsync(r, q, p, CancellationToken.None)` | existing |
| `SendCommandAsync(r, q, p, "id")` | new |
| `SendCommandAsync(r, q, p, null)` | new |

The same result holds for an options-object shape. The reason is the overload tie-breaker: with every other argument
equal, the candidate that needs no default argument substituted wins. `default` binds to the existing 4-parameter
overload because the new one would need `cancellationToken` defaulted.

So the trap as stated (CS0121) does not occur with a **required** fourth parameter. It does occur if the new parameter
is *optional*: `SendCommandAsync(r, q, p)` then matches both overloads, each needing defaults.

**Decision.**
- New overload `SendCommandAsync<TResult>(string route, IMessage request, MessageParser<TResult> resultParser, string
  requestId, CancellationToken cancellationToken = default)`. `requestId` is **required** (non-optional).
  - `null` means "SDK generates one" (the store path, R11).
  - Any other value must parse as a UUID in `D` format (36 characters, any case). It is normalized to lowercase `D`.
    Otherwise `ArgumentException` before any I/O.
- Static `GameServerClient.NewRequestId()` returns a fresh lowercase `D` UUID, for games that record the id in their own
  state before sending.
- `CommandResult<TResult>.RequestId`:
  - additive property;
  - new 5-argument constructor; the old 4-argument constructor sets `string.Empty`.
- `GameServerError.RequestId` is now also set on client-made errors of every call (network error, invalid response),
  from the id the call used. New static `GameServerError.Client(code, message, retryable, requestId)` (additive).
- Unity's compiler gives the same binding. **[VERIFY V1]**

**Rationale.** A string parameter is the smallest additive surface. With it required, every existing call keeps its
binding (probe). An options object adds a type for one value (no other per-call option is required by the spec).

**Alternatives.**
- Options object `CommandSendOptions { RequestId }` (same binding rules; rejected as speculative).
- A differently named method `SendCommandWithIdAsync` (kept as the V1 fallback).
- `requestId` before `request` (rejected: two adjacent strings `route, requestId` invite swaps).

## R11 — SDK pending-command store (FR-042..FR-047, D10, D11, D14; trap T8) — **architect decision, owner may override**

**Decision.** Format and algorithms in [sdk-pending-store.md](contracts/sdk-pending-store.md).

- **Interface** `IPendingCommandStore` (`Runtime/Core`) with `Entries`, `Record(PendingCommand)`, `Remove(requestId)`,
  `LoadProblem`, `WriteProblem`. Two implementations: `InMemoryPendingCommandStore` (tests, opt-in process-only use) and
  `PersistentPendingCommandStore(string folder)`.
- **How a client gets it (D14, opt-in):** `GameServerClientOptions.PendingCommands` (settable property, default null) and
  `GameServerClientOptions.PendingCommandMaxAge` (default 23 h, D11; `Validate()` requires `(0, 168 h]`).
  - Not a constructor parameter: an extra optional constructor parameter would change a public signature, and a second
    constructor with optional parameters would make `new GameServerClient(o, t, null)` ambiguous (CS0121).
- **One file per entry** (trap T8):
  - `{requestId}.pending` in the folder. The name comes only from the SDK-validated lowercase UUID (FR-045).
  - Container v1: magic `GSPC`, version, payload length, payload (request id, player id, route, recorded-at server ms,
    body), SHA-256.
  - **Record** = write `{id}.pending.tmp`, `Flush(true)`, `File.Move` to the final name. A new name each time, so there is
    no replace.
  - **Remove** = `File.Delete`, no flush. A delete lost by a power cut makes the entry reappear; its resend replays, which
    is safe.
  - **Load** (constructor) = enumerate `*.pending`, parse each, skip and delete leftover `*.tmp`. A damaged entry file is
    skipped and named in `LoadProblem`; the other entries load.
  - Cost per command is O(1) and independent of the number of pending entries.
- **Reuse of 015** (`CacheFileOps`): same seam class, plus one virtual member `EnumerateFiles(folder, pattern)`. Same
  `IsFileProblem` classification, same "memory first, disk failure exposed, never thrown" rule. The folder-name rule and
  Unity helper shape come from `UnityConfigCache` (`UnityPendingCommandStore.Create(environmentName)` → folder
  `persistentDataPath/gameserver-pending/{name}`, iOS no-backup flag).
- **Client flow.**
  - `SendCommandAsync` with a store: request id (caller's or generated) → `Record` **before the first attempt** → send.
  - Final answer → `Remove`: a `CommandResponse` with any code, or an error envelope whose code is not retryable.
  - Unknown → keep: network error after retries, `INVALID_RESPONSE`, a retryable envelope as last answer (including
    `COMMIT_UNKNOWN_RETRY`, `AGGREGATE_VERSION_AHEAD`), cancellation after `Record`.
- **Listing and resend (D10, no automatic resend).**
  - `PendingCommands` returns the store entries.
  - `StatusOf(PendingCommand)` returns `Resendable | Expired | OtherPlayer`, computed against `ServerNow` and the session's
    player.
  - `ResendPendingAsync<TResult>(PendingCommand, MessageParser<TResult>, CancellationToken)` sends the stored bytes with
    the stored id to the stored route, and returns a `CommandResult` like `SendCommandAsync`. It throws
    `InvalidOperationException` for a non-resendable entry (caller bug).
  - `DiscardPendingCommand(requestId)` removes an entry.
  - Login, logout, snapshot, config and time calls never touch the store (FR-044).
- **Threading.** Synchronous I/O on the calling thread (the Unity main thread), like the config cache. Not thread-safe;
  one client per folder.

[VERIFY V8, V9]

**Rationale.**
- Trap T8: a single container (the 015 shape) costs two full rewrites + two device flushes per command, growing with
  the number of pending entries.
- Per-entry files cost one flush per command (record) and an unflushed delete (remove). Each entry file is still
  all-or-nothing (temp + rename), which is what FR-045 asks ("never a partial entry").
- The narrowing "a damaged entry is skipped" instead of "the whole store reads empty" is stricter about losing data and
  equally safe (plan N6).

**Alternatives.**
- One container file with `File.Replace` (fallback of V9).
- Write on a background thread (rejected: the store would need locking, and Unity WebGL has no threads).
- `PlayerPrefs` (rejected as in 015).

**Trade-offs.**
- One file per in-flight command (normally 0-3 files).
- Directory enumeration at construction.

## R12 — SDK unavailable sections (FR-027, BC-8)

**Decision.**
- `SectionStore` gains:
  - `Unavailable` (`IReadOnlyDictionary<string, UnavailableSectionState>`, with id, code, code number);
  - `IsUnavailable(id)`;
  - event `AvailabilityChanged(string id)`.
- Marking happens from login, snapshot and command responses (`unavailable_sections`). A marked section keeps its last
  good copy. A later `Set`/`Apply` of the section clears its mark. `Clear()` clears marks.
- `ApplyLogin` clears the store as today **except** when the login is for the same player as the store's current
  content. In that case it keeps the copies of the sections the login lists as unavailable (a re-login during a rolling
  deploy keeps showing the wallet). The client remembers the player whose sections it holds.
- `AGGREGATE_VERSION_AHEAD` is retried by the existing retry rule (retryable, retry-after 1000 ms ≤ `MaxRetryAfter`).
  There is no new rule. The SDK's `ErrorCodes` gains the three constants.

## R13 — Sample seeding without test churn (FR-050, D12; trap T9) — **architect decision, owner may override**

**Facts.** 34 test files start the full sample game. Assertions on a new player's exact gold, ledger counts, revisions
and receipts sit in at least `BattleTests`, `ShopTests`, `GmAdjustTests`, `GateAndBanTests`, `IapAnalyticsTests`,
`AccountPurgeTests`, `SdkHarness`-based client tests and `CouchbaseGameHost` tests. `SampleTestHost` itself does not load
the sample game. The full-game tests use `tests/Shared/config`, not `samples/SampleHost/config`.

**Decision (D12 changed: data-driven seeding).**
- The sample game registers initializer `sample.starting_gold` (touches `wallet`). It grants
  `startingGold` gold through `EconomyOperations.Grant` (ledger source `account_init`), read from a new **server-only,
  optional** config file `account.json` (`{ "startingGold": 100 }`).
- The file is declared with a 015 rule: integer ≥ 0 and ≤ the gold `maxQuantity` of `resources.json`.
- `samples/SampleHost/config/base` gets the file and lists it in `files` (not in `clientVisible`).
- `tests/Shared/config` does **not** get it. The initializer then changes nothing, and by R2 no receipt is written: the
  34 full-game test files keep their expectations.
- New `SampleAccountInitializerTests` (sample host with a config that has `account.json`) cover the seeded path:
  balance, one ledger entry with its source, receipt, flag, concurrent first logins.
- KestrelDemo (real SampleHost config) shows the seed (manual check).

**Rationale.** The reference sample shows the real path (registration, config rule, grant, ledger), and the framework
suite pays no churn. The alternative named in the brief ("seed only in the sample game, test hosts unseeded") is exactly
this, achieved through config instead of a test-only switch.

**Alternatives.**
- Seed unconditionally and update ≈ 15 test files (rejected: large churn in tests that test other things).
- A setting that disables initializers in tests (rejected: a switch that production could set; FR-013 spirit).
- Fixture only (rejected by D12).

**Trade-offs.**
- A second config file for the sample.
- The SampleHost server config version changes once (it hashes `files`). No test pins its value
  (`HealthAndShutdownTests` and `ConfigCompressionTests` compare with `IGameConfig`).

## R14 — Receipt-size parity in the TestKit double (FR-034, SC-005; trap T11) — **architect decision on inputs**

**Decision.**
- `CommandRunner.BuildReceipt` moves to internal static `ReceiptBuilder.Build(ReceiptInputs)` (Platform.Commands). It is
  used by the runner, the creation writer and the double. `ReceiptDocument.SizeOf` is unchanged.
- `CommandContextDouble.CheckCommit(outcome, encodeResult)` adds the receipt-size violation, built with the same builder
  from:

| Receipt input | Runner | Double |
| --- | --- | --- |
| `receiptId` | hash, 32 chars | same function (configured actor, player, operation, request id) |
| `gameId`, `environmentId` | deployment | actor's (`test`/`test` by default) |
| `actorId`, `actorType`, `playerId`, `correlationId` | request | configured actor (default correlation = default request id) |
| `requestId` | `X-Request-Id` (36 chars) | `WithRequestId` (default 36 chars) |
| `bodyHash` | SHA-256 hex (64 chars) | 64-char placeholder (fixed length) |
| `resultCode`, `resultCompact` | outcome | outcome + encoder |
| `configVersion` | instance config | double's config (`WithConfig`) |
| `schemaVersions` | Touches ∪ Reads | same descriptor set |
| `aggregates[].newRev` | loaded rev + 1 | seeded rev + 1 (seeded = 1, missing = 0; new `Seed(id, state, rev)` overload) |
| `claimKeys`, `outbox` | context | same context |
| `committedAtUtc` | fixed-format UTC | fixed-format UTC (`At`) |

- The same inputs give byte-identical receipts. Different lengths of the configured values (actor id, config label, rev
  digits) change the size by exactly their length difference. The XML doc lists the placeholders (FR-034).
- The no-encoder `CheckCommit(outcome)` keeps its behaviour (no result → no receipt check), documented.
- For initializers, `CommandContextDouble.ForAccountInitializer(name, modules)` and `CheckAccountCreation()` use the
  creation's descriptor and receipt inputs (operation `identity.create_account`, empty result, system actor).

[VERIFY V7]

## R15 — Round release note and release act (FR-090, FR-091, D13) — **architect decision, owner may override**

**Decision (D13 split).**
- **Inside 016 (last docs task, after the 016 review fixes, so the BC list is final):**
  - write `specs/016-login-command/release-notes-draft.md` (FR-090);
  - merge the four drafts into `docs/releases/v0.7.0.md` (new folder): one list of fixed FG ids, one breaking-change table
    per slice with ids written `0NN/BC-n` (015 decision), one ordered first-consumer checklist 013 → 016, the
    architect/orchestrator decisions awaiting owner override, the open manual checks (015 T058 device run, 016 manual
    checks).
  - The header says **"v0.7.0 — draft, finalized at tag"**.
- **Round-close step (orchestrator, outside the slice, after 016 is merged and the owner confirmed T058 and the Couchbase
  re-run):**
  - one commit on `main` (`docs: release v0.7.0`) that sets the release date in `docs/releases/v0.7.0.md`;
  - restamp every page carrying "v0.7.0 (chưa phát hành)" to the released form: `docs/html/{index,01-gioi-thieu,
    02-them-command,03-deploy}.html`, `docs/guides/{environments-and-local-run,module-authoring}.md`,
    `docs/pcr/{economy,gm,iap,identity}.md` at the time of writing; the step greps `chưa phát hành` and `unreleased`
    again;
  - the annotated tag `v0.7.0` whose message points at the release file.
  - Push of branch and tag only with the owner (spec Out of Scope; resume rule).

**Rationale.**
- The content needs the final 016 BC list, so it belongs to the slice.
- The release act depends on owner gates that are not slice work (T058 on devices) and must not be blocked inside a
  slice branch or done twice.
- Restamping inside 016 would claim "released" before the tag exists.

## R16 — Guard tests to extend (trap T10)

| Guard | Change |
| --- | --- |
| `ArchitectureTests.Inside_platform_only_the_transactional_write_paths_use_transaction_contexts` | no rule change (`AccountWriter` lives in `Platform.Commands`); comment lists the creation writer |
| `ArchitectureTests.Rules_engine_detects_real_dependencies` | add a positive control: `AccountWriter` depends on `ITransactionContext` |
| `ArchitectureTests.Only_platform_writes_through_transactions` | unchanged; must stay green with `LoginCompletion` calling `IAccountWriter` |
| New `ArchitectureTests.Login_writes_accounts_only_through_the_account_writer` | `GameServer.Modules.Identity.Adapters` no longer depends on `IPlayerAggregateWriter`'s creation-anchor overload for login (reflection over `LoginCompletion` dependencies) |
| `ArchitectureTests.Core_and_business_projects_are_independent_of_…` | covers `IAccountInitializer` in business projects (no change, must stay green with the sample/fixture initializers) |
| `TestKitArchitectureTests.AllowedInternalTypes` | + `GameServer.Platform.Commands.ReceiptBuilder`, `…ReceiptInputs`, `…InitializerContext`, `…AccountCreation` (descriptor factory), `…CommitViolation` |
| `TestKitArchitectureTests.The_kit_references_no_test_framework_…` | unchanged, must stay green |
| `ErrorCodeBandTests` | the three new core codes inside 0-999 and outside the game band |
| `SdkPublicApiTests` | re-record `TestData/sdk-public-api.txt` at the 016 base (T001) so 015's additions are protected too; then 0 removed/changed (SC-009) |
| `UnityPackageMetaTests` | covers the new SDK files (`PendingCommandStore.cs`, `PendingCommandFormat.cs`, `UnityPendingCommandStore.cs`) |
| `CommittedOutputTests` (WireGen) | all three configs after regeneration |
| `DocumentedConfigurationKeysTests` (SampleGame.Tests) | `GameServer:Commands:*` five keys in the documented list (FR-033) |
| `ConfigDeclarationParityTests` (SampleGame.Tests) | the sample's `account.json` rule gives the same lines host vs kit |
| `ExceptionMiddleware` new test | no read failure reaches "Unhandled exception." (FR-026) |

## R17 — WireGen regeneration (014 seam)

After the proto changes (wire-changes §1), run `tools/GameServer.WireGen generate` then `verify` for:
- `sdk/GameServer.Client/wiregen.json` (expected diff: `Runtime/Generated/gameserver/core/v1/Core.cs`,
  `Runtime/Generated/Identity.cs`);
- `samples/SampleGame/wiregen.json` (expected: no diff; sample protos import core types from the SDK);
- `tests/ConsumerFixture/tools/wiregen.json` (expected: no diff).

`CommittedOutputTests` verifies all three. The route catalog does not change (no new route). An unexpected diff in the
second or third is a finding, not something to commit blindly.

## Facts to confirm before coding (VERIFY)

| # | Fact | How to confirm | Fallback |
| --- | --- | --- | --- |
| V1 | Unity 6000.3.22f1's compiler binds `SendCommandAsync(r, q, p)`, `(r, q, p, default)`, `(r, q, p, ct)` to the existing overload and `(r, q, p, "id")`, `(r, q, p, null)` to the new one, as Roslyn in .NET SDK 10.0.403 at `LangVersion 9` does (**probe done for .NET, 2026-11-04**) | A probe script under KestrelDemo `Assets/Editor` compiled by `smoke-build.ps1`, plus an SDK test file (C# 9) with the five call shapes | Rename the new method `SendCommandWithIdAsync` (same parameters) |
| V2 | A non-SDK exception thrown by the transaction body (`CommandRejectedException`, typed read failure, `AccountInitializerFailedException`) **before** any staged write is rethrown unchanged by `CouchbaseDocumentStore` and the in-memory/file stores, and nothing is committed | Couchbase integration test: handler outcome over the receipt limit and over the result limit; initializer that rejects; read of a broken aggregate in Touches → 0 documents written, typed outcome | Unwrap `CbTransactionFailed.InnerException` chain for our exception types in the store adapter |
| V3 | Two concurrent creation transactions for the same player on Couchbase: the loser gets `DocumentExistsException` (or the SDK retries the lambda, which then reads the committed profile); 20 concurrent first logins + 5 lost-response retries end with 1 profile insert, 1 receipt, 1 ledger entry within 3 race rounds and the 10 s expiration | `CouchbaseAccountCreationTests` (SC-002) | Raise the writer's race rounds to 5 for logins; exhaustion stays `CONCURRENT_UPDATE` retryable |
| V4 | Two sequential `GetManyAsync` calls in one attempt (profile, then anchor + touches) work on Couchbase and the in-memory/file stores, and the second read sees a concurrent anchor removal as a write-write conflict after the anchor rewrite (010 R9 holds) | Existing purge-race test (US2-5 of 010) run against the new writer + a new test with initializers | One bundle read of profile + anchor + touches in every login attempt (returning logins pay ≤ 4 extra reads in the transaction) |
| V5 | An exception thrown by `PlayerBearerAuthenticationHandler.HandleAuthenticateAsync` propagates through `HttpContext.AuthenticateAsync` (ASP.NET Core 10.0.14) and `ProfileEnforcementMiddleware` to `ExceptionMiddleware` unchanged | Host test: broken profile + bearer request → envelope with the typed code, retry-after 1000 ms for version-ahead | Catch the marked exception in the handler and `Fail(code, details)`; envelope loses only the retry delay (SDK backoff applies) |
| V6 | The exception types the loader can raise for a damaged document are exactly: STJ `JsonException`, `NotSupportedException`, `InvalidOperationException` (`GetValue<T>` type mismatch, null state), `FormatException`/`OverflowException` from `JsonNumbers` on absurd numbers, and anything an upgrader throws | Corruption matrix test (`AggregateReadFailureTests`): state of wrong shape, wrong primitive types, `schemaVersion` as string, `rev` as object, `createdAtUtc` as number, `state` missing, `state` as array, upgrader throwing `KeyNotFoundException` | Catch every exception except `OperationCanceledException`/`OutOfMemoryException` in the deserialize block (the plan already does so for upgraders) |
| V7 | Runner and double give byte-identical receipts for the same inputs (sample battle end and shop buy, fixture `fixture.save`, the sample initializer) and the only variable-length inputs are those of R14's table | `ReceiptParityTests` (Platform.Web.Tests + fixture): host receipt bytes from the store vs `ReceiptBuilder` output of the double | Add the missing input to the double's configuration (e.g. `WithCorrelationId`) and its XML doc |
| V8 | Record (temp write + `Flush(true)` + `File.Move`) and remove (`File.Delete`) of a 4 KiB entry each ≤ 10 ms p95 outside Unity on the reference machine, on C: and D: (015 measured D: slower for flushes) | Opt-in perf test `GAMESERVER_PERF=1`, 200 iterations after warm-up | Record with `Flush(false)` (survives app death, the target case, not power loss), documented in PCR unity-sdk |
| V9 | `File.Move(tmp, final)` when `final` is absent is a rename on Windows (.NET + Unity Mono), Android and iOS IL2CPP; `Directory.EnumerateFiles(folder, "*.pending")` survives IL2CPP stripping High | Unit test outside Unity; device run of KestrelDemo (manual, with the 015 T058 session) | One container file with `File.Replace` (015 shape) |
| V10 | `map<string,string> details` and `repeated UnavailableSection` regenerate with WireGen into C# 9-compatible code with `Google.Protobuf` 3.36.2, like `ErrorEnvelope.details` | WireGen `verify` + SDK project build (C# 9, netstandard2.1, 0 warnings) + Unity smoke build | None needed beyond fixing generator config; `details` could become `repeated Detail {key, value}` (still additive) |
| V11 | The GM OpenAPI generator (`docs/api/gm-openapi.json`) emits the new `unreadable` array of the player view and the `details`/`unavailable` members of the GM command response (010 noted generator limits for some shapes) | `GmOpenApiTests` snapshot | Hand-written schema fragment registered for those types, documented in PCR gm |
| V12 | The per-localId (and per-IP) login limits allow the SC-002 test (20 concurrent first logins of one credential + 5 retries) only with raised test settings; production defaults are unchanged | Test settings in the SC-002 test | None (test configuration) |
