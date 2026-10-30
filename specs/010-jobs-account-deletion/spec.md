# Feature Specification: Lease jobs and account deletion — 30-day grace, purge, anonymization (P0.5 slice 010)

**Feature Branch**: `010-jobs-account-deletion`

**Created**: 2026-10-30

**Status**: Draft

**Input**: "010 jobs-account-deletion — P0.5: PRIV-01 (player-initiated account deletion required by Apple/Google and
Decree 13/2023: one transaction {set `pa_profile.deleted = {requestedAt, purgeAfter}`, `sessionEpoch++`, receipt}; hide
the identity mappings; 30-day grace period during which GM with `player.read` sees the state and GM with `player.ban`
cancels; afterwards a job holding a lease (JOB-01) purges **every** `pa_*` document of the player through the
transaction runner and anonymizes `playerId` in receipts/ledger/audit by overwriting the field; TBD-04c). Only the
minimal part of JOB-01 needed by the purge job: lease document `leases::{jobName}` = `{owner, leaseUntil,
fencingToken}` acquired/renewed by CAS, safe re-run. Acceptance: ACC-37. Durable job framework (JOB-01 full, JOB-02 job
consumer, ACC-15) stays P1; PRIV-02 data export is P1."

## Scope note

- Already in place (P0–009): `profile.deleted {requestedAtUtc, purgeAfterUtc}` field; error `ACCOUNT_DELETED` (401) and
  the check order deleted → suspended → replaced at login, session and command (IDN-10); GM mutations refuse a target
  pending deletion; GM player view shows the `profile` aggregate (GM-02); identity mappings `identities/{provider}:{subject}`
  carry `playerId` (IDN-02/04); receipts, ledger, `gm_audit` and module outbox projections carry `playerId`/`actorId`;
  claim markers (e.g. `extref_claims`) carry `playerId`; SDK raises `AccountBlocked` for `ACCOUNT_DELETED`.
- This slice adds: a minimal lease-based job runner (single active runner per job across instances, fencing token);
  the player command that requests deletion; the GM action that cancels it during grace; the purge job that, after
  grace, removes the player's data and anonymizes what must be kept; an extension point so each module purges or
  anonymizes its own data; a startup rule that every declared collection states how deletion treats it; SDK call.
- Not included: durable job framework with generic progress documents and job outbox consumer (JOB-01 full, JOB-02 P1
  part, ACC-15, OPS-05 job part — P1); data export (PRIV-02, P1); GM job pages (GM-05, P1); GM- or support-initiated
  deletion; receipt retention purge after 180 days (CMD-08, see Assumptions); GM frontend (owner 2026-10-27).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Player deletes their account (Priority: P1)

From the game's settings a player asks to delete their account. The request is accepted at once: every session of the
player stops working, nobody can log in to the account with any of its identities, and the player is told when the
data will be erased (30 days later).

**Why this priority**: Apple and Google require in-app account deletion and Decree 13/2023 requires honouring it; it is
the store-compliance core of the slice (PRIV-01, ACC-37).

**Independent Test**: A test player with a guest and a provider identity requests deletion; the response carries the
purge date, and every later request with the old token, guest login and provider login return `ACCOUNT_DELETED`.

**Acceptance Scenarios**:

1. **Given** a player with a valid session, **When** they request deletion, **Then** one transaction sets
   `profile.deleted = {requestedAt = now, purgeAfter = now + 30 days}`, increments `sessionEpoch` and writes a receipt;
   the response returns `purgeAfter` (PRIV-01, ACC-37).
2. **Given** the deletion is committed, **When** the old token is used on the instance that served the request,
   **Then** 401 `ACCOUNT_DELETED` at once; on any other instance within ≤ 60 s (IDN-08 cache window) (ACC-37).
3. **Given** a player pending deletion, **When** they log in with the guest credential or any linked provider
   identity, **Then** 401 `ACCOUNT_DELETED` with the end of the grace period in the details; no session is issued and
   no new player is created for that identity (identity hidden, ACC-37).
4. **Given** a player pending deletion, **When** another player tries to link one of its identities, **Then** 409
   `IDENTITY_CONFLICT` as for any mapped identity; the identity stays reserved until purge.
5. **Given** the response was lost, **When** the SDK retries the same request, **Then** it gets either the committed
   result or `ACCOUNT_DELETED`; there is exactly one receipt and one deletion request.
6. **Given** a player under a command-group ban (not a login ban), **When** they request deletion, **Then** the request
   is accepted (privacy right is not blocked by a gameplay ban).

---

### User Story 2 - Data is purged and anonymized after the grace period (Priority: P1)

When the grace period has ended, a background job erases the player's game data and identities and keeps the financial
and audit trail with the player id replaced by an anonymous id, so accounting and investigations still add up but the
records no longer point to the person.

**Why this priority**: Without the purge the deletion is only a lock; ACC-37 requires progression/resource purge and
anonymized ledger/receipts.

**Independent Test**: With the clock advanced past `purgeAfter`, run the purge job; then scan every collection: no
player aggregate of the player remains, the original player id appears nowhere, receipt and ledger counts and sums
are unchanged under the anonymous id, and the released identity creates a new player on next login.

**Acceptance Scenarios**:

1. **Given** a player whose `purgeAfter` has passed, **When** the purge job runs, **Then** every registered `pa_*`
   document of the player is removed through the transaction runner (PRIV-01, DAT-09).
2. **Given** the same purge, **Then** in receipts, ledger entries, GM audit records, claim markers and module outbox
   projections the player's id (`playerId`, and `actorId`/audit target where it is the player) is overwritten with one
   anonymous id for that player; the records themselves are kept (PRIV-01, CMD-08, ACC-37).
3. **Given** the same purge, **Then** the player's identity mappings and session records are removed; a later login
   with the same guest credential or provider identity creates a new, empty player.
4. **Given** the purge job crashes or loses its lease half way, **When** it runs again (same or other instance),
   **Then** it continues where it stopped, repeating finished steps harmlessly, and the end state is the same as an
   uninterrupted run.
5. **Given** a player whose purge has started, **When** anyone presents an old token or a credential of that player,
   **Then** no session is issued for the old player id at any point during or after the purge.
6. **Given** a player pending deletion whose `purgeAfter` has not yet passed, **When** the job runs, **Then** the player
   is not touched.
7. **Given** purge has completed, **When** GM with `player.read` looks up the old player id, **Then** GM sees that the
   account was deleted and when, with no personal data and no link to the anonymous id.

---

### User Story 3 - GM sees deletion state and cancels during grace (Priority: P1)

A player who changed their mind contacts support. A GM finds the account, sees that it is pending deletion and when it
will be purged, and cancels the deletion; the player logs in again with the same credentials and nothing is lost.

**Why this priority**: PRIV-01 and ACC-37 require GM visibility (`player.read`) and cancellation (`player.ban`) during
grace.

**Independent Test**: Request deletion, cancel it through the GM API with a reason, and log in again with the same
guest and provider credentials; the progress is intact and one audit record exists.

**Acceptance Scenarios**:

1. **Given** a player pending deletion, **When** GM with `player.read` looks it up (by player id, mapping key or
   display name), **Then** the view shows the deletion state, `requestedAt` and `purgeAfter` (GM-02, PRIV-01).
2. **Given** a player pending deletion and `now < purgeAfter`, **When** GM with `player.ban` cancels with a reason,
   **Then** `profile.deleted` is cleared, identities resolve to the player again, and exactly one audit record with
   the 7 fields of SEC-14 (permission `player.ban`) is written in the same transaction (ACC-37).
3. **Given** a cancelled deletion, **When** the player logs in with any of its credentials, **Then** login succeeds as
   the same player with all data; old sessions stay revoked (a new login is required).
4. **Given** GM without `player.ban`, **When** cancelling, **Then** 403; nothing changes.
5. **Given** a player not pending deletion, **When** GM cancels, **Then** 409 with a code saying no deletion is
   pending; nothing changes.
6. **Given** `purgeAfter` has passed or purge has started, **When** GM cancels, **Then** 409 with a code saying the
   grace period is over; the purge is not affected. Cancel and purge start racing each other: exactly one wins.
7. **Given** a cancelled deletion, **When** the player later requests deletion again, **Then** a new 30-day grace
   period starts.

---

### User Story 4 - Background jobs run once across instances (Priority: P2)

Operators run several API instances. A registered background job (first user: the purge job) runs on at most one
instance at a time; if that instance dies, another takes over after the lease expires; a stalled former holder cannot
overwrite the new holder's work.

**Why this priority**: The purge must not run twice in parallel or be lost on restart (JOB-01, OPS-05); this is the
minimal lease part the slice needs, not the full durable job framework.

**Independent Test**: Start two hosts on the same Couchbase with a short lease; observe one holder; kill it and see the
other take over after expiry; inject a paused holder that resumes after losing the lease and verify its progress write
is refused.

**Acceptance Scenarios**:

1. **Given** two or more instances with the same registered job, **When** the job is due, **Then** only the instance
   holding the job's lease runs it; the lease records owner, `leaseUntil` and a fencing token (JOB-01).
2. **Given** the holder keeps running, **Then** it renews the lease before it expires; **Given** it stops renewing
   (crash), **Then** another instance acquires the lease after `leaseUntil` with a larger fencing token.
3. **Given** a former holder that lost its lease, **When** it tries to record progress, **Then** the write is refused
   because its fencing token is older, and it stops.
4. **Given** graceful shutdown, **Then** the holder stops at a safe point within the drain budget and releases the
   lease so another instance can continue at once (OPS-03).
5. **Given** two jobs registered with the same name, **Then** startup fails.

---

### User Story 5 - A module purges its own data (Priority: P2)

A game module that keeps player data outside player aggregates (its own collection, projection or claim markers)
declares how account deletion treats it and, if needed, contributes its own purge/anonymize step, without changing the
framework.

**Why this priority**: The framework is modular (EXT-01..05); PRIV-01 says *every* piece of player data is covered,
which only holds if modules cannot forget their collections.

**Independent Test**: The sample counter module's projection is anonymized by the purge; a test module with a custom
step sees it called with the player id and anonymous id, re-run safely; a host with a collection lacking a deletion
declaration fails to start.

**Acceptance Scenarios**:

1. **Given** a module that declares a collection, **When** the host starts, **Then** every declared collection states
   one deletion treatment: holds no player data / anonymize the player reference / remove the player's records /
   handled by the module's own step; a missing treatment, or "module step" without a registered step, fails startup
   with a message naming module and collection (ARC-06, ACC-26 style).
2. **Given** a module step, **When** a purge runs, **Then** the step receives the player id and the anonymous id, and
   the purge completes only after the step reports success; a failing step leaves the purge pending and it is retried
   on the next run.
3. **Given** a module step that already ran, **When** the purge is resumed, **Then** running it again changes nothing.

---

### Edge Cases

- Deletion request racing a login (takeover) on another device: either the login commits first and is then revoked by
  the deletion, or the login sees the deletion and returns `ACCOUNT_DELETED`; never a session that outlives the
  ≤ 60 s cache window.
- Command already past authentication (e.g. IAP verified outside the transaction) commits just after the deletion
  request: the grant is recorded normally and is anonymized/purged with everything else; no special rollback.
- Store refund/revoke notification (IAP-03, slice 012) for a player pending deletion or purged: out of this slice; the
  anonymized claim marker keeps the one-time guarantee, and 012 must handle a purged player.
- The anonymous id must not be derivable from the player id; the link is kept only while the purge is in progress and
  discarded when it completes.
- Purge of a player that never created some aggregate: missing documents count as already purged.
- A record kind that is append-only by rule (ledger, audit — "never deleted", CMD-08) is still overwritten in the
  player-id fields only; amounts, timestamps, receipt references and correlation ids are unchanged.
- Payload contents of receipts/outbox/audit (e.g. `before`/`after`) that a module knows to hold personal data are
  handled by that module's step; the framework only rewrites the standard identifier fields.
- Large history (many receipts/ledger entries): purge processes in bounded batches and resumes; no single transaction
  exceeds the transaction limits (≤ 10 s, DAT-04).
- Backlog of many due players: each job run handles a bounded number of players; the rest wait for the next run.
- Instance clocks differ: lease duration is far larger than tolerated clock skew; grace end is evaluated with server
  time (TIME-01).
- Restoring a backup taken before a purge would bring the data back: the restore runbook (slice 013) must re-run
  pending purges; out of this slice.
- Operational rolling logs that contain the player id expire with log retention (TBD-08, 30 files); they are not
  rewritten.

## Requirements *(mandatory)*

### Functional Requirements

**Lease jobs (minimal JOB-01)**

- **FR-001** (JOB-01, OPS-05): Background jobs are registered explicitly with a unique name, an interval and an
  enabled flag (configuration); a duplicate name fails startup. Every instance may host the job; only the lease holder
  runs it.
- **FR-002** (JOB-01): A lease document per job holds `{owner, leaseUntil, fencingToken}`; acquisition and renewal use
  CAS; each new acquisition increases the fencing token; the holder renews before expiry and stops when renewal fails.
  Lease documents do not use Couchbase expiry (DAT-03).
- **FR-003** (JOB-01): Progress a job records is accepted only with the current fencing token; a writer with an older
  token is refused and stops. Re-running a job after a crash is safe (idempotent steps).
- **FR-004** (OPS-03): On graceful shutdown the holder stops at a safe point within the drain budget and releases its
  lease.
- **FR-005** (JOB-01, constitution I): A job writes `pa_*` and receipts only through the transaction runner
  (`IPlayerAggregateWriter` path), never by plain key-value writes.

**Deletion request (PRIV-01)**

- **FR-006** (PRIV-01, IDN-10): Player command to request account deletion: one transaction {set `profile.deleted =
  {requestedAt, purgeAfter = requestedAt + grace}`, `sessionEpoch + 1`, receipt}; result returns `purgeAfter`.
  Idempotent by `requestId` (CMD-01..09). A command-group ban does not block it.
- **FR-007** (PRIV-01, IDN-08): After commit the serving instance stops accepting the player's sessions at once
  (cache refreshed); other instances within ≤ 60 s. Session records of the player are removed no later than purge.
- **FR-008** (PRIV-01, IDN-10, API-05): While pending, the player's identity mappings stay reserved to the player but
  are hidden: guest or provider login with them returns 401 `ACCOUNT_DELETED` (details carry `purgeAfter`), creates
  no session and no new player; link by another player returns `IDENTITY_CONFLICT`.
- **FR-009** (PRIV-01, GM-07): While pending, the player sends no commands and GM mutations on the player are refused,
  except the cancel action (FR-011).
- **FR-010**: Grace period default 30 days (Appendix B, TBD-04c), configurable per deployment, recorded in PCR.

**GM visibility and cancel (PRIV-01, GM-02, SEC-14)**

- **FR-011** (PRIV-01, GM-07, SEC-14): GM action to cancel a pending deletion with a reason; permission `player.ban`;
  runs through the command pipeline in one transaction {clear `profile.deleted`, receipt, audit with 7 fields};
  identities resolve to the player again; sessions are not restored. Allowed only while `now < purgeAfter` and the
  purge has not started; otherwise 409 (grace over); no pending deletion → 409 (not pending). Cancel and purge start
  are mutually exclusive.
- **FR-012** (PRIV-01, GM-02): GM with `player.read` sees deletion state (pending / purging / purged), `requestedAt`,
  `purgeAfter` and, after purge, the purge time; lookup by player id, mapping key and display name works while
  pending. After purge the view has no personal data and no link to the anonymous id. GM OpenAPI document updated.

**Purge and anonymization (PRIV-01, ACC-37)**

- **FR-013** (PRIV-01, JOB-01): A purge job (FR-001) finds players whose `purgeAfter` has passed without scanning all
  players, marks each as purging (from then cancel is refused), and processes a bounded number of players and records
  per run.
- **FR-014** (PRIV-01, DAT-09): Purge removes every registered `pa_*` document of the player through the transaction
  runner.
- **FR-015** (PRIV-01, CMD-08, CMD-09): Purge overwrites the player id in receipts, ledger entries, GM audit records,
  claim markers and module outbox projections with one anonymous id per player (`actorId` and audit target too where
  they are the player); records are not deleted and their other fields are unchanged. Receipts are rewritten only
  transactionally (DAT-09).
- **FR-016** (PRIV-01, IDN-04): Purge removes the player's identity mappings (guest and provider) so the identities can
  create a new player later; at no point during or after purge can any credential or old token authenticate as the
  purged player id.
- **FR-017** (PRIV-01, JOB-01): Purge is resumable and idempotent: progress per player is recorded with the fencing
  token; a re-run skips or harmlessly repeats finished steps; a failing step leaves the player in purging state for
  retry and raises a metric/log.
- **FR-018** (PRIV-01): The anonymous id is random (not derived from the player id); the player-id → anonymous-id link
  exists only during purge and is discarded at completion. A purge record without personal data (player id, requested,
  purged times) remains for GM and for answering stale tokens.

**Module extension (EXT-01, ARC-06)**

- **FR-019** (ARC-06, EXT-01): Every collection a module declares states its deletion treatment: no player data /
  anonymize player reference / remove player records / module step. Missing treatment, or module step without a
  registered step, fails startup naming module and collection. Built-in collections (identity, sessions, receipts,
  ledger, `extref_claims`, `gm_audit`, sample counter projection, GM staff/sessions, gate, key ring) are declared.
- **FR-020** (EXT-01): A module may register a deletion step that receives player id and anonymous id; steps must be
  idempotent; purge completes only when all steps succeed. Steps of modules run before identity mappings are released
  (FR-016) and before the profile is removed.

**SDK, contract, observability**

- **FR-021** (SDK-02): SDK `RequestAccountDeletionAsync()` (command, retry with same bytes) returns `purgeAfter`; on
  success the SDK drops the local session and raises the account-deleted event; `ACCOUNT_DELETED` details expose
  `purgeAfter` when present.
- **FR-022** (API-11, PCR-01): New error codes for cancel (not pending; grace over) in the catalog; PCR identity /
  platform / gm / unity-sdk updated with grace period, job interval, lease duration, batch sizes and deletion
  treatments.
- **FR-023** (OBS-01, LOG-05): Metrics for deletions requested / cancelled / purged, purge step failures, age of the
  oldest overdue purge, lease acquisitions/losses; logs carry no credential and only the player id needed to trace.

### Key Entities

- **Job lease**: one per job name — owner instance, `leaseUntil`, fencing token.
- **Job registration**: name, interval, enabled flag, the work to run.
- **Deletion request record**: player id, `requestedAt`, `purgeAfter`, state (pending → cancelled | purging → purged),
  purge progress (finished steps) with fencing token, anonymous id while purging, cancel actor/reason, purge time.
- **Anonymous player id**: random replacement for the player id in retained records; one per deleted player.
- **Collection deletion treatment**: per declared collection — none / anonymize / remove / module step.
- **Module deletion step**: module-provided, idempotent purge/anonymize of the module's own data.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of deletion requests make every session and credential of the player fail with `ACCOUNT_DELETED`
  — immediately on the serving instance, within 60 s on every instance (ACC-37).
- **SC-002**: After purge, a full scan of all collections finds 0 player aggregate documents and 0 occurrences of the
  original player id outside the purge record (ACC-37).
- **SC-003**: Receipt and ledger entry counts and per-resource ledger sums for the player are identical before and
  after purge (under the anonymous id).
- **SC-004**: 100% of GM cancellations within grace let the player log in again with the same credentials and
  unchanged data; 0 cancellations succeed after grace or purge start.
- **SC-005**: With 2 instances, injected crash and lease loss mid-purge, every due player ends fully purged with the
  same end state as an uninterrupted run, and no two holders run the same job at the same time.
- **SC-006**: With default settings, a due player is purged within 24 hours after `purgeAfter` (in tests: within one
  job run after the clock passes `purgeAfter`).
- **SC-007**: 100% of hosts with a declared collection lacking a deletion treatment fail at startup.

## Assumptions

- Cancel is a GM action only (PRIV-01 assigns it to `player.ban`); logging in during grace returns `ACCOUNT_DELETED`
  (IDN-10 order) and does not cancel. The client shows the purge date and points to support.
- Deletion is requested only by the player in-app. GM- or support-initiated deletion (e.g. for a login-banned player
  or an e-mail request) is out of scope; a login-banned player cannot authenticate and goes through support.
- "Hide identity" (PRIV-01 "xoá/ẩn", ACC-37 "identity ẩn") = keep the mapping reserved but refuse it with
  `ACCOUNT_DELETED` during grace, remove it at purge. Removing it at request time would let the identity create a new
  account during grace and make cancel impossible.
- Cancel is allowed until `purgeAfter`, not until the job actually starts, so the outcome does not depend on job
  latency.
- Anonymization overwrites standard identifier fields (`playerId`, `actorId`, audit target, claim marker `playerId`);
  receipt keys (hash including the player id) are kept — the hash cannot be reversed without the request id.
- Purge removes the full `pa_*` set per PRIV-01, which is stronger than ACC-37's "progression/resource".
- The display name is purged with `profile`; staff-written texts (ban/cancel reasons) are not scanned for personal data.
- Couchbase backups and operational logs follow their own retention (TBD-08); re-applying purges after a restore is a
  runbook item of slice 013.
- Receipt retention purge after 180 days (CMD-08, P0) does not exist yet; it is not part of this slice, but it should
  reuse the job runner of FR-001..005 (candidate: slice 011 for ACC-23 or 013).
- Default numbers (lease duration, renew period, job interval, batch size) are chosen in the plan and recorded in PCR.
- Unity sample client (KestrelDemo) changes are optional; the SDK method is tested in the SDK test suite.
