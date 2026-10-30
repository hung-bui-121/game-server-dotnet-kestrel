# Feature Specification: Analytics audit log for Login and Payment — default JSONL events per instance (slice 012)

**Feature Branch**: `012-analytics-audit`

**Created**: 2026-10-31

**Status**: Draft

**Input**: Owner task 3 of `tasks/task_30102026.txt` (2026-10-30): "implement audit log (default for the template): login
and payment", with the fields the data team needs — Payment: `userID` (string; player of the transaction, used for
`COUNT(DISTINCT userID)` per month, the main axis of the UL metric), `transactionID` (string; unique transaction id, used
to dedupe with `dropDuplicates`, for cleaning and tracing), `chargeTime` (timestamp; charge time, derives `log_month`;
"the log must state its time zone explicitly — the real UTC offset differs by country/region, e.g. VN UTC+7, TW/CN
UTC+8, EU UTC+0/+1"); Login: `userID` (string; player who logged in, used to find players still active in later months
M+1, M+2), `activeTime` (timestamp; login time, derives `log_month` and `log_date` for the `active_days > 3` condition;
time zone must be explicit). Owner decisions 2026-10-30 (`AgentMemories/features/task-30102026/00_plan.md`): sink = JSONL
file per instance and per event type, one file per UTC day (Q8); `userID` = raw `playerId`, not hashed, retention 25
months, documented (Q11); every record carries UTC time plus local time with offset plus IANA zone name; plan defaults
for Q9 (`chargeTime` = server grant commit time, store purchase time as an optional extra field), Q10 (a "resume" login
event at most once per player per day for players who reuse a stored session) and Q12 (time zone per deployment, IANA
name configured, offset computed per event, parallel UTC copy).

## Scope note

- Already in place (P0–011): IAP purchase command `iap.purchase` verified with the store before the transaction (IAP-01)
  and granted once per external reference through the claim marker `extref::iap_{store}::{reference}` committed with the
  grant and the receipt (IAP-02, ECO-08); replay of the same request returns the stored result without running the
  handler (CMD-02, CMD-04); rejected commands roll back everything they staged, including outbox entries; receipt outbox
  with inline relay after commit plus a sweeper, at-least-once, one consumer per outbox kind, retry with backoff and dead
  letter after the configured attempts (JOB-02, OPS-05); ledger projection with source `iap` (ECO-06); one common end of
  every successful login — guest, provider and confirmed switch (`LoginCompletion`, IDN-07, IDN-08, IDN-10, IDN-11);
  player bearer authentication with the account checks deleted → suspended → replaced and lazy session touch (IDN-08,
  IDN-10); server clock `IClock` (TIME-01); operational log as compact JSON per instance with a bounded drop queue
  (LOG-01..LOG-06) and instance id from `Observability:InstanceId` / `HOSTNAME` (LOG-03); readiness with degraded state
  and log-sink metrics (OBS-03); flavors local/dev (Development), beta/uat (Staging), live (Production) with startup
  validation and the `--provision` mode (slice 011); account purge with anonymization of `playerId` in receipts, ledger
  and audit (slice 010, PRIV-01).
- Gaps this slice closes: no analytics event exists — the only trace of a login is an operational log line, which may be
  dropped by design (LOG-06) and has no explicit time zone; payments exist as ledger entries keyed by the store reference
  (for Google the purchase token, which must not leave the server, LOG-05) without a local time or a test/sandbox flag;
  players who reuse a stored 30-day session never log in again, so a login-only signal under-counts active days.
- This slice adds: a framework analytics event stream (the first, narrow part of ANA-01) with two event types, `payment`
  and `login`; a default file sink (one JSONL file per instance, event type and UTC day) behind a replaceable sink seam;
  per-deployment time zone settings validated at startup; metrics and health for the sink; the payment event committed
  through the receipt outbox; the login event (and the once-a-day resume event) emitted best effort; documentation for the
  data team and operations.
- Not included: other analytics events (purchase refunds of IAP-03, economy, battle, tutorial… — P1 ANA-01 and slice
  iap-notifications), other sinks (Couchbase collection, Kafka/HTTP — P1 through the same seam), price and currency of
  purchases, per-player time zone (GeoIP), log shipper installation and data-lake jobs (operations / data team), HTML
  documentation (task 5), library packaging (task 4, skipped by owner), GM frontend (another developer, owner 2026-10-27).

## Clarifications

### Session 2026-10-31 (reconciliation with plan.md, deviations SD1–SD13)

- SD1 → FR-003: the payment event id is a digest of the outbox entry id and the receipt commit time; the entry id alone
  can repeat when CMD-07 supersedes an expired receipt with the same receipt id (request id reused after 24 h).
- SD2 → FR-024: consumers skip **any** line that is not valid JSON; after the restart repair a cut line can sit in the
  middle of a file, not only at its end.
- SD3 → edge case "rolling deploy or rollback": a pre-012 build counts a failed attempt for the unknown kind and
  dead-letters the receipt after the maximum relay attempts (about 3 h); dead letters are never swept again.
- SD4 → edge case "dead-lettered receipt", FR-038: no re-drive tool exists; the guide documents a manual re-drive query;
  a tool is deferred to the ops-readiness slice.
- SD5 → FR-021, US3-3/US3-4: the zone is required only when analytics is **active** (enabled and a sink registered).
- SD6 → SC-006, FR-035: an end-to-end scan test (records byte-identical to file lines) plus a scan of the files written
  by the sample-host smoke test replace "every file of the full suite" (test hosts write no files).
- SD7 → FR-031: still one section `GameServer:Analytics`, validated when serving, not in `--provision`.
- SD8 → FR-017: a resume is counted once bearer authentication succeeded, even if the request is refused afterwards.
- SD9 → FR-009: `chargeTime` = receipt `committedAtUtc` = server time of the committing attempt (≤ 10 s before commit).
- SD10 → FR-032: the git-ignore rule for the local analytics directory is anchored.
- SD11 → SC-004: the load tool gains a purchase step; an opt-in in-process comparison is added.
- SD12 → FR-013: inline delivery bound = 500 ms (configurable 50..5 000 ms), also used by sweeper deliveries.
- SD13 → new FR-039: `Enabled` must be uniform across the instances of a deployment. Plan D20 → new FR-040: outbox kind
  `analytics` is reserved.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Data team counts paying users per month (Priority: P1)

The data team computes UL (unique paying users per month) from the payment records: `COUNT(DISTINCT userID)` grouped by
the month of `chargeTime`. Every purchase the server grants appears exactly once after deduplication, no rejected or
replayed purchase adds a record, test/sandbox purchases are recognisable, and nothing secret is in the file.

**Why this priority**: This is the payment half of owner task 3 and the main axis of the UL metric; a missing or doubled
payment changes revenue reporting.

**Independent Test**: With the fake store verifier, make N distinct purchases for M players (some players buying twice),
replay some requests with the same request id, resubmit some receipts with new request ids (ALREADY_GRANTED), send
invalid receipts, and force the inline relay to fail for some purchases so the sweeper delivers them later; read every
payment file of every instance and check: after dedupe by `transactionID` there are exactly N records, `COUNT(DISTINCT
userID)` = M, every record has the required fields, and no record holds a purchase token or receipt payload.

**Acceptance Scenarios**:

1. **Given** a player whose purchase is verified and granted, **When** the purchase command commits, **Then** one payment
   record is written to the payment file of the delivering instance with `userID` = the player id, a `transactionID`
   unique to that purchase, `chargeTime` (local time with offset), its UTC copy, the IANA time zone name, store, product
   id, the test flag, `gameId`, `environmentId`, an event id and the schema version (Task 3, ANA-01).
2. **Given** a granted purchase, **When** the client sends the same request again (same request id, within or after a
   commit-unknown answer), **Then** the stored result is returned and no new payment record is written (CMD-02, CMD-04).
3. **Given** a receipt already granted, **When** the client submits it with a new request id, **Then** the answer is
   ALREADY_GRANTED and no payment record is written (IAP-02, ECO-08); **Given** an invalid receipt, unknown product or
   store failure, **Then** no record is written.
4. **Given** the inline relay of a committed purchase fails (sink write error, process stop right after commit), **When**
   the sweeper of any instance delivers the receipt later, **Then** the payment record is written then, with the same
   event id, `transactionID` and `chargeTime` as an earlier copy would have had; a record written twice (relay retried
   after a partial delivery) has identical identifying fields, so dedupe by `transactionID` or event id removes it.
5. **Given** a purchase verified as a store test or sandbox purchase (Google license-tester purchase, Apple Sandbox /
   Xcode / local testing environment, any purchase verified by the fake verifier), **When** it is granted, **Then** its
   record has the test flag set; real purchases have it cleared. **Given** the Production deployment, **Then** test
   purchases are already refused by the verifiers (existing rule), so live records always have the flag cleared.
6. **Given** a Google purchase, **When** the record is written, **Then** it contains neither the purchase token nor the
   receipt payload; the transaction id is the store's own transaction identifier (Google order id, Apple transaction
   id), and the store order id is also given for tracing (LOG-05).
7. **Given** the store reported its own purchase time during verification, **When** the record is written, **Then** that
   time is included as an optional extra field; `chargeTime` remains the server commit time of the grant (owner Q9).

---

### User Story 2 - Data team measures retention and active days (Priority: P1)

The data team finds which players are still active in months M+1 and M+2 and counts active days per player per month
(`active_days > 3`) from the login records. Every successful login of any kind produces a record; players who come back
with a stored session also produce a record once per day; refused logins produce none.

**Why this priority**: The login half of owner task 3. Without the resume record, games that store the session token
(30-day sliding session, IDN-08) would report most returning players as inactive.

**Independent Test**: Run guest logins, provider logins and confirmed switch logins; run logins refused for a banned,
deleted and suspended account, failed credentials and SWITCH_ACCOUNT; let a player with a stored session call commands
on two different local days without logging in; check one login record per successful login, none for refused ones,
and exactly one resume record per player per local day per instance.

**Acceptance Scenarios**:

1. **Given** a guest login, a provider login (Play Games, Game Center) or a confirmed switch login that succeeds,
   **When** the login returns the new session to the client, **Then** one login record is written with `userID`,
   `activeTime` (local time with offset), its UTC copy, the IANA zone name, `source = login`, the provider of the
   identity used, whether the login created the player, `gameId`, `environmentId`, an event id and the schema version
   (Task 3).
2. **Given** a login refused for any reason (account deleted or purged, suspended, wrong guest secret, invalid provider
   proof, rate limited, SWITCH_ACCOUNT not confirmed), **When** it is answered, **Then** no login record is written.
3. **Given** a player who sends an authenticated request with a stored session and has had no login or resume record on
   this instance during the current local day of the deployment, **When** authentication succeeds, **Then** one login
   record with `source = resume` is written; further requests of that player on the same local day on this instance
   write nothing (owner Q10).
4. **Given** a player who logged in on this instance today, **When** they send more requests later the same local day,
   **Then** no resume record is written.
5. **Given** a request whose session is refused (expired, replaced, account deleted or suspended), **When** it is
   answered, **Then** no resume record is written.
6. **Given** the login sink cannot keep up or the analytics directory is not writable, **When** players log in, **Then**
   every login still succeeds with unchanged latency; records that cannot be queued are dropped and counted; nothing
   blocks or fails the login (best effort, JOB-02 accepted loss).

---

### User Story 3 - Every record states its time unambiguously (Priority: P1)

The data team derives `log_month` and `log_date` in the deployment's local time without guessing: each record carries
the instant in UTC, the same instant in local time with its offset, and the IANA zone name. Zones with daylight saving
time get the right offset on each side of the change. Operations chooses the zone per deployment.

**Why this priority**: The owner flagged the time zone twice ("must state its time zone explicitly"); a wrong offset
moves purchases and logins near midnight into the wrong day or month.

**Independent Test**: With a controllable clock, emit records for deployments configured with `Asia/Ho_Chi_Minh`,
`Asia/Taipei` and `Europe/London` at instants on both sides of the London summer-time changes and around midnight local
and UTC; check offsets, local dates and that the local and UTC fields denote the same instant. Start hosts with an
unknown zone name, a missing zone in Staging/Production and on a system without time zone data; each fails at startup.

**Acceptance Scenarios**:

1. **Given** a deployment configured with an IANA zone, **When** any record is written, **Then** it contains the UTC
   instant (`Z` suffix), the local time with the offset valid at that instant (e.g. `+07:00`), the offset as a separate
   field and the zone name; all with millisecond precision (Task 3, owner Q12).
2. **Given** `Europe/London`, **When** records are written in January and in July, **Then** their offsets are `+00:00`
   and `+01:00`; around the change hours the offset matches the instant, never the configuration time or the process
   start time.
3. **Given** a zone name that cannot be resolved on the machine (typo, Windows-only id where IANA is required, container
   without time zone data or running in invariant globalization mode), **When** the host starts with analytics active,
   **Then** startup fails before traffic, naming the setting, the value and the likely fix (install time zone data).
4. **Given** a Staging or Production deployment with analytics active and no zone configured, **When** the host starts,
   **Then** startup fails naming the setting; **Given** Development or Test, **Then** the zone defaults to UTC and a
   startup warning says so.
5. **Given** the machine's own time zone differs from both UTC and the configured zone, **When** records and files are
   produced, **Then** neither the record times nor the file day boundaries depend on the machine's time zone; all times
   come from the server clock (TIME-01).

---

### User Story 4 - Operations runs the analytics files safely (Priority: P2)

DevOps points each deployment at an analytics directory on a persistent volume or a shipper, sees one set of files per
instance that a shipper can pick up once a day is over, and gets metrics and health signals when the sink drops or
fails. Analytics problems never stop gameplay.

**Why this priority**: Needed before live (losing files loses revenue data), but stories 1–3 deliver the events first.

**Independent Test**: Run two instances on one machine with distinct instance ids and one directory; cross 00:00 UTC with
a controllable clock; kill an instance mid-write and restart it; make the directory read-only and the disk full; start a
second process with the same instance id; check file names, line integrity, rollover, metrics, readiness and that
commands and logins keep working.

**Acceptance Scenarios**:

1. **Given** a running instance, **When** it writes records, **Then** each event type goes to its own file whose name
   contains the game id, the instance id (the same id as the operational log files, LOG-03), the event type and the UTC
   date; one record per line, UTF-8 JSON (owner Q8).
2. **Given** the server clock passes 00:00 UTC, **When** the next record is written, **Then** it goes to a new file for
   the new UTC date; a file whose UTC day is over is never appended to again, so a shipper may treat it as complete.
3. **Given** an instance restarted on the same day with the same instance id, **When** it writes, **Then** it appends to
   the existing file of the day; a line cut by an abrupt stop is never merged with the next record (the next record
   starts on a new line).
4. **Given** a second process configured with the same instance id and directory as a running one, **When** it starts,
   **Then** it fails at startup naming the instance id setting; two processes never write the same file (LOG-01).
5. **Given** the directory becomes unwritable or the disk is full, **When** records cannot be written, **Then** login
   records are dropped and counted, payment deliveries are left to the outbox sweeper (retried with backoff), a sink
   error metric increases, readiness reports Degraded (not Unhealthy) for a window, and commands and logins keep working
   (LOG-06, OBS-03).
6. **Given** the analytics directory has less free space than the configured threshold, **When** readiness is checked,
   **Then** it reports Degraded, as for the log directory.
7. **Given** a graceful stop (OPS-03), **When** the host drains, **Then** queued login records are written and files
   flushed before exit, within the drain budget.
8. **Given** the host runs in `--provision` mode, **When** it provisions the schema, **Then** no analytics file or lock is
   created and analytics settings are not required.

---

### User Story 5 - Game teams get analytics by default and can extend it (Priority: P2)

A game built on the framework gets login and payment records without writing code; it can turn analytics off, change
the directory and zone by configuration, observe events in its own tests, and later (P1) add event types or other sinks
without touching the framework's emission points.

**Why this priority**: The owner asked for this "as default for the template"; the seam is what lets P1 ANA-01 grow
without rework.

**Independent Test**: Start the sample host with only its flavor file: records appear. Turn analytics off: no file, no
lock, purchases still complete and their receipts are not left pending. In an in-process test, capture events without
files. Register a second sink in a test host and see both sinks receive every event.

**Acceptance Scenarios**:

1. **Given** the sample host in any flavor, **When** it starts with its committed flavor file, **Then** analytics is on
   with the flavor's directory and zone, and records are written without any game code.
2. **Given** analytics turned off by configuration, **When** players log in and purchase, **Then** no record and no file
   are produced, the outbox entries of purchases are acknowledged (no receipt stays pending or is dead-lettered because
   of analytics) and no analytics metric reports errors.
3. **Given** an in-process test, **When** it replaces the default sink with an in-memory sink, **Then** it can read the
   emitted events, with the same content as the file records, without touching the disk.
4. **Given** a game or a later slice registers another sink, **When** events are emitted, **Then** the emission points
   (purchase command, login, resume) are unchanged; the sink receives the same events and the same delivery guarantees.
5. **Given** the IAP purchase command (module code), **When** it records the payment event, **Then** it uses only the
   command context of Core (outbox entry), with no dependency on files, the sink or the host (ARC-01 arch test stays
   green).

---

### User Story 6 - Data team and operations find one documented contract (Priority: P3)

The data team reads one document that lists every field of every event type, its type, time semantics, delivery
guarantee and deduplication rule, and how to tell test from real purchases. Operations reads the settings per flavor, the
shipping requirement, the time zone data requirement of the image and the retention and privacy rules.

**Why this priority**: Makes stories 1–4 usable by people outside the framework team; the file format becomes a contract
with the data pipeline.

**Independent Test**: A data analyst who has not seen the code computes monthly unique payers and active days per
player from the files of a test run, using only the document, and matches the expected numbers; every setting named in
the docs exists in the options.

**Acceptance Scenarios**:

1. **Given** the PCR and the analytics guide, **When** the data team reads them, **Then** they find the schema of each
   event type with field names exactly as written, the schema version rule, at-least-once delivery and dedupe by
   `transactionID` / event id, the meaning of `source = resume`, the test flag, that `chargeTime` is the grant time, and
   the 25-month retention (PCR-01).
2. **Given** the deploy checklist items in the environments guide, **When** operations prepares a flavor, **Then** they
   find the analytics settings and their defaults per flavor, the persistent volume or shipper requirement with the
   same policy as LOG-03, the time zone data requirement of the container image, and the privacy note that analytics
   files keep the raw player id after account deletion (PRIV-01, owner Q11).

---

### Edge Cases

- A purchase committed on instance A whose inline relay fails is delivered by the sweeper of instance B: the record is in
  B's file, possibly on a later UTC day than its `chargeTime`. Consumers derive months from record fields, never from
  file names or file dates.
- A record re-emitted after a partial relay can appear in two instances' files or two days' files; dedupe by
  `transactionID` (payments) or event id (any event) is a data-pipeline step, documented as such.
- A purchase verified at 23:59 local and committed at 00:01 falls into the next day/month by `chargeTime` (owner Q9:
  accepted); the optional store purchase time lets the data team compare.
- Google test purchases may have no order id: the transaction id then falls back to a non-reversible digest of the store
  reference, stable for that purchase, so dedupe still works and no token is exposed.
- Google promo-code purchases are valid grants with no revenue: they are not test purchases; the record carries the
  store's purchase category when the store reports one so revenue reports can exclude them.
- A commit-unknown purchase (CMD-04) that actually committed: the record comes from the outbox of the committed receipt;
  the client's retry is a replay and writes nothing.
- A receipt dead-lettered after the maximum relay attempts because the analytics sink kept failing: the payment record
  is missing until operations re-drives the receipt; dead-lettered receipts are never swept again. No re-drive tool
  exists in this slice: the environments guide documents a manual query that resets the receipt's outbox bookkeeping so
  the sweeper relays it (a GM/ops re-drive tool is deferred to the ops-readiness slice). The existing dead-letter metric
  and alert cover it, and the sink error metric points at the cause.
- Rolling deploy or rollback: an instance running a build without the payment consumer cannot relay receipts carrying
  the payment entry (unknown outbox kind): it logs an error, counts a failed attempt and backs off, and the receipt is
  delivered by an instance of the new build. After a full rollback to a build without the consumer, receipts still
  pending at that moment are retried by the old build and dead-lettered after the maximum relay attempts (10, about
  3 h); after rolling forward they need the manual re-drive (previous edge case) — stated in the deploy notes.
- An instance with analytics disabled in a deployment whose other instances have it enabled acknowledges the payment
  entries it relays without writing them, so those payments have no record (FR-039: `Enabled` is uniform per
  deployment).
- Account purge (010) after a payment: the record already written keeps the raw player id (owner Q11); a receipt still
  pending relay at purge time is anonymized by the purge first, so its late record carries the anonymous id.
- A player logs in on two instances the same day, or an instance restarts: several login/resume records for the same
  player and day are possible; `active_days` counts distinct local dates, so duplicates do not change it.
- A request authenticated by the development test scheme (Development/Test only) is not a resume.
- A player with an extremely high request rate: the resume check is an in-memory lookup per request; the per-instance
  set of players seen today is bounded and reset per local day; overflow may cause an extra resume record, never a block.
- The deployment zone changes (configuration change and restart): records before and after carry their own zone and
  offset; nothing is rewritten.
- The local day of the resume rule follows the configured zone; the file day follows UTC. Both are documented.
- Very large days (many logins on one instance): one file per instance, type and day without size splitting; the guide
  gives a size estimate per million logins so operations sizes the volume.
- Clock jumps backwards (NTP correction) around 00:00 UTC: a file of a closed day is not reopened; records keep their
  own timestamps.
- Disk full while a payment record is being written: the line is not acknowledged; a partial line may remain and is
  followed by a new line on the next write; the payment is retried by the sweeper.
- Analytics directory on the same volume as the operational logs: allowed; free-space checks apply to both.
- Two hosts on one machine without distinct instance ids (e.g. `start-hosts.ps1` without its id variable): the second
  fails at startup (story 4 scenario 4); the script already sets distinct ids.

## Requirements *(mandatory)*

### Functional Requirements

**Event stream and sink seam (Task 3; ANA-01, JOB-02, ARC-01, LOG-06)**

- **FR-001** (ANA-01): The framework emits analytics events of two types in this slice, `payment` and `login`, to every
  configured analytics sink. Events are not written through the operational log pipeline and do not depend on its buffer
  (LOG-06: audit/ledger-like records must not depend on the log buffer).
- **FR-002** (ANA-01): The sink is a replaceable seam: the framework provides the default file sink (FR-022..FR-030) and
  an in-memory sink for tests; a game or a later slice may add or replace sinks (e.g. Couchbase collection, Kafka/HTTP in
  P1) without changing emission points.
- **FR-003** (ANA-01, JOB-02): Payment events are committed as an outbox entry of the purchase receipt in the same
  transaction as the grant and delivered to the sinks by the existing relay and sweeper: at-least-once, with an event id
  stable across redeliveries and unique per purchase, derived from the outbox entry and the receipt commit time (a
  non-reversible digest of both: the outbox entry id alone can repeat when a receipt expired after the 24 h replay
  window is superseded by a new one with the same receipt id, CMD-07). The receipt id is a separate field for tracing.
- **FR-004** (JOB-02, PERF-01): Login and resume events are emitted best effort through a bounded in-process queue: they never
  block, delay measurably or fail the request; when the queue is full or the sink fails they are dropped and counted.
  The accepted loss is recorded in PCR (JOB-02: in-memory events only when loss is accepted and recorded).
- **FR-005** (ARC-01): Module (application) code records events only through Core abstractions (the command context's
  outbox); file, sink and host types are reachable only from adapters and platform. The architecture tests stay green.
- **FR-006** (ANA-01): Every event has a schema version; adding optional fields keeps the version, removing or changing
  the meaning or type of a field increments it. The schema is a PCR semantic item (PCR-01).

**Payment event (Task 3; IAP-01, IAP-02, ECO-08, CMD-02, CMD-04, LOG-05, owner Q9)**

- **FR-007** (Task 3): Each purchase granted by `iap.purchase` produces exactly one payment event before dedupe of
  redeliveries. Fields: `event = payment`, schema version, event id, `gameId`, `environmentId`, `userID` (raw player
  id), `transactionID`, `chargeTime` (local), `chargeTimeUtc`, time zone name, UTC offset, store, product id, store
  order id (when the store gives one), `isTest`, receipt id. Exact JSON names beyond the owner's are fixed in the plan
  and PCR.
- **FR-008** (Task 3, IAP-02, LOG-05): `transactionID` is unique per granted purchase, identical in every redelivery of
  the same event, and prefixed by the store; it is the store's own transaction identifier (Apple transaction id, Google
  order id) and, when the store gives none, a non-reversible digest of the store reference. It never contains a Google
  purchase token, a receipt payload or a JWS.
- **FR-009** (owner Q9): `chargeTime` is the server grant time recorded on the receipt (`committedAtUtc`): the server
  clock time of the transaction attempt that committed the grant, at most the transaction expiration (10 s) before the
  actual commit (the exact commit instant is not recorded anywhere). When the verifier obtained the store's purchase
  time, it is included as an optional extra field in UTC.
- **FR-010** (Task 3, IAP-01): `isTest` is true for purchases the store marks as test or sandbox (Google license-tester
  purchase type, Apple environment Sandbox, Xcode or local testing) and for every purchase verified by the fake verifier;
  false otherwise. The verified-purchase result carries this flag from the verifier. The store's purchase category
  (e.g. Google promo) is included when reported.
- **FR-011** (CMD-02, CMD-04, IAP-02, ECO-08): A replayed request (same request id), a rejected purchase (INVALID_RECEIPT,
  ALREADY_GRANTED, UNKNOWN_ITEM, any economy rejection) and a failed verification produce no payment event.
- **FR-012** (DAT-04, D2): The payment outbox entry is small and keeps the purchase receipt within the receipt size limit
  (≤ 4 KiB); it holds no player identifier beyond those the receipt already holds, so the 010 purge anonymization covers
  it without a new rule.
- **FR-013** (PERF-01, JOB-02): Delivery of the payment event on the inline relay path is bounded in time; a slow or failed
  write leaves the receipt pending for the sweeper and never fails the purchase or delays its response by more than the
  bound: 500 ms by default (configurable 50..5 000 ms, recorded in PCR); the same bound applies to sweeper deliveries.
  A payment delivery is acknowledged only once the record has been handed to the operating system as a complete line.
- **FR-014** (JOB-02): When analytics is disabled, payment outbox entries are acknowledged without writing, so receipts
  are never left pending or dead-lettered because of analytics.

**Login event (Task 3; IDN-07, IDN-08, IDN-10, IDN-11, owner Q10)**

- **FR-015** (Task 3, IDN-07): Each login that returns a new session — guest, provider (Play Games, Game Center) or
  confirmed switch — emits one login event: `event = login`, schema version, event id (unique), `gameId`,
  `environmentId`, `userID`, `activeTime` (local), `activeTimeUtc`, time zone name, UTC offset, `source = login`,
  provider name of the identity used, and whether this login created the player.
- **FR-016** (IDN-10, PRIV-01, BAN-01): Refused or failed logins emit nothing: account deleted or purged, suspended,
  wrong credential, invalid provider proof, rate limited, unconfirmed SWITCH_ACCOUNT, and logins that fail after the
  session was created but before success is returned. Identity link is not a login and emits nothing.
- **FR-017** (owner Q10, IDN-08): A request whose player bearer authentication succeeded (session valid, account checks
  passed, epoch current) emits a login event with `source = resume` when this instance has emitted neither a login nor a
  resume event for that player during the current local day of the deployment zone. This holds even when the request is
  refused after authentication (gate, permission, body limit): the player was demonstrably active. At most one resume
  event per player, local day and instance; the provider field is absent for resume; the "created the player" flag is
  false.
- **FR-018** (owner Q10): A login event marks the player as seen for the current local day on that instance, so a later
  request the same day does not emit a resume event. The per-day set is held in memory, bounded, and cleared at local
  midnight; losing it (restart, overflow) may produce an extra resume event and never blocks a request.
- **FR-019** (OPS-09, SEC-08): Requests authenticated by the development test scheme, GM requests and public requests never emit
  resume events.

**Time (Task 3; TIME-01, LOG-04, owner Q12)**

- **FR-020** (TIME-01, owner Q12): Every event carries its instant from the server clock in three forms: UTC with `Z`,
  local time with offset (ISO-8601, millisecond precision), the UTC offset as its own field, plus the IANA zone name of
  the deployment. The offset is computed per event from the zone rules at that instant (daylight saving correct).
- **FR-021** (OPS-01, ARC-06, owner Q12): The zone is one setting per deployment (IANA name). With analytics active
  (enabled **and** at least one sink registered), an unresolvable or non-IANA name (e.g. a Windows zone id) fails startup
  naming the setting, the value and the fix (time zone data / ICU in the image); a missing value fails startup in Staging
  and Production and defaults to UTC with a startup warning in Development and Test. With analytics inactive the zone is
  not required (a host that registers no sink starts as before, with one warning when analytics is enabled).

**File sink (owner Q8; LOG-01, LOG-03, LOG-06, OBS-01, OBS-03, OPS-03)**

- **FR-022** (owner Q8, LOG-03): The default sink writes one file per instance, event type and UTC day; the file name
  contains the game id, the instance id (the same resolution as the operational log: setting, then `HOSTNAME`, then
  machine name), the event type and the UTC date. One event per line, UTF-8 JSON, no other content.
- **FR-023** (owner Q8): The file day is the UTC date of the server clock at write time, never the machine's local time
  zone; after the day changes, the previous day's file is never appended to again.
- **FR-024** (LOG-01): A restarted instance appends to the file of the current day; every write starts on a new line, so
  a line cut by an abrupt stop never merges with the next record. Consumers skip any line that is not valid JSON: after
  the restart repair a cut line can sit in the middle of the file, not only at its end.
- **FR-025** (LOG-01): Two processes never write the same analytics file: a process whose instance id and directory are
  already in use by a running process fails at startup naming the instance id setting; a crashed holder does not block
  the next start.
- **FR-026** (LOG-06, OBS-01): The login queue is bounded (default 10 000 events, configurable) and never blocks;
  dropped events increase a drop counter by event type. Metrics: events written per type, events dropped per type, sink
  write errors; labels never include player ids (OBS-02).
- **FR-027** (LOG-06, OBS-03): A sink write error or less free space in the analytics directory than the configured
  threshold makes readiness Degraded (not Unhealthy) for a window, as for the operational log; liveness is unaffected.
- **FR-028** (OPS-03): On graceful stop, queued events are written and files flushed within the drain budget; events
  still queued when the budget ends are counted as dropped.
- **FR-029** (TBD-08, owner Q11): The server never deletes analytics files by default; an optional age limit for local
  files may be configured. Retention of shipped analytics data is 25 months, a documented policy of the data store, not
  enforced by the server.
- **FR-030** (DAT-07, slice 011): The `--provision` mode starts no sink, takes no lock and needs no analytics settings.

**Configuration and defaults (OPS-01, ARC-06, ARC-10)**

- **FR-031** (OPS-01): Analytics settings live in one section (`GameServer:Analytics`): enabled (default on), directory
  (default `analytics`, relative paths resolved against the content root, separate from the log directory), time zone,
  queue size, inline delivery bound (FR-013), resume tracker capacity (FR-018), low-disk threshold, degraded error
  window, optional local age limit. The section is validated when the host serves, never in `--provision` (FR-030). The
  directory is created when missing; an uncreatable or unwritable directory fails startup naming the setting.
- **FR-032** (Task 3 "default for the template", OPS-01): The sample host's committed flavor files set analytics on and an
  explicit zone in every flavor (local, dev, beta, uat, live); the local flavor's directory is ignored by git with an
  anchored rule that does not hide source folders named `Analytics` (case-insensitive checkouts).
- **FR-033** (ARC-09, ARC-10): `gameId` and `environmentId` in every event come only from the deployment configuration.
- **FR-034** (LOG-05, SEC-11): Settings contain no secret; analytics adds no key to `GameServer:SecretKeys`.

**Privacy and security (LOG-05, PRIV-01, OBS-02, owner Q11)**

- **FR-035** (LOG-05): No event contains a session token, password or guest secret, provider token or auth code, Google
  purchase token, receipt payload or JWS, provider subject id, client IP or request body. An end-to-end test drives
  every emitting flow (guest, provider and switch login, resume, Google purchase with and without order id, Apple
  purchase) and checks the records — the same bytes the file sink writes — for these values and for the exact documented
  key sets; the sample-host smoke test scans the analytics files it writes on disk (ACC-25 analogue).
- **FR-036** (owner Q11, PRIV-01): `userID` is the raw internal player id. The account purge (010) does not rewrite
  analytics files already written; this is stated in PCR, in the guide and as an input for the game's privacy policy,
  together with the 25-month retention.

**Documentation and contract (PCR-01, OPS-02)**

- **FR-037** (PCR-01): The platform PCR records the analytics contract: event types, fields with types and time
  semantics, schema version rule, delivery guarantee per type (payment at-least-once through the outbox; login best
  effort with accepted loss), dedupe keys, file naming and day rule, settings with defaults and per-flavor values,
  metrics and health effects, inline payment-write bound, privacy and retention. The IAP PCR records the payment event and
  the test flag; the identity PCR records the login and resume events.
- **FR-038** (OPS-02): The environments guide adds the analytics settings per flavor and deploy checklist items:
  persistent volume or shipper for the analytics directory with the same policy as LOG-03, time zone data in the image,
  distinct instance ids for processes sharing a directory, rollout/rollback note on the new outbox kind (a full rollback
  dead-letters purchase receipts still pending after the maximum relay attempts), and the manual re-drive procedure for
  dead-lettered receipts (no re-drive tool in this slice; deferred to the ops-readiness slice).

**Deployment consistency (added 2026-10-31 from plan SD13 and D20)**

- **FR-039** (JOB-02, OPS-01): `GameServer:Analytics:Enabled` must be identical on every instance of a deployment: an
  instance with analytics disabled acknowledges payment outbox entries committed by enabled instances without writing
  them (FR-014). The rule is stated in the platform PCR and in the deploy checklist of the environments guide.
- **FR-040** (OPS-05, PCR-01): The outbox kind `analytics` is reserved by the framework; a game consumer or projection
  registered with that kind fails startup with the existing duplicate-kind error. Announced as a breaking item in PCR.

### Key Entities

- **Analytics event**: one business fact for the data pipeline — type (`payment` or `login`), schema version, event id,
  game and environment id, `userID`, instant (UTC, local with offset, zone name) and type-specific fields.
- **Payment event**: a granted purchase — `transactionID`, `chargeTime`, store, product, order id, test flag, receipt id,
  optional store purchase time and category; committed with the grant, delivered at least once.
- **Login event**: a successful login (`source = login`, provider, created-player flag) or the first authenticated
  request of a player on a local day (`source = resume`); best effort.
- **Analytics sink**: destination of events; the default file sink, the in-memory test sink, future sinks.
- **Analytics file**: JSONL file per instance, event type and UTC day; append-only; closed when the UTC day ends.
- **Deployment time zone**: IANA zone name configured per deployment; source of the local time and offset of each event
  and of the local day used by the resume rule.
- **Analytics settings**: enabled, directory, zone, queue size, low-disk threshold, optional local age limit.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the payment test of story 1 (N granted purchases with replays, ALREADY_GRANTED resubmissions, invalid
  receipts and forced relay failures), the files of all instances contain exactly N distinct `transactionID` values, 0
  records for rejected or replayed requests, and `COUNT(DISTINCT userID)` equals the number of distinct buyers.
- **SC-002**: 100% of successful logins (guest, provider, switch) in the test suite produce one login record; 0 records
  for refused logins; exactly one resume record per player, local day and instance in the resume test.
- **SC-003**: 100% of records carry UTC, local-with-offset and zone name that denote the same instant; offsets are correct
  for `Asia/Ho_Chi_Minh`, `Asia/Taipei` and `Europe/London` on both sides of each daylight-saving change.
- **SC-004**: With analytics on, login p95 latency increases by at most 1 ms and purchase p95 latency by at most 5 ms
  compared with analytics off, measured by an opt-in in-process comparison and by the load tool (which gains a purchase
  step with the fake verifier) on the local flavor; with the analytics directory unwritable, 100% of logins and
  purchases still succeed.
- **SC-005**: Files roll at 00:00 UTC regardless of the machine time zone; no record is written into a file whose UTC date
  differs from the server clock's UTC date at write time.
- **SC-006**: An end-to-end scan test over every emitting flow (records byte-identical to the file lines) and a scan of
  every analytics file written by the sample-host smoke test find 0 session tokens, guest secrets, purchase tokens,
  receipt payloads or passwords (test hosts use the in-memory sink, so the rest of the suite writes no analytics file).
- **SC-007**: 100% of the misconfiguration cases (unknown zone, missing zone in Staging/Production, unwritable directory,
  duplicate instance id on one directory) fail at startup before serving traffic, each message naming the setting.
- **SC-008**: At three times the assumed login rate (ACC-16: 20 logins/s, burst ×3) with the default queue, 0 login
  records are dropped; with the sink blocked, memory stays bounded and drops are counted.
- **SC-009**: A data analyst using only the published contract computes monthly unique payers and active days per player
  from the files of a test run and matches the expected values.

## Assumptions

- `transactionID` uses the store's transaction identifier rather than the claim reference because the Google reference is
  the purchase token, a credential-like value that LOG-05 forbids in shipped data; the Google order id is unique per
  one-time purchase. The digest fallback exists only for purchases without an order id (test purchases).
- Test purchases are already refused in Production by both verifiers (existing code), so `isTest` separates sandbox data
  of local/dev/beta/uat from live when files of several environments meet in one data lake; `environmentId` is in every
  record for the same reason.
- `chargeTime` is the receipt's recorded commit time, i.e. the committing attempt's server time (owner Q9, FR-009). The
  optional store purchase time comes from data the verifiers
  already receive (Google purchase time, Apple purchase date); no extra store call is made.
- Login events are emitted after the login has succeeded (session created and response built); "after the session is
  created" in the prior analysis is read as "on success" so that failed logins are not counted.
- The resume rule is per instance and in memory (no database write per request); duplicates across instances or
  restarts do not affect `active_days`, which counts distinct local dates. The local day uses the deployment zone.
- The zone default for the sample host's flavor files is `Asia/Ho_Chi_Minh` (the team's current market); a game changes
  it per deployment. Development and Test fall back to UTC only when the setting is absent.
- The analytics directory is separate from the operational log directory so that shipping and retention can differ; both
  need a persistent volume or a shipper (LOG-03). The server does not ship files.
- The file sink flushes each payment record before acknowledging it and flushes login records at least once per second;
  the exact batching is a plan decision within the latency criteria of SC-004.
- The payment outbox kind is consumed by the analytics delivery, not projected into a collection; no new collection,
  index or deletion treatment is needed (010 rule "every collection states its deletion treatment" is unaffected).
- Raw `playerId` in files is accepted by the owner (Q11) as an internal id, with 25-month retention; this is not a
  statement that analytics files are outside the game's privacy obligations — the game's policy must cover them.
- Price and currency are not in the payment event; the data team maps `productId` to price from the store or catalog.
- Volume estimate for sizing: a login record is about 400 bytes, so one million logins is about 0.4 GB per day before
  compression.

## Out of Scope

- Analytics events other than `payment` and `login` (refund/revoke from IAP-03 — next IAP slice, economy, gameplay, GM),
  and a game-facing API to emit custom events from module code beyond the outbox seam (P1 ANA-01).
- Sinks other than the JSONL file and the in-memory test sink (Couchbase collection with export job, Kafka, HTTP) — P1.
- Log shipper installation, data-lake ingestion, UL / retention queries and dashboards (data team, operations).
- Per-player time zone or country (GeoIP), device or app-version fields, price and currency.
- Rewriting or deleting shipped analytics data on account deletion (owner Q11); hashing of `userID`.
- Size-based splitting of analytics files, compression, server-side upload.
- HTML documentation (task 5), library packaging (task 4), GM frontend (owner 2026-10-27).
