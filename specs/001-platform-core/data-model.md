# Data Model: Platform Core (001)

Mọi document nằm trong scope `g_<gameId>` của bucket theo environment (DAT-01). Thời gian là UTC
ISO-8601. Kích thước tối đa theo Phụ lục B của tài liệu yêu cầu.

## Player aggregate document — collection `pa_<aggregateId>`, key `<playerId>`

| Field | Type | Rule |
| --- | --- | --- |
| `gameId` | string | = deployment config; defense-in-depth (DAT-01) |
| `environmentId` | string | = deployment config |
| `playerId` | string | = key |
| `aggregateId` | string | = descriptor id, `[a-z][a-z0-9_]{1,31}` |
| `schemaVersion` | int | ≥ 1; nâng lười bằng upgrader chain (DAT-02) |
| `rev` | long | 0 khi chưa tồn tại; +1 mỗi commit có thay đổi (API-10) |
| `createdAtUtc` / `updatedAtUtc` | string | từ `IClock` |
| `state` | object | JSON của kiểu state do module định nghĩa |

- Không tồn tại ⇔ `factory.CreateDefault()` với `rev = 0` (DAT-09); Insert ở lần ghi đầu, Replace sau đó.
- Kích thước serialized: ≥ 32 KiB → metric `aggregate_size_bytes` + cảnh báo; > 64 KiB →
  `AGGREGATE_TOO_LARGE`, rollback.
- Chỉ ghi qua `ICommandRunner`/`IPlayerAggregateWriter`.

## Receipt — collection `receipts`, key `rcpt::<receiptId>`

| Field | Type | Rule |
| --- | --- | --- |
| `receiptId` | string | base64url(SHA-256(`actorId` \| operation \| `requestId`))[0..32] (CMD-01) |
| `gameId`, `environmentId` | string | |
| `actorId` | string | player hoặc GM staff id |
| `actorType` | string | `player` / `gm` / `system` |
| `playerId` | string | player đích (index; CMD-09) |
| `operation` | string | `<module>.<command>` |
| `requestId` | string | UUID từ header |
| `bodyHash` | string | hex SHA-256 raw body (CMD-02) |
| `resultCode` | string | `OK` cho thành công |
| `resultCompact` | string | base64 bytes kết quả module, ≤ 512 B |
| `configVersion` | string | CFG-02 |
| `schemaVersions` | map<string,int> | aggregate đã chạm → schemaVersion |
| `aggregates` | `[{aggregateId, newRev}]` | chỉ aggregate đã đổi |
| `claimKeys` | string[] | marker đã Insert |
| `committedAtUtc` | string | replay window 24 h tính theo field này (CMD-07) |
| `outbox` | `[OutboxEntry]` | JOB-02 |
| `outboxPending` | bool | true nếu `outbox` không rỗng; partial index |
| `correlationId` | string | = requestId |

- ≤ 4 KiB serialized (vượt → lỗi lập trình, rollback). Không expiry (DAT-03).
- Business rejection không tạo receipt (research R5).

## OutboxEntry (nhúng trong receipt)

| Field | Type | Rule |
| --- | --- | --- |
| `entryId` | string | `<receiptId>:<index>` — xác định, idempotent |
| `kind` | string | consumer đăng ký theo kind (ví dụ `counter_log`, sau này `ledger`, `audit`) |
| `payload` | object | JSON do module định nghĩa |

Đích: consumer quyết định collection; mặc định `ProjectionConsumer` Insert-if-absent document key
`<kind>::<entryId>` với `{entryId, receiptId, playerId, payload, committedAtUtc}`.

## Claim marker — collection do module khai, key do module dựng

`{ claimKey, receiptId, playerId, createdAtUtc }`; insert-only, không expiry. Tối đa 2/command.

## Config snapshot (in-memory, không lưu DB)

| Field | Rule |
| --- | --- |
| `label` | từ `config/base/manifest.json` (env overlay có thể đổi) |
| `files` | tên file → JSON hiệu lực sau overlay (file env thay thế nguyên file base cùng tên) |
| `clientVisible` | danh sách tên file |
| `configVersion` | `"{label}+{hex(sha256(concat(sorted(name + '\n' + bytes))))[0..12]}"` |

## Descriptor (đăng ký tại startup, không lưu DB)

- **ModuleDescriptor**: `Name`, `Version`, `DependsOn[]`, `RequiredConfigFiles[]`, `Permissions[]`,
  `Collections[]` (+ index), `Aggregates[]`, `Commands[]`, `ErrorCodes` (dải + danh sách),
  `OutboxConsumers[]`.
- **PlayerAggregateDescriptor**: `AggregateId`, `StateType`, `SchemaVersion`, `ClientVisible`,
  `WarnBytes = 32768`, `HardBytes = 65536`, `Factory`, `Upgraders` (from→to).
- **CommandDescriptor**: `Operation`, `Touches[]` (1..4), `Reads[]`, `MaxClaimMarkers` (0..2),
  `RequestType`, `ResultType`, `Validator`, `Handler`, `Profile` (`Player`/`Gm`), `Permission` (GM).
- **ErrorCode**: `Number`, `Name` (`UPPER_SNAKE`), `Retryable`, `HttpStatus`, `SafeMessage`.

## Actor context

`ActorId`, `ActorType`, `PlayerId` (đích), `GameId`, `EnvironmentId`, `Permissions`, `CorrelationId`,
`Profile`. Dựng từ authentication + deployment config; không từ payload (API-12, ARC-10).

## State transitions của một command

```text
received → (gate) → authenticated → authorized → decoded/validated
  → replay lookup ─ found & same hash ─→ replay response
                 ─ found & diff hash ─→ 409 IDEMPOTENCY_PAYLOAD_MISMATCH
                 ─ none ─→ transaction attempt(s)
transaction attempt: GetMany(touches ∪ reads ∪ receipt ∪ known markers)
  → receipt exists (race) → return committed result
  → handler → business error → rollback → 200 code
           → ok → write changed aggregates (size check) + Insert markers + Insert receipt → commit
commit → ok → relay outbox (best-effort) → 200 OK + SectionUpdates
       → ambiguous → 503 COMMIT_UNKNOWN_RETRY
       → expired (conflict) → 409 CONCURRENT_UPDATE
       → failed → 500 INTERNAL
```
