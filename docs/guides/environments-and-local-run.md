# Môi trường (flavor) và chạy local

Một bản build chạy ở mọi môi trường; môi trường được chọn bằng **flavor** = tên môi trường của host. Hướng dẫn này gồm:
chọn flavor, biến môi trường bắt buộc, chạy local một lệnh, file store, cấu hình Couchbase, TLS, quyền RBAC và lệnh
provisioning. Hợp đồng vận hành (switch, exit code, tiền tố output): `docs/pcr/platform.md` mục 17. Nhiều instance, tải và
drill sự cố trên Couchbase thật: `docs/guides/local-real-env-testing.md`.

## 1. Flavor

| Flavor (tên môi trường của host) | `GameServer:DeploymentEnvironment` | `GameServer:EnvironmentId` | `GameServer:Store` | Dữ liệu | Công cụ dev |
| --- | --- | --- | --- | --- | --- |
| `Local` | Development | `local` | `File` | thư mục `FileStore:Folder` (mẫu: `../_database/dbbase`) | bật |
| `Dev` | Development | `dev` | `Couchbase` | bucket của team, replicas 0 trên một node | bật |
| `Beta` | Staging | `beta` | `Couchbase` | bucket riêng; IAP sandbox | tắt |
| `Uat` | Staging | `uat` | `Couchbase` | bucket riêng; IAP sandbox | tắt |
| `Live` | Production | `live` | `Couchbase` | cluster riêng (DAT-01), bắt buộc TLS | tắt |

"Công cụ dev" = xác thực thử nghiệm qua header `X-Test-Player`, module cheat, seed staff GM, tài liệu GM OpenAPI, verifier
IAP và identity provider giả. Chỉ có ở Development/Test.

**Chọn flavor**: đặt `ASPNETCORE_ENVIRONMENT` (hoặc `DOTNET_ENVIRONMENT`) = `Local`, `Dev`, `Beta`, `Uat` hoặc `Live`.
Viết đúng hoa thường: trên Linux tên file phân biệt hoa thường. Launch profile và script local đặt sẵn giá trị này
(mục 3). Host nạp cấu hình theo thứ tự, nguồn sau ghi đè nguồn trước:

```text
appsettings.json  <  appsettings.{Flavor}.json  <  biến môi trường  <  dòng lệnh (--urls, --Section:Key=value, …)
```

- `appsettings.json` của host mẫu là phần chung, không chứa giá trị riêng của flavor nào (không deployment environment, environment
  id, store, seed staff hay secret). Mọi giá trị của flavor nằm trong `appsettings.{Flavor}.json`.
- Game có thể thêm flavor riêng: tạo `appsettings.<Tên>.json` đặt `GameServer:DeploymentEnvironment` (và `GameServer:EnvironmentId`
  không trùng các id chuẩn ở bảng trên).
- Overlay config game: `config/env/<DeploymentEnvironment>`. **Beta và uat dùng chung overlay `Staging`** (quyết định của owner);
  overlay riêng theo `EnvironmentId` chưa có.

**Kiểm tra khi khởi động** (trước khi nhận traffic; mọi lỗi của một lần khởi động được báo **cùng lúc**, host thoát với code 1):

- `GameServer:DeploymentEnvironment` phải là Development, Staging, Production hoặc Test (không phân biệt hoa thường, được chuẩn hoá).
  Không đặt → chỉ dùng tên môi trường của host khi tên đó chính là một deployment environment. Giá trị sai hoặc thiếu
  **không bao giờ** thành Development (fail-closed): thông báo nêu key, các giá trị hợp lệ, file `appsettings.<Tên>.json` mong
  đợi (có tìm thấy không) và bảng flavor.
- `GameServer:EnvironmentId` là id chuẩn (`local`, `dev`, `beta`, `uat`, `live`) thì `GameServer:DeploymentEnvironment` phải đúng
  giá trị của bảng (ví dụ `live` với Development → lỗi nêu cả hai key).
- Store `InMemory` và `File` chỉ được dùng ở Development/Test.
- Secret (mục 2) không được nằm trong file cấu hình nào ngoài Development/Test.
- Thiếu secret của module (SSO GM, pepper, master key Data Protection, khoá IAP) và setting Couchbase được báo chung trong cùng
  một danh sách.
- Khoá IAP được kiểm khi khởi động (từ T055): ngoài Development/Test, `Iap:UseFakeVerifier=true` là lỗi. Khi dùng verifier thật
  (mặc định ngoài Development/Test, hoặc `Iap:UseFakeVerifier=false` ở Development) mỗi key thiếu trong `Iap:Google:PackageName`,
  `Iap:Google:ServiceAccountEmail`, `Iap:Google:ServiceAccountPrivateKeyPem`, `Iap:Apple:BundleId`,
  `Iap:Apple:RootCertificateBase64` là một lỗi nêu biến môi trường; PEM phải là private key RSA ký được, root Apple phải là
  certificate DER base64. Thông báo nêu key, không bao giờ nêu giá trị. Chế độ `--provision` không kiểm IAP.
- Giá trị sai kiểu (ví dụ `Couchbase__KvTimeoutMs=abc`) → lỗi `[configuration] Failed to convert configuration value … at
  '<key>' …` trong cùng báo cáo, thoát 1 (không còn exception không xử lý).

**Beta và uat không có công cụ dev**: không cheat, không xác thực thử nghiệm, không seed staff, không GM OpenAPI (chặt hơn
OPS-09, vì beta/uat có tester bên ngoài và receipt sandbox thật). Công tắc `GameServer:Staging:AllowCheatModule` **chưa có**
(hoãn — PCR platform mục 17); QA cần cheat thì dùng local hoặc dev.

**Môi trường dev phải giới hạn mạng**: flavor `Dev` bật xác thực thử nghiệm (header `X-Test-Player` mạo danh mọi player), cheat
và seed staff GM có mật khẩu commit trong repo (`dev-only-change-me`). Chỉ cho truy cập từ mạng nội bộ / VPN, không bao giờ
mở ra Internet.

## 2. Biến môi trường bắt buộc theo flavor

Secret luôn đến từ biến môi trường (hoặc secret provider), không bao giờ từ file commit (OPS-08). Tên biến = key cấu hình
với `:` thay bằng `__`.

| Flavor | Bắt buộc từ môi trường |
| --- | --- |
| local | không có |
| dev | `Couchbase__Password` (đặt một lần làm biến môi trường **user**) |
| beta, uat | `Couchbase__ConnectionString`, `Couchbase__Username`, `Couchbase__Password`, `DataProtection__MasterKey`, `Gm__Oidc__Authority`, `Gm__Oidc__ClientId`, `Gm__Oidc__ClientSecret`, `Identity__GuestPepper`, khoá IAP (`Iap__Google__PackageName`, `Iap__Google__ServiceAccountEmail`, `Iap__Google__ServiceAccountPrivateKeyPem`, `Iap__Apple__BundleId`, `Iap__Apple__RootCertificateBase64`), khoá provider khi bật (`Identity__Providers__PlayGames__ClientId`, `Identity__Providers__PlayGames__ClientSecret`) |
| live | như beta/uat; `Couchbase__ConnectionString` phải là `couchbases://…` (thêm `Couchbase__TrustCertificatePath` khi dùng CA riêng) |
| lần chạy provisioning (mọi flavor Couchbase) | chỉ `Couchbase__ConnectionString`, `Couchbase__Username`, `Couchbase__Password` của identity quản trị (mục 5.5) |

Secret mặc định (`GameServer:SecretKeys`, không được có giá trị trong file nào ngoài Development/Test):

| Key cấu hình | Biến môi trường |
| --- | --- |
| `Identity:GuestPepper` | `Identity__GuestPepper` |
| `Couchbase:Password` | `Couchbase__Password` |
| `Iap:Google:ServiceAccountPrivateKeyPem` | `Iap__Google__ServiceAccountPrivateKeyPem` |
| `DataProtection:MasterKey` | `DataProtection__MasterKey` |
| `Gm:Oidc:ClientSecret` | `Gm__Oidc__ClientSecret` |
| `Identity:Providers:PlayGames:ClientSecret` | `Identity__Providers__PlayGames__ClientSecret` (mới ở slice 011) |

Thiếu khoá IAP trong bảng → host không khởi động (mục 1). Thông báo lỗi của `CouchbaseSettingsCheck` và của IAP nêu sẵn tên
biến (`Couchbase__Password`, `Iap__Google__PackageName`, …); lỗi của module khác (SSO GM, pepper) nêu key cấu hình — dùng bảng
trên để đổi sang tên biến. Test repository kiểm mọi `samples/SampleHost/appsettings*.json` không chứa secret
nào ở trên, kể cả file local/dev.

## 3. Chạy local

Không cần Couchbase: flavor `Local` dùng file store (mục 4) ở `samples/_database/dbbase` (gitignore), cổng `5080`.

### 3.1 Script (cách chính)

```powershell
./tools/local/run-local.ps1                    # Windows PowerShell 5.1 hoặc PowerShell 7
./tools/local/run-local.ps1 -Port 5090 -Reset  # cổng khác, bắt đầu với store rỗng
```

```bash
tools/local/run-local.sh                       # bash: macOS, Linux, Git Bash
tools/local/run-local.sh --port 5090 --reset
```

Script build `samples/SampleHost` (Debug), chạy `dotnet run --no-build --launch-profile local -- --urls …` từ thư mục
`samples/SampleHost`, chờ `GET /health/ready` = 200 (tối đa 60 s), in khối URL rồi tiếp tục hiện log của host:

```text
Local game server is ready (flavor Local, data ../_database/dbbase)
  Player API   http://localhost:5080/          (routes /api/v1/..., /v1/time)
  Health       http://localhost:5080/health/ready
  GM UI        http://localhost:5080/gm/v1/ui  (staff admin / dev-only-change-me)
  GM OpenAPI   http://localhost:5080/gm/v1/openapi/gm.json
Press Ctrl+C to stop.
```

| Tuỳ chọn PowerShell / bash | Mặc định | Tác dụng |
| --- | --- | --- |
| `-Port <n>` / `--port <n>` | 5080 | host nhận `--urls http://localhost:<n>` |
| `-Reset` / `--reset` | tắt | host nhận `--FileStore:ResetOnStart=true`: **host** xoá nội dung thư mục store sau khi đã khoá nó (mục 4.3) |
| `-Lan` / `--lan` | tắt | nghe mọi interface (`http://0.0.0.0:<n>`) để điện thoại cùng mạng kết nối; in cảnh báo đỏ và các địa chỉ IPv4 LAN |

| Exit code | Ý nghĩa (thông báo nêu nguyên nhân và cách sửa) |
| --- | --- |
| 0 | dừng bằng Ctrl+C sau khi host dừng êm |
| 2 | tham số sai |
| 3 | không tìm thấy .NET SDK của `global.json` (tìm ở `PATH` rồi `~/.dotnet10`) |
| 4 | cổng đang bận — nêu cổng và `-Port` / `--port` |
| 5 | build lỗi |
| 6 | host thoát trước khi sẵn sàng (thông báo của host in ngay phía trên: thư mục đang bị host khác khoá, file hỏng, …) |
| 7 | quá 60 s chưa sẵn sàng — in 20 dòng cuối của file log mới nhất trong `samples/SampleHost/logs/`, dừng host |
| khác | exit code của host sau khi đã sẵn sàng |

Ghi chú:

- **Ctrl+C**: readiness chuyển 503, host drain (`GameServer:Hosting:DrainDelaySeconds`, 5 s), commit đang chạy hoàn tất, thoát 0.
  Script chờ tối đa 35 s rồi kill cả cây process. Không đường nào để lại host chạy sau khi script thoát. Chạy lại giữ nguyên dữ
  liệu (player, phiên, cookie GM).
- **Mỗi lần chạy đều build** (~30 s trên máy dev kể cả khi không có gì đổi, 15 project) trước khi khởi động host (~9 s tới ready).
  Muốn bỏ bước build: dùng launch profile (mục 3.2).
- Script kiểm readiness qua `127.0.0.1` (với `-Lan` host chỉ bind `0.0.0.0`, không bind `::1`).
- `-Lan`: danh sách IPv4 gồm cả adapter ảo (Hyper-V/WSL, ví dụ `172.25.x.x`) — chọn địa chỉ của card Wi-Fi/LAN thật. Host cũng
  ghi một cảnh báo khi Development nghe trên địa chỉ không phải loopback. Chỉ dùng trên mạng tin cậy, dừng ngay khi test xong.
- Hai script cùng thư mục dữ liệu: script thứ hai thoát 6 với thông báo `[filestore] the data folder '…' is used by another host`
  (host thứ hai từ chối trong ≤ 5 s sau khi khởi động).
- `run-local.sh` cần bit thực thi (`git add --chmod=+x`); không có thì chạy `bash tools/local/run-local.sh`.

### 3.2 IDE và `dotnet run` (launch profile)

`samples/SampleHost/Properties/launchSettings.json`:

| Profile | Flavor | URL | Ghi chú |
| --- | --- | --- | --- |
| `local` (mặc định, profile đầu tiên) | `Local` | `http://localhost:5080` | IDE mở `/gm/v1/ui`; cùng thư mục dữ liệu với script |
| `dev-couchbase` | `Dev` | `http://localhost:5080` | Couchbase local, bucket `gameserver-dev`; mật khẩu từ biến môi trường user `Couchbase__Password` (profile không chứa mật khẩu) |

```powershell
dotnet run --project samples/SampleHost                                  # profile local
dotnet run --project samples/SampleHost --launch-profile dev-couchbase   # Couchbase local
```

`dotnet run --launch-profile` dùng thư mục project làm working directory và content root (đã kiểm), nên `../_database/dbbase`
luôn là `samples/_database/dbbase`.

**Đổi cổng**: `--urls` thắng tất cả (`dotnet run --project samples/SampleHost -- --urls http://localhost:5090`). `Urls` trong
`appsettings.Local.json` thắng `ASPNETCORE_URLS` (biến có tiền tố host là nguồn thấp nhất), nên sửa `applicationUrl` của launch
profile **không** đổi được cổng — profile và file local giữ cùng một URL (test kiểm).

### 3.3 Nhiều instance trên Couchbase ("môi trường thật")

`tools/local/start-hosts.ps1 -Count 2` chạy instance trên `5080`, `5081`, … với flavor `Dev` (script đặt
`ASPNETCORE_ENVIRONMENT=Dev` rồi khôi phục giá trị cũ; các biến `GameServer__Store`, `Couchbase__*` và `--urls` của script vẫn
ghi đè file dev). Chi tiết, tải và drill: `docs/guides/local-real-env-testing.md`.

### 3.4 Client Unity

Mặc định `serverUrl` của client mẫu (`SampleGameClient`, `SmokeBuild.cs`, `sdk/unity/ci/smoke-build.ps1`) là
`http://localhost:5080/`. Thiết bị thật: chạy với `-Lan`, đặt `serverUrl` = địa chỉ LAN in ra.

## 4. File store (flavor local)

Store của flavor `Local`: chính `InMemoryDocumentStore` (cùng semantic transaction, kiểm bằng bộ conformance) được nạp từ một
thư mục lúc khởi động và **ghi xuống đĩa mọi thay đổi trước khi nó được nhìn thấy**. Bị từ chối ngoài Development/Test (thông báo
nêu store, môi trường và cách sửa; thư mục không bị tạo hay khoá). File store **không** thay cho test Couchbase.

| Key | Mặc định | Quy tắc |
| --- | --- | --- |
| `FileStore:Folder` | — | bắt buộc khi `GameServer:Store` = `File`; đường dẫn tương đối tính từ content root; trên Windows đường dẫn document dài nhất phải ≤ 259 ký tự (kiểm khi khởi động) |
| `FileStore:ResetOnStart` | false | truyền trên dòng lệnh (`--FileStore:ResetOnStart=true`, script `-Reset`) |

### 4.1 Cấu trúc thư mục

```text
<Folder>/
├── store.json                         {"format":"gameserver-file-store","version":1}
├── .lock                              khoá độc quyền khi host chạy; OS nhả khi process thoát hoặc crash
├── journal.json                       chỉ tồn tại giữa commit point và lúc áp xong commit nhiều document
└── g_<gameId>/
    └── <collection>/
        └── <tên mã hoá>.json          một document: {"key", "expiresAtUtc" (ISO-8601 UTC hoặc null), "content"}
```

- Mỗi document một file JSON (UTF-8, thụt lề, đọc được). Tên file: ký tự `[a-z0-9._-]` giữ nguyên, byte khác → `%XX` (hex hoa),
  tên thiết bị Windows (`con`, …) mã hoá ký tự đầu; tên mã hoá dài hơn 120 ký tự → `~<base32(sha256(key))>` (khoá thật nằm trong
  trường `key`).
- Commit một document: ghi file tạm → rename. Commit nhiều document: ghi `journal.json.tmp` → rename thành `journal.json`
  (**commit point**) → áp từng file → xoá journal. Khởi động: xoá `*.tmp`, replay `journal.json` nếu còn → mọi commit hoặc có đủ
  hoặc không có gì.
- Phiên, phiên GM, key ring Data Protection, lease, purge record, receipt, projection đều là document của store → giữ qua restart
  (token player và cookie GM cũ vẫn dùng được).

### 4.2 Giới hạn

| Mục | Hành vi |
| --- | --- |
| Truy vấn | Không có N1QL / Query Workbench; truy vấn GM và job chạy trên bản in-memory |
| Sửa tay | Chỉ khi host **đã dừng**; sửa lúc host chạy không được nạp (không hot reload) và bị ghi đè ở lần ghi sau của key đó |
| Độ bền | Ghi và flush xuống OS, **không fsync**: chịu được mọi kiểu process chết (Ctrl+C, kill), **không** chịu được mất điện / OS crash; file bị cắt dở → khởi động lỗi, nêu tên file |
| File hỏng | JSON sai, `key` không khớp tên file, `content` không phải object, `expiresAtUtc` sai, tên file lạ → khởi động lỗi, nêu 20 file đầu và số còn lại; không bỏ qua im lặng. Sửa hoặc xoá file khi host dừng, hoặc reset |
| Hết hạn | Document hết hạn không bao giờ được trả về; file của nó bị xoá ở **lần khởi động sau** (không có sweeper chạy nền) |
| Một host | `.lock` mở `FileShare.None`: host thứ hai trên cùng thư mục thoát 1 ngay (`[filestore] the data folder '…' is used by another host. Stop it, or run this host with another FileStore:Folder.`); crash không để lại khoá |
| Định dạng | Thư mục không rỗng mà không có `store.json`, hoặc `store.json` khác format/version → khởi động lỗi, hướng dẫn reset |
| Đĩa đầy | Lỗi **trước** commit point = transaction thất bại bình thường (`INTERNAL`, không ghi gì), readiness vẫn healthy. Lỗi **sau** commit point → store chuyển trạng thái faulted (mọi lần ghi sau đều lỗi, `/health/ready` unhealthy, LogError nêu file); restart → replay journal |
| Hiệu năng | Đo 2026-10-30 (Windows 11, SSD, ổ D:): nạp 10 000 document 546 ms; 500 commit 6 document: p50 22,8 ms, p95 44,8 ms. Trong `%TEMP%` trên C: p95 82 ms — antivirus quét mỗi lần ghi + rename (~3,5 ms so với ~0,8 ms) |

### 4.3 Reset

- `-Reset` (script) hoặc `--FileStore:ResetOnStart=true`: **sau khi lấy khoá**, host xoá mọi thứ trong thư mục trừ `.lock` rồi tạo
  lại `store.json`. Host đang chạy giữ khoá nên chặn được reset. Từ chối (khởi động lỗi, không xoá gì) khi thư mục không phải thư
  mục store và không rỗng, là gốc ổ đĩa, thư mục user profile, content root hoặc thư mục cha của nó, hoặc có mục `.git`.
- Xoá thư mục bằng tay khi host dừng cũng là reset.

### 4.4 Bảo mật và antivirus

- `_database/` chứa **key ring Data Protection không mã hoá** (Development không có master key), phiên player, credential guest và
  cookie GM giải mã được. Không chia sẻ, không commit (đã gitignore), không copy sang máy khác.
- Windows Defender quét mỗi file mới làm commit chậm 3–5 lần. Giữ thư mục dữ liệu ngoài đường dẫn bị quét (không để trong
  `%TEMP%` trên C:), hoặc thêm exclusion (PowerShell admin): `Add-MpPreference -ExclusionPath <repo>\samples\_database`.

## 5. Couchbase (flavor dev, beta, uat, live)

### 5.1 Key cấu hình

| Key | Mặc định | Giới hạn / quy tắc |
| --- | --- | --- |
| `Couchbase:ConnectionString` | — (bắt buộc; **không còn** mặc định `couchbase://localhost`) | `couchbase://h1[,h2…][?params]` hoặc `couchbases://…` (TLS), ít nhất một host; danh sách seed phân tách bằng dấu phẩy |
| `Couchbase:Username` | — (bắt buộc) | identity của deployment |
| `Couchbase:Password` | — | bắt buộc ở **mọi** môi trường, kể cả Development (flavor `Dev` đọc biến môi trường user); chỉ từ môi trường (`Couchbase__Password`) |
| `Couchbase:BucketName` | — (bắt buộc) | một bucket cho mỗi môi trường (DAT-01) |
| `Couchbase:KvTimeoutMs` | 2500 | 100 .. 1000 × min(`GameServer:Limits:PlayerTimeoutSeconds`, `GameServer:Limits:GmTimeoutSeconds`) = 10 000 mặc định. Timeout commit durable vẫn là `GameServer:Transactions:KvDurableTimeoutSeconds` |
| `Couchbase:QueryTimeoutMs` | 20000 (SDK: 75 000) | 100 .. 1000 × `GameServer:Limits:GmTimeoutSeconds` = 30 000 mặc định. Player API không query; query phục vụ trang GM và job |
| `Couchbase:TrustCertificatePath` | null (trust store của hệ thống) | file X.509 đọc được (PEM hoặc DER), tương đối tính từ content root; cảnh báo khi connection string không dùng TLS |
| `Couchbase:Compression` | true | nén Snappy body KV — **thực sự bật** từ slice 011 (trước đó SDK không có thuật toán nén nên không nén gì) |
| `Couchbase:CompressionMinSizeBytes` | 32 | 0 .. 20 971 520 |
| `Couchbase:NumKvConnections` | 2 | 1 .. 32 |
| `Couchbase:MaxKvConnections` | 5 | `Couchbase:NumKvConnections` .. 64 |
| `Couchbase:UseGetMulti` | false | không đổi (PCR mục 2) |
| `Couchbase:ProvisionOnStartup` | Development: true; còn lại false | false → kiểm schema khi khởi động (mục 5.5) |
| `Couchbase:ProvisioningTimeoutSeconds` | 600 | 30 .. 7200; giới hạn một lần provisioning (cả câu lệnh `CREATE INDEX`) |

Scope luôn là `g_<gameId>` (không cấu hình). Giá trị ngoài khoảng, thiếu key bắt buộc, scheme sai, file CA không đọc được →
khởi động lỗi; thông báo nêu key và khoảng hợp lệ (không in connection string).

### 5.2 Ánh xạ từ `database_settings.json` (cấu hình tham chiếu của owner)

| Tham chiếu | Framework | Ghi chú |
| --- | --- | --- |
| `dbbase.bucketName` | `Couchbase:BucketName` | |
| `dbbase.serverIp` | `Couchbase:ConnectionString` | `couchbase://ip1,ip2`; `couchbases://` cho TLS |
| `dbbase.operationTimeOut` (2000) | `Couchbase:KvTimeoutMs` | |
| `dbbase.compressThreshold` (2048) | `Couchbase:CompressionMinSizeBytes` | với `Couchbase:Compression` = true |
| `dbbase.bucketType: couchbase` | — | không phải setting của app; transaction cần bucket loại Couchbase, ops tạo |
| `dbbase.bucketType: memcached` | — (bỏ) | bucket memcached đã deprecated và không chạy được transaction |
| `dbbase.moxiPort` | — (bỏ) | Moxi không còn trong Couchbase Server; SDK bootstrap từ connection string trên cổng chuẩn |
| `dbcache` (memcached) | — (bỏ, owner quyết định) | không có nơi dùng |
| `localFolder` | `FileStore:Folder` | chỉ flavor local |

Key cũ còn trong cấu hình (`Couchbase:ServerIp`, `Couchbase:OperationTimeOut`, `Couchbase:CompressThreshold`, `Couchbase:MoxiPort`,
`Couchbase:BucketType`, `Couchbase:DbCache`, `dbbase:*`, `dbcache`; không phân biệt hoa thường) → **cảnh báo** khi khởi động nêu key
thay thế, không bao giờ lỗi. Ví dụ: `[couchbase] legacy setting dbbase:operationTimeOut is ignored: renamed: use Couchbase:KvTimeoutMs (FR-015).`

### 5.3 TLS và CA

- TLS bật theo scheme `couchbases://`. Production + `couchbase://` → **lỗi**; Staging → cảnh báo; Development/Test → không gì.
  Không có công tắc bỏ qua.
- `Couchbase:TrustCertificatePath`: CA này trở thành **root tin cậy duy nhất** cho cả kết nối KV và HTTP (trust store của OS không
  được dùng); tên host vẫn được kiểm. Chỉ có tác dụng với `couchbases://`.
- File phải là **root CA** (certificate tự ký của CA). Trỏ vào intermediate → chuỗi không tới được root tin cậy (`PartialChain`)
  → mọi kết nối bị từ chối. Node phải gửi kèm intermediate trong handshake: host **không** tải certificate thiếu qua AIA
  (`DisableCertificateDownloads`, không có I/O mạng trong handshake).
- Chưa kiểm trên cluster có TLS (cluster local không có TLS). **Điều kiện bắt buộc trước mọi lần cutover Live**: chạy hai test
  opt-in trên một cluster TLS có CA riêng (`COUCHBASE_TLS_CONNSTR` + `COUCHBASE_CA_PATH`) và cả hai phải pass:
  `CouchbaseSettingsIntegrationTests.Tls_trusts_the_configured_ca_and_refuses_another` và
  `CouchbaseProvisioningTests.Production_provisions_without_runtime_secrets_and_serves_on_the_data_only_identity_over_tls`.

### 5.4 Quyền RBAC

| Identity | Dùng ở | Role |
| --- | --- | --- |
| Data-only (runtime) | mọi instance API của beta/uat/live | trên scope `[bucket:g_<gameId>]`: `Data Reader`, `Data Writer`, `Query Select`, `Query Insert`, `Query Update`, `Query Delete` |
| Quản trị (provisioning) | chỉ bước `--provision` của pipeline | trên **bucket** (scope chưa tồn tại ở lần chạy đầu): `Bucket Admin`, `Query Manage Index`, `Data Reader`; thêm `Query System Catalog` (role cấp cluster). Cú pháp: `bucket_admin[b]`, `query_manage_index[b]`, `data_reader[b]`, `query_system_catalog` |

- Couchbase 7.6 ghi role theo scope là `data_reader[bucket:scope]`; dạng `[bucket:scope:*]` bị từ chối.
- Đã kiểm (EE 7.6.12): user data-only chạy được host Staging với provisioning tắt (readiness 200); user đó chạy `--provision` → exit 2
  ngay mục đầu (`FAILED scope g_x: CouchbaseException (HTTP 403)` — không liệt kê được scope).
- Bộ role quản trị ở trên đã kiểm bằng user hạn chế (T055, `CouchbaseProvisionerRolesTests`): scope mới → exit 0, chạy lại → mọi
  mục `present`. Bỏ từng role → exit 2: thiếu `Data Reader` → `FAILED collection g_x.txn_meta: AuthenticationFailureException`
  (bước chờ đọc KV); thiếu `Query Manage Index` → `FAILED index …: AuthenticationFailureException … [13014]`; thiếu
  `Query System Catalog` → `FAILED index …: not in system:indexes after CREATE INDEX (… needs the Query System Catalog role …)`;
  thiếu `Bucket Admin` → `FAILED scope g_x: CouchbaseException (HTTP 403)`.

### 5.5 Provisioning

Ngoài Development, instance API **không** tạo schema. Bước pipeline chạy chính binary của host với `--provision` và identity quản
trị, trước khi deploy:

```bash
ASPNETCORE_ENVIRONMENT=Live \
Couchbase__ConnectionString=couchbases://cb1.live,cb2.live Couchbase__Username=gs-provisioner \
Couchbase__Password="$PROVISIONER_PASSWORD" \
dotnet SampleHost.dll --provision
```

```powershell
# local, flavor dev (bucket gameserver-dev)
dotnet run --project samples/SampleHost --launch-profile dev-couchbase -- --provision
```

- Cần: flavor hợp lệ, `GameServer:Store` = `Couchbase`, connection string, user, password, bucket. **Không** cần SSO GM, IAP, pepper,
  master key. Không chạy web server, job hay sweeper. Không bao giờ tạo bucket, không xoá hay sửa gì.
- Switch `--provision` không phân biệt hoa thường; `--provision=<giá trị>`, `-provision`, `/provision` bị từ chối (thoát 1): provider
  cấu hình sẽ đọc chúng thành key và host sẽ chạy server thay vì provisioning.
- Secret (`Couchbase:Password`, …) nằm trong file JSON ngoài Development/Test → thoát 1, như khi chạy server (OPS-08).
- Thứ tự: scope → `txn_meta` → các collection khác → chờ collection trả lời KV → index (framework, module, index tham chiếu của purge)
  → chờ mọi index `online` → kiểm lại.
- Output: mỗi mục một dòng tiền tố `provision:` (`created`, `present`, `created (online after N s)`), cuối cùng
  `provision: done: X created, Y present`. Lỗi: `provision: FAILED <loại> <tên>: <nguyên nhân không chứa credential>` và
  `provision: stopped after N items; fix the cause and run again (idempotent)`.
- **Idempotent**: chạy lại chỉ tạo phần còn thiếu (mục có sẵn báo `present`, không bị sửa); sau khi lỗi, chạy lại tiếp tục từ chỗ
  còn thiếu. Index trùng tên nhưng khác định nghĩa vẫn báo `present` (không phát hiện lệch định nghĩa).
- Cluster manager bận (HTTP 503, gặp khi nhiều scope được provision cùng lúc): mỗi lệnh quản trị — liệt kê scope, tạo scope, tạo
  collection — thử lại tối đa 8 lần, chờ 0,5 s rồi gấp đôi, tối đa 5 s (~22 s). Lỗi khác không thử lại.
- Index ở trạng thái `error` (build lỗi) → exit 2 ngay, gợi ý `DROP INDEX` rồi chạy lại (provisioner không sửa index có sẵn). Chờ
  index `online` bị giới hạn bởi `Couchbase:ProvisioningTimeoutSeconds` (cả khi provision lúc khởi động ở Development).

| Exit code | Ý nghĩa |
| --- | --- |
| 0 | mọi thứ đã có (tạo lần này hoặc trước đó) |
| 1 | cấu hình sai (deployment identity, setting Couchbase, quy tắc registry, secret trong file, switch viết sai) hoặc store không phải Couchbase (`[provision] --provision needs GameServer:Store=Couchbase; …`) |
| 2 | provisioning lỗi hoặc quá `Couchbase:ProvisioningTimeoutSeconds` (hoặc Ctrl+C); sửa nguyên nhân rồi chạy lại |

Thời gian: build index của một scope mới khi cluster đang tải có thể vượt 600 s mặc định (đo dưới tải của cả bộ test: 23–88 s mỗi
index; `gameserver-dev` thiếu 12 mục: 98 s). Gặp exit 2 vì timeout thì **chạy lại** (an toàn, tiếp tục), hoặc tăng
`Couchbase__ProvisioningTimeoutSeconds` (tối đa 7200).

**Kiểm schema khi khởi động** (`Couchbase:ProvisionOnStartup` = false, mặc định ngoài Development): trước khi nghe cổng, host đọc thử
mọi collection bắt buộc bằng identity runtime (chỉ cần quyền đọc KV). Thiếu collection → thoát 1 trước khi nghe cổng:

```text
 - [couchbase] scope g_sample in bucket sample-live is missing collections txn_meta, pa_wallet. Fix: run the
   provisioning command (<host> --provision) with an administrative credential (DAT-07).
```

`<host>` được in đúng nguyên văn (thay bằng lệnh chạy host của bạn). Index không được kiểm khi khởi động (cần quyền catalog);
báo cáo provisioning là nguồn đúng. Flavor `Dev` (Development) vẫn tự provision khi khởi động theo mặc định.

## 6. Exit code của host

| Code | Chạy bình thường | `--provision` |
| --- | --- | --- |
| 0 | dừng êm sau khi phục vụ | mọi thứ đã có |
| 1 | cấu hình sai hoặc bị từ chối trước traffic: lỗi validation (cả giá trị sai kiểu), file store bị khoá / hỏng / sai format / không đọc-ghi được (I/O, quyền), scope Couchbase thiếu collection, switch `--provision` viết sai | cấu hình sai, secret trong file, switch viết sai hoặc store không phải Couchbase |
| 2 | — | provisioning lỗi hoặc timeout |
| khác | lỗi runtime không xử lý (mặc định .NET) | lỗi không xử lý |

Code 1 in ra stderr `Game server startup validation failed:` rồi mỗi lỗi một dòng ` - <lỗi>`, ví dụ thiếu flavor:
`dotnet SampleHost.dll` không đặt `ASPNETCORE_ENVIRONMENT` (tên môi trường mặc định của .NET là Production) → lỗi
`GameServer:EnvironmentId`, store in-memory, master key, pepper và SSO GM cùng lúc, không bao giờ chạy như Development.
