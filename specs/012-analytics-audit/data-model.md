# Data model — 012 analytics-audit

Record schemas v1, configuration, the outbox entry and the on-disk layout. No new Couchbase collection, index or
deletion treatment (the payment entry lives inside existing receipts). Decisions and rationale: [research.md](research.md).
Data-team contract: [contracts/analytics-records.md](contracts/analytics-records.md); operations:
[contracts/analytics-operations.md](contracts/analytics-operations.md).

## 1. Configuration — section `GameServer:Analytics` (`AnalyticsOptions`, R8)

| Key | Type | Default | Valid | Used by |
| --- | --- | --- | --- | --- |
| `Enabled` | bool | `true` | — | pipeline, consumer, login analytics |
| `TimeZone` | string (IANA) | none | resolvable, `HasIanaId`; required in Staging/Production when active; Development/Test default `UTC` + warning | every record |
| `Directory` | path | `analytics` | non-empty; relative → content root; created at start; must be writable | file sink |
| `QueueCapacity` | int | `10000` | 100..1 000 000 | pipeline |
| `DeliveryTimeoutMs` | int | `500` | 50..5 000 | outbox consumer (inline bound, FR-013) |
| `ResumeTrackerCapacity` | int | `200000` | 1 000..10 000 000 | identity resume rule |
| `LowDiskBytes` | long | `1073741824` (1 GiB) | ≥ 0 | health |
| `ErrorWindowSeconds` | int | `300` | 10..86 400 | health |
| `RetainLocalDays` | int? | none (never delete) | ≥ 1 | file sink |

"Active" = `Enabled` and at least one `IAnalyticsSink` registered. Instance id is **not** an analytics key: it is
`Observability:InstanceId` → `HOSTNAME` → machine name (same as the log files, LOG-03). No key is a secret (FR-034).
Validated by `AnalyticsOptionsValidator` (`ValidateOnStart`, merged into the serve startup report; not run by
`--provision`).

### Sample host flavor values (FR-032)

| Flavor | `Enabled` | `TimeZone` | `Directory` (effective) |
| --- | --- | --- | --- |
| Local | true | `Asia/Ho_Chi_Minh` | `samples/SampleHost/analytics` (git-ignored, anchored) |
| Dev | true | `Asia/Ho_Chi_Minh` | `analytics` under the content root |
| Beta | true | `Asia/Ho_Chi_Minh` | `analytics` → persistent volume or shipper |
| Uat | true | `Asia/Ho_Chi_Minh` | same |
| Live | true | `Asia/Ho_Chi_Minh` | same |

Flavor file fragment (each of `appsettings.{Local,Dev,Beta,Uat,Live}.json`):

```json
"GameServer": {
  "Analytics": { "Enabled": true, "TimeZone": "Asia/Ho_Chi_Minh" }
}
```

`TestHost.DefaultSettings()` adds `GameServer:Analytics:TimeZone = UTC`.

## 2. Validation rules (startup, before traffic)

| Rule | Where | Message names |
| --- | --- | --- |
| `TimeZone` resolvable and IANA (when active) | `AnalyticsOptionsValidator` | key, value, fix (IANA name, tzdata/ICU in the image) |
| `TimeZone` present in Staging/Production (when active) | `AnalyticsOptionsValidator` | key, deployment environment |
| numeric ranges, non-empty `Directory` | `AnalyticsOptionsValidator` | key, allowed range |
| directory creatable and writable | `AnalyticsFileSink.StartingAsync` → `GameServerStartupException` → exit 1 | `GameServer:Analytics:Directory`, absolute path |
| single writer per (game, instance, directory) | `AnalyticsFileSink.StartingAsync` (lock) → exit 1 | `Observability:InstanceId`, resolved id, directory |
| enabled but no sink, Staging/Production | `AnalyticsOptionsValidator` → exit 1 (review T066) | `GameServer:Analytics:Enabled`, deployment environment, fix (`AddAnalyticsFiles()` or `Enabled=false`) |
| enabled but no sink, Development/Test | pipeline start — **warning** only | "analytics enabled but no sink registered (AddAnalyticsFiles)" |
| zone defaulted to UTC | pipeline start — **warning** (Development/Test) | key |

## 3. Record envelope v1 (every event type)

One JSON object per line, UTF-8 (no BOM), compact, keys in this order. Types are JSON types.

| # | Field | JSON type | Format / values | Source |
| --- | --- | --- | --- | --- |
| 1 | `event` | string | `^[a-z][a-z0-9_]{0,31}$`: `payment`, `login` | event type |
| 2 | `schemaVersion` | number | integer ≥ 1 (`1` for both types) | event type |
| 3 | `eventId` | string | 32 lowercase hex | payment: `hex(SHA-256("{entryId}\|{committedAtUtc}"))[0..32]`; login: random GUID `N` |
| 4 | `gameId` | string | deployment `GameServer:GameId` | `GameServerOptions` (FR-033) |
| 5 | `environmentId` | string | deployment `GameServer:EnvironmentId` (`local`, `dev`, `beta`, `uat`, `live`, …) | `GameServerOptions` |
| 6 | `userID` | string | raw internal player id (`p_…`), or the anonymous id of a purged account (late delivery) | receipt `playerId` / authenticated player |
| 7 | `{timeField}` | string | local time, `yyyy-MM-ddTHH:mm:ss.fff±hh:mm` | instant in the deployment zone |
| 8 | `{timeField}Utc` | string | `yyyy-MM-ddTHH:mm:ss.fffZ` | same instant, ms-truncated |
| 9 | `timeZone` | string | IANA name, e.g. `Asia/Ho_Chi_Minh` (`UTC` when defaulted) | configuration |
| 10 | `utcOffset` | string | `±hh:mm` valid at that instant (`+00:00` for UTC, never `Z`) | zone rules |
| 11 | `receiptId` | string | 32 chars base64url; **outbox events only** | receipt |
| 12.. | type-specific fields | — | see §3.1/§3.2; absent when unknown | event payload |

Fields 7 and 8 are named per type: `chargeTime`/`chargeTimeUtc` (payment), `activeTime`/`activeTimeUtc` (login).

### 3.1 `payment` v1 (owner fields: `userID`, `transactionID`, `chargeTime`)

| Field | JSON type | Required | Format / values |
| --- | --- | --- | --- |
| `chargeTime` / `chargeTimeUtc` | string | yes | server grant time = receipt `committedAtUtc` (owner Q9) |
| `receiptId` | string | yes | receipt of the purchase command |
| `transactionID` | string | yes | `apple:{transactionId}` · `google:{orderId}` · `google:h:{32 hex}` (SHA-256 of the purchase token when no order id) |
| `store` | string | yes | `google`, `apple` |
| `productId` | string | yes | store product id (≤ 128 chars) |
| `orderId` | string | no | store order id (Google `GPA.…`; Apple = transaction id) |
| `isTest` | bool | yes | store test/sandbox purchase or fake verifier; always `false` in Production deployments |
| `purchaseCategory` | string | no | `test`, `promo`, `rewarded` (Google `purchaseType`) |
| `storePurchaseTimeUtc` | string | no | store-reported purchase time, `yyyy-MM-ddTHH:mm:ss.fffZ` |

Example:

```json
{"event":"payment","schemaVersion":1,"eventId":"5b1f0c9e2a7d4c8e9f0a1b2c3d4e5f60","gameId":"sample","environmentId":"live","userID":"p_01JB7Q2M8XKZ4N6P0R3S5T7V9W","chargeTime":"2026-10-30T21:05:09.123+07:00","chargeTimeUtc":"2026-10-30T14:05:09.123Z","timeZone":"Asia/Ho_Chi_Minh","utcOffset":"+07:00","receiptId":"q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e","transactionID":"google:GPA.3391-5521-0937-41862","store":"google","productId":"gems_100","orderId":"GPA.3391-5521-0937-41862","isTest":false,"storePurchaseTimeUtc":"2026-10-30T14:05:01.000Z"}
```

### 3.2 `login` v1 (owner fields: `userID`, `activeTime`)

| Field | JSON type | Required | Format / values |
| --- | --- | --- | --- |
| `activeTime` / `activeTimeUtc` | string | yes | server time of the login success or of the authenticated request (resume) |
| `source` | string | yes | `login` (new session issued) or `resume` (first authenticated request of the local day on this instance) |
| `provider` | string | `login` only | identity used: `guest`, `pgs`, `gamecenter`, or a game's provider name |
| `isNewUser` | bool | yes | `true` when this login created the player; always `false` for `resume` |

Example (`resume`):

```json
{"event":"login","schemaVersion":1,"eventId":"0f9e8d7c6b5a49382716a5b4c3d2e1f0","gameId":"sample","environmentId":"live","userID":"p_01JB7Q2M8XKZ4N6P0R3S5T7V9W","activeTime":"2026-10-31T08:00:01.002+07:00","activeTimeUtc":"2026-10-31T01:00:01.002Z","timeZone":"Asia/Ho_Chi_Minh","utcOffset":"+07:00","source":"resume","isNewUser":false}
```

Typical sizes: login ≈ 330–380 B, payment ≈ 520–650 B per line (sizing: 1 million logins ≈ 0.4 GB/day uncompressed).

## 4. Domain values in code (not stored)

| Type | Assembly | Fields |
| --- | --- | --- |
| `AnalyticsEventType` | Core | `Name`, `SchemaVersion`, `TimeField`; payment = `("payment", 1, "chargeTime")` (IAP), login = `("login", 1, "activeTime")` (Identity) |
| `VerifiedPurchase` (+ init props) | IAP business | `IsTest`, `PurchasedAt`, `PurchaseCategory` (R12) |
| `PaymentFields` | IAP business | `transactionID`, `store`, `productId`, `orderId?`, `isTest`, `purchaseCategory?`, `storePurchaseTimeUtc?` — explicit `[JsonPropertyName]`, nulls not written |
| `LoginFields` | Identity adapters | `source`, `provider?`, `isNewUser` |
| `AnalyticsEnvelope` | Platform | type, event id, user id, instant, receipt id?, fields |
| `AnalyticsRecord` | Platform | event, event id, UTF-8 JSON line (no newline) |

## 5. Payment outbox entry (inside the purchase receipt, FR-012)

```json
{"entryId":"q8Zr0bKk3Gm1v7yQx2TfLw5sNc9HdA4e:2","kind":"analytics","payload":{"event":"payment","schemaVersion":1,"timeField":"chargeTime","fields":{"transactionID":"google:GPA.3391-5521-0937-41862","store":"google","productId":"gems_100","orderId":"GPA.3391-5521-0937-41862","isTest":false,"storePurchaseTimeUtc":"2026-10-30T14:05:01.000Z"}}}
```

- Index = number of grants (entries `0..n-1` are ledger entries), deterministic across transaction attempts.
- **No player id** in the payload; `userID`, `chargeTime`, `receiptId` and `eventId` are derived at delivery from the
  receipt (`playerId`, `committedAtUtc`, `receiptId`, `entryId`), so the 010 purge anonymization of the receipt covers
  the late record.
- Size: ≈ 330 B typical; **≤ 512 B** worst case (`productId` 128 chars, `purchaseCategory` and store time present).
  Receipt budget: a Google purchase with 3 grants, a 300-character token and a 128-character product id stays below
  3.3 KiB of the 4 KiB limit (test). Purchases with many grants and long tokens were already near the limit before this
  slice (each ledger entry repeats the token); the analytics entry adds at most 0.5 KiB (risk table in plan).

## 6. Files on disk (default sink)

```text
<GameServer:Analytics:Directory>/
├── .{gameId}-{instanceId}.lock                      # exclusive while the process runs (FileShare.None)
├── {gameId}-{instanceId}-payment-{yyyyMMdd}.jsonl   # UTC date of the server clock at write time
├── {gameId}-{instanceId}-login-{yyyyMMdd}.jsonl
└── … other instances' files (own lock, own names)
```

- `gameId`, `instanceId` sanitized like the log file name (letters, digits, `-`, `_`, `.`; others → `_`).
- Append-only; one record per line terminated by `\n`; a file of a past UTC date is never appended to again (closed
  day). A line cut by a crash is followed by a `\n` on the next open, so it stays one unparseable line.
- Written and flushed to the OS per batch (payment acknowledged only after that flush); no fsync.
- Deleted only by the optional `RetainLocalDays` pass, and only this instance's own files.

## 7. In-memory state (per instance)

| State | Owner | Bound | Lost on restart |
| --- | --- | --- | --- |
| Event queue | `AnalyticsPipeline` | `QueueCapacity` (10 000) records | queued login/resume records (counted as dropped on graceful stop; uncounted on crash); queued payments are redelivered by the sweeper |
| Players seen on the current local day | `LoginAnalytics` (`DailyPlayerSet`) | `ResumeTrackerCapacity` (200 000), cleared when full | yes → at most one extra resume per player |
| Open file handles (one per event type), current UTC day | `AnalyticsFileSink` | event types × 1 | n/a |
| Sink error counters, last error | `AnalyticsPipeline.Status` | — | yes |

## 8. Ownership (single source of truth)

| Fact | Source of truth |
| --- | --- |
| A purchase was granted | the committed receipt (+ claim marker); the analytics record is a projection of its outbox entry |
| Payment record content | outbox payload (type fields) + receipt (`playerId`, `committedAtUtc`, `receiptId`, `entryId`) + configuration (game, environment, zone) |
| Login record content | `LoginCompletion` / bearer handler at emission + configuration |
| Deployment zone | `GameServer:Analytics:TimeZone` (resolved once per process) |
| Instance id | `ObservabilityOptions.ResolveInstanceId()` |
| File day | server clock (`IClock`) in UTC, monotonic per process |
