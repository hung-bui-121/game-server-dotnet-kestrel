# PCR — sample.counter

Module mẫu của framework (ACC-13, EXT-05). Version 0.1.0.

## Aggregate

| Aggregate | Collection | Schema | Client-visible | State |
| --- | --- | --- | --- | --- |
| `counter` | `pa_counter` | 2 (v1→v2 thêm `increments = 0`) | có | `{value: int64, increments: int32}` |

## Command → Touches / Reads

| Operation | Route | Touches | Reads | Claim markers | Outbox |
| --- | --- | --- | --- | --- | --- |
| `counter.increment` | `POST /api/v1/counter/increment` | `counter` | — | 0 | `counter_log` → collection `counter_log` |

Query: `POST /api/v1/counter/get` (snapshot `counter`). GM: `GET /gm/v1/counter/{playerId}` (permission `counter.read`).

## Error codes (10000–10099)

| Number | Name | HTTP | Retryable |
| --- | --- | --- | --- |
| 10001 | COUNTER_LIMIT_EXCEEDED | 200 | no |
| 10002 | COUNTER_INVALID_AMOUNT | 200 | no |

`note` > 64 ký tự → 400 `BAD_REQUEST` (validate ở adapter, ARC-02).

## Extensible capabilities

| Capability | Mặc định | Thay bằng |
| --- | --- | --- |
| `ICounterLimitPolicy` | tối đa 10 / lần | `ReplacePolicy<ICounterLimitPolicy, T>()` |
| `ICounterService` (dùng như service, ARC-04) | đọc snapshot | `ReplacePolicy<ICounterService, T>()` |
