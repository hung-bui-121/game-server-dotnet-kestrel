# PCR — platform

Published Contract Register của nền tảng (PCR-01). Thay đổi mục **semantic** là breaking change (ARC-08);
thay đổi mục **vận hành** chỉ cần ghi changelog. Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4.

Phiên bản: 0.1.0 (slice 001 platform-core, 2026-10-26).

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
- Đọc bundle (DAT-11): aggregate `Touches ∪ Reads` + receipt nạp bằng **một** `GetMulti` (Couchbase) trong
  transaction. **Phát hiện khi implement (SDK 3.10.0):** `GetMulti` không trả `TransactionGetResult`, nên mỗi
  aggregate **thực sự bị ghi** cần thêm một `ctx.GetAsync` trước `ReplaceAsync`; nội dung được so với bản đọc
  bundle, khác → command chạy lại (`StaleReadException`). Chi phí đọc ≈ 1 RTT + 1 RTT cho mỗi aggregate đổi.
  Tắt `GetMulti` bằng `Couchbase:UseGetMulti=false` (đọc tuần tự).
- Mọi ghi `pa_*` và `rcpt::*` chỉ qua `ICommandRunner` (arch test). Ngoại lệ duy nhất: cờ `outboxPending` của
  receipt **đã commit** được đặt `false` bằng `MutateIn` ngoài transaction (receipt không bao giờ còn bị
  transaction ghi lại).
- Business rejection (HTTP 200 + code) **không tạo receipt**: transaction rollback, kể cả claim marker. Retry
  cùng `requestId` sau rejection được xử lý lại như mới.
- Insert race (hai instance cùng ghi receipt/claim) hoặc stale read → runner chạy lại tối đa 3 vòng; hết vòng →
  409 `CONCURRENT_UPDATE` (retryable).

## 3. Idempotency và replay (semantic — CMD-01..09)

| Mục | Giá trị |
| --- | --- |
| `requestId` | UUID ở header `X-Request-Id` (cũng là correlation id); thiếu/sai → 400 |
| `receiptId` | `base64url(SHA-256(actorId|operation|requestId))[0..32]` |
| `bodyHash` | hex SHA-256 trên raw body như nhận được |
| Replay window | 24 h theo `committedAtUtc` (`GameServer:Commands:ReplayWindowHours`) |
| Replay cùng body | cùng `code`, `receipt_id`, `result` (byte-identical), `replayed = true`; `updates` chứa aggregate **hiện hành** (`full = true`, `new_rev` = rev hiện tại ≥ rev đã commit, `base_rev` = rev đã commit − 1) |
| Replay khác body | 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` |
| Ngoài window | như request mới; receipt cũ được thay bằng receipt mới |
| Commit không rõ | 503 `COMMIT_UNKNOWN_RETRY` + `retry_after_ms`; retry cùng bytes |
| Receipt / resultCompact | ≤ 4 KiB / ≤ 512 B; vượt = lỗi lập trình (500) |
| Lookup | `GET /api/v1/player/receipts/{receiptId}` — chỉ actor sở hữu; actor khác nhận 404 |

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
sweeper mỗi 30 s cho receipt pending cũ hơn 10 s (partial index `ix_receipts_outbox_pending`). Consumer
idempotent theo `entryId`; projection mặc định Insert-if-absent `<kind>::<entryId>`. Kind không có consumer →
receipt giữ pending, metric `outbox_relay_inline_failures_total`.

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

Dải: core 0–999; module khai `ErrorCodeSet(rangeStart, rangeEnd)`; trùng tên/số/chồng dải → startup fail.
Player nhận `ErrorEnvelope` (Protobuf, kể cả lỗi middleware); GM nhận Problem Details JSON với `code`,
`codeNumber`, `requestId`, `retryable`, `retryAfterMs`.

## 9. Public endpoint allowlist (semantic — SEC-09)

| Route | Mục đích |
| --- | --- |
| `GET /v1/time` | server time (TIME-01) |

Health endpoints (`/health/live`, `/health/ready`) thêm ở slice 005.

## 10. Extensible capabilities (EXT-01)

| Capability | Điểm thay thế |
| --- | --- |
| Command handler | `ConfigureExtensions(x => x.ReplaceHandler<TReq, TRes, THandler>(operation))` |
| Policy/service của module | `ConfigureExtensions(x => x.ReplacePolicy<TService, TImpl>())` |
| Gate (maintenance/version) | đăng ký `IGateEvaluator` |
| Outbox consumer | đăng ký `IOutboxConsumer` cho một `kind` |
| Document store | `ITransactionalDocumentStore` (Couchbase; in-memory chỉ cho test/dev) |

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
