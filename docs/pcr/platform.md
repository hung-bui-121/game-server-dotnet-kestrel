# PCR — platform

Published Contract Register của nền tảng (PCR-01). Thay đổi mục **semantic** là breaking change (ARC-08);
thay đổi mục **vận hành** chỉ cần ghi changelog. Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4.

Phiên bản: 0.7.0 (slice 001 + sửa sau review + slice 002 identity + slice 004 GM + slice 005 observability, 2026-10-26;
slice 010 lease job và xoá tài khoản, 2026-10-30 — mục 16; slice 011 môi trường theo flavor, file store, setting Couchbase,
`--provision`, 2026-10-30 — mục 17; slice 012 analytics login và payment, 2026-10-31 — mục 18; slice 013 consumer
readiness, 2026-11-03 — mục 19; slice 014 wire contract, 2026-11-03 — mục 20 và `RouteName` ở mục 13; slice 015 game config,
2026-11-04 — mục 21, nén `GetConfig` ở mục 1 và 9, metric/readiness ở mục 13, `IStartupCheck` ở mục 17). Đúng với framework
v0.7.0 (chưa phát hành / unreleased).

## 1. Limits và timeout theo nhóm endpoint (vận hành — API-06, PERF-04)

| Tham số | Player | GM | Cấu hình |
| --- | --- | --- | --- |
| Body tối đa | 64 KiB | 1 MiB | `GameServer:Limits:PlayerBodyBytes` / `GmBodyBytes` |
| Timeout request | 10 s | 30 s | `PlayerTimeoutSeconds` / `GmTimeoutSeconds` → 503 `REQUEST_TIMEOUT` |
| Concurrency limiter (per instance) | `command` 200 | `gm` 50 | queue = 2× permit, oldest-first → 429 `RATE_LIMITED` + `Retry-After` |
| Per-player ordering (CMD-06) | concurrency 1, queue 2 | — | `GameServer:Commands:PerPlayerQueueLimit` |
| Protobuf decode | recursion ≤ 32, size = body | — | — |
| Kestrel `MaxRequestBodySize` | 1 MiB toàn cục, hạ per-endpoint theo profile | | |
| Bundle `GetConfig` (slice 015) | 1 MiB **chưa nén** (response lớn nhất có thể) | — | `GameServer:Limits:ConfigBundleBytes`; kiểm khi khởi động (mục 21), 413 khi request vẫn giữ |
| Nén `GetConfig` (slice 015) | ngưỡng 1024 B; gzip level 6, Brotli quality 4; encoding không nhỏ hơn ở lần thử lúc start thì không dùng; tối đa max(1, số CPU / 4) response nén cùng lúc **mỗi host**, còn lại gửi không nén (`config_compression_skipped_total{reason}`) | — | `GameServer:ConfigCompression` (mặc định `true`) |

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
| Commit kèm mã nghiệp vụ | `CommandOutcome.Commit(result, code)`: `code` phải có trong catalog (core hoặc ErrorCodeSet của một module đã đăng ký, đúng instance đã khai) và có HTTP 200; state + receipt được ghi, `resultCode = code`, HTTP 200; replay trả lại đúng mã. Mã ngoài catalog, mã trùng tên nhưng khác mã đã khai, hoặc mã có HTTP ≠ 200 = lỗi lập trình: 500 `INTERNAL`, không ghi gì (API-11). Receipt cũ có mã ngoài catalog: replay trả `OK` + log Warning |
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

Dải đang dùng và dải giữ chỗ (slice 013, FG-011, FR-060; hằng số `GameServer.Errors.ErrorCodeBands`):

| Dải | Chủ |
| --- | --- |
| 0–999 | core |
| 10000–10099 | `sample.counter` |
| 11000–11099 | `economy` |
| 12000–12099 | `iap` |
| 13000–13099 | `gm` |
| 14000–14099 | `identity` |
| 40000–40099 | `sample.game` |
| **50000–89999** | **module của game (consumer)** — `ErrorCodeBands.GameStart`/`GameEnd`; game tự chia dải con (game đầu tiên: 50000–50999) |
| 90000–90099 | template `template.module` (ví dụ của tài liệu) |
| 90000–99999 | lãnh thổ framework — `ErrorCodeBands.FrameworkReservedStart`/`FrameworkReservedEnd` |

Module của framework (core, built-in, sample, template) không bao giờ khai dải hay mã trong dải game:
`ErrorCodeBandTests` (Architecture.Tests) fail và nêu module, dải hoặc mã. Framework không kiểm dải của game lúc chạy
(game *được* dùng dải đó); luật trùng/chồng dải ở trên vẫn áp dụng cho mọi module.
Player nhận `ErrorEnvelope` (Protobuf, kể cả lỗi middleware); GM nhận Problem Details JSON với `code`,
`codeNumber`, `requestId`, `retryable`, `retryAfterMs`. Cả hai có `details` (map chuỗi an toàn, ví dụ `until` của
`ACCOUNT_SUSPENDED`; field 9 của `ErrorEnvelope`, thêm ở 0.3.0, không breaking).

`ErrorCodeSet` có phần tử null (field `ErrorCode` khai dưới set liệt kê nó) → `ArgumentException` nêu cách sửa ngay khi
khởi tạo set (trước đây: `NullReferenceException` trong startup validation).

**Breaking (sau slice 012):** `CommandOutcome.Commit(result, code)` với mã ngoài catalog, mã trùng tên nhưng khác mã đã
khai, hoặc mã có HTTP ≠ 200 trước đây commit (client nhận `OK` hoặc mã sai); giờ trả 500 `INTERNAL` và không ghi gì. Kiểm
handler của game trước khi nâng cấp. Route command mà operation chưa đăng ký, hoặc sai profile/permission so với command,
giờ làm startup fail (mục 12).

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
overlay), `not_modified` khi `config_version` của request bằng **một trong hai** version hiện tại (client hoặc server, so
ordinal; SDK trước 0.7.0 giữ version server); response có thêm field additive `client_config_version` (5, slice 015). Bundle
chưa nén > `GameServer:Limits:ConfigBundleBytes` (1 MiB) → startup fail (mục 21); 413 khi request vẫn giữ. Đây là route duy
nhất được nén (mục 21).

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
- Runner: command profile `Player` chỉ cho actor player, `Gm` chỉ cho actor GM có permission của command, `Dev` chỉ khi
  development tools được phép (local, dev, test — `GameServerOptions.AllowsDevProfile`) và khi đó cho mọi actor → sai → 403
  `FORBIDDEN`. Từ chối Dev xảy ra trước limiter, đọc receipt, guard và transaction, ở cả `ExecuteAsync` lẫn
  `TryReplayAsync` (receipt Dev cũ không được replay; metric `command_results_total{code=FORBIDDEN}` đếm cả hai đường).
  Command `Gm` phải commit với đúng một audit; command khác không được có audit (SEC-14).
- **Profile Dev (slice 013, FG-010) — chặt hơn OPS-09:** Dev chỉ khi development tools được phép (local, dev, test),
  không bao giờ ở Staging (beta, uat), Production (live) hay deployment environment nào khác. Ba lớp độc lập cùng đọc
  `GameServerOptions.AllowsDevProfile` (= `AllowsDevelopmentTools`, không có setting): (1) startup — mỗi endpoint Dev
  (`[endpoint POST /api/v1/cheat/grant] uses profile Dev in deployment environment '…' (environment id '…'). Fix: …`) và
  mỗi command Dev đã đăng ký, kể cả module `ServiceOnly()`, `Hosting:MapPlayerApi=false`, command không route và
  `--provision` (`[module economy.cheat] command 'cheat.grant' has profile Dev in deployment environment '…' …`) là một dòng
  của báo cáo khởi động (exit 1); (2) pipeline — `ProfileEnforcementMiddleware` trả 403 `FORBIDDEN` ngay sau khi tìm
  profile, trước mọi scheme xác thực (không chạm session, không event resume), CSRF, cache header và body limit, ghi một
  Warning `Dev endpoint {Method} {Route} refused in deployment environment {DeploymentEnvironment} ({EnvironmentId}).` (chỉ
  route template, không header/token); switch profile tường minh, giá trị profile lạ → 403; (3) runner như trên.
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
- Startup validator (route command — `MapPlayerCommand`/`MapDevCommand`/`MapGmCommand` gắn `CommandOperationMetadata`):
  operation không module nào đăng ký → startup fail (ARC-07); profile của route ≠ profile của command → startup fail
  (SEC-08); route GM có permission ≠ `CommandDescriptor.Permission` → startup fail (SEC-07). Command đã đăng ký mà không có
  route thì không bị báo (host chỉ GM, module `ServiceOnly`, code game gọi `ICommandRunner` trực tiếp).

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
| Queue | async, `BufferSize` 10 000, không chặn khi đầy → bỏ và đếm `log_dropped_total{sink="file"}` (cộng dồn cả host đã dừng; console sink đếm riêng `sink="console"`, cùng tag cho `log_queue_entries`; health `log`: `dropped` = file, `consoleDropped` = console) |
| Mức theo nguồn | `Logging:LevelOverrides` (prefix SourceContext → level, key không phân biệt hoa thường), cấu hình ghi đè lên mặc định `Microsoft.AspNetCore`, `Couchbase.Client.Transactions`, `ExecuteApplicationLambda` = Warning — phần transaction của Couchbase SDK ghi ~6 dòng Information mỗi transaction (đo 2026-10-27); log bootstrap/topology của SDK giữ nguyên. Level sai → startup fail. Mức thấp hơn `Logging:LogLevel` của Microsoft vẫn bị filter đó cắt trước |
| Provider khác | Serilog thay `ILoggerFactory` và **không** ghi sang provider khác (console mặc định chờ khi queue đầy → có thể chặn request) |
| Thuộc tính | `InstanceId`, `GameId`, `EnvironmentId`, `RequestId` (scope), `SourceContext`; trace/span id |
| Che dữ liệu | giá trị thuộc tính (kể cả lồng trong object destructure `{@x}`) có tên chứa token/password/passwd/pwd/secret/pepper/authorization/bearer/jwt/cookie/privatekey/signingkey/apikey/connectionstring/receiptpayload/signeddata/credential → `***`. **Không** được che: object log bằng `{x}` (không `@`, bị `ToString()`), nội dung exception (`@x`), scope dạng chuỗi — module không được đưa secret vào các chỗ đó |
| Lỗi sink | `SelfLog` (process-wide; thay handler SelfLog khác) → `log_sink_errors_total` (thông báo "queue đầy" không tính — đã đếm ở drop), lỗi gần nhất trong chi tiết GM; `log_dir_free_bytes` đo **mount chứa thư mục log** (root dài nhất khớp); health `log` Degraded khi lỗi trong `SinkErrorWindowSeconds` (300) hoặc đĩa < `LowDiskBytes` (1 GiB) |

**Request event (semantic với vận hành và code consumer — slice 013, FG-025, FR-080..FR-089):** đổi tên/xoá một thuộc tính,
category, event id, result code cố định hoặc setting dưới đây là breaking (PCR-01).

- **Một event mỗi HTTP request** host trả lời hoặc client huỷ, trên mọi route (player, GM, public, Dev; command, query, login,
  snapshot, config; 404/405 và mọi lỗi), ghi khi pipeline framework xong. `RequestLogMiddleware` là middleware **ngoài cùng**
  của `UseGameServer` (trước forwarded headers và `ExceptionMiddleware`), nên đo cả pipeline và thấy status cuối. **Không** ghi
  cho `/health/live`, `/health/ready` (luôn loại) và path trong `GameServer:RequestLog:ExcludedPaths` (prefix theo segment,
  không phân biệt hoa thường); không ghi khi request log tắt cho deployment.
- Category `GameServer.Requests`, event id `1` / `Request`, template (`@mt` trong file) `{HttpMethod} {Route} -> {Status} {ResultCode} in {DurationMs} ms`
  (message đã format không chứa actor id).

| Thuộc tính | Kiểu | Giá trị |
| --- | --- | --- |
| `HttpMethod` | string | `GET`, `POST`, … |
| `Route` | string | template của endpoint (`/api/v1/player/receipts/{receiptId}`); không khớp endpoint (404, 405, scanner): chỉ segment đầu của path rồi `/...` (`/api/v1/nope` → `/api/...`, gốc → `/`; phần còn lại do client đặt, có thể chứa id/token), đã làm sạch (ký tự điều khiển → `?`, cắt 120 ký tự + `...`), không bao giờ có query string |
| `RouteName` | string? | tên route trong catalog (slice 014, mục 20: `battle.start`, `platform.config`): từ `RouteDeclarationMetadata` của endpoint, không có thì `WireCatalog.FindRouteName(method, template)`; null khi không khớp endpoint. Đứng ngay sau `Route`; không có trong dòng console; các thuộc tính của slice 013 không đổi |
| `Profile` | string? | `Public`, `Player`, `Gm`, `Dev`; null khi không khớp endpoint |
| `Operation` | string? | operation của command (`counter.increment`), cả khi command bị từ chối trước runner; null cho route không phải command |
| `ActorType` | string? | `player`, `gm`; null khi chưa xác thực |
| `ActorId` | string? | player id hoặc staff id từ principal đã xác thực (không bao giờ từ header/body/query), làm sạch và cắt 64 ký tự (header test-auth thành claim nên cũng bị làm sạch) |
| `RequestId` | string | correlation id (`X-Request-Id`: UUID của client hoặc do server sinh) |
| `TraceId` | string? | W3C trace id (32 hex) của activity request; null khi không có activity |
| `Status` | int? | HTTP status đã gửi; null khi client huỷ |
| `ResultCode` | string | code client nhận được (API-11): `OK` khi không có code; business code của command (commit có code hoặc reject ở HTTP 200); code của `ErrorEnvelope`/Problem Details; `ABORTED` khi client đi trước khi response bắt đầu (khi đó `Status` null, event không bao giờ khẳng định commit nó không thấy); `INTERNAL` cho lỗi không xử lý; `HTTP_{status}` cho response non-2xx viết ngoài framework mà không có code |
| `Replayed` | bool | command trả lời từ receipt |
| `NotModified` | bool | config get trả `not_modified` |
| `Aborted` | bool | client đi trước khi response bắt đầu |
| `DurationMs` | double | thời gian cả pipeline framework, làm tròn 0,1 ms |

  Cộng enrichment sẵn có khi dùng `AddObservability()`: `GameId`, `EnvironmentId`, `InstanceId`, `SourceContext`. Với command
  route đã tới runner, event và `command_results_total{code}` cùng một code (FR-083); request bị từ chối trước runner có event,
  không có metric.
- **Level**: Information khi `Status` < 400 (kể cả business code ở HTTP 200 — reject là gameplay bình thường), Warning cho 4xx
  và abort, Error cho 5xx. Lọc theo deployment bằng `Observability:Logging:LevelOverrides:GameServer.Requests` (`Warning` = chỉ
  4xx/5xx/abort, `Fatal` = coi như tắt).
- **Quyền riêng tư (LOG-05)**: không body, query string, giá trị header, cookie, token, password, guest secret, provider token,
  CSRF token, purchase token hay địa chỉ IP; middleware không đọc body request/response. Player id và staff id là id nội bộ
  theo chính sách LOG-04/TBD-08 hiện có.
- **Mất mát chấp nhận được (LOG-06)**: event đi qua pipeline log bounded, có drop như mọi event khác; sink không theo kịp →
  event bị bỏ và đếm (`log_dropped_total`); log không bao giờ làm chậm hay làm hỏng request. Lỗi khi dựng/ghi event bị nuốt,
  đếm ở `request_log_failures_total` (meter `GameServer.Platform`; lỗi bên trong Serilog đã vào `log_sink_errors_total`), response
  không đổi.

**Outcome feature (semantic với code consumer, FR-084):** `IRequestOutcomeFeature` (namespace `GameServer.Platform.Web.Requests`)
trên `HttpContext.Features`, lấy bằng `context.RequestOutcome()` (không bao giờ null; host không có middleware thì tạo khi dùng lần
đầu). Thành viên: `ResultCode`, `Operation`, `Replayed`, `NotModified` (get/set), `StatusCode`, `Aborted`, `Completed` (get),
`RouteName` (get; slice 014, default interface member trả `null` — implementation của consumer không phải sửa; framework đặt
cùng giá trị với request event khi chốt).

```csharp
app.Use(async (HttpContext context, RequestDelegate next) =>   // middleware của game, đặt TRƯỚC app.UseGameServer()
{
    await next(context);
    var outcome = context.RequestOutcome();
    // outcome.Completed == true, outcome.ResultCode != null, StatusCode null chỉ khi Aborted
});
app.UseGameServer();
```

- Được framework đặt cho mọi response nó viết (`ErrorResponseWriter` — mọi lỗi, 404, 405, refusal profile/CSRF/permission/Dev,
  gate, rate limiter, timeout; endpoint command player/Dev/GM — code, replayed, operation, **trước** hook `afterExecute`; config
  — not-modified) và chốt sau pipeline framework, cùng giá trị với request event dù event bật hay tắt.
- Endpoint consumer và hook `afterExecute` đọc được giá trị đặt tới lúc đó (`Completed` false, `StatusCode` null); middleware
  consumer ngoài pipeline framework đọc giá trị cuối sau `await next`. Bao gồm rejection và route không phải command, điều mà hook
  `afterExecute` của `MapPlayerCommand` không thấy.
- Middleware consumer lấy feature **trước** `await next` cũng được: framework giữ đúng instance đã có (do
  `context.RequestOutcome()` tạo) và chốt chính instance đó, nên tham chiếu lấy sớm cũng đọc được giá trị cuối.
- Endpoint consumer có thể đặt `ResultCode`, `Operation`, `Replayed`, `NotModified` cho response của chính nó; framework không
  ghi đè code đó, trừ `ABORTED`/`INTERNAL` khi chốt. Response non-2xx của consumer không có code → `HTTP_{status}`.
- Đọc feature không bao giờ buffer, copy hay parse body response. Không phải hợp đồng wire: client không thấy nó.

**Console (vận hành, FR-087, FR-088):** `Observability:Logging:Console:Enabled` (mặc định `false`; flavor `Local` và `Dev` của
host mẫu đặt `true`), `Observability:Logging:Console:MinimumLevel` (`Information`, cùng luật level), `Observability:Logging:Console:BufferSize`
(1 000, ≥ 1). Một dòng dễ đọc mỗi event ≥ `MinimumLevel`: request event dạng
`08:15:02.123 INF POST /api/v1/counter/increment (counter.increment) player=p_7f3a… req=1a2b3c4d -> 200 OK 12.4 ms`
(`staff=` cho GM, `replayed`, `not_modified`, `-` thay status khi abort), event khác `HH:mm:ss.fff LVL message [SourceContext]` và
exception ở các dòng sau; màu chỉ khi stdout là terminal. Ký tự điều khiển trong mọi giá trị in ra (message, thuộc tính, category,
exception) → `?` (không có escape ANSI hay dòng giả; exception giữ xuống dòng). Queue riêng bounded, drop (đếm
`log_dropped_total{sink="console"}`, tách khỏi file) — không bao giờ là
console provider mặc định (chờ khi queue đầy); stdout bị dừng/redirect không chặn request. **Thêm vào** file sink, không thay: nội
dung file không đổi, log vận hành vẫn không đi tới provider khác. Bật ngoài Development/Test được phép, kèm một Warning khi start.

**Request log theo flavor (FR-086, quyết định 2026-11-03, owner có thể đổi):**

| Flavor (deployment environment) | Request event | Console (host mẫu) |
| --- | --- | --- |
| local, dev (Development) | bật (mặc định) | bật (file flavor) |
| test host của framework/consumer (Development/Test) | bật (test logger bắt) | tắt (`LocalFlavorFactory` ép tắt) |
| beta, uat (Staging) | **tắt** (mặc định); `GameServer:RequestLog:Enabled` = `true` bật (+ Warning khi start) | tắt |
| live (Production) | **tắt** (mặc định); cùng switch | tắt |

`GameServer:RequestLog:Enabled` (bool?, không đặt = theo deployment environment như bảng), `GameServer:RequestLog:ExcludedPaths`
(string[], tối đa 50, mỗi mục bắt đầu bằng `/`, ≤ 200 ký tự, không `?`; mảng bị thay chứ không gộp). Giá trị sai → startup
report `[request-log] …`, exit 1. Dung lượng khi bật: ~300–400 B/event (≈ 13–17 GB/instance/ngày ở 500 request/s, ≈ 1,3–1,7 GB ở
50 request/s, chưa nén); với giới hạn file mặc định (100 MiB × 30 file ≈ 3 GiB) 500 request/s chỉ giữ ~4–5 giờ log vận hành:
nâng `Observability:Logging:FileSizeLimitBytes`/`RetainedFileCountLimit` hoặc ship log trước khi bật ở Staging/Production.

**Metrics/traces:** OpenTelemetry; sources/meters `GameServer.*` + ASP.NET Core (hosting, Kestrel, rate limiting) + HttpClient
+ runtime. OTLP khi `Observability:Otlp:Endpoint` có giá trị (`Protocol` `grpc` | `http/protobuf`; với HTTP, đường dẫn
`/v1/traces`, `/v1/metrics` được nối vào endpoint gốc); Prometheus qua collector. Cấu hình sai (level, URI, protocol) → startup fail.
Metric bổ sung slice 005: `command_results_total{operation, code, replayed}`, `http_errors_total{code}`, `log_*`. Slice 015:
gauge `config_version_info{version}` giữ tên, label và giá trị version **server**; gauge mới `client_config_version_info{version}`
= version client (mục 21); counter `config_compression_skipped_total{reason}` đếm response `GetConfig` đầy đủ gửi không nén dù
client chấp nhận một encoding (`busy`: gate của host hết permit; `probe`: client chỉ chấp nhận encoding bị loại ở lần thử lúc
start; mục 21). Không metric nào
có tag player/request (OBS-02); `gs.request_id`, `gs.player_id` là attribute của span. Span `command {operation}` là con của span
HTTP. Couchbase SDK tracing và `txn_cleanup_lost_total`: chờ cluster thật (V2).

**Health (semantic):**

| Route | Profile | Check | Trả về |
| --- | --- | --- | --- |
| `GET /health/live` | Public | không có | 200 `Healthy` |
| `GET /health/ready` | Public | `store` (≤ 2 s), `config`, `log`, `shutdown` | 200 `Healthy`/`Degraded`, 503 `Unhealthy`; chỉ một từ, không chi tiết; kết quả dùng chung `Observability:ReadyCacheMilliseconds` (1000) để flood không thành flood vào store |
| `GET /gm/v1/health` | GM `ops.read` | như ready | JSON từng check: status, description, duration, data; data của check `config` có `configVersion` (server) và `clientConfigVersion` (slice 015) |

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
| `GameServer:DeploymentEnvironment=Test` chỉ với `GameServer:EnvironmentId` = `test` (host test tự động; Test bật công cụ dev). Môi trường triển khai do game đặt (`qa`, …) phải dùng Development, Staging hoặc Production (slice 013, review m9, fail-closed) | `[deployment] GameServer:DeploymentEnvironment=Test is reserved for automated test hosts with GameServer:EnvironmentId 'test', but EnvironmentId is '<id>' …` |
| `GameServer.TestKit` được nạp ngoài Development/Test (trừ TestServer in-process của kit) (slice 013, review m8) | `[deployment] the consumer test kit (GameServer.TestKit) is loaded in '<env>' …`, cách sửa |
| Store `InMemory`/`File` chỉ ở Development/Test | `[deployment] the <InMemory/File> store is configured in '<env>'. Fix: set GameServer:Store=Couchbase …` |
| Secret trong file cấu hình ngoài Development/Test (`GameServer:SecretKeys`) | key, file, tên biến môi trường |
| Endpoint hoặc command profile Dev ngoài Development/Test (slice 013, mục 12) | endpoint (method + route) hoặc module + operation, deployment environment, environment id, cách sửa |
| Development nghe trên địa chỉ không phải loopback | một Warning sau khi server nghe (không lỗi) |

Mọi lỗi của một lần khởi động (deployment identity, registry, service, secret, `IStartupCheck`, validator option của module qua
`IStartupValidator`) nằm trong **một** `GameServerStartupException`; Warning ghi một lần, category `GameServer.Startup`.
Overlay config game `config/env/<DeploymentEnvironment>`: beta và uat dùng chung `Staging`. Staging (beta, uat) **không** có cheat,
xác thực thử nghiệm, seed staff GM, GM OpenAPI, verifier/provider giả (chặt hơn OPS-09). Từ slice 013 framework tự chặn
profile Dev ngoài Development/Test ở ba lớp (startup, pipeline, runner — mục 12), không chỉ dựa vào `if` trong host.

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

Slice 015 (FG-023, BC-9, BC-10): mỗi check được framework **tạo** từ đăng ký của nó (instance, kiểu qua `ActivatorUtilities`,
hoặc factory) trong một `try` riêng, mỗi lần start một lần, rồi chạy `Check` trong một `try` khác; check không còn là singleton
resolve từ container và không bị dispose. Constructor/factory ném → một dòng "could not be created" mang tên check (kiểu
implementation; factory → class mà factory trả về khi nó là class không abstract cài `IStartupCheck`
(`AddSingleton<IStartupCheck, X>(sp => …)`, method group trả `X`), nếu không thì kiểu khai báo factory, bỏ qua lớp closure do
compiler sinh), các check khác vẫn chạy. Factory trả null → dòng "could not be created" với `InvalidOperationException: the
registration's factory returned null`. Constructor ném `GameServerStartupException` không marker → các violation của nó, nguyên
văn (serve và provisioning như nhau). Check chỉ được đọc từ `IServiceCollection` đưa cho `AddGameServer`: check đăng ký chỉ trong
container builder của bên thứ ba (Autofac `ConfigureContainer`) không được thấy và không chạy (không phát hiện; hoãn). Check cần
config đọc qua `GameConfigStatus` (`State` `NotLoaded`/`Loaded`/`Failed`, `Config`, `DeclarationsPassed`, `TryGetConfig`,
`TryGetCatalog<T>`; không bao giờ ném, publish một lần trước khi các check chạy). Check nhận `IGameConfig` hoặc một catalog khai báo vẫn an toàn: khi config hoặc
catalog không có, nó nhận một dòng "did not run" thay vì lặp lại nguyên nhân. Provisioning **không** load game config
(`GameConfigStatus` ở `NotLoaded`): check cần config hay catalog được bỏ qua với **warning** trên stderr, exit code không đổi.
Host/test đưa instance `IGameConfig` (`TestHost` `config:`) đi vòng qua loader, nên check như vậy vẫn chạy trong provisioning ở
đó. Văn bản dòng: mục 21.

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
| `tools/local/run-local.ps1` / `.sh` | `-Port`/`--port` (5080), `-Reset`/`--reset` (`--FileStore:ResetOnStart=true`), `-Lan`/`--lan` (`0.0.0.0`, cảnh báo đỏ + địa chỉ LAN); build Debug mỗi lần, `dotnet run --no-build --project <host> --launch-profile local` từ thư mục host, readiness `http://127.0.0.1:<port>/health/ready` ≤ 60 s |
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

### Breaking (slice 013, FG-010 — so với `v0.6.0`)

| Mục | Thay đổi | Cách chuyển |
| --- | --- | --- |
| BC-1 Dev ở startup | endpoint và command profile Dev trước đây chỉ bị chặn ở Production; nay startup fail ở mọi môi trường ngoài Development/Test (beta, uat, live, giá trị khác), một dòng cho mỗi endpoint và mỗi command, kể cả module `ServiceOnly()`, `MapPlayerApi=false` và `--provision` | chỉ `AddModule` module cheat/Dev trong `if (gameServer.Options.AllowsDevelopmentTools)` (như `samples/SampleHost/Program.cs`); test dựng host Staging/Production với module Dev phải bỏ module hoặc chờ lỗi mới |
| BC-2 Dev lúc chạy | route Dev map sau `UseGameServer` trả 403 `FORBIDDEN` ngoài Development/Test (Warning trong log); runner từ chối command Dev (`FORBIDDEN`) cho mọi actor, cả execute lẫn replay | job hoặc luồng GM gọi command Dev ở Staging phải dùng command không phải Dev |
| BC-3 Thông báo | `[endpoint …] uses profile Dev in Production. Fix: do not register cheat/dev modules in Production (OPS-09).` → `[endpoint …] uses profile Dev in deployment environment '<env>' (environment id '<id>'). Fix: register Dev (cheat) modules only when development tools are allowed (GameServerOptions.AllowsDevelopmentTools: Development/Test, flavors local and dev) (OPS-09, SEC-08).` + dòng mới `[module …] command '…' has profile Dev …` | test so khớp text cũ: so khớp nhãn endpoint (`[endpoint POST /api/v1/cheat/grant]`) hoặc text mới |

### Không cung cấp

| Hạng mục | Ghi chú |
| --- | --- |
| `GameServer:Staging:AllowCheatModule` | điểm mở rộng hoãn (R13): nếu QA cần cheat trên uat — chỉ Staging, từ chối ở Production, chỉ đăng ký `CheatModule` sau permission GM có audit, không bao giờ xác thực thử nghiệm hay seed; nếu thêm, chỉ mở rộng `GameServerOptions.AllowsDevProfile` (slice 013, FR-006) |
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

## 19. Consumer readiness (slice 013 — FG-001/002/003/004/006/007/010/011/012/025, ARC-08, API-05, LOG-04)

Dùng framework từ repo game (submodule pin vào annotated tag): `docs/guides/consumer-guide.md`. Mọi API trong mục này là
API cho consumer; xoá hoặc đổi chữ ký sau `v0.7.0` là breaking (ARC-08). Đúng với framework v0.7.0 (chưa phát hành).

### API công khai mới (semantic với code consumer)

| API | Assembly / namespace | Hợp đồng |
| --- | --- | --- |
| `GameServerOptions.AllowsDevProfile` | Platform, `GameServer.Platform` | quyết định duy nhất cho profile Dev (= `AllowsDevelopmentTools`: Development/Test); xem "Luật Dev" dưới |
| `GameServerOptions.RequestLog` (`RequestLogSettings`) | Platform | section `GameServer:RequestLog` (mục 13) |
| `IRequestOutcomeFeature`, `HttpContext.RequestOutcome()`, `RequestResultCodes`, `RequestLog.Category`/`EventId` | Platform.Web, `GameServer.Platform.Web.Requests` | outcome của request (mục 13): framework đặt cho mọi response nó ghi; sau pipeline mọi thành viên là cuối cùng và `ResultCode` không bao giờ null; đọc không cần body |
| `ConsoleLoggingSettings` (`Observability:Logging:Console`) | Platform.Observability | console sink tuỳ chọn (mục 13) |
| `ErrorCodeBands` (`GameStart` 50000, `GameEnd` 89999, `FrameworkReservedStart` 90000, `FrameworkReservedEnd` 99999) | Core, `GameServer.Errors` | dải game (mục 8) |
| `GameServerVersion` (`Framework`, `FrameworkInformational`, `Of(Assembly)`, `InformationalOf(Assembly)`) | Platform | version framework; `Of` = phần trước `+` của informational version, không có thì `AssemblyName.Version` 3 phần, không có nữa thì `unknown` |
| `GameServerRuntimeInfo` (record: `GameId`, `EnvironmentId`, `DeploymentEnvironment`, `InstanceId`, `GameAssembly`, `GameVersion`, `FrameworkVersion`; `ResolveInstanceId(configured)`), `GameServerBuilder.Runtime` | Platform / Platform.Web | singleton mỗi host, dựng một lần trong `AddGameServer`; game assembly = `IHostEnvironment.ApplicationName` (entry assembly, cả dưới `WebApplicationFactory<TEntryPoint>`), không có thì entry assembly của process, không có nữa thì `unknown`. `ResolveInstanceId` = luật instance id duy nhất (`Observability:InstanceId` → `HOSTNAME` → tên máy) cho tên file log, `service.instance.id`, file analytics và GM |
| `GmModule.VersionRoute`, `GmVersionResponse` | Gm.Adapters, `GameServer.Modules.Gm` | `GET /gm/v1/ops/version`, `ops.read` (PCR gm) |
| Test kit `src/GameServer.TestKit` (`TestHost`, `CapturedLog`, `LocalFlavorFactory<TEntryPoint>`, `LocalFlavorOptions`, `LocalFlavorSettings`, `TestGameConfig`, `CommandContextDouble`, `RecordedWrite`, `RecordedClaim`, `ProtobufHttp`, `IdentityHttp`, `TestStores`/`TestStoreKind`, `GmBrowser`/`CookieJarHandler`) | TestKit, `GameServer.TestKit` | không phụ thuộc framework test nào, không `Sample.*`; xem luật dưới |

**Luật của test kit:** thành viên chỉ được thêm (additive); tham số thêm sau luôn tuỳ chọn và đứng cuối (slice 014 thêm
kiểm mapping FG-027 vào đây). `tests/Shared` (`SampleTestHost`, `GameHttp`, `FileStoreTestSupport`) không phải API
consumer.

**Luật `LocalFlavorSettings`:** setting của framework mà ghi đĩa, chạy việc nền hoặc ghi console được thêm vào
`LocalFlavorSettings.Keys` trong chính slice đưa nó vào; `LocalFlavorSettingsTests` kiểm mỗi key có trên một kiểu options
và được nêu trong consumer guide. Bộ hiện tại (8 key): `FileStore:Folder`, `GameServer:Outbox:SweeperEnabled`,
`GameServer:Jobs:Enabled`, `GameServer:Hosting:DrainDelaySeconds`, `GameServer:Analytics:Directory`,
`Observability:InstanceId`, `Observability:Logging:Directory`, `Observability:Logging:Console:Enabled` (= `false`).

**`CommandContextDouble`:** bọc chính `CommandContext` của runner, nên mọi luật truy cập (Touches/Reads, kiểu state, giới
hạn claim, claim `receipts`/`pa_*`, claim trùng hoặc có sẵn) ném đúng exception và thông báo của runner; `NewId`/`NewSeed`
bằng của runner với cùng receipt id (test conformance). `Writes` như runner: `Set` lại state đã nạp (hoặc đổi rồi đặt lại)
không phải là ghi. `CheckCommit(outcome)` trả đúng thông báo commit-rule của runner (dùng chung `CommitRules`): code commit
không có trong catalog hoặc ≠ HTTP 200, aggregate ghi vượt `HardBytes` (runner trả `AGGREGATE_TOO_LARGE`; envelope đo với
GameId/EnvironmentId của actor, mặc định `test`/`test`), luật audit GM; `CheckCommit(outcome, encodeResult)` thêm giới hạn
resultCompact (`ResultCompactMaxBytes`, giới hạn = mặc định framework hoặc `WithCommandSettings`). **Không** kiểm kích thước
receipt (`ReceiptMaxBytes`): receipt chứa body hash, config version và encoding của host, double không có.

### Luật Dev (semantic — FG-010, FR-001..FR-008)

Một quyết định: `GameServerOptions.AllowsDevProfile` (Development/Test = flavor `local`, `dev` và host test; không có
setting). Ba lớp độc lập dùng nó:

1. startup validation: một dòng cho mỗi endpoint Dev và mỗi command Dev đã đăng ký (kể cả module `ServiceOnly()`,
   `GameServer:Hosting:MapPlayerApi=false`, command không có route và `--provision`), exit 1;
2. pipeline: route Dev lọt qua (map sau `UseGameServer`) trả 403 `FORBIDDEN` trước mọi scheme xác thực, một Warning; giá
   trị profile lạ bị từ chối như vậy;
3. runner: command Dev bị từ chối `FORBIDDEN` cho mọi actor ở `ExecuteAsync` và `TryReplayAsync`, trước limiter, đọc
   receipt, guard và transaction.

Chi tiết và thông báo: mục 17 ("Breaking (slice 013, FG-010)").

### Request log và console (vận hành)

| Key | Mặc định | Ghi chú |
| --- | --- | --- |
| `GameServer:RequestLog:Enabled` | không đặt = bật ở Development/Test, tắt ở Staging/Production | `true` ngoài Development/Test → một Warning khi start kèm ước lượng dung lượng |
| `GameServer:RequestLog:ExcludedPaths` | `[]` | ≤ 50 mục, bắt đầu bằng `/`, ≤ 200 ký tự, không `?`; prefix theo segment |
| `Observability:Logging:Console:Enabled` | `false` | flavor `Local`/`Dev` của host mẫu đặt `true`; `LocalFlavorSettings` ép `false` |
| `Observability:Logging:Console:MinimumLevel` | `Information` | |
| `Observability:Logging:Console:BufferSize` | `1000` | ≥ 1; đầy thì bỏ và đếm `log_dropped_total` |

Thuộc tính của event, level và outcome feature: mục 13. Dung lượng: `docs/guides/environments-and-local-run.md` mục 8.

### Version và phát hành (semantic — FG-007, FR-050..FR-055)

- Một khai báo: `GameServerFrameworkVersion` trong `Directory.Build.props` của framework (`0.7.0`) → `Version` của mọi
  project framework: assembly và file version `0.7.0.0`, informational `0.7.0+{40 hex sha}` (Source Link của SDK; không
  có metadata git thì không có hậu tố). Áp dụng cả khi consumer build project framework qua `ProjectReference` (props
  của framework được niêm phong, FG-003). `sdk/unity/com.gameserver.client/package.json` `version` bằng giá trị đó
  (`VersionTests`, thông báo nêu cả hai giá trị).
- Dòng khởi động (Information, category `GameServer.Startup`, cuối `UseGameServer` thành công; không có khi validation
  fail hay `--provision`): `Starting {GameId} {GameVersion} on framework {FrameworkVersion}: environment {EnvironmentId} ({DeploymentEnvironment}), instance {InstanceId}.`
- Telemetry resource: `service.name` `game-server-{gameId}` (không đổi), `service.version` = `GameVersion`,
  `service.instance.id` = `InstanceId` (không đổi), `gameserver.framework.version` = `FrameworkVersion`,
  `deployment.environment.name` (không đổi).
- GM `GET /gm/v1/ops/version` (`ops.read`); endpoint Public và health probe không lộ version (OBS-03).
- Phát hành: annotated tag `v{GameServerFrameworkVersion}` (`v0.7.0`); tăng props và `package.json` cùng một commit; giữa
  hai tag các build phân biệt bằng sha. Release notes của slice 013–016 gộp thành message của tag.

### Breaking (slice 013 — so với `v0.6.0`)

| # | Thay đổi | Cách chuyển |
| --- | --- | --- |
| BC-1 | endpoint và command Dev làm startup fail ngoài Development/Test (trước: chỉ Production), một dòng mỗi endpoint/command | chỉ đăng ký module cheat/Dev trong `if (gameServer.Options.AllowsDevelopmentTools)`; chạy thử mọi flavor không phải development một lần (cả `--provision`) |
| BC-2 | route Dev map sau `UseGameServer` trả 403 `FORBIDDEN` ngoài Development/Test; runner từ chối command Dev | như BC-1; job/luồng GM ở Staging dùng command không phải Dev |
| BC-3 | text startup cho endpoint Dev đổi, thêm dòng cho command Dev (mục 17) | test so khớp nhãn `[endpoint POST …]` hoặc text mới |
| BC-4 | `tests/Shared/{ProtobufHttp,IdentityHttp,TestStores,GmBrowser,TestHost}.cs` chuyển vào `src/GameServer.TestKit` (namespace `GameServer.TestKit`); `TestStores.CreateFileStore` không còn public; `TestHost` của kit mặc định chỉ Identity và config rỗng | bỏ link `Compile` tới `tests/Shared`, thêm `ProjectReference` tới kit, đổi `using`, dùng `LocalFlavorFactory<Program>` và `CommandContextDouble` |
| BC-5 | `Directory.Build.targets` của consumer không còn tới project framework; `IsTestProject` theo đường dẫn tương đối với root framework | chuyển những gì từng chèn vào project framework sang project của game; bỏ target chặn thư mục `tests` (FG-004) |
| BC-6 | assembly framework mang `0.7.0.0` / informational `0.7.0+sha` (trước: `1.0.0.0`); telemetry có `service.version` (= version game) và `gameserver.framework.version` | sửa dashboard theo `1.0.0.0` hoặc theo việc thiếu `service.version`; xoá stamp tag framework tự viết và dòng log version tự viết của game |
| BC-7 | một request event mỗi request ở Development/Test (mặc định) và console tuỳ chọn; tăng dung lượng log nơi bật | Staging/Production không đổi trừ khi bật; xoá request log tự viết của game; bật console trong file flavor local của game |

Bước chuyển đầy đủ: release notes `v0.7.0` (bản nháp `specs/013-consumer-readiness/release-notes-draft.md`).

## 20. Wire contract: route catalog, endpoint check, export, section contract (slice 014 — FG-026, FG-027, FG-028, ARC-07, API-02, API-10, SEC-07..09)

Mỗi route HTTP được khai **một lần** dưới dạng dữ liệu (`RouteDeclaration`); mapping, check khi khởi động, request log
(`RouteName`, mục 13) và code sinh cho Unity (`tools/GameServer.WireGen`, PCR unity-sdk) đều đọc cùng khai báo đó. Không có
setting nào tắt check (FR-013). Đúng với framework v0.7.0 (chưa phát hành / unreleased).

### Catalog entry (semantic với code consumer và generator)

`WireCatalog` (namespace `GameServer.Platform.Web.Catalog`, singleton DI, có sau `Build()` mà không cần start) giữ `Routes`
(ordinal theo `Name`) và `Sections` (ordinal theo `Id`); `TryGetRoute`, `TryGetSection`, `FindRouteName(method, template)`
(khai báo any-method khớp mọi method), `Validate()` (lỗi khai báo, dưới đây), `ToExportJson()`.

| Trường `RouteDeclaration` (Core, `GameServer.Routing`) | Luật |
| --- | --- |
| `Name` | `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`, ≤ 64 ký tự, duy nhất mỗi host; route command mặc định lấy tên = operation (`RouteDeclaration.Command`). **Ổn định: đổi tên là breaking (ARC-08)** |
| `Methods` | HTTP method viết hoa, sắp ordinal, không trùng; rỗng = mọi method (health probe, `AnyMethod`) |
| `Template` | đúng một `/` đầu; route template ASP.NET hợp lệ; có segment version `(^\|/)v[0-9]+(/\|$)` trừ `/health/live`, `/health/ready` (API-02) |
| `Profile` | `Public`, `Player`, `Gm`, `Dev` (`SecurityProfile`) |
| `Surface` | `PlayerApi` (map khi `GameServer:Hosting:MapPlayerApi`), `GmApi` (khi `GameServer:Hosting:MapGmApi`), `Host` (luôn map) |
| `Operation` | operation của command; null cho route không phải command |
| `Permission` | permission GM; bắt buộc với profile `Gm`, null với profile khác (SEC-07) |
| `Condition` | tên một hosting condition đã đăng ký; null = luôn map khi surface được map |

`RouteEntry(Declaration, Owner, Origin, ServiceOnly)`: `Owner` = tên module, `platform` (`RouteEntry.PlatformOwner`) hoặc
`host` (`RouteEntry.HostOwner`, khai bằng `GameServerBuilder.DeclareRoutes(...)` trước `Build()`; route Public của host vẫn
cần `GameServer:PublicEndpointAllowlist`); `ServiceOnly` = owner đăng ký bằng `ServiceOnly()`. Module khai route trong
`ModuleDescriptor.Routes` (project business); route Public trong `Routes` cũng tính là public endpoint (SEC-09, mục 9).
Platform: `PlatformRoutes` (`Time`, `Snapshot`, `Config`, `Receipt`, `GmOpenApi`, `All`); module framework: `IdentityRoutes`,
`IapRoutes`, `EconomyRoutes`, `CheatRoutes`, `GmRoutes`, `ObservabilityRoutes` (`Live`, `Ready`). Bảng route của từng module:
PCR của module.

**Luật đặt tên:** `<area>.<route>` chữ thường, phần cách bằng `.`, chữ trong phần cách bằng `_` (`identity.login_guest`,
`gm.ops_gate_min_client_version`). Tên sinh trong Unity = tách theo `.` và `_`, viết hoa chữ đầu mỗi phần, nối lại
(`identity.login_guest` → `IdentityLoginGuest`; section `wallet` → `Wallet`/`WalletId`).

**Origin:** `Framework` khi owner là `platform` hoặc tên assembly của kiểu module bắt đầu bằng `GameServer.` (prefix dành riêng
cho framework — game không đặt assembly bắt đầu bằng `GameServer.`); còn lại `Game`. Khai báo của host là `Game`.

**Hosting condition:** `services.AddRouteCondition(name, sp => bool)` (Platform.Web) trong module; khai báo tham chiếu bằng
tên. Có sẵn: `development_tools` (`RouteConditions.DevelopmentTools`, platform, = `GameServerOptions.AllowsDevelopmentTools`:
`platform.gm_openapi`, `gm.auth_login`) và `gm.sso` (module GM, = `Gm:Oidc` đã cấu hình: `gm.sso_start`, `gm.sso_callback`).
Condition quyết định hai chiều: khai báo chưa map chỉ là lỗi khi condition đúng, và endpoint đã map mà condition sai là lỗi
(dòng "Map khi condition sai" dưới đây); route Public có condition sai không tính là public endpoint. Tên không đăng ký là
lỗi khai báo (chỉ một dòng, không thêm dòng condition sai). Condition đọc option module không hợp lệ (vd. `gm.sso` khi `Gm`
sai) được coi là sai; lỗi option đã có dòng riêng.

**Mapping từ khai báo:** `MapPlayerCommand`/`MapDevCommand(declaration, …)`, `MapGmCommand(declaration, …)` (operation và
permission lấy từ khai báo; thiếu → `ArgumentException`), `MapDeclared(declaration, handler).As*Endpoint()` cho route không
phải command, `.WithDeclaration(declaration)` cho endpoint do thư viện map (`MapHealthChecks`, `MapOpenApi`). Endpoint mang
`RouteDeclarationMetadata`; endpoint map bằng chuỗi được khớp theo cặp (method, template). Khai báo gắn vào endpoint phải là
**chính instance** mà module, platform hoặc host liệt kê (so sánh tham chiếu): map từ khai báo không ai liệt kê (hoặc từ một bản
sao cùng tên) là "chưa khai", vì export (và route sinh cho Unity) không có nó.

### Lỗi khai báo (semantic — dòng startup, `WireCatalog.Validate`)

Chạy trong `UseGameServer`, `--provision` và export mode; mỗi lỗi một dòng, exit 1, không bind port.

| Lỗi | Dòng |
| --- | --- |
| Tên sai | `[route {name}] (owner {owner}) name does not match ^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$ (≤ 64). Fix: use <area>.<route> in lower case (FG-026).` |
| Tên trùng | `[route {name}] is declared by {ownerA} and {ownerB}. Fix: give each route a unique name (FG-026).` |
| Trùng method + template | `[route {method} {template}] is declared twice: {nameA} ({ownerA}) and {nameB} ({ownerB}). Fix: declare it once (ARC-07).` |
| Template không hợp lệ | `[route {name}] template '{template}' is not a valid route template: {parser message}. Fix: correct the declaration (FG-026).` |
| Thiếu version | `[route {name}] template '{template}' has no version segment. Fix: prefix with /v{n}/ (API-02).` |
| Gm không permission / không phải Gm mà có permission | `[route {name}] has profile {profile} and permission '{permission}'. Fix: GM routes declare their permission, other routes none (SEC-07).` |
| Condition chưa đăng ký | `[route {name}] uses hosting condition '{condition}' which is not registered. Fix: services.AddRouteCondition("{condition}", …) in the module (FG-026).` |
| `PublicEndpoints` khác profile (BC-5) | `[module {module}] lists {template} in PublicEndpoints but route {name} declares profile {profile}. Fix: declare it Public or remove it from PublicEndpoints (SEC-09).` |
| Hai mapper cho một aggregate (BC-4) | `[aggregate {id}] has {n} wire mappers. Fix: register one mapper per aggregate; replace a framework mapper with ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…)) (API-10).` |
| Hidden member không tồn tại | `[aggregate {id}] hidden member '{path}' does not exist on {TState}. Fix: use C# member names joined by '.' (FG-027).` |
| Field may-stay-default không tồn tại | `[aggregate {id}] field '{path}' does not exist on {message full name}. Fix: use proto field names joined by '.' (FG-027).` |
| `TState` của mapper không nhận state của aggregate | `[aggregate {id}] wire mapper takes {TState} but the aggregate holds {state type}. Fix: AddAggregateWireMapper<{state type}, TWire>(…) (API-10).` |
| `ReplaceWireMapper` đổi message | `[aggregate {id}] ReplaceWireMapper must keep {message full name}; a new payload needs a new aggregate (FG-021).` |
| Mapper không kiểu (Warning, không phải lỗi) | `[aggregate {id}] wire mapper registered without its message type: the catalog has no message for this section and Unity gets no parser. Fix: AddAggregateWireMapper<TState, TWire>(…) (FG-028).` |

`{method}` của dòng trùng là các method chung, nối bằng `,`; `{TState}` là tên kiểu state. Warning được ghi một lần mỗi
section không kiểu, category `GameServer.Startup` (cùng đường với warning của `IStartupCheck`, mục 17); host vẫn chạy.

### Lỗi endpoint (semantic — dòng startup, `EndpointCatalogValidator`)

Mọi endpoint route đã map ↔ catalog, ở **mọi** môi trường; `{label}` = `METHODS /template`. Endpoint không phải route bị bỏ
qua. Template so sau khi chuẩn hoá `/` đầu, phân biệt hoa thường.

| Lỗi | Dòng |
| --- | --- |
| Chưa khai (kể cả endpoint gắn khai báo không ai liệt kê) | `[endpoint {label}] is not declared in the route catalog. Fix: declare it in the module's ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes for a host endpoint) and map it from the declaration (FG-026).` |
| Gắn bản sao của khai báo đã liệt kê (cùng tên, khác instance) | `[endpoint {label}] is mapped from a declaration named {name} that is not the one {owner} lists. Fix: map it from the declaration instance listed in ModuleDescriptor.Routes (or GameServerBuilder.DeclareRoutes), not a copy (FG-026).` |
| Map khi condition sai | `[route {name}] is mapped but its condition '{condition}' does not hold. Fix: map it only when the condition holds, or correct the declaration's condition (FG-026).` (dòng duy nhất: route Public có condition của module/platform vẫn là public template, không thêm dòng allowlist) |
| Condition đọc options không hợp lệ | chính dòng lỗi options (`OptionsValidationException.Failures`), sau các dòng endpoint, một lần (không lặp khi `ValidateModuleOptions` đã báo); condition coi là sai. Options chỉ có `Validate()` (không `ValidateOnStart`) cũng làm startup fail |
| Template khác (endpoint gắn khai báo) | `[route {name}] declared at {declared template} but mapped at {mapped template}. Fix: map it with the declaration and no route group prefix, or correct the declaration (FG-026).` |
| Method khác | `[endpoint {label}] maps method {method} but route {name} declares {declared methods}. Fix: declare one route per method or correct the mapping (FG-026).` |
| Profile khác | `[endpoint {label}] has profile {mapped} but route {name} declares {declared}. Fix: use the As*Endpoint helper of the declared profile or correct the declaration (SEC-08).` |
| Operation khác | `[endpoint {label}] runs command '{mapped}' but route {name} declares '{declared}'. Fix: map it with MapPlayerCommand/MapDevCommand/MapGmCommand(declaration, …) (ARC-07).` |
| Permission khác | `[endpoint {label}] requires permission '{mapped}' but route {name} declares '{declared}'. Fix: map it with the declaration (SEC-07).` |
| Khai mà không map | `[route {name}] ({label}, owner {owner}, surface {surface}) is declared but not mapped. Fix: map it in the module's {map method} method or remove the declaration (FG-026).` |

`{map method}` = `MapPlayerApi` (surface `PlayerApi`), `MapGmApi` (`GmApi`), `MapHostEndpoints` (`Host`). "Khai mà không map"
chỉ báo khi owner đã đăng ký và không `ServiceOnly()`, surface của nó được map và condition đúng. Endpoint map bằng chuỗi gõ
sai cho hai dòng (chưa khai + khai mà không map); endpoint gắn khai báo trong `MapGroup` có prefix cho một dòng (template
khác). Profile chỉ được so khi endpoint có đúng một `SecurityProfileMetadata`.

### Kiểm tra trước khi phục vụ (semantic — FR-012)

- `EndpointSetValidationService` (`IHostedLifecycleService`, đăng ký **đầu tiên** trong `AddGameServer`) chạy trong
  `StartingAsync`, trước `GenericWebHostService.StartAsync` (pipeline + Kestrel bind) và trước mọi hosted service khác: dựng
  tập endpoint cuối cùng **một lần** (kể cả endpoint map sau `UseGameServer`, trực tiếp hoặc trong `MapGroup`) và chạy cùng ba
  validator (security, command route, catalog — gồm "khai mà không map"). Lỗi → một `GameServerStartupException` chứa mọi
  dòng, exit 1 qua `RunGameServerAsync`, không port nào được bind; `TestServer` và `WebApplicationFactory` ném cùng exception.
- `UseGameServer` chạy các check không phải endpoint; nó chỉ dựng endpoint (để gộp vào cùng một báo cáo) khi đã có lỗi khác.
  Cấu hình hợp lệ chỉ có lỗi endpoint → báo khi host start (cùng dòng, exit 1, không bind). Dòng `Starting …` (mục 19) được
  ghi sau khi check endpoint đạt.
- Endpoint map muộn nhận đúng các dòng trên (và các dòng security/command sẵn có); không có gì đánh dấu "muộn". Từ chối Dev
  lúc request của slice 013 vẫn giữ.
- Chi phí: validation ≈ 1 ms cho 217 endpoint; mỗi endpoint được build một lần trước khi phục vụ, như slice 013 (đo
  2026-11-03, `EndpointCheckPerformanceTests`, `GAMESERVER_PERF=1`).

### Export (semantic với generator — FR-030, format `gameserver.wire-contract/1`)

`<host> --environment <flavor> --export-wire-contract <file>` (qua `GameServerCommandLine.Parse` + `RunGameServerAsync`;
host phải dùng hai API này): `Build()`, `ModuleRegistry.Validate` (deployment identity, luật module như `--provision`) và
`WireCatalog.Validate()` (lỗi → mỗi dòng một dòng stderr, exit 1, không ghi file), thư mục của file phải tồn tại (không thì
exit 1 nêu tên), ghi `ToExportJson()` (UTF-8 không BOM, LF; ghi vào file tạm `<tên file>.<guid>.wire-contract.tmp` cùng thư mục rồi
move đè lên path, nên path không bao giờ chứa export dở; process bị kill giữa hai bước có thể để lại file tạm đó, xoá được),
dispose, exit 0. **Không bao giờ** `UseGameServer`, hosted
service, store, game config, secret, socket. Bị từ chối (exit 1): thiếu path, dạng `=`, `-export-wire-contract`,
`/export-wire-contract`, switch hai lần, argument tiếp theo bắt đầu bằng `-`, kết hợp với `--provision`, file không ghi được.

| Thuộc tính | Kiểu | Ý nghĩa |
| --- | --- | --- |
| `format` | string | `gameserver.wire-contract/1` |
| `frameworkVersion` | string | `GameServerVersion.Framework` (`0.7.0`) |
| `protobufVersion` | string | informational version của `Google.Protobuf` mà host nạp |
| `routes[]` | object | `name`, `methods[]` (bỏ = mọi method), `template`, `profile`, `surface`, `owner`, `origin` (`framework`/`game`), `operation?`, `permission?`, `condition?`, `serviceOnly?` (chỉ khi true) |
| `sections[]` | object | `id`, `owner`, `origin`, `message?`, `hiddenMembers[]?`, `mayStayDefault[]?` |

Tất định: thuộc tính theo thứ tự trên, indent 2 space, null và mảng rỗng bị bỏ, `routes` theo `name`, `sections` theo `id`
(ordinal); không timestamp, path, máy, user, game id hay environment — hai lần chạy cho cùng bytes. **Versioning:** thêm thuộc
tính là thay đổi minor (reader bỏ qua thuộc tính lạ); đổi nghĩa hay xoá thuộc tính là major mới (`/2`) và reader fail với
major khác.

### Section contract (semantic với Unity — FG-027, FG-028, API-10)

- Một aggregate client-visible có wire mapper = một section `SectionEntry(Id, Owner, Origin, MessageFullName, HiddenMembers,
  MayStayDefaultFields)`. Đăng ký: `services.AddAggregateWireMapper<TState, TWire>(aggregateId, map,
  o => o.Hidden(...).MayStayDefault(...))` — `MessageFullName` = full name proto của `TWire`.
- `Hidden("A.B", …)`: member của state giữ trên server có chủ đích (tên member C# nối bằng `.`, phần tử collection trong suốt);
  `MayStayDefault("a.b", …)`: field proto mà giá trị có nghĩa có thể là default (enum 0; tên field proto nối bằng `.`). Hai danh
  sách sắp ordinal, có trong catalog và export (nguồn của PCR module), được kiểm ở startup (dòng ở trên) và bởi
  `WireConformance` của test kit.
- `AddAggregateWireMapper<TState>(id, Func<TState, IMessage>)` (không kiểu) là `[Obsolete]` (BC-3, giữ qua v0.7.x; CS0618 thành
  lỗi build dưới `TreatWarningsAsErrors`): section không có `message`, Unity không có parser, một Warning lúc start.
- Một mapper mỗi aggregate id: registry giữ mapper **đầu tiên** và báo trùng (BC-4; không còn last-wins). Thay mapper (kể cả của
  framework): `ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(id, map, configure))` — xoá các mapper đăng ký trước
  đó cho id bằng `AddAggregateWireMapper` (instance) rồi đăng ký mapper có kiểu; id chưa có mapper → chỉ đăng ký (không lỗi).
  Mapper đăng ký bằng kiểu hoặc factory không bị xoá và vẫn bị báo trùng. **Mapper thay phải giữ message** (`TWire`) của mapper
  có kiểu mà nó thay: Unity parse section bằng parser của message đó (section framework: parser trong gói), nên đổi message làm
  startup fail (dòng ở trên); payload mới cần aggregate mới. `TState` của mapper (thay hoặc không) phải nhận được state của
  aggregate (`IsAssignableFrom`), nếu không startup fail thay vì 500 ở mỗi snapshot.
- `WireConformance.Check`/`CheckModules` chỉ thấy mapper do module đăng ký; override `ReplaceWireMapper` của host chỉ được
  kiểm qua `CheckAll(services)` trên host đã build. Sample dựng từ state type của **aggregate** (không phải `TState` của
  mapper), nên mapper khai trên lớp cơ sở vẫn bị kiểm member chỉ lớp dẫn xuất có; collection/dictionary hai phần tử, perturb
  phần tử đầu và cuối (bắt `.First()`/`.Last()`), enum lấy lần lượt các giá trị khác 0 theo vị trí phần tử, perturbation
  không trùng giá trị cùng member ở phần tử khác (data-model §7.2).
- **FR-055:** slice này không đổi message wire, route player, document hay response GM API nào; payload của section framework
  giữ nguyên từng byte (`WireMapperRegistrationTests`).

### Breaking (slice 014 — so với `v0.6.0`)

| # | Thay đổi | Lộ ra thế nào | Cách chuyển |
| --- | --- | --- | --- |
| BC-1 | mọi endpoint đã map phải có trong catalog (gắn đúng instance khai báo đã liệt kê) và khớp khai báo; endpoint chưa khai, lệch khai báo, hoặc map khi condition sai làm startup fail ở mọi môi trường | exit 1, một dòng mỗi endpoint (method, route, fix) | khai route trong `ModuleDescriptor.Routes` của module (hoặc `GameServerBuilder.DeclareRoutes` cho endpoint của host) và map từ khai báo đó |
| BC-2 | endpoint map sau `UseGameServer` được kiểm trước khi phục vụ: catalog, profile, segment version, trùng, allowlist Public, luật Dev, route command | exit 1, cùng dòng như endpoint trong pipeline; với cấu hình không có lỗi nào khác, lỗi endpoint giờ ném từ `StartAsync`/`RunAsync` chứ không từ `UseGameServer`, và dòng `Starting …` cũng chuyển sang đó | sửa endpoint theo dòng báo, hoặc chuyển nó vào một module |
| BC-4 | hai wire mapper cho một aggregate id làm startup fail (trước: cái sau thắng) | exit 1, dòng nêu aggregate | một mapper mỗi aggregate; thay bằng `ConfigureExtensions(e => e.ReplaceWireMapper<TState, TWire>(…))`, giữ nguyên message (`TWire`) — payload mới cần aggregate mới |
| BC-5 | route liệt kê trong `ModuleDescriptor.PublicEndpoints` mà khai profile khác làm startup fail | exit 1, dòng nêu module, template và route | khai profile `Public` hoặc bỏ khỏi `PublicEndpoints` |

BC-3 (mapper không kiểu `[Obsolete]`): "Section contract" ở trên. BC-6 (`sdk/GameServer.Client` không còn sinh code cho
KestrelDemo): PCR unity-sdk. Bước chuyển đầy đủ: release notes `v0.7.0` (bản nháp
`specs/014-wire-contract/release-notes-draft.md`).

## 21. Game config: kiểm nội dung khi khởi động, catalog, config status, hai version, nén và bundle (slice 015 — FG-018, FG-019, FG-023, FG-024, CFG-01..03, API-03, API-06, ARC-06, ARC-08)

Module khai file config nó đọc, luật nội dung và catalog có kiểu trong `ModuleDescriptor`; framework kiểm mọi thứ ở **khối đầu
tiên** của `UseGameServer` và báo một dòng mỗi lỗi, trước mọi traffic. Đúng với framework v0.7.0 (chưa phát hành / unreleased).

### Thứ tự một lần start ở chế độ serve (semantic)

1. Loader (`[config] …`). Config không load được → **không** chạy luật, catalog, bundle (all or nothing).
2. Registry (có dòng file bắt buộc), luật của module, rồi catalog — theo thứ tự đăng ký module, thứ tự khai báo, thứ tự luật báo.
3. Dòng kích thước bundle, khi player API được map.
4. Wire catalog, services, deployment, gate, request log (không đổi).
5. Startup check (tạo, did not run, dòng riêng của check).
6. Option của module và endpoint (không đổi, slice 014).

Provision và export không chạy pha này. Một dòng của luật/catalog là error; warning của luật có cùng văn bản, được log một lần
(category `GameServer.Startup`), không fail.

### Loader (semantic — văn bản dòng)

Loader báo **mọi** lỗi trong một `GameServerStartupException` (BC-3; trước: dừng ở lỗi đầu). Mỗi `.json` và manifest được parse một
lần, tên property lặp ở bất kỳ độ sâu nào bị từ chối (BC-2; so không phân biệt hoa thường — `OrdinalIgnoreCase`, như reader của framework — sau khi unescape: `"a"`/`"A"` trong
cùng một object là lỗi). Lỗi parse của
một manifest, hoặc manifest không đọc được, chỉ bỏ danh sách file của manifest đó. File không đọc được (`IOException`,
`UnauthorizedAccessException`) là một dòng, không phải exception. Một mục `files`/`clientVisible` null, rỗng hay chỉ có khoảng
trắng là một dòng mỗi danh sách và bị bỏ qua (không dòng "does not exist" hay "not listed" theo sau). Tên file có ký tự điều khiển
(`char.IsControl`) bị từ chối bằng dòng "is not allowed", ký tự đó in dạng `\uXXXX` (hex hoa); `GameConfigVersions.Compute` ném
`ArgumentException` với tên như vậy.

```text
[config] '{path}' not found. Fix: create config/base/manifest.json with label, files and clientVisible (CFG-01).
[config] '{path}' is not valid JSON: {parser message}. Fix: correct the manifest (CFG-01).
[config] manifest 'label' is empty. Fix: set a label in config/base/manifest.json (CFG-01).
[config] file name '{name}' is not allowed. Fix: use a plain relative file name.
[config] file '{path}' listed in the manifest does not exist. Fix: add the file or remove it from the manifest.
[config] '{path}' is not valid JSON: {parser message}. Fix: correct the file (CFG-01).
[config] clientVisible file '{name}' is not listed in 'files'. Fix: add it to manifest 'files'.
[config] '{path}' has property '{name}' twice at {jsonPath} (line {line}, byte {byte}). Fix: keep one property; the last one used to win silently (CFG-01).
[config] '{path}' has properties '{firstName}' and '{name}' at {jsonPath} (line {line}, byte {byte}), which differ only in case. Fix: keep one property; config is read ignoring case, so the last one used to win silently (CFG-01).
[config] '{path}' cannot be read: {message} Fix: check that the host process can read the file (CFG-01).
[config] '{manifestPath}' lists an empty name in '{files|clientVisible}'. Fix: remove the entry or name a file (CFG-01).
```

`{line}` là dòng (từ 1) của tên lặp, `{byte}` là byte (từ 1) của dấu ngoặc mở tên đó trong dòng (với cặp khác hoa thường: vị trí
của tên thứ hai; `{firstName}` là cách viết đầu tiên trong object).

### Khai báo (semantic với code module — `GameServer.Config`, Core)

| Thành viên `ModuleDescriptor` | Ý nghĩa |
| --- | --- |
| `RequiredConfigFiles` (có sẵn) | file bắt buộc, không luật |
| `ConfigFiles` | `ConfigFileDeclaration.Required(name, rules…)` / `Optional(name, rules…)` |
| `ConfigCatalogs` | `ConfigCatalogDeclaration.Of<T>(c => T.Load(c.Config), files…)` |

Tập file bắt buộc của một module = `RequiredConfigFiles` ∪ `ConfigFiles` bắt buộc ∪ file của catalog mà module không khai
`Optional`. Thiếu một file trong tập đó → dòng registry có sẵn, một lần mỗi (module, file); không luật nào của file đó chạy và
không catalog nào đọc nó được build (không dòng thứ hai):

```text
[module {module}] required config file '{file}' is missing. Fix: add it to config/base/manifest.json 'files' and to config/base (CFG-01).
```

Luật (`ConfigRule(name, check)`) đọc `ConfigContext` (`Json(file)`: một `JsonDocument` mỗi file, parse một lần; `Config`) và báo qua
`ConfigProblems` (`Error`/`Warning` tại `ConfigLocation.WholeFile`, `Row(n)`, `Key(k)`, `Path(jsonPath)`, `InFile(file)`). Luật
viết bằng `ConfigJson` (`TryGetObject`, `TryGetArray`, `RequiredString`, `RequiredInteger`, `OptionalInteger`, `RequireObject`,
`PathOf`) để mọi module dùng cùng câu chữ: `'{property}' is missing`, `'{property}' is null`, `'{property}' must be {a type}, found
{kind}`, `'{property}' must be an integer of at least {min}, found {value}`. "Số nguyên JSON" = `TryGetInt64` (`1.0`, `1e3`, `"5"`
là lỗi). Một luật của game có thể nằm trên file của framework (`resources.json`); mỗi dòng nêu module khai báo. Test kit:
`ConfigDeclarationCheck.Check(config, modules)` cho đúng các dòng của host (`Errors`, `Warnings`, `Passed`, `ThrowIfFailed()`,
`Catalog<T>()`, `AddCatalogsTo(services)`).

### Dòng luật và catalog (semantic — văn bản dòng)

```text
[module {module}] config {file}[ (config/env/{env})]{location}: {problem}. Fix: {fix}
[module {module}] config {file}: rule '{rule}' threw {ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018).
[module {module}] catalog {Type}[ ({file}{location})]: {problem}. Fix: {fix}
[module {module}] catalog {Type}: the build threw {ExceptionType}: {message}. Fix: report problems through ConfigProblems instead of throwing (FG-018).
[module {module}] catalog {Type}: the build read optional config file '{file}', which is absent. Fix: build an empty catalog when Config.HasFile("{file}") is false, or declare the file Required (FG-018).
[catalog {Type}] was not built and no other line explains why (framework fault). Fix: report it with this config; the catalog would fail at its first use (FG-018).
[module {module}] catalog {Type}: the build returned no catalog and reported no problem. Fix: return the catalog or report why (FG-018).
[catalog {Type}] is declared by {moduleA} and {moduleB}. Fix: declare it in one module; the other takes it from DI (FG-018).
[catalog {Type}] the registration that replaces the declaration of module {module} could not be resolved: {ExceptionType}: {message}. Fix: correct that registration (host or test), or remove it so the platform builds the declared catalog (FG-018).
```

`{location}` là `""`, `" row {n}"`, `" key '{key}'"` hoặc `" at {jsonPath}"`. `{problem}` và `{fix}` do luật đưa; formatter bỏ một
dấu `.` cuối của mỗi phần nên mỗi dòng kết thúc đúng một lần. Luật của framework (văn bản từng dòng): PCR economy và PCR iap; luật
mẫu: PCR sample-game.

### Catalog và DI (semantic)

| Binding của kiểu T (quyết định sau `Build()`, descriptor non-keyed cuối cùng của T) | Pha config làm gì | DI trả gì |
| --- | --- | --- |
| *Declared*: descriptor do platform đăng ký trong `AddModule` (`TryAdd`) | build một lần khi file có mặt và không có lỗi luật | instance đã build; build hỏng → exception có marker; pha chưa chạy → `InvalidOperationException` hướng dẫn `UseGameServer` hoặc `ConfigDeclarationCheck` |
| *Provided*: host/test đăng ký T (trước **hoặc** sau `AddModule`) | resolve T một lần trong `try` (dòng cat-host khi hỏng) | đăng ký của host |
| Module thứ hai khai cùng T | dòng cat-dup; bỏ qua | của module đầu |

File `Optional` vắng **không** chặn build: build tự quyết catalog rỗng (`c.Config.HasFile(file)`), build đọc
(`c.Json`) một file `Optional` vắng → dòng "the build read optional config file … which is absent" nêu đúng file đã đọc (chỉ khi
exception là lần đọc file vắng đó; build ném vì lý do khác → dòng "the build threw", dù có file vắng). Catalog không build được
(file bắt buộc thiếu hoặc có lỗi luật) không thêm dòng thứ hai; một catalog không build mà không dòng nào giải thích → dòng
"framework fault" (chốt chặn, cả host và kit), nên không catalog khai báo nào lazy. service phụ thuộc nó (outbox consumer, startup
check) không lặp nguyên nhân. Module **không** đăng ký catalog khai báo trong `ConfigureServices` (test kiến trúc
`ConfigCatalogArchitectureTests`); catalog không khai báo vẫn lazy (lỗi của nó lộ ở request đầu tiên).

### Config status (semantic với startup check — `GameServer.Platform.GameConfigStatus`)

`State`: `NotLoaded` (chưa publish; provisioning) → `Loaded` (`Config` = instance mà `IGameConfig` resolve ra, `DeclarationsPassed`
= không có dòng file bắt buộc, luật hay catalog ở lần start này; warning không tính) hoặc `Failed`. `TryGetCatalog<T>(out
catalog)`: catalog khai báo T đã build ở lần start này (bởi khai báo, hoặc đăng ký của host/test); false khi không `Loaded`, T
không phải catalog khai báo, hoặc không build được. Publish một lần bởi pha config, trước các check; không thành viên nào ném.
Unit test của check: `TestGameConfigStatus.NotLoaded()`, `.Failed()`, `.From(report)` (kit).

### Startup check (semantic — văn bản dòng, mục 17)

```text
[{check}] the startup check could not be created: {ExceptionType}: {message}. Fix: register its dependencies; a check that reads config takes GameConfigStatus (FG-023).
[{check}] the startup check did not run: the game config did not load (see the [config] lines). Fix: take GameConfigStatus instead of IGameConfig and skip when it is not Loaded (FG-023).
[{check}] the startup check did not run: catalog {Type} was not built (see the lines above). Fix: take GameConfigStatus and read the catalog with TryGetCatalog (FG-023).
[{check}] the startup check did not run: provision mode does not load the game config. Fix: take GameConfigStatus and skip when it is NotLoaded (FG-023).
[{check}] the startup check failed: {ExceptionType}: {message}
```

Dòng thứ tư là **warning** (chỉ ở provisioning, BC-9); dòng cuối là văn bản có sẵn. Ở provisioning mọi trường hợp "config hay
catalog không có" đều là warning, không bao giờ là error. Check (constructor hoặc `Check`) ném `GameServerStartupException` không marker → các violation của nó, nguyên văn.

### Hai version (semantic — FG-024, FG-019, API-03)

```text
server = label + "+" + hex12(SHA-256("gameserver.config.server/1\n" + "label\n" + label + "\n"
           + for name in sort_ordinal(files):         "file\n" + name + "\n" + len(bytes) + "\n" + bytes
           + "visible\n" + for name in sort_ordinal(clientVisible): name + "\n"))
client = label + "+" + hex12(SHA-256("gameserver.config.client/1\n" + "label\n" + label + "\n"
           + for name in sort_ordinal(clientVisible): "file\n" + name + "\n" + len(bytes) + "\n" + bytes))
```

| Thay đổi | Version server | Version client |
| --- | --- | --- |
| Bytes của file chỉ server | đổi | **giữ** |
| Bytes của file client-visible | đổi | đổi |
| Thêm/bớt file trong `clientVisible` (base hoặc overlay env) | đổi | đổi |
| Label | đổi | đổi |
| Thứ tự hoặc trùng lặp trong danh sách manifest | giữ | giữ |
| Mọi file client-visible | ≠ version client | ≠ version server |

Version server: receipt, `ReceiptView.config_version`, `ICommandContext.ConfigVersion`, response command của GM, `config_version`
của năm message player (nghĩa không đổi), metric `config_version_info`, readiness `configVersion`. Version client: field additive
`client_config_version` (`ErrorEnvelope` 10, `CommandResponse` 9, `SnapshotResponse` 4, `GetConfigResponse` 5, `LoginResponse` 7),
metric `client_config_version_info`, readiness `clientConfigVersion`, khoá cache của SDK (PCR unity-sdk). `IGameConfig.ClientConfigVersion`,
`GameConfigVersions.Compute(label, files, clientVisible)`, `new GameConfig(label, files, clientVisible)`; constructor 4 tham số và
`FileGameConfigLoader.ComputeVersion` là `[Obsolete]` (BC-6).

### Bundle `GetConfig` và kiểm kích thước (semantic — FR-052, FR-053)

Bundle được serialize **một lần** khi khởi động (file client-visible sắp ordinal + version server); mỗi request chỉ serialize phần
đuôi (giờ server, version client), byte giống hệt serialize cả message. Khi config load được và
`GameServer:Hosting:MapPlayerApi=true`, response lớn nhất (đuôi với giờ âm, varint 10 byte) được so với
`GameServer:Limits:ConfigBundleBytes` (giới hạn bytes **chưa nén**); bằng giới hạn thì start được, lớn hơn → (BC-7):

```text
[config] the client-visible bundle needs a GetConfig response of up to {size} bytes ({files} files), over GameServer:Limits:ConfigBundleBytes = {limit}. Fix: raise the limit or remove files from 'clientVisible' (CFG-03, API-06).
```

### Nén (semantic với client và vận hành — FR-050, FR-051)

| Điều kiện | Kết quả |
| --- | --- |
| `GameServer:ConfigCompression=false` (rollback) | bytes và header như trước 015: không `Content-Encoding`, không `Vary` |
| `not_modified`, hoặc body < 1024 B | không nén + `Vary: Accept-Encoding` |
| không có `Accept-Encoding`, `identity`, mọi ứng viên `q=0`, không parse được, chỉ `deflate` | không nén + `Vary` |
| `br` chấp nhận (q > 0, trực tiếp hoặc qua `*`) và q(br) ≥ q(gzip) | Brotli quality **4**, `Content-Encoding: br` |
| còn lại, `gzip` chấp nhận | gzip level **6**, `Content-Encoding: gzip` |
| kết quả nén không nhỏ hơn | không nén + `Vary` |
| encoding không được chọn ở lần thử lúc start (bundle không nén nhỏ hơn được) | không nén; client chỉ chấp nhận encoding đó → `config_compression_skipped_total{reason="probe"}` |
| host này đã có max(1, CPU/4) response đang nén (gate theo host, không theo process) | không nén (body như khi tắt nén, + `Vary`), `config_compression_skipped_total{reason="busy"}` |

Chỉ handler `GetConfig` nén (theo request, buffer từ pool); không có middleware nén của ASP.NET nên không route nào khác (login,
session, GM, health, error) có `Content-Encoding` (BREACH). `Content-Length` = bytes trên dây; body đã giải nén giống hệt body chưa
nén. Body request không bao giờ được giải nén. Đo (2026-11-04, Windows 11, .NET 10.0.14, đường code thật, 200 lần sau 20 warm-up):
bundle 1 MiB gzip 6 p95 ≈ 9 ms tỉ lệ 14,0 %, Brotli 4 p95 ≈ 7,5 ms 14,2 %; mẫu 1,7 KB gzip 28,3 %; bundle 28 KB dạng first consumer
gzip 15,7 %. gzip 9 / Brotli 6 (luật T006) cho p95 ≈ 18,2–18,7 ms, trong 10 % của ngân sách 20 ms (SC-005), nên dùng mức thấp hơn.
Unity Editor và Windows player gửi `Accept-Encoding: deflate, gzip` và tự giải gzip; Android/iOS: PCR unity-sdk.

### Dòng thông tin khi khởi động (vận hành)

Sau dòng `Starting …` (slice 013), một lần, category `GameServer.Startup`, Information:

```text
Config {Label}: version {ConfigVersion}, client version {ClientConfigVersion}, {FileCount} files ({ClientVisibleCount} client-visible, bundle {BundleBytes} B, compression {Compression}), {RuleCount} rules, {CatalogCount} catalogs.
```

`{BundleBytes}` = response lớn nhất (số so với giới hạn); `{Compression}` = `off` (rollback switch) hoặc encoding mà lần thử lúc
start giữ (`br,gzip`, `br`, `gzip`, `none`) và số permit của host, ví dụ `br,gzip, permits 2` (max(1, CPU/4); bundle vượt giới hạn
thì không thử, host không start); khi player API không map: `… client-visible, bundle not served), …`.

### Breaking (slice 015 — so với `v0.6.0`)

| # | Thay đổi | Lộ ra thế nào | Cách chuyển |
| --- | --- | --- | --- |
| BC-1 | Nội dung config được kiểm khi khởi động (luật `resources.json`, `iap.json`, luật mẫu), catalog build trước traffic | exit 1, một dòng mỗi lỗi nêu module, file, row/key/path và fix | sửa file theo dòng báo |
| BC-2 | Property JSON lặp tên trong file config hoặc manifest là lỗi loader; tên chỉ khác hoa thường cũng tính là lặp (config đọc không phân biệt hoa thường) | exit 1, dòng "has property … twice" hoặc "has properties … which differ only in case" | giữ một property |
| BC-3 | `FileGameConfigLoader.Load` báo mọi lỗi trong một exception | nhiều violation hơn, văn bản cũ giữ nguyên | đọc mọi violation |
| BC-4 | SDK khoá cache, `ConfigOutdated`, `ConfigVersionChanged` theo version client khi server gửi | ít `ConfigVersionChanged` hơn (không có khi sửa file chỉ server); một lần tải lại sau deploy | so cache với `ClientConfigVersion` |
| BC-5 | Version server hash thêm tập `clientVisible`: giá trị đổi một lần khi nâng cấp | giá trị mới trong metric và receipt | cập nhật giá trị ghim |
| BC-6 | `IGameConfig.ClientConfigVersion`; constructor 4 tham số và `ComputeVersion` `[Obsolete]` | CS0535, CS0618 (= lỗi build) | thêm member; `new GameConfig(label, files, clientVisible)` hoặc `TestGameConfig` |
| BC-7 | Bundle client-visible lớn hơn `GameServer:Limits:ConfigBundleBytes` làm startup fail (trước: 413 cho mọi client) | exit 1, dòng kích thước bundle | nâng giới hạn hoặc chuyển file thành chỉ server |
| BC-8 | Catalog khai báo do platform đăng ký, không còn trong `ConfigureServices` của module | `InvalidOperationException` khi dựng services chỉ từ `ConfigureServices` | đăng ký instance, hoặc `ConfigDeclarationCheck.Check(config, modules).ThrowIfFailed().AddCatalogsTo(services)` |
| BC-9 | Provisioning không load game config; check cần nó được bỏ qua với warning | dòng `warning:` trên stderr, exit code không đổi | nhận `GameConfigStatus`, bỏ qua khi `NotLoaded` |
| BC-10 | Startup check được tạo từ đăng ký ở mỗi lần start, trong isolation riêng; không phải singleton, không dispose. Check chỉ đăng ký trong container của bên thứ ba (Autofac `ConfigureContainer`) không chạy | không có với check stateless | giữ check stateless, không disposable; đăng ký mọi check trong `IServiceCollection` |

Bước chuyển đầy đủ: release notes `v0.7.0` (bản nháp `specs/015-config/release-notes-draft.md`).
