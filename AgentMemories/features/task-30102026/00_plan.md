# Plan — 5 task của owner (`tasks/task_30102026.txt`), 2026-10-30

Chỉ là kế hoạch. Chưa sửa code. Slice 010 (xoá tài khoản) đang được làm trên cùng working tree. Mọi subtask đụng `src/`,
`tests/` hay `docs/pcr/` chỉ bắt đầu **sau khi 010 merge vào `main`**.

## 0. Tóm tắt khuyến nghị

| Task | Khuyến nghị | Size | Spec Kit? |
| --- | --- | --- | --- |
| 1 Môi trường theo flavor | Flavor = `ASPNETCORE_ENVIRONMENT` + `appsettings.{Flavor}.json`. Không thêm khái niệm mới: `EnvironmentId` = tên flavor, `DeploymentEnvironment` map như bảng 1.3. Store mới `LocalFile` = `InMemoryDocumentStore` + lưu file theo từng document + journal cho mỗi commit. Bỏ `dbcache` vì không có chỗ nào dùng. Mở rộng `CouchbaseOptions` cho timeout, TLS và compression. | L | Có (gộp với task 2) |
| 2 Chạy local | Profile `local` trong `launchSettings.json`, key `Urls` (không dùng `Kestrel:Endpoints`) cổng 5080, script `tools/local/run-local.ps1` và `.sh` | S | Gộp vào slice của task 1 |
| 3 Audit log Login và Payment | Một phần sớm của **ANA-01**. Payment đi qua outbox trong transaction IAP (at-least-once, dedupe theo `transactionID`). Login ghi sau khi commit, best effort. Sink mặc định là file JSONL riêng cho từng instance, chia file theo ngày UTC. Mỗi bản ghi có cả thời điểm UTC lẫn giờ địa phương kèm offset và tên múi giờ IANA. | M | Có |
| 4 Đóng gói thành library | Private NuGet: các package `GameServer.*` dùng chung một version (lockstep, ARC-08). Thêm package `GameServer.Templates` (`dotnet new`) và `GameServer.Build` (analyzer và banned API cho game). Unity SDK phát hành qua UPM git URL theo tag. CI pack rồi build một consumer từ chính các package vừa đóng gói. | L | Có (kèm ADR-0002) |
| 5 Tài liệu HTML | Ba file HTML tự chứa trong `docs/html/`, không dùng CDN, sơ đồ bằng SVG inline. 5a và 5b viết ngay sau khi task 1 và 2 xong. 5c có bản nháp sau task 4, slice 013 bổ sung runbook và drill. | L | Không (chỉ là docs). Bắt buộc review độ chính xác so với code |

Tổng ước lượng: **≈ 21–30 ngày-người** (bảng ở mục 8). Phần lớn nằm ở task 1 (file store), task 4 (packaging + CI) và task 5
(phải đúng với code).

## 1. Task 1 — Môi trường theo flavor (local / dev / beta / uat / live)

### 1.1 Hiện trạng

- `src/GameServer.Platform/GameServerOptions.cs`:
  - `StoreKind { InMemory, Couchbase }`.
  - `DeploymentEnvironment` nhận Development, Staging, Production hoặc Test (OPS-01). `IsDevelopment`/`IsTest` mở dev tools.
  - `EnvironmentId` là nhãn tự do (ARC-09).
- `GameServerBuilder.cs:118-120`: nếu `GameServer:DeploymentEnvironment` không được đặt thì lấy `IHostEnvironment.EnvironmentName`.
  Vì vậy flavor `Local` hay `Dev` mà quên đặt key này sẽ thành môi trường "không phải dev", và store in-memory/file bị từ chối
  khi khởi động. Fail-fast như vậy là đúng, nhưng cần ghi rõ trong tài liệu.
- `src/GameServer.Platform.Couchbase/CouchbaseOptions.cs` có `ConnectionString`, `Username`, `Password`, `BucketName`,
  `UseGetMulti`, `ProvisionOnStartup`. `CouchbaseConnection.ConnectAsync` mới chỉ đặt `KvDurabilityTimeout`, các cấu hình
  transaction và serializer. **Thiếu**: `KvTimeout`, `QueryTimeout`, TLS (đường dẫn CA, `couchbases://`), compression,
  số KV connection.
- Dữ liệu được phân vùng theo DAT-01: **một bucket cho mỗi environment**, scope `g_<gameId>`, mỗi module một collection.
  `dbbase` trong ví dụ của owner tương ứng với `Couchbase:BucketName`.
- `src/GameServer.Platform.Testing/InMemoryDocumentStore.cs` (476 dòng) đã implement **toàn bộ** các contract:
  - transaction optimistic có retry;
  - `IKeyValueStore` có expiry;
  - `IPagedQueryStore`, `IMaintenanceQueryStore` (`QueryDueAsync`, `QueryReferencesAsync` cho purge của 010);
  - transaction-only collections;
  - `IDevelopmentOnlyStore`: startup từ chối store này ngoài Development/Test (`GameServerApplicationExtensions.cs:128`).

  Store này mất toàn bộ dữ liệu khi restart. Đó là gap thật của "local".
- `samples/SampleHost/Program.cs` tự chọn store bằng `if (Store == Couchbase)`. Hiện chỉ có `appsettings.json`, không có file
  `appsettings.{Env}.json` nào.
- Framework **không có** khái niệm bucket cache dùng chung. `ProfileCache` và `ExpiringCache` là cache in-memory trong từng
  instance. Session lưu ở collection `sessions` có TTL.

### 1.2 Gap

1. Chưa có store local lưu bền qua restart.
2. Chưa có file cấu hình riêng cho từng flavor.
3. Thiếu các tham số Couchbase cho production: timeout, TLS, compression.
4. Bảng tham số của owner (memcached, `moxiPort`, `serverIp`) không khớp với SDK 3.x.
5. Chưa có lệnh provisioning cho Production (DAT-07 yêu cầu "lệnh ops ở Production"). Hiện chỉ có cách đặt
   `ProvisionOnStartup=true`.

### 1.3 Đề xuất map flavor

| Flavor | `ASPNETCORE_ENVIRONMENT` | `GameServer:DeploymentEnvironment` | `EnvironmentId` | `Store` | Couchbase |
| --- | --- | --- | --- | --- | --- |
| local | `Local` | Development | `local` | `LocalFile` | — |
| dev | `Dev` | Development | `dev` | `Couchbase` | bucket `gs-dev` (replicas 0 nếu một node) |
| beta | `Beta` | Staging (xem Q3) | `beta` | `Couchbase` | bucket `gs-beta` |
| uat | `Uat` | Staging | `uat` | `Couchbase` | bucket `gs-uat` |
| live | `Live` | Production | `live` | `Couchbase` | cluster riêng (DAT-01) |

- Ở flavor `dev`, `IsDevelopment` vẫn là true nên cheat, test auth và GM seed vẫn bật. Nếu owner muốn `dev` "giống thật" thì
  đổi `DeploymentEnvironment` thành Staging (Q2).
- Game config overlay vẫn theo `config/env/<DeploymentEnvironment>` như hiện tại. Hệ quả là beta và uat dùng chung overlay
  Staging (Q4).

### 1.4 Map tham số trong ví dụ của owner

| Tham số của owner | Trong framework | Ghi chú |
| --- | --- | --- |
| `dbbase.bucketName` | `Couchbase:BucketName` | Một bucket cho mỗi environment, scope `g_<gameId>` |
| `bucketType: couchbase` | — | Bắt buộc là bucket Couchbase để dùng transaction (DAT-04). Storage backend (Couchstore hay Magma) là tham số của script provisioning, không phải của app |
| `serverIp` | `Couchbase:ConnectionString` | `couchbase://ip1,ip2`. Dùng `couchbases://` khi bật TLS (SEC-01) |
| `operationTimeOut: 2000` | **mới** `Couchbase:KvTimeoutMs` (mặc định SDK 2500), `QueryTimeoutMs` | KV durable timeout đã có: `GameServer:Transactions:KvDurableTimeoutSeconds` |
| `compressThreshold: 2048` | **mới** `Couchbase:Compression` / `CompressionMinSizeBytes` | [VERIFY] tên property trong SDK 3.10 (`ClusterOptions.Compression`, `CompressionMinSize`) |
| `moxiPort` | bỏ | Moxi không còn từ Couchbase Server 5.x. SDK 3 bootstrap qua connection string (KV 11210, TLS 11207) |
| `dbcache` (memcached) | bỏ | Không có consumer. Memcached bucket đã deprecated ở 7.x [VERIFY bị gỡ ở 8.0]. Nếu sau này cần cache dùng chung thì dùng ephemeral bucket hoặc collection có TTL (Q1) |
| `localFolder` | **mới** `LocalStore:Folder` (mặc định `../_database/dbbase`, tính từ content root) | Thêm `_database/` vào `.gitignore` |
| (thiếu) | `Couchbase:Username`. `Couchbase:Password` chỉ lấy từ biến môi trường (OPS-08) | |
| (thiếu) | **mới** `Couchbase:TrustCertificatePath` (CA tự ký), `NumKvConnections` | |

### 1.5 Phương án cho file store local

| Phương án | Ưu | Nhược |
| --- | --- | --- |
| **A. `InMemoryDocumentStore` + persistence hook (khuyến nghị)** | Dùng lại 100 % ngữ nghĩa đã qua conformance suite (transaction, query, TTL, purge). Code mới chỉ là load lúc khởi động và ghi file khi commit | Cần thêm một hook công khai vào `InMemoryDocumentStore` (đụng file mà 010 vừa sửa) |
| B. Store mới đọc/ghi trực tiếp từ file | Không đụng InMemory | Viết lại transaction và các query, rất dễ lệch ngữ nghĩa với Couchbase |
| C. Couchbase Server thật chạy Docker | Giống thật 100 % | Không phải "file giả lập". Máy dev hiện không có Docker (ADR-0001) |

**Layout của phương án A:**

```
_database/dbbase/
  g_<gameId>/<collection>/<key đã escape>.json   = { "content": {...}, "expiresAtUtc": null }
  _journal.jsonl
  .lock
```

- **Một file cho mỗi document**, để dev mở ra xem và sửa được giống trên Couchbase UI.
- **Ghi một commit:**
  1. Append toàn bộ batch của commit vào `_journal.jsonl` rồi flush.
  2. Ghi từng document: viết file tạm, sau đó rename.
  3. Truncate journal.
- **Khi khởi động:** load mọi file, rồi replay phần journal còn lại. Một commit nhiều document vì vậy vẫn nguyên tử khi process
  bị crash.
- **`.lock`** được mở exclusive. Host thứ hai trỏ vào cùng thư mục thì fail-fast. Muốn chạy nhiều instance thì dùng Couchbase
  qua `start-hosts.ps1`.
- Store vẫn là `IDevelopmentOnlyStore`.

### 1.6 Subtask

| ID | Việc | File | Done khi | Agent | Phụ thuộc | Size |
| --- | --- | --- | --- | --- | --- | --- |
| T1.1 | Thiết kế: vị trí project của file store (đề xuất project mới `src/GameServer.Platform.LocalStore` tham chiếu `Platform.Testing`; hoặc đặt luôn trong `Platform.Testing`), API hook (`ICommitObserver` + `Restore`), format file, cách escape key trên Windows (`::`, `/`), các option mới của Couchbase | spec slice mới `specs/0NN-local-env/` | spec, plan và data-model được duyệt. Q1–Q5 đã chốt | system-architect | 010 merge, Q1–Q5 | M |
| T1.2 | Hook commit/restore trong `InMemoryDocumentStore` (cả transaction lẫn ghi KV, expiry) | `src/GameServer.Platform.Testing/InMemoryDocumentStore.cs` | conformance suite hiện có vẫn pass. Hook được gọi đúng một lần cho mỗi commit, với đúng tập document | code-implementer | T1.1 | S |
| T1.3 | `LocalFileDocumentStore` gồm load, journal, lock, `AddLocalFileDocumentStore(options)`, `StoreKind.LocalFile` | `src/GameServer.Platform.LocalStore/*`, `GameServerOptions.cs`, `GameServer.slnx` | Ghi rồi dispose rồi mở lại thì dữ liệu bằng nhau. Journal bị cắt giữa commit được replay hoặc bỏ đúng. Host thứ hai trên cùng thư mục fail với thông báo rõ. Startup từ chối khi `DeploymentEnvironment=Production` | code-implementer | T1.2 | M |
| T1.4 | Chạy conformance suite và test purge của 010 trên file store | `tests/GameServer.Platform.Tests/*` | Toàn bộ suite xanh trên cả 3 store | code-implementer | T1.3 | S |
| T1.5 | Thêm option Couchbase (`KvTimeoutMs`, `QueryTimeoutMs`, TLS CA, compression, `NumKvConnections`) và validate lúc startup | `CouchbaseOptions.cs`, `CouchbaseConnection.cs` | Giá trị sai thì startup fail và message nêu đúng key. Integration test trên Couchbase 7.6 pass | code-implementer | T1.1 | S |
| T1.6 | Các file `appsettings.{Local,Dev,Beta,Uat,Live}.json` cho SampleHost, không chứa secret (OPS-08 tự kiểm). `Program.cs` thêm nhánh `LocalFile` | `samples/SampleHost/*` | Mỗi flavor khởi động được (Couchbase dùng env var). Live thiếu secret thì fail | code-implementer | T1.3, T1.5 | S |
| T1.7 | Lệnh provisioning cho ops (`SampleHost --provision-only`, hoặc tool riêng) để Production không phải bật `ProvisionOnStartup` | `src/GameServer.Platform.Couchbase/CouchbaseProvisioner.cs`, host | Lệnh tạo scope, collection và index rồi thoát với exit 0, chạy lại vẫn idempotent. Có thể dời sang 013 (Q6) | code-implementer | T1.5 | S |
| T1.8 | Review | các file đã đổi | Không còn Blocker hay Major | tech-lead-reviewer | T1.2–T1.7 | S |

### 1.7 Rủi ro

| Rủi ro | Impact | Giảm thiểu |
| --- | --- | --- |
| Hook làm lệch ngữ nghĩa InMemory, khiến cả 400+ test bị ảnh hưởng | High | Hook chỉ quan sát, không thay đổi luồng. Chạy full suite trước khi merge |
| Ghi file nằm trong lock của InMemory làm commit chậm | Low (chỉ ở dev) | Đo burst nhỏ. Chấp nhận khoảng ms |
| Dev sửa file JSON khi server đang chạy, rồi bị server ghi đè | Med | Ghi rõ trong tài liệu "sửa khi server đã dừng". Không làm hot-reload (Q5) |
| Quên đặt `DeploymentEnvironment` trong `appsettings.{Flavor}.json` | Med | Validate `DeploymentEnvironment` thuộc tập cho phép, kèm thông báo gợi ý cách sửa |

## 2. Task 2 — Chạy server ở chế độ local

### 2.1 Hiện trạng và gap

- `dotnet run --project samples/SampleHost` đã chạy được với store InMemory trên cổng mặc định 5000. Dữ liệu mất khi dừng.
- `tools/local/start-hosts.ps1` chạy N instance trên Couchbase, cổng 5080+, và truyền `--urls`.
- Chưa có `Properties/launchSettings.json`, chưa có profile, chưa có script "một lệnh" cho flavor local. Unity E2E đang dùng
  `http://localhost:5080/`.

### 2.2 Đề xuất

- Đặt cổng bằng key `"Urls": "http://localhost:5080"` trong `appsettings.Local.json`.
  - **Không** dùng `Kestrel:Endpoints`, vì cấu hình endpoint của Kestrel thắng cả `--urls` và sẽ phá `start-hosts.ps1`.
  - Với key `Urls`, tham số `--urls` trên command line vẫn ghi đè được.
- `launchSettings.json` có hai profile:
  - `local`: `ASPNETCORE_ENVIRONMENT=Local`, mở trình duyệt vào `/gm/v1/ui`;
  - `dev-couchbase`: flavor Dev, password lấy từ biến môi trường user.
- Script `tools/local/run-local.ps1` và `run-local.sh`:
  - tham số `-Port 5080`, `-Reset` (xoá `_database`), `-Lan` (bind `0.0.0.0` để thử trên thiết bị thật, có in cảnh báo);
  - build, chạy, chờ `/health/ready`, rồi in URL của API, GM UI và OpenAPI.

### 2.3 Subtask

| ID | Việc | File | Done khi | Agent | Phụ thuộc | Size |
| --- | --- | --- | --- | --- | --- | --- |
| T2.1 | `launchSettings.json` (profile `local`, `dev-couchbase`) và `Urls` trong `appsettings.Local.json` | `samples/SampleHost/Properties/launchSettings.json`, `appsettings.Local.json` | `dotnet run --launch-profile local` nghe ở `localhost:5080`, dữ liệu còn sau restart. `start-hosts.ps1 -Count 2` vẫn chạy được ở 5080/5081 | code-implementer | T1.6 | S |
| T2.2 | `run-local.ps1` và `run-local.sh` | `tools/local/` | Trên máy sạch: một lệnh, sau ≤ 60 s `/health/ready` trả 200. `-Reset` xoá dữ liệu. Port đã bị chiếm thì báo lỗi rõ, exit ≠ 0 | code-implementer | T2.1 | S |
| T2.3 | Cập nhật `README.md` (mục chạy local) và `docs/guides/local-real-env-testing.md`. URL mặc định của KestrelDemo trỏ 5080 | `README.md`, `docs/guides/*`, `samples/SampleGame/KestrelDemo/...` (không grep đệ quy `samples/`) | Hướng dẫn chỉ còn một đường chạy local | code-implementer | T2.2 | S |
| T2.4 | Review gộp chung với T1.8 | | | tech-lead-reviewer | | — |

Rủi ro: `-Lan` để lộ GM UI có staff seed ra mạng LAN (Med). Giảm thiểu: `-Lan` in cảnh báo, và Q7 quyết định có cho phép hay
không.

## 3. Task 3 — Audit log Login và Payment (template mặc định)

### 3.1 Hiện trạng

- **Payment:**
  - `src/GameServer.Modules.Iap/IapModel.cs`: `IapPurchaseHandler` chạy trong command transaction.
  - Đã có `VerifiedPurchase(Store, ProductId, ExternalReference = Google purchaseToken | Apple transactionId, OrderId)` và
    claim `extref::iap_{store}::{reference}`, nên mỗi giao dịch chỉ grant một lần.
  - Ledger (`EconomyOperations`, outbox → collection `ledger`) đã ghi `LedgerSource(iap, ExternalReference, OrderId)`.
  - **Chưa có** cờ `IsTest`/sandbox trong `VerifiedPurchase`.
- **Login:** `src/GameServer.Modules.Identity.Adapters/LoginCompletion.cs` là điểm chung của mọi kiểu login (guest, provider,
  switch). Login **không** phải command: nó đi qua `IPlayerAggregateWriter.UpdateAsync`, không có receipt và không có outbox.
  Hiện chỉ có một dòng log vận hành `"Login of player ..."`.
- **Outbox:** `OutboxRelay` nhận thêm `IOutboxConsumer` tuỳ ý qua DI (`OutboxRelay.cs:73`), một consumer cho mỗi kind. Giao nhận
  at-least-once: relay inline, kèm sweeper.
- **Log vận hành:** Serilog compact JSON có queue drop (LOG-06). LOG-06 ghi rõ "audit/ledger không phụ thuộc log buffer", nên
  **không** ghi analytics qua log vận hành.
- **Requirement:** ANA-01 (SHOULD, P1): "Analytics event nghiệp vụ xuất qua outbox tới pipeline analytics". **Task 3 là một phần
  sớm, hẹp của ANA-01.** Seam dựng ở đây (sink + outbox kind) sẽ được P1 mở rộng thêm event.

### 3.2 Phương án sink

| Phương án | Ưu | Nhược |
| --- | --- | --- |
| **A. File JSONL riêng cho từng instance, chia theo ngày UTC (khuyến nghị)** | Giống format team đã quen (Sniper PVP). Log shipper (Vector, Fluent Bit, Filebeat) đẩy vào data lake/Spark. Không thêm write vào DB | Cần volume hoặc sidecar (giống LOG-03). PRIV-01 không viết lại được các file này |
| B. Collection Couchbase `analytics_events` + job export | Bền, truy vấn được, purge của 010 xử lý được (`DeletionTreatment.Anonymize`) | Thêm write amplification vào mọi login. Vẫn cần job export |
| C. stdout để log shipper đọc | Đơn giản nhất | Lẫn với log vận hành, mất dữ liệu khi queue đầy |
| D. Ghi thẳng vào pipeline (Kafka/HTTP) | Thời gian thực | Thêm dependency ngoài, cần retry. Để dành cho P1 qua cùng interface |

**Khuyến nghị A**, đặt sau interface `IAnalyticsSink`. Phương án B và D thành các implementation khác của interface này ở P1.

### 3.3 Thiết kế đề xuất (architect chốt)

- **Payment:** `IapPurchaseHandler` gọi `ctx.AddOutbox("analytics.payment", {...})` trong cùng transaction với grant.
  - Mã nghiệp vụ chỉ dùng Core API, nên ARC-01 vẫn sạch.
  - `AnalyticsOutboxConsumer` (Platform) ghi vào sink.
  - Có thể ghi trùng (relay chạy lại). Pipeline dedupe theo `transactionID` hoặc `eventId`, đúng như owner đã định.
  - Replay của client không chạy lại handler, nên không sinh event mới.
- **Login:** `LoginCompletion.CompleteAsync` gọi `IAnalyticsSink.Emit(login)` **sau khi** đã tạo session.
  - Best effort, qua channel có giới hạn, có metric `analytics_dropped_total`.
  - Mất một event login không làm sai UL (UL tính trên payment). Với retention, sai số rất nhỏ (tính theo số ngày distinct).
- **Bẫy:** SDK cho phép game lưu session token (`Abstractions.cs:47`, session sống 30 ngày, sliding). Game nào lưu token thì
  **không gọi Login mỗi ngày**, nên `active_days > 3` sẽ bị đếm thiếu. Đề xuất phát thêm event `login` với `source="resume"` ở
  request có xác thực đầu tiên của player trong mỗi ngày (theo múi giờ của deployment), dedupe trong từng instance bằng
  `ExpiringCache`. Q10 quyết định.
- **Schema v1:** tên field theo đúng bảng của owner, cộng thêm các field truy vết.

  ```json
  {"event":"payment","schemaVersion":1,"eventId":"<outbox entryId>","gameId":"sample","environmentId":"live",
   "userID":"p_…","transactionID":"google:GPA.1234-…","chargeTime":"2026-10-30T21:05:09.123+07:00",
   "chargeTimeUtc":"2026-10-30T14:05:09.123Z","timeZone":"Asia/Ho_Chi_Minh","utcOffset":"+07:00",
   "store":"google","productId":"gems_100","orderId":"GPA.1234-…","isTest":false,"receiptId":"…"}
  {"event":"login","schemaVersion":1,"eventId":"…","gameId":"sample","environmentId":"live","userID":"p_…",
   "activeTime":"2026-10-30T08:00:01.000+07:00","activeTimeUtc":"2026-10-30T01:00:01.000Z",
   "timeZone":"Asia/Ho_Chi_Minh","utcOffset":"+07:00","provider":"guest","source":"login","isNewUser":false}
  ```

  - `timeZone` là tên IANA, cấu hình cho mỗi deployment (`GameServer:Analytics:TimeZone`). Offset tính **cho từng event**, nên
    múi giờ có giờ mùa hè (EU +0/+1) vẫn đúng.
  - `chargeTime` là thời điểm commit grant trên server (`CommittedAtUtc` của receipt), không phải thời điểm mua phía store (Q9).
- **File:** `{Analytics:Directory}/{gameId}-{instanceId}-{event}-{yyyyMMdd UTC}.jsonl`, mỗi dòng một event, flush theo dòng hoặc
  theo lô 1 s.
  - Writer tự viết, lấy ngày từ `IClock`. **Không** dùng rolling của Serilog, vì Serilog roll theo giờ địa phương của máy
    (`DateTime.Now`), nên trên máy Windows của dev, ranh giới file sẽ không trùng ngày UTC.

### 3.4 Subtask

| ID | Việc | File | Done khi | Agent | Phụ thuộc | Size |
| --- | --- | --- | --- | --- | --- | --- |
| T3.1 | Spec và plan (ANA-01 phần 1). Chốt schema, sink, `isTest`, resume event, retention | `specs/0NN-analytics-audit/` | Q8–Q12 đã chốt. Spec trích dẫn ANA-01, LOG-03, LOG-05, LOG-06, PRIV-01 | system-architect | 010 merge | S |
| T3.2 | `IAnalyticsSink`, `JsonlFileAnalyticsSink` (bounded channel, ngày UTC theo `IClock`, metric drop và lỗi, health degraded khi ghi lỗi), section `GameServer:Analytics` (`Enabled`, `Directory`, `TimeZone`). Startup fail nếu `TimeZone` không resolve được | `src/GameServer.Platform/Analytics/*` (hoặc `Platform.Observability`) | Unit test: offset đúng cho `Asia/Ho_Chi_Minh`, `Asia/Taipei`, `Europe/London` (cả hai phía DST). Qua 00:00 UTC thì sang file mới. Queue đầy thì drop và đếm, không chặn request | code-implementer | T3.1 | M |
| T3.3 | Event Payment: thêm `IsTest` vào `VerifiedPurchase` (Google `purchaseType`, Apple `environment`, fake verifier), `AddOutbox("analytics.payment")`, đăng ký `AnalyticsOutboxConsumer` | `IapModel.cs`, `GooglePlayReceiptVerifier.cs`, `AppleJwsReceiptVerifier.cs`, `FakeStoreReceiptVerifier.cs`, `IapModule.cs` | HTTP test: mua hàng ra đúng một dòng, đủ field. Replay cùng requestId không sinh thêm dòng. Relay chạy lại thì dòng mới có cùng `eventId`/`transactionID`. Reject (`ALREADY_GRANTED`) không sinh dòng | code-implementer | T3.2 | S |
| T3.4 | Event Login (và resume nếu Q10 = có) | `LoginCompletion.cs`, `PlayerBearerAuthenticationHandler.cs` (resume) | HTTP test: guest login và provider login mỗi lần ra một dòng. Login bị từ chối (ban, deleted) không ra dòng. Resume ra tối đa một dòng cho mỗi player, mỗi ngày, mỗi instance | code-implementer | T3.2 | S |
| T3.5 | PCR và hướng dẫn: format file là semantic với pipeline data. Ghi schema version, quy tắc dedupe, cách ship log | `docs/pcr/platform.md`, `docs/pcr/iap.md`, `docs/pcr/identity.md` | PCR có bảng field, đơn vị thời gian, múi giờ, at-least-once | code-implementer | T3.3, T3.4 | S |
| T3.6 | Review, đặc biệt LOG-05: không có token/receipt trong event | | Không còn Blocker hay Major | tech-lead-reviewer | T3.5 | S |

### 3.5 Rủi ro

| Rủi ro | Impact | Giảm thiểu |
| --- | --- | --- |
| Container chạy chế độ globalization-invariant hoặc thiếu tzdata, nên `TimeZoneInfo` không tìm được tên IANA | High (live fail khi khởi động) | Kiểm lúc startup (fail-fast). Hướng dẫn deploy (5c) ghi rõ image cần `tzdata`/ICU |
| Mất file JSONL khi pod bị thay | High | Cùng chính sách với LOG-03: volume bền hoặc sidecar ≤ 60 s. Đây là mục trong checklist deploy |
| Có giao dịch test hoặc sandbox lẫn vào doanh thu | Med | Field `isTest`, kèm `environmentId`. Data team lọc |
| PRIV-01: file đã ship vẫn còn `userID` sau khi tài khoản bị xoá | Med (pháp lý) | Q11: chấp nhận vì `playerId` là id nội bộ không phải PII, kèm retention, hoặc hash `userID` |
| Không có event login khi game lưu token | Med | Resume event (Q10) |

## 4. Task 4 — Đóng gói core thành library

### 4.1 Hiện trạng

- Monorepo `GameServer.slnx`: 14 project trong `src/`, `sdk/GameServer.Client`, `modules/Sample.Counter`, `templates/module`
  (copy tay, đọc `templates/module/README.md`), `samples/SampleHost`. Tất cả nối với nhau bằng `ProjectReference`.
- Không có `Version`, `PackageId`, `IsPackable`, SourceLink. **Không có thư mục `.github/` (chưa có CI nào).** Như vậy CI-01 của
  P0 chưa có pipeline thật.
- Requirement đã chốt: ARC-08 (MUST, P0):
  - private NuGet, lockstep SemVer;
  - `Module.Contracts` bật PublicAPI analyzer;
  - CI build game mẫu từ chính package vừa đóng gói.

  SDK-01: UPM versioned lockstep với `.proto`.
- Unity SDK: `sdk/unity/com.gameserver.client/package.json` version `0.6.0`. Remote là GitHub
  (`hung-bui-121/game-server-dotnet-kestrel`).
- Analyzer TIME-01/PERF-01 (`BannedSymbols.txt`) hiện chỉ áp dụng trong repo này qua `Directory.Build.props`. Game project dùng
  package sẽ **mất** các ràng buộc đó nếu không ship kèm.
- GM UI là embedded resource (`GameServer.Modules.Gm.Adapters.csproj:14`), nên đóng package được.

### 4.2 Phương án

| Phương án | Cập nhật bản sửa lỗi | Ưu | Nhược |
| --- | --- | --- | --- |
| **A. Private NuGet + `dotnet new` template (khuyến nghị)** | Maintainer tag `v1.2.3`, CI publish. Project tăng version (Dependabot/Renovate tự mở PR) | Đúng ARC-08. Version rõ ràng, rollback được. Game không sửa trộm được core. SourceLink cho phép debug vào code framework | Cần feed và quyền truy cập. Mỗi public type trở thành contract |
| B. Git submodule hoặc subtree + `ProjectReference` | `git submodule update` | Debug và sửa trực tiếp | Game dễ sửa core rồi lệch khỏi framework. Không có version. Merge conflict. Vi phạm ARC-08 |
| C. GitHub template repo (fork) | Không có đường cập nhật | Bắt đầu nhanh | Đúng điều owner muốn tránh |

**Khuyến nghị A.** Có thể bổ sung "chế độ dev của maintainer": một file `Directory.Build.local.props` không commit, đổi
`PackageReference` thành `ProjectReference` tới bản checkout framework, khi cần debug sâu.

- Feed mặc định đề xuất: **GitHub Packages**, vì repo đang ở đó (Q13).
- Unity SDK: **UPM git URL** dạng `https://github.com/<org>/<repo>.git?path=sdk/unity/com.gameserver.client#v1.2.3`. Thay thế
  được bằng tarball đính kèm release hoặc scoped registry (Verdaccio).

### 4.3 Package dự kiến (một version lockstep)

- **Core và platform:** `GameServer.Core`, `GameServer.Platform`, `GameServer.Platform.Web`, `GameServer.Platform.Couchbase`,
  `GameServer.Platform.Observability`, `GameServer.Platform.Testing`, `GameServer.Platform.LocalStore` (từ task 1).
- **Module:** `GameServer.Modules.{Identity,Economy,Iap,Gm}` và `.Adapters` của từng module.
- **Client:** `GameServer.Client`.
- **`GameServer.Build`:** buildTransitive props gồm BannedApiAnalyzers, `BannedSymbols.txt`, `TreatWarningsAsErrors` được
  khuyến nghị, đường dẫn import `.proto` của `proto/gameserver/*.proto` cho Grpc.Tools.
- **`GameServer.Templates`:**
  - `dotnet new gs-game`: host, `config/`, `appsettings.{flavor}.json`, `launchSettings`, `run-local`, project test, arch test
    mẫu;
  - `dotnet new gs-module`: tạo từ `templates/module`.
- `samples/`, `tests/`, `tools/` đặt `IsPackable=false`.

### 4.4 Subtask

| ID | Việc | File | Done khi | Agent | Phụ thuộc | Size |
| --- | --- | --- | --- | --- | --- | --- |
| T4.1 | ADR-0002 "Packaging và phân phối": danh sách package, policy SemVer (0.x đến P1, breaking = major sau 1.0), changelog, nhánh `release/x.y` để backport hotfix, feed, chế độ dev của maintainer, cách game override | `docs/adr/0002-packaging.md`, spec slice | Q13–Q16 đã chốt. ADR Accepted | system-architect | 010 merge | M |
| T4.2 | Metadata pack: `Version` lockstep (một property), `IsPackable` theo thư mục, `PackageId`, README, SourceLink, `snupkg`, `ContinuousIntegrationBuild` | `Directory.Build.props`, `Directory.Packages.props`, các csproj | `dotnet pack -c Release` ra đủ N `.nupkg` và `.snupkg` cùng version. Build vẫn sạch | code-implementer | T4.1 | M |
| T4.3 | Package `GameServer.Build` (analyzer, banned API, import proto) | `build/GameServer.Build/*` | Consumer gọi `DateTime.UtcNow` trong module thì build lỗi. Consumer `import "gameserver/…proto"` thì compile được | code-implementer | T4.2 | S |
| T4.4 | Package `GameServer.Templates` (`gs-game`, `gs-module`), viết lại từ SampleHost và `templates/module` | `templates/*` | `dotnet new gs-game -n Foo && dotnet run --launch-profile local` chạy được trên feed local. Test của template pass | code-implementer | T4.2, T2.1 | M |
| T4.5 | CI GitHub Actions: `ci.yml` (build, test, arch, integration nếu có Couchbase service, pack, rồi build consumer từ feed thư mục local) và `release.yml` (tag `v*` thì push package lên feed và tạo release kèm tarball UPM) | `.github/workflows/*` | PR chạy xanh. Tag thử `v0.7.0-preview.1` publish được. Consumer build từ package (ARC-08) | code-implementer | T4.2–T4.4 | M |
| T4.6 | Unity SDK: version lockstep (`package.json` = version NuGet), hướng dẫn UPM git URL, kiểm `smoke-build.ps1` với git URL | `sdk/unity/com.gameserver.client/package.json`, `sdk/unity/ci/*` | KestrelDemo cài SDK bằng git URL theo tag và build smoke pass | code-implementer | T4.5 | S |
| T4.7 | PublicAPI analyzer (`PublicAPI.Shipped.txt`) cho các package contract (Core, `*.Contracts`, nếu có) | các csproj | Đổi public API mà không cập nhật file thì build fail | code-implementer | T4.2 | S |
| T4.8 | Hướng dẫn cho maintainer và game team: tạo project, cập nhật version, backport hotfix | `docs/guides/consuming-framework.md` (và tham chiếu trong 5a/5c) | Một game dev mới làm theo được trong ≤ 30 phút | code-implementer | T4.4–T4.6 | S |
| T4.9 | Review | | Không còn Blocker hay Major | tech-lead-reviewer | T4.2–T4.8 | M |

### 4.5 Rủi ro

| Rủi ro | Impact | Giảm thiểu |
| --- | --- | --- |
| Public surface rất lớn và chưa ổn định, nên version nào cũng có breaking | High | Giữ 0.x đến P1. PublicAPI analyzer. Ghi migration note trong changelog |
| Game team cần PAT để đọc GitHub Packages (kể cả trong CI của game) | Med | Q13: dùng feed nội bộ của công ty nếu có (Nexus, Artifactory, Azure Artifacts) |
| Đổi mọi csproj cùng lúc thì conflict với 011 đang làm | Med | Làm task 4 khi không có slice nào khác đang mở (mục 7) |
| Game mất các ràng buộc analyzer | Med | Package `GameServer.Build` bắt buộc trong template |
| `.proto` của framework và của game lệch version | Med | `.proto` ship trong package cùng version. Thêm `buf breaking` vào CI (CI-01) |

## 5. Task 5 — Tài liệu HTML

### 5.1 Hiện trạng

- Markdown: `README.md`, `docs/guides/module-authoring.md` (đường thêm command ở mức code), `docs/guides/local-real-env-testing.md`,
  `docs/pcr/*` (contract), `docs/adr/0001`.
- Chưa có HTML, chưa có tài liệu giới thiệu hay kiến trúc tổng thể, chưa có tài liệu deploy.
- Slice **013 ops-readiness** sẽ làm: Linux/container guide, runbook, deploy/rollback, restore drill, secret scan, PERF budget,
  load test.

### 5.2 Đề xuất

- `docs/html/index.html` và ba file:
  - `01-gioi-thieu.html`
  - `02-them-command.html`
  - `03-deploy.html`
- Mỗi file **tự chứa**: CSS inline, sơ đồ là SVG inline, không dùng CDN, nên mở offline hay gửi qua email đều được. Viết bằng
  tiếng Việt, giữ nguyên identifier.
- Phương án khác: viết Markdown rồi build HTML (pandoc, docfx) và publish lên GitHub Pages. Đẹp hơn và ít lệch hơn, nhưng thêm
  công cụ, và Pages cho repo private cần plan trả phí (Q17).
- **Phải đúng với code.** Người viết nội dung (system-architect hoặc code-implementer) đọc code và ra bản nháp nội dung.
  frontend-web-dev dựng HTML. tech-lead-reviewer đối chiếu với code. Thêm vào Definition of Done của các slice sau mục "cập nhật
  `docs/html` khi đổi pipeline, hosting hoặc deploy".

### 5.3 Nội dung bắt buộc

**5a. Giới thiệu**

- Nhu cầu: backend dùng chung cho nhiều game, server authoritative.
- Phạm vi P0, P0.5, P1.
- Cấu trúc repo và package (sau task 4).
- Kiến trúc:
  - Host, Platform, Platform.Web, Couchbase, module 2 project, ARC-01;
  - mô hình "một command = một transaction" với Touches/Reads, receipt/replay, outbox;
  - security profile;
  - gate, observability;
  - Unity SDK.
- Điểm mạnh, mỗi điểm kèm bằng chứng:
  - idempotency: ACC-06 với số đo burst;
  - đăng ký tường minh và fail-fast;
  - kiểm thử nhiều tầng;
  - mở rộng được qua `ReplacePolicy`/`ReplaceHandler`.
- Giới hạn hiện tại: chưa có cluster nhiều node, chưa có sandbox store thật.

**5b. Thêm command.** Sơ đồ sequence, và mỗi bước có link tới file:

1. Unity `GameServerClient` gửi POST Protobuf, header gồm requestId, bearer, version client.
2. Kestrel.
3. `CorrelationMiddleware`.
4. `ExceptionMiddleware`.
5. `PlayerBearerAuthenticationHandler` (session, epoch) và `BanCommandGuard`.
6. `GateMiddleware`.
7. `ProfileEnforcementMiddleware`.
8. Endpoint của `MapPlayerCommand` (`CommandEndpointFactory.cs`): giới hạn body, parse Protobuf, **đọc param và validate trong
   `ToDomain`**.
9. `CommandRunner`:
   - limiter cho mỗi player (CMD-06);
   - tra receipt theo requestId: replay hay mismatch;
   - transaction: **get database = framework đọc các aggregate trong Touches/Reads (GetMulti)**, upgrade schema;
   - **handler (logic)**: `ctx.Get`/`ctx.Set`/`AddOutbox`/`TryClaimAsync`;
   - **save database**: ghi aggregate với rev+1 và receipt, commit;
   - relay outbox.
10. Map wire, đưa vào response các section đã đổi và `serverTimeUtc`. Nếu có lỗi thì trả `ErrorEnvelope`.

Phải nêu rõ khác biệt so với mô hình "handler tự get/save DB" mà owner mô tả: handler **không** gọi DB, framework làm hộ.
Ví dụ dùng `modules/Sample.Counter` với đủ các bước:

- `.proto`;
- record command và result;
- `CommandDescriptor`;
- handler;
- `ModuleDescriptor`, error code;
- `MapPlayerCommand`;
- PCR;
- test;
- regenerate Unity SDK, gọi từ Unity.

Thêm các mục: bỏ hoặc tắt command, override handler của module có sẵn, command GM, các lỗi thường gặp
(`DateTime.UtcNow`, `Set` ngoài Touches, route thiếu `/v{n}/`).

**5c. Deploy cho DevOps.** Hướng dẫn gồm:

- artifact: `dotnet publish`, image;
- flavor và biến môi trường;
- danh sách secret (`GameServer:SecretKeys`);
- Couchbase:
  - version ≥ 7.6;
  - bucket cho mỗi environment, replicas;
  - `txn_meta` tạo trước;
  - RBAC;
  - lệnh provisioning (T1.7);
- reverse proxy và `ForwardedHeaders`;
- health probe;
- grace period ≥ 35 s;
- log và analytics: volume hoặc sidecar;
- OTLP;
- Data Protection key ring;
- GM SSO, CORS;
- credential IAP;
- NTP.

Checklist dạng bảng (mục, cách kiểm, ai ký). Các nhóm: Database, Security, Config, Observability, Capacity, Release/rollback,
Backup/restore. Mục nào chưa có công cụ hay drill thì đánh dấu "→ slice 013".

### 5.4 Cách hoà với 013

5c **không** thay 013. 5c là phần HTML (giao diện cho người đọc) của các deliverable mà 013 làm ra:

- bản nháp 5c viết ngay theo hiện trạng (sau task 1, 2 và 4);
- 013 thêm runbook, drill, rollback và restore, rồi cập nhật cùng file HTML đó;
- checklist trong 5c chính là tiêu chí ký duyệt của 013.

Container guide và Dockerfile để ở 013. 5c chỉ tham chiếu tới chúng.

### 5.5 Subtask

| ID | Việc | File | Done khi | Agent | Phụ thuộc | Size |
| --- | --- | --- | --- | --- | --- | --- |
| T5.1 | Bản nháp nội dung 5a và 5b (Markdown nội bộ), có link tới file và dòng code thật | `AgentMemories/features/task-30102026/docs-draft-*.md` | Mỗi bước ở 5.3 trỏ đúng file hoặc type đang tồn tại (grep được) | system-architect | 010 merge, T2 | M |
| T5.2 | Dựng `index.html`, `01-gioi-thieu.html`, `02-them-command.html`: CSS chung inline, SVG cho kiến trúc và sequence, mục lục, chạy được ở màn hình ≥ 360 px | `docs/html/*` | Mở offline không lỗi console. Không có request ra ngoài. Dark/light đọc được | frontend-web-dev | T5.1 | M |
| T5.3 | Bản nháp nội dung 5c và checklist | draft md | Mọi key cấu hình nhắc tới đều có trong `GameServerOptions`/`CouchbaseOptions`/`ObservabilityOptions` | system-architect | T1, T4 | M |
| T5.4 | Dựng `03-deploy.html` | `docs/html/03-deploy.html` | Như T5.2. Checklist in ra được (print CSS) | frontend-web-dev | T5.3 | S |
| T5.5 | Review độ chính xác: làm theo 5b, thêm thử một command `counter.reset` trên nhánh nháp, rồi bỏ đi | | Một dev làm theo 5b, build và test pass mà không phải hỏi thêm | tech-lead-reviewer | T5.2, T5.4 | S |

Rủi ro: HTML viết tay bị lệch khỏi code sau vài slice (Med). Giảm thiểu: thêm mục vào DoD, ghi footer "đúng với commit X",
và khi review mỗi slice thì grep các identifier được nhắc trong `docs/html`.

## 6. Câu hỏi cho owner (mỗi câu kèm mặc định đề xuất)

| # | Câu hỏi | Mặc định đề xuất | Ví dụ, hệ quả |
| --- | --- | --- | --- |
| Q1 | `dbcache` (memcached) có cần không? | **Bỏ.** Framework không có cache dùng chung. Session nằm ở collection có TTL | Memcached bucket deprecated ở 7.x. Nếu cần cache chung thì dùng ephemeral bucket hoặc collection có TTL, làm ở slice khác |
| Q2 | Flavor `dev` là Development (có cheat, test auth, GM seed) hay Staging? | Development | `dev` trên Couchbase dùng chung cho team, cheat bật được |
| Q3 | `beta` có người chơi thật và nạp tiền thật không? | Không: Staging, Apple Sandbox | Nếu có thì beta = Production (cluster riêng, không dev tool, Apple env Production) |
| Q4 | Game config overlay theo `DeploymentEnvironment` (beta và uat dùng chung) hay theo từng flavor (`config/env/<EnvironmentId>`)? | Giữ như hiện tại | Nếu uat cần giá shop khác beta thì thêm tầng overlay theo `EnvironmentId` (đổi CFG-01, nhỏ) |
| Q5 | File emulator: một file cho mỗi document hay một file cho mỗi collection? Có hot-reload khi sửa tay không? Có cần N1QL hay query tuỳ ý không? | Một file cho mỗi document. Không hot-reload, sửa khi server dừng. Chỉ hỗ trợ các query mà framework dùng (page, due, references) | `_database/dbbase/g_sample/pa_wallet/p_abc.json`. Module tự viết N1QL riêng sẽ không chạy trên local |
| Q6 | Lệnh provisioning cho Production làm ở slice này hay ở 013? | Làm ở slice local-env (T1.7), vì 5c cần nó | `SampleHost --provision-only` trong pipeline deploy |
| Q7 | Có cho phép `run-local -Lan` (bind `0.0.0.0` để thử trên điện thoại) không? | Có, kèm cảnh báo, chỉ ở flavor local | GM seed lộ ra LAN, nên in cảnh báo đỏ |
| Q8 | Sink của audit log: JSONL theo ngày UTC, collection Couchbase, hay stdout? | JSONL riêng cho từng instance và từng event, theo ngày UTC, đặt sau `IAnalyticsSink` | `sample-a-payment-20261030.jsonl`. Log shipper đẩy vào data lake |
| Q9 | `chargeTime` là thời điểm server grant hay thời điểm mua phía store? | Thời điểm server commit grant. Thời điểm phía store làm field phụ khi verifier có | Mua lúc 23:59 nhưng verify lúc 00:01 thì rơi sang tháng sau. Hiếm, chấp nhận được |
| Q10 | Có tính "resume" (đã có session, không login lại) là login không? | Có: `source="resume"`, tối đa một lần cho mỗi player mỗi ngày | Nếu không, game lưu token sẽ có `active_days` thấp sai |
| Q11 | `userID` trong file analytics sau PRIV-01 (xoá tài khoản)? | Giữ `playerId` (id nội bộ, không phải PII). Retention file 25 tháng. Ghi rõ trong chính sách | Phương án khác: `HMAC(pepper, playerId)`, nhưng khi đó data team không join được với GM tool |
| Q12 | Định dạng múi giờ trong log? Lấy múi giờ theo deployment hay theo quốc gia của từng player? | Theo deployment: IANA `timeZone` + `utcOffset` cho mỗi event + bản UTC song song | VN: `Asia/Ho_Chi_Minh` `+07:00`. EU: `Europe/London` `+00:00`/`+01:00`. Theo từng player thì cần quốc gia (GeoIP), ngoài phạm vi |
| Q13 | Feed NuGet: GitHub Packages, Azure Artifacts, hay feed nội bộ VNG (Nexus, Artifactory)? | GitHub Packages (repo đang ở GitHub) | GitHub Packages cần PAT `read:packages` cho mỗi dev và mỗi CI của game |
| Q14 | Unity SDK: UPM git URL, tarball, hay scoped registry? | Git URL theo tag | `…/game-server-dotnet-kestrel.git?path=sdk/unity/com.gameserver.client#v0.7.0`. Repo private thì máy Unity cần SSH key |
| Q15 | Version policy: giữ 0.x đến hết P1? Có hỗ trợ backport hotfix cho bản cũ không? | 0.x đến P1. Chỉ backport cho minor mới nhất (`release/0.y`) | Game đang ở 0.7 muốn fix thì lên 0.7.z. 0.6 không còn được vá |
| Q16 | Có giữ cách dùng submodule/source cho maintainer khi debug không? | Có, bằng `Directory.Build.local.props` không commit. Game không dùng cách này | |
| Q17 | HTML: file tĩnh trong repo `docs/html/` hay publish thành trang web? | File tĩnh trong repo, tự chứa | Nếu muốn có web thì GitHub Pages (repo private cần Enterprise) hoặc wiki nội bộ |
| Q18 | Đánh số slice mới (local-env, packaging, analytics) chen vào trước 011: đổi số các slice cũ, hay dùng số tiếp theo khi tạo? | Dùng số tiếp theo khi tạo (011 = local-env…). Giftcode, iap-notifications và ops-readiness lùi số. Cập nhật `00_summary.md` | |

## 7. Thứ tự thực hiện và cách đan với P0.5

```
[đang chạy] 010 jobs-account-deletion  ── merge main
   │
   ├─▶ S-A  local-env (task 1 + 2)            ~6–9 ngày    ← ưu tiên 1: mọi slice sau dùng flavor/local store
   │      └─ checkpoint: review T1.8, merge
   ├─▶ S-B  packaging (task 4)                ~7–10 ngày   ← làm khi không có slice nào khác mở (đụng mọi csproj)
   │      └─ checkpoint: tag v0.7.0-preview, consumer build xanh, merge
   ├─▶ S-C  analytics-audit (task 3, ANA-01 phần 1)  ~4–5 ngày ← trước 012 (012 thêm event refund vào cùng seam)
   │      └─ checkpoint: review T3.6, merge
   ├─▶ 011 giftcode → 012 iap-notifications → 013 ops-readiness (+ hoàn thiện 5c)
   │
   └─ song song (chỉ đụng docs/html + draft): T5.1/T5.2 sau S-A; T5.3/T5.4 sau S-B; T5.5 trước 011
```

- **011 có nên chờ không? Có, chờ S-A và S-B.**
  - Giftcode là module mới. Nó nên được viết theo layout package/template mới và có `appsettings.{flavor}` ngay từ đầu.
  - Nếu làm 011 song song với S-B thì conflict ở mọi csproj.
- **S-C có thể đổi chỗ với 011** nếu owner cần giftcode gấp. Ràng buộc duy nhất: S-C phải xong **trước 012**, vì cả hai cùng
  sửa module IAP.
- S-A và S-B có thể chạy song song nếu có hai người. S-A tạo project `Platform.LocalStore` mới, nên S-B phải đưa project này vào
  danh sách package. Một người thì làm tuần tự.
- **Checkpoint chung:** sau mỗi slice phải đạt build sạch, full test (gồm Couchbase integration) và tech-lead review với 0 Blocker.

## 8. Ước lượng

| Task | Ngày-người | Phần lớn công sức nằm ở |
| --- | --- | --- |
| 1 | 5–7 | file store + journal + conformance trên 3 store |
| 2 | 1–2 | script + launch profile |
| 3 | 4–5 | sink + múi giờ + test HTTP |
| 4 | 6–9 | pack + CI (chưa có gì) + template + UPM |
| 5 | 5–7 | độ chính xác của 5b và 5c, dựng 3 trang HTML |
| **Tổng** | **21–30** | Chưa tính thời gian chờ owner chốt Q1–Q18 |

## Quyết định của owner (2026-10-30)

- Q1 `dbcache`: **bỏ**. Chỉ cấu hình một bucket chính; ephemeral bucket khi nào cần mới thêm.
- Q2/Q3 flavor: **local/dev → Development, beta/uat → Staging, live → Production** (beta/uat không có payment thật, IAP sandbox).
- Task 4 (library packaging): **skip, tách ra làm riêng sau**. Q13–Q16 bỏ ngỏ.
- Q8/Q11 audit log: **JSONL theo instance + loại event, mỗi ngày UTC một file; userID = playerId thô** (không băm), giữ 25 tháng; mỗi bản ghi có UTC + giờ local kèm offset + tên IANA zone.
- Các câu còn lại (Q4–Q7, Q9, Q10, Q12, Q17, Q18): dùng mặc định đề xuất trong plan.
