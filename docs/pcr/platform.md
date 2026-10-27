# PCR — platform

Published Contract Register của nền tảng (PCR-01). Thay đổi mục **semantic** là breaking change (ARC-08);
thay đổi mục **vận hành** chỉ cần ghi changelog. Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4.

Phiên bản: 0.5.0 (slice 001 + sửa sau review + slice 002 identity + slice 004 GM + slice 005 observability, 2026-10-26).

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
- Mọi ghi `pa_*` và `rcpt::*` chỉ qua `ICommandRunner` / `IPlayerAggregateWriter` (arch test). Ngoại lệ: các cờ
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
`Couchbase:Password`, `Iap:Google:ServiceAccountPrivateKeyPem`) không được có giá trị trong **bất kỳ** file cấu hình nào (kể cả
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
bootstrap staff chờ nó trước khi đọc store, bất kể thứ tự hosted service.
