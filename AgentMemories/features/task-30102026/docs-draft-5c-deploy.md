# Bản nháp nội dung 5c — Deploy cho DevOps (kèm checklist)

> **Đúng với commit 175df65** (branch `docs-html-guides`, 2026-10-31). Mọi key, class, switch, exit code, metric và file nêu
> dưới đây đã được grep/đọc trong code ở commit này. Footer của trang HTML phải giữ dòng này.

- **Người đọc:** DevOps / SRE, DBA Couchbase, security reviewer.
- **Trang HTML đích:** `docs/html/03-deploy.html` (task T5.4). Người dựng HTML: frontend-web-dev. Người đối chiếu: tech-lead-reviewer.
- **Phạm vi:** trạng thái hiện tại sau slice 010, 011, 012. Phần chưa có công cụ, manifest hay drill được đánh dấu
  **→ ops-readiness (015)**. Slice 015 bổ sung runbook, drill, Dockerfile/manifest rồi cập nhật cùng trang này; checklist ở
  mục 17 là tiêu chí ký duyệt của 015 (plan §5.4).
- **Nguồn chính (giữ link trong HTML):** `docs/guides/environments-and-local-run.md`, `docs/guides/analytics.md`,
  `docs/guides/local-real-env-testing.md`, `docs/pcr/platform.md` (§1, §13–§18), `docs/pcr/gm.md`, `docs/pcr/iap.md`,
  `docs/pcr/identity.md`, `specs/011-local-env/contracts/operations-cli.md`, `Requirements/Game_Server_System_Requirements_v1.2.md`.

Quy ước trong bản nháp (frontend giữ thành badge):

| Ký hiệu | Nghĩa |
| --- | --- |
| **→ 015** | chưa có công cụ, manifest, runbook hay drill; slice ops-readiness (015) làm |
| **[ĐÃ CHẠY]** | người viết đã chạy thử trên artifact publish ở commit 175df65 (2026-10-31, Windows, không Couchbase) |
| **[SUY LUẬN]** | suy ra từ code, chưa kiểm end-to-end |
| **[VERIFY]** | phụ thuộc hành vi bên ngoài repo (image .NET, Couchbase, IdP), cần kiểm khi làm 015 |

---

## 1. Tổng quan triển khai

Một bản build (host, ví dụ `SampleHost`) chạy ở mọi môi trường. Môi trường được chọn bằng **flavor** = tên môi trường
của host. Mỗi instance là một process ASP.NET Core/Kestrel stateless phía sau reverse proxy/load balancer. Toàn bộ trạng thái
nằm trong Couchbase. Mỗi instance ghi file log và file analytics xuống đĩa của nó.

**Sơ đồ 1 — topology (mô tả cho SVG inline):**

```text
Nodes:
  [Client Unity]  [Trình duyệt GM]          (ngoài)
  [Reverse proxy / LB — TLS termination]    (biên)
  [Instance 1..N: host .NET 10 / Kestrel]   (cụm app; mỗi instance có 2 volume: logs/, analytics/)
  [Couchbase cluster ≥ 7.6]                 (bucket theo môi trường, scope g_<gameId>)
  [OTLP collector]                          (tuỳ chọn)
  [Log/analytics shipper]                   (sidecar hoặc agent node)
  [Dịch vụ ngoài: Google OAuth/Play API, static.gc.apple.com, IdP OIDC của công ty]
Edges:
  Client Unity  --HTTPS Protobuf-->  Proxy
  Trình duyệt GM --HTTPS JSON /gm/**--> Proxy
  Proxy --HTTP + X-Forwarded-For/-Proto--> Instance
  Instance --couchbases:// KV 11207 + HTTPS mgmt/query--> Couchbase
  Instance --OTLP grpc|http/protobuf--> Collector
  Instance --ghi file--> volume logs/, analytics/ --> Shipper --> data lake / log store
  Instance --HTTPS egress--> Dịch vụ ngoài
  Pipeline (một lần mỗi deploy) --"--provision" với identity quản trị--> Couchbase
```

Ghi chú cho SVG: vẽ hai identity Couchbase khác màu: "runtime (data-only)" trên cạnh Instance→Couchbase và "provisioner (quản
trị)" trên cạnh Pipeline→Couchbase.

---

## 2. Artifact

### 2.1 Build và publish

```bash
dotnet publish samples/SampleHost/SampleHost.csproj -c Release -o out/SampleHost
```

- SDK: .NET **10.0.403** (`global.json`, `rollForward: latestPatch`). Target `net10.0` (`Directory.Build.props`).
- Output là bản framework-dependent: chạy bằng `dotnet SampleHost.dll` trên ASP.NET Core runtime 10. **[ĐÃ CHẠY]** Output gồm
  `SampleHost.dll`, các assembly `GameServer.*`, `appsettings.json`, `appsettings.{Local,Dev,Beta,Uat,Live}.json` và thư mục
  `config/base/*.json` (config game, copy nhờ `CopyToOutputDirectory` trong `samples/SampleHost/SampleHost.csproj`).
- Game thật thay `samples/SampleHost` bằng host của mình. Mọi điều trong trang này áp dụng cho host nào gọi
  `GameServerCommandLine.Parse(args)` và `app.RunGameServerAsync(mode)` như `samples/SampleHost/Program.cs`. Thiếu
  `RunGameServerAsync` thì không có `--provision`.
- Artifact chứa cả `appsettings.Local.json` và `appsettings.Dev.json` (có staff seed `dev-only-change-me`). Hai file này chỉ
  được nạp khi flavor là `Local` hoặc `Dev`. Ở Staging/Production, `Gm:Bootstrap:Staff` bị từ chối khi khởi động
  (`GmOptionsValidator`).

### 2.2 Thư mục làm việc = content root (quan trọng)

ASP.NET Core lấy **thư mục hiện tại** làm content root. `appsettings*.json`, `config/` (`GameServer:ConfigPath`), `logs`
(`Observability:Logging:Directory`) và `analytics` (`GameServer:Analytics:Directory`) đều tính từ đó.

**[ĐÃ CHẠY]** Chạy `dotnet <đường dẫn>/SampleHost.dll --provision` từ thư mục khác (`C:\`) với `ASPNETCORE_ENVIRONMENT=Live`
cho exit 1:

```text
 - [deployment] GameServer:DeploymentEnvironment is not set and the host environment name 'Live' is not a deployment
   environment (...). Fix: set GameServer:DeploymentEnvironment in appsettings.Live.json (found: no, content root C:\) ...
```

Vì vậy container/service phải đặt working directory = thư mục publish (Docker `WORKDIR`), hoặc truyền `--contentRoot <dir>`.

### 2.3 Container image → 015

- Repo **chưa có** Dockerfile, manifest K8s hay CI (`.github/` không tồn tại). Dockerfile, container guide và manifest thuộc 015
  (plan §5.4).
- Ràng buộc đã biết cho image (PCR platform §18, kiểm 2026-10-31 bằng Docker):
  - `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04) có `tzdata`: **dùng được**.
  - `aspnet:10.0-alpine` và `aspnet:10.0-noble-chiseled` **không có** dữ liệu múi giờ. Mọi tên múi giờ trừ `UTC` báo không tìm thấy,
    nên host exit 1 khi analytics bật ở Staging/Production.
  - `aspnet:10.0-noble-chiseled-extra` **dùng được**. Cách khác: cài `tzdata` vào image.
- **[VERIFY]** Image .NET 8 trở lên đặt sẵn cổng mặc định qua `ASPNETCORE_HTTP_PORTS` (8080) và chạy bằng user không phải root
  (`app`). Volume `logs/`, `analytics/` phải ghi được bởi user đó. Thư mục analytics không ghi được thì host exit 1 nêu key và
  đường dẫn.

---

## 3. Flavor và biến môi trường

### 3.1 Bảng flavor (nguồn: `StandardFlavors` trong `src/GameServer.Platform/DeploymentEnvironments.cs`)

| Flavor (`ASPNETCORE_ENVIRONMENT`) | `GameServer:DeploymentEnvironment` | `GameServer:EnvironmentId` | `GameServer:Store` | Bucket mẫu (`Couchbase:BucketName`) | Công cụ dev |
| --- | --- | --- | --- | --- | --- |
| `Local` | Development | `local` | `File` | — (thư mục `FileStore:Folder`) | bật |
| `Dev` | Development | `dev` | `Couchbase` | `gameserver-dev` | bật |
| `Beta` | Staging | `beta` | `Couchbase` | `sample-beta` | tắt |
| `Uat` | Staging | `uat` | `Couchbase` | `sample-uat` | tắt |
| `Live` | Production | `live` | `Couchbase` (bắt buộc TLS) | `sample-live`, cluster riêng (DAT-01) | tắt |

- Chọn flavor bằng `ASPNETCORE_ENVIRONMENT` (hoặc `DOTNET_ENVIRONMENT`). Phải viết đúng hoa thường vì tên file phân biệt hoa
  thường trên Linux. Giá trị `GameServer:*` của mỗi flavor nằm trong `samples/SampleHost/appsettings.{Flavor}.json`.
- "Công cụ dev" gồm: xác thực thử bằng header `X-Test-Player`, module cheat, seed staff GM, GM OpenAPI
  (`/gm/v1/openapi/gm.json`), verifier IAP giả và identity provider giả. Chúng chỉ có ở Development/Test
  (`GameServerOptions.AllowsDevelopmentTools`). Beta và uat **không** có công cụ dev.
- Flavor `Dev` phải nằm trong mạng nội bộ/VPN: header `X-Test-Player` mạo danh được mọi player, và staff seed có mật khẩu
  commit trong repo.
- Giá trị `StoreKind` (`src/GameServer.Platform/GameServerOptions.cs`): `InMemory`, `Couchbase`, `File`. `InMemory` và `File`
  bị từ chối ngoài Development/Test.
- Overlay config game: `config/env/<DeploymentEnvironment>` (`src/GameServer.Platform/Config/FileGameConfigLoader.cs`). Beta
  và uat **dùng chung** `config/env/Staging`. Host mẫu hiện chỉ có `config/base`.

### 3.2 Thứ tự nạp cấu hình

```text
appsettings.json  <  appsettings.{Flavor}.json  <  biến môi trường  <  dòng lệnh (--urls, --Section:Key=value)
```

Tên biến môi trường = key cấu hình, thay `:` bằng `__`. Ví dụ `Couchbase:Password` thành `Couchbase__Password`, phần tử mảng
`GameServer:Hosting:ForwardedHeaders:KnownNetworks[0]` thành `GameServer__Hosting__ForwardedHeaders__KnownNetworks__0`.

### 3.3 Fail-closed khi khởi động

Mọi lỗi của một lần khởi động được in **cùng lúc** ra stderr, sau dòng `Game server startup validation failed:`. Mỗi lỗi một
dòng ` - …`, rồi host thoát với code 1 (`HostExitCodes.InvalidConfiguration`). Flavor sai hoặc thiếu **không bao giờ** thành
Development.

**[ĐÃ CHẠY]** Artifact publish, `ASPNETCORE_ENVIRONMENT=Live`, không đặt biến nào, chạy server. Exit 1 với đúng 11 lỗi:
`DataProtection:MasterKey`, `Couchbase:ConnectionString`, `Couchbase:Username`, `Couchbase:Password`,
`Identity:GuestPepper`, năm khoá `Iap:*` và `Gm:Oidc:Authority/ClientId/ClientSecret`. Đây là cách nhanh để DevOps xem còn
thiếu gì: chạy một lần với flavor đích rồi đọc danh sách lỗi. Không lỗi nào in giá trị.

### 3.4 Biến bắt buộc theo flavor

| Flavor | Bắt buộc từ môi trường hoặc secret provider |
| --- | --- |
| local | không có |
| dev | `Couchbase__Password` |
| beta, uat | `Couchbase__ConnectionString`, `Couchbase__Username`, `Couchbase__Password`, `DataProtection__MasterKey`, `Gm__Oidc__Authority`, `Gm__Oidc__ClientId`, `Gm__Oidc__ClientSecret`, `Identity__GuestPepper`, `Iap__Google__PackageName`, `Iap__Google__ServiceAccountEmail`, `Iap__Google__ServiceAccountPrivateKeyPem`, `Iap__Apple__BundleId`, `Iap__Apple__RootCertificateBase64`; khi bật Play Games thêm `Identity__Providers__PlayGames__ClientId`, `Identity__Providers__PlayGames__ClientSecret` |
| live | như beta/uat. `Couchbase__ConnectionString` phải là `couchbases://…`. Dùng CA riêng thì thêm `Couchbase__TrustCertificatePath` |
| lần chạy `--provision` (mọi flavor Couchbase) | chỉ `Couchbase__ConnectionString`, `Couchbase__Username`, `Couchbase__Password` của **identity quản trị** |

Biến nên đặt theo deploy (không phải secret, không có trong file flavor mẫu): `GameServer__Analytics__Directory` (đường dẫn
tuyệt đối trên volume), `Observability__Logging__Directory`, `Observability__InstanceId` (khi không dùng StatefulSet),
`Observability__Otlp__Endpoint`, `GameServer__Hosting__ForwardedHeaders__*`, `Gm__Oidc__BootstrapManagers__0__*`,
`Gm__Cors__AllowedOrigins__0`, cổng nghe (`ASPNETCORE_URLS` hoặc `--urls`). File `appsettings.{Beta,Uat,Live}.json` không đặt
`Urls`.

Ví dụ khung (chỉ có tên, **không** có giá trị thật):

```bash
ASPNETCORE_ENVIRONMENT=Live
Couchbase__ConnectionString=couchbases://<cb-node-1>,<cb-node-2>
Couchbase__Username=<runtime-data-only-user>
Couchbase__Password=<từ secret manager>
Couchbase__TrustCertificatePath=/etc/gameserver/couchbase-root-ca.pem
DataProtection__MasterKey=<từ secret manager>
Identity__GuestPepper=<từ secret manager>
Gm__Oidc__Authority=https://<idp>
Gm__Oidc__ClientId=<client id>
Gm__Oidc__ClientSecret=<từ secret manager>
Iap__Google__PackageName=<package>
Iap__Google__ServiceAccountEmail=<sa email>
Iap__Google__ServiceAccountPrivateKeyPem=<từ secret manager>
Iap__Apple__BundleId=<bundle id>
Iap__Apple__RootCertificateBase64=<Apple Root CA G3, DER base64>
GameServer__Analytics__Directory=/var/lib/gameserver/analytics
Observability__Logging__Directory=/var/lib/gameserver/logs
GameServer__Hosting__ForwardedHeaders__Enabled=true
GameServer__Hosting__ForwardedHeaders__KnownNetworks__0=<CIDR của proxy>
```

---

## 4. Secret

Danh sách mặc định `GameServer:SecretKeys` (`src/GameServer.Platform/GameServerOptions.cs`):

| Key cấu hình | Biến môi trường | Ghi chú |
| --- | --- | --- |
| `Identity:GuestPepper` | `Identity__GuestPepper` | **Không đổi sau khi đã có player.** Hash guest = HMAC-SHA256(pepper, secret) và code không có pepper cũ, nên đổi pepper làm mọi guest không login được **[SUY LUẬN]** |
| `Couchbase:Password` | `Couchbase__Password` | bắt buộc ở mọi môi trường, kể cả Development |
| `Iap:Google:ServiceAccountPrivateKeyPem` | `Iap__Google__ServiceAccountPrivateKeyPem` | PKCS#8 PEM, phải ký được (kiểm khi khởi động) |
| `DataProtection:MasterKey` | `DataProtection__MasterKey` | base64 của đúng 32 byte ngẫu nhiên |
| `Gm:Oidc:ClientSecret` | `Gm__Oidc__ClientSecret` | |
| `Identity:Providers:PlayGames:ClientSecret` | `Identity__Providers__PlayGames__ClientSecret` | |

- Ngoài Development/Test, key trong danh sách này mà có giá trị trong **bất kỳ** file cấu hình nào thì host exit 1, kể cả khi
  biến môi trường ghi đè (`DeploymentChecks.ValidateSecretSources`). Chế độ `--provision` cũng kiểm.
- Check so khớp **đúng tên key**. `DataProtection:PreviousMasterKeys:<id>` (master key cũ khi rotate) cũng là secret nhưng
  **không** có trong danh sách mặc định. Cấp nó qua secret provider. Nếu game override `GameServer:SecretKeys` thì liệt kê lại
  đủ 6 key mặc định rồi mới thêm key mới (comment trong code nói mảng bị thay thế khi bind, không gộp).
- Test repository kiểm mọi `samples/SampleHost/appsettings*.json` không chứa secret. Secret scan trong CI (ACC-48) chưa có → 015.
- Tài liệu, ticket và manifest commit **không bao giờ** chứa giá trị secret. Chỉ ghi tên biến và nơi lưu (secret manager).
- Mỗi môi trường dùng credential riêng (OPS-01): user Couchbase, pepper, master key, OIDC client.

---

## 5. Couchbase

### 5.1 Cluster và bucket

| Mục | Yêu cầu | Nguồn |
| --- | --- | --- |
| Phiên bản | Couchbase Server **≥ 7.6** (đã kiểm EE 7.6.12). Bật service Data, Query, Index. SDK `CouchbaseNetClient` 3.10.0 | README, `Directory.Packages.props` |
| Bucket | một bucket loại **Couchbase** cho mỗi môi trường. Live dùng **cluster riêng** (DAT-01). Không dùng memcached (deprecated, không có transaction). App không bao giờ tạo bucket: DBA tạo | DAT-01, PCR platform §17 |
| Scope | luôn là `g_<gameId>`, không cấu hình được (`CouchbaseOptions.ScopeName`). `GameServer:GameId`: chữ thường, số, `_`, tối đa 24 ký tự. Đổi `GameId` = scope mới, tức dữ liệu mới | `CouchbaseOptions.cs`, thông báo khởi động |
| Replica | một node: **replicas = 0**. Replicas = 1 trên một node thì mọi transaction lỗi `DurabilityImpossible`. Production: **≥ 2 node + ≥ 1 replica** (G4). Durability luôn là `Majority`, không bao giờ `None` | Requirements G4, DAT-05, V4 |
| NTP | đồng bộ trên **mọi** node Couchbase (transaction cần) | OPS-01 |
| Transaction metadata | collection `g_<gameId>.txn_meta`, `--provision` tạo **trước tiên**. Không bao giờ xoá. Mọi instance dùng cùng `MetadataCollection` (framework cố định) | DAT-07, `CouchbaseClusterOptionsFactory.cs` |
| Lost-transaction cleanup | chạy trong mỗi instance (`CleanupWindow` 60 s). **Không instance nào chạy thì không có cleanup**, nên bảo trì không được tắt mọi instance khi còn transaction dở | OPS-05 |

### 5.2 Key cấu hình (`CouchbaseOptions`, section `Couchbase`)

| Key | Mặc định | Giới hạn (kiểm khi khởi động bởi `CouchbaseSettingsCheck`) |
| --- | --- | --- |
| `Couchbase:ConnectionString` | — (bắt buộc) | `couchbase://h1[,h2…]` hoặc `couchbases://…` |
| `Couchbase:Username` | — (bắt buộc) | identity của deployment |
| `Couchbase:Password` | — | bắt buộc ở mọi môi trường, chỉ từ môi trường |
| `Couchbase:BucketName` | — (bắt buộc) | |
| `Couchbase:KvTimeoutMs` | 2500 | 100 .. 1000 × min(`GameServer:Limits:PlayerTimeoutSeconds`, `GmTimeoutSeconds`) |
| `Couchbase:QueryTimeoutMs` | 20000 | 100 .. 1000 × `GameServer:Limits:GmTimeoutSeconds` |
| `Couchbase:TrustCertificatePath` | null | file PEM/DER của **root CA**, tương đối tính từ content root |
| `Couchbase:Compression` | true | Snappy |
| `Couchbase:CompressionMinSizeBytes` | 32 | 0 .. 20 971 520 |
| `Couchbase:NumKvConnections` / `MaxKvConnections` | 2 / 5 | 1..32 / Num..64 |
| `Couchbase:UseGetMulti` | false | giữ false (PCR platform §2) |
| `Couchbase:ProvisionOnStartup` | Development: true, còn lại: false | **giữ false ở beta/uat/live** |
| `Couchbase:ProvisioningTimeoutSeconds` | 600 | 30 .. 7200 |

Key cũ từ `database_settings.json` (`Couchbase:ServerIp`, `OperationTimeOut`, `CompressThreshold`, `MoxiPort`, `BucketType`,
`DbCache`, `dbbase:*`, `dbcache`) chỉ sinh **warning** nêu key thay thế, không bao giờ gây lỗi.

### 5.3 TLS

- Live (Production) với `couchbase://` thì exit 1. **[ĐÃ CHẠY]**: `[couchbase] Couchbase:ConnectionString uses couchbase:// (no TLS)
  in 'Production'. Fix: use couchbases://, with Couchbase:TrustCertificatePath for a private CA …`. Staging chỉ có warning,
  Development không có gì. Không có công tắc bỏ qua.
- `Couchbase:TrustCertificatePath` là root **duy nhất** được tin cho cả KV lẫn HTTP. Trust store của OS không được dùng, tên host
  vẫn được kiểm (`CouchbaseTrustedRoots.cs`).
- File phải là **root CA** tự ký. Trỏ vào intermediate thì chuỗi `PartialChain` và mọi kết nối bị từ chối.
- `DisableCertificateDownloads = true`: host không tải certificate thiếu qua AIA, nên **node Couchbase phải gửi kèm
  intermediate** trong handshake.
- **Cổng phát hành TLS (bắt buộc trước mọi cutover Live; chưa từng chạy vì cluster local không có TLS) → 015.** Hai test opt-in
  phải ra **Passed**, không phải Skipped (thiếu biến thì test tự skip qua `Assert.SkipWhen`):
  - `CouchbaseSettingsIntegrationTests.Tls_trusts_the_configured_ca_and_refuses_another` (class nằm trong
    `tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseSettingsTests.cs`)
  - `CouchbaseProvisioningTests.Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls`
    (`tests/GameServer.Platform.Couchbase.IntegrationTests/CouchbaseProvisioningTests.cs`)

  Biến: `COUCHBASE_TLS_CONNSTR`, `COUCHBASE_CA_PATH`, cùng các biến của fixture integration (`COUCHBASE_CONNSTR`,
  `COUCHBASE_USERNAME`, `COUCHBASE_PASSWORD`, `COUCHBASE_BUCKET`, xem README). Lệnh gợi ý (chưa chạy):
  `dotnet test --project tests/GameServer.Platform.Couchbase.IntegrationTests -c Release --filter-method "*Tls_*" --filter-method "*_over_tls"`.

### 5.4 Hai identity RBAC

| Identity | Dùng ở | Role (cú pháp 7.6) |
| --- | --- | --- |
| **Runtime (data-only)** | mọi instance API beta/uat/live | trên scope `[<bucket>:g_<gameId>]`: `data_reader`, `data_writer`, `query_select`, `query_insert`, `query_update`, `query_delete` (UI: Data Reader, Data Writer, Query Select/Insert/Update/Delete) |
| **Provisioner (quản trị)** | chỉ bước `--provision` của pipeline | trên **bucket** (vì scope chưa có ở lần đầu): `bucket_admin[<bucket>]`, `query_manage_index[<bucket>]`, `data_reader[<bucket>]`, cộng role cấp cluster `query_system_catalog` |

- Role theo scope viết `[bucket:scope]`. Couchbase 7.6 từ chối dạng `[bucket:scope:*]`.
- Bộ role của provisioner đã kiểm bằng user hạn chế (`CouchbaseProvisionerRolesTests`). Thiếu bất kỳ role nào trong bốn role thì
  exit 2. Ví dụ thiếu `query_system_catalog`: `FAILED index …: not in system:indexes after CREATE INDEX (… needs the Query System
  Catalog role …)`.
- User data-only chạy `--provision` thì exit 2 ngay mục đầu (`FAILED scope g_x: CouchbaseException (HTTP 403)`). Đây là cách
  kiểm nhanh rằng identity runtime **không** có quyền quản trị.

### 5.5 Provisioning (`--provision`)

```bash
cd /app   # thư mục publish = content root
ASPNETCORE_ENVIRONMENT=Live \
Couchbase__ConnectionString=couchbases://<cb-node-1>,<cb-node-2> \
Couchbase__Username=<provisioner-user> \
Couchbase__Password="$PROVISIONER_PASSWORD" \
dotnet SampleHost.dll --provision
```

- Cần: flavor hợp lệ, `GameServer:Store=Couchbase`, connection string, user, password, bucket. **Không** cần SSO GM, IAP, pepper,
  master key hay setting analytics (**[ĐÃ CHẠY]**: Live `--provision` không có biến nào chỉ báo 3 lỗi Couchbase). Không chạy web
  server, job hay sweeper. Không tạo bucket, không xoá hay sửa gì.
- Thứ tự: scope → `txn_meta` → các collection khác → chờ collection trả lời KV → index (framework, module, index tham chiếu purge)
  → chờ mọi index `online` → kiểm lại.
- Output stdout có tiền tố ổn định `provision:` (là hợp đồng, đổi là breaking change):
  `provision: game <id>, environment <env> (<DeploymentEnvironment>), bucket <b>, scope g_<id>`, mỗi mục `created` / `present` /
  `created (online after N s)`, cuối cùng `provision: done: X created, Y present`. Khi lỗi:
  `provision: FAILED <kind> <name>: <cause>` rồi `provision: stopped after N items; fix the cause and run again (idempotent)`.
  Output không chứa credential.
- Switch: đúng `--provision`, không phân biệt hoa thường. `--provision=…`, `-provision`, `/provision` cho exit 1
  (**[ĐÃ CHẠY]**: `[provision] '--provision=.' is not the provisioning switch …`).

| Exit code (`HostExitCodes`) | Ý nghĩa | Pipeline làm gì |
| --- | --- | --- |
| 0 (`Ok`) | mọi thứ đã có (tạo lần này hoặc trước đó) | tiếp tục deploy |
| 1 (`InvalidConfiguration`) | cấu hình sai: flavor, setting Couchbase, secret nằm trong file JSON, switch viết sai, store không phải Couchbase | dừng, sửa cấu hình |
| 2 (`ProvisioningFailed`) | provisioning lỗi, quá `Couchbase:ProvisioningTimeoutSeconds`, hoặc Ctrl+C | dừng. Timeout thì **chạy lại** (idempotent, tiếp tục từ chỗ thiếu) hoặc tăng timeout (≤ 7200). Index ở trạng thái `error` thì `DROP INDEX` rồi chạy lại |

- Idempotent: mục có sẵn báo `present` và không bị sửa. Index trùng tên nhưng khác định nghĩa vẫn báo `present` (không phát hiện
  lệch). Index có sẵn ở trạng thái `deferred` thì lỗi, kèm gợi ý `BUILD INDEX`.
- Cluster manager bận (HTTP 503) được tự thử lại tối đa 8 lần (~22 s).
- Thời gian: build index của scope mới khi cluster đang tải mất 23–88 s mỗi index (đo dưới tải của bộ test), nên có thể vượt
  600 s.

**Kiểm schema khi khởi động** (`Couchbase:ProvisionOnStartup=false`): trước khi nghe cổng, instance đọc thử mọi collection bắt
buộc bằng identity runtime. Thiếu collection thì exit 1:
`[couchbase] scope g_<id> in bucket <b> is missing collections …. Fix: run the provisioning command (<host> --provision) …`.
Index **không** được kiểm khi khởi động; báo cáo của `--provision` là nguồn đúng.

### 5.6 Nén Snappy

- Từ slice 011, body KV ≥ 32 byte **thực sự** được nén Snappy (`CouchbaseClusterOptionsFactory`, `WithSnappyCompression()`).
- Theo dõi CPU của data service và latency KV (p95/p99 của command) trên beta trước khi lên live.
- Rollback không cần deploy code: đặt `Couchbase__Compression=false` rồi restart instance.

---

## 6. Mạng: reverse proxy, TLS, ForwardedHeaders

- TLS kết thúc ở LB/proxy (SEC-01). Kestrel nghe HTTP nội bộ và không được mở thẳng ra Internet. Code không có
  `UseHttpsRedirection`/HSTS, nên proxy chịu trách nhiệm redirect HTTPS.
- `GameServer:Hosting:ForwardedHeaders` (`ForwardedHeadersSettings`): `Enabled`, `KnownProxies[]` (IP), `KnownNetworks[]`
  (CIDR), `ForwardLimit` (mặc định 1). Chỉ xử lý `X-Forwarded-For` và `X-Forwarded-Proto`, chỉ tin proxy trong danh sách.
  Bật mà danh sách rỗng, network `/0`, IP hoặc CIDR sai, hay `ForwardLimit < 1` thì exit 1 (`DeploymentChecks.cs`).
- Chuỗi CDN + LB: đặt `ForwardLimit` = số hop tin cậy. Để 1 thì IP client chính là IP edge của CDN, khiến rate limit theo IP
  (login) và audit sai.
- **[SUY LUẬN]** `X-Forwarded-Host` **không** được xử lý, nên proxy phải giữ nguyên header `Host`. OIDC tạo `redirect_uri` từ
  scheme và host của request. Sau proxy TLS mà thiếu `X-Forwarded-Proto` tin cậy thì redirect URI thành `http://…` và IdP từ
  chối. Cookie GM vẫn `Secure` ngoài Development bất kể scheme (`GmCookies.cs`).
- Route public (không cần xác thực): `/health/live`, `/health/ready`, `/v1/time`, cùng các route login và SSO. Khuyến nghị
  (không bắt buộc trong code): proxy chỉ cho `/gm/**` từ mạng nội bộ/VPN.
- Egress cần mở: `oauth2.googleapis.com`, `androidpublisher.googleapis.com` (IAP Google), `games.googleapis.com` (Play Games),
  `static.gc.apple.com` (Game Center), authority của IdP OIDC, OTLP collector. Xác minh Apple JWS chạy offline (root CA lấy từ
  cấu hình).
- `AllowedHosts` trong `appsettings.json` mẫu là `*`.

---

## 7. Health, readiness, shutdown

### 7.1 Probe (`src/GameServer.Platform.Observability/ObservabilityModule.cs`)

| Route | Check | HTTP | Dùng cho |
| --- | --- | --- | --- |
| `GET /health/live` | không check nào | 200 `Healthy` | liveness (không restart khi DB chết) |
| `GET /health/ready` | `store` (≤ 2 s), `config`, `log`, `shutdown`, `analytics` (khi gọi `AddAnalyticsFiles()`) | **200** khi `Healthy` **hoặc** `Degraded`; **503** khi `Unhealthy` | readiness / LB health check |
| `GET /gm/v1/health` | như ready, kèm chi tiết JSON | cần permission GM `ops.read` | người vận hành |

- Body của `/health/ready` chỉ là một chữ (`Healthy`, `Degraded`, `Unhealthy`). Kết quả được cache
  `Observability:ReadyCacheMilliseconds` (1000 ms).
- **`Degraded` vẫn trả 200**, nên probe không rút instance. Degraded xảy ra ở:
  - `log`: có lỗi sink log trong `Observability:Logging:SinkErrorWindowSeconds` (300 s), hoặc đĩa log còn ít hơn
    `Observability:Logging:LowDiskBytes` (1 GiB);
  - `analytics`: có lỗi sink analytics trong `GameServer:Analytics:ErrorWindowSeconds` (300 s), hoặc thư mục còn ít hơn
    `GameServer:Analytics:LowDiskBytes` (1 GiB). Check này không bao giờ Unhealthy.

  Phát hiện Degraded bằng metric (mục 9) hoặc bằng cách so body của probe, **không** dựa vào status code.
- `Unhealthy` (503): store không trả lời, đang shutdown, file store bị faulted.
- Instance chỉ nghe cổng sau khi qua validation, kiểm schema Couchbase, lock file analytics và nạp key ring Data Protection.
  Vì vậy `/health/live` trả 200 thì host đã qua bước khởi động **[SUY LUẬN]**.

### 7.2 Graceful shutdown (OPS-03)

```text
SIGTERM → /health/ready = 503 ngay
        → vẫn nhận request thêm GameServer:Hosting:DrainDelaySeconds (5 s) để LB rút instance
        → ngừng nhận; request đang chạy có tới GameServer:Hosting:ShutdownTimeoutSeconds (25 s, tính cả drain)
        → commit đang chạy hoàn tất, job nhả lease, analytics drain hàng đợi, logger flush
```

- **Grace period của orchestrator ≥ 35 s** (= `ShutdownTimeoutSeconds` + 10 s). Tăng `ShutdownTimeoutSeconds` thì tăng grace theo.
  `ShutdownTimeoutSeconds` phải lớn hơn `DrainDelaySeconds`, nếu không thì exit 1.
- Đĩa analytics bị treo (I/O stall) **không** bị ngân sách drain giới hạn. Process đứng tới khi orchestrator kill. Payment vẫn an
  toàn (receipt chờ sweeper), login trong hàng đợi thì mất.
- Đã drill trên Couchbase local (`docs/guides/local-real-env-testing.md` §5): dừng êm khi đang commit (ACC-28) và crash giữa
  commit.

---

## 8. Log và analytics trên đĩa

### 8.1 Log (`ObservabilityOptions.Logging`)

- File `{Logging:Directory}/{gameId}-{instanceId}-{yyyyMMdd}[_NNN].log`, compact JSON (`@t` UTC).
- Rolling 100 MiB mỗi file (`FileSizeLimitBytes`), giữ 30 file (`RetainedFileCountLimit`), `RetainedFileTimeLimitDays` tuỳ chọn.
  Theo mặc định là khoảng 3 GiB mỗi instance id **[SUY LUẬN]**.
- Hàng đợi async 10 000 (`BufferSize`). Hàng đợi đầy thì bỏ dòng và đếm `log_dropped_total`, không chặn request.
- Thư mục `logs` mặc định là tương đối, tức nằm trong filesystem của container. Đặt `Observability__Logging__Directory` vào volume
  bền, hoặc dùng sidecar shipper (LOG-03).
- Che secret theo tên thuộc tính (token, password, secret, pepper, …). Exception và object log bằng `{x}` **không** được che.

### 8.2 Analytics (slice 012, `AnalyticsOptions`, section `GameServer:Analytics`)

- File `{gameId}-{instanceId}-{event}-{yyyyMMdd}.jsonl` (event `login`, `payment`), mỗi ngày UTC một file. File của ngày UTC trước
  là hoàn chỉnh.
- **Volume:** volume local/block bền **riêng cho mỗi pod**. Không dùng NFS/SMB vì lock một-writer không đáng tin trên đó. Đặt
  đường dẫn tuyệt đối, ví dụ `GameServer__Analytics__Directory=/var/lib/gameserver/analytics` kèm
  `volumeMounts.mountPath: /var/lib/gameserver/analytics`. Thư mục mặc định `analytics` (tương đối) mất khi container bị thay.
- **Instance id:** `Observability:InstanceId` → `HOSTNAME` → tên máy. Hai process dùng chung một thư mục mà trùng id thì process
  thứ hai exit 1. K8s **Deployment** đổi tên pod mỗi lần restart, sinh bộ file mới mỗi lần, và `RetainLocalDays` không dọn file
  của pod cũ. Dùng **StatefulSet**, hoặc đặt `Observability:InstanceId` cố định cho mỗi volume.
- **Múi giờ:** `GameServer:Analytics:TimeZone` (tên IANA) bắt buộc ở Staging/Production. Flavor mẫu dùng `Asia/Ho_Chi_Minh`. Id
  Windows (`SE Asia Standard Time`) bị từ chối. Image phải có tzdata (mục 2.3).
- **`GameServer:Analytics:Enabled` phải giống nhau trên mọi instance** của một deployment. Instance tắt sẽ ack entry payment của
  instance bật mà không ghi record.
- `Enabled=true` mà host không đăng ký sink (không gọi `AddAnalyticsFiles()`) thì Staging/Production exit 1.
- **Độ bền:** batch có payment được flush **xuống đĩa** (fsync) trước khi ack. Tốn khoảng +58 ms p95 cho một lần mua trên máy dev.
  Batch chỉ có login chỉ flush xuống OS.
- **Shipper** (Vector, Fluent Bit, Filebeat): trễ ≤ 60 s (LOG-03). Coi file của ngày UTC trước là hoàn chỉnh. Tail file hôm nay
  được. **Bỏ qua dòng không parse được** (dòng bị cắt do crash có thể nằm giữa file).
- Dung lượng khoảng 0,4 GB mỗi triệu login mỗi ngày (chưa nén). Payment không đáng kể.
- Retention: server không xoá file, trừ khi đặt `GameServer:Analytics:RetainLocalDays`. Data lake giữ 25 tháng (chính sách của
  kho dữ liệu, server không cưỡng chế).
- Key còn lại và mặc định: `QueueCapacity` 10 000, `DeliveryTimeoutMs` 500, `ResumeTrackerCapacity` 200 000, `LowDiskBytes`
  1 GiB, `ErrorWindowSeconds` 300. Không key nào là secret.

### 8.3 Hazard rollback và dead letter của analytics

- **Rolling deploy từ build trước 012:** an toàn. Instance cũ gặp receipt có entry kind `analytics` thì ghi một attempt rồi
  backoff; instance mới giao.
- **Rollback toàn bộ về build trước 012:** receipt còn chờ lúc đó bị build cũ thử lại, rồi **dead-letter** sau
  `GameServer:Outbox:MaxRelayAttempts` (10 lần, khoảng 3 h). Record payment thiếu cho tới khi re-drive.
- Theo dõi `outbox_dead_letter_total` > 0. Re-drive tool (endpoint GM/ops) chưa có → 015. Re-drive thủ công trên Couchbase (đã
  chạy thử trong integration test):

```sql
UPDATE `<bucket>`.`g_<gameId>`.`receipts` r
SET r.outboxDeadLetter = false, r.outboxAttempts = 0
UNSET r.outboxNextAttemptAtUtc
WHERE r.outboxPending = true AND r.outboxDeadLetter = true
  AND ANY e IN r.outbox SATISFIES e.kind = "analytics" END;
```

  Sau đó sweeper relay lại. Bản sao analytics được dedupe ở downstream theo `transactionID`.

---

## 9. Observability: OTLP, metric, cảnh báo

- OTLP bật khi `Observability:Otlp:Endpoint` có giá trị (URI tuyệt đối). `Observability:Otlp:Protocol` là `grpc` (mặc định)
  hoặc `http/protobuf`. Với `http/protobuf`, `/v1/traces` và `/v1/metrics` được nối vào endpoint. URI hay protocol sai thì
  exit 1. Prometheus đi qua collector. README ghi rõ **chưa kiểm với collector OTLP thật**.
- Metric không bao giờ có tag player hoặc request (OBS-02).

| Metric (đã grep trong `src/`) | Cảnh báo đề xuất | Nguồn ngưỡng |
| --- | --- | --- |
| `outbox_dead_letter_total` | > 0 | PCR §18 |
| `analytics_sink_errors_total` | > 0 | guide §7.2 |
| `analytics_events_dropped_total{event="login"}` | tăng liên tục > 5 phút | guide §7.2 |
| `analytics_delivery_failures_total` | tăng liên tục | guide §7.2 |
| `analytics_queue_entries` | > 50 % `QueueCapacity` | guide §7.2 |
| `analytics_dir_free_bytes`, `log_dir_free_bytes` | < `LowDiskBytes` (1 GiB) | PCR §13, §18 |
| `log_sink_errors_total`, `log_dropped_total` | > 0 | PCR §13 |
| `account_purge_overdue_seconds` | `max` > 86400, **hoặc** gauge vắng mặt > 3 × interval của job | PCR §16 (SC-006) |
| `account_purge_step_failures_total`, `account_purges_total{result="fenced_out"}` | > 0 | PCR §16 |
| `job_runs_total`, `job_lease_losses_total` | lease loss lặp lại | PCR §16 |
| `command_results_total{code}`, `http_errors_total{code}` | tỉ lệ lỗi 5xx / `INTERNAL` | PCR §13 |

- `txn_cleanup_lost_total` (OPS-05) **chưa được implement** (grep không thấy) → 015.

---

## 10. Data Protection key ring (`DataProtectionSettings`, section `DataProtection`)

- Key ring dùng chung cho mọi instance của một deployment, lưu ở document `dataprotection/keyring` trong Couchbase. Không cần
  volume hay file share.
- `DataProtection:MasterKey`: base64 của đúng 32 byte ngẫu nhiên, mã hoá key at rest (AES-256-GCM). Ngoài Dev/Test, thiếu hoặc sai
  thì exit 1.
- Rotate master key: đặt `MasterKeyId` mới cùng `MasterKey` mới, chuyển khoá cũ vào `DataProtection:PreviousMasterKeys:<id cũ>`
  (secret, xem mục 4). Rotate key data-protection theo yêu cầu: `POST /gm/v1/ops/dataprotection/rotate`. Key kích hoạt sau
  `RotationDelaySeconds` (900).
- Lịch: `KeyLifetimeDays` 90, `ActivationLeadHours` 48, `RefreshMinutes` 5.
- Mất master key thì key ring không giải mã được: cookie GM, token CSRF và cookie OIDC đều hỏng **[SUY LUẬN]**. Dữ liệu player
  không bị ảnh hưởng. Sao lưu master key trong secret manager.
- Data Protection dùng đồng hồ hệ thống, không dùng `IClock`.

---

## 11. GM: SSO và CORS (`GmOptions`, section `Gm`)

- Ngoài Development/Test, đăng nhập GM **chỉ qua OIDC**. `Gm:Oidc:Authority` (https), `Gm:Oidc:ClientId`, `Gm:Oidc:ClientSecret`
  bắt buộc (`GmOptionsValidator`).
- Đăng ký redirect URI ở IdP: `https://<host GM>/gm/v1/auth/sso/callback` (`GmSso.CallbackRoute`). Luồng: code + PKCE,
  `response_mode=query`.
- `Gm:Oidc:RequireMfa` mặc định bật ngoài Dev/Test (claim `amr`). Nếu IdP ép MFA bằng chính sách mà không phát `amr` thì đặt
  `false` và ghi nhận. `Gm:Oidc:EmailVerifiedClaim` mặc định là `email_verified` (Entra ID cần optional claim).
- Staff đầu tiên tạo qua `Gm:Oidc:BootstrapManagers[]` (`{staffId, email}` hoặc `{staffId, issuer, subject}`). Production không có
  login local. `Gm:Bootstrap:Staff` ngoài Dev/Test thì exit 1.
- `Gm:Cors:AllowedOrigins[]`: rỗng = same-origin (mặc định). Chỉ origin chính xác, không wildcard, `https` ngoài Development.
- GM frontend do dev khác làm (TBD-21). API và OpenAPI commit ở `docs/api/gm-openapi.json`. Route OpenAPI runtime chỉ có ở
  Dev/Test.
- Thu hồi quyền staff có hiệu lực ≤ `Gm:CacheSeconds` (60 s).

---

## 12. IAP (`IapOptions`, section `Iap`)

- `IapOptionsValidator` chạy khi khởi động, không chạy ở `--provision`.
  - Ngoài Dev/Test, `Iap:UseFakeVerifier=true` thì exit 1.
  - Khi dùng verifier thật, thiếu bất kỳ key nào sau đây là một lỗi riêng: `Iap:Google:PackageName`,
    `Iap:Google:ServiceAccountEmail`, `Iap:Google:ServiceAccountPrivateKeyPem`, `Iap:Apple:BundleId`,
    `Iap:Apple:RootCertificateBase64`.
  - PEM phải là private key RSA ký được. Root Apple phải là certificate DER base64 (Apple Root CA G3).
- Beta/uat (Staging) dùng IAP sandbox, không có payment thật (quyết định owner 2026-10-30).
- **Apple ở Production chỉ nhận receipt Production.** App Store review mua bằng sandbox trên bản production nên bị từ chối
  `INVALID_RECEIPT`, một lý do app review hay reject. **Chính sách owner còn mở**, phải chốt trước Live.
- Google: test purchase bị từ chối ở Production. Timeout gọi store `Iap:StoreTimeoutSeconds` (10 s).
- Refund/revoke S2S (Google RTDN, Apple v2) chưa có, thuộc slice 014.

---

## 13. Job, lease, xoá tài khoản

- Job trong `ModuleDescriptor.Jobs` chạy trên **một** instance tại một thời điểm, phối hợp bằng document `leases/leases::{jobName}`
  (CAS, không expiry). Bật/tắt bằng `GameServer:Jobs:Enabled` (mặc định true). Lease 60 s, renew 20 s, poll 30 s.
- **Không bao giờ xoá `leases::*`** (PCR platform §16). Token tạo lại bắt đầu từ 1, trong khi purge record giữ token lớn hơn, nên
  các player đó bị bỏ qua mãi (LogError "never delete leases::*", `account_purges_total{result=fenced_out}`). Cách sửa: đặt
  `fencingToken` của `leases::identity.account-purge` (KV, CAS) lớn hơn token lớn nhất trong `account_deletions`.
- Purge job `identity.account-purge`: mặc định 50 player mỗi 10 phút (khoảng 7 200/ngày) (`GameServer:Purge:*`). Grace trước
  purge là `Identity:Deletion:GraceDays` (30, 1..90).
- Rủi ro vận hành mở (→ 015 runbook):
  - restore backup trước purge làm dữ liệu quay lại, nên phải chạy lại purge cho record `purged`;
  - tombstone Couchbase giữ key cũ khoảng 3 ngày;
  - retention log nên ≤ grace + thời gian purge, vì log giữ `RequestId` cạnh player id.

---

## 14. Đồng hồ (NTP)

- **Node Couchbase:** NTP bắt buộc (OPS-01, transaction).
- **Node app:** server dùng đồng hồ hệ thống qua `IClock`. Thời gian ảnh hưởng tới:
  - hết hạn lease job (`leaseUntilUtc`);
  - kiểm `|now − timestamp| ≤ 10 phút` của Game Center;
  - thời hạn phiên GM và phiên player;
  - Data Protection;
  - ngày UTC của file analytics;
  - `serverTimeUtc` trả cho client (TIME-01).

  Mọi node app phải chạy NTP. Lệch giờ giữa các instance làm lease bị takeover sớm **[SUY LUẬN]**.

---

## 15. Luồng deploy

### 15.1 Các bước

| # | Bước | Lệnh / hành động | Đạt khi | Thất bại thì |
| --- | --- | --- | --- | --- |
| 1 | Build + test | `dotnet build GameServer.slnx -c Release`; `dotnet test --solution GameServer.slnx -c Release --no-build` (kèm integration Couchbase trên 7.6 trước khi merge, chưa có CI → 015) | build sạch, test xanh | dừng |
| 2 | Publish | `dotnet publish <host>.csproj -c Release -o out/<host>`; đóng image → 015 | artifact có `appsettings.{Flavor}.json`, `config/` | dừng |
| 3 | Provision | `ASPNETCORE_ENVIRONMENT=<Flavor> … dotnet <host>.dll --provision` với identity **provisioner**, từ thư mục publish | exit 0, dòng `provision: done:` | exit 1: sửa cấu hình. exit 2: sửa nguyên nhân rồi chạy lại |
| 4 | Deploy (rolling) | thay từng instance; credential **runtime**; grace ≥ 35 s; volume logs/analytics | instance mới khởi động, không có `Game server startup validation failed:` | exit 1: đọc danh sách lỗi; rollback |
| 5 | Readiness | `GET /health/ready` = 200 `Healthy` (không phải `Degraded`) trên từng instance trước khi chuyển instance kế tiếp | LB đưa instance vào | không lên: xem log, rollback |
| 6 | Smoke | `GET /v1/time`; login guest + một command qua Unity SDK hoặc load tool; GM SSO đăng nhập được; beta: một purchase sandbox → có dòng trong file `…-payment-…jsonl`; kiểm metric | tất cả đạt | rollback |

### 15.2 Rollback

| Tình huống | Hành động | Lưu ý |
| --- | --- | --- |
| Instance mới không khởi động (exit 1) | sửa biến môi trường hoặc deploy lại artifact cũ | cấu hình sai bị chặn trước khi nhận traffic, chưa hỏng dữ liệu |
| Lỗi sau khi phục vụ | deploy lại artifact trước (rolling, cùng grace) | schema chỉ thêm (provision không bao giờ xoá hay sửa), nên **không có rollback DB**. Collection/index thừa của bản mới không hại |
| Rollback qua mốc slice 012 | như trên, rồi theo dõi `outbox_dead_letter_total`; sau khi roll-forward chạy N1QL re-drive (mục 8.3) | hazard dead-letter |
| Latency/CPU do nén | `Couchbase__Compression=false`, restart | không cần build |
| Cần chặn client trong lúc xử lý | bật maintenance hoặc tăng phiên bản client tối thiểu qua GM API gate (`gate/gate`, PCR platform §15): 503 `MAINTENANCE` / 426 `UPDATE_REQUIRED`, áp dụng ≤ 10 s | GM, health, Dev không bị chặn |
| Runbook deploy/rollback có drill | **→ 015** | OPS-06, ACC-19 |

### 15.3 Sơ đồ 2 — deploy flow và rollback (mô tả cho SVG inline)

```text
Nodes (theo thứ tự trái → phải, hàng chính):
  N1 "Build + test"            (dotnet build / dotnet test)
  N2 "Publish"                 (dotnet publish → out/<host>; image → 015)
  N3 "Provision"               (<host>.dll --provision, identity provisioner)
  N4 "Deploy rolling"          (identity runtime, grace ≥ 35 s, volume logs/analytics)
  N5 "Readiness"               (/health/ready = 200 Healthy)
  N6 "Smoke"                   (/v1/time, login + command, GM SSO, purchase sandbox ở beta)
  N7 "Done"
Hàng phụ (rollback, phía dưới):
  R1 "Sửa cấu hình"            (khi exit 1)
  R2 "Chạy lại provision"      (khi exit 2: idempotent)
  R3 "Deploy lại artifact cũ"  (rolling; DB không rollback)
  R4 "Re-drive analytics"      (nếu rollback qua mốc 012: N1QL mục 8.3)
Edges:
  N1→N2→N3 ; N3 --exit 0--> N4 ; N3 --exit 1--> R1 --> N3 ; N3 --exit 2--> R2 --> N3
  N4→N5 ; N5 --200 Healthy--> N6 ; N5 --503/không lên--> R3
  N4 --exit 1 (startup validation)--> R1
  N6 --đạt--> N7 ; N6 --lỗi--> R3 ; R3 --(nếu qua 012)--> R4
Nhãn trên N3: "exit 0 / 1 / 2". Nhãn trên N4: "SIGTERM → 503 → drain 5 s → ≤ 25 s".
```

---

## 16. Hạng mục còn mở (tổng hợp)

| Hạng mục | Trạng thái | Làm ở |
| --- | --- | --- |
| Dockerfile, container guide, manifest K8s (StatefulSet, volume, probe, grace) | chưa có | → 015 |
| CI (build, test, integration Couchbase, secret scan ACC-48) | chưa có `.github/` | → 015 |
| Cổng phát hành TLS (2 test opt-in) | chưa từng chạy | → 015, **bắt buộc trước Live** |
| Runbook deploy/rollback, restore drill, RPO/RTO (TBD-09 còn mở, ACC-19) | chưa có | → 015 |
| Load test report (TST-03), PERF-05 budget | chưa có | → 015 |
| Re-drive tool cho analytics dead-letter | N1QL thủ công | → 015 |
| Metric `txn_cleanup_lost_total` | chưa implement | → 015 |
| Couchbase outage drill (`tools/local/couchbase-outage.ps1`) | owner chưa chạy | → 015 |
| Kiểm cluster nhiều node, collector OTLP thật, sandbox Apple/Google, thiết bị thật (ACC-03/29) | chưa | → 015 / thủ công trước release |
| Chính sách receipt sandbox của Apple ở Production | owner chưa chốt | owner, trước Live |
| Game Center pin `gc-prod-12` hết hạn 2027-05-27 | theo dõi | vận hành |

---

## 17. Checklist deploy

Cột "Ai ký" là vai trò đề xuất: **DevOps**, **DBA** (quản trị Couchbase), **Security**, **Backend lead**, **Data team**,
**Owner**. Cột "Trạng thái": **Tự động** = host tự chặn khi khởi động (vẫn kiểm bằng cách khởi động một lần); **Thủ công** =
người kiểm; **→ 015** = chưa có công cụ hay drill. Frontend: thêm cột checkbox trống và cột "Ngày / chữ ký" khi in.

### 17.1 Database (11 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| DB-01 | Couchbase Server ≥ 7.6, bật service Data, Query, Index | UI → Servers, hoặc REST `GET /pools/default` | DBA | Thủ công |
| DB-02 | Một bucket loại Couchbase cho mỗi môi trường; Live trên cluster riêng | danh sách bucket; `Couchbase:BucketName` của từng flavor khác nhau | DBA | Thủ công |
| DB-03 | Replica: một node = 0; Live ≥ 2 node + ≥ 1 replica; không durability `None` | bucket settings. Một node mà replicas ≥ 1 thì mọi command lỗi `DurabilityImpossible` | DBA | Thủ công |
| DB-04 | NTP đồng bộ trên mọi node Couchbase | `timedatectl` / `chronyc tracking` trên từng node | DBA | Thủ công |
| DB-05 | User provisioner có đúng `bucket_admin[b]`, `query_manage_index[b]`, `data_reader[b]`, `query_system_catalog` | `GET /settings/rbac/users`; `--provision` exit 0 | DBA, Security | Thủ công |
| DB-06 | User runtime chỉ có 6 role data/query trên scope `[b:g_<gameId>]` | `--provision` bằng user runtime **phải** exit 2 với `HTTP 403`; instance chạy được với `ProvisionOnStartup=false` | DBA, Security | Thủ công |
| DB-07 | `--provision` exit 0 trước mỗi deploy; chạy lại báo toàn `present` | log pipeline có `provision: done: X created, Y present` | DevOps | Thủ công |
| DB-08 | `txn_meta` có trước instance đầu tiên và không bao giờ bị xoá | dòng `provision: collection g_<id>.txn_meta …`; thiếu thì instance exit 1 (kiểm schema) | DBA | Tự động (thiếu) / Thủ công (không xoá) |
| DB-09 | `Couchbase:ProvisionOnStartup` không bật ở beta/uat/live | grep cấu hình deploy; mặc định false ngoài Development | DevOps | Thủ công |
| DB-10 | Nén Snappy: đo CPU data service + p95/p99 KV trên beta trước Live; biết cách rollback `Couchbase__Compression=false` | dashboard Couchbase + metric command, so với trước khi bật | Backend lead | Thủ công |
| DB-11 | Bộ integration test Couchbase pass trên đúng phiên bản Server/SDK của môi trường | `dotnet test --project tests/GameServer.Platform.Couchbase.IntegrationTests -c Release` (~19–25 phút) | Backend lead | Thủ công (CI → 015) |

### 17.2 Security (14 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| SEC-01 | Live dùng `couchbases://`; `Couchbase:TrustCertificatePath` trỏ **root CA** (không phải intermediate); node gửi kèm intermediate | khởi động Live thành công; `couchbase://` thì exit 1 | Security, DBA | Tự động (scheme) / Thủ công (CA) |
| SEC-02 | **Cổng TLS:** `CouchbaseSettingsIntegrationTests.Tls_trusts_the_configured_ca_and_refuses_another` và `CouchbaseProvisioningTests.Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls` **Passed** (không Skipped) trên cluster TLS có CA riêng | kết quả test với `COUCHBASE_TLS_CONNSTR` + `COUCHBASE_CA_PATH` | Backend lead, Security | **→ 015, bắt buộc trước Live** |
| SEC-03 | 6 key `GameServer:SecretKeys` chỉ đến từ secret provider hoặc biến môi trường; không file nào chứa giá trị | khởi động thành công ngoài Dev/Test (có giá trị trong file thì exit 1); review manifest | Security | Tự động |
| SEC-04 | `DataProtection:PreviousMasterKeys:*` (nếu có) cũng ở secret provider | review cấu hình; check **không** tự chặn key này | Security | Thủ công |
| SEC-05 | Secret scan repo + log trong CI (ACC-48) | job CI | Security | → 015 |
| SEC-06 | Credential riêng cho từng môi trường (user Couchbase, pepper, master key, OIDC client, service account) | đối chiếu secret manager | Security | Thủ công |
| SEC-07 | `Identity:GuestPepper` của Live được sao lưu và **không đổi** sau khi có player | quy trình secret manager | Security, Owner | Thủ công |
| SEC-08 | Không có công cụ dev ở beta/uat/live: `GET /gm/v1/openapi/gm.json` → 404, header `X-Test-Player` không xác thực được, không có staff seed | gọi thử trên từng môi trường | Security, QA | Tự động + kiểm tay |
| SEC-09 | Flavor `Dev` chỉ truy cập được từ mạng nội bộ/VPN | firewall/proxy rule | Security, DevOps | Thủ công |
| SEC-10 | TLS kết thúc ở proxy; Kestrel không mở ra Internet; HTTP→HTTPS redirect ở proxy | quét cổng từ ngoài; `curl -I http://…` | DevOps | Thủ công |
| SEC-11 | ForwardedHeaders: `Enabled=true`, `KnownProxies`/`KnownNetworks` đúng subnet proxy, `ForwardLimit` = số hop tin cậy; proxy giữ header `Host` | khởi động (cấu hình sai thì exit 1); request thử: IP client trong log/audit là IP thật | DevOps, Security | Tự động (cú pháp) / Thủ công (giá trị) |
| SEC-12 | GM SSO: Authority https, client riêng cho môi trường, redirect `https://<host>/gm/v1/auth/sso/callback` đăng ký ở IdP, `RequireMfa` bật (hoặc ghi lý do tắt), `BootstrapManagers` đặt | đăng nhập GM thử qua SSO; thiếu Oidc thì exit 1 | Security | Tự động (thiếu) / Thủ công (luồng) |
| SEC-13 | `Gm:Cors:AllowedOrigins` rỗng hoặc chỉ origin https chính xác; khuyến nghị `/gm/**` chỉ từ mạng nội bộ | review cấu hình; wildcard hoặc http thì exit 1 | Security | Tự động (cú pháp) / Thủ công |
| SEC-14 | Pin Game Center (`gc-prod-12`, hết hạn 2027-05-27) có nhắc lịch; cập nhật qua `Identity:Providers:GameCenter:TrustedRootsBase64` / `IntermediatesBase64` | lịch vận hành; theo dõi LogError `PROVIDER_UNAVAILABLE` | Backend lead | Thủ công |

### 17.3 Config (9 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| CFG-01 | `ASPNETCORE_ENVIRONMENT` đúng `Beta`/`Uat`/`Live` (hoa thường) | dòng đầu của `--provision`: `environment live (Production)` | DevOps | Tự động (sai thì exit 1) |
| CFG-02 | Working directory = thư mục publish (content root) | khởi động OK; sai thì exit 1 kèm `found: no, content root …` | DevOps | Tự động |
| CFG-03 | Mọi biến bắt buộc của flavor (mục 3.4) đã đặt | khởi động một lần, không có `Game server startup validation failed:` | DevOps | Tự động |
| CFG-04 | `GameServer:GameId` đúng game, không đổi sau Live (đổi = scope mới) | `provision: game <id>…` | Backend lead | Thủ công |
| CFG-05 | Overlay config `config/env/Staging` / `config/env/Production` đúng (beta và uat dùng chung Staging) | review thư mục `config/` trong artifact | Backend lead | Thủ công |
| CFG-06 | Cổng nghe đặt qua `ASPNETCORE_URLS` / `--urls` và khớp probe/LB | probe 200 | DevOps | Thủ công |
| CFG-07 | `GameServer:Analytics:TimeZone` là tên IANA; `GameServer:Analytics:Enabled` giống nhau trên mọi instance | khởi động (zone sai thì exit 1); so cấu hình các instance | DevOps, Data team | Tự động (zone) / Thủ công (Enabled) |
| CFG-08 | IAP: verifier thật, đủ 5 key `Iap:*`; không `Iap:UseFakeVerifier=true` | khởi động (thiếu thì exit 1); beta: một purchase sandbox thành công | Backend lead | Tự động + smoke |
| CFG-09 | Grace period orchestrator ≥ `GameServer:Hosting:ShutdownTimeoutSeconds` + 10 s (mặc định ≥ 35 s) | manifest; drill dừng giữa commit | DevOps | Thủ công (drill → 015) |

### 17.4 Observability (9 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| OBS-01 | Thư mục log trên volume bền hoặc có shipper (`Observability__Logging__Directory`) | restart pod, file log cũ còn hoặc đã ship | DevOps | Thủ công |
| OBS-02 | Thư mục analytics trên volume local/block bền riêng mỗi pod, đường dẫn tuyệt đối, user container ghi được | restart pod, file `.jsonl` còn; không ghi được thì exit 1 | DevOps | Thủ công |
| OBS-03 | Instance id ổn định và duy nhất mỗi thư mục (StatefulSet hoặc `Observability:InstanceId`) | tên file không đổi qua restart; trùng id thì exit 1 | DevOps | Thủ công |
| OBS-04 | Image có tzdata (aspnet:10.0 Ubuntu hoặc chiseled-extra; không Alpine/chiseled thường) | khởi động Staging với `TimeZone=Asia/Ho_Chi_Minh` thành công | DevOps | → 015 (Dockerfile) |
| OBS-05 | Shipper: trễ ≤ 60 s, bỏ dòng không parse được, coi file ngày UTC trước là hoàn chỉnh | kiểm cấu hình shipper; data team nhận được file | DevOps, Data team | Thủ công |
| OBS-06 | OTLP: `Observability:Otlp:Endpoint` + `Protocol` đặt, collector nhận trace/metric | thấy span `command {operation}` trên backend tracing | DevOps | Thủ công (chưa kiểm với collector thật) |
| OBS-07 | Cảnh báo theo bảng mục 9 đã cấu hình | alert rule tồn tại; thử kích một cảnh báo | DevOps | Thủ công |
| OBS-08 | Phát hiện `Degraded` (log/analytics) bằng metric hoặc body probe, không dựa vào HTTP status | alert trên `*_sink_errors_total`, `*_dir_free_bytes` | DevOps | Thủ công |
| OBS-09 | Retention log ≤ grace xoá tài khoản + thời gian purge (quyền riêng tư) | chính sách log store | Security, Owner | → 015 (runbook) |

### 17.5 Capacity (6 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| CAP-01 | Số instance × limit per instance (`GameServer:Limits:CommandConcurrency` 200, `GmConcurrency` 50, queue ×2, body 64 KiB / 1 MiB) đủ cho tải dự kiến | tính toán + load test | Backend lead, DevOps | → 015 (load test TST-03, PERF-05) |
| CAP-02 | Luôn ≥ 1 instance chạy (lost-transaction cleanup), khuyến nghị ≥ 2 cho HA; bảo trì không tắt hết instance | manifest (`replicas`, PodDisruptionBudget) | DevOps | Thủ công |
| CAP-03 | Đĩa: log ≈ 100 MiB × 30 file mỗi instance id; analytics ≈ 0,4 GB / triệu login / ngày; dư > 1 GiB (`LowDiskBytes`) | kích thước volume; metric `*_dir_free_bytes` | DevOps | Thủ công |
| CAP-04 | Kết nối KV: (`NumKvConnections`..`MaxKvConnections`) × số node × số instance trong giới hạn của cluster | tính toán, dashboard Couchbase | DBA | Thủ công |
| CAP-05 | Throughput purge (mặc định ≈ 7 200 player/ngày) đủ; cảnh báo overdue bật | metric `account_purge_overdue_seconds` | Backend lead | Thủ công |
| CAP-06 | `Couchbase:ProvisioningTimeoutSeconds` đủ cho build index trên dữ liệu thật | lần `--provision` trên môi trường có dữ liệu; exit 2 vì timeout thì chạy lại hoặc tăng | DBA | Thủ công |

### 17.6 Release / rollback (8 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| REL-01 | Đọc bảng breaking change của PCR platform §17, §18 (và PCR module) khi nâng phiên bản framework | ghi chú release | Backend lead | Thủ công |
| REL-02 | Rolling deploy: từng instance qua readiness 200 `Healthy` rồi mới tới instance kế; LB dùng `/health/ready` | log deploy | DevOps | Thủ công |
| REL-03 | Smoke sau deploy (mục 15.1 bước 6) đạt | checklist smoke | QA, DevOps | Thủ công |
| REL-04 | Kế hoạch rollback: artifact trước còn sẵn; không rollback DB (schema chỉ thêm) | artifact registry | DevOps | Thủ công |
| REL-05 | Rollback qua mốc 012: theo dõi `outbox_dead_letter_total`, có sẵn N1QL re-drive | runbook | DevOps, Data team | Thủ công (tool → 015) |
| REL-06 | Tương thích client cũ: kế hoạch dùng gate (`minClientVersions`, maintenance) và ma trận tương thích (OPS-06) | review | Backend lead, Owner | → 015 (ACC-19) |
| REL-07 | Owner chốt chính sách receipt sandbox của Apple ở Production trước Live | quyết định ghi lại | Owner | Mở |
| REL-08 | Runbook deploy/rollback đã drill | biên bản drill | DevOps | → 015 |

### 17.7 Backup / restore (6 mục)

| ID | Mục | Cách kiểm | Ai ký | Trạng thái |
| --- | --- | --- | --- | --- |
| BAK-01 | Backup gồm **mọi** collection của scope `g_<gameId>`, kể cả `txn_meta`, ở **cùng một điểm thời gian** (DAT-08) | cấu hình công cụ backup (cbbackupmgr / backup service) | DBA | → 015 |
| BAK-02 | Restore drill, RPO/RTO ghi nhận (TBD-09 còn mở, ACC-19) | biên bản drill | DBA, Owner | → 015 |
| BAK-03 | Sau restore: chạy lại purge cho mọi record `account_deletions` ở trạng thái `purged` | runbook | Backend lead | → 015 |
| BAK-04 | Secret không nằm trong backup DB (master key, pepper, mật khẩu) được sao lưu ở secret manager | quy trình secret manager | Security | Thủ công |
| BAK-05 | File analytics/log được ship ra ngoài; backup DB không bao gồm chúng | data lake nhận đủ file | Data team | Thủ công |
| BAK-06 | Nếu dùng XDCR cho DR: XDCR **không** replicate metadata transaction (V17); có runbook | review thiết kế DR | DBA | → 015 |

**Tổng: 63 mục.** Database 11, Security 14, Config 9, Observability 9, Capacity 6, Release/rollback 8, Backup/restore 6.

---

## 18. Ghi chú cho người dựng HTML (T5.4)

- Mục lục theo các mục 1–17. Mỗi đường dẫn file là `<code>` (không link ra ngoài; tài liệu offline). Có thể link tương đối tới
  `../guides/*.md`, `../pcr/*.md`.
- Hai SVG inline: Sơ đồ 1 (mục 1) và Sơ đồ 2 (mục 15.3). Đọc được ở cả dark lẫn light.
- Checklist mục 17: bảng có cột checkbox và cột "Ngày / chữ ký". Print CSS: mỗi nhóm bắt đầu trang mới, header bảng lặp lại khi
  sang trang, ẩn mục lục và sơ đồ khi in (hoặc in sơ đồ đơn sắc).
- Badge: "→ 015" (cam), "[ĐÃ CHẠY]" (xanh), "[SUY LUẬN]" (xám), "Tự động" / "Thủ công" / "Mở".
- Footer: "Đúng với commit 175df65 (2026-10-31). Khi đổi pipeline, hosting hoặc deploy: cập nhật trang này (DoD)."
- **Không** thêm giá trị secret nào vào HTML, kể cả ví dụ. Chỉ dùng `<…>` làm placeholder.

---

## 19. Đính chính so với plan (`00_plan.md` §1, §5)

| Plan | Thực tế ở 175df65 |
| --- | --- |
| "→ slice 013" (ops-readiness) | đánh số lại: ops-readiness = **015** (013 giftcode, 014 iap-notifications; `00_summary.md`) |
| 5c viết "sau task 4" (packaging) | task 4 bị owner bỏ (2026-10-30); 5c viết từ hiện trạng sau 010–012 |
| `StoreKind.LocalFile`, `LocalStore:Folder` | `StoreKind.File`, `FileStore:Folder` (`FileStoreOptions`, `GameServer.Platform.Testing/FileStore`) |
| lệnh `SampleHost --provision-only` (T1.7) | switch là `--provision` (`GameServerCommandLine.ProvisionSwitch`), exit 0/1/2 |
| "`txn_meta` tạo trước" như một bước tay | `--provision` tự tạo `txn_meta` **trước tiên**; instance kiểm khi khởi động và exit 1 nếu thiếu |
| Done T5.3: key nằm trong `GameServerOptions`/`CouchbaseOptions`/`ObservabilityOptions` | còn có `AnalyticsOptions` (`GameServer:Analytics`), `DataProtectionSettings` (tên "Settings", không phải "Options"), `GmOptions`, `IapOptions`, `IdentityOptions`, `IdentityProviderOptions`, `FileStoreOptions` |
| artifact gồm "image" | không có Dockerfile; image và manifest → 015 |
| `00_summary.md` ghi `GameServer:Environment` ∈ local/dev/… | key thật là `GameServer:EnvironmentId` (đính chính phụ, không thuộc plan) |
| Đề bài 5c chỉ nêu test `CouchbaseSettingsIntegrationTests.Tls_…` | cổng TLS gồm **hai** test; test thứ hai là `CouchbaseProvisioningTests.Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls` |
