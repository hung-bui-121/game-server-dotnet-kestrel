# Data model — 016 login-command

Entities, state, documents, formats and lines of [spec.md](spec.md). Decisions are in [research.md](research.md). C#
signatures are in [contracts/public-api.md](contracts/public-api.md), proto and JSON in
[contracts/wire-changes.md](contracts/wire-changes.md), the device file in
[contracts/sdk-pending-store.md](contracts/sdk-pending-store.md), codes and lines in
[contracts/errors-and-report-lines.md](contracts/errors-and-report-lines.md).

No Couchbase collection, index or document **shape** changes. The account creation writes ordinary `pa_*` documents, one
ordinary receipt and ordinary claim markers.

## 1. Account creation

### 1.1 Account initializer registration (Core, immutable, from `ModuleDescriptor.AccountInitializers`)

| Field | Rule |
| --- | --- |
| `Name` | `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, starting with the first segment of the module name and a dot (T082 m10; was `^[a-z][a-z0-9_]*\.[a-z][a-z0-9_.]*$`), unique across modules |
| `InitializerType` | implements `IAccountInitializer` (enforced by `Of<T>`); created per attempt with `ActivatorUtilities` |
| `Touches` | distinct registered aggregate ids, never `profile`; may be empty (an initializer that only adds outbox entries) |
| `MaxClaimMarkers` | 0..2; sum over all initializers ≤ 2 |
| order | module registration order, then list order in the descriptor |

Derived at startup (once, `AccountCreation.Describe(registry)`):
- the **creation descriptor**: operation `identity.create_account`, touches = union of initializer touches in first-seen
  order, `maxClaimMarkers` = sum, profile `Player` (audit rule expects 0 entries);
- the **creation touch set** = `profile` + union. Its size must be ≤ 4 (R4).

### 1.2 Creation transaction (one attempt)

```text
read [pa_profile/{p}]
├─ exists ─► mutate(profile) ─► replace if changed ─► Updated(profile', rev+1)          (no receipt)
└─ absent ─► read [identities/{mappingKey}, pa_{t1}/{p} … pa_{tn}/{p}]
             anchor missing / other player ─► AggregateAnchorMissingException (nothing written)
             load touches (typed failure ─► abort)
             ctx = CommandContext(systemActor, now, config, touches, creationDescriptor, tx, receiptId)
             for each initializer i: code = i.InitializeAsync(InitializerContext(ctx, i))   (code ≠ null ─► abort)
             profile' = mutate(profileDescriptor.CreateDefault())
             commit rules (before any staged write): aggregate sizes, audit = 0, receipt size
             stage: anchor rewrite, insert profile', insert/replace changed touches, insert claims,
                    insert receipt  (only when writes ∪ claims ∪ outbox ≠ ∅)
             ─► Created(profile', 1, receiptId?)
```

The decision `exists | absent` belongs to the attempt. A new `CommandContext` is created per attempt. The writer's outer
loop re-runs the attempt up to 3 times on `DocumentExistsException`/`StaleReadException`.

### 1.3 Creation receipt (`receipts/rcpt::{receiptId}`, existing shape)

| Field | Value |
| --- | --- |
| `receiptId` | `ReceiptIds.Compute(playerId, playerId, "identity.create_account", requestId)` |
| `actorId` / `actorType` | player id / `"system"` |
| `playerId` | the new player |
| `operation` | `identity.create_account` |
| `requestId` | UUID generated once per login call (same for all attempts and rounds) |
| `bodyHash` | SHA-256 hex of the empty byte string |
| `resultCode` / `resultCompact` | `OK` / `""` |
| `configVersion` | server config version of the instance |
| `schemaVersions` | the initializer touches (not `profile`) |
| `aggregates[]` | changed initializer touches with `newRev = 1` (or rev + 1 if a document existed), not `profile` |
| `claimKeys[]`, `outbox[]`, `outboxPending` | from the shared context |
| `correlationId` | login request correlation id |

Read through the player's receipt lookup (CMD-09: `actorId` = player id) and the GM receipt view. The relay (inline +
sweeper) projects its ledger entries. The purge anonymizes it like any other receipt.

### 1.4 New-player flag

`AccountWriteResult.Created` → `LoginResult.IsNewPlayer` → `LoginResponse.is_new_player` and analytics `createdPlayer`
(`isNewUser`). There is one value and one source. `false` for:
- a returning player;
- a race loser;
- the retry of a first login whose response was lost.

## 2. Read failures

### 2.1 `AggregateReadFailure` (Platform, immutable)

| Field | Meaning |
| --- | --- |
| `AggregateId`, `PlayerId` | which document |
| `Reason` | `VersionAhead` · `UpgradeFailed` · `Unreadable` · `MapperFailed` (log/metric names `version_ahead`, `upgrade_failed`, `unreadable`, `mapper_failed`) |
| `ExceptionType` | full name of the exception (empty for `VersionAhead`); never the message |
| `StoredSchemaVersion`, `CodeSchemaVersion` | from the envelope and the descriptor (0 when the envelope cannot be read) |
| `Code` | `AGGREGATE_VERSION_AHEAD` for `VersionAhead`, else `AGGREGATE_UNREADABLE` |

Classification (in `TryLoad`, order of the loader):

| Step | Failure |
| --- | --- |
| `schemaVersion` > descriptor | `VersionAhead` |
| missing upgrader for a step, or an upgrader throws | `UpgradeFailed` |
| envelope field of a wrong type, state JSON not deserializable into the state type, null state | `Unreadable` |
| (Platform.Web) wire mapper throws for the loaded state | `MapperFailed` |
| store timeout/unavailable | **not** a read failure (existing retryable codes) |

### 2.2 Where a failure goes

| Reader | Degrades? | Client sees |
| --- | --- | --- |
| snapshot, explicit section list | yes | 200; section absent; `unavailable_sections` entry |
| login (non-profile aggregate) | yes | 200; session; epoch + 1 once; entry in `unavailable_sections` |
| login / bearer authentication / provider login (profile) | no | error envelope with the typed code |
| command touching or reading it | no | error envelope with the typed code, details `aggregate`; nothing written |
| command replay | yes | 200 replayed result; aggregate absent from `updates`, listed |
| command response, mapper throws after the commit | yes | 200 committed result; aggregate absent from `updates`, listed |
| GM player view | yes | 200; `unreadable[]` entry; `deletion = null` when `profile` is broken |
| GM command / GM target validator | no | GM JSON error with the code |
| account purge (profile) | no | job step failed (existing reporting) |

### 2.3 Unavailable section (wire)

`UnavailableSection { id, code, code_number }` in `SnapshotResponse` (5), `LoginResponse` (9), `CommandResponse` (10).
Ordered by `id` (ordinal). A section is never both in `sections`/`updates` and in `unavailable_sections` of one response.

## 3. Command output limits

### 3.1 Settings (`GameServer:Commands`, validated at startup)

| Key | Default | Range | Constant? |
| --- | --- | --- | --- |
| `ReplayWindowHours` | 24 | 1..168 | no |
| `PerPlayerQueueLimit` | 2 | 0..16 | no |
| `ReceiptMaxBytes` | 4096 | 1024..65536 | no |
| `ResultCompactMaxBytes` | 512 | 64..16384; base64 length + 1024 ≤ `ReceiptMaxBytes` | no |
| `MaxAggregatesPerGame` | 16 | 1..16 | no |
| `CommandDescriptor.MaxTouches` | 4 | — | yes (DAT-09) |
| `CommandDescriptor.MaxClaimMarkersLimit` | 2 | — | yes (DAT-04) |

### 3.2 Rejection

`COMMAND_OUTPUT_TOO_LARGE` (23, HTTP 200, not retryable), details `part` ∈ {`result`, `receipt`}, `bytes`, `limit`.
The check is "greater than": exactly at the limit commits. No receipt, no write, no claim. A retry with the same request
id executes again (no receipt) and gets the same code.

### 3.3 Receipt size worked example (PCR platform, measured in the implementation)

The receipt of a sample `battle.end` with *n* ledger entries ≈ base + *n* × entry. The first consumer measured base ≈ 894 B
and entry ≈ 278 B, which gives about 11 entries at 4096 B and about 55 at 16384 B. The implementation task measures the
sample's own numbers with `ReceiptBuilder` and writes them into PCR platform §22.

## 4. SDK state

### 4.1 Request id

Lowercase UUID `D`, either the caller's (validated, normalized) or `GameServerClient.NewRequestId()`. Every attempt of one
call uses it. Exposed on `CommandResult.RequestId` and on `GameServerError.RequestId` of every exception the call throws.

### 4.2 Pending command (one file per entry, container v1)

| Field | Source |
| --- | --- |
| `RequestId` | normalized UUID (also the file name) |
| `PlayerId` | session player id at record time |
| `Route` | the route string passed to `SendCommandAsync` |
| `Body` | the exact bytes sent |
| `RecordedAtServerUtc` | `client.ServerNow` at record time (server clock, TIME-01) |

States of an entry:

```text
           Record (before 1st attempt)
  (none) ───────────────────────────────► Pending
  Pending ── CommandResponse (any code) ─► removed
  Pending ── envelope, not retryable ────► removed
  Pending ── no answer / retryable last / cancelled ──► Pending (kept)
  Pending ── Discard ─────────────────────► removed
  status(Pending) = OtherPlayer  if PlayerId ≠ session player
                  = Expired      if ServerNow − RecordedAtServerUtc ≥ PendingCommandMaxAge (default 23 h)
                  = Resendable   otherwise
```

### 4.3 Unavailable sections in `SectionStore`

`id → (code, codeNumber)`:
- set from a response's `unavailable_sections`;
- cleared by the next `Set`/`Apply` of that section and by `Clear()`.

The last good copy of a marked section stays in the store. A login for the player whose sections the store holds keeps
the copies of the sections that login lists as unavailable. Any other login clears the store as today.

## 5. Metrics (new)

| Name | Labels | Incremented when |
| --- | --- | --- |
| `aggregate_read_failures_total` | `aggregateId`, `reason` | each failed load or mapping (R6) |
| `command_output_too_large_total` | `operation`, `part` | each `COMMAND_OUTPUT_TOO_LARGE` rejection (also `identity.create_account`) |
| `account_creations_total` | `result` = `created` · `failed` | each creation attempt that ended (committed, or failed for an initializer/rule/store reason) |
| `account_initializer_failures_total` | `initializer`, `result` = `rejected` · `threw` · `rule` | each initializer-caused creation failure |

Existing metrics also count the creation: `receipt_size_bytes`, `aggregate_size_bytes`, `txn_*` (operation
`identity.create_account`).

## 6. State ownership

| State | Owner | Lifetime |
| --- | --- | --- |
| Initializer registrations, creation descriptor | `ModuleRegistry` / `AccountCreation` | host lifetime (built once) |
| Creation decision, context, staged writes | one transaction attempt of `AccountWriter` | attempt |
| New-player flag | `AccountWriteResult` → `LoginResult` | request |
| Read failures of a request | `PlayerSnapshot` / `CommandExecution.Unavailable` | request |
| Failure log and metric | `AggregateReadFailures` | process |
| Command limits | `GameServerOptions.Commands` (validated once) | host lifetime |
| Pending entries | `PersistentPendingCommandStore` (only writer: its client) | until a final answer or discard |
| Unavailable marks | `SectionStore` | client session (see §4.3) |
