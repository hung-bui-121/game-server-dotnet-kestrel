# PCR — platform

Published Contract Register của nền tảng (PCR-01). Thay đổi mục **semantic** là breaking change (ARC-08);
thay đổi mục **vận hành** chỉ cần ghi changelog. Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4.

Phiên bản: 0.7.0 (slice 001 + sửa sau review + slice 002 identity + slice 004 GM + slice 005 observability, 2026-10-26;
slice 010 lease job và xoá tài khoản, 2026-10-30 — mục 16; slice 011 môi trường theo flavor, file store, setting Couchbase,
`--provision`, 2026-10-30 — mục 17; slice 012 analytics login và payment, 2026-10-31 — mục 18).

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
  đạt** cho tới khi bật GetMulti — cần owner quyết định sau khi có số đo. Số đo đầu tiên (mục 11, V23-T1,
  2026-10-27): `Task.WhenAll` nhiều `ctx.GetAsync` chạy được trong transaction và nhanh nhất — phương án thứ ba,
  ≈ 1 RTT mà không cần get lại trước khi ghi.
- Conflict: Couchbase chỉ phát hiện **write-write**; document chỉ đọc (`Reads`) có thể đổi trong lúc transaction
  chạy (read skew). Store in-memory mô phỏng đúng như vậy.
- Mọi ghi `pa_*` và `rcpt::*` chỉ qua `ICommandRunner` / `IPlayerAggregateWriter` / `IAccountPurgeWriter` (purge, mục 16;
  arch test: trong `GameServer.Platform` chỉ namespace `Commands`, `Aggregates`, `Persistence`, `Privacy` dùng
  `ITransactionContext`). Ngoại lệ: các cờ
  outbox của receipt **đã commit** (`outboxPending`, `outboxAttempts`, `outboxNextAttemptAtUtc`,
  `outboxDeadLetter`) được cập nhật bằng `MutateIn` ngoài transaction, có CAS lấy từ một get mới; receipt chưa nhìn
  thấy (đang unstaging) được bỏ qua cho sweeper. Rủi ro còn lại: receipt hết replay window bị transaction mới thay
  thế cùng lúc với `MutateIn` (hiếm; xem V15).
- Business rejection (HTTP 200 + code) **không tạo receipt**: transaction rollback, kể cả claim marker. Retry
  cùng `requestId` sau rejection được xử lý lại như mới. Rejection bằng mã có HTTP ≠ 200
  (`CommandOutcome.Reject(code, details)`, vd. 409 `IDENTITY_CONFLICT`) cũng rollback, nhưng trả **error envelope**
  đúng status kèm `details` thay vì `CommandResponse` (slice 009).
- Claim marker có thể mang `attributes` (`TryClaimAsync(collection, key, attributes)`), lưu trong document claim.
  Document insert-only cùng key space với claim nhưng tạo ngoài command (vd. mapping của login provider lần đầu) đi
  qua `ClaimStore.InsertOrGetAsync` (transaction đọc → insert, đua → trả bản thắng) — không bao giờ ghi KV vào key
  mà transaction có thể insert (V15-T2).
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
| Command có bước xác minh async (`MapPlayerCommand(..., toDomainAsync, ...)`: IAP, `identity.link`) | receipt đã commit trong window được replay **trước** `toDomainAsync` (`ICommandRunner.TryReplayAsync`): retry không gọi lại store/provider (mã dùng một lần); khác body → 409 trước khi gọi ra ngoài |

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

Health endpoints (`/health/live`, `/health/ready`) do module `platform.observability` khai (slice 005); đường dẫn cố
định, không có version (chỉ hai route này được miễn quy tắc `/v{n}/`). Xem mục 13.

## 10. Extensible capabilities (EXT-01)

| Capability | Điểm thay thế |
| --- | --- |
| Command handler | `ConfigureExtensions(x => x.ReplaceHandler<TReq, TRes, THandler>(operation))` |
| Policy/service của module | `ConfigureExtensions(x => x.ReplacePolicy<TService, TImpl>())` |
| Gate (maintenance/version) | đăng ký `IGateEvaluator` |
| Outbox consumer | đăng ký `IOutboxConsumer` cho một `kind` |
| Document store | `ITransactionalDocumentStore` + `IKeyValueStore` (Couchbase; in-memory chỉ Development/Test — startup chặn ở môi trường khác) |
| Command guard | `ICommandGuard` (chạy trước mỗi lần thực thi mới) |
| Identity provider | `IIdentityProviderAdapter` (module identity, slice 009) |
| Truy vấn phân trang | `IPagedQueryStore` (in-memory; Couchbase N1QL `RequestPlus`) |
| Health của store | `IStoreHealthProbe` (mặc định: một KV get key không tồn tại) |
| Endpoint cấp host | `IHostEndpointsModule` (map ở mọi host, bất kể `MapPlayerApi`/`MapGmApi`) |
| Lease job (slice 010) | `ModuleDescriptor.Jobs = [new JobDescriptor(name, typeof(TJob), intervalSeconds)]`, `TJob : ILeaseJob` (Core) — mục 16 |
| Xoá dữ liệu player (slice 010) | `CollectionSpec(..., Deletion: DeletionTreatment, PlayerField = "playerId")` bắt buộc; `ModuleDescriptor.DeletionSteps = [new DeletionStepRegistration(name, typeof(TStep))]`, `TStep : IAccountDeletionStep` — mục 16 |
| Truy vấn bảo trì (slice 010) | `IMaintenanceQueryStore` (`QueryDueAsync`, `QueryReferencesAsync`; in-memory và Couchbase) — chỉ cho job |
| Hook sau command (slice 010) | `MapPlayerCommand(..., afterExecute)` / `MapGmCommand(..., afterExecute)`: `Func<CommandExecution, HttpContext, Task>` chạy sau commit **hoặc** replay (execution có receipt), không chạy khi bị từ chối bằng mã nghiệp vụ; dùng để làm mới cache của instance phục vụ (vd. `ProfileCache`) |

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
- `IGmTargetValidator.ValidateAsync(playerId, operation, ct)`: kiểm player đích trước khi chạy command GM (module gm: tồn
  tại + không chờ xoá → 404; riêng `identity.cancel_deletion` được nhắm player đang chờ xoá, player đã purge → 409
  `DELETION_GRACE_OVER`). **Breaking (slice 010):** thêm tham số `operation` — implementation của game phải thêm tham số
  (migration: bỏ qua nó để giữ hành vi cũ).
- `IKeyValueStore` từ chối ghi `receipts`, `pa_*`, `gm_audit` và (slice 010) `account_deletions`.
- Response GM: `Cache-Control: no-store`.
- Middleware profile: mutation GM có `Sec-Fetch-Site: cross-site` → 403.
- `GateHeaders` (slice 006): `X-Client-Version`, `X-Platform`, `X-Build-Id` — SDK gửi trên mọi request (GATE-02); server so
  `minClientVersion` ở P0.5.
- Startup validator: endpoint GM phải có permission trong catalog của các module đã đăng ký (SEC-08).

## 11. TST-04 — kết quả thực nghiệm

Test ở `tests/GameServer.Platform.Couchbase.IntegrationTests/Tst04TransactionExperiments.cs` (Skipped khi thiếu
`COUCHBASE_CONNSTR`). Lần chạy đầu: **2026-10-27, SDK 3.10.0, Couchbase Server Enterprise 7.6.12 (một node,
localhost, Windows), bucket replicas = 0** — 33/33 integration test pass. Số đo thời gian trên localhost (RTT < 1 ms)
chỉ để so sánh tương đối, không dùng làm số capacity.

| Test | Kết quả | Hệ quả |
| --- | --- | --- |
| V15-T1 replace(cas) thường vào doc đang staged | `CasMismatchException`; transaction commit; giá trị cuối = của transaction | Ghi CAS ngoài transaction không đè được bản staged |
| V15-T2 insert thường vào staged insert | **insert thường thành công, transaction vẫn báo commit, giá trị cuối = của insert thường** — bản ghi của transaction bị mất mà không lỗi | Xác nhận luật DAT-04: không được ghi ngoài transaction vào key mà transaction insert (`rcpt::*`, claim marker). `IKeyValueStore` đã chặn `receipts`/`pa_*`/`gm_audit`; collection claim marker (vd. `extref_claims`) **chưa** bị chặn — follow-up: khai báo collection claim trong descriptor và chặn ở `EnsureWritable` |
| V15-T3 staged insert vô hình với Get/Exists/N1QL | Vô hình với Get, Exists và N1QL `request_plus` (đúng tài liệu) | Đọc ngoài transaction không thấy receipt chưa commit |
| V23-T1 đọc 4 doc trong transaction, 50 vòng sau 3 vòng làm nóng (2 lần chạy) | tuần tự: median 1,25–2,91 ms, p95 2,10–4,98 ms · `Task.WhenAll` 4 × `ctx.GetAsync`: **median 0,49–0,90 ms**, p95 1,81–2,27 ms, không lỗi · `GetMulti` (`PrioritizeLatency`): median 1,53–2,80 ms, p95 2,81–4,46 ms · `Task.WhenAll` rồi `Replace` từng `TransactionGetResult` (tuần tự): commit | Đọc song song bằng `Task.WhenAll` chạy được trong transaction và nhanh nhất, kết quả dùng thẳng cho `Replace` (không cần get lại như chế độ GetMulti). Quy tắc SDK cho thao tác đồng thời: chờ mọi thao tác song song xong trước bước tiếp theo; mutation đầu tiên không chạy song song. Đề xuất cho DAT-11 — owner quyết (mục 2), đo lại trên cluster có độ trễ mạng thật |
| V17-T1 số ATR sau 200 transaction trên một key | Lần 1: 3 ATR trong `txn_meta`; lần 2 (đếm trước/sau): 3 → 4 (+1) | ATR dùng lại, không tăng theo số transaction (scope dùng chung với các thí nghiệm khác trong class) |
| V17-T2 ATR nằm ở `txn_meta` | 0 → 1 ATR trong `txn_meta`, 0 trong collection dữ liệu; `_txn:client-record` có trong `txn_meta` | `MetadataCollection` có hiệu lực (DAT-07) |
| V4-T1 `Majority` trên bucket replicas = 0 | Commit được | Một node dev phải dùng bucket replicas = 0 |
| V16-T1 counter ngoài transaction không rollback | Counter giữ giá trị sau khi transaction lỗi; lambda chạy 1 lần; `TransactionFailedException` | Counter cấp số (vd. id) không được coi là một phần của transaction |

Ghi chú đo: query/index **không** trả document hệ thống `_txn:*` (ATR, client record) — lần chạy đầu đếm ATR bằng
N1QL ra 0. Test đếm bằng KV `Exists` trên danh sách ATR id cố định của SDK (`AtrIds.AllAtrIds`, internal, đọc bằng
reflection — chỉ dùng trong test thực nghiệm).

Phần chưa tự động hoá (drill thủ công): lost-attempt cleanup khi kill process giữa commit (V17-T1), hai
process khác `MetadataCollection` (V17-T2), bucket replicas = 1 trên một node (V4-T1).

## 13. Observability và vận hành (slice 005 — LOG-01..06, OBS-01..04, OPS-02/03/08)

Bật bằng `gameServer.AddObservability()` (project `GameServer.Platform.Observability`, section `Observability`).

**Log (vận hành, trừ định dạng file là semantic với hệ thống ship log):**

| Mục | Giá trị |
| --- | --- |
| File | `{Logging:Directory}/{gameId}-{instanceId}-{yyyyMMdd}[_NNN].log`, compact JSON (`@t` UTC, `@mt`, `@l`, `@x`, `@tr`, `@sp`) |
| Instance id | `Observability:InstanceId` → `HOSTNAME` → tên máy. Nhiều process cùng máy + cùng thư mục phải đặt id khác nhau (LOG-03) |
| Rolling | ngày + kích thước (`FileSizeLimitBytes` 100 MiB), giữ `RetainedFileCountLimit` 30 file, `RetainedFileTimeLimitDays` tuỳ chọn; restart cùng instance append |
| Queue | async, `BufferSize` 10 000, không chặn khi đầy → bỏ và đếm `log_dropped_total` (cộng dồn cả host đã dừng) |
| Mức theo nguồn | `Logging:LevelOverrides` (prefix SourceContext → level, key không phân biệt hoa thường), cấu hình ghi đè lên mặc định `Microsoft.AspNetCore`, `Couchbase.Client.Transactions`, `ExecuteApplicationLambda` = Warning — phần transaction của Couchbase SDK ghi ~6 dòng Information mỗi transaction (đo 2026-10-27); log bootstrap/topology của SDK giữ nguyên. Level sai → startup fail. Mức thấp hơn `Logging:LogLevel` của Microsoft vẫn bị filter đó cắt trước |
| Provider khác | Serilog thay `ILoggerFactory` và **không** ghi sang provider khác (console mặc định chờ khi queue đầy → có thể chặn request) |
| Thuộc tính | `InstanceId`, `GameId`, `EnvironmentId`, `RequestId` (scope), `SourceContext`; trace/span id |
| Che dữ liệu | giá trị thuộc tính (kể cả lồng trong object destructure `{@x}`) có tên chứa token/password/passwd/pwd/secret/pepper/authorization/bearer/jwt/cookie/privatekey/signingkey/apikey/connectionstring/receiptpayload/signeddata/credential → `***`. **Không** được che: object log bằng `{x}` (không `@`, bị `ToString()`), nội dung exception (`@x`), scope dạng chuỗi — module không được đưa secret vào các chỗ đó |
| Lỗi sink | `SelfLog` (process-wide; thay handler SelfLog khác) → `log_sink_errors_total` (thông báo "queue đầy" không tính — đã đếm ở drop), lỗi gần nhất trong chi tiết GM; `log_dir_free_bytes` đo **mount chứa thư mục log** (root dài nhất khớp); health `log` Degraded khi lỗi trong `SinkErrorWindowSeconds` (300) hoặc đĩa < `LowDiskBytes` (1 GiB) |

**Metrics/traces:** OpenTelemetry; sources/meters `GameServer.*` + ASP.NET Core (hosting, Kestrel, rate limiting) + HttpClient
+ runtime. OTLP khi `Observability:Otlp:Endpoint` có giá trị (`Protocol` `grpc` | `http/protobuf`; với HTTP, đường dẫn
`/v1/traces`, `/v1/metrics` được nối vào endpoint gốc); Prometheus qua collector. Cấu hình sai (level, URI, protocol) → startup fail.
Metric bổ sung slice 005: `command_results_total{operation, code, replayed}`, `http_errors_total{code}`, `log_*`. Không metric nào
có tag player/request (OBS-02); `gs.request_id`, `gs.player_id` là attribute của span. Span `command {operation}` là con của span
HTTP. Couchbase SDK tracing và `txn_cleanup_lost_total`: chờ cluster thật (V2).

**Health (semantic):**

| Route | Profile | Check | Trả về |
| --- | --- | --- | --- |
| `GET /health/live` | Public | không có | 200 `Healthy` |
| `GET /health/ready` | Public | `store` (≤ 2 s), `config`, `log`, `shutdown` | 200 `Healthy`/`Degraded`, 503 `Unhealthy`; chỉ một từ, không chi tiết; kết quả dùng chung `Observability:ReadyCacheMilliseconds` (1000) để flood không thành flood vào store |
| `GET /gm/v1/health` | GM `ops.read` | như ready | JSON từng check: status, description, duration, data |

**Shutdown (OPS-03):** khi dừng, ready = 503 ngay; server vẫn nhận request thêm `GameServer:Hosting:DrainDelaySeconds` (5) để
load balancer kịp rút instance, rồi mới ngừng nhận; request đang chạy có tới `ShutdownTimeoutSeconds` (25, tính cả drain); commit
đang chạy hoàn tất (PERF-02); logger dispose cùng container → flush. Orchestrator đặt grace period ≥ 35 s. Cấu hình sai → startup fail.

**Reverse proxy (OPS-02):** `GameServer:Hosting:ForwardedHeaders {Enabled, KnownProxies[], KnownNetworks[] (CIDR), ForwardLimit=1}`;
chỉ proxy trong danh sách được tin `X-Forwarded-For`/`-Proto`; bật mà danh sách rỗng, hoặc network `/0` → startup fail. Chuỗi
CDN + load balancer cần `ForwardLimit` = số hop tin cậy (để 1 thì IP client là IP edge của CDN).

**Secret (OPS-08):** ngoài Development/Test, các khoá `GameServer:SecretKeys` (mặc định `Identity:GuestPepper`,
`Couchbase:Password`, `Iap:Google:ServiceAccountPrivateKeyPem`; danh sách hiện tại: mục 17) không được có giá trị trong **bất kỳ** file cấu hình nào (kể cả
khi biến môi trường ghi đè) → startup fail; dùng biến môi trường (`Identity__GuestPepper`) hoặc secret provider.

## 15. Gate: bảo trì và phiên bản client tối thiểu (slice 008 — GATE-01, GATE-02, PERF-08)

**Document `gate/gate`** (collection platform `gate`, chỉ ghi trong transaction có audit, qua GM API — PCR gm):
`{maintenance{enabled, message, expectedEndUtc}, minClientVersions{platform: {minVersion, updateUrl}}, rev}`.

| Mục | Giá trị (semantic với client) |
| --- | --- |
| Phạm vi | Endpoint có `GateMetadata`: mọi Player API + login. GM, health, Dev không bị chặn. Chạy **trước** authentication |
| Bảo trì | 503 `MAINTENANCE`, details `message`, `expectedEndUtc` (ISO UTC); `retryAfterMs` = thời gian tới giờ dự kiến (30 s … 1 h), không có giờ → 300 000 |
| Phiên bản | Header `X-Platform` (so khớp không phân biệt hoa thường) có `minVersion` → `X-Client-Version` (`a.b[.c[.d]]`, số) nhỏ hơn, thiếu hoặc sai → 426 `UPDATE_REQUIRED`, details `minClientVersion`, `updateUrl`. Thiếu `X-Platform` hoặc platform không có minimum → không kiểm phiên bản. Bảo trì ưu tiên |
| Cache | `GameServer:Gate:CacheSeconds` (mặc định 10, tối đa 10) per instance; instance xử lý thay đổi áp dụng ngay (thay đổi xảy ra trong lúc đang đọc không bị mất); lỗi đọc store → giữ bản cũ (fail-open, log Warning); lần đọc đầu lỗi → cho qua và thử lại sau 1 s; đồng hồ lùi → coi như hết hạn |
| Thay thế | `IGateEvaluator` (EXT-04); mặc định `StoreGateEvaluator` |

**TTL cache đọc chung (PERF-08):** session + `pa_profile` (epoch, ban) `Identity:CacheSeconds` ≤ 60; staff/permission/
ticket GM `Gm:CacheSeconds` ≤ 60; gate ≤ 10. Giá trị ngoài khoảng → startup fail. Config không cache TTL (bất biến theo
`configVersion`). Không cache nào quyết định trừ tiền: số dư đọc trong transaction.

## 14. Data Protection, CSRF, CORS, audited store, OpenAPI (slice 007 — OPS-04, SEC-05, SEC-06, SEC-14)

**Key ring dùng chung (semantic với vận hành):** mọi instance của một deployment (`gameId` + `environmentId`, application
name `gameserver:{gameId}:{environmentId}`) dùng một key ring trong document `dataprotection/keyring`
(`{keys[], rev}`, Insert/Replace có CAS, gộp khi ghi đồng thời). Bảo vệ cookie GM, antiforgery token, cookie
correlation/nonce của OIDC.

| Mục | Giá trị |
| --- | --- |
| Mã hoá at rest | AES-256-GCM bằng `DataProtection:MasterKey` (base64 32 byte, secret provider); phần tử lưu `masterKeyId`, `nonce`, `tag`; `DataProtection:PreviousMasterKeys` giữ khoá cũ còn giải mã được. Thiếu/sai ngoài Dev/Test → startup fail; Dev/Test không có khoá → lưu không mã hoá (log Warning) |
| Không sync-over-async | `IXmlRepository` đọc snapshot trong RAM; `KeyRingService` tải trước khi nhận request (`StartingAsync`), làm mới mỗi `RefreshMinutes` (5) |
| Tạo key | Request không bao giờ tạo key. Key đầu tiên khi khởi động (instance khởi động cùng lúc: chỉ key ghi trước được giữ); key kế tiếp tạo trước `ActivationLeadHours` (48 h) khi key hiện tại hết hạn (`KeyLifetimeDays` 90). Rotate theo yêu cầu (`POST /gm/v1/ops/dataprotection/rotate`) kích hoạt sau `RotationDelaySeconds` (900; ≥ 2 × refresh + 5 phút ngoài Dev/Test) |
| Ghi | Mỗi lần thêm key = một transaction `IAuditedStore` {key ring, 1 audit}: key tạo theo lịch → audit `platform_audit` (`dataprotection.key_created`); rotate → audit `ops.keys.rotate` của staff, retry cùng `X-Request-Id` → replay, không tạo key thứ hai. `dataprotection` chỉ ghi trong transaction |
| Key kích hoạt | Lần làm mới gặp key kích hoạt từ lần trước (± 5 phút) → Data Protection dựng lại key ring, key mới thành mặc định ngay (không chờ ~24 h) |
| Grace | Key cũ không bị xoá: cookie phát hành trước rotate vẫn giải mã được (ACC-49) |
| Instance khác | Key ring tải về làm Data Protection bỏ cache ngay (`RefreshingKeyManager`); key id lạ → làm mới một lần |

**CSRF (SEC-05):** `ProfileEnforcementMiddleware` sau xác thực: method unsafe + (scheme cookie trong
`CsrfOptions.CookieSchemes` hoặc endpoint `RequireCsrf()`) → `IAntiforgery` (header `X-CSRF-Token` + cookie) +
`Sec-Fetch-Site` ≠ `cross-site` + `Origin` cùng origin/allowlist; lỗi 403 `CSRF_FAILED`. Endpoint Public `/gm/**` unsafe
thiếu `RequireCsrf()` → startup fail. Đường dẫn có nhưng sai method → 405 `METHOD_NOT_ALLOWED` (ErrorEnvelope/Problem Details).

**CORS (SEC-06):** middleware CORS chỉ trên `/gm/**`, policy `gs-gm` do module gm đăng ký khi có allowlist; không có
policy → không header CORS.

**`IAuditedStore` (SEC-14):** transaction cho document không phải dữ liệu player (staff, cấu hình vận hành): từ chối
`pa_*`/`receipts`; transaction có ghi phải insert đúng 1 `gm_audit`, audit không sửa được. `gm_staff`,
`gm_staff_identities` chỉ ghi trong transaction (`IKeyValueStore` từ chối — TST-04 V15-T2): module khai
`CollectionSpec(TransactionOnly: true)`, platform tự thêm `dataprotection`. `gm_audit` là collection của platform (có cả
khi không đăng ký module gm).

**OpenAPI:** document `gm` (`GET /gm/v1/openapi/gm.json`, chỉ Development/Test), bản commit `docs/api/gm-openapi.json`
được test so khớp (`UPDATE_OPENAPI=1` để ghi lại). Mỗi operation: `x-gm-permission` (`public` hoặc permission),
`x-gm-csrf`, response `4XX`/`5XX` = Problem Details.

**Khởi tạo store dùng chung (`IStoreInitializer`):** Couchbase provisioner chạy một lần mỗi process; key ring và
bootstrap staff chờ nó trước khi đọc store, bất kể thứ tự hosted service. Tạo scope/collection: "đã tồn tại" = instance khác
đã tạo; cluster manager trả 503 "Service is temporarily unavailable" khi đang bận đổi manifest (gặp khi nhiều scope được
provision cùng lúc, 2026-10-30) → thử lại tối đa 6 lần, backoff 0,5 s × lần (như câu lệnh index; sửa ở slice 010).

## 16. Lease job và xoá tài khoản (slice 010 — JOB-01, PRIV-01, DAT-03, DAT-04, PERF-07, ACC-37)

### Lease job (semantic với vận hành)

Mỗi job khai trong `ModuleDescriptor.Jobs` chạy trên **một** instance tại một thời điểm, điều phối bằng document KV
`leases/leases::{jobName}` (CAS, **không expiry** — DAT-03): `{jobName, owner ({machine}:{pid}:{8 hex}) | null, fencingToken,
leaseUntilUtc, nextRunAtUtc, acquiredAtUtc, renewedAtUtc, lastRun{startedAtUtc, completedAtUtc, result}, gameId, environmentId}`.

| Mục | Giá trị |
| --- | --- |
| Lấy lease | document chưa có (insert, token 1), hoặc đã nhả và `nextRunAtUtc <= now`, hoặc đang giữ nhưng `leaseUntilUtc <= now` (takeover, log Warning). Mỗi lần lấy `fencingToken + 1`. Đua → một instance thắng, không retry |
| Gia hạn | mỗi `RenewSeconds`; CAS thua, hoặc không gia hạn được trước `leaseUntilUtc − 5 s` → mất lease, token của run bị huỷ (`LeaseLost`, không nhả) |
| Nhả | sau run: xong/lỗi → `nextRunAtUtc = now + interval`; bị dừng do shutdown (OPS-03, budget 2 s, không dùng token dừng) → `nextRunAtUtc = now` để instance khác chạy ngay |
| Fencing | lease chỉ giới hạn ai **bắt đầu**; tiến độ được rào ở nơi lưu (purge record, mục dưới): instance tạm dừng (GC/VM) quá lease bị từ chối khi ghi — đã kiểm trên Couchbase thật (`CouchbaseLeaseAndPurgeTests`). Gặp token lớn hơn ở record, job đọc lại document lease (`LeaseStore.IsHeldAsync`): lease đã sang token khác → `LeaseLost` (dừng, không nhả); lease vẫn mang token của run → document lease đã bị xoá/tạo lại (token bắt đầu lại từ 1): bỏ qua player đó + LogError, run tiếp tục (review T054) |
| **Runbook: không bao giờ xoá `leases::*`** | token tạo lại bắt đầu từ 1 trong khi purge record giữ token lớn hơn → các player đó bị bỏ qua mãi (LogError "Purge of player … skipped … never delete leases::*", metric `account_purges_total{result=fenced_out}`). Khắc phục: đặt `fencingToken` của `leases::identity.account-purge` (KV, CAS) lớn hơn token lớn nhất trong `account_deletions` |
| Thời gian | từ `IClock`; chỉ chờ poll/renew dùng giờ thật. `LeaseJobRunner.RunOnceAsync(job, ct)` cho test/vận hành |
| Khai báo | tên `^[a-z][a-z0-9._-]{1,63}$`, trùng giữa module, type không phải `ILeaseJob`, interval ngoài 10..86400 → startup fail; `GameServer:Jobs:Settings:{job}` với tên không có job nào đăng ký → startup fail (review T054) |
| Metrics | `job_runs_total{job,result}` (chỉ run đã giữ lease), `job_run_duration_seconds{job}`, `job_lease_acquisitions_total{job}`, `job_lease_losses_total{job}`; activity `job {name}` |

| Cấu hình (vận hành) | Mặc định | Giới hạn |
| --- | --- | --- |
| `GameServer:Jobs:Enabled` | true (TestHost: false) | — |
| `GameServer:Jobs:LeaseSeconds` | 60 | 30..600, ≥ 3 × `Transactions:ExpirationSeconds` |
| `GameServer:Jobs:RenewSeconds` | 20 | 1..LeaseSeconds/3 |
| `GameServer:Jobs:PollSeconds` | 30 (± 20 %) | 5..LeaseSeconds — mỗi instance không giữ lease tốn 1 KV get/job/poll |
| `GameServer:Jobs:Settings:{job}:Enabled` / `:IntervalSeconds` | true / mặc định của descriptor | interval 10..86400 |

### Deletion treatment (semantic với module — FR-019)

Mọi collection module khai và mọi collection đích projection phải có **đúng một** `DeletionTreatment` (`NoPlayerData`,
`Anonymize`, `Remove`, `ModuleStep`), nếu không startup fail (thông báo nêu module + collection). Platform cố định: `pa_*` =
Remove theo key; `receipts`, `gm_audit` = Anonymize (`playerId`); `leases`, `dataprotection`, `gate` = NoPlayerData;
`account_deletions` = purge record (ngoài kế hoạch). `ModuleStep` cần một `DeletionSteps` cùng module; tên step trùng, type
không phải `IAccountDeletionStep` → startup fail. `PlayerField` = đường dẫn trường chứa player id (mặc định `playerId`).

Provisioner tạo index tham chiếu `ix_{collection}_player_ref (playerField, META().id)` cho **mọi** collection Anonymize/Remove
(kể cả `receipts`, `ledger`, `gm_audit`), trừ khi đã có index khai báo đúng hai key đó và không có WHERE (review T054: index
partial không phục vụ mọi tham chiếu; index lịch sử `(player, time, id)` làm mỗi batch sort toàn bộ lịch sử của player);
`ix_receipts_player (playerId, committedAtUtc DESC, META().id DESC)` chuyển từ module gm sang platform (lịch sử GM).

### Purge (semantic)

- **Writer duy nhất**: `IAccountPurgeWriter` (Platform). Mỗi lời gọi = một transaction ≤ `BatchSize` document, đọc purge record
  và từ chối nếu `record.fencingToken > ticket.FencingToken` (`PurgeFencedOutException`) hoặc record đã `purged`
  (`PurgeAlreadyCompletedException`: chính claim này đã hoàn tất, ví dụ transaction cuối báo ambiguous nhưng đã commit →
  purge tính là `completed`, job đi tiếp — review T054), rồi ghi token
  của ticket vào record. Commit không nhận token của job (PERF-02). Phương thức: `BeginAsync` (đánh dấu profile purging + insert
  record trong một transaction, hoặc claim record đang purging), `ApplyAsync` (Anonymize/Remove theo kế hoạch, từ chối
  `pa_profile` và treatment sai), `RewriteAsync` / `ScrubOutboxAsync` (đường đi của module step), `CompleteStepAsync`,
  `CompleteAsync` (xoá `pa_profile` + record `purged` trong một transaction), `RecordFailureAsync` (backoff).
  Insert record thua đua (Couchbase báo "exists") hoặc stale read → đọc lại tối đa 3 vòng; hai instance cùng begin một player
  trên Couchbase → một record, token lớn hơn giữ (đã kiểm).
- **Thứ tự** (`AccountPurgeService`): `receipts` → `anonymize:{c}` (thứ tự đăng ký) → `step:{name}` (thứ tự module) →
  `verify` (≤ 3 vòng truy vấn lại receipts và các collection Anonymize) → `aggregates` (mọi `pa_*` trừ profile, theo key) →
  `remove:{c}` (sessions, identities…) → hoàn tất (profile + record). Các step Remove chạy **cuối** (review T054): khi mapping
  còn, credential cũ vẫn bị từ chối `ACCOUNT_DELETED`; sau `remove:identities` chỉ còn transaction cuối (cửa sổ còn lại: PCR
  identity). Step Remove tự drain mọi thứ đã vào collection của nó nên `verify` không cần truy vấn lại chúng. Step xong được ghi vào `completedSteps`; purge tiếp tục từ step dở, chạy lại
  step dở là vô hại. Mỗi step là vòng `QueryReferencesAsync` (RequestPlus, ≤ `BatchSize`) + ghi cho tới khi rỗng; một batch không
  ghi được gì mà lần sau trả lại đúng các key đó → lỗi (không lặp vô hạn).
- **Quy tắc ghi đè (R8, D1)**: mọi chuỗi bằng đúng player id (lồng ở bất kỳ đâu) → anonymous id; trường khác giữ nguyên. Receipt
  của player thêm: `resultCompact = ""`, `bodyHash = ""`, **`requestId = ""`, `correlationId = ""`**, `claimKeys` trỏ vào
  collection Remove → `"{collection}/redacted"`, outbox `gm_audit` → `payload.correlationId = ""`; `gm_audit` của player:
  `payload.correlationId = ""` (`receiptId` giữ). Lý do (review T054, B1): khoá receipt `SHA-256(actorId|playerId|operation|requestId)`
  không có secret — còn `requestId` (hoặc correlation id, cùng UUID) cạnh khoá thì ai cũng thử được `Compute(X, X, op, requestId)
  == receiptId` với id ứng viên X và nối lại mọi bản ghi qua `receiptId`/`entryId`. Test quét mọi chuỗi trong store sau purge.
  Remove ở collection không phải `pa_*` chỉ xoá document mà trường player **vẫn** bằng player id.
- **Anonymous id**: `anon_` + 26 base32 (128 bit, `RandomNumberGenerator`), một id cho mọi bản ghi giữ lại của player; chỉ nằm
  trong record khi đang purging, bị xoá khi `purged`; **không bao giờ log** (log purge chỉ có player id, step, kiểu exception).
- **Receipt outbox đang chờ**: sau khi viết lại receipt, purge tự relay outbox còn pending (`IOutboxRelay`) để projection mang
  anonymous id.
- **Module step**: nhận `IPlayerDataEraser` (`PlayerId`, `AnonymousId`, `BatchSize`, `FindAsync`, `RewriteAsync` — trả null =
  xoá, `ScrubOutboxAsync` cho projection kind của chính module), chỉ trên collection của chính module; `pa_*`, `receipts`,
  `gm_audit`, collection module khác → từ chối. `FindAsync` trả tối đa `min(limit, BatchSize)` key; `RewriteAsync` > `BatchSize`
  key → `ArgumentException`; hết budget của run giữa step → `PurgeBudgetExhaustedException` (step **không được nuốt**), purge
  `incomplete`, step chạy lại từ đầu ở run sau (review T054). Step phải idempotent. Module không bao giờ mở transaction và chỉ
  job purge + `Platform.Privacy` được phụ thuộc `IAccountPurgeWriter` (arch test).
- **Thất bại**: record giữ `purging`, `attempts + 1`, `lastFailure{step, errorType, atUtc}` (không message),
  `nextAttemptAtUtc = now + RetryDelayMinutes × 2^(attempts−1)` (tối đa `MaxRetryDelayMinutes`); step xong reset `attempts`.
  Metric `account_purge_step_failures_total{step}` + LogError. Step id `begin`: purge chưa claim được record (lỗi store, profile
  không đọc được) — player bị bỏ qua tới run sau, không chặn hàng đợi.

**Document `account_deletions/deletion::{playerId}`** (chỉ ghi trong transaction, không expiry): `{playerId, gameId,
environmentId, state (purging|purged), requestedAtUtc, purgeAfterUtc, purgeStartedAtUtc, purgedAtUtc, anonymousId?,
fencingToken, completedSteps[]?, documentsProcessed, attempts?, lastFailure?, nextAttemptAtUtc}` (chi tiết data-model 010).
Sau purge là purge record (GM view, cancel → 409) — nơi duy nhất còn player id cũ (SC-002, kiểm bằng scan mọi collection
trên Couchbase).

**Truy vấn bảo trì (`IMaintenanceQueryStore`, PERF-07)** — chỉ dùng trong job, `RequestPlus`, limit 1..1000, tên collection/
trường chỉ gồm định danh:

| Truy vấn | Dùng cho | Index (EXPLAIN trên Couchbase 7.6.12, 2026-10-30) |
| --- | --- | --- |
| `QueryDueAsync(DueQuery)`: trường thời gian `<= now`, trường unset `IS NOT VALUED`, cũ nhất trước rồi theo key | record đến hạn (`nextAttemptAtUtc`), profile hết grace (`state.deleted.purgeAfterUtc`, định dạng STJ — `DueTimeFormat.Json`) | `ix_account_deletions_due (nextAttemptAtUtc, META().id) WHERE purgedAtUtc IS NOT VALUED`, `ix_profile_deletion_due (state.deleted.purgeAfterUtc, META().id) WHERE state.deleted.purgeStartedAtUtc IS NOT VALUED`: đọc theo thứ tự index, không sort. (Bản đầu không có `META().id` trong key → planner sort **toàn bộ backlog đến hạn** mỗi run — sửa khi chạy EXPLAIN ở T052) |
| `QueryReferencesAsync(ReferenceQuery)`: trường = giá trị, theo key, `AfterKey` (thêm ở slice 010, additive) để phân trang qua document job không đổi | các step purge, `ScrubOutboxAsync` | index `_player_ref (field, META().id)` của **cả bảy** collection (receipts, gm_audit, ledger, identities, sessions, extref_claims, counter_log): thứ tự index, LIMIT đẩy xuống, không sort (assert trên EXPLAIN). Bản trước review T054 dùng `ix_receipts_player`/`ix_ledger_player`/`ix_gm_audit_player` (khoá thứ hai `committedAtUtc`) → sort toàn bộ lịch sử của player mỗi batch (~n²/BatchSize) — đã sửa. Không truy vấn nào primary/sequential scan |

**Store primitive mới:** `ITransactionContext.RemoveAsync(StoredDocument existing)` (Couchbase `AttemptContext.RemoveAsync`;
in-memory: staged remove, có version check, rollback giữ document; conformance chạy cả hai store).

**Creation anchor (R9):** `IPlayerAggregateWriter.UpdateAsync(..., DocumentKey? creationAnchor, ct)` — aggregate chưa tồn tại
chỉ được tạo khi anchor tồn tại và cùng `playerId`; anchor được ghi lại nguyên trạng trong cùng transaction (write lock), nên
login lần đầu đua với purge xoá mapping: một bên commit; anchor mất → `AggregateAnchorMissingException`, không ghi gì (kiểm trên
Couchbase thật, cả mapping do login guest ghi bằng KV).

| Cấu hình (vận hành) | Mặc định | Giới hạn |
| --- | --- | --- |
| `GameServer:Purge:MaxPlayersPerRun` | 50 | 1..500 |
| `GameServer:Purge:BatchSize` | 25 | 1..100 document / transaction (DAT-04) |
| `GameServer:Purge:MaxDocumentsPerRun` | 5000 | 100..100 000 |
| `GameServer:Purge:RetryDelayMinutes` | 10 | 1..1440, nhân đôi mỗi lần lỗi liên tiếp |
| `GameServer:Purge:MaxRetryDelayMinutes` | 1440 | ≥ RetryDelayMinutes |

**Throughput:** mặc định 50 player / 10 phút ≈ 7 200 player/ngày; player có 100 k document lịch sử cần ~20 run. Đo trên
Couchbase 7.6.12 một node localhost (2026-10-30): purge trọn một player (9 receipt, 2 ledger, 1 audit, claim, counter log,
2 identity, session, các `pa_*`) trong một run ≈ 0,9 s — chỉ số tương đối, không phải capacity.

**Metrics và cảnh báo:** `account_purges_total{result}` (`completed`, `incomplete`, `failed`, `fenced_out`),
`account_purge_step_failures_total{step}` (step id ở trên, `begin`, `complete`), `account_purge_documents_total{action}`
(`anonymize`, `remove`), gauge `account_purge_overdue_seconds` (tuổi quá `purgeAfter` của player đến hạn cũ nhất — record tiếp
tục **và** profile chờ, kể cả khi record tiếp tục lấp đầy run — mà run gần nhất trên instance giữ lease thấy). Giá trị chỉ được
báo trong 2 × interval của job (giờ thật) rồi biến mất: instance không còn chạy job không báo giá trị cũ (review T054).
**Cảnh báo: `max(account_purge_overdue_seconds) > 86400`** (SC-006) và **gauge vắng mặt > 3 × interval** (job không chạy). Giới
hạn đã biết: player đang chờ backoff sau lỗi (`nextAttemptAtUtc` ở tương lai) không nằm trong danh sách, nên lỗi lặp lại không
làm gauge tăng; theo dõi thêm `account_purge_step_failures_total`.

**Ngoại lệ CMD-08/SEC-14 (Complexity Tracking của plan 010):** `ledger` và `gm_audit` không bao giờ bị xoá, nhưng purge **ghi đè**
các giá trị bằng player id (anonymous id) trong chúng, và xoá `payload.correlationId` của audit (trường correlation của SEC-14 trở
thành rỗng cho player đã purge; tham chiếu receipt giữ nguyên — review T054, B1) — chỉ qua `AccountPurgeWriter`, trong
transaction có fencing. `AuditedStore` vẫn từ chối sửa audit ở mọi đường khác.

**Hook `afterExecute`:** chạy sau commit (hoặc replay); lỗi của hook được LogError và **không** biến command đã commit thành
5xx (review T054). Hook của identity ghi metric/log trước khi làm mới cache.

**Hạng mục mở (không làm ở slice 010):**

| Hạng mục | Ghi chú |
| --- | --- |
| Schema OpenAPI cho body/response của `POST /gm/v1/players/{playerId}/deletion/cancel` | route đã có trong `gm-openapi.json` (permission, CSRF) nhưng body `{reason}` và `GmCommandResponse` chưa có schema chi tiết (review T054, 12b) |
| Id đã purge trong `pa_*` của **player khác** | purge chỉ ghi dữ liệu của chính player; giá trị bằng id cũ nằm trong aggregate của người khác (bạn bè, tin nhắn, đối thủ…) không được xoá/ẩn — cần cơ chế khi có module xã hội; liên quan P1 MSG-01 (review T054, 12c) |

**Rủi ro còn lại (vận hành):**

| Rủi ro | Hệ quả | Xử lý |
| --- | --- | --- |
| Tombstone: Couchbase giữ tombstone của document đã xoá (key `pa_*` = player id) và entry ATR tới metadata purge (mặc định 3 ngày) | id cũ còn thấy ở tầng storage vài ngày với quản trị viên | không qua API/query nào; runbook (slice 013) |
| Relay đang chạy đã đọc receipt **trước** khi viết lại → insert projection với id cũ sau step anonymize | một projection còn id cũ | purge tự relay receipt pending sau khi viết lại; step `verify` truy vấn lại mọi collection (≤ 3 vòng); thông thường receipt đã relay ≥ 30 ngày trước purge. Cửa sổ còn lại = một batch sweeper đang bay |
| `MutateIn` của sweeper vào receipt cùng lúc transaction purge | mất ghi cờ `outboxPending` | một lần relay thừa, idempotent; sweeper không ghi `playerId` |
| Restore backup trước purge | dữ liệu quay lại | ngoài phạm vi; runbook restore (slice 013) phải chạy lại purge cho record `purged` |
| Log giữ scope `RequestId` cạnh player id (LOG-01) | trong thời gian giữ log, `RequestId` + player id trong log nối lại được receipt (khoá tính lại được) dù store đã ẩn danh | ngoài store; retention log là giới hạn thời gian của liên kết này — runbook (slice 013) đặt retention log ≤ grace + thời gian purge, hoặc xoá log theo player id khi purge |
| Document `leases::*` bị xoá | player có record token lớn hơn bị bỏ qua (LogError) | runbook ở mục Lease job |
| Backlog > ≈ 7 200/ngày | SC-006 trễ | cảnh báo overdue; tăng `MaxPlayersPerRun` / giảm interval |

## 17. Môi trường, store và provisioning (slice 011 — OPS-01, OPS-08, OPS-09, SEC-01, DAT-03, DAT-04, DAT-07)

Hướng dẫn dùng: `docs/guides/environments-and-local-run.md`. Đổi exit code, switch hoặc tiền tố output dưới đây là breaking
change với pipeline và script.

### Flavor (semantic với vận hành)

Flavor = tên môi trường của host (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`), nạp `appsettings.{Flavor}.json` trên file chung;
biến môi trường rồi dòng lệnh ghi đè. Nguồn duy nhất của bảng: `StandardFlavors` (`GameServer.Platform`).

| Flavor | `GameServer:DeploymentEnvironment` | `GameServer:EnvironmentId` | `GameServer:Store` | Công cụ dev |
| --- | --- | --- | --- | --- |
| `Local` | Development | `local` | `File` | bật |
| `Dev` | Development | `dev` | `Couchbase` | bật |
| `Beta` | Staging | `beta` | `Couchbase` | tắt |
| `Uat` | Staging | `uat` | `Couchbase` | tắt |
| `Live` | Production | `live` | `Couchbase` (bắt buộc TLS) | tắt |

| Quy tắc (khởi động, trước traffic) | Thông báo nêu |
| --- | --- |
| `GameServer:DeploymentEnvironment` ∈ {Development, Staging, Production, Test}, không phân biệt hoa thường, chuẩn hoá về cách viết chuẩn. Không đặt → dùng tên môi trường của host **chỉ khi** tên đó là deployment environment. Giá trị sai/thiếu không bao giờ thành Development (fail-closed) | key, giá trị hợp lệ, file `appsettings.<Tên>.json` mong đợi (tìm thấy hay không), bảng flavor |
| `GameServer:EnvironmentId` là id chuẩn ⇒ `GameServer:DeploymentEnvironment` = giá trị của bảng (Test không được miễn) | cả hai key, giá trị mong đợi |
| Store `InMemory`/`File` chỉ ở Development/Test | `[deployment] the <InMemory/File> store is configured in '<env>'. Fix: set GameServer:Store=Couchbase …` |
| Secret trong file cấu hình ngoài Development/Test (`GameServer:SecretKeys`) | key, file, tên biến môi trường |
| Development nghe trên địa chỉ không phải loopback | một Warning sau khi server nghe (không lỗi) |

Mọi lỗi của một lần khởi động (deployment identity, registry, service, secret, `IStartupCheck`, validator option của module qua
`IStartupValidator`) nằm trong **một** `GameServerStartupException`; Warning ghi một lần, category `GameServer.Startup`.
Overlay config game `config/env/<DeploymentEnvironment>`: beta và uat dùng chung `Staging`. Staging (beta, uat) **không** có cheat,
xác thực thử nghiệm, seed staff GM, GM OpenAPI, verifier/provider giả (chặt hơn OPS-09).

`GameServer:SecretKeys` mặc định (6 key): `Identity:GuestPepper`, `Couchbase:Password`, `Iap:Google:ServiceAccountPrivateKeyPem`,
`DataProtection:MasterKey`, `Gm:Oidc:ClientSecret`, `Identity:Providers:PlayGames:ClientSecret` (mới).

### Store (semantic)

| `GameServer:Store` | Dùng cho | Được phép |
| --- | --- | --- |
| `InMemory` (mặc định của class — test) | test, chạy thử bỏ đi | Development/Test |
| `File` | flavor `Local` | Development/Test |
| `Couchbase` | dev, beta, uat, live | mọi môi trường |

Store bị từ chối thì thư mục/kết nối không bị chạm tới (lỗi báo trước khi store mở).

### File store (semantic với công cụ dev)

`FileDocumentStore` (`GameServer.Platform.Testing`) = `InMemoryDocumentStore` (cùng semantic transaction; conformance chạy cả
trên file store, kèm kiểm mở lại) + ghi xuống đĩa trước khi thay đổi được nhìn thấy. Đăng ký: `AddFileDocumentStore(services,
configuration, environment)`; arch test: ngoài `GameServer.Platform.Testing` chỉ host và test được phụ thuộc.

| Mục | Giá trị |
| --- | --- |
| Cấu hình | `FileStore:Folder` (bắt buộc khi Store=File, tương đối tính từ content root; mẫu `../_database/dbbase`, gitignore `_database/`), `FileStore:ResetOnStart` (false) |
| Format | `store.json` = `{"format":"gameserver-file-store","version":1}`; document `g_<gameId>/<collection>/<tên>.json` = `{key, expiresAtUtc, content}`; tên: `[a-z0-9._-]` giữ nguyên, byte khác `%XX` (hex hoa), tên thiết bị Windows mã hoá ký tự đầu, tên mã hoá > 120 ký tự → `~` + 52 ký tự base32 của SHA-256 (khoá thật trong `key`) |
| Commit | một document: file tạm → rename; nhiều document: `journal.json.tmp` → rename `journal.json` (**commit point**) → áp → xoá journal. Khởi động: xoá `*.tmp`, replay journal. Tất cả hoặc không có gì (kiểm ở mọi bước crash + 20 lần kill process thật) |
| Độ bền | flush xuống OS, **không fsync**: chịu mọi kiểu process chết, không chịu mất điện/OS crash (file cắt dở → khởi động lỗi nêu tên) |
| Lỗi ghi | trước commit point → `TransactionFailedException` (`INTERNAL`), không ghi gì, readiness healthy (kể cả đĩa đầy); sau commit point → store faulted: mọi lần ghi sau lỗi, health `store` unhealthy, LogError; restart replay journal |
| Khoá | `.lock` mở `FileShare.None` suốt đời store; host thứ hai thoát 1 (≤ 5 s); OS nhả khi crash |
| Lỗi mở | lỗi I/O hoặc quyền khi mở (thư mục chỉ đọc, ACL, một file thay cho thư mục) → `[filestore] the data folder '<thư mục>' cannot be opened (<KiểuException>: …)`, exit 1 (T055) |
| Nạp | song song; file hỏng (JSON sai, key ≠ tên, `content` không phải object, `expiresAtUtc` sai, tên lạ) → khởi động lỗi nêu 20 file đầu + số còn lại; thư mục lạ không rỗng / format hoặc version khác → lỗi có hướng dẫn reset |
| Hết hạn | absolute UTC; không bao giờ trả về; file xoá ở lần khởi động sau (không sweeper) |
| Reset | `FileStore:ResetOnStart=true`: sau khi lấy khoá, xoá mọi thứ trừ `.lock`, tạo lại `store.json`; từ chối thư mục không phải store và không rỗng, gốc ổ đĩa, user profile, content root và thư mục cha, thư mục có `.git` |
| Giới hạn | không N1QL; không hot reload (sửa tay chỉ khi host dừng); Windows: đường dẫn document dài nhất ≤ 259 ký tự (kiểm khi khởi động); thao tác file đồng bộ trong lock của store (ngoại lệ constitution IV, chỉ Development) |
| Bảo mật | thư mục chứa key ring Data Protection **không mã hoá** (Development không có master key), phiên, credential guest |

**Hiệu năng (T020, `GAMESERVER_PERF=1`, Windows 11, SSD, 2026-10-30):** ổ D: nạp 10 000 document 546 ms (chỉ tiêu ≤ 10 s);
500 commit 6 document p50 22,8 ms, p95 44,8 ms (chỉ tiêu ≤ 50 ms), max 404 ms. Trong `%TEMP%` trên C: p95 82 ms (không đạt):
antivirus làm ghi + rename ~3,5 ms so với ~0,8 ms — giữ thư mục dữ liệu ngoài đường dẫn bị quét hoặc thêm exclusion.

### `IStartupCheck` (extension point, semantic với package store/host)

```csharp
public interface IStartupCheck { void Check(StartupCheckResult result); }   // GameServer.Platform
public sealed class StartupCheckResult { List<string> Errors; List<string> Warnings; }
```

Đăng ký bằng DI (nhiều implementation). `UseGameServer` và chế độ provisioning chạy mọi check: Errors gộp vào báo cáo khởi động
(exit 1), Warnings ghi một lần (provisioning: stderr `warning: …`). Check không được ném lỗi và không I/O ngoài đọc file local do
cấu hình chỉ định. Một check vẫn ném (T055): `GameServerStartupException` → các violation của nó vào báo cáo; exception khác →
một lỗi `[<TênKiểuCheck>] the startup check failed: <KiểuException>: <message>`; các check còn lại vẫn chạy, báo cáo vẫn đủ. Có
sẵn: `DeploymentIdentityCheck`, `CouchbaseSettingsCheck`, `FileStoreCheck`.

Binding: `IConfiguration.GetSettings<T>(section)` (`GameServer.Platform.Web.Hosting.ConfigurationBinding`, dùng cho
`GameServer` trong `AddGameServer` và cho `Couchbase` trong host mẫu) đổi lỗi binder (giá trị sai kiểu) thành
`GameServerStartupException` `[configuration] … at '<key>' …` → exit 1 với báo cáo, thay vì exception không xử lý.

### Couchbase (vận hành)

| Key | Mặc định | Giới hạn |
| --- | --- | --- |
| `Couchbase:ConnectionString` | — (bắt buộc) | `couchbase://` hoặc `couchbases://`, ≥ 1 host, seed list phân tách bằng dấu phẩy |
| `Couchbase:Username` | — (bắt buộc) | |
| `Couchbase:Password` | — | bắt buộc ở mọi môi trường (từ T055 cả Development/Test), chỉ từ môi trường |
| `Couchbase:BucketName` | — (bắt buộc) | |
| `Couchbase:KvTimeoutMs` | 2500 | 100 .. 1000 × min(`GameServer:Limits:PlayerTimeoutSeconds`, `GameServer:Limits:GmTimeoutSeconds`) |
| `Couchbase:QueryTimeoutMs` | 20000 | 100 .. 1000 × `GameServer:Limits:GmTimeoutSeconds` |
| `Couchbase:TrustCertificatePath` | null | file X.509 PEM/DER đọc được của **root CA**, tương đối tính từ content root |
| `Couchbase:Compression` | true | Snappy (`WithSnappyCompression()` của SDK 3.10) |
| `Couchbase:CompressionMinSizeBytes` | 32 | 0 .. 20 971 520 |
| `Couchbase:NumKvConnections` | 2 | 1 .. 32 |
| `Couchbase:MaxKvConnections` | 5 | `Couchbase:NumKvConnections` .. 64 |
| `Couchbase:UseGetMulti` | false | không đổi (mục 2) |
| `Couchbase:ProvisionOnStartup` | Development true, còn lại false | |
| `Couchbase:ProvisioningTimeoutSeconds` | 600 | 30 .. 7200 |

- **TLS (SEC-01):** Production + `couchbase://` → lỗi khởi động; Staging → Warning; Development/Test → không gì. CA của
  `Couchbase:TrustCertificatePath` là root tin cậy **duy nhất** cho KV và HTTP (`X509Chain` `CustomRootTrust` qua callback xác thực
  certificate của `TlsSettings`; `WithTrustedServerCertificates` của SDK vẫn tin trust store của OS trước nên không dùng), tên host
  vẫn được kiểm, không kiểm revocation (như mặc định SDK). File phải là root CA tự ký: chỉ intermediate → chuỗi `PartialChain` →
  từ chối (test đơn vị `Only_the_root_ca_anchors_the_chain_and_missing_intermediates_are_not_fetched`). Không tải certificate
  thiếu qua AIA trong handshake (`ChainPolicy.DisableCertificateDownloads = true`): node phải gửi intermediate.
- **Cổng phát hành TLS (bắt buộc trước mọi cutover Live):** cluster local không có TLS, nên hai test opt-in
  `CouchbaseSettingsIntegrationTests.Tls_trusts_the_configured_ca_and_refuses_another` và
  `CouchbaseProvisioningTests.Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls` phải
  chạy và pass trên một cluster TLS có CA riêng (`COUCHBASE_TLS_CONNSTR` + `COUCHBASE_CA_PATH`) trước khi live dùng `couchbases://`.
- **Nén:** trước slice 011 `Compression=true` không nén gì (thuật toán mặc định của SDK là null); nay body KV ≥ 32 byte được nén
  Snappy — cả bộ Couchbase chạy với nén bật. Vận hành: theo dõi CPU của data service và latency KV (p95/p99 của command) trên
  beta trước khi lên live; rollback không cần deploy code: `Couchbase:Compression=false` (biến `Couchbase__Compression=false`)
  rồi restart instance.
- **Key cũ** của `database_settings.json` (`Couchbase:ServerIp`, `Couchbase:OperationTimeOut`, `Couchbase:CompressThreshold`,
  `Couchbase:MoxiPort`, `Couchbase:BucketType`, `Couchbase:DbCache`, `dbbase:*`, `dbcache`) → Warning nêu key thay thế, không bao giờ
  lỗi. Bỏ hẳn: `dbcache` (owner), bucket memcached (deprecated, không transaction), `moxiPort` (Moxi không còn).
- Query timeout 20 s cũng giới hạn câu lệnh của provisioner → `CREATE INDEX` dùng `Couchbase:ProvisioningTimeoutSeconds` làm timeout
  câu lệnh (dưới tải, 20 s làm build index bị cắt phía client).

### Provisioning (semantic với pipeline — DAT-07)

```text
<host> [--provision] [--urls <url>] [--<Section>:<Key>=<value> …]
```

| Mục | Giá trị |
| --- | --- |
| Switch | `--provision`, không phân biệt hoa thường (bị bỏ khỏi args trước khi dựng cấu hình); không có → chạy server. `--provision=…`, `-provision`, `/provision[=…]` → exit 1 (`[provision] '<arg>' is not the provisioning switch …`, không in giá trị) |
| Cần | flavor hợp lệ, `GameServer:Store=Couchbase`, connection string, user, password, bucket; secret không nằm trong file JSON ngoài Development/Test (như chạy server). **Không** cần SSO GM, IAP, pepper, master key. Không chạy web server, job, sweeper |
| Thứ tự | scope → `txn_meta` → collection khác (thứ tự ordinal) → chờ collection trả lời KV → index (framework, module, tham chiếu purge) → chờ mọi index `online` (poll 2 s) → kiểm lại |
| Output (stdout) | `provision: game <id>, environment <env> (<DeploymentEnvironment>), bucket <b>, scope g_<id>`, mỗi mục `provision: <kind> <name> created` hoặc `present` (index mới: `created (online after N s)`), cuối `provision: done: X created, Y present`. Lỗi: `provision: FAILED <kind> <name>: <cause>` + `provision: stopped after N items; fix the cause and run again (idempotent)`; nguyên nhân = `<kiểu exception>[ (HTTP n)]: <dòng đầu message>`, không chứa credential |
| Exit code | 0 mọi thứ đã có; 1 cấu hình sai, secret trong file, switch viết sai hoặc store không phải Couchbase (`[provision] --provision needs GameServer:Store=Couchbase …`); 2 lỗi, timeout hoặc Ctrl+C |
| Lỗi tạm thời | cluster manager trả 503 khi bận (đo: cả `GetAllScopesAsync` lẫn tạo collection dưới tải) → liệt kê scope, tạo scope, tạo collection thử lại tối đa 8 lần, backoff 0,5 s gấp đôi, tối đa 5 s; lỗi khác không thử lại. Câu đọc `system:indexes` là read-only (SDK tự thử lại lỗi tạm thời). Index trạng thái `error` → exit 2 ngay (gợi ý `DROP INDEX`); chờ index `online` giới hạn bởi `Couchbase:ProvisioningTimeoutSeconds` |
| Idempotent | mục có sẵn báo `present`, không bao giờ sửa/xoá; chạy lại sau lỗi tiếp tục phần còn thiếu; không tạo bucket; index trùng tên khác định nghĩa vẫn `present`; index có sẵn ở trạng thái `deferred` → lỗi kèm gợi ý `BUILD INDEX` |
| Credential | cùng key `Couchbase:Username`/`Couchbase:Password`, pipeline đặt identity quản trị: `bucket_admin[b]` + `query_manage_index[b]` + `data_reader[b]` (bước chờ/kiểm đọc KV) + `query_system_catalog` — cấp bucket vì scope chưa có ở lần đầu. Đã kiểm bằng user hạn chế (T055, `CouchbaseProvisionerRolesTests`: scope mới exit 0, chạy lại toàn `present`); bỏ bất kỳ role nào trong bốn role → exit 2 (đã đo từng role). Runtime dùng identity data-only trên scope (`Data Reader`, `Data Writer`, `Query Select/Insert/Update/Delete`); role theo scope viết `[bucket:scope]` (7.6 từ chối `[bucket:scope:*]`). Data-only chạy `--provision` → exit 2 ở mục đầu (`FAILED scope …: CouchbaseException (HTTP 403)`, đã kiểm) |
| Thời gian | `CREATE INDEX` không `defer_build` chờ tới khi build xong (EE 7.6.12: 200 000 document 8,3 s); dưới tải của cả bộ test mỗi index của scope mới 23–88 s → một scope mới có thể vượt 600 s: exit 2, chạy lại tiếp tục |

**Kiểm schema khi khởi động (`Couchbase:ProvisionOnStartup` = false):** trước khi nghe cổng, mọi collection bắt buộc (kể cả
`txn_meta`) được đọc thử song song (`GetAsync("_schema_probe")`, 3 s) bằng identity runtime. Collection lạ trên SDK 3.10 + EE 7.6.12
**không** ném `CollectionNotFoundException`: là `UnambiguousTimeoutException` (sau `KvTimeout`) có retry reason `CollectionNotFound`
(scope lạ: `ScopeNotFound`) — cả hai dạng đều tính là thiếu; lỗi khác (không kết nối được, xác thực) giữ nguyên. Thiếu → exit 1
trước khi nghe cổng: `[couchbase] scope g_<id> in bucket <b> is missing collections <c1>, <c2>. Fix: run the provisioning command
(<host> --provision) with an administrative credential (DAT-07).` (`<host>` in nguyên văn). Index không được kiểm khi khởi động.

**Exit code của host (chạy server):** 0 dừng êm; 1 cấu hình sai hoặc bị từ chối trước traffic (validation kể cả giá trị sai kiểu,
file store khoá/hỏng/sai format/không đọc-ghi được, thiếu collection, switch `--provision` viết sai), stderr
`Game server startup validation failed:` + mỗi lỗi một dòng ` - …`; khác = lỗi runtime không xử lý. `RunGameServerAsync` trả code, không gọi `Environment.Exit` (`WebApplicationFactory<Program>` chạy cùng entry point).

### Chạy local (vận hành)

| Mục | Giá trị |
| --- | --- |
| Cổng mặc định | `http://localhost:5080` (`Urls` trong `appsettings.Local.json`, launch profile, client Unity mẫu, `smoke-build.ps1`) |
| Thứ tự URL | `--urls` > `Urls` của file flavor > `ASPNETCORE_URLS` (biến tiền tố host là nguồn thấp nhất: `applicationUrl` của launch profile **không** đổi được cổng) |
| Launch profile | `local` (mặc định; flavor Local) và `dev-couchbase` (flavor Dev, mật khẩu từ biến môi trường user `Couchbase__Password`); `dotnet run --launch-profile` dùng thư mục project làm working directory |
| `tools/local/run-local.ps1` / `.sh` | `-Port`/`--port` (5080), `-Reset`/`--reset` (`--FileStore:ResetOnStart=true`), `-Lan`/`--lan` (`0.0.0.0`, cảnh báo đỏ + địa chỉ LAN); build Debug mỗi lần, `dotnet run --no-build --launch-profile local` từ `samples/SampleHost`, readiness `http://127.0.0.1:<port>/health/ready` ≤ 60 s |
| Exit code script | 0 dừng bằng Ctrl+C sau khi host dừng êm; 2 tham số sai; 3 thiếu SDK của `global.json`; 4 cổng bận; 5 build lỗi; 6 host thoát trước khi sẵn sàng; 7 quá 60 s (in 20 dòng log cuối, dừng host); khác = exit code của host sau khi sẵn sàng. Không đường nào để lại host chạy |
| `start-hosts.ps1` | đặt `ASPNETCORE_ENVIRONMENT=Dev` cho mỗi host (khôi phục sau); tham số và cổng 5080+ giữ nguyên |

Nghiệm thu thủ công (T048, 2026-10-30): Windows PowerShell 5.1 và Git Bash đạt mọi mục đã chạy; **chưa chạy**: PowerShell 7,
macOS/Linux, IDE, điện thoại thật qua `-Lan`, Unity E2E trên 5080.

### Breaking (so với slice 010)

| Mục | Thay đổi | Cách chuyển |
| --- | --- | --- |
| `Couchbase:ConnectionString` | bỏ mặc định `couchbase://localhost` | đặt trong file flavor hoặc `Couchbase__ConnectionString` |
| Host `Program.cs` | trong `try … catch (GameServerStartupException)`: `GameServerCommandLine.Parse(args)` (ném khi switch viết sai) → `WebApplication.CreateBuilder(commandLine.HostArgs)` → … (section store đọc bằng `GetSettings<T>`) → `Build()`; sau đó `return await app.RunGameServerAsync(mode);` thay cho `UseGameServer()` + `Run()` | theo `samples/SampleHost/Program.cs`; `--provision` chỉ có khi dùng `RunGameServerAsync` |
| `Couchbase:Password` ở Development/Test | trước: không bắt buộc; nay thiếu → lỗi khởi động | đặt `Couchbase__Password` (biến môi trường user cho profile `dev-couchbase`) |
| Khoá IAP | trước: chỉ lỗi ở lần xác minh receipt đầu tiên; nay kiểm khi khởi động (PCR iap) | đặt đủ `Iap__*` (kể cả `Iap__Apple__RootCertificateBase64`) cho beta/uat/live |
| `GameServer:SecretKeys` | thêm `Identity:Providers:PlayGames:ClientSecret` | chuyển giá trị đó khỏi file cấu hình ngoài Development/Test |
| Thông báo store chỉ-dev | `in-memory store is configured` → `[deployment] the <InMemory/File> store is configured in '<env>'. …` | ai so khớp text cũ phải sửa |
| `TestHost` | `GameServer:EnvironmentId` mặc định `dev` → `test` (id chuẩn `dev` + Staging/Production/Test giờ là lỗi) | test cần `dev` phải đặt cả Development |
| `appsettings.json` của host mẫu | không còn deployment environment, environment id, store, seed staff; chạy không flavor → exit 1, không bao giờ Development | chọn flavor (`ASPNETCORE_ENVIRONMENT`) |
| Nén Couchbase | thực sự bật (Snappy) | theo dõi CPU data service + latency KV trên beta; rollback `Couchbase:Compression=false` |

### Không cung cấp

| Hạng mục | Ghi chú |
| --- | --- |
| `GameServer:Staging:AllowCheatModule` | điểm mở rộng hoãn (R13): nếu QA cần cheat trên uat — chỉ Staging, từ chối ở Production, chỉ đăng ký `CheatModule` sau permission GM có audit, không bao giờ xác thực thử nghiệm hay seed |
| Overlay config theo `EnvironmentId` | beta/uat dùng chung `config/env/Staging` (owner) |
| Kiểm index khi khởi động | cần quyền catalog; báo cáo provisioning là nguồn đúng |
| CI chạy integration Couchbase | CI-01 còn mở; chạy local với EE 7.6.12 trước merge |

## 18. Analytics: login và payment (slice 012 — ANA-01, JOB-02, LOG-01/03/05/06, OBS-01..03, TIME-01, PRIV-01)

Hướng dẫn cho data team và game team: `docs/guides/analytics.md`; vận hành (volume, shipper, tzdata, rollback):
`docs/guides/environments-and-local-run.md` mục 7. Đổi tên/kiểu/ý nghĩa một field, ngữ nghĩa thời gian, quy tắc dedupe,
tên file, key, metric hay health check dưới đây là breaking change (PCR-01, ARC-08).

### Event và envelope (semantic với data team)

| `event` | Sự kiện | Giao | Dedupe |
| --- | --- | --- | --- |
| `payment` | purchase được store xác minh và server cấp (`iap.purchase`) | **at-least-once**: outbox entry kind `analytics` commit cùng grant, inline relay + sweeper | `transactionID` |
| `login` | `source=login`: login cấp session mới (guest, provider, switch đã xác nhận); `source=resume`: request đầu tiên của player trong một ngày local trên một instance, xác thực bằng `PlayerBearer` | **best effort** (hàng đợi trong bộ nhớ) — mất được chấp nhận (JOB-02), xem dưới | `eventId` (hiếm khi cần) |

Không bao giờ ghi: login bị từ chối/thất bại, link identity, request mua bị replay (cùng request id), purchase bị từ chối
(`ALREADY_GRANTED`, `INVALID_RECEIPT`, sản phẩm lạ), xác minh store thất bại.

Mỗi record là **một** object JSON trên một dòng (UTF-8, không BOM, `\n` kết thúc), key theo thứ tự cố định:

| # | Field | Kiểu | Ý nghĩa |
| --- | --- | --- | --- |
| 1 | `event` | string | `^[a-z][a-z0-9_]{0,31}$` |
| 2 | `schemaVersion` | number | ≥ 1, theo từng event (v1 cho cả hai) |
| 3 | `eventId` | string | 32 hex thường. Payment: SHA-256 của `{entryId}\|{committedAtUtc}` (32 hex đầu) — giống nhau ở mọi bản sao; login: GUID ngẫu nhiên |
| 4 | `gameId` | string | key `GameServer:GameId` |
| 5 | `environmentId` | string | key `GameServer:EnvironmentId` (`local`, `dev`, `beta`, `uat`, `live`, …) — **lọc `live` khi tính doanh thu** |
| 6 | `userID` | string | id player nội bộ (thô); payment giao trễ sau khi tài khoản bị purge mang id ẩn danh `anon_…` |
| 7 | `{time}` | string | giờ local của deployment kèm offset của **thời điểm đó**: `yyyy-MM-ddTHH:mm:ss.fff±hh:mm` |
| 8 | `{time}Utc` | string | cùng thời điểm, `yyyy-MM-ddTHH:mm:ss.fffZ` (cắt tới millisecond **trước** khi đổi múi giờ) |
| 9 | `timeZone` | string | tên IANA của deployment (`UTC` chỉ khi mặc định ở Development/Test) |
| 10 | `utcOffset` | string | `±hh:mm` tại thời điểm đó (`+00:00` cho UTC, không bao giờ `Z`) |
| 11 | `receiptId` | string | chỉ event qua outbox (payment) |
| 12.. | field theo type | — | null không ghi; field trùng tên envelope bị bỏ (log lỗi một lần mỗi type) |

`{time}` là `chargeTime` (payment) và `activeTime` (login). `log_month`/`log_date` = 7/10 ký tự đầu của `{time}` (lịch local
của deployment, theo yêu cầu owner); muốn lịch UTC dùng `{time}Utc`. Một deployment = một múi giờ (không theo player).

### Schema v1

`payment` (owner: `userID`, `transactionID`, `chargeTime`):

| Field | Kiểu | Bắt buộc | Ý nghĩa |
| --- | --- | --- | --- |
| `chargeTime`, `chargeTimeUtc` | string | có | **giờ cấp của server** = `committedAtUtc` của receipt (giờ server của lần chạy transaction đã commit, ≤ 10 s trước commit), không phải giờ store. Xác minh 23:59:59 và cấp 00:00:01 thuộc ngày/tháng sau |
| `receiptId` | string | có | receipt của command mua |
| `transactionID` | string | có | `apple:{transactionId}` · `google:{orderId}` · `google:h:{32 hex}` = SHA-256 của purchase token khi Google không trả `orderId` (license tester, một số promo). **Không bao giờ là purchase token** (LOG-05) |
| `store` | string | có | `google`, `apple` |
| `productId` | string | có | ≤ 128 ký tự; giá và tiền tệ **không** có trong record |
| `orderId` | string | không | order id của store khi có |
| `isTest` | bool | có | purchase test/sandbox của store hoặc verifier giả; luôn `false` trên `live` (purchase test bị từ chối ở Production) |
| `purchaseCategory` | string | không | Google `purchaseType`: `test`, `promo` (grant hợp lệ **không có doanh thu**), `rewarded` |
| `storePurchaseTimeUtc` | string | không | giờ mua store báo, UTC |

`login` (owner: `userID`, `activeTime`): `activeTime`/`activeTimeUtc` (giờ login thành công hoặc request được xác thực),
`source` (`login` | `resume`), `provider` (chỉ `source=login`: `guest`, `pgs`, `gamecenter` hoặc provider của game),
`isNewUser` (login tạo player; luôn `false` với `resume`).

Phiên bản: `schemaVersion` theo từng event. Thêm field tuỳ chọn → giữ version (reader bỏ qua field lạ). Bỏ/đổi tên/đổi kiểu,
định dạng hoặc ý nghĩa field → version mới, công bố ở PCR trước release; giai đoạn chuyển tiếp hai version có thể cùng
xuất hiện (lọc theo `schemaVersion`).

### File (semantic với data team và vận hành)

- Tên `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl` trong thư mục `GameServer:Analytics:Directory`; `gameId`, `instanceId`
  làm sạch như tên file log (chữ, số, `-`, `_`, `.`; khác → `_`); parse từ phải (`event` không có `-`, instance id có thể có).
- Ngày = ngày **UTC** của đồng hồ server (`IClock`) **lúc ghi**, đơn điệu trong một process (đồng hồ lùi qua 00:00 UTC không
  mở lại ngày đã đóng). File của ngày UTC trước hôm nay là hoàn chỉnh, không bao giờ được ghi thêm.
- Append; ghi và flush mỗi batch: batch có record payment (giao outbox, ack receipt) flush **xuống đĩa** (sống qua crash node
  / mất điện; một `fsync` nằm trên đường ack inline của purchase — SC-004 sửa 2026-10-31), batch chỉ có login flush xuống OS
  (sống qua process chết, không qua mất điện — như file store 011). Khi mở lại file
  không rỗng mà byte cuối không phải `\n`, sink ghi `\n` trước: dòng bị cắt do crash thành **một dòng không parse được**,
  có thể nằm giữa file. **Reader bỏ qua mọi dòng không phải JSON hợp lệ** (SD2); dòng đó chưa từng được ack (payment trong
  đó sẽ được giao lại ở dòng sau; login trong đó mất).
- **Không suy ra ngày nghiệp vụ từ tên file**: payment có thể được instance khác ghi muộn, vào ngày UTC sau `chargeTime`.
- Một writer cho mỗi (game, instance, thư mục): lock `.{gameId}-{instanceId}.lock` (`FileShare.None`) lấy trong
  `StartingAsync` (trước khi server nghe — [VERIFY] V3 đã kiểm trên Windows và Linux); bận → exit 1, thông báo nêu
  `Observability:InstanceId`. Lock không đáng tin trên NFS/SMB: chỉ volume local/block.
- **Kubernetes**: Deployment + instance id mặc định `HOSTNAME` → tên pod đổi mỗi lần restart → file mới mỗi lần, và
  `RetainLocalDays` chỉ dọn file của **chính** instance id hiện tại (file của pod cũ ở lại). Dùng StatefulSet (tên pod ổn
  định) hoặc đặt `Observability:InstanceId` rõ ràng. `Directory` mặc định `analytics` nằm trong filesystem của container (mất
  khi container bị thay): mount volume bền và trỏ `GameServer:Analytics:Directory` vào đó lúc deploy (ví dụ
  `GameServer__Analytics__Directory=/var/lib/gameserver/analytics`); flavor mẫu không đặt đường dẫn tuyệt đối.
- Ghi file là I/O đồng bộ dưới một lock, không theo cancellation: đĩa treo (I/O stall) chặn dispatcher và **ngân sách drain
  lúc dừng không giới hạn được** trường hợp này (process chờ tới khi orchestrator kill). Payment vẫn an toàn (receipt chờ
  sweeper); login còn trong hàng đợi mất.
- Server không xoá file analytics trừ khi đặt `GameServer:Analytics:RetainLocalDays` (chỉ file của chính instance cũ hơn N ngày UTC).

### Cấu hình (vận hành; section `GameServer:Analytics`, kiểu `AnalyticsOptions`, `ValidateOnStart`)

| Key | Mặc định | Hợp lệ | Ghi chú |
| --- | --- | --- | --- |
| `GameServer:Analytics:Enabled` | `true` | — | **phải giống nhau trên mọi instance của deployment** (instance tắt sẽ ack entry payment của instance bật mà không ghi — FR-039) |
| `GameServer:Analytics:TimeZone` | (không) | IANA, `HasIanaId`; Windows id bị từ chối ([VERIFY] V1) | **bắt buộc** ở Staging/Production khi analytics active; Development/Test mặc định `UTC` + một warning |
| `GameServer:Analytics:Directory` | `analytics` | không rỗng; tương đối → content root | phải tạo được và ghi được (lỗi → exit 1 nêu key và đường dẫn) |
| `GameServer:Analytics:QueueCapacity` | `10000` | 100..1 000 000 | hàng đợi chung (payment và login) |
| `GameServer:Analytics:DeliveryTimeoutMs` | `500` | 50..5 000 | chặn trên thời gian chờ inline của một payment (FR-013) |
| `GameServer:Analytics:ResumeTrackerCapacity` | `200000` | 1 000..10 000 000 | player nhớ cho quy tắc resume mỗi ngày local mỗi instance |
| `GameServer:Analytics:LowDiskBytes` | `1073741824` | ≥ 0 | ngưỡng Degraded |
| `GameServer:Analytics:ErrorWindowSeconds` | `300` | 10..86 400 | Degraded sau lỗi sink |
| `GameServer:Analytics:RetainLocalDays` | (không = không xoá) | ≥ 1 | |

"Active" = `Enabled` **và** có ít nhất một `IAnalyticsSink` đăng ký (SD5). Không key nào là secret (không thêm vào
`GameServer:SecretKeys`). `--provision` không đọc setting analytics, không tạo thư mục/lock/file. Flavor mẫu: cả năm flavor
`Enabled=true`, `TimeZone=Asia/Ho_Chi_Minh`; file chung không có key analytics. Instance id không phải key analytics:
`Observability:InstanceId` → `HOSTNAME` → tên máy (như log, LOG-03).

### Giao và chặn trên (semantic)

- **Payment**: handler `iap.purchase` thêm entry kind `analytics` (index = số grant) trong transaction của grant; payload
  `{event, schemaVersion, timeField, fields}` **không chứa player id** (userID, giờ, receiptId, eventId lấy từ receipt lúc giao →
  purge 010 ẩn danh hoá cả record giao trễ). Consumer chỉ ack khi **mọi** sink đã ghi và flush dòng; inactive → ack, không ghi.
  Hết hàng đợi / quá `DeliveryTimeoutMs` / sink lỗi / đang dừng / payload hỏng → ném → relay ghi một attempt, backoff
  30 s → 1 h, dead letter sau `GameServer:Outbox:MaxRelayAttempts` (10, ≈ 3 h). Bản giao inline đã hết giờ nhưng dispatcher
  đã lấy vẫn có thể được ghi → bản sao trùng mọi field định danh, dedupe loại.
- **Chặn trên inline (FR-013, SD12)**: response mua chờ record payment tối đa `DeliveryTimeoutMs` = **500 ms** (ngoài relay
  outbox sẵn có); bình thường < 1 ms. Hết giờ: mua vẫn thành công, sweeper giao record sau.
- **Login/resume (JOB-02, mất được chấp nhận)**: `TryEmit` không bao giờ chờ hay ném. Mất khi: hàng đợi đầy, sink lỗi, dừng
  quá ngân sách drain, crash (record còn trong hàng đợi). Tín hiệu: `analytics_events_dropped_total{event="login"}`. Lý do
  chấp nhận: UL tính từ payment; active days đếm ngày local phân biệt, một login/resume khác trong ngày bù lại.
- Dừng êm: readiness 503 → Kestrel drain → sweeper dừng → pipeline drain hàng đợi tới hết `GameServer:Hosting:ShutdownTimeoutSeconds`
  (phần còn lại đếm `dropped{reason=shutdown}`, delivery còn chờ thất bại `stopped`) → sink đóng file và nhả lock.

### Kind outbox `analytics` được giữ chỗ (semantic với module)

Framework đăng ký consumer kind `analytics` trong `AddGameServer` (luôn luôn). Game đăng ký projection hoặc `IOutboxConsumer`
cùng kind → lỗi khởi động `[outbox] two consumers handle kind 'analytics'`.

### Điểm mở rộng (extension point, **preview**)

| Điểm | Cách dùng |
| --- | --- |
| Sink mới (P1: Couchbase, Kafka, HTTP) | đăng ký thêm `IAnalyticsSink` (`GameServer.Platform.Analytics`); nhận đúng các byte của file sink; payment chỉ ack khi **mọi** sink thành công |
| Event at-least-once từ command | `ctx.AddAnalyticsEvent(new AnalyticsEventType(name, version, timeField), fields)` (Core, `GameServer.Analytics`); file riêng theo `name`. **Preview**: field của event game chưa phải hợp đồng công bố |
| Event best effort từ adapter | `IAnalyticsEmitter.TryEmit(type, userId, occurredAtUtc, fields)` |
| Host | `gameServer.AddAnalyticsFiles()` (`GameServer.Platform.Observability.Analytics`) — host mẫu gọi sau `AddObservability()`; idempotent (gọi hai lần = một lần). `Enabled=true` mà không có sink nào: Staging/Production **lỗi khởi động** (exit 1, nêu cách sửa `AddAnalyticsFiles()` hoặc `GameServer:Analytics:Enabled=false`); Development/Test → inactive + một warning, entry payment được ack mà không ghi |

### Metric và health (vận hành)

Meter `GameServer.Analytics` (một instance mỗi host): `analytics_events_written_total{event,sink}`,
`analytics_events_dropped_total{event,reason=queue_full|sink_error|shutdown}`, `analytics_sink_errors_total{sink}`,
`analytics_delivery_failures_total{reason=queue_full|timeout|sink_error|stopped|malformed}`, `analytics_queue_entries`,
`analytics_dir_free_bytes`; meter `GameServer.Identity`: `login_resume_tracker_resets_total`. Payment không bao giờ bị
"dropped" ở đây (nằm trong outbox, đếm ở `analytics_delivery_failures_total` và `outbox_relay_inline_failures_total{kind="analytics"}`).
Tag không bao giờ chứa player id, event id hay transaction id (OBS-02). Inactive → không metric nào.

Readiness check `analytics` (tag `ready`, đăng ký bởi `AddAnalyticsFiles()`): **Degraded** khi thư mục còn ít hơn
`GameServer:Analytics:LowDiskBytes` hoặc có lỗi sink trong `GameServer:Analytics:ErrorWindowSeconds` (đồng hồ server);
**không bao giờ Unhealthy**; trạng thái theo host (không static như `LogSinkMonitor`). `/health/ready` công khai chỉ trả chữ
trạng thái; chi tiết GM (`ops.read`): `sinkErrors`, `dropped`, `queue`, `freeBytes`, `lastError` (≤ 500 ký tự).

### Quyền riêng tư (owner Q11, PRIV-01)

`userID` là id thô (không băm) — dữ liệu cá nhân theo privacy policy của game. Dữ liệu đã ship giữ **25 tháng** (chính sách
kho dữ liệu, server không cưỡng chế). Xoá tài khoản **không** ghi lại file đã ghi/đã ship; payment giao sau purge mang id ẩn
danh. Record không bao giờ chứa session token, mật khẩu, guest secret, token/auth code provider, subject id provider, purchase
token Google, payload/JWS receipt, IP client, body request (kiểm bằng test quét, SC-006).

### Hiệu năng (đo 2026-10-31, Windows 11, .NET 10.0.403)

| Đo | Kết quả |
| --- | --- |
| p95 trong process (`TestHost`, 400 mẫu, file sink trên ổ dữ liệu) — tắt / bật | trước review T066 (flush xuống OS): login 0,16 / 0,15 ms; purchase 0,34 / 0,45 ms → chênh login ≈ 0, purchase +0,11 ms (SC-004: ≤ 1 ms / ≤ 5 ms). **Sau review T066 (batch có payment flush xuống đĩa)**: login 0,11 / 0,32 ms; purchase 0,17 / 58,5 ms → chênh login +0,21 ms, purchase +58 ms, đạt **SC-004 sửa 2026-10-31** (login ≤ 1 ms; purchase ≤ 100 ms trên máy dev, luôn < `DeliveryTimeoutMs` 500 ms). fsync thô một dòng 300 byte: D: (Windows) p50 40 ms / p95 53 ms; Linux trong Docker Desktop p50 92 ms; SSD/block volume production dự kiến nhanh hơn |
| Burst 180 login/s × 60 s, hàng đợi mặc định | 10 800 login, hàng đợi tối đa 23, **0 dropped** (SC-008) |
| Sink bị chặn, 200 000 emit | hàng đợi giữ 10 000, 189 986 dropped (đã đếm), heap tăng ~2 MiB |
| Load tool `burst --purchase-every 5` trên flavor Local (file store), 10/5 rps × 2 | p95 bị file store chi phối (50–1 000 ms, dao động lớn giữa các lần chạy); chênh bật/tắt nằm trong nhiễu, không quy được cho analytics; ở 50/20 rps × 3 file store bão hoà (p95 vài giây) cả khi tắt |

### Breaking (so với slice 011)

| Mục | Thay đổi | Ai bị ảnh hưởng |
| --- | --- | --- |
| Kind outbox `analytics` | được giữ chỗ (consumer của framework) | game có consumer/projection tên `analytics` → lỗi khởi động |
| Receipt `iap.purchase` | thêm một entry outbox (≤ 512 B) | ngân sách 4 KiB của receipt; build trước 012 relay receipt này sẽ dead-letter (rollback) |
| Lỗi khởi động mới khi analytics active | thiếu zone ở Staging/Production, zone lạ/Windows id, thư mục không ghi được, trùng instance id trên một thư mục | deployment gọi `AddAnalyticsFiles()` phải đặt `GameServer:Analytics:TimeZone` và instance id riêng |
| Lỗi khởi động mới khi enabled mà không có sink (review T066) | Staging/Production: `GameServer:Analytics:Enabled=true` (mặc định) mà không có `IAnalyticsSink` nào → exit 1 | host không gọi `AddAnalyticsFiles()`: gọi nó hoặc đặt `GameServer:Analytics:Enabled=false` |
| Lỗi khởi động của consumer outbox (review T066) | consumer đọc options có `.Validate()` (không `ValidateOnStart`) bị sai → lỗi khởi động thay vì mọi command trả `INTERNAL` | game có consumer như vậy |
| `IAnalyticsSink` (preview) | `AnalyticsRecord.Guaranteed` (init-only, record giao outbox) | sink tự viết nên làm batch có record này bền trước khi trả về |
| Claim key `extref` dài (review T066) | key > 246 byte UTF-8 → `extrefh::{sourceType}::{sha256 hex}` (PCR economy) | key cũ ≤ 246 byte không đổi; trước đây key dài hơn lỗi `INTERNAL` trên Couchbase |
| `VerifiedPurchase` | thêm init-only `IsTest`, `PurchasedAt`, `PurchaseCategory` | tương thích source/binary; equality của record gồm chúng; verifier tự viết không đặt `IsTest` → báo purchase thật |
| Verifier giả | mọi purchase là test; quy ước mới `noorder:{ref}` | test/công cụ dùng verifier giả |
| Hợp đồng ngữ nghĩa mới | JSONL schema v1, tên file, thời gian, dedupe | pipeline dữ liệu |
| Hạ tầng test | `TestHost` đăng ký `InMemoryAnalyticsSink` (`TestHost.Analytics`) và `GameServer:Analytics:TimeZone` = `UTC`; `WebApplicationFactory<Program>` của host mẫu phải đặt `GameServer:Analytics:Directory` (+ instance id) riêng hoặc tắt analytics | test của game dựng trên host mẫu |
| API bị cấm | `TimeZoneInfo.Local`, `DateTime.Today` trong code framework/module | tác giả module (lỗi biên dịch) |
