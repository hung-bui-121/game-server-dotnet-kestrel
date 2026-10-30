# Data model — 010 jobs-account-deletion

All collections live in scope `g_<gameId>` (DAT-07). "TX" = written only inside transactions (`EnsureWritable` refuses
KV writes). "KV" = plain key-value with CAS. No new document uses Couchbase expiry (DAT-03).

## `pa_profile/{playerId}` — `state.deleted` (changed, identity)

| Field | Type | Rule |
| --- | --- | --- |
| `requestedAtUtc` | ISO-8601 (STJ `DateTimeOffset`, offset 0) | set by `identity.request_deletion` = `ctx.Now` |
| `purgeAfterUtc` | same | `requestedAtUtc + Identity:Deletion:GraceDays` |
| `purgeStartedAtUtc` | same, nullable — **new** | set by the purge start transaction; from then cancel is refused |

`deleted = null` ⇔ not deleted. Additive field: `schemaVersion` stays 1, no upgrader. Not sent to the client (wire
`ProfileState` has only `display_name`, `session_epoch`).

State machine (single source of truth for access and cancel = this field; for progress = the deletion record):

```text
(none) --request--> pending {purgeStartedAtUtc = null}
pending --GM cancel (now < purgeAfter)--> (none)
pending --purge start (now >= purgeAfter)--> purging {purgeStartedAtUtc set} + record inserted   [same transaction]
purging --purge complete--> profile removed; record.state = purged                              [same transaction]
```

New index (identity descriptor, PERF-07): `ix_profile_deletion_due` ON `pa_profile(state.deleted.purgeAfterUtc)` WHERE
`state.deleted.purgeStartedAtUtc IS NOT VALUED` — holds only pending players.

## `account_deletions/deletion::{playerId}` (TX — new, platform)

Progress document and fencing anchor while purging; purge record afterwards (FR-017, FR-018).

| Field | Type | While purging | After purge |
| --- | --- | --- | --- |
| `playerId` | string | yes | yes (purge record; the only place the old id remains, SC-002) |
| `gameId`, `environmentId` | string | yes | yes |
| `state` | `purging` \| `purged` | `purging` | `purged` |
| `requestedAtUtc`, `purgeAfterUtc` | `FormatTime` string | copied from profile at start | kept |
| `purgeStartedAtUtc` | string | yes | kept |
| `purgedAtUtc` | string? | null | set |
| `anonymousId` | string? | `anon_` + 26 base32 | **removed** (FR-018) |
| `fencingToken` | int64 | highest job lease token that wrote it | kept |
| `completedSteps[]` | string[] | step ids of research R7 | removed |
| `documentsProcessed` | int64 | running count | kept (no personal data) |
| `attempts` | int | consecutive failures | removed |
| `lastFailure` | `{step, errorType, atUtc}`? | last failure, no message | removed |
| `nextAttemptAtUtc` | string? | `now` at start; `now + backoff` after a failure | null |

Writes: only `AccountPurgeWriter`, each in a transaction that first checks `fencingToken <= ticket.FencingToken`.
Index: `ix_account_deletions_due` ON `account_deletions(nextAttemptAtUtc)` WHERE `purgedAtUtc IS NOT VALUED`.
Retention: kept (no expiry, DAT-03); holds no personal data beyond the pseudonymous old player id and times.

## `leases/leases::{jobName}` (KV, CAS — new, platform)

| Field | Type | Rule |
| --- | --- | --- |
| `jobName` | string | `^[a-z][a-z0-9._-]{1,63}$` |
| `owner` | string? | `{machine}:{pid}:{8 hex}`; null = released |
| `fencingToken` | int64 | +1 on every acquisition (including takeover) |
| `leaseUntilUtc` | string | `now + LeaseSeconds` on acquire/renew; `now` on release |
| `nextRunAtUtc` | string | set on release: `now + interval` (finished) or `now` (interrupted) |
| `acquiredAtUtc`, `renewedAtUtc` | string | diagnostics |
| `lastRun` | `{startedAtUtc, completedAtUtc, result}`? | `JobRunResult` name |
| `gameId`, `environmentId` | string | |

Acquire when missing (insert, token 1), or `owner == null && nextRunAtUtc <= now`, or
`owner != null && leaseUntilUtc <= now` (takeover). Never written in a transaction.

## Receipts after anonymization (`receipts/rcpt::{receiptId}`, changed fields only)

| Field | After purge |
| --- | --- |
| `playerId` | anonymous id |
| `actorId` | anonymous id when it was the player (Player commands); GM staff id unchanged |
| `outbox[].payload.*` | every string equal to the player id → anonymous id |
| `resultCompact`, `bodyHash` | `""` (personal data in results; replay window long expired) |
| `claimKeys[]` | entries into a Remove-treated collection → `"{collection}/redacted"` |
| everything else | unchanged (operation, codes, aggregates/revs, committedAtUtc, correlationId, outbox flags, key) |

Index `ix_receipts_player (playerId, committedAtUtc DESC, META().id DESC)` moves from the gm descriptor to platform
provisioning.

## Collection deletion treatments (FR-019)

| Collection | Owner | Treatment | Player field | Reference index |
| --- | --- | --- | --- | --- |
| `pa_*` | platform | Remove by key | key | — |
| `receipts` | platform | Anonymize (receipt rules) | `playerId` | `ix_receipts_player` |
| `gm_audit` | platform | Anonymize | `playerId` | `ix_gm_audit_player` |
| `account_deletions` | platform | purge record (excluded) | — | — |
| `leases`, `dataprotection`, `gate` | platform | NoPlayerData | — | — |
| `identities` | identity | Remove | `playerId` | `ix_identities_player_ref` (new) |
| `sessions` | identity | Remove | `playerId` | `ix_sessions_player_ref` (new) |
| `ledger` | economy | Anonymize | `playerId` | `ix_ledger_player` |
| `extref_claims` | economy | Anonymize | `playerId` | `ix_extref_claims_player_ref` (new) |
| `gm_staff`, `gm_staff_identities`, `gm_sessions` | gm | NoPlayerData | — | — |
| `counter_log` | sample.counter | Anonymize | `playerId` | `ix_counter_log_player_ref` (new) |

A game module adding a collection or projection target must add a row (startup fails otherwise, SC-007).

## Anonymous id

`anon_` + 26 characters base32 (128 random bits, `RandomNumberGenerator`). One per deleted player, generated at purge
start, stored only in the record while purging, never logged. Passes `GmHttp.RequirePlayerId`, so GM can list
receipts/ledger by the anonymous id when it appears in a financial investigation, but nothing links it back.

## Configuration (PCR)

| Key | Default | Bounds | Owner |
| --- | --- | --- | --- |
| `Identity:Deletion:GraceDays` | 30 | 1..90 | identity (FR-010, TBD-04c) |
| `GameServer:Jobs:Enabled` | true | — | platform (`false` in `TestHost` defaults) |
| `GameServer:Jobs:LeaseSeconds` | 60 | 30..600, ≥ 3 × `Transactions:ExpirationSeconds` | platform |
| `GameServer:Jobs:RenewSeconds` | 20 | 1..LeaseSeconds/3 | platform |
| `GameServer:Jobs:PollSeconds` | 30 | 5..LeaseSeconds | platform |
| `GameServer:Jobs:Settings:{job}:Enabled` | descriptor default (true) | — | platform |
| `GameServer:Jobs:Settings:{job}:IntervalSeconds` | descriptor default | 10..86400 | platform |
| `identity.account-purge` default interval | 600 s | | identity descriptor |
| `GameServer:Purge:MaxPlayersPerRun` | 50 | 1..500 | platform |
| `GameServer:Purge:BatchSize` | 25 | 1..100 (documents per transaction) | platform |
| `GameServer:Purge:MaxDocumentsPerRun` | 5000 | 100..100000 | platform |
| `GameServer:Purge:RetryDelayMinutes` | 10 | 1..1440 (doubles per failure) | platform |
| `GameServer:Purge:MaxRetryDelayMinutes` | 1440 | ≥ RetryDelayMinutes | platform |

Throughput at defaults: 50 players / 10 min ≈ 7 200 players/day; a player with 100 k history documents needs ~20 runs.
Alert `account_purge_overdue_seconds > 86400` (SC-006).
