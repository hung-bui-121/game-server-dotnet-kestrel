# Bản nháp nội dung 5a — Giới thiệu framework (nhu cầu, cấu trúc, kiến trúc, điểm mạnh)

> **Đúng với commit 175df65** (nhánh `012-analytics-audit`, tài liệu làm trên `docs-html-guides`).
> Trang HTML đích: `docs/html/01-gioi-thieu.html`. Footer của trang phải ghi "đúng với commit 175df65".

## Ghi chú cho người dựng HTML (frontend-web-dev)

- Văn xuôi tiếng Việt; identifier (tên type, method, file, key cấu hình, route) giữ nguyên, đặt trong `<code>`.
- Mọi đường dẫn file là **tương đối từ gốc repo**. Số dòng (nếu có) chỉ để tham khảo, sẽ lệch dần; tên type/method mới là
  tham chiếu chính.
- Hai sơ đồ cần vẽ bằng SVG inline: **Sơ đồ A** (kiến trúc khối, mục 4.1) và **Sơ đồ B** (chiều tham chiếu project, mục 4.2).
  Đặc tả ở dạng hộp/mũi tên/nhãn bên dưới.
- Bảng "Điểm mạnh" (mục 5) và "Giới hạn" (mục 6) nên là bảng HTML thật, không phải ảnh.
- Liên kết sang trang 5b (`02-them-command.html`) ở cuối mục 4.4 và ở mục 7.

---

## 1. Framework này giải quyết nhu cầu gì

**Một backend dùng chung cho nhiều game mobile.** Mỗi game Unity cần cùng một nhóm việc: đăng nhập, lưu tiến trình và tài
nguyên, mua IAP, công cụ GM, log, kiểm soát bảo trì. Framework làm sẵn các phần chung đó; mỗi game chỉ viết phần nghiệp vụ riêng
(command, rule, aggregate) dưới dạng **module**, và không sửa core.

**Server là nguồn sự thật (server-authoritative).** Client gửi *hành động* (ví dụ "kết thúc trận, thắng"), không gửi kết quả
("tôi có 500 gold"). Server kiểm tra, tính kết quả, lưu, rồi trả về phần dữ liệu đã đổi. Client không bao giờ tự quyết số dư.
(Yêu cầu PLY-01, `Requirements/Game_Server_System_Requirements_v1.2.md` mục 8.1.)

**Kiểu giao tiếp:** HTTP request–response. Unity dùng HTTPS + Protobuf; GM tool web dùng HTTPS + JSON. Không có realtime tick,
UDP hay đồng bộ di chuyển (ngoài phạm vi, yêu cầu mục 2.1).

Ba mục tiêu chính của tài liệu yêu cầu (mục 1), mỗi mục được đo bằng tiêu chí nghiệm thu:

| # | Mục tiêu | Đo bằng (ID yêu cầu) |
| --- | --- | --- |
| 1 | I/O bất đồng bộ, chịu được tải dồn: không sync-over-async, giới hạn hàng đợi có số | PERF-01..10, ACC-16, ACC-52 |
| 2 | Kiến trúc module: thêm module/command/policy mà không sửa Core; game tự định nghĩa dữ liệu player | ARC-01..10, EXT-01..05, ACC-13 |
| 3 | Xác thực, phân quyền, audit GM thống nhất cho module cũ và mới | SEC-02, SEC-07, SEC-08, SEC-14, ACC-09..12 |

Công nghệ nền: C# / .NET 10 (SDK `10.0.403` ghim trong `global.json`), ASP.NET Core + Kestrel, Minimal API, Couchbase ≥ 7.6
(distributed transactions bắt buộc), Google.Protobuf (cùng một version ở server và Unity).

## 2. Phạm vi và trạng thái hiện tại

Phân kỳ theo tài liệu yêu cầu mục 2.6:

| Phase | Nội dung | Trạng thái (tại commit 175df65) |
| --- | --- | --- |
| **P0** — MVP vertical slice | Host, module registry, command runner một transaction + receipt/replay, outbox; Identity (guest, session một thiết bị); Economy (wallet/inventory/ledger); IAP (verifier Apple/Google + fake); GetConfig; SampleGame (battle start/end, shop); GM backend; observability; Unity SDK | **Xong**, slice 001–006 (`specs/001-*` … `specs/006-*`), đã merge vào `main` |
| **P0.5** — Production hardening | GM SSO/CSRF/CORS/Data Protection (007), gate bảo trì + version tối thiểu + ban (008), Google Play Games / Game Center (009), lease job + xoá tài khoản (010), flavor + file store + `--provision` + script chạy local (011), audit log analytics login/payment (012) | 007–012 **xong**. 007–009 đã vào `main`; 010–012 nằm trên nhánh của từng slice, chưa merge |
| P0.5 — còn lại | giftcode (013), IAP S2S notification + refund (014), ops-readiness: secret scan, load test report, container guide, runbook, deploy/rollback, restore drill (015) | Chưa làm |
| **P1** — Feature modules | Event, durable jobs, GM page tương ứng, PRIV-02 (export dữ liệu), GFT-07, MSG-01, ANA-01 phần còn lại | Chưa làm |
| P2 | Mở rộng riêng của từng game | Do game làm |

Ngoài phạm vi framework: realtime, Leaderboard/League (owner bỏ 26/10/2026), GM frontend đầy đủ (dev khác làm dựa trên
`docs/api/gm-openapi.json`).

Nguồn trạng thái: `AgentMemories/features/p05-speckit/00_summary.md`.

## 3. Cấu trúc repo

Hiện tại framework **chưa đóng gói thành NuGet package** (task đóng gói được tách ra làm sau). Game và host tham chiếu các
project bằng `ProjectReference` trong cùng solution `GameServer.slnx`.

| Đường dẫn | Nội dung |
| --- | --- |
| `src/GameServer.Core` | Contract dùng chung, không phụ thuộc web/DB/Protobuf: `ICommandHandler`, `ICommandContext`, `CommandDescriptor`, `CommandOutcome`, `ModuleDescriptor`, `PlayerAggregateDescriptor`, `ErrorCode`, `IClock`, `ILeaseJob`, `AnalyticsEvents` |
| `src/GameServer.Platform` | Lõi runtime: `ModuleRegistry` (đăng ký + kiểm tra khi khởi động), `CommandRunner`, receipt, outbox (`OutboxRelay`, `OutboxSweeper`), store abstraction (`ITransactionalDocumentStore`), lease job (`LeaseJobRunner`), xoá tài khoản (`AccountPurgeService`), analytics pipeline, `GameServerOptions` |
| `src/GameServer.Platform.Web` | Hosting (`AddGameServer`, `UseGameServer`, `RunGameServerAsync`), middleware, map endpoint command (`CommandEndpointFactory`, `GmCommandEndpointFactory`), Protobuf binding, lỗi (`ErrorResponseWriter`), security profile, Data Protection, GM OpenAPI |
| `src/GameServer.Platform.Couchbase` | Store Couchbase (transactions, provisioning, TLS, RBAC) — nơi **duy nhất** gọi Couchbase SDK |
| `src/GameServer.Platform.Observability` | Log file JSON (Serilog), OpenTelemetry, `/health/live` + `/health/ready`, file sink analytics |
| `src/GameServer.Platform.Testing` | Store in-memory (có tiêm lỗi), `FakeClock`, file store của flavor `Local` |
| `src/GameServer.Modules.Identity*` | Aggregate bắt buộc `profile`, guest login, provider login (Play Games, Game Center), session, ban, xoá tài khoản |
| `src/GameServer.Modules.Economy*` | Wallet, inventory, ledger, GM adjust |
| `src/GameServer.Modules.Iap*` | Mua IAP: verifier Apple/Google/fake |
| `src/GameServer.Modules.Gm*` | GM: staff, cookie session, SSO, permission, audit, trang `/gm/v1/ui` |
| `modules/Sample.Counter` | **Module mẫu** tối thiểu (1 command, 1 query, 1 policy thay được, 1 endpoint GM, 1 outbox projection) — ví dụ của trang 5b |
| `templates/module` | Khung module 2 project để copy (`templates/module/README.md`) |
| `samples/SampleGame` | Game mẫu: module `SampleGame` (battle, shop, GM progression) + client Unity 6 `samples/SampleGame/KestrelDemo` |
| `samples/SampleHost` | Host chạy thử: `Program.cs` + `appsettings.{Local,Dev,Beta,Uat,Live}.json` |
| `sdk/unity/com.gameserver.client` | Gói Unity (UPM) client SDK; `sdk/GameServer.Client` build cùng mã nguồn cho .NET và sinh code Protobuf |
| `proto/gameserver/core/v1/core.proto` | Contract wire chung: `CommandResponse`, `SectionUpdate`, `ErrorEnvelope`, snapshot, config |
| `tests/*` | Unit, HTTP, kiến trúc, Couchbase integration, SDK (mục 5) |
| `tools/local`, `tools/GameServer.LoadTest` | Script chạy local, chạy nhiều instance, drill sự cố; công cụ tải/tranh chấp |
| `docs/pcr`, `docs/adr`, `docs/guides`, `docs/api` | Published Contract Register từng module, ADR, hướng dẫn, OpenAPI GM |
| `specs/` | Spec Kit: spec, plan, tasks theo slice |

**Một game cụ thể dùng framework thế nào:** game viết module của mình (2 project, giống `modules/Sample.Counter`), tạo một host
(giống `samples/SampleHost/Program.cs`) gọi `AddGameServer()` rồi `AddModule<...>()` cho từng module cần dùng, cấu hình bằng
`appsettings.{Flavor}.json`.

## 4. Kiến trúc

### 4.1 Sơ đồ khối (Sơ đồ A — cần vẽ SVG)

Đặc tả để vẽ. Bố cục từ trái sang phải, ba cột.

**Cột trái — Client**
- Hộp `Unity game` chứa hộp con `com.gameserver.client (GameServerClient)`.
- Hộp `GM tool (web)`.

**Cột giữa — một hộp lớn "Host process (ASP.NET Core + Kestrel)"**, bên trong xếp từ trên xuống:
1. Dải ngang `Middleware pipeline`: các ô nối tiếp
   `ForwardedHeaders` → `ExceptionMiddleware` → `CorrelationMiddleware` → `Routing` → `GateMiddleware` →
   `ProfileEnforcementMiddleware (auth)` → `RequestTimeouts` → `RateLimiter`.
2. Dải ngang `Endpoints`: ô `Player API (Protobuf) /api/v1/...`, ô `GM API (JSON) /gm/v1/...`, ô `Public /v1/time, login, /health/*`.
3. Hộp `Platform`: ô `CommandRunner`, ô `ModuleRegistry`, ô `Outbox relay + sweeper`, ô `Lease jobs`, ô `Analytics pipeline`.
4. Dải ngang `Modules` (mỗi module một ô): `Identity`, `Economy`, `IAP`, `GM`, `SampleGame`, `Sample.Counter`, ô nét đứt
   `Module của game`.
5. Hộp `Core (contract)` nằm dưới cùng, mỏng, có nhãn "Modules chỉ phụ thuộc Core".

**Cột phải — Hạ tầng**
- Trụ `Couchbase` (nhãn: "bucket theo môi trường, scope `g_{gameId}`, collection `pa_*`, `receipts`, …").
- Trụ nét đứt `File store (chỉ flavor Local)`.
- Hộp `Files: logs/*.json, analytics/*.jsonl`.
- Hộp `OTLP collector (tuỳ chọn)`.
- Hộp `Apple / Google (verify IAP, identity)`.

**Mũi tên**
- `Unity game` → dải `Middleware pipeline`: nhãn "HTTPS POST, Protobuf, X-Request-Id + Bearer".
- `GM tool` → dải `Middleware pipeline`: nhãn "HTTPS, JSON, cookie + CSRF".
- `Endpoints (Player API / GM API)` → `CommandRunner`: nhãn "command".
- `CommandRunner` → `Modules`: nhãn "gọi handler".
- `CommandRunner` → `Couchbase`: nhãn "1 transaction / command".
- `Outbox relay + sweeper` → `Couchbase`: nhãn "projection".
- `Analytics pipeline` → `Files`: nhãn "JSONL".
- `IAP` module (thực ra endpoint IAP) → `Apple / Google`: nhãn "trước transaction".
- `Host` → `OTLP collector`: nét đứt, nhãn "traces, metrics".

### 4.2 Phân tầng và luật phụ thuộc (Sơ đồ B — cần vẽ SVG)

Sơ đồ chiều **tham chiếu project** (không phải đường mạng). Các hộp và mũi tên "A → B" nghĩa là "A tham chiếu B":

```text
SampleHost ──► Module.Adapters ──► Module ──► GameServer.Core
SampleHost ──► GameServer.Platform.Couchbase ──► GameServer.Platform ──► GameServer.Core
SampleHost ──► GameServer.Platform.Observability
Module.Adapters ──► GameServer.Platform.Web ──► GameServer.Platform
```

Nhãn trên mũi tên `Module ──► GameServer.Core`: "chỉ được tham chiếu Core (ARC-01)". Đặt một biển cấm (nét đỏ có gạch chéo) từ
`Module` tới `Platform.Web`, `Platform.Couchbase`, `Google.Protobuf`, nhãn "arch test chặn".

Luật được **test kiến trúc** kiểm (`tests/GameServer.Architecture.Tests/ArchitectureTests.cs`), không chỉ là quy ước:

| Luật | Tên test |
| --- | --- |
| Core và project nghiệp vụ không phụ thuộc web, database, wire, logging | `Core_and_business_projects_are_independent_of_web_database_wire_and_logging` |
| Chỉ adapter Couchbase dùng Couchbase SDK | `Only_the_couchbase_adapter_uses_the_couchbase_sdk` |
| Chỉ Platform ghi qua transaction; module không chạm store trực tiếp | `Only_platform_writes_through_transactions`, `Modules_do_not_touch_the_document_store_directly` |
| Project nghiệp vụ không phụ thuộc adapter của mình hay module khác | `Business_projects_do_not_depend_on_their_adapters_or_other_modules` |
| Handler nhận type domain, không nhận message wire | `Command_handlers_receive_domain_types_not_wire_messages` |
| Không MVC controller (chỉ Minimal API, đăng ký tường minh) | `No_mvc_controllers` |
| Chỉ Observability dùng Serilog/OpenTelemetry | `Only_the_observability_project_uses_serilog_and_opentelemetry` |

Thêm: `BannedSymbols.txt` cấm `DateTime.UtcNow`, `DateTime.Now`, `DateTimeOffset.UtcNow`, `TimeZoneInfo.Local`,
`DateTime.Today` (dùng `IClock`) và `Task.Wait`/`.Result` (dùng `await`) — vi phạm là lỗi build vì build bật
warnings-as-errors.

### 4.3 Mô hình module

Một module gồm **2 project** (tài liệu yêu cầu mục 4.2; ví dụ `modules/Sample.Counter`):

| Project | Chứa | Tham chiếu |
| --- | --- | --- |
| `Module` (vd. `Sample.Counter`) | State aggregate, record command/result, handler, policy, error code, nửa nghiệp vụ của `ModuleDescriptor` | chỉ `GameServer.Core` |
| `Module.Adapters` (vd. `Sample.Counter.Adapters`) | Lớp `IGameModule` (+ `IPlayerApiModule` / `IGmApiModule`), file `.proto`, map wire ↔ domain, đăng ký DI | module + `GameServer.Platform.Web` |

Điểm vào duy nhất của module là `ModuleDescriptor` (`src/GameServer.Core/Modules/ModuleDescriptor.cs`): tên, `DependsOn`,
`Permissions`, `Aggregates`, `Commands`, `ErrorCodes`, `Collections` (kèm cách xử lý khi xoá tài khoản), `OutboxProjections`,
`Jobs`, `DeletionSteps`, `PublicEndpoints`, `RequiredConfigFiles`.

Host đăng ký module tường minh, không quét assembly (ARC-07):

```csharp
gameServer
    .AddModule<IdentityModule>()
    .AddModule<EconomyModule>()
    .AddModule<IapModule>()
    .AddModule<SampleGameModule>()
    .AddModule<CounterModule>()
    .AddModule<GmModule>();
```

(trích `samples/SampleHost/Program.cs`). `AddModule<T>(m => m.ServiceOnly())` đăng ký service của module mà không map endpoint
(ARC-04). Game thay rule bằng `ConfigureExtensions(x => x.ReplacePolicy<...>().ReplaceHandler<...>(...))`
(`src/GameServer.Platform.Web/Hosting/ExtensionRegistry.cs`).

### 4.4 Mô hình "một command = một transaction"

Đây là ý quan trọng nhất của framework. Mọi thao tác **làm đổi dữ liệu player** là một *command*:

- **Aggregate**: dữ liệu player chia thành các khối JSON (aggregate), mỗi khối một document `pa_{aggregateId}` với key =
  `playerId`, có `rev` và `schemaVersion`. Ví dụ `profile`, `wallet`, `inventory`, `counter`. Tối đa 16 aggregate một game.
- **`CommandDescriptor`** (`src/GameServer.Core/Commands/CommandDescriptor.cs`) khai báo trước: tên operation
  (`<module>.<command>`), `Touches` (aggregate được ghi, 1..4), `Reads` (chỉ đọc), số claim marker tối đa (0..2), security
  profile, permission GM.
- **`CommandRunner`** (`src/GameServer.Platform/Commands/CommandRunner.cs`) mở **một** transaction Couchbase, đọc
  `Touches ∪ Reads` + receipt, gọi handler, rồi ghi các aggregate đã đổi (`rev + 1`), claim marker và **receipt** trong cùng
  transaction, commit. Handler **không** gọi database.
- **Receipt + replay (idempotency)**: client sinh `X-Request-Id` (UUID) cho mỗi command. `receiptId` =
  SHA-256(actorId | playerId | operation | requestId). Gửi lại cùng requestId và cùng body trong 24 h → server trả **đúng kết quả
  cũ** (`replayed = true`), không chạy lại. Cùng requestId nhưng body khác → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`.
- **Từ chối nghiệp vụ** (`CommandOutcome.Reject`) → rollback, không ghi gì, không receipt; client nhận HTTP 200 với mã nghiệp vụ.
- **Outbox**: hiệu ứng phụ (log, ledger, audit GM, analytics) là entry `ctx.AddOutbox(kind, payload)` nằm **trong receipt**, nên
  commit cùng lúc với dữ liệu. Sau commit, relay inline giao cho consumer; nếu lỗi, `OutboxSweeper` (mỗi 30 s) giao lại; quá 10 lần
  → dead letter. Consumer mặc định chiếu entry vào một collection (`OutboxProjection`).
- **Section / rev**: response trả các aggregate *client-visible* đã đổi (`SectionUpdate` có `base_rev`, `new_rev`, payload
  Protobuf). SDK Unity giữ bản sao theo `rev`.
- **Nâng schema lười**: tăng `schemaVersion` của aggregate thì khai một `IAggregateUpgrader` cho mỗi bước; document cũ được nâng khi
  đọc và ghi lại ở lần commit kế tiếp.

Chi tiết đường đi của một command và cách viết command mới: **trang 5b** (`02-them-command.html`).

### 4.5 Security profile và xác thực

Mọi endpoint phải có **đúng một** security profile, gắn bằng helper trong `src/GameServer.Platform.Web/Routing/ApiModules.cs`:

| Profile | Helper | Dùng cho | Xác thực |
| --- | --- | --- | --- |
| Player | `AsPlayerEndpoint()` (tự gắn bởi `MapPlayerCommand`) | API game, Protobuf | `Authorization: Bearer <token>` → `PlayerBearerAuthenticationHandler` (session, profile: đã xoá → bị khoá → bị thay phiên) |
| Gm | `AsGmEndpoint(permission)` (tự gắn bởi `MapGmCommand`) | GM API, JSON | cookie GM + permission + CSRF cho thao tác ghi |
| Public | `AsPublicEndpoint(...)`, `AsPublicPlayerEndpoint()` | `/v1/time`, login, health | không; route phải nằm trong allowlist (`GameServer:PublicEndpointAllowlist` hoặc `ModuleDescriptor.PublicEndpoints`) |
| Dev | `AsDevEndpoint()` | cheat, công cụ dev | chỉ Development/Test; startup chặn ở Production |

- **Deny-by-default**: `ProfileEnforcementMiddleware` trả 403 cho endpoint không có profile, kể cả khi bộ kiểm tra lúc khởi động
  bị tắt.
- **Kiểm tra lúc khởi động** (`EndpointSecurityValidator`): route thiếu `/v{n}/`, route trùng, Public chưa vào allowlist, Dev ở
  Production, GM mutation Public thiếu CSRF → host **không khởi động**.
- Session một thiết bị: login mới tăng `SessionEpoch` của profile; token cũ nhận `SESSION_REPLACED`.
- GM (slice 007): SSO OIDC, cookie, CSRF, CORS allowlist, Data Protection key ring lưu trong store, audit mọi thao tác ghi
  (mỗi GM command ghi đúng một entry `gm_audit`, runner kiểm).

### 4.6 Gate, ban và giới hạn tải

- **Gate** (`GateMiddleware`, slice 008): document `gate` chứa cờ bảo trì và version client tối thiểu theo platform. Chạy
  **trước** xác thực trên Player API và login; trả 503 `MAINTENANCE` hoặc 426 `UPDATE_REQUIRED`. Cache ≤ 10 s mỗi instance
  (`GameServer:Gate:CacheSeconds`). GM, health, Dev vẫn hoạt động.
- **Ban**: ban login làm xác thực thất bại (`ACCOUNT_SUSPENDED`); ban theo nhóm command do `BanCommandGuard` (một
  `ICommandGuard`) chặn **lần chạy mới** trong `CommandRunner`, retry của command đã commit vẫn được replay.
- **Giới hạn** (`GameServer:Limits:*`, `GameServer:Commands:*`): body Player ≤ 64 KiB, timeout Player 10 s, concurrency command
  toàn instance 200 + hàng đợi, mỗi player chạy tuần tự 1 command (hàng đợi 2, quá thì `RATE_LIMITED`).

### 4.7 Lưu trữ

| Store | Dùng ở | Ghi chú |
| --- | --- | --- |
| Couchbase (`AddCouchbaseDocumentStore`) | flavor `Dev`, `Beta`, `Uat`, `Live` | bucket theo môi trường, scope `g_{gameId}` (`CouchbaseOptions.ScopeName`), distributed transactions; `Live` bắt buộc `couchbases://` (TLS) |
| File store (`AddFileDocumentStore`) | flavor `Local` | một file/document, journal làm điểm commit; bị từ chối ngoài Development/Test |
| In-memory (`AddInMemoryDocumentStore`) | test | có tiêm lỗi (`StoreFaultInjector`) |

Cả ba implement cùng contract `ITransactionalDocumentStore` (`src/GameServer.Platform/Persistence/DocumentStore.cs`) và chạy cùng bộ
test conformance (`tests/GameServer.Platform.Tests/Persistence/StoreConformanceTests.cs`,
`FileStoreConformanceTests.cs`, `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseStoreConformanceTests.cs`). Quyết định
kiến trúc: `docs/adr/0001-runner-store-abstraction.md`.

**Provisioning** (slice 011): `dotnet SampleHost.dll --provision` tạo scope, collection, index rồi thoát (exit 0/1/2). Collection
và index được suy ra từ `ModuleDescriptor` của các module đã đăng ký. Quyền RBAC tối thiểu và lệnh mẫu:
`docs/guides/environments-and-local-run.md` mục 5.

### 4.8 Flavor (môi trường) — slice 011

Một bản build chạy mọi môi trường. Flavor = tên môi trường host (`ASPNETCORE_ENVIRONMENT`), nạp `appsettings.{Flavor}.json`.

| Flavor | `GameServer:DeploymentEnvironment` | `GameServer:EnvironmentId` | `GameServer:Store` | Công cụ dev |
| --- | --- | --- | --- | --- |
| `Local` | Development | `local` | `File` | bật |
| `Dev` | Development | `dev` | `Couchbase` | bật |
| `Beta` | Staging | `beta` | `Couchbase` | tắt |
| `Uat` | Staging | `uat` | `Couchbase` | tắt |
| `Live` | Production | `live` | `Couchbase` | tắt |

(Ánh xạ trong code: `StandardFlavors.DeploymentEnvironmentById`, `src/GameServer.Platform/DeploymentEnvironments.cs`.)
"Công cụ dev" = xác thực thử bằng header, module cheat, staff GM seed, OpenAPI GM, verifier IAP và identity provider giả.
Flavor sai hoặc không khớp → host dừng khi khởi động, không bao giờ âm thầm chạy như Development.

Chạy local một lệnh: `./tools/local/run-local.ps1` (Windows) hoặc `tools/local/run-local.sh` — cổng 5080, file store, không cần
Couchbase. Chi tiết: `README.md`, `docs/guides/environments-and-local-run.md`.

### 4.9 Việc chạy nền

- **Outbox sweeper** (`OutboxSweeper`): giao lại outbox chưa giao, mỗi 30 s, an toàn khi nhiều instance (consumer idempotent).
- **Lease job** (slice 010, `LeaseJobRunner`, contract `ILeaseJob` trong Core): job khai trong `ModuleDescriptor.Jobs`, mỗi lúc chỉ
  một instance giữ lease (document `leases::{job}`, fencing token).
- **Xoá tài khoản** (slice 010): player gửi `identity.request_deletion`; job `identity.account-purge` xoá/ẩn danh dữ liệu theo
  `DeletionTreatment` mà mỗi `CollectionSpec` bắt buộc khai (NoPlayerData / Anonymize / Remove / ModuleStep).

### 4.10 Quan sát và audit

- **Log**: file JSON theo instance (rolling, hàng đợi có giới hạn, che secret) — `src/GameServer.Platform.Observability`.
- **OpenTelemetry**: mỗi command là một span `command {operation}`; metric command, transaction, outbox. Gửi tới collector khi đặt
  `Observability:Otlp:Endpoint`.
- **Health**: `/health/live` (không kiểm dependency), `/health/ready` (kiểm store, analytics…).
- **Audit bền**: receipt (mọi command), ledger (Economy), `gm_audit` (mọi thao tác GM).
- **Analytics** (slice 012): file JSONL `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl` cho `login` và `payment`. Payment ghi
  ít nhất một lần (nằm trong outbox của transaction cấp quà); login best effort. Module dùng `ctx.AddAnalyticsEvent(...)`
  (`src/GameServer.Core/Analytics/AnalyticsEvents.cs`). Hướng dẫn cho data team: `docs/guides/analytics.md`.

### 4.11 Unity client SDK

Gói `sdk/unity/com.gameserver.client` (version 0.6.0), lớp chính `GameServerClient`
(`Runtime/Core/GameServerClient.cs`):

- Gắn header `X-Request-Id`, `X-Client-Version`, `X-Platform`, `X-Build-Id`, `Authorization: Bearer` cho mọi request.
- `SendCommandAsync(route, request, resultParser)`: serialize body **một lần**, retry gửi lại đúng bytes với cùng requestId → server
  replay, một hiệu ứng duy nhất.
- `Sections` (`SectionStore`): bản sao aggregate theo `rev`; tự refresh khi lệch.
- Sự kiện `SessionReplaced`, `AccountBlocked`, `Maintenance`, `UpdateRequired`, `ConfigVersionChanged`.
- `ServerNow`: giờ server theo đồng hồ monotonic.

Client mẫu Unity 6: `samples/SampleGame/KestrelDemo` (scene `SampleGameClient`). Contract: `docs/pcr/unity-sdk.md`.

### 4.12 GM API

GM API là JSON dưới `/gm/v1/...`, dùng chung pipeline command (receipt, idempotency, audit). Trang tĩnh tối thiểu `/gm/v1/ui`;
GM frontend đầy đủ do dev khác làm dựa trên `docs/api/gm-openapi.json` (test `tests/GameServer.Gm.Tests/OpenApiTests.cs` bắt bản commit
phải khớp host đang chạy).

## 5. Điểm mạnh (mỗi điểm kèm bằng chứng)

| Điểm mạnh | Cơ chế | Bằng chứng |
| --- | --- | --- |
| **Không mất, không nhân đôi giao dịch khi mạng chập chờn** | Receipt + replay theo requestId; SDK gửi lại đúng bytes; commit không rõ → 503 `COMMIT_UNKNOWN_RETRY`, retry trả kết quả đã commit | `tests/GameServer.Platform.Web.Tests/CommandPipelineTests.cs`: `Command_returns_section_update_and_replays_identically`, `Concurrent_identical_requests_have_one_effect`, `Ambiguous_commit_returns_503_and_retry_returns_committed_result`, `Timeout_after_commit_returns_503_and_retry_replays_without_second_effect` |
| **Không tiêu trùng số dư khi nhiều instance cùng ghi** | Đọc + kiểm + ghi trong một Couchbase transaction; xung đột write-write do transaction giải quyết | ACC-06 trên Couchbase EE 7.6.12, 2 instance: 20/20 vòng đúng một `OK` + một `INSUFFICIENT_FUNDS`, ledger khớp wallet (`docs/guides/local-real-env-testing.md` mục 6) |
| **Chịu tải dồn** | Async toàn bộ (analyzer cấm `.Wait()`/`.Result`), limiter concurrency + hàng đợi có số, mỗi player tuần tự | ACC-16 burst 150 command/s + 60 login/s: 100 % OK, p95 command 42–54 ms, p99 ≤ 180 ms; dừng êm giữa burst và kill -9 giữa burst: dữ liệu khớp 100/100, outbox pending 0 (cùng nguồn) |
| **Đăng ký tường minh, sai là không khởi động** | `ModuleRegistry.Validate` + `EndpointSecurityValidator` + `IStartupCheck`; mọi lỗi của một lần khởi động được báo **cùng lúc** | Ví dụ lỗi được bắt: command trùng tên, aggregate không tồn tại trong `Touches`, dải error code chồng nhau, thiếu upgrader, route thiếu `/v{n}/`, aggregate client-visible thiếu wire mapper, collection thiếu deletion treatment, store dev ở Production. Test: `tests/GameServer.Platform.Web.Tests/StartupValidationTests.cs` |
| **Mở rộng mà không sửa core** | `ReplacePolicy`, `ReplaceHandler`, `ServiceOnly`, `ICommandGuard`, `IGateEvaluator`, `IOutboxConsumer`, `IIdentityProviderAdapter` — handler thay thế vẫn chạy trong pipeline chung | `tests/Sample.Counter.Tests/ExtensionTests.cs`: `Game_can_replace_the_policy`, `Game_can_replace_the_handler_and_keeps_idempotency`, `Service_only_module_maps_no_endpoint_but_its_service_works`; bảng capability: `docs/pcr/platform.md` mục 10 |
| **Ranh giới kiến trúc được kiểm bằng test** | Test kiến trúc + banned symbols | `tests/GameServer.Architecture.Tests/ArchitectureTests.cs` (mục 4.2) |
| **Kiểm thử nhiều tầng** | Unit (in-memory store có tiêm lỗi, `FakeClock`), HTTP end-to-end (`tests/Shared/TestHost.cs`), kiến trúc, conformance store chạy trên in-memory / file / Couchbase, integration Couchbase thật, SDK chạy với server thật (`tests/GameServer.Client.Tests`), smoke build Unity IL2CPP (`sdk/unity/ci/smoke-build.ps1`) | Slice 012: 1174 test (in-memory 1089 pass + 85 skip vì cần Couchbase/điều kiện riêng); bộ Couchbase chạy ~23–25 phút (ghi chép `AgentMemories/features/p05-speckit/00_summary.md`) |
| **Một bản build cho mọi môi trường, an toàn mặc định** | Flavor; công cụ dev bị từ chối ngoài Development; secret chỉ từ biến môi trường; `Live` bắt buộc TLS | `docs/guides/environments-and-local-run.md` mục 1–2, PCR platform mục 17 |
| **Dev chạy local không cần hạ tầng** | File store + `run-local` một lệnh, dữ liệu giữ qua lần chạy | `tools/local/run-local.ps1`, `tests/Sample.Counter.Tests/SampleHostSmokeTests.cs` |
| **Audit và analytics đáng tin** | Audit GM và analytics payment nằm trong outbox của chính transaction | PCR platform mục 18, `docs/guides/analytics.md` |

## 6. Giới hạn hiện tại

| Giới hạn | Chi tiết |
| --- | --- |
| Chưa kiểm cluster Couchbase nhiều node | Mới chạy Couchbase EE 7.6.12 **một node** (2026-10-27) |
| Chưa kiểm sandbox thật của Apple/Google | Verifier IAP chạy với fake; ACC-47 sandbox thật thuộc P0.5 |
| Chưa kiểm thiết bị thật | Play Games / Game Center (ACC-03, ACC-29) và Unity trên Android/iOS (ACC-18): còn thủ công; chưa build iOS |
| TLS release gate chưa chạy | Test TLS Couchbase (opt-in) chưa chạy vì máy dev không có cluster TLS — bắt buộc trước khi lên `Live` |
| Đọc bundle trong transaction chưa đạt "≈ 1 RTT" | Mặc định đọc tuần tự từng aggregate; `Couchbase:UseGetMulti` tắt mặc định (PCR platform mục 2) |
| Chưa có giftcode, IAP refund/S2S, runbook/deploy/restore drill | Slice 013, 014, 015 |
| Chưa có Event, durable job (P1), GM frontend đầy đủ | P1; GM frontend do dev khác làm |
| Chưa đóng gói NuGet | Game dùng `ProjectReference` trong cùng repo |
| 010–012 chưa merge vào `main` | Nằm trên nhánh của slice |
| Giới hạn thiết kế (cố ý) | ≤ 16 aggregate/game; `Touches` ≤ 4; claim marker ≤ 2; aggregate ≤ 64 KiB; receipt ≤ 4 KiB; result ≤ 512 B; replay window 24 h |

## 7. Đọc tiếp

- Thêm command mới, đường đi của một request: **trang 5b** (`02-them-command.html`).
- Viết module: `docs/guides/module-authoring.md`, `templates/module/README.md`.
- Môi trường, chạy local, Couchbase, provisioning: `docs/guides/environments-and-local-run.md`.
- Kiểm thử như môi trường thật: `docs/guides/local-real-env-testing.md`.
- Analytics: `docs/guides/analytics.md`.
- Contract: `docs/pcr/platform.md` (transaction, idempotency, outbox, error code, extensible capabilities), `docs/pcr/sample-counter.md`,
  `docs/pcr/unity-sdk.md`.
- Yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md`.
