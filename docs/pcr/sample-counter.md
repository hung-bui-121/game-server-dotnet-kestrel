# PCR — sample.counter

Module mẫu của framework (ACC-13, EXT-05). Version 0.1.0.

Đúng với framework v0.7.0 (chưa phát hành).

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

## Xoá tài khoản (slice 010 — FR-019)

`counter_log` = `DeletionTreatment.Anonymize` (player field `playerId`; index `ix_counter_log_player_ref` do platform tạo):
purge ghi đè player id bằng anonymous id, giữ bản ghi. `pa_counter` bị xoá theo key cùng mọi `pa_*`. Module mẫu không cần
`DeletionSteps`; module có dữ liệu không mô tả được bằng treatment khai `ModuleStep` + `IAccountDeletionStep` (PCR platform
mục 16, ví dụ test `tests/Sample.Counter.Tests/DeletionExtensionTests.cs`).

## Route catalog và section (slice 014 — FG-026, FG-027, FG-028)

`CounterRoutes` (`modules/Sample.Counter/Sample.Counter/CounterRoutes.cs`), liệt kê ở `CounterModuleDefinition.Routes`;
`CounterModule.cs` map từ declaration, không literal route. Bảng route lấy từ export của SampleHost (`--environment
Local --export-wire-contract`, format `gameserver.wire-contract/1`). Endpoint check lúc khởi động, quy tắc đặt tên và
điều kiện hosting: PCR platform mục 20. Module mẫu có origin `game` (assembly không mang tiền tố `GameServer.`): hằng số
Unity sinh vào `SampleGameRoutes` / `SampleGameSections` của KestrelDemo (`samples/SampleGame/wiregen.json`), ví dụ
`SampleGameRoutes.CounterIncrement`.

| Route | Method | Template | Profile | Operation | Permission | Condition |
| --- | --- | --- | --- | --- | --- | --- |
| `counter.get` | POST | `/api/v1/counter/get` | Player | — | — | — |
| `counter.gm_read` | GET | `/gm/v1/counter/{playerId}` | Gm | — | `counter.read` | — |
| `counter.increment` | POST | `/api/v1/counter/increment` | Player | `counter.increment` | — | — |

Section (mapper typed `AddAggregateWireMapper<CounterState, Wire.CounterState>`; Unity `SampleGameSections.Counter` /
`CounterId`):

| Section | Message | Hidden (không lên wire) | May-stay-default |
| --- | --- | --- | --- |
| `counter` | `sample.counter.v1.CounterState` | — | — |

`WireConformance` (TestKit) kiểm mapper mang mọi member không hidden lên wire và set mọi field proto
(`tests/GameServer.Platform.Web.Tests/WireConformanceTests.cs`).
