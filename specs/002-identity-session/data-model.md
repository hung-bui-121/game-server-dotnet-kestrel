# Data Model: Identity & Session (002)

## `identities` — key `guest:{localId}` (ngoài transaction, không expiry)

| Field | Type | Rule |
| --- | --- | --- |
| `mappingKey` | string | = key |
| `playerId` | string | `p_` + 26 ký tự base32 |
| `gameId`, `environmentId` | string | deployment |
| `linkedAtUtc` | string | thời điểm tạo |
| `providerAttributes.secretHash` | string | hex HMAC-SHA256(pepper, secret) |

Insert-only khi tạo (race → đọc lại). `localId`: 1–64 ký tự `[A-Za-z0-9_-]`. Secret: ≥ 32 byte.

## `sessions` — key `hex(SHA-256(token))` (ngoài transaction, có expiry)

| Field | Type | Rule |
| --- | --- | --- |
| `playerId` | string | |
| `epoch` | long | = `profile.sessionEpoch` sau khi tăng |
| `createdAtUtc` | string | hạn tuyệt đối = +90 ngày |
| `lastTouchAtUtc` | string | hạn trượt = +30 ngày; touch khi cách > 24 h (CAS, best effort) |
| `gameId`, `environmentId` | string | |

Expiry document = `min(lastTouch + 30d, created + 90d)`.

## Aggregate `profile` (schema 1, client-visible)

| Field | Type | Rule |
| --- | --- | --- |
| `displayName` | string | |
| `sessionEpoch` | long | +1 mỗi login (IPlayerAggregateWriter) |
| `ban` | `{reason, untilUtc, scope: "login" | string[], actor}` \| null | BAN-01 |
| `deleted` | `{requestedAtUtc, purgeAfterUtc}` \| null | PRIV-01 (P0.5 ghi, P0 chỉ kiểm) |
| `identities` | string[] | mapping keys đã link |

Wire `ProfileState` chỉ gửi `display_name`, `session_epoch` (ban/deleted không lộ qua snapshot).

## Thứ tự kiểm tra request Player

```text
Bearer token → sessions/{hash} (cache ≤ 60 s) — thiếu/hết hạn → UNAUTHENTICATED
  → profile (cache ≤ 60 s): deleted → ACCOUNT_DELETED
                            ban login còn hạn → ACCOUNT_SUSPENDED(until)
                            session.epoch < profile.sessionEpoch → SESSION_REPLACED
  → OK (actor = player) → lazy touch nếu cách > 24 h
```
