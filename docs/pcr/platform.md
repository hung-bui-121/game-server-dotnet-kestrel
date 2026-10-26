# PCR — platform

Published Contract Register của nền tảng (PCR-01). Thay đổi mục **semantic** là breaking change (ARC-08);
thay đổi mục **vận hành** chỉ cần ghi changelog. Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4.

Phiên bản: 0.4.0 (slice 001 + sửa sau review + slice 002 identity + slice 004 GM, 2026-10-26).

## 1. Limits và timeout theo nhóm endpoint (vận hành — API-06, PERF-04)

| Tham số | Player | GM | Cấu hình |
| --- | --- | --- | --- |
| Body tối đa | 64 KiB | 1 MiB | `GameServer:Limits:PlayerBodyBytes` / `GmBodyBytes` |
| Timeout request | 10 s | 30 s | `PlayerTimeoutSeconds` / `GmTimeoutSeconds` → 503 `REQUEST_TIMEOUT` |
| Concurrency limiter (per instance) | `command` 200 | `gm` 50 | queue = 2× permit, oldest-first → 429 `RATE_LIMITED` + `Retry-After` |
| Per-player ordering (CMD-06) | concurrency 1, queue 2 | — | `GameServer:Commands:PerPlayerQueueLimit` |
| Protobuf decode | recursion ≤ 32, size = body | — | — |
| Kestrel `MaxRequestBodySize` | 1 MiB toàn cục, hạ per-endpoint theo profile | | |

Budget tổng = giá trị × số instance.

## 2. Transaction boundary và durability (semantic — DAT-04, DAT-05, DAT-09)

- Mỗi command thay đổi trạng thái = **một** transaction: aggregate trong `Touches` (≤ 4) + receipt
  `rcpt::{receiptId}` (Insert) + claim marker (≤ 2, Insert). Durability `Majority`. `ExpirationTime` 10 s,
  `KvDurableTimeout` 5 s. Deadline commit tách khỏi request token (PERF-02).
- Đọc bundle (DAT-11): **mặc định đọc tuần tự** `GetOptionalAsync` (N + 1 RTT) cho tới khi TST-04 (V23-T1) và
  bộ conformance chạy trên cluster thật. **Phát hiện khi implement (SDK 3.10.0):** `GetMulti` không trả
  `TransactionGetResult`, nên ở chế độ `Couchbase:UseGetMulti=true` mỗi aggregate **thực sự bị ghi** cần thêm một
  `ctx.GetAsync` trước `ReplaceAsync`; nội dung được so với bản đọc bundle, khác → command chạy lại
  (`StaleReadException`). Chế độ GetMulti dùng `PrioritizeReadSkewDetection`. Chỉ tiêu "≈ 1 RTT" của DAT-11 **chưa
  đạt** cho tới khi bật GetMulti — cần owner quyết định sau khi có số đo.
- Conflict: Couchbase chỉ phát hiện **write-write**; document chỉ đọc (`Reads`) có thể đổi trong lúc transaction
  chạy (read skew). Store in-memory mô phỏng đúng như vậy.
- Mọi ghi `pa_*` và `rcpt::*` chỉ qua `ICommandRunner` / `IPlayerAggregateWriter` (arch test). Ngoại lệ: các cờ
  outbox của receipt **đã commit** (`outboxPending`, `outboxAttempts`, `outboxNextAttemptAtUtc`,
  `outboxDeadLetter`) được cập nhật bằng `MutateIn` ngoài transaction, có CAS lấy từ một get mới; receipt chưa nhìn
  thấy (đang unstaging) được bỏ qua cho sweeper. Rủi ro còn lại: receipt hết replay window bị transaction mới thay
  thế cùng lúc với `MutateIn` (hiếm; xem V15).
- Business rejection (HTTP 200 + code) **không tạo receipt**: transaction rollback, kể cả claim marker. Retry
  cùng `requestId` sau rejection được xử lý lại như mới.
- Insert race (hai instance cùng ghi receipt/claim) hoặc stale read → runner chạy lại tối đa 3 vòng; hết vòng →
  409 `CONCURRENT_UPDATE` (retryable).

## 3. Idempotency và replay (semantic — CMD-01..09)

| Mục | Giá trị |
| --- | --- |
| `requestId` | UUID ở header `X-Request-Id` (cũng là correlation id); thiếu/sai → 400 |
| `receiptId` | `base64url(SHA-256(actorId|playerId|operation|requestId))[0..32]` (player đích nằm trong khoá) |
| `bodyHash` | hex SHA-256 trên raw body như nhận được |
| Replay window | 24 h theo `committedAtUtc` (`GameServer:Commands:ReplayWindowHours`) |
| Replay cùng body | cùng `code`, `receipt_id`, `result` (byte-identical), `replayed = true`; `updates` chứa aggregate **hiện hành** (`full = true`, `new_rev` = rev hiện tại ≥ rev đã commit, `base_rev` = rev đã commit − 1) |
| Replay khác body | 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` |
| Ngoài window | như request mới; receipt cũ được thay bằng receipt mới |
| Commit không rõ | 503 `COMMIT_UNKNOWN_RETRY` + `retry_after_ms`; retry cùng bytes |
| Receipt / resultCompact | ≤ 4 KiB / ≤ 512 B; vượt = lỗi lập trình (500) |
| Lookup | `GET /api/v1/player/receipts/{receiptId}` — chỉ actor sở hữu; actor khác nhận 404 |
| Guard (`ICommandGuard`, ví dụ ban theo nhóm command) | chỉ chặn lần thực thi mới; retry của command đã commit vẫn replay |
| Commit kèm mã nghiệp vụ | `CommandOutcome.Commit(result, code)`: state + receipt được ghi, `resultCode = code`, HTTP 200; replay trả lại đúng mã |
| Id xác định | `ctx.NewId(purpose)` / `ctx.NewSeed(purpose)` = SHA-256(receiptId|purpose) — ổn định qua attempt và retry |

## 4. Revision và section (semantic — API-10)

Section ≡ player aggregate `ClientVisible`. `rev` tăng 1 cho mỗi commit làm aggregate đổi; aggregate không
đổi giữ `rev` và không có trong `updates`. Document chưa tồn tại = state mặc định, `rev = 0`. Snapshot đọc
ngoài transaction, song song, nhất quán per aggregate (không chéo aggregate).

## 5. Ngân sách aggregate (semantic — DAT-09)

≤ 16 aggregate/game; `Touches` 1..4; marker ≤ 2. Kích thước document: cảnh báo ≥ 32 KiB
(`aggregate_size_warnings_total`), cứng > 64 KiB → `AGGREGATE_TOO_LARGE` (HTTP 200), rollback toàn command.

## 6. Schema version (semantic — DAT-02)

Upgrader chain per aggregate (đủ một upgrader cho mỗi bước); nâng lười khi đọc; ghi lại ở commit kế tiếp làm
aggregate đổi; `aggregate_upgrades_total`. Document có `schemaVersion` mới hơn code → 500 `INTERNAL`.

## 7. Outbox (semantic — JOB-02, OPS-05)

Entry `{entryId = <receiptId>:<index>, kind, payload}` trong receipt. Relay inline sau commit (best effort) +
sweeper mỗi 30 s ±20 % cho receipt pending cũ hơn 10 s (partial index `ix_receipts_outbox_pending`). Consumer
idempotent theo `entryId`; projection mặc định Insert-if-absent `<kind>::<entryId>`. Relay thất bại → backoff
`30 s × 2^(n-1)` (tối đa 1 h) theo `outboxNextAttemptAtUtc`; sau 10 lần → `outboxDeadLetter = true`, ra khỏi hàng
đợi, metric `outbox_dead_letter_total` (cần vận hành xử lý). Kind không có consumer tính là thất bại.

## 8. Error-code catalog — core (semantic — API-05, API-11)

| Number | Name | HTTP | Retryable |
| --- | --- | --- | --- |
| 0 | OK | 200 | no |
| 1 | BAD_REQUEST | 400 | no |
| 2 | UNAUTHENTICATED | 401 | no |
| 3 | FORBIDDEN | 403 | no |
| 4 | NOT_FOUND | 404 | no |
| 5 | PAYLOAD_TOO_LARGE | 413 | no |
| 6 | UNSUPPORTED_MEDIA_TYPE | 415 | no |
| 7 | RATE_LIMITED | 429 | yes |
| 8 | REQUEST_TIMEOUT | 503 | yes |
| 9 | MAINTENANCE | 503 | yes |
| 10 | UPDATE_REQUIRED | 426 | no |
| 11 | IDEMPOTENCY_PAYLOAD_MISMATCH | 409 | no |
| 12 | CONCURRENT_UPDATE | 409 | yes |
| 13 | COMMIT_UNKNOWN_RETRY | 503 | yes |
| 14 | AGGREGATE_TOO_LARGE | 200 | no |
| 15 | INTERNAL | 500 | no |
| 16 | ACCOUNT_SUSPENDED | 403 | no |
| 17 | ACCOUNT_DELETED | 401 | no |
| 18 | SESSION_REPLACED | 401 | no |
| 19 | IDENTITY_CONFLICT | 409 | no |
| 20 | SWITCH_ACCOUNT | 409 | no |

Dải: core 0–999; module khai `ErrorCodeSet(rangeStart, rangeEnd)`; trùng tên/số/chồng dải → startup fail.
Player nhận `ErrorEnvelope` (Protobuf, kể cả lỗi middleware); GM nhận Problem Details JSON với `code`,
`codeNumber`, `requestId`, `retryable`, `retryAfterMs`. Cả hai có `details` (map chuỗi an toàn, ví dụ `until` của
`ACCOUNT_SUSPENDED`; field 9 của `ErrorEnvelope`, thêm ở 0.3.0, không breaking).

Xác thực theo profile (SEC-02): endpoint Player chỉ thử scheme Player (`PlayerBearer`, + scheme thử nghiệm ở
Development/Test), endpoint GM chỉ thử scheme GM. Scheme thử nghiệm header (`X-Test-*`) và store in-memory bị
startup chặn ngoài Development/Test.

## 9. Public endpoint allowlist (semantic — SEC-09)

| Route | Mục đích |
| --- | --- |
| `GET /v1/time` | server time (TIME-01) |
| `POST /api/v1/identity/login/guest` | guest login (khai báo bởi module identity qua `ModuleDescriptor.PublicEndpoints`) |
| `POST /gm/v1/auth/login` | GM local login, chỉ map ở Development/Test (module gm) |
| `GET /gm/v1/ui` | trang GM tĩnh (module gm) |

`GetConfig` (`POST /api/v1/config/get`) là endpoint **Player** (SEC-09, CFG-03): trả các file `clientVisible` (bytes sau
overlay), `not_modified` khi client gửi đúng version; bundle > `GameServer:Limits:ConfigBundleBytes` (1 MiB) → 413.

Health endpoints (`/health/live`, `/health/ready`) thêm ở slice 005.

## 10. Extensible capabilities (EXT-01)

| Capability | Điểm thay thế |
| --- | --- |
| Command handler | `ConfigureExtensions(x => x.ReplaceHandler<TReq, TRes, THandler>(operation))` |
| Policy/service của module | `ConfigureExtensions(x => x.ReplacePolicy<TService, TImpl>())` |
| Gate (maintenance/version) | đăng ký `IGateEvaluator` |
| Outbox consumer | đăng ký `IOutboxConsumer` cho một `kind` |
| Document store | `ITransactionalDocumentStore` + `IKeyValueStore` (Couchbase; in-memory chỉ Development/Test — startup chặn ở môi trường khác) |
| Command guard | `ICommandGuard` (chạy trước mỗi lần thực thi mới) |
| Truy vấn phân trang | `IPagedQueryStore` (in-memory; Couchbase N1QL `RequestPlus`) |

## 12. GM API dùng chung (semantic — slice 004)

- `IPagedQueryStore.QueryPageAsync(PagedQuery)`: lọc bằng nhau một trường, sắp một trường (+ key), keyset cursor opaque;
  `pageSize` 1–200 (ngoài khoảng → 400); tên collection và đường dẫn trường chỉ gồm định danh (chặn injection). Trường
  lọc và trường sắp phải là **chuỗi JSON**; document thiếu hoặc khác kiểu không khớp (in-memory và Couchbase như nhau).
  Chỉ dùng ngoài hot path (PERF-07); collection cần index `(trường lọc, trường sắp [DESC], META().id [DESC])` theo
  hướng truy vấn để `ORDER BY … LIMIT` đọc từ index. **Chưa chạy trên Couchbase thật** (cần EXPLAIN khi có cluster).
- `GmHttp`: `ReadPageRequest` (`pageSize` bắt buộc), `QueryAsync`, `ReadJsonAsync` (body JSON ≤ GM limit), `RequirePlayerId`.
- `MapGmCommand<TBody, TRequest, TResult>(pattern, operation, permission, toDomain)`: route phải có `{playerId}`; `TBody :
  IGmCommandBody` (reason bắt buộc); xem PCR gm.
- `ICommandContext.AddAudit(...)` (Core): outbox kind `gm_audit`; module gm projection vào `gm_audit`.
- `PlayerSnapshotReader.ReadAllAsync(playerId)`: mọi aggregate kể cả hidden (cho GM).
- Runner: command profile `Player` chỉ cho actor player, `Gm` chỉ cho actor GM có permission của command, `Dev` cho cả hai
  → sai → 403 `FORBIDDEN`. Command `Gm` phải commit với đúng một audit; command khác không được có audit (SEC-14).
- `IGmTargetValidator`: kiểm player đích trước khi chạy command GM (module gm: tồn tại + không chờ xoá → 404).
- `IKeyValueStore` từ chối ghi `receipts`, `pa_*` và `gm_audit`.
- Response GM: `Cache-Control: no-store`.
- Middleware profile: mutation GM có `Sec-Fetch-Site: cross-site` → 403.
- Startup validator: endpoint GM phải có permission trong catalog của các module đã đăng ký (SEC-08).

## 11. TST-04 — kết quả thực nghiệm

**Chưa chạy** (2026-10-26): máy phát triển không có Couchbase/Docker. Test có sẵn ở
`tests/GameServer.Platform.Couchbase.IntegrationTests/Tst04TransactionExperiments.cs`, báo Skipped khi thiếu
`COUCHBASE_CONNSTR`. Chạy trong CI hoặc máy có Couchbase rồi chép output `[TST-04 …]` vào bảng dưới.

| Test | Kết quả | Ngày / SDK / Server |
| --- | --- | --- |
| V15-T1 replace(cas) thường vào doc đang staged | — | — |
| V15-T2 insert thường vào staged insert | — | — |
| V15-T3 staged insert vô hình với Get/Exists/N1QL | — | — |
| V23-T1 `Task.WhenAll` ctx.GetAsync; GetMulti vs tuần tự | — | — |
| V17-T1 số ATR sau N transaction | — | — |
| V17-T2 ATR nằm ở `txn_meta` | — | — |
| V4-T1 `Majority` trên bucket replicas = 0 | — | — |
| V16-T1 counter ngoài transaction không rollback | — | — |

Phần chưa tự động hoá (drill thủ công): lost-attempt cleanup khi kill process giữa commit (V17-T1), hai
process khác `MetadataCollection` (V17-T2), bucket replicas = 1 trên một node (V4-T1).
