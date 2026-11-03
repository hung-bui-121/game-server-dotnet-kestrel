# Feature Specification: Login and command robustness — new-player flag, account initializer, degraded reads of a broken aggregate, typed command-size rejections, caller-owned request ids (slice 016)

**Feature Branch**: `016-login-command`

**Created**: 2026-11-04

**Status**: Draft

**Input**: Owner-approved improvement round (2026-11-03) from the first consumer game `E:\ProjGits\zh-pvp-server` (submodule
`framework/` pinned to tag `v0.6.0`; Unity client `E:\ProjGits\research-and-development-games`), friction log
`docs/framework-feedback/FRICTION_LOG.md` of that repo. Four sequential slices, one tag `v0.7.0` at the end of the round;
013 (group A), 014 (group B) and 015 (group C) are merged into `main`. This slice is group D, the last one of the round, in
this priority order:

- **FG-013**: login always carries the full snapshot, and the response does not say whether this login created the
  player. The first consumer ignores the login sections and reads the snapshot again to find out.
- **FG-015**: there is no hook that runs when an account is created. Aggregate defaults are constants
  (`createDefault: () => new T()`), so a game cannot give a new player a starting balance from config. The first consumer
  added a command `account.init` and a state flag.
- **FG-029**: one aggregate that does not load (document written by newer code, JSON that no longer deserializes) makes
  the whole snapshot and the whole login answer 500. The login has already raised the session epoch, so every retry
  raises it again and still fails.
- **FG-016**: the command limits are tight for commands with many rewards, they are not documented as settings, and a
  result or receipt over its byte limit is a 500 `INTERNAL` found only at run time.
- **FG-022**: the SDK creates the request id inside each `SendCommandAsync` call. The game cannot pass one in or read it
  out, so when the app dies after the server committed and before the response arrived, the next app start sends a new
  request and the command runs twice.

Decisions taken by default in this spec are marked **[DEFAULT]** and listed again under Assumptions, so the owner can
override them.

## Scope note

- Verified on `main` = `5904b74` (2026-11-04, branch `016-login-command` created from it). The claims of the five entries
  still hold. Some lines have moved (see "Friction claims re-checked" at the end).
- **Already in place (P0–015):**
  - Login (IDN-07, IDN-08): `GuestLoginService` inserts the mapping `identities/{mappingKey}` outside a transaction, then
    `LoginCompletion.CompleteAsync` reads the profile, raises `profile.sessionEpoch` through `IPlayerAggregateWriter` (one
    transaction; a missing profile is created there, anchored on the mapping, research R9 of 010), creates the session, and
    reads every client-visible aggregate (`PlayerSnapshotReader.ReadAsync(playerId, null)`). Provider login
    (`ProviderLoginService`) ends in the same `CompleteAsync`. The analytics `login` record already has
    `isNewUser = (profile was null)` (012).
  - `LoginResponse` fields 1–7 (`client_config_version` = 7 since 015). `SnapshotRequest.sections` already selects
    sections; `SnapshotResponse` fields 1–4. `CommandResponse` fields 1–9. `ErrorEnvelope` fields 1–10.
  - A client-visible aggregate without a wire mapper already fails startup (`GameServerApplicationExtensions.cs:402`, 014).
    What remains for FG-029 is a mapper that throws for one stored state at run time.
  - Command runner (CMD-01..09): receipt id = hash(actorId, playerId, operation, requestId); the player actor id is the
    player id, so a request id replays across sessions and app restarts within the 24 h window (CMD-07), as long as the
    body bytes are identical (CMD-02). `CommitRules` (013) holds the code, audit, aggregate-size and result-size checks
    shared by the runner and the test kit. `AGGREGATE_TOO_LARGE` is already a typed rejection (HTTP 200, nothing
    commits).
  - `GameServerOptions.Commands` binds `GameServer:Commands:{ReplayWindowHours, PerPlayerQueueLimit, ReceiptMaxBytes,
    ResultCompactMaxBytes, MaxAggregatesPerGame}` without range checks. `CommandDescriptor.MaxTouches = 4` and
    `MaxClaimMarkersLimit = 2` are constants (DAT-09).
  - SDK (`com.gameserver.client` 0.7.0): `SendCommandAsync` serializes once and resends the same bytes with one request
    id per call (`Guid.NewGuid()` in `SendAsync`); login is never retried after a lost attempt (`RetryMode.RejectedOnly`).
    `PersistentConfigCache` + `CacheFileOps` (015) give the atomic-file pattern (temp file, flush to device, replace;
    damaged file = empty with a reason; names never become paths). `Runtime/Core` is C# 9 / `netstandard2.1` without
    UnityEngine.
  - Round rules from 013–015 that this slice follows: routes are declared in the route catalog (014); every startup fault
    joins one aggregated report (011 FR-009, 015); proto changes are additive only (API-03); the test kit has no
    unit-test-framework dependency (013); framework error codes live in the core catalog, games in 50000–89999 (013).
- **Gaps this slice closes:**
  - `LoginResponse` has no new-player flag.
  - No account-creation hook; the profile is the only aggregate a first login writes.
  - `AggregateDocumentSerializer.Load` throws (`INTERNAL` for a newer schema, a raw `JsonException` or
    `InvalidOperationException` otherwise) and every reader propagates it: snapshot, login, GM player view, commands,
    replays, `ReadOneAsync` callers (profile cache, GM target validator). Login reads the snapshot after the epoch write.
  - Result and receipt over their limits throw `InvalidOperationException` in the runner (500). The settings are
    undocumented, unvalidated, and the test kit does not check the receipt size.
  - The SDK has no caller-supplied request id, does not expose the id it used, and keeps nothing across app restarts.
- **Not included:** login without sections or with known revisions (D2), declarative starting balances in
  `resources.json`, ledger entries with several deltas, a configurable `MaxTouches`, re-running an initializer for
  accounts created before it existed, and the deferred friction entries FG-005, FG-008, FG-009, FG-014, FG-017, FG-020,
  FG-030, FG-031 (see Out of Scope).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The login tells the client that the player is new, and its sections are the one read the client needs (Priority: P1) — FG-013

A client logs in (guest or provider) and learns from the login response whether this login created the player. With the
initializer of story 2 and the degraded read of story 3, the sections of the same response already hold the new player's
starting state and every readable section, so the client applies them and does not read the snapshot again at boot.

**Why this priority**: it is the first thing every game does with the framework, and the first consumer pays one extra
read of all player data per boot (login sections ignored, `POST /api/v1/player/snapshot` right after) only to find out
whether the player is new. The flag is one additive field; the rest of the gain comes from stories 2 and 3.

**Independent Test**:
- Log in a new guest, then the same guest again, then a new provider identity, then that identity again, then a
  confirmed `switch_account` into a new account. Check the flag of each response and that it equals the analytics record's
  `isNewUser`.
- Simulate a lost response of a first login (server committed, client retries the login): check that the retry reports
  `false`, as documented.
- With an SDK client, check that the flag is readable from the value `LoginGuestAsync` / `LoginWithProviderAsync` return.

**Acceptance Scenarios**:

1. **Given** a credential that maps to no player, **When** the login succeeds, **Then** `LoginResponse` carries the
   new-player flag `true`, a session, and the sections of the new player (including what the account initializers of
   story 2 wrote).
2. **Given** a player that already exists, **When** it logs in (guest, provider, or a takeover from another device),
   **Then** the flag is `false`.
3. **Given** a first login whose response was lost, **When** the client logs in again with the same credential, **Then**
   the flag is `false` (the account exists now), and the PCR states that the flag means "this login created the player",
   not "the player has never seen the game". Durable onboarding state belongs in a game aggregate seeded by an initializer.
4. **Given** a login, **When** the analytics `login` record is written, **Then** its `isNewUser` equals the flag of the
   response (one rule, not two).
5. **Given** an SDK from before this slice, **When** it logs in to this server, **Then** it ignores the unknown field and
   works as today (additive, API-03).

---

### User Story 2 - A new account gets its starting state in the transaction that creates it (Priority: P2) — FG-015

A game or module registers an account initializer. When a login creates a player, the framework runs every registered
initializer inside the transaction that creates the profile. An initializer works like a command handler on an
`ICommandContext`: it reads the config and catalogs, grants resources through Economy (ledgered), and sets the starting
state of the aggregates it declares. Either the account exists with its starting state, or nothing exists and the next
login tries again. A retried or concurrent first login never grants twice.

**Why this priority**: every game with a starting state hits it (the first consumer rated it "high"). Without it, each game
adds an init command, a state flag, a client trigger and a "treat already-initialized as success" rule, and a player whose
app dies between login and init exists without a starting state until the next boot.

**Independent Test**:
- Register an initializer that grants starting gold from config through `EconomyOperations.Grant` and sets a game
  aggregate. Log in a new guest: check the balance, the single ledger entry (with its source type), the game aggregate, the
  receipt of the creation, and that the login sections show them.
- Log in the same guest again, send 20 concurrent first logins for one new credential, and retry a first login whose
  response was lost. Check that the ledger has exactly one entry and the balance equals the starting amount.
- Inject a store failure at each step of the creation transaction and an initializer that rejects, then one that throws.
  Check that neither a profile without starting state nor starting state without a profile exists, and that the next login
  creates both.
- Start hosts with an initializer whose declared aggregates are unknown, or whose aggregates plus `profile` exceed 4.
  Check one startup line each in the aggregated report.
- Run the initializer host-free with the test kit's command context double and check the commit rules.

**Acceptance Scenarios**:

1. **Given** registered initializers and a credential that maps to no player, **When** the login creates the player,
   **Then** the profile, every aggregate the initializers set, their ledger entries (outbox of one receipt) and the receipt
   commit in one transaction (DAT-04, ECO-04, ECO-06), and the login response shows the new state.
2. **Given** an existing player, **When** it logs in, **Then** no initializer runs, whatever its state.
3. **Given** several first logins for the same credential at once, or a retry of a first login whose response was lost,
   **When** they complete, **Then** exactly one creation committed and every grant was applied once.
4. **Given** an initializer that rejects (an `ErrorCode`) or throws, or a store failure during the creation transaction,
   **When** the login runs, **Then** nothing of the creation commits (no profile, no seeded aggregate, no receipt, no
   ledger entry), the login fails with an error envelope, the failure is logged with the initializer's name and counted,
   and the next login with the same credential runs the creation again.
5. **Given** initializers that together declare unknown aggregates, `profile`, or more aggregates than the transaction
   budget allows (`profile` + the union of declared aggregates ≤ 4, DAT-09), **When** the host starts, **Then** startup
   fails with one line per fault naming the module and the initializer, together with every other startup fault.
6. **Given** an initializer that writes an aggregate it did not declare, adds an audit entry, or makes a result or
   document too large, **When** it runs, **Then** the same commit rules as for a command apply (`CommitRules`).
7. **Given** accounts created before an initializer was registered (or before this slice), **When** they log in, **Then**
   no initializer runs for them (D6). The guide says how a game covers them (a command of its own, or a data reset before
   launch).
8. **Given** a consumer test, **When** it runs an initializer over the test kit's command context double, **Then** it gets
   the same commit checks as for a command handler, with no host or store.

---

### User Story 3 - One broken aggregate does not take down the snapshot or the login (Priority: P3) — FG-029

When one aggregate of a player cannot be read (its document was written by a newer schema, an upgrader throws, the JSON no
longer deserializes into the state type, or its wire mapper throws for that state), the snapshot and the login still
answer with every other section, list the broken one with a typed code, and log and count the failure. A command that
touches or reads the broken aggregate is refused with the same typed code instead of a 500. The GM player view shows the
other aggregates and the read error. The broken document is never written over by a read.

**Why this priority**: a rolling deploy or a rollback that crosses a schema bump, or one bad document, locks the player out
of the game today, and every login retry raises the session epoch again. The fix is contained (one read path) and makes the
failure visible (code, log, metric) instead of an anonymous "Unhandled exception.".

**Independent Test**:
- For each failure kind (version ahead, upgrader throws, state does not deserialize, mapper throws), seed one broken
  aggregate for a player, then call snapshot (all sections, and an explicit list that includes the broken one), login,
  a command that touches it, a command that only reads it, a command that touches neither, the replay of a command
  committed before the damage, the GM player view and `PlayerSnapshotReader.ReadOneAsync`.
- Check: no 500 for snapshot, login, GM view and the untouched command; the other sections are identical to an undamaged
  read; the broken one is listed with its code; the touching commands get the typed code and change nothing; one log line
  and one metric increment per failed read; the session epoch is raised once per successful login.
- Damage `profile` itself and check that login and authenticated requests fail with the typed code (not 500).
- With an SDK client, check the unavailable section is exposed and the last good copy, if any, is kept.

**Acceptance Scenarios**:

1. **Given** a player whose `wallet` document has a `schemaVersion` newer than the code, **When** the player reads the
   snapshot, **Then** the response has every other section and lists `wallet` as unavailable with code
   `AGGREGATE_VERSION_AHEAD`. The response is HTTP 200.
2. **Given** a player whose aggregate state no longer deserializes (or whose upgrader throws, or whose wire mapper throws
   for that state), **When** the player reads the snapshot or logs in, **Then** the response has every other section and
   lists that aggregate with code `AGGREGATE_UNREADABLE`. The login creates its session and raises the epoch exactly once.
3. **Given** a broken aggregate, **When** a command touches or reads it, **Then** the command is refused with an error
   envelope carrying that code and details naming the aggregate (no receipt, nothing written), and a command that touches
   neither runs normally.
4. **Given** a command committed before the aggregate broke, **When** the client retries it within the replay window,
   **Then** the replay returns the committed result and code, and lists the broken aggregate as unavailable instead of
   failing.
5. **Given** a broken aggregate, **When** a GM reads the player, **Then** the view shows every other aggregate and the
   broken one with its code and reason, not a 500.
6. **Given** a broken `profile`, **When** the player logs in or calls any player endpoint, **Then** the answer is an error
   envelope with the typed code (the profile carries the session epoch and access checks, so it cannot be skipped).
7. **Given** any failed read, **When** it happens, **Then** one log entry names the aggregate, the player, the reason and
   the exception type (never the state content), the metric counts it by aggregate and reason, and the client message
   is the catalog's safe message (API-04).
8. **Given** an SDK from before this slice, **When** a section is unavailable, **Then** the section is simply absent from
   `sections` (never present with an empty payload that would parse as a default state).

---

### User Story 4 - Command limits are documented settings, and going over one is a typed rejection (Priority: P4) — FG-016

The command limits a game designer hits (receipt bytes, result bytes, aggregates per game, replay window, per-player
queue, touches per command, claim markers) are listed in PCR platform with their meaning, their defaults, which ones are
settings and their allowed ranges. A setting out of range fails startup. A command whose result or receipt would exceed
its limit is refused with a typed business code and nothing commits, like `AGGREGATE_TOO_LARGE`. A consumer test can check
the receipt size of a handler's outcome host-free, with the same number the runner computes.

**Why this priority**: the first consumer's next phase has `battle.*` and `chest.*` commands with many rewards. Today the
limit is found as a 500 in production, and the game writes one measuring test per command. Raising `ReceiptMaxBytes` is
already possible but undocumented.

**Independent Test**:
- Run a sample command whose result is one byte over `ResultCompactMaxBytes` and one whose receipt (many ledger entries)
  is one byte over `ReceiptMaxBytes`, and the same commands at exactly the limit. Check the code, the details, that nothing
  committed, the metric and the log line.
- Raise `GameServer:Commands:ReceiptMaxBytes` and check that the same command commits.
- Start hosts with each command setting below and above its range, and with a result limit that cannot fit in the receipt
  limit. Check one startup line each.
- With the test kit's command context double, check that the receipt size it reports equals the size the runner measured
  for the same outcome (sample commands, fixture command).

**Acceptance Scenarios**:

1. **Given** a command whose encoded result exceeds `ResultCompactMaxBytes`, **When** it runs, **Then** the response is
   HTTP 200 with code `COMMAND_OUTPUT_TOO_LARGE` and details naming the part (`result`), its size and the limit; nothing
   commits; no receipt is written.
2. **Given** a command whose receipt (ledger and other outbox entries, claims, aggregates, result) exceeds
   `ReceiptMaxBytes`, **When** it runs, **Then** the same code is returned with part `receipt`, and nothing commits.
3. **Given** either case, **When** it happens, **Then** one Error log names the operation, the part, the size and the
   limit, and the metric counts it by operation and part. A retry with the same request id is executed again (a rejection
   has no receipt, PCR §2) and gets the same code.
4. **Given** `GameServer:Commands` settings, **When** the host starts, **Then** each value outside its documented range,
   and a result limit whose base64 form cannot fit in the receipt limit, adds one line to the aggregated startup report.
5. **Given** PCR platform, **When** a designer reads the limits, **Then** each limit is listed with its default, whether it
   is a setting, its range, what it costs (Couchbase document and transaction size), and a worked example: how many
   ledger entries fit in a receipt at the default and how to raise it.
6. **Given** a consumer test with the command context double, **When** it checks a handler outcome, **Then** it gets the
   receipt-size problem the runner would report, measured the same way.

---

### User Story 5 - A command sent before the app died is replayed, not run twice, after the restart (Priority: P5) — FG-022

A game passes its own request id to `SendCommandAsync`, or lets the SDK keep the commands whose outcome is unknown in a
pending-command store on the device. After an app restart and a new login, the game lists the pending commands and resends
each one with its original request id and its original bytes. The server replays the committed ones (same result, same
receipt) and executes the ones that never arrived. A command whose outcome is known (any server answer that is final) is
removed from the store.

**Why this priority**: it removes the main reason games must make every command idempotent by state, and it is SDK-only on
top of the existing replay contract (CMD-02, CMD-07). The first consumer's P2 commands (battle end, chests) need it, but
state idempotency remains good practice, so it comes after the server-side fixes.

**Independent Test**:
- In SDK tests outside Unity, against the sample host: send a command with a caller-supplied id, kill the client (drop the
  response after the server committed), create a new client on the same pending folder (simulated restart), log in,
  resend: check `replayed = true`, the same receipt id, and one ledger entry.
- Repeat with the request dropped before it reached the server: the resend executes once.
- Inject a failure at each step of a pending-store save and load truncated, corrupt, unknown-format and unreadable files,
  as for the 015 config cache.
- Check that an entry for another player, and an entry older than the configured maximum age, are not resent.
- Check that every final answer removes the entry, and that a retryable error or no answer keeps it.

**Acceptance Scenarios**:

1. **Given** a game that passes a request id, **When** it calls `SendCommandAsync` with it, **Then** every attempt of that
   call sends that id, the result exposes it, and the same id with the same bytes in a later call replays the committed
   command (CMD-02). An id that is not a UUID is refused by the SDK before any network call.
2. **Given** a client created with a pending-command store, **When** it sends a command, **Then** the route, the exact body
   bytes, the request id, the player id and the server time are stored before the first attempt leaves the device, and
   the entry is removed once the server gave a final answer (a command response with any code, or a non-retryable error).
3. **Given** a stored entry whose outcome is unknown (no response, cancellation after send, or a retryable error such as
   `COMMIT_UNKNOWN_RETRY` as the last answer), **When** the app restarts and the same player logs in, **Then** the entry is
   listed, and resending it sends the stored bytes with the stored id: the server replays it if it committed and executes
   it once otherwise.
4. **Given** a stored entry of another player, **When** the current player resends it, **Then** the SDK refuses. **Given**
   an entry older than the configured maximum age (default below the 24 h replay window), **When** it is listed, **Then**
   it is marked expired and the SDK does not resend it; the game decides from state and discards it.
5. **Given** a save in progress, **When** the process dies at any point of it, **Then** the next start loads either the
   previous entries or the new ones, never a partial entry. A damaged store file reads as empty with a reason, and a disk
   failure never fails the SDK call.
6. **Given** a client without a pending-command store and without a caller id, **When** it sends commands, **Then** the
   behaviour is exactly today's (one new id per call, nothing written to disk).
7. **Given** the Unity package, **When** it is compiled outside Unity (C# 9, `netstandard2.1`), **Then** the store builds
   with no UnityEngine reference; a Unity helper rooted at `Application.persistentDataPath` lives in `Runtime/Unity` under
   `#if UNITY_5_3_OR_NEWER`.

---

### User Story 6 - The samples, fixture and docs use the same paths, and the round's release notes are complete (Priority: P6) — FG-013, FG-015, FG-016, FG-022, FG-029

The sample game seeds a starting state through an initializer, the consumer fixture registers an initializer and tests it
with the test kit, the sample Unity client reads the new-player flag and uses caller-owned request ids with the pending
store, and the guides and PCRs describe the shipped behaviour. As the last slice of the round, the four release-note drafts
are merged into one `v0.7.0` release note.

**Why this priority**: the framework's own modules and samples are the reference examples, and docs must describe shipped
behaviour, so this story comes last.

**Independent Test**:
- Run the sample host and the consumer fixture suites; log in a new player in `KestrelDemo` (manual) and check the log.
- A reviewer follows the consumer guide to add an initializer, a large-reward command with a raised receipt limit, and a
  resend of pending commands after restart. The reviewer records every missing or wrong step.

**Acceptance Scenarios**:

1. **Given** the sample host, **When** a new player logs in, **Then** the sample game's initializer has seeded its
   starting state from declared config, with a ledger entry, and the sample tests cover it.
2. **Given** `docs/guides/consumer-guide.md`, `docs/guides/module-authoring.md`, `templates/module/README.md`,
   `docs/pcr/{platform,identity,economy,sample-game,unity-sdk}.md` and the Vietnamese pages `docs/html/01-gioi-thieu.html`
   and `02-them-command.html`, **When** a developer reads them, **Then** they describe this slice's behaviour, and no page
   still says that a newer document is a 500 or that an oversized receipt is a programming error answered with 500.
3. **Given** the round's drafts (`specs/01{3,4,5,6}-*/release-notes-draft.md`), **When** this slice closes, **Then**
   `docs/releases/v0.7.0.md` holds every fixed FG id, every breaking change of the round with its fix, and the
   first-consumer migration checklist in order.

---

### Edge Cases

**Login and new-player flag (FG-013)**

- **Lost response of a first login**: the account exists, the retry is a normal login (flag `false`, epoch + 1). The SDK
  already never retries a login by itself; the guide says the flag is for presentation (a welcome screen), and that a
  game that must not miss onboarding keeps it in its own state, seeded by an initializer (for example a tutorial step).
- **Provider login with `switch_account = true` into an identity that maps to no player**: a new player is created, the
  flag is `true`, initializers run. Answered `SWITCH_ACCOUNT` (not confirmed): nothing is created, as today.
- **Login racing the account purge** (creation anchor of 010): the purge removed the mapping, the creation transaction
  fails on the anchor, the login answers `ACCOUNT_DELETED`, and no initializer write survives (same transaction).
- **A new player whose initializer wrote nothing** (no initializer registered, or one that sets nothing): the flag is still
  `true`; the sections are the defaults at `rev = 0`, as today.
- **Store failure after the epoch write and before the sections are read** (timeout of the snapshot read): the login
  fails as today with a retryable error; a retry raises the epoch again. Accepted: the session of the failed login was
  never delivered, so no client loses a session. Only aggregate *read* failures degrade (story 3).

**Account initializer (FG-015)**

- **Two first logins race** (same device double-tap, two devices with the same guest credential): both find no profile;
  both run the creation transaction; one commits, the other conflicts on the profile insert and re-runs as a normal login
  (the writer's existing race rounds). Initializers run inside the transaction lambda, which may run more than once:
  like handlers they must be pure over `ICommandContext` (CMD-04), so a re-run never double-applies.
- **The initializer reads a catalog** (015): catalogs are built before traffic, so it gets them through DI like a handler.
- **The mapping exists but the profile does not** (the process died between the mapping insert and the creation, or the
  creation failed): the next login creates the profile and runs the initializers. This is the "nothing exists, the next
  login tries again" case of story 2.
- **An initializer grants a resource the catalog does not know, or over `maxQuantity`**: the grant rejects, the creation
  rolls back, the login fails and logs the initializer and code. With 015's startup rules, the usual cause (bad config) is
  found at startup instead.
- **Accounts created before the initializer** (legacy accounts, or accounts of a game that adds an initializer later):
  not initialized (D6). A game that needs them initialized keeps its own command for them (the first consumer's
  `account.init` already does this) or resets data before launch.
- **Account purge**: the seeded aggregates are ordinary `pa_*` documents and the creation's ledger entries are ordinary
  ledger entries; the purge job of 010 handles them as today.
- **Several initializers** (a framework module and a game): all run in module registration order, in the same
  transaction, over one context. Two initializers writing the same aggregate see each other's writes, in order.
- **Initializer touches `profile`**: refused at startup (the framework owns the profile write of the creation).
- **GM-created or imported players**: not covered; there is no such path today.

**Broken aggregate (FG-029)**

- **Rolling deploy across a schema bump**: an old instance reads a document written by a new one. Snapshot and login on
  the old instance list it as `AGGREGATE_VERSION_AHEAD`; commands touching it are refused with that code, which is
  retryable, so the SDK retries the same bytes (nothing committed) and a retry on a new instance succeeds.
- **Rollback to code older than stored documents**: the same as above, for as long as the rollback lasts. The PCR keeps
  the rule "deploy code that reads a schema before any instance writes it".
- **Broken `profile`**: no degradation is possible (it holds the session epoch, bans and deletion). Login and every
  authenticated request answer the typed code; the account purge job reports the step as failed, as for any read fault.
- **Broken aggregate in a command's `Reads` only**: the command is refused like for `Touches`, because a handler would
  otherwise run on a state it cannot see.
- **Replay of a committed command whose aggregate broke later**: the receipt is the record of what committed; the replay
  answers it and lists the broken aggregate as unavailable instead of putting it in `updates`.
- **Wire mapper throws after a command committed** (the new state of a touched aggregate cannot be mapped): the commit
  stands. The response carries the result and code, omits that section from `updates` and lists it as unavailable; the
  retry replays the same way. Today this is a 500 after the commit and every retry is a 500.
- **Every section is broken**: the snapshot answers HTTP 200 with no section and the unavailable list (a client can show
  a maintenance message instead of a crash).
- **An explicit section list that names a broken section**: the same degradation. An unknown or hidden section id is still
  400 `BAD_REQUEST`, as today.
- **A store error while reading** (timeout, unavailable): not a broken aggregate. It fails the request with the existing
  retryable codes; nothing degrades.
- **A hot player with a broken aggregate polls the snapshot**: one log entry per failed read. The log carries no state
  content, so the volume stays small; the metric is the signal ops watches.
- **Lazy upgrade of a broken document**: never written back by a read (no read path writes), so nothing is lost; the fix
  is a code fix or an ops repair of the document.

**Command size limits (FG-016)**

- **Exactly at the limit**: accepted (the check is "greater than").
- **The receipt is over the limit only because of outbox entries another module adds** (ledger, analytics, audit): the
  same rejection; the log names the operation, so the owner can raise the limit.
- **A GM command over the limit**: the same code in the GM command response (business rejection, nothing commits).
- **Receipts committed under a higher limit, read after the limit was lowered**: replays and receipt lookups never check
  the limit; only new commits do.
- **The receipt size depends on the request id, config version and player id lengths**: all are fixed-format, so the
  test kit computes the same size as the runner (story 4 scenario 6) when it is given the same config version and ids.
- **`MaxTouches` and claim markers**: stay constants (DAT-09). The initializer of story 2 counts against the same
  transaction budget.

**Caller-owned request ids (FG-022)**

- **The app dies before the pending entry is written**: the request has not left the device (the entry is written
  first), so nothing ran.
- **The app dies after the server answered and before the SDK removed the entry**: the next start lists it; resending
  replays the committed answer (or, for a rejection, which has no receipt, executes again and gets the same rejection
  unless state changed). The game sees the same outcome.
- **Resent after the replay window (24 h)**: the server would execute it as new. The SDK marks entries older than the
  configured maximum age as expired and does not resend them (D11).
- **Account switch on the device** (provider login into another account): entries of the previous player stay listed
  with their player id and are not resendable by the new player; the game discards them.
- **The same request id reused for different bytes**: the server answers `IDEMPOTENCY_PAYLOAD_MISMATCH` (409, CMD-02),
  a final answer, so the entry is removed and the caller gets the error.
- **Login and session calls**: never stored (login is not deduplicated by the server and every login revokes the
  previous session).
- **Two clients on one store folder**: not supported; the guide says one client per folder, like the config cache.
- **Disk full or folder not writable**: the command is sent anyway (the outcome is the same as without a store), and the
  store exposes the write failure.
- **Body bytes**: stored as sent and resent byte-identical (CMD-02 hashes the raw bytes); the SDK never re-serializes a
  stored request.
- **Secrets**: the store never holds the session token; it holds the command body, which the game already sends in clear
  over TLS.

## Requirements *(mandatory)*

### Functional Requirements

**New-player flag and the login snapshot (FG-013; IDN-07, PLY-02, API-03, ANA-01)**

- **FR-001** (FG-013, API-03) **[D1]**: `LoginResponse` MUST gain an additive boolean field (`is_new_player = 8`, plan
  wire-changes §1) that is `true` exactly when this login created the player (the profile did not exist and the creation
  transaction committed in this request), and `false` otherwise. Guest login, provider login and a confirmed account
  switch into a new account set it the same way. No existing field changes meaning.
- **FR-002** (FG-013, ANA-01): The analytics `login` record's `isNewUser` and the response flag MUST come from the same
  value. `LoginResult` (the identity module's result type) MUST carry it.
- **FR-003** (FG-013, IDN-07) **[DEFAULT, D2]**: Login MUST keep returning every client-visible section (IDN-07's first
  option), now subject to the degradation of FR-020..FR-023. This slice adds no login request field to skip or filter
  sections and no "known revisions" exchange.
- **FR-004** (FG-013, SDK-03): The SDK MUST expose the flag through the `LoginResponse` that `LoginGuestAsync` and
  `LoginWithProviderAsync` already return (generated message), and MUST apply the login sections as today. The PCR
  unity-sdk and the consumer guide MUST state that the login sections are a full snapshot and that a boot needs no second
  snapshot read.
- **FR-005** (FG-013, PCR-01): PCR identity MUST define the flag ("this login created the player"), its value after a
  lost first-login response (`false`), and its relation to analytics `isNewUser`.

**Account initializers (FG-015; IDN-01, DAT-04, DAT-09, ECO-01, ECO-04, ECO-06, CMD-03, ARC-01, ARC-06, EXT-04)**

- **FR-010** (FG-015, ARC-01, ARC-06): The framework MUST let a module register account initializers from its business
  project (no ASP.NET Core or Protobuf dependency; the business-project architecture rule stays green). An initializer
  declares the aggregates it may write (its touches) and, like a command handler, runs over an `ICommandContext` that gives
  the new player's id, the clock, the loaded config, deterministic ids and seeds, claims and outbox, and may be created
  with services (catalogs, options) the way handlers are. It returns OK or a rejection code.
- **FR-011** (FG-015, IDN-01, DAT-04) **[D3]**: When a login finds no profile for the resolved player, the framework MUST create the
  account in **one** transaction that holds: the creation anchor rewrite (010, R9), the profile (with the new session
  epoch and the identity key), every aggregate the initializers changed, the claim markers they inserted, and one receipt
  whose outbox holds their ledger (and other) entries (ECO-06, CMD-03). The receipt's operation is a framework-reserved
  name **[DEFAULT: `identity.create_account`]**. Its request id is a server UUID generated once per login call, so the
  receipt key cannot be computed from the player id (010 B1); at most one creation per player is guaranteed by the
  profile insert. Its actor is the system actor with `actorId` = the player id (CMD-09 lookup). The receipt is written
  only when the initializers changed an aggregate, reserved a claim or added an outbox entry (plan N4, architect
  decision). The outbox is relayed after the commit like a command's (inline relay + sweeper).
- **FR-012** (FG-015) **[D6]**: Initializers MUST run only inside that creation transaction. An existing profile MUST NOT run any
  initializer, whatever state its other aggregates have. Concurrent or retried first logins MUST commit at most one
  creation (the profile insert decides; the loser re-runs as a normal login), so no grant is applied twice.
- **FR-013** (FG-015, CMD-04, constitution I): The transaction lambda may run several times; initializers MUST follow the
  handler rules (no I/O or side effect outside the context). The framework MUST apply `CommitRules` to the creation as to
  a command: writes only to declared aggregates, no audit entry (it is not a GM command), aggregate size budget,
  receipt size limit (FR-031).
- **FR-014** (FG-015): An initializer rejection, an initializer exception, a commit-rule violation or a store failure
  MUST roll back the whole creation (no profile, no seeded aggregate, no receipt, no claim, no ledger entry). The login
  MUST then fail with an error envelope **[DEFAULT, D15: `INTERNAL` for a rejection, an exception or a rule violation (a module
  bug: config is already checked at startup); the existing retryable codes for store failures]**, log one Error naming
  the module, the initializer and the code or exception type, and count it in a metric by initializer and result. The
  next login with the same credential MUST run the creation again.
- **FR-015** (FG-015, DAT-09, ARC-07) **[D4]**: At startup the framework MUST check, and report each fault as one line of the
  aggregated startup report naming the module and the initializer:
  - every declared aggregate is registered;
  - no initializer declares `profile`;
  - `profile` plus the union of every initializer's declared aggregates is at most `CommandDescriptor.MaxTouches` (4);
  - claim markers of all initializers together stay within `MaxClaimMarkersLimit` (2).

  Provision mode runs the same structural checks (it already validates the registry).
- **FR-016** (FG-015, PCR-01): The creation MUST be observable: the receipt is readable through the receipt lookup of the
  player (CMD-09) and the GM receipt view; the ledger entries carry the source type the initializer chose; a metric counts
  creations by result.
- **FR-017** (FG-015, 013 FR-010): The consumer test kit MUST let a test run an initializer host-free over the command
  context double (default state of every declared aggregate at `rev = 0`, config through `TestGameConfig`), with the same
  commit checks as for a handler, without a unit-test-framework dependency.
- **FR-018** (FG-015) **[DEFAULT, D5]**: `PlayerAggregateDescriptor` keeps its constant `createDefault`. Defaults derived
  from config or the player id are not added: they would change the balance of players who never wrote the aggregate when
  config changes, and would put resources in a wallet without a ledger entry (ECO-06).

**Degraded reads of a broken aggregate (FG-029; PLY-02, API-04, API-05, API-10, API-11, DAT-02, CMD-04, OBS)**

- **FR-020** (FG-029, API-11): The core error-code catalog MUST gain two codes **[DEFAULT, D7]**:
  - `AGGREGATE_VERSION_AHEAD`: the stored `schemaVersion` is newer than the code (rolling deploy, rollback); HTTP 503,
    retryable, with a retry delay;
  - `AGGREGATE_UNREADABLE`: an upgrader threw, the state does not deserialize, or the wire mapper threw for that state;
    HTTP 500, not retryable.

  Both carry details `aggregate` (the aggregate id) and never the exception message or state content (API-04). The SDK's
  `ErrorCodes` gains both constants.
- **FR-021** (FG-029): The aggregate loader MUST report a read failure as a typed failure naming the aggregate, the player
  and the reason (`version_ahead`, `upgrade_failed`, `unreadable`, `mapper_failed`) instead of a raw exception, for every
  reader: snapshot, login, the command runner (execute and replay), the GM player view, `PlayerSnapshotReader.ReadOneAsync`
  and `ReadAllAsync`, the profile cache and the account purge. A store error is not a read failure (FR-020 does not apply).
- **FR-022** (FG-029, API-10, API-03): `SnapshotResponse` and `LoginResponse` MUST gain an additive repeated field listing
  the unavailable sections, each with the section id, the code name and the code number (message `UnavailableSection`;
  `SnapshotResponse.unavailable_sections = 5`, `LoginResponse.unavailable_sections = 9`,
  `CommandResponse.unavailable_sections = 10`; plan wire-changes §1). A section that failed is absent
  from `sections` (never present with an empty or default payload). Every other section MUST be returned exactly as an
  undamaged read returns it. The response is HTTP 200. An explicit `SnapshotRequest.sections` list behaves the same way.
- **FR-023** (FG-029, IDN-07, IDN-08): A login MUST NOT fail because a client-visible aggregate other than `profile` is
  unreadable. It creates the session and raises the epoch once, and answers with FR-022's list. A broken `profile` fails
  the login, and any authenticated request, with an error envelope carrying the typed code (FR-020) instead of `INTERNAL`.
- **FR-024** (FG-029, CMD-04): A command whose `Touches` or `Reads` include an unreadable aggregate MUST be refused before
  the handler runs, with an error envelope carrying the typed code; nothing commits and no receipt is written. A replay
  of a committed command MUST answer from its receipt and list an aggregate it cannot read as unavailable instead of
  failing. `CommandResponse` MUST gain the same additive unavailable list (FR-022), also used when a committed command's
  new state of a touched aggregate cannot be mapped to the wire: the commit stands, the section is omitted from
  `updates` and listed.
- **FR-025** (FG-029, GM-02): The GM player view MUST list every readable aggregate as today and each unreadable one with
  its code and reason (additive JSON fields, OpenAPI `docs/api/gm-openapi.json` updated), instead of failing.
- **FR-026** (FG-029, OBS, constitution VII): Each failed read MUST produce one log entry (aggregate, player, reason,
  exception type, request or correlation id; no state content) and one increment of a new metric counted by aggregate and
  reason. The `Unhandled exception.` path of `ExceptionMiddleware` MUST no longer be reached by an aggregate read failure.
- **FR-027** (FG-029, SDK-03): The SDK MUST track unavailable sections from login, snapshot and command responses:
  - `SectionStore` (or the client) exposes the set of unavailable section ids with their codes, and an event when it
    changes;
  - a section listed as unavailable keeps its last good copy (never moved back, never cleared by a refresh);
  - a later successful read of the section clears its mark.

  Its handling of `AGGREGATE_VERSION_AHEAD` follows the retryable flag (resend the same bytes), with no new retry rule.
- **FR-028** (FG-029, PCR-01): PCR platform §4 (snapshot), §6 (schema version: "newer than code → 500" replaced by the
  typed codes and the degradation) and §8 (catalog) MUST describe this behaviour; `docs/html/02-them-command.html` §6b and
  `01-gioi-thieu.html` MUST be updated where they state the 500.

**Command limits and typed size rejections (FG-016; CMD-03, DAT-09, API-06, Appendix B, PCR-01)**

- **FR-030** (FG-016, API-11): The core catalog MUST gain the business code `COMMAND_OUTPUT_TOO_LARGE` (HTTP 200, not
  retryable) **[DEFAULT, D8]**, with details `part` (`result` or `receipt`), `bytes` and `limit`. The details travel in a
  new additive `CommandResponse.details` map (field 11) and in the GM command response's `details`. That field carries
  the details of every HTTP 200 rejection, module codes included (plan N5, BC-11, architect decision).
- **FR-031** (FG-016, CMD-03): The runner MUST refuse, with that code and nothing committed, a command (Player, GM or Dev)
  whose encoded result exceeds `ResultCompactMaxBytes` or whose receipt exceeds `ReceiptMaxBytes`, measured as today. The
  same applies to the account creation of FR-011. Both checks live in `CommitRules` so the runner and the test kit share
  them. Each refusal adds one Error log (operation, part, size, limit) and one increment of a metric counted by operation
  and part.
- **FR-032** (FG-016, Appendix B, PCR-01): Every `GameServer:Commands` setting MUST be validated at startup against a
  documented range, each violation one line of the aggregated report **[DEFAULT ranges, D9]**:
  - `ReplayWindowHours` 1..168;
  - `PerPlayerQueueLimit` 0..16;
  - `ReceiptMaxBytes` 1024..65536;
  - `ResultCompactMaxBytes` 64..16384, and its base64 length plus 1 KiB at most `ReceiptMaxBytes`;
  - `MaxAggregatesPerGame` 1..16 (DAT-09 caps it at 16).

  The defaults stay (4096, 512, 16, 24, 2, Appendix B). `MaxTouches` (4) and `MaxClaimMarkersLimit` (2) stay constants.
- **FR-033** (FG-016, PCR-01): PCR platform MUST list every command limit (the five settings and the two constants, the
  64 KiB aggregate hard budget, the player body limit) with its default, whether it is a setting, its range, its cost and
  its failure mode, and a worked example of how many ledger entries fit in a receipt at the default (measured with the
  sample's grant, the first consumer measured about 11) and at a raised limit. The `GameServer:Commands:*` keys MUST be in
  the documented-keys list.
- **FR-034** (FG-016, 013 FR-010): The test kit's command context double MUST check the receipt size of an outcome with
  the runner's measure (same builder, fixed-length request id, body hash and receipt id, the config version of its config)
  and report the runner's line, so a consumer test needs no file-store measurement. Its documentation states the inputs
  that are placeholders.

**Caller-owned request ids and pending commands (FG-022; SDK-02, CMD-01, CMD-02, CMD-07, API-07)**

- **FR-040** (FG-022, SDK-02): `GameServerClient` MUST gain an additive `SendCommandAsync` overload that takes the request
  id (`SendCommandAsync(route, request, parser, string? requestId, cancellationToken = default)`. `requestId` is a
  required parameter, and null lets the SDK generate the id. Every existing call keeps its binding, as the plan's
  research R10 probe shows). The SDK MUST refuse an id that is not a UUID in format `D` before any network call. Every
  attempt of the call sends that id with the same bytes.
- **FR-041** (FG-022): The request id a command used MUST be readable by the caller: on `CommandResult` (additive member)
  and on the `GameServerException` thrown by a command call when no answer arrived (`GameServerError.RequestId`, today set
  only from an error envelope).
- **FR-042** (FG-022) **[DEFAULT, D14]**: The SDK MUST define a pending-command store interface in `Runtime/Core` and a
  persistent implementation constructed with a folder path; a client uses a store only when the game passes one (through
  `GameServerClientOptions.PendingCommands`, plan N8). When a client has a store, every `SendCommandAsync` call MUST record the
  route, the exact body bytes, the request id, the player id and the server time **before** the first attempt is sent,
  and MUST remove the entry once the server gave a final answer: a `CommandResponse` (any code) or an error envelope whose
  code is not retryable. No answer, cancellation after the send, or a retryable last answer (including
  `COMMIT_UNKNOWN_RETRY`) keeps it.
- **FR-043** (FG-022, CMD-02, CMD-07): The client MUST list stored entries and resend one: the resend sends the stored
  bytes with the stored id to the stored route, and returns a `CommandResult` as `SendCommandAsync` does. It MUST refuse an
  entry of another player than the session's, and an entry older than a configured maximum age **[DEFAULT 23 h, below the
  24 h replay window, D11]**, which it lists as expired. The game can discard any entry. Resends never happen
  automatically **[DEFAULT, D10]**.
- **FR-044** (FG-022, SDK-02): Login, logout, snapshot, config and time calls MUST NOT be stored.
- **FR-045** (FG-022, 015 FR-061..FR-063): The persistent store MUST follow the 015 cache rules: atomic saves (temp file,
  flush to device, rename; one file per entry, plan N6), a crash leaves the old or the new entries, a damaged or
  unknown-format entry file is skipped with a reason and no exception (the other entries load), a disk failure never fails the SDK call and is exposed, no file name comes from
  server data (an entry's file name, if any, comes only from its validated request id), the session token is never stored,
  and its container format and version rule are documented in PCR unity-sdk.
- **FR-046** (FG-022, SDK-01): The store MUST be C# 9 / `netstandard2.1` with no UnityEngine dependency; `Runtime/Unity`
  MAY add a helper rooted at `Application.persistentDataPath` with the 015 folder-name rule, under
  `#if UNITY_5_3_OR_NEWER`. A client without a store and without caller ids MUST behave exactly as today. SDK changes MUST
  be additive (no public type or member removed or changed in signature); the package stays `0.7.0`; every new file ships
  with a committed `.meta`.
- **FR-047** (FG-022, SDK-02): The cost of recording an entry MUST stay within SC-008 on the thread the SDK runs on; the
  XML doc states the threading rule (one client per store folder, the Unity main thread).

**Samples, fixture and dogfood**

- **FR-050** (FG-015) **[DEFAULT, D12]**: The sample game MUST register an initializer that seeds a starting state from a
  declared config value (015 declarations with a rule), including one Economy grant with its ledger source. The value
  comes from an optional, server-only `account.json` (`startingGold`). `samples/SampleHost/config` has the file;
  `tests/Shared/config` does not, so the framework test hosts stay unseeded and keep their expectations. New sample
  tests cover the seeded path (plan N9, research R13).
- **FR-051** (FG-015, FG-016): `tests/ConsumerFixture` MUST register an initializer and test it with the test kit call of
  FR-017, and check a handler outcome's receipt size with FR-034.
- **FR-052** (FG-013, FG-022): The sample Unity client (`KestrelDemo`) MUST log the new-player flag, use the login sections
  without a second snapshot at boot, and use the persistent pending-command store with a listing and resend after login.

**Documentation (PCR-01; FG-012 continuation of 013)**

- **FR-060** (FG-013, FG-015, FG-016, FG-022, FG-029): `docs/guides/consumer-guide.md` MUST gain or update sections on:
  - the login flow (flag, full sections, no second read);
  - account initializers (touch budget, rules, failure mode, legacy accounts, test kit call);
  - degraded reads and what a client does with an unavailable section;
  - command limits (which to raise, cost) and the size rejection;
  - caller request ids and the pending-command store, with the Unity setup and the resend after login.
- **FR-061** (PCR-01, ARC-08): The following MUST record this slice:
  - PCR platform: §2/§3 (size rejection, account creation transaction), §4, §5, §6, §8 (three new codes), the limits
    table of FR-033, the new metrics, the breaking changes BC-1..BC-9;
  - PCR identity: login flow, flag, account creation and initializers, broken profile;
  - PCR economy: grants from an initializer (source type, ledger);
  - PCR sample-game: the sample initializer;
  - PCR unity-sdk: caller request ids, pending-command store, unavailable sections;
  - `docs/guides/module-authoring.md` and `templates/module/README.md`: initializer registration;
  - `docs/html/01-gioi-thieu.html` and `02-them-command.html`, where login, defaults, schema versions or limits appear.
- **FR-062** (FR-072 of 013): Pages and guides touched by this slice MUST state the framework version they match (`0.7.0`,
  "unreleased" until the tag).

**Release and cross-cutting (ARC-08, PCR-01, constitution VI)**

- **FR-090** (ARC-08): Slice 016 MUST write `specs/016-login-command/release-notes-draft.md`, listing the fixed FG ids,
  every breaking change with its fix, the first-consumer migration checklist in order and the rolling-deploy notes.
- **FR-091** (ARC-08) **[DEFAULT, D13]**: As the last slice of the round, slice 016 MUST merge the drafts of 013, 014, 015
  and 016 into `docs/releases/v0.7.0.md`: one list of fixed FG ids, one breaking-change table per slice with its fix, one
  ordered first-consumer checklist (013 → 016), and the manual checks still open (015 T058 device run). Cross-slice
  breaking-change ids are written `0NN/BC-n`. The file is a draft marked "finalized at tag". Setting the release date,
  restamping the pages marked "v0.7.0 (chưa phát hành)" and the annotated tag are a round-close step after the merge, not
  a task of this slice (plan N10). The tag itself and any push wait for the owner.
- **FR-092** (constitution VI): `GameServer.slnx` MUST build in Debug and Release with 0 warnings, and the full test suite,
  Couchbase included, MUST pass. Every acceptance scenario maps to an automated test or a documented manual check. The
  manual checks are:
  - the reviewer walkthrough of story 6;
  - `KestrelDemo` new-player login and a pending-command resend after an app restart (`smoke-build.ps1`).

  The creation transaction and the concurrent first logins of story 2 MUST also run on Couchbase (transactions on).

### Key Entities

- **New-player flag**: `LoginResponse` boolean, true when this login created the player; equals analytics `isNewUser`.
- **Account initializer**: a module's code run once per account, inside the creation transaction, over an
  `ICommandContext`, with declared touches.
- **Account creation transaction**: anchor rewrite + profile insert + initializer writes + claims + one receipt
  (`identity.create_account`) with its outbox; at most one commits per player.
- **Read failure**: an aggregate that cannot be loaded or mapped, with a reason (`version_ahead`, `upgrade_failed`,
  `unreadable`, `mapper_failed`) and a typed code (`AGGREGATE_VERSION_AHEAD`, `AGGREGATE_UNREADABLE`).
- **Unavailable section**: an entry `{id, code, code_number}` of the additive list in snapshot, login and command responses.
- **Command output limit**: `ResultCompactMaxBytes` and `ReceiptMaxBytes`, validated settings; over the limit =
  `COMMAND_OUTPUT_TOO_LARGE`.
- **Caller request id**: a UUID the game passes to `SendCommandAsync` and can read back from the result.
- **Pending command**: route, exact body bytes, request id, player id, server time of one command whose outcome is unknown,
  kept on the device until a final answer.

## Breaking changes (to consumers of `v0.6.0` / `main` after 015)

| # | Change | Who is affected | How it is surfaced | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | A result or receipt over its limit is the business rejection `COMMAND_OUTPUT_TOO_LARGE` (HTTP 200, nothing commits) instead of a 500 `INTERNAL` (FR-030, FR-031) | Clients and tests that expect 500 for an oversized command; dashboards counting `command_results_total{code="INTERNAL"}` for it | Command response with the new code and details; new metric; Error log line | Treat the code as a design bug of the command; raise the limit (FR-032) or return less |
| BC-2 | `GameServer:Commands:*` values are validated at startup (FR-032); `MaxAggregatesPerGame` above 16 is refused | Deployments with out-of-range values (silently accepted before) | Startup exit 1, one line per setting naming the range | Use a value in range |
| BC-3 | **Snapshot and login semantics (PCR platform §4, API-10):** a snapshot or login no longer always holds every client-visible section; an unreadable one is absent and listed in the new unavailable field (FR-022, FR-023) | Client code that assumes every section is present after login or snapshot (old SDKs see the section missing, where the whole call used to fail) | A section missing from the SDK's store; the new list in new SDKs | Check the unavailable list (or a missing section) and show a recoverable state |
| BC-4 | A command or replay that touches or reads an unreadable aggregate answers `AGGREGATE_VERSION_AHEAD` (503, retryable) or `AGGREGATE_UNREADABLE` (500) instead of 500 `INTERNAL`; a broken `profile` fails login and authenticated requests with these codes (FR-020, FR-023, FR-024). PCR §6 "newer than code → 500 `INTERNAL`" changes | Clients and tests that match `INTERNAL`; SDKs now retry the version-ahead case (retryable) | New codes in error envelopes and `http_errors_total{code}` | Match the new codes; keep the deploy rule of PCR §6 |
| BC-5 | `AggregateDocumentSerializer.Load` and `PlayerSnapshotReader.ReadAsync/ReadAllAsync/ReadOneAsync` throw a `GameServerException` with `AGGREGATE_VERSION_AHEAD` or `AGGREGATE_UNREADABLE` (details `aggregate`) instead of `GameServerException(INTERNAL)`, `JsonException` or `InvalidOperationException`. Signatures are unchanged; the additive `TryLoad`, `ReadAvailableAsync` and `ReadAllAvailableAsync` return the failures instead of throwing (FR-021; plan N1) | Game or test code that calls these readers directly and catches the old exception types | Different exception type and code at run time (no compile error) | Catch `GameServerException` (or use the degrading members) |
| BC-6 | A first login runs every registered account initializer in the creation transaction (FR-011): new accounts start with seeded aggregates at `rev ≥ 1`, one `identity.create_account` receipt and the ledger entries of the grants, **when an initializer changes something** (no receipt otherwise) | Tests and tools that assert a new player's sections are defaults at `rev = 0` when a module registers an initializer with effects (the sample game does only when `account.json` exists, FR-050) | Different sections and revs in the first login response; one creation receipt per new account | Assert the seeded state; only modules that register an initializer change anything |
| BC-7 | `LoginResult` gains the init properties `IsNewPlayer` and `Unavailable` (FR-002, FR-023; plan N2) | Code that reads `LoginResult` | None at compile time (additive init properties) | Read the new values where needed |
| BC-8 | **SDK semantics (PCR unity-sdk):** an unavailable section keeps its last good copy and is marked; `AGGREGATE_VERSION_AHEAD` is retried by the SDK (retryable) (FR-027) | Game code that reads every section after login without checking | The mark and event; a few retries before an exception on the version-ahead code | Read the unavailable set; handle the codes |
| BC-9 | Account initializer declarations are validated at startup (FR-015) | Modules that register an initializer with a bad declaration (new API, so only new code) | Startup exit 1, one line per fault | Fix the declaration |
| BC-10 | A login whose transaction ends ambiguous, expires or loses every race round answers `COMMIT_UNKNOWN_RETRY` (503) or `CONCURRENT_UPDATE` (409, retryable) instead of a 500 `INTERNAL` (FR-014; plan N11, architect decision) | Clients and dashboards that count login 500s | New codes on the login routes | Handle them like any retryable code; the SDK still never retries a login after an unknown outcome |
| BC-11 | `CommandResponse.details` (new field 11) and the GM command response's `details` carry the details of every HTTP 200 rejection, module codes included; the XML doc of `ICommandOutcome.Details` changes from "not sent" to "sent" (FR-030; plan N5, architect decision) | Modules that pass details to `CommandOutcome.Reject` with an HTTP 200 code | Details visible to the client | Keep rejection details safe to show (they already must be, as for non-200 codes) |

No route, persisted document shape or GM API response field changes meaning. Additive: `LoginResponse` (flag and
unavailable list), `SnapshotResponse` and `CommandResponse` (unavailable list; `CommandResponse.details`, BC-11), three core error codes, GM player view
fields, the initializer API, the test kit calls, the SDK overload, `CommandResult` member, pending-command store and
unavailable-section tracking. No existing proto field is renamed, renumbered or given a new meaning (API-03). The 014
route catalog gains no route.

## First-consumer migration (zh-pvp-server and its Unity client after `v0.7.0`)

Based on the friction log entries and on the `FRAMEWORK-GAP` markers found in both repos on 2026-11-04 (server
`7911f20`, client `research-and-development-games` `49df950ad`; the client writes markers as `FRAMEWORK-GAP FG-NNN`
without the colon). The game session owns the removal. This slice makes it possible and lists it in the release notes
(FR-090, FR-091).

| FG | Marker / workaround today | Removable after `v0.7.0` | Stays (game choice) |
| --- | --- | --- | --- |
| FG-015 | `src/ZhPvp.Game/Account/InitAccountHandler.cs:16` (marker): command `account.init` (`POST /api/v1/account/init`, Touches `progress`, `wallet`, `armory`), state flag `ProgressState.InitVersion`, code `ACCOUNT_ALREADY_INITIALIZED` (50101), registration `GameModuleDefinition.cs:25`, route `GameModule.cs:20,34-38`. Docs `docs/pcr/game.md` lines 41-57, 66-69, 78 | The handler body becomes an account initializer with touches `progress`, `wallet`, `armory` (`profile` + 3 = 4, exactly the budget of FR-015): starting CASH grant through `EconomyOperations.Grant` (ledger source `account_init`), starter guns, equipped gun, `InitVersion = 1`. The marker goes. `tests/ZhPvp.Tests/Game/AccountInitTests.cs` becomes "a first login seeds the account once" (concurrent logins, lost response) and `InitAccountHandlerTests.cs` runs the initializer through the test kit (FR-017) | `account.init` route, command and error code: keep only if accounts created before the bump must still be initialized (D6); the game is pre-launch, so deleting the dev/test data is the simpler choice. `ProgressState.InitVersion` can stay as seeded state (or its proto field becomes `reserved`) |
| FG-013 | Client markers `Assets/MadProject/Scripts/Online/OnlineSession.cs:200` (login sections ignored, boot reads the snapshot) and `PlayerReadModel.cs:24` (`IsReady` only after that snapshot); new player inferred from `progress.init_version == 0`; boot step `Initializing` (`OnlineSession.cs:214-217, 279-320`) | Both markers. Boot: login → config → apply the login sections and mark ready; drop `RefreshSectionsAsync` and the `InitAccountAsync` step (the initializer already ran). `IsNewPlayer` from the login response if the game shows a first-time screen. `INIT_NOT_APPLIED` / `PROGRESS_MISSING` failure paths simplify; a missing `progress` now means "unavailable section" (story 3) | `docs/pcr/game.md` line 79 ("login sections are not read") is updated by the game |
| FG-022 | Server marker `InitAccountHandler.cs:17` (state flag, not the receipt, makes a second init a no-op); client marker `OnlineSession.cs:276` (unknown outcome fails the boot, RETRY re-runs it) | Both markers: the server one goes with the handler; on the client, P2 commands pass their own request id and use the pending-command store (`new GameServerClient(...)` at `OnlineSession.cs:83`, with a Unity store folder per environment like the 015 config cache), and resend pending entries after login | State idempotency of `battle.end` (`battle_id`), `chest.*`, `gun.upgrade` (`02_design_p1.md` §4) stays good practice (the friction log says so) |
| FG-016 | `tests/ZhPvp.Tests/Game/AccountInitTests.cs:124` (marker): the test measures result and receipt sizes from the file store because an overflow is a 500 | The marker and the file-store measurement: an overflow is now `COMMAND_OUTPUT_TOO_LARGE`, and the test kit checks the receipt size host-free (FR-034). Raise `GameServer:Commands:ReceiptMaxBytes` in `appsettings` if P2 chests need more than about 11 ledger entries | The design rule "≤ 3 Touches per game command" (`02_design_p1.md` §4): `MaxTouches` stays 4 |
| FG-029 | No marker; rule in `docs/pcr/game.md` (Aggregate section: bump `schemaVersion` with an upgrader, deploy code before writing a new version) | — | The rule stays (deploy order is still required); the game doc can note that a broken aggregate now degrades instead of blocking login |

Markers removable by this slice: 3 on the server (FG-015 and FG-022 in `InitAccountHandler.cs`, FG-016 in
`AccountInitTests.cs`) and 3 on the client (FG-013 in `OnlineSession.cs` and `PlayerReadModel.cs`, FG-022 in
`OnlineSession.cs`), covering four FG ids; FG-029 has no marker. The friction log entries FG-013, FG-015, FG-016, FG-022
and FG-029 can move to `fixed-in-v0.7.0`. Order for the game (after the 013–015 steps): bump; fix BC-7 if its tests build
`LoginResult` (they do not today); move `account.init` into an initializer and reset dev data; switch the client boot to
the login sections; then adopt caller request ids and the pending store for P2 commands.

## Dependencies and seams with other slices

- **015**:
  - Initializers read config and declared catalogs, which are built before traffic.
  - `LoginResponse` keeps `config_version` and `client_config_version`, so a client with a persistent config cache still
    knows at login whether to refresh.
  - The pending-command store reuses the 015 file pattern (`CacheFileOps`, folder-name rule, Unity helper shape).
  - `GameConfigStatus` is unaffected.
- **014**:
  - No new route. The new proto fields regenerate `Core.cs` and `Identity.cs` in the Unity package with WireGen (same
    paths and `.meta`).
  - The export format does not change; it could list initializers later.
  - `WireConformance` is unaffected (no new section).
  - A wire mapper that throws at run time is the `mapper_failed` reason (FR-021); a missing mapper still fails startup.
- **013**:
  - The new error codes are core codes (0–999), outside the game band.
  - The test kit gains the initializer run and the receipt-size check (additive; the allow-list guard test of the kit
    is extended).
  - The request log records the new codes through `IRequestOutcomeFeature` unchanged.
- **012**: the login analytics record's `isNewUser` uses the same value as the flag (FR-002); the creation's ledger
  entries flow through the existing relay; payment analytics are unaffected.
- **010**: the creation anchor is kept and the purge handles seeded aggregates and creation receipts as any other.
- **Deferred (Out of Scope)**:
  - FG-030 (upgrade metric counts reads) is touched only by the new read-failure metric, which is separate.
  - FG-014 (`MapPlayerQuery`) would reuse the degraded reader.
  - FG-017 (per-resource visibility) is unrelated.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the login matrix of story 1 (new guest, returning guest, new provider identity, returning provider
  identity, confirmed switch into a new account, takeover, lost first-login response), the flag is `true` in exactly the
  creating logins and equals the analytics `isNewUser` in 100% of logins.
- **SC-002**: With an initializer that grants a starting balance and sets one game aggregate:
  - 20 concurrent first logins of one new credential, plus 5 retries after a dropped response, give exactly 1 creation
    receipt, 1 ledger entry and a balance equal to the starting amount, on the in-memory store and on Couchbase;
  - a fault injected at each step of the creation transaction (and an initializer rejection and exception) leaves either
    the full account or nothing in 100% of cases, and the next login completes the creation.
- **SC-003**: A first login with an initializer touching 3 aggregates and one ledger grant adds at most 20 ms p95 to the
  first-login time on the local Couchbase reference setup, compared with a first login without initializer; later logins
  are unchanged within measurement noise (opt-in perf test, `GAMESERVER_PERF=1`).
- **SC-004**: In the broken-aggregate matrix (4 reasons × snapshot, explicit snapshot list, login, touching command,
  reading command, untouched command, replay, GM view, `ReadOneAsync`):
  - 0 responses are 500 `INTERNAL`;
  - snapshot, login and GM view answer 200 with every other section byte-identical to an undamaged read;
  - each touching or reading command gets the typed code and writes nothing;
  - each failed read gives exactly 1 log entry and 1 metric increment;
  - a successful login raises the epoch exactly once.
- **SC-005**: A result or receipt 1 byte over its limit gives `COMMAND_OUTPUT_TOO_LARGE` with 0 documents written, and the
  same command at exactly the limit commits. For the sample game's commands and the fixture's command, the receipt size
  the test kit reports equals the size the runner measured (0 bytes difference).
- **SC-006**: Each out-of-range `GameServer:Commands` value and each bad initializer declaration gives exactly one startup
  line; seeding all of them plus one unrelated violation in one start gives all lines in one report.
- **SC-007**: With the pending-command store, a simulated app death at each point (before the send, after the send and
  before the server, after the commit and before the response, after the response and before the entry is removed)
  followed by a restart, login and resend gives exactly one execution (ledger count) in 100% of the cases, and the resend
  after a committed attempt answers `replayed = true` with the original receipt id. A fault injected at every save step
  leaves the old or the new entries loadable in 100% of cases; every corrupt-file variant gives an empty store with
  0 exceptions.
- **SC-008**: Recording and removing a pending entry with a 4 KiB body each take at most 10 ms p95 outside Unity on the
  reference machine (flush to device included).
- **SC-009**: The public API diff of the Unity package between `main` before and after this slice shows 0 removed or
  changed members; the package stays `0.7.0`; every new file has a committed `.meta`. The proto diff shows only new field
  numbers and new messages (API-03).
- **SC-010**: After the game bumps to `v0.7.0`, the 6 markers listed in "First-consumer migration" can be removed, a new
  player's boot performs 1 player-data read (the login) instead of 2, and its starting state comes from the creation
  transaction with 0 client calls.

## Assumptions

Decisions taken by default (owner may override; each is referenced where it applies):

- **D1 — the flag means "this login created the player"** (FR-001): it is the only definition the server can give
  without guessing what the client saw, and it already exists in analytics (`isNewUser`). A lost first-login response
  gives `false` on the retry; durable onboarding is game state. A creation timestamp field was considered and not added:
  it would invite clock-based "new player" rules on the client.
- **D2 — login keeps the full snapshot; skipping sections or exchanging known revisions is deferred** (FR-003). Reasons:
  - the first consumer reads the snapshot twice only because the login could not tell it about a new player and an
    uninitialized account; with the flag and the initializer, the login sections are exactly the read it needs, so the
    double read goes away without a new login option;
  - a skip saves something only when the client already holds the current state, and the SDK cannot today: the section
    store is in memory and cleared at every login. "Known revisions" needs a persistent section cache on the device,
    trust rules for it and invalidation, a separate feature;
  - the snapshot is one parallel KV read of at most 16 documents (≈ 1 RTT, PLY-02), and IDN-07 allows the full snapshot;
  - a later additive request field (`SnapshotRequest`-style section list, or known revisions) stays possible.
- **D3 — initializers run in the creation transaction, not in a later transaction or a lazy check**: atomicity with the
  account gives "account exists ⇒ seeded" with no marker, no extra read on every login and no window where the account
  exists unseeded. A later transaction keyed by a receipt would expire with the replay window (CMD-07) and need a
  permanent marker.
- **D4 — the creation counts against the command transaction budget** (`profile` + initializer touches ≤ 4, claims ≤ 2;
  FR-015): DAT-04/DAT-09 bound every state-changing transaction, and the first consumer's init fits exactly (3 + profile).
- **D5 — no config-derived `createDefault`** (FR-018): see the reason in the requirement (ECO-06 ledger, retroactive
  change of untouched players).
- **D6 — no initializer for existing accounts**: an account created before an initializer was registered is never
  initialized by the framework. A backfill could double-grant players that a game command already initialized (the first
  consumer's case). Versioned initializers are out of scope.
- **D7 — two read-failure codes** (FR-020): `AGGREGATE_VERSION_AHEAD` is a deploy-order state that a retry on another
  instance fixes (503, retryable); `AGGREGATE_UNREADABLE` needs a code or data fix (500, not retryable). One code would
  either retry hopeless cases or never retry the rolling-deploy case. The unavailable section is reported in a new list,
  not as a section with an empty payload, because an old SDK would parse an empty payload as a valid default state (a
  wallet at 0).
- **D8 — `COMMAND_OUTPUT_TOO_LARGE` is a business rejection (HTTP 200)**, like `AGGREGATE_TOO_LARGE`: the size depends on
  the run (number of rewards), nothing commits, and the client gets a code instead of an anonymous 500. One code with a
  `part` detail instead of two.
- **D9 — command settings ranges** (FR-032): ranges keep every Appendix B default valid, keep a receipt below the 64 KiB
  aggregate budget, cap `MaxAggregatesPerGame` at DAT-09's 16, and require that the largest result fits in the receipt.
  Defaults are unchanged; only `MaxTouches` and the claim limit stay constants (DAT-09 MUST).
- **D10 — no automatic resend**: the SDK lists pending commands and the game resends them after login. The game knows
  whether a command still makes sense (a battle that the server already expired) and in which order to run it with its
  own boot steps.
- **D11 — maximum age of a pending entry 23 h** (FR-043), below the server's default 24 h replay window, measured on the
  server clock the SDK tracks (TIME-01), so a device clock change does not move it. A server with a different
  `ReplayWindowHours` needs the SDK option set to match; the guide says so.
- **D12 — the sample game dogfoods the initializer** (FR-050). Alternative: only the consumer fixture (smaller test
  churn, but the reference sample would not show the path). **Plan (architect decision, owner may override):** the
  amount comes from an optional server-only `account.json` present in SampleHost and absent from the test config, so the
  sample shows the path and the test hosts stay unseeded (research R13).
- **D13 — the round's release note is merged in this slice** (FR-091), because 016 is the last slice before the tag.
  **Plan (architect decision, owner may override):** merged as a draft; the release date, the page restamp and the tag
  are a round-close step after the merge (research R15).
- **D14 — the pending-command store is opt-in**, like the persistent config cache (015 D11): `Runtime/Core` has no platform
  path to choose, and a game without it keeps today's behaviour.
- **D15 — an initializer failure answers `INTERNAL`** (FR-014): startup config rules (015) leave mostly module bugs; a
  dedicated code would give the client nothing to act on. The log and metric name the initializer.

Other assumptions:

- **Requirements wording**:
  - IDN-07 allows "token and snapshot", which the login keeps.
  - IDN-01 says "the player aggregate does not need to be created first — an absent document is the default state";
    initializers are additive to it (an absent document still reads as the default), not a contradiction.
  - API-05 lists 5xx other than the named ones as infrastructure errors; `AGGREGATE_UNREADABLE` (500) and
    `AGGREGATE_VERSION_AHEAD` (503) are server-side faults, consistent with that.
  - Appendix B values are starting values that may change after measurement (PCR operational section), so validated
    ranges with unchanged defaults are not a requirement change.
- The reference machine and the local Couchbase setup are those of the 013–015 measurements; perf tests are opt-in.
- The first consumer's figures (receipt base ≈ 894 B, ledger outbox entry ≈ 278 B, about 11 entries at 4 KiB) come from
  its friction log (FG-016, measured 2026-11-02).
- SDK request ids are UUIDs formatted `D`, as today; the server's request-id rule is unchanged.

## Owner confirmations

No `[NEEDS CLARIFICATION]` remains; every open point has a default. The owner should confirm:

1. **D2**: login keeps the full snapshot; no skip or known-revisions option in this slice.
2. **D4 / D6**: the initializer shares the 4-aggregate transaction budget with `profile`, and never runs for existing
   accounts (no backfill).
3. **D7 / BC-3 / BC-4**: two new read-failure codes (503 retryable, 500), the unavailable list in snapshot, login and
   command responses, and the semantic change of PCR §4 and §6.
4. **D8 / BC-1**: oversized result or receipt becomes an HTTP 200 business rejection.
5. **D12 / D13**: the sample game seeds a starting state, and this slice writes `docs/releases/v0.7.0.md`.

The plan (2026-11-04) confirms D1-D11, D14 and D15, changes D12 and D13 as noted under Assumptions, and adds architect
decisions the owner should also confirm (plan "Owner questions"):

6. the creation receipt is written only when initializers had effects (FR-011, BC-6);
7. `CommandResponse.details` carries module rejection details too (BC-11);
8. login store failures answer the command codes instead of 500 (BC-10);
9. a same-player re-login keeps the last good copy of unavailable sections (FR-027).

## Plan traps (from the plan brief, 2026-11-04)

Handled in the plan; numbering used by research.md.

1. Initializer execution placement: inside the profile-creating transaction, with receipt and outbox, in
   `GameServer.Platform` (constitution I, `ITransactionContext` arch test) → research R1.
2. Race rounds: the writer's outer rounds and the store's body re-runs; the create decision must belong to the attempt
   → R1.
3. `createdPlayer` analytics taken from the pre-read (both racing logins report a new user) → R1.
4. Size and read-failure exceptions must not end in the `INTERNAL` catch-all → R5, R8.
5. Wire mapper failure after the commit; leave the section out, never an empty payload → R7.
6. Broken profile on bearer authentication and in the GM view → R5.
7. `SendCommandAsync` overload binding (CS0121 risk) → R10.
8. Pending-store write cost on the Unity main thread → R11.
9. Sample seeding churn in full-game test hosts → R13.
10. Guard tests to extend → R16.
11. Receipt-size parity in the test kit double → R14.

## Out of Scope

- Login without sections, with a section filter, or with known revisions (D2); a persistent section cache in the SDK.
- Declarative starting balances in `resources.json` (a framework initializer driven by config); a game writes one
  initializer call instead.
- Running initializers for existing accounts, versioned initializers, and GM tools to re-run or reset an account.
- A configurable `MaxTouches` or claim-marker limit; ledger entries with several deltas; receipts without the result.
- Automatic resend of pending commands, resend of logins, and a server-side "pending command" API.
- Repairing broken aggregate documents (ops tool), and FG-030's upgrade metric split.
- FG-005, FG-008, FG-009 (packaging), FG-014 (`MapPlayerQuery`), FG-017, FG-020, FG-031.
- Pushing the branch or the `v0.7.0` tag, and editing the game or client repositories (the game session removes its
  workarounds after the tag).

## Friction claims re-checked on `main` (`5904b74`)

- **FG-013**: `LoginCompletion.cs:97` (`snapshots.ReadAsync(playerId, null, …)`) and `:102` (`createdPlayer: profile is
  null`, analytics only) hold. `identity.proto` `LoginResponse` has no new-player field (fields 1–7; 7 is
  `client_config_version` since 015). **Also**: `SnapshotRequest.sections` (core.proto) already lets a client read some
  sections; the SDK's `ApplyLogin` clears the section store at every login.
- **FG-015**: `LoginCompletion` writes only the profile (`WriteEpochAsync`, `:106-137`, creation anchored on the
  mapping); `PlayerAggregateDescriptor.cs:83` (`Func<TState> createDefault`) and `EconomyModel.cs:68` (wallet) / `:71`
  (inventory) hold. No account-creation interface or event exists in `src/`.
- **FG-016**: `CommandDescriptor.cs:11` (`MaxTouches = 4`) holds; the touches check is now `ModuleRegistry.cs:283-288`
  (log: 252), the aggregate count `:244-249` (log: 213), the 64 KiB budget check `:235-239` (log: 208). The result check is
  now `CommandRunner.cs:306-309` through `CommitRules.CheckResultSize` (013; log: 286) and the receipt check
  `CommandRunner.cs:352-356` (log: 333); both still throw `InvalidOperationException` (500). The settings are
  `GameServerOptions.cs:146-159` (log: 136-140), still unvalidated; PCR platform §3 still says "vượt = lỗi lập trình (500)".
  **Also**: the test kit's double checks the result size but not the receipt size (`CommandContextDouble.cs:264`).
- **FG-022**: `Guid.NewGuid()` is now at `GameServerClient.cs:419` inside `SendAsync` (log: 393); no overload takes an id,
  and `CommandResult` does not expose it. **Also**: a client-made `GameServerError` (network error) has no request id.
- **FG-029**:
  - `AggregateDocumentSerializer.Load` throws `INTERNAL` for a newer schema at `:124-129` (log: 83-88), and the
    deserialization (`:144-145`, `DeserializeState` `:195-197`; log: 103-104, 144-146) throws raw exceptions.
  - `ExceptionMiddleware.cs:53-57` logs "Unhandled exception." and writes `INTERNAL` (holds).
  - `PlayerSnapshotReader.cs:23` holds (all sections in one `Select`).
  - Login reads the snapshot after the epoch write and the session (`LoginCompletion.cs:93, 96, 97`; holds).
  - **Also**:
    - the same `Load` is called by `CommandRunner.cs:292` (execute) and `:452` (replay), `ReadAllAsync` (GM player view,
      `GmPlayerEndpoints.cs:174`), `ReadOneAsync` (`ProfileCache.cs:26`, `GmTargetValidator.cs:20`) and
      `AccountPurgeWriter.cs:79`;
    - a wire mapper exception in a command response happens after the commit;
    - a missing mapper already fails startup (014).

