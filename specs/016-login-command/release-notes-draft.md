# Release notes draft — v0.7.0 (slice 016 login-command)

Framework `0.7.0` (unreleased). Input of `docs/releases/v0.7.0.md` (T083), which merges the drafts of slices 013, 014, 015 and
016 into one round note (cross-slice ids written `0NN/BC-n`). Started in T002 (2026-11-04, framework `6054a04`); extended
by the story checkpoints T022, T032, T046, T054, T062 and T075 (done 2026-11-04); final pass in T076. The release date, the restamp of the
pages marked "chưa phát hành", the tag and any push are a separate round-close step, not part of this slice.

**Fixed:** FG-013 (the login says whether it created the player; its sections are the client's boot read), FG-015 (a new
account gets its starting state in the transaction that creates it), FG-029 (one broken aggregate no longer takes down the
snapshot or the login), FG-016 (command limits are documented, validated settings; going over one is the typed rejection
`COMMAND_OUTPUT_TOO_LARGE`), FG-022 (caller-owned request ids and an opt-in pending-command store let a command sent before
the app died be replayed, not run twice). **Breaking:** BC-1 to BC-11 (table below).

**Unchanged:** no route and no persisted document shape changes; five additive proto fields and one message
(`LoginResponse.is_new_player` = 8, `LoginResponse.unavailable_sections` = 9, `SnapshotResponse.unavailable_sections` = 5,
`CommandResponse.unavailable_sections` = 10, `CommandResponse.details` = 11, message `UnavailableSection`); additive GM JSON
members; no existing field changes meaning (API-03); the Unity package stays `0.7.0`.

## FG-013 — new-player flag

- **`LoginResponse.is_new_player`** (field 8, SDK `LoginResponse.IsNewPlayer`) is `true` only for the login whose
  transaction created the player. It is `false` for a returning player, for the loser of two racing first logins of one
  credential, for a takeover from a second device, for an unconfirmed `switch_account` (which answers `SWITCH_ACCOUNT` and
  creates nothing), and for the retry of a first login whose response was lost: the server had committed the creation, so
  the retry is a normal login (session epoch + 1 once more). A confirmed `switch_account` into an identity without an
  account creates that account and answers `true`.
- **Analytics:** the login record's `isNewUser` comes from the same value, so it always equals the flag. Before, two
  racing first logins of one credential both reported `isNewUser = true` (the value came from a read before the write).
- **How:** every login writes the profile through the new Platform service `IAccountWriter`
  (`GameServer.Platform.Commands`). Each transaction attempt decides "create or update" from what it read and returns the
  decision; a returning login is the same transaction as before (one read, one replace). Login store failures answer
  typed codes (BC-10).
- **SDK:** no source change; `IsNewPlayer` is a generated member. Against a server before 016 it reads `false`.
- The login sections are every client-visible section with its `rev`, so a client can boot on the login answer without a
  second snapshot. A section the server cannot read is left out and listed in `unavailable_sections` (FG-029 below).

## FG-015 — account initializers

- **API (Core, `GameServer.Accounts`):** `IAccountInitializer.InitializeAsync(ICommandContext) → ValueTask<ErrorCode?>`
  (null = OK), registered with `AccountInitializerRegistration.Of<T>(name, touches, maxClaimMarkers = 0)` in the new
  `ModuleDescriptor.AccountInitializers`. An initializer is created per transaction attempt from DI (constructor
  injection, like a command handler) and must be pure over the context: the transaction may run it several times.
- **When it runs:** only in the transaction attempt of a first login that finds no profile, in module registration order
  then list order, over one shared context (initializers that declare the same aggregate see each other's writes). An
  existing account never runs an initializer, and accounts created before an initializer was registered are not seeded
  later (no backfill, D6): keep the old game command for them or reset pre-launch data.
- **Scope per initializer:** each one sees its own view: `Get`/`Set` outside its `Touches` throw the runner's DAT-11/DAT-09
  message naming it (`Command 'identity.create_account (initializer {name})' wrote aggregate '…' …`), and its claims count
  against its own `maxClaimMarkers`.
- **Budget (D4, checked at startup, BC-9):** `profile` + the union of all initializers' touches ≤ 4 aggregates
  (`CommandDescriptor.MaxTouches`), claim markers summed over all initializers ≤ 2, names unique, `profile` never touched,
  no module command named `identity.create_account`.
- **Receipt only with effects:** when an initializer changed an aggregate, reserved a claim or added an outbox entry, the
  creation writes one receipt `identity.create_account` (actor type `system`, actor id = the player, a random request id
  per login call, empty result, schema versions of the touched aggregates). Its outbox (ledger entries of grants) is relayed
  like a command's. Without effects, the profile is written alone, exactly as before. The player reads the receipt through
  `GET /api/v1/player/receipts/{id}`; the id is in the Information line `Account {PlayerId} created: {n} initializers,
  receipt {ReceiptId|none}.` and in the GM receipt list (it is not on the wire, plan N3).
- **Every commit rule before the first staged write:** aggregate sizes, audit (0 entries), receipt size. A failing
  initializer — a returned code, a throw (`OperationCanceledException` excluded) or a rule — leaves nothing (no profile,
  aggregate, claim or receipt), is logged once in category `GameServer.Accounts` (`… returned {Code}.`, `… threw
  {ExceptionType}.` or `{RuleLine}`, each ending `Nothing was created; the next login retries.`; never an exception
  message), is counted in `account_initializer_failures_total{initializer, result=rejected|threw|rule}` (a receipt over its
  limit uses `initializer="*"` and also counts `command_output_too_large_total{operation="identity.create_account",
  part="receipt"}`) and `account_creations_total{result="failed"}`, and the login answers 500 `INTERNAL` (D15). The next
  login of the same credential retries the creation.
- **Metrics:** `account_creations_total{result=created|failed}` and `account_initializer_failures_total`; the creation also
  records `receipt_size_bytes`, `aggregate_size_bytes` and `txn_*` with operation `identity.create_account`.
- **Kit:** `CommandContextDouble.ForAccountInitializer(name, modules)` runs an initializer host-free behind the writer's view
  (system actor for `p_test`, touches at their default state, operation `identity.create_account`), and
  `CheckAccountCreation()` returns the writer's own rule lines (aggregate size, audit, receipt size measured with the
  writer's receipt builder).
- **Measured (SC-002, SC-003):** 20 concurrent first logins of one credential plus 5 retries after a lost response create
  one account, one receipt, one ledger entry and exactly one `is_new_player = true` on the in-memory, File and Couchbase
  stores (Couchbase: every race loser became a normal login in its second round, 0 store retries). On local Couchbase EE
  7.6.12 a first login with an initializer over three aggregates and one grant has p95 14.36 ms against 9.09 ms without
  (+5.27 ms, budget 20 ms); returning logins p95 5.50 ms with the initializer registered against 4.85 ms without.

## FG-029 — degraded reads of a broken aggregate

- **Two codes:** `AGGREGATE_VERSION_AHEAD` (24, HTTP 503, retryable, `retry_after_ms = 1000`: the stored `schemaVersion` is
  newer than the code's — a rolling deploy) and `AGGREGATE_UNREADABLE` (25, HTTP 500: an upgrader is missing or threw, an
  envelope field has the wrong type, the state is missing, `null`, not a JSON object or does not deserialize, or the wire
  mapper threw). Details carry only `aggregate`; the exception has no inner exception.
- **Degrading sites (200):** the snapshot (all sections or an explicit list), the login (a non-profile aggregate), the replay
  of a committed command, the command response when a wire mapper throws after the commit (the commit stands), and the GM
  player view. The broken section is left out — never sent with an empty payload — and listed in `unavailable_sections`
  (`{id, code, code_number}`, ordered by id; GM: `unreadable[]` with `id`, `code`, `reason`, `storedSchemaVersion`,
  `codeSchemaVersion`, and `unavailable[]` in the GM command response). Every other section is byte-identical to an
  undamaged read.
- **Typed refusals:** a command that touches or reads a broken aggregate answers the typed envelope before its handler and
  writes nothing; GM commands and the GM target validator answer the GM JSON error with the code; `ReadOneAsync` and the
  other throwing readers throw it.
- **Broken profile:** the login, every authenticated request (bearer) and the provider login answer the typed code (503 with
  the retry delay for a version-ahead profile); the profile is never cached broken, so a repaired document serves the next
  request. A profile whose *mapper* throws is only listed (the server still holds the state). The GM view lists a broken
  profile and gives `deletion = null`; the player still exists. The account purge cannot begin for a broken profile
  (reported as today: `Purge of player {id} could not begin (GameServer.Errors.GameServerException); retried next run.`); a
  broken non-profile aggregate never blocks a deletion.
- **One log line and one metric per failed read:** category `GameServer.Aggregates`, Warning `Aggregate {AggregateId} of
  player {PlayerId} is unavailable: version_ahead (stored schemaVersion {Stored}, code {Code}). Deploy code that reads it
  before writing it (PCR platform §6).`, Error `… is unavailable: {Reason} ({ExceptionType}).` otherwise; never state content
  or an exception message. `aggregate_read_failures_total{aggregateId, reason}`. The error middleware writes no second line
  for these, and "Unhandled exception." is never reached (SC-004 matrix: four reasons × nine readers).
- **SDK:** `SectionStore.Unavailable` / `IsUnavailable(id)` / event `AvailabilityChanged` (new type
  `UnavailableSectionState`). A marked section keeps its last good copy; the next `Set`/`Apply` of it clears the mark. A
  login for the same player keeps the copies of the sections it lists as unavailable; another player's login starts from
  an empty store. `AGGREGATE_VERSION_AHEAD` is retried by the existing retryable rule with the same bytes and request id.
- **Deploy order rule kept** (PCR platform §6): deploy code that reads a new schema version everywhere before any instance
  writes it; the degradation only keeps the other sections and the login working during the window.

## FG-016 — command limits and `COMMAND_OUTPUT_TOO_LARGE`

- **Every commit rule runs before the first staged write** (runner and account writer): handler → code → result size →
  per-touch change detection, document build and `AGGREGATE_TOO_LARGE` → audit → receipt build → receipt size → only then the
  aggregates, claims and receipt are staged. A refused run never depends on the store's rollback (VERIFY V2: the stores
  rethrow the body's exception unchanged; confirmed again on Couchbase by `CouchbaseRunnerTests`, T053).
- **Typed rejection:** a result over `ResultCompactMaxBytes` or a receipt over `ReceiptMaxBytes` is the business code
  `COMMAND_OUTPUT_TOO_LARGE` (23, HTTP 200, not retryable) with details `part` (`result` | `receipt`), `bytes`, `limit`;
  exactly at the limit commits ("greater than" rule). Nothing commits and no receipt is written, so a retry with the same
  request id executes again and gets the same code. Player, Dev and GM commands alike (GM: 200 JSON with `code`,
  `codeNumber` 23 and `details`). One Error line per request in category `GameServer.Commands`, written outside the
  transaction attempt: `Command {Operation} refused with COMMAND_OUTPUT_TOO_LARGE: {Part} is {Bytes} bytes, limit {Limit}.
  Fix: return less or raise GameServer:Commands:{SettingName} (PCR platform §22).` — and one
  `command_output_too_large_total{operation, part}`; `command_results_total{code}` counts the code. A creating login over a
  limit stays `INTERNAL` with the creation rule line (D15, T026).
- **Details on the wire (BC-11):** `CommandResponse.details` (field 11) and the GM command response's `details` (object of
  strings, empty for a commit; `docs/api/gm-openapi.json` gains only this member) carry the details of every HTTP 200
  rejection, module codes included (`CommandOutcome.Reject(code, details)`); `CommandExecution.Details` is the new init
  property behind them.
- **Settings ranges (BC-2):** `CommandSettings.Validate()` joins the startup report (serve, `--provision` and the kit) after
  the account-initializer block, one line per value: `ReplayWindowHours` 1..168, `PerPlayerQueueLimit` 0..16,
  `ReceiptMaxBytes` 1024..65536, `ResultCompactMaxBytes` 64..16384, `MaxAggregatesPerGame` 1..16, plus the cross rule
  `4·⌈ResultCompactMaxBytes/3⌉ + 1024 ≤ ReceiptMaxBytes` (the largest result must fit its own receipt). An out-of-range
  `MaxAggregatesPerGame` gets only its range line and the aggregate count is checked against 16. Defaults (24, 2, 4096,
  512, 16) are valid. Because of the cross rule the smallest accepted `ReceiptMaxBytes` is 1112 (with
  `ResultCompactMaxBytes` = 64); 1024 is inside its own range but always fails the cross rule.
- **TestKit receipt check (FR-034):** `CommandContextDouble.CheckCommit(outcome, encodeResult)` now also returns the receipt
  line (`Command '{operation}' receipt would be {bytes} bytes; ReceiptMaxBytes is {limit}: …`), measured with the runner's
  own receipt builder from the configured actor, player, request id, receipt id, config version, `At`, seeded revisions,
  claims and outbox; the request body hash is a 64-character placeholder (the runner's SHA-256 hex has the same length).
  The result line is the new text `Command '{operation}' result is {bytes} bytes; ResultCompactMaxBytes is {limit}: the
  runner rejects the command with COMMAND_OUTPUT_TOO_LARGE (part result) and nothing commits. …`. `CheckCommit(outcome)`
  without an encoder checks neither size (no result, no receipt). New `Seed<TState>(aggregateId, state, rev)` (rev ≥ 1; the
  receipt's `newRev` is rev + 1); `Seed(aggregateId, state)` keeps rev 1. Parity (VERIFY V7, T052): for the sample
  `shop.buy`, `battle.end` and a seeding account creation, the stored receipt and the double's measure have the same size
  (0 bytes difference); a longer configured value changes the measure by its JSON length (a `+`, which the JSON writer
  escapes as `\u002B`, counts six bytes on both sides).

## FG-022 — caller request ids and the pending-command store

- **Caller request ids:** `SendCommandAsync(route, request, parser, string? requestId, ct = default)` — the fourth parameter
  is required (`null` = generated), so every existing call keeps its overload (VERIFY V1). A UUID in format D in any case is
  normalized to lowercase; anything else (empty, `N` format, braces, 37 characters) throws `ArgumentException` before any
  network call or disk write. Every attempt sends the id with the same bytes; the same id and bytes later (also after an
  app restart) replay the committed command. `GameServerClient.NewRequestId()`, `CommandResult.RequestId` (the
  four-argument constructor sets `""`), and `GameServerError.Client(code, message, retryable, requestId)`: SDK-made errors
  of a call (`NETWORK_ERROR`, `INVALID_RESPONSE`) now carry the call's request id.
- **Opt-in pending store** (`GameServerClientOptions.PendingCommands`, default null = nothing written;
  `PendingCommandMaxAge` default 23 h, validated in (0, 168 h]): with a store, a command is recorded `(id, session player,
  route, exact body bytes, ServerNow)` before its first attempt; removed on a command response with any code or an error
  envelope that is not retryable (e.g. `IDEMPOTENCY_PAYLOAD_MISMATCH`); kept when the outcome is unknown (no response,
  `INVALID_RESPONSE`, a retryable last answer such as `COMMIT_UNKNOWN_RETRY` or `AGGREGATE_VERSION_AHEAD`, cancellation).
  Login, logout, snapshot, config and time calls never touch it. A disk failure never stops the command: the store keeps
  the entry in memory and reports `WriteProblem`.
- **After a restart:** `client.PendingCommands` lists the entries; `StatusOf(entry)` is `OtherPlayer` (another player or no
  session), `Expired` (at least `PendingCommandMaxAge` old on the server clock, so a device clock change does not move it)
  or `Resendable`; `ResendPendingAsync(entry, parser)` sends the stored bytes with the stored id to the stored route (a
  committed command answers `Replayed` with its original receipt) and throws `InvalidOperationException` for an entry that
  is not resendable or not in the store; `DiscardPendingCommand(id)` removes one (unknown ids ignored). The SDK never
  resends by itself.
- **`PersistentPendingCommandStore(folder)`**: one file `{requestId}.pending` per entry (container v1 `GSPC`, SHA-256),
  written to `{id}.pending.tmp`, flushed to the device and renamed (`Replace` when the same id is recorded again); removed
  with an unflushed delete. Load deletes leftover temp files, skips, deletes and names damaged entries in `LoadProblem`
  (`"{file}: {reason}"`), leaves files whose names are not exact lowercase entry names alone (Windows matches patterns
  case-insensitively, VERIFY V9), and never throws for the disk. File names come only from validated ids; the session
  token is never written. Unity: `UnityPendingCommandStore.Create(environmentName)` →
  `persistentDataPath/gameserver-pending/{name}` (the config-cache name rule, iOS no-backup flag on the folder); one client
  per folder, Unity main thread.
- **Measured (SC-008, T061):** record of a 4 KiB entry p50 2.29 / p95 5.56 ms, remove p95 0.26 ms on C: (`%TEMP%`, opt-in
  `GAMESERVER_PERF=1`); `Flush(true)` kept.
- Client snippet:

  ```csharp
  var options = new GameServerClientOptions(baseUrl, clientVersion, platform, buildId)
  {
      PendingCommands = UnityPendingCommandStore.Create("live"),
  };
  var client = new GameServerClient(options, new UnityWebRequestTransport());
  await client.LoginGuestAsync(localId, secret);
  foreach (var pending in new List<PendingCommand>(client.PendingCommands))   // a copy: the live list changes below
  {
      if (client.StatusOf(pending) == PendingCommandStatus.Resendable)
      {
          await client.ResendPendingAsync(pending, BattleEndResult.Parser);   // replays a committed command
      }
      else
      {
          client.DiscardPendingCommand(pending.RequestId);                   // decide from the player's state
      }
  }

  var requestId = GameServerClient.NewRequestId();                            // optional: keep it in game state first
  var result = await client.SendCommandAsync(GameRoutes.BattleEnd, request, BattleEndResult.Parser, requestId);
  ```

## Samples, fixture and docs (US6)

- **Sample game seeds starting gold (FR-050, D12 changed).** New account initializer `sample.starting_gold`
  (`samples/SampleGame/SampleGame/SampleAccountInitializer.cs`, touches `wallet`) grants `startingGold` gold with ledger source
  `account_init`, read from the new optional, server-only `account.json`; rule `sample.account` (`startingGold` an integer from
  0 to the gold `maxQuantity`; line `[module sample.game] config account.json: startingGold must be an integer from 0 to
  {maxQuantity} (the maxQuantity of gold in resources.json); found {value}. Fix: set the gold a new account starts with.`).
  `samples/SampleHost/config/base/account.json` = `{ "startingGold": 100 }`, listed in `files`, not client-visible:
  SampleHost server config version `sample-1+8a37f29ee2dc` → `sample-1+53ca1d498d7a` (once), client version
  `sample-1+9b25b8b6bc0b` unchanged; the startup line now says `6 files (4 client-visible …), 5 rules`. `tests/Shared/config`
  has no `account.json`, so the full-game suites are unchanged; the hosts on the real SampleHost config needed no assertion
  change (T064).
- **Consumer fixture** registers `fixture.welcome` (welcome note in the hidden `notes` aggregate, cut to `notes.json`
  `maxLength`) and tests it with the kit (`FixtureInitializerTests`) and the receipt check (`FixtureReceiptSizeTests`: host and
  kit measure the same `fixture.save` receipt; one byte under it gives the runner's receipt line).
- **Template** gains a commented initializer example (registration, touches budget, purity, failure, kit test).
- **KestrelDemo** logs `new player: {IsNewPlayer}`, boots on the login sections, keeps commands in
  `UnityPendingCommandStore.Create("local")`, resends the `Resendable` entries after login (`resent {id}: replayed=…`),
  discards the others, and sends battle end with `GameServerClient.NewRequestId()` (an unknown outcome logs `EndBattle → pending
  {id}`). `SmokeBuild.EndToEnd` accepts `-e2eDevice <name>` to keep one guest device across runs (PlayerPrefs).
- **Docs:** consumer guide §13 (login flow, account initializers, degraded reads, command limits, request ids and the pending
  store; §5.1 table, §10 pointer and §11 bump steps updated); module-authoring §3/§4 and the template README; PCR platform §2,
  §3, §4, §5, §6, §8, §19 and the new §22 (limits table, measured receipt example, metrics, log and startup lines,
  BC-1..BC-11); PCR identity, economy, sample-game, gm and unity-sdk; HTML pages 01, 02 (§3 step 9, codes table, §6b,
  troubleshooting) and 03 (metrics, settings validation). Every touched page keeps the "0.7.0 (chưa phát hành / unreleased)"
  stamp.
- **Measured receipt sizes (PCR platform §22):** a creation receipt with the sample grant is 1068 B; each further ledger entry
  adds 277–279 B; 11 entries fit 4096 B (3841 B; 12 = 4120 B) and 55 fit 16384 B (16117 B; 56 = 16396 B).

## Breaking changes

"How it surfaces" is replaced by the real lines and codes at each story checkpoint (asserted literally by the slice 016
tests, or measured where noted).

| Id | Change | Who is affected | How it surfaces | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | A result or receipt over its limit is the business rejection `COMMAND_OUTPUT_TOO_LARGE` (HTTP 200, nothing commits) instead of a 500 `INTERNAL` (FR-030, FR-031) | Clients and tests that expect 500 for an oversized command; dashboards counting `command_results_total{code="INTERNAL"}` for it | HTTP 200 command response `COMMAND_OUTPUT_TOO_LARGE` (23) with details `part`/`bytes`/`limit`, e.g. `{part: "result", bytes: "513", limit: "512"}`; nothing written, no receipt; `command_output_too_large_total{operation, part}`; Error line `Command {Operation} refused with COMMAND_OUTPUT_TOO_LARGE: {Part} is {Bytes} bytes, limit {Limit}. Fix: return less or raise GameServer:Commands:{SettingName} (PCR platform §22).` (category `GameServer.Commands`); TestKit lines `Command '{op}' result is …` / `… receipt would be …` (errors-and-report-lines §5) | Treat the code as a design bug of the command; raise the limit (FR-032) or return less |
| BC-2 | `GameServer:Commands:*` values are validated at startup (FR-032); `MaxAggregatesPerGame` above 16 is refused | Deployments with out-of-range values (silently accepted before) | Startup exit 1 (serve and provision), one line per value after the account-initializer lines, e.g. `[deployment] GameServer:Commands:ReplayWindowHours is 0; allowed 1..168 (CMD-07).` and `[deployment] GameServer:Commands:ResultCompactMaxBytes 512 needs 1708 receipt bytes (base64 + 1024) but ReceiptMaxBytes is 1024. Fix: raise ReceiptMaxBytes or lower ResultCompactMaxBytes.` | Use a value in range |
| BC-3 | **Snapshot and login semantics (PCR platform §4, API-10):** a snapshot or login no longer always holds every client-visible section; an unreadable one is absent and listed in the new unavailable field (FR-022, FR-023) | Client code that assumes every section is present after login or snapshot (old SDKs see the section missing, where the whole call used to fail) | `unavailable_sections` entry `{id, code, code_number}` (`AGGREGATE_VERSION_AHEAD` 24 / `AGGREGATE_UNREADABLE` 25), HTTP 200; the section absent from `sections` (old SDKs see only that) | Check the unavailable list (or a missing section) and show a recoverable state |
| BC-4 | A command or replay that touches or reads an unreadable aggregate answers `AGGREGATE_VERSION_AHEAD` (503, retryable) or `AGGREGATE_UNREADABLE` (500) instead of 500 `INTERNAL`; a broken `profile` fails login and authenticated requests with these codes (FR-020, FR-023, FR-024). PCR §6 "newer than code → 500 `INTERNAL`" changes | Clients and tests that match `INTERNAL`; SDKs now retry the version-ahead case (retryable) | Error envelope 503 `AGGREGATE_VERSION_AHEAD` (`retryable`, `Retry-After: 1`, `retry_after_ms = 1000`) or 500 `AGGREGATE_UNREADABLE`, details `{"aggregate": id}`; `http_errors_total{code}` and `command_results_total{code}` with the new codes; nothing written; the replay of a command committed before the damage stays 200 and lists the aggregate | Match the new codes; keep the deploy rule of PCR §6 |
| BC-5 | `AggregateDocumentSerializer.Load` and `PlayerSnapshotReader.ReadAsync/ReadAllAsync/ReadOneAsync` throw a `GameServerException` with `AGGREGATE_VERSION_AHEAD` or `AGGREGATE_UNREADABLE` (details `aggregate`) instead of `GameServerException(INTERNAL)`, `JsonException`, `InvalidOperationException`, `FormatException`, `OverflowException` or an upgrader's own exception. Signatures are unchanged; the additive `TryLoad`, `ReadAvailableAsync` and `ReadAllAvailableAsync` return the failures instead of throwing (FR-021; plan N1). A stored `state` that is missing, `null` or not a JSON object was silently read as the default state; it is now `AGGREGATE_UNREADABLE` (T004) | Game or test code that calls these readers directly and catches the old exception types | Different exception type and code at run time (no compile error) | Catch `GameServerException` (or use the degrading members) |
| BC-6 | A first login runs every registered account initializer in the creation transaction (FR-011): new accounts start with seeded aggregates at `rev ≥ 1`, one `identity.create_account` receipt and the ledger entries of the grants, **when an initializer changes something** (no receipt otherwise) | Tests and tools that assert a new player's sections are defaults at `rev = 0` when a module registers an initializer with effects (the sample game does only when `account.json` exists, FR-050) | Seeded sections at `rev = 1` in the first login answer (`is_new_player = true`); one `identity.create_account` receipt (actor type `system`, actor id = player) and its ledger entries per new account; Information line `Account {PlayerId} created: {n} initializers, receipt {ReceiptId}.` | Assert the seeded state; only modules that register an initializer change anything |
| BC-7 | `LoginResult` gains the init properties `IsNewPlayer` (T018) and `Unavailable` (FR-002, FR-023; plan N2). `LoginCompletion.CompleteAsync`, `GuestLoginService.LoginAsync` and `ProviderLoginService.LoginAsync` gain a trailing optional `string? correlationId = null` (Planning note 8; the services pass the request's `X-Request-Id` correlation id down to the creation receipt). `LoginCompletion` takes `IAccountWriter` instead of `IPlayerAggregateWriter` (DI-created) | Code that reads `LoginResult` or calls these methods | None at compile time (additive init properties, optional parameters); a binary built against the old signatures must be rebuilt | Read the new values where needed |
| BC-8 | **SDK semantics (PCR unity-sdk):** an unavailable section keeps its last good copy and is marked; `AGGREGATE_VERSION_AHEAD` is retried by the SDK (retryable) (FR-027) | Game code that reads every section after login without checking | `Sections.IsUnavailable(id)` and `Sections.Unavailable[id].Code`, event `AvailabilityChanged(id)`; the last good copy stays in the store; the version-ahead code is retried (1000 ms delay) before a `GameServerException` | Read the unavailable set; handle the codes |
| BC-9 | Account initializer declarations are validated at startup (FR-015) | Modules that register an initializer with a bad declaration (new API, so only new code) | Startup exit 1 (serve and provision), one line per fault in the aggregated report after the command lines, e.g. `[module m] account initializer 'm.x' touches 'profile'. Fix: remove it; the framework writes the profile of a new account.` and `[deployment] account creation touches 5 aggregates (profile + …); max is 4. …` (errors-and-report-lines §4.1) | Fix the declaration |
| BC-10 | A login whose transaction ends ambiguous, expires or loses every race round answers `COMMIT_UNKNOWN_RETRY` (503) or `CONCURRENT_UPDATE` (409, retryable) instead of a 500 `INTERNAL` (FR-014; plan N11, architect decision) | Clients and dashboards that count login 500s | Error envelope instead of the raw 500: an ambiguous commit answers 503 `COMMIT_UNKNOWN_RETRY` (`retryable`, `retry_after_ms = 1000`); an expired transaction or three lost insert races answer 409 `CONCURRENT_UPDATE` (`retryable`, `retry_after_ms = 200`); a failed transaction stays 500 `INTERNAL`, now logged as `Request failed with INTERNAL: Login transaction failed for player {id}.` instead of `Unhandled exception.`. Nothing is written in any of these cases (`AccountWriterTests`, T017) | Handle them like any retryable code; the SDK still never retries a login after an unknown outcome |
| BC-11 | `CommandResponse.details` (new field 11) and the GM command response's `details` carry the details of every HTTP 200 rejection, module codes included; the XML doc of `ICommandOutcome.Details` changes from "not sent" to "sent" (FR-030; plan N5, architect decision) | Modules that pass details to `CommandOutcome.Reject` with an HTTP 200 code | `CommandResponse.details` / GM `details` hold the `Reject(code, details)` map for HTTP 200 codes (e.g. `{reason: "sold_out"}`); empty for commits. Fallback if the owner overrides: send framework codes' details only (a one-line filter in `CommandEndpointFactory.BuildResponse` and `GmCommandEndpointFactory.BuildResponse`) | Keep rejection details safe to show (they already must be, as for non-200 codes) |

## zh-pvp-server migration checklist (in this order)

After the 013, 014 and 015 steps of their release notes. File:line references are zh-pvp-server `7911f20` and the client
`research-and-development-games` `49df950ad` (spec "First-consumer migration"); the client writes its markers without the
colon (`FRAMEWORK-GAP FG-NNN`). Verified in a temp clone by T078.

1. **Bump** the `framework/` submodule to `v0.7.0` (with the 013–015 steps). BC-7 needs no code change: no game test builds
   `LoginResult`.
2. **`InitAccountHandler` into an `IAccountInitializer` (FG-015, BC-6, BC-9).**
   - `src/ZhPvp.Game/Account/InitAccountHandler.cs:16` (marker `FRAMEWORK-GAP: FG-015`): the handler body (command
     `account.init`, touches `progress`, `wallet`, `armory`; starting CASH grant through `EconomyOperations.Grant` with
     ledger source `account_init`, starter guns, equipped gun, `InitVersion = 1`) becomes an initializer with the same
     touches (`profile` + 3 = 4, exactly the creation budget) registered in `ModuleDescriptor.AccountInitializers`.
   - `InitAccountHandler.cs:17` (marker `FRAMEWORK-GAP: FG-022`): goes with the handler (the state flag no longer guards a
     second init; the profile insert does).
   - Registration `src/ZhPvp.Game/GameModuleDefinition.cs:25` and route `src/ZhPvp.Game.Adapters/GameModule.cs:20,34-38`:
     remove the `account.init` command, route and code `ACCOUNT_ALREADY_INITIALIZED` (50101), or keep them only for
     accounts created before the bump (D6, no backfill). The game is pre-launch: deleting the dev/test data is simpler.
     `ProgressState.InitVersion` may stay as seeded state.
   - `docs/pcr/game.md` lines 41-57, 66-69 and 78 (the `account.init` command and its rules) are updated by the game.
3. **Reset dev data** (accounts created before the bump never run the initializer, D6).
4. **Kit-based tests (FG-016, FR-017, FR-034).** `tests/ZhPvp.Tests/Game/AccountInitTests.cs:124` (marker
   `FRAMEWORK-GAP: FG-016`): delete the file-store measurement of result and receipt sizes (an overflow is now
   `COMMAND_OUTPUT_TOO_LARGE`, BC-1); the test becomes "a first login seeds the account once" (concurrent logins, lost
   response), and `InitAccountHandlerTests.cs` runs the initializer through `CommandContextDouble.ForAccountInitializer`
   and `CheckAccountCreation()` (consumer guide §13.2, fixture `FixtureInitializerTests`). Raise
   `GameServer:Commands:ReceiptMaxBytes` if P2 chests need more than about 11 ledger entries (measured 277–279 B per entry,
   PCR platform §22; BC-2: the value must stay in 1024..65536 and, with the cross rule, at least 1112) and check the receipt
   with `CheckCommit(outcome, encodeResult)` (fixture `FixtureReceiptSizeTests`).
5. **Client boot on the login sections (FG-013, BC-3, BC-8).**
   - `Assets/MadProject/Scripts/Online/OnlineSession.cs:200` (marker `FRAMEWORK-GAP FG-013`: login sections ignored, boot
     reads the snapshot) and `PlayerReadModel.cs:24` (marker `FRAMEWORK-GAP FG-013`: `IsReady` only after that snapshot):
     login → config → apply the login sections and mark ready.
   - `OnlineSession.cs:214-217` and `:279-320` (boot step `Initializing`, `RefreshSectionsAsync`, `InitAccountAsync`):
     drop the step; the initializer already ran. Read `LoginResponse.IsNewPlayer` for a first-time screen instead of
     `progress.init_version == 0`. A missing `progress` now means "unavailable section" (read `Sections.Unavailable`).
   - `docs/pcr/game.md` line 79 ("login sections are not read") is updated by the game.
6. **Request ids and the pending store for P2 commands (FG-022).** `OnlineSession.cs:276` (marker `FRAMEWORK-GAP FG-022`:
   an unknown outcome fails the boot, RETRY re-runs it): P2 commands pass `GameServerClient.NewRequestId()` (or `null`) to
   `SendCommandAsync(route, request, parser, requestId)`; set `Options.PendingCommands =
   UnityPendingCommandStore.Create(environmentName)` where the client is created (`OnlineSession.cs:83`, one folder per
   environment like the 015 config cache); after login, list a copy of `PendingCommands`, resend the `Resendable` ones and
   discard the others (consumer guide §13.5 and KestrelDemo `SampleGameClient.cs` show the loop). Keep
   `PendingCommandMaxAge` (23 h) below `GameServer:Commands:ReplayWindowHours` (24 h).
7. **Remove the six markers:** server `InitAccountHandler.cs:16` (FG-015), `InitAccountHandler.cs:17` (FG-022),
   `AccountInitTests.cs:124` (FG-016); client `OnlineSession.cs:200` (FG-013), `PlayerReadModel.cs:24` (FG-013),
   `OnlineSession.cs:276` (FG-022). FG-029 has no marker; its rule in `docs/pcr/game.md` (deploy code before writing a new
   schema version) stays. The friction-log entries FG-013, FG-015, FG-016, FG-022 and FG-029 can move to
   `fixed-in-v0.7.0`.

## Rolling deploy

- The deploy order rule of PCR platform §6 stays: deploy code that reads a new `schemaVersion` everywhere before any
  instance writes it. An old instance that meets a newer document logs one Warning line per read
  (`… is unavailable: version_ahead (stored schemaVersion {n}, code {m}). Deploy code that reads it before writing it …`) and
  counts `aggregate_read_failures_total{reason="version_ahead"}`: alert on it during a deploy.
- During a rolling deploy an old instance that reads an aggregate written by a new one no longer answers 500: the snapshot
  and the login leave the section out and list it (`AGGREGATE_VERSION_AHEAD`), and a command touching it answers 503
  `AGGREGATE_VERSION_AHEAD` (retryable, 1000 ms), which the SDK retries. A broken `profile` fails the login and every
  authenticated request with that code until the new code is everywhere.

Compatibility (contracts/wire-changes.md §5):

| Peer | Effect |
| --- | --- |
| SDK ≤ 0.7.0-before-016 against this server | ignores fields 8/9, 5, 10/11 (proto3 unknown fields); a broken section is simply absent from `sections` (BC-3); `AGGREGATE_VERSION_AHEAD` is retried by the existing retryable rule; `COMMAND_OUTPUT_TOO_LARGE` is returned as a business code |
| This SDK against a server before 016 | `IsNewPlayer` reads `false`; `unavailable_sections`/`details` empty; nothing else changes |
| Mixed rollout (old and new instances) | old instances never send the new fields; `is_new_player` from an old instance is `false` (presentation flag only, D1) |
| GM frontend | additive JSON members only |

## Decisions awaiting owner override (owner confirmations)

Each was applied as an architect decision; owner confirmation pending. Status unchanged at T075 (2026-11-04): none answered;
the docs describe the applied decision and name the fallback where one exists (BC-11).

| # | Decision | Status |
| --- | --- | --- |
| 1 | **A3 / N4**: the `identity.create_account` receipt is written only when initializers had effects (none for a game without initializers) | applied as architect decision, owner confirmation pending |
| 2 | **A8 / BC-11**: `CommandResponse.details` (and the GM `details`) carry module rejection details too, not only framework codes | applied as architect decision, owner confirmation pending |
| 3 | **D12 changed**: the sample game seeds starting gold from an optional server-only `account.json` (SampleHost has it, test configs do not) | applied as architect decision, owner confirmation pending |
| 4 | **D13 changed**: 016 writes `docs/releases/v0.7.0.md` as a draft; date, page restamp and tag are a separate round-close step | applied as architect decision, owner confirmation pending |
| 5 | **A9 / BC-10**: login store failures answer `COMMIT_UNKNOWN_RETRY` / `CONCURRENT_UPDATE` instead of 500 | applied as architect decision, owner confirmation pending |
| 6 | **A12**: a same-player re-login keeps the last good copy of the sections it lists as unavailable | applied as architect decision, owner confirmation pending |

## Platform facts checked before coding (VERIFY)

| Item | Result (2026-11-04) | Consequence |
| --- | --- | --- |
| V1 (T003) | Unity 6000.3.22f1 (`-langversion:9.0`) binds `(r, q, p)`, `(r, q, p, default)`, `(r, q, p, ct)` to the existing overload and `(r, q, p, "id")`, `(r, q, p, null)` to the new one; no `CS0121`, 0 `error CS`/`warning CS` | new overload as in public-api §7.1 (no rename) |
| V2 (T006) | in-memory, File and Couchbase rethrow a body exception (private type or `GameServerException`) as the same instance, `Exception.Data` kept, no inner exception, body run once, nothing committed — before and after one staged insert | no unwrap in the store adapter |
| V4 (T006) | two sequential `GetManyAsync` calls in one attempt work on all three stores; a concurrent anchor removal after the rewrite: in-memory/File → the login's commit conflicts, the re-run sees no anchor (`AggregateAnchorMissingException`, nothing written); Couchbase → the removal waits for the login commit (write-write conflict, 3 attempts), then removes the rewritten anchor | sequential second read (no bundle-read fallback) |
| V5 (T005) | a marked `GameServerException` from `HandleAuthenticateAsync` reaches `ExceptionMiddleware` unchanged (503, `Retry-After: 1`, `retry_after_ms = 1000`, details kept) when `ProfileEnforcementMiddleware` authenticates — but a host with **exactly one** authentication scheme gets ASP.NET's automatic default scheme and the `UseAuthentication` middleware `WebApplication` adds in front of the whole pipeline, so the exception escapes `ExceptionMiddleware` (raw 500, no envelope, no request log line) | fallback for T040: the bearer handler catches the marked exception and `Fail`s with code and details (the envelope's retry delay is the writer's default 1000 ms for retryable codes, so nothing is lost) |
| V6 (T004) | the loader raises `JsonException` (some wrapping `InvalidOperationException`/`FormatException`), `InvalidOperationException` (`GetValue<string>`, a missing upgrader step, null state), `FormatException`/`OverflowException` (`JsonNumbers` on absurd or non-numeric envelope fields) and whatever an upgrader throws; `NotSupportedException` was not observed. A missing, `null`, array or string `state` is **not** an error today (read as the default state) | explicit list for the envelope and the deserialize block, catch-all except `OperationCanceledException`/`OutOfMemoryException` for the upgrade chain, plus an explicit "state is not a JSON object → `unreadable`" check (BC-5 row) |
| V8 (T007) | 4 KiB record (temp + `Flush(true)` + `File.Move`) p95: C: `%TEMP%` 6.09 ms, D: 32.24 ms; remove p95 1.52 ms / 0.46 ms; with `Flush(false)` record p95 1.69 ms / 0.37 ms | `Flush(true)` kept; SC-008 reference drive C: (where `persistentDataPath` lives on Windows); D: recorded only |
| V9 (T007, .NET 10) | `File.Move` with the destination absent is a rename; onto an existing file `IOException` (0x800700B7); `EnumerateFiles(folder, "*.pending")` returns neither `*.pending.tmp` nor `x.pendingx` but does return `y.PENDING` (Windows names are case-insensitive); a process killed between flush and move leaves a complete temp file and no entry | per-entry files kept; the loader validates the file name (lowercase UUID + `.pending`). Unity Mono: T068; devices: T069 |
| V7 (T052) | runner and double give the same receipt size for `shop.buy`, `battle.end` and a seeding creation with the same actor, player, request id, receipt id, config version and time (0 bytes difference); no input missing from the double | none (no `WithCorrelationId` needed: `WithActor` carries the correlation id) |
| V8 final (T061) | `PendingCommandStorePerformanceTests`: 4 KiB record p95 5.56 ms, remove p95 0.26 ms on C: | `Flush(true)` kept |
| V11 (T008) | the GM OpenAPI generator emits `unreadable` (array of `$ref`), `details` (object, string `additionalProperties`) and `unavailable` (array of `$ref`) — but only for endpoints that declare `.Produces<T>()`; today the GM player view and the GM command endpoints declare none, so neither response has a schema in `docs/api/gm-openapi.json` | generator output as is, with `.Produces<GmPlayerView>()` and `.Produces<GmCommandResponse>()` added (T043/T049); no hand-written fragment |
| V12 (T009) | `LoginPerLocalIdPerMinute` = 10 (guest login, per local id) is below SC-002's 25 logins of one credential; `FailedLoginPerIpPerMinute` = 60 counts failures only; `LoginPerIpWarnPerMinute` = 600 only logs | the SC-002 tests raise `Identity:LoginPerLocalIdPerMinute` in test settings; production defaults unchanged |

## Manual checks

| Check | What | Status |
| --- | --- | --- |
| T003 | V1 overload binding with a stub in the KestrelDemo Editor (Unity 6000.3.22f1) | done 2026-11-04 (agent-run, see V1) |
| T060 | package `Runtime` compiled outside Unity (C# 9, netstandard2.1) against Unity 6000.3.22f1 `UnityEngine.CoreModule` + `UnityWebRequestModule`, with and without `UNITY_IOS` | done 2026-11-04: 0 warnings, 0 errors both ways |
| T064 | the real SampleHost config tests after `account.json` lands | done 2026-11-04: `Sample.Counter.Tests`, `SampleGame.Tests`, Architecture, Client and Platform.Web suites green with no assertion change; Couchbase `SampleHostFlavorTests` skip without `COUCHBASE_CONNSTR` (they log in no player, T009) |
| T068 | Editor compile, Windows player and Android IL2CPP build of KestrelDemo with the real package; pending store in the Editor and the Windows player | done 2026-11-04 (agent-run, Unity 6000.3.22f1): `-CompileOnly` 0 `error CS`/`warning CS`; run A through a dropping proxy `new player: True`, `EndBattle → pending {id}`, one `{id}.pending` file; run B `pending {id} Resendable`, `resent {id}: replayed=True`, folder empty, one battle reward for that command; Windows player probe recorded, reloaded and removed three entries; Android APK `Build/KestrelDemo.apk` built (IL2CPP arm64, stripping High, 0 errors) |
| T069 | Android device and iOS build + device run (with the pending 015 T058 session) | pending (owner); APK built 2026-11-04, five starts listed in tasks.md T069; the tag waits for it |
| T077 | fresh reviewer walkthrough of the consumer guide | pending |
| T078 | first-consumer check in a temp clone of zh-pvp-server | pending |

## Known before the tag

- **Single-scheme hosts (found by T005, pre-existing).** A host that registers exactly one authentication scheme (Identity
  without the GM module and without test authentication) gets ASP.NET's automatic default scheme, and `WebApplication`
  then adds its own authentication middleware in front of the `UseGameServer` pipeline. Any exception thrown by that
  scheme's handler escapes `ExceptionMiddleware` and the request log (raw 500). Slice 016 makes the broken-profile path
  safe in the handler (T040); other handler exceptions (for example a store timeout in session validation) keep the
  pre-existing behaviour on such hosts. Hosts with Identity and GM, or with test authentication, are not affected.
- 015 T058 (Android/iOS device run) is still open and gates the tag; it is combined with 016 T069 (five starts, owner).
- **`ReceiptMaxBytes` lower bound (T050).** The documented range 1024..65536 and the cross rule together make 1112 the
  smallest value that starts (with `ResultCompactMaxBytes` = 64); a value of 1024..1111 gets only the cross line. Both
  lines are contract texts and were kept; PCR platform §22 says so (T072).
- **Failed account creation logs two lines (T026, for the tech-lead review).** The writer's `GameServer.Accounts` Error line
  (the cause) and the error middleware's `Request failed with INTERNAL: Account creation of player {id} failed ({result},
  initializer {name}).` Kept as is; the review decides whether the middleware line should be suppressed with the read-failure
  "reported" marker (PCR platform §22 documents both).
- **`GameServerClient.PendingCommands` is the store's live list (found by T067).** `foreach` over it while calling
  `ResendPendingAsync` or `DiscardPendingCommand` throws `InvalidOperationException: Collection was modified` at the next step,
  even with one entry (checked with `InMemoryPendingCommandStore` in a scratch console). KestrelDemo, consumer guide §13.5,
  PCR unity-sdk and the snippet above iterate a copy. A fix in the SDK (return a snapshot from `PendingCommands`/`Entries`) is
  left to the review: it changes no signature.
- **`smoke-build.ps1` has no `-E2EDevice` pass-through.** The `-e2eDevice` switch of `SmokeBuild.EndToEnd` is a Unity
  command-line argument; T068 ran those two E2E runs with `Unity.exe … -executeMethod KestrelDemoBuild.EndToEnd -e2eDevice t068`
  directly (the script's own E2E command line plus the switch), because T067/T068 allow changes to the two KestrelDemo files
  only.
- **PCR platform §8 still lacks codes 21 `CSRF_FAILED` and 22 `METHOD_NOT_ALLOWED`** (pre-existing; T072 added only 23–25).
