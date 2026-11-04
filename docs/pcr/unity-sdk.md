# PCR — Unity client SDK

Gói UPM `com.gameserver.client` (slice 006, version 0.7.0 — lockstep với contract `.proto` của server và với version framework
`GameServerFrameworkVersion`, slice 013; `VersionTests` kiểm hai giá trị bằng nhau). Nguồn: SDK-01..04,
API-09/10, IDN-10, GATE-02, TIME-01, CFG-03; slice 014 (FG-021, FG-026, FG-028): code sinh từ catalog của server, typed
accessor của section; slice 015 (FG-019, FG-024): version config client, cache config bền, nén `GetConfig`; slice 016 (FG-013, FG-022, FG-029): cờ người
chơi mới, request id của caller, pending store, section không đọc được. Đúng với framework v0.7.0 (chưa phát hành / unreleased).

## Gói (semantic với client)

| Thành phần | Nội dung |
| --- | --- |
| `Runtime/Core` | `GameServerClient`, `SectionStore`, `SectionKey`/`SectionKey<T>`/`SectionTable`/`SectionRead`, `GameServerError`, transport/clock/delay/session/config interfaces, `HttpClientTransport` |
| `Runtime/Unity` | `UnityWebRequestTransport`, `UnityConfigCache` (chỉ biên dịch trong Unity, `#if UNITY_5_3_OR_NEWER`) |
| `Runtime/Core` (slice 015) | `PersistentConfigCache` (public), `ConfigCacheFormat`, `CacheFileOps`, `AssemblyInfo` (internal; `InternalsVisibleTo("GameServer.Client.Tests")`) — mục "Cache config bền" |
| `Runtime/Generated` | code **commit**, sinh bởi WireGen (`sdk/GameServer.Client/wiregen.json`, mục "Code sinh" dưới): protoc C# của `core.proto`, `identity.proto`, `economy.proto`, `iap.proto` và `FrameworkRoutes.cs`, `FrameworkSections.cs`. Build `sdk/GameServer.Client` không ghi vào gói (slice 014, BC-6) |
| `Runtime/Plugins/Google.Protobuf.dll` | 3.36.2 netstandard2.0 = version server pin (API-09); auto-referenced |
| `Runtime/Plugins/System.Runtime.CompilerServices.Unsafe.dll` | 4.5.2 netstandard2.0 — phụ thuộc của Google.Protobuf mà profile Unity không có trên Android (V7: thiếu thì IL2CPP build fail "Unable to resolve reference") |
| `Runtime/link.xml` + `Editor/LinkXmlProvider.cs` | preserve `Google.Protobuf` và `GameServer.Client`; provider đưa link.xml cho Unity linker (Unity không tự đọc link.xml trong package). **Game phải tự preserve assembly chứa message proto của game** (KestrelDemo: `Assets/link.xml` → `SampleGame.Client`) |
| `Runtime/Unity/UnityWebRequestTransport` | gọi từ main thread; huỷ (`CancellationToken`) được chuyển về main thread để `Abort`; lỗi khi khởi tạo request → `TransportException`, tài nguyên được dispose |
| Client mẫu | **không** nằm trong gói: project Unity 6 `samples/SampleGame/KestrelDemo` tham chiếu gói bằng `file:` (login → config → battle → shop, log trên màn hình); code sinh bởi WireGen (`samples/SampleGame/wiregen.json`) ở
`Assets/SampleGame/Generated`: `SampleGame.cs`, `SampleCounter.cs`, `SampleGameRoutes.cs`, `SampleGameSections.cs`; script
dùng `SampleGameRoutes.BattleStart`/`BattleEnd`/`ShopBuy`, `Sections.Register(SampleGameSections.Table)` và
`Sections.Get(FrameworkSections.Wallet)`. `serverUrl` mặc định `http://localhost:5080/` = cổng của flavor local (slice 011; `SmokeBuild.cs` và `smoke-build.ps1` dùng cùng giá trị) |

Yêu cầu: Unity 2021.3+ (C# 9, .NET Standard 2.1). Code SDK giữ C# 9, không `record`/`init`. `.meta` được commit (cài qua git URL).

## Hành vi (semantic)

- **Header**: mọi request có `X-Request-Id` (UUID), `X-Client-Version`, `X-Platform`, `X-Build-Id` (tên = `GateHeaders` của server);
  request cần session có `Authorization: Bearer <token>`; token không bao giờ nằm trong body.
- **Command**: body serialize **một lần**, mọi lần retry gửi lại đúng mảng bytes đó cùng `X-Request-Id` **và cùng token** (đọc một lần
  trước vòng retry) → server replay, một hiệu ứng. Khi server đã trả lời, `SendCommandAsync` **không ném lỗi**: refetch section thất bại
  → `SectionsStale = true`, result không đọc được → `ResultUnreadable = true` (game retry bằng request mới sẽ áp dụng hai lần).
- **Login không tự retry** khi kết quả không chắc (mất response, timeout, 503): server không khử trùng login và mỗi login thu hồi
  phiên cũ, một bản trùng đến muộn sẽ làm phiên client đang giữ bị `SESSION_REPLACED`. Chỉ retry `RATE_LIMITED` (server từ chối trước
  khi xử lý); `NETWORK_ERROR` → game gọi login lại.
- **Retry** (request idempotent, mặc định 4 lần sau lần đầu): lỗi mạng/không có response, mã `retryable` (`COMMIT_UNKNOWN_RETRY`,
  `CONCURRENT_UPDATE`, `RATE_LIMITED`, `REQUEST_TIMEOUT`), response không phải Protobuf của server (200 HTML của proxy/captive portal,
  502/503/504). Chờ `max(200 ms × 2^n ≤ 5 s ± 20 %, retryAfterMs)`; `retryAfterMs` > `MaxRetryAfter` (30 s) → kết thúc ngay, game
  quyết định. Hết lượt → `GameServerException(NETWORK_ERROR | mã cuối)`. Options sai (BaseUrl không kết thúc `/`, jitter ngoài 0..1…) →
  `ArgumentException` khi tạo client.
- **200 phải là Protobuf** của server; login không có token hoặc command không có `code` → `INVALID_RESPONSE`, không tạo phiên.
- **Không retry**: mã nghiệp vụ (HTTP 200 + `code` ≠ OK → `CommandResult.Code`), lỗi 4xx, và các mã có sự kiện dưới đây.
- **Provider** (slice 009): `LoginWithProviderAsync(ProviderCredential, switchAccount = false)` — như login guest (không retry khi
  mơ hồ), gửi kèm token hiện có để server trả `SWITCH_ACCOUNT` (phiên giữ nguyên) thay vì rời player hiện tại; game hỏi người chơi
  rồi gọi lại với `switchAccount: true` và **proof mới** (mã Play Games dùng một lần). `LinkProviderAsync(credential)` là command
  (retry cùng bytes, server replay mà không xác minh lại): `OK` (`AlreadyLinked`), `PROVIDER_ALREADY_LINKED`; ném
  `IDENTITY_CONFLICT` (409, details `provider`) khi identity thuộc player khác — không merge. `IDENTITY_PROOF_INVALID` → lấy proof mới;
  `PROVIDER_UNAVAILABLE` → thử lại sau (link tự retry cùng bytes: với mã Play Games dùng một lần, retry có thể kết thúc bằng
  `IDENTITY_PROOF_INVALID` → lấy mã mới). Lỗi `ACCOUNT_DELETED`/`ACCOUNT_SUSPENDED` từ `LoginWithProviderAsync` nói về tài khoản
  đích: SDK vẫn phát `AccountBlocked` nhưng **không xoá** phiên hiện tại.
- **Xoá tài khoản** (slice 010, PRIV-01): `RequestAccountDeletionAsync()` là command (retry cùng bytes). `OK` → SDK xoá phiên và
  section, phát `AccountBlocked` với mã `ACCOUNT_DELETED` (`CodeNumber = -1`, `HttpStatus = 0` — quy ước lỗi do client dựng) và
  `Details["purgeAfter"]` (`GameServerClient.PurgeAfterDetail`, `yyyy-MM-ddTHH:mm:ssZ`). `GameServerException(ACCOUNT_DELETED)` từ
  lời gọi này **cũng** nghĩa là xoá đã commit (retry sau khi mất response): phiên và section đã xoá, `Details["purgeAfter"]` có
  ngày xoá. Mọi `ACCOUNT_DELETED` khác (login, request) mang `purgeAfter` khi tài khoản đang chờ/đang purge; game hiển thị ngày và
  hướng dẫn liên hệ hỗ trợ (huỷ chỉ do GM). Sau purge, credential cũ đăng nhập thành player mới.
- **Sự kiện**: `SessionReplaced` (xoá token; **không** tự đăng nhập lại), `AccountBlocked` (`ACCOUNT_SUSPENDED`, `ACCOUNT_DELETED` —
  xoá token, `SWITCH_ACCOUNT` — giữ token), `Maintenance`, `UpdateRequired`, `ConfigVersionChanged(version)`, `Sections.SectionChanged(id)`.
  Handler của game ném lỗi → SDK bắt, báo qua `HandlerFailed`, xử lý request không bị ảnh hưởng.
- **Section** (API-10): login xoá rồi ghi lại; snapshot chỉ ghi khi `rev` ≥ rev cục bộ (snapshot đến muộn không làm lùi); update `full` áp dụng nếu `newRev ≥ rev` cục bộ;
  update không `full` chỉ áp khi `baseRev = rev` và `newRev = baseRev + 1`, nếu không SDK gọi snapshot cho đúng section đó.
- **Thời gian**: `ServerNow` = mốc giờ server tại trung điểm round trip + thời gian trôi trên **đồng hồ monotonic** của máy (đổi giờ
  máy không ảnh hưởng). Giữ mẫu có round trip nhỏ nhất; mẫu tệ hơn > 1,5× (+50 ms) bị bỏ.
- **Config** (slice 015, BC-4): `ServerConfigVersion` = `config_version` không rỗng cuối cùng (version server, chỉ để đối chiếu receipt);
  `ClientConfigVersion` (mới) = `client_config_version` khi server gửi, nếu không thì `config_version` (server trước 015). Login,
  snapshot, command và error envelope cập nhật cả hai. `ConfigOutdated` và `ConfigVersionChanged(version)` theo version **client**
  (sự kiện một lần mỗi version mới): sửa file chỉ server không còn làm client tải lại. `RefreshConfigAsync` gửi `Config.Version`;
  `not_modified` → không tải (cache giữ theo version khác được đánh khoá lại một lần bằng chính file của nó); có file → lưu dưới
  version client của response. Server trả `not_modified` cho cả version server mà SDK trước 0.7.0 giữ.
- **Nén** (slice 015): SDK không đặt `Accept-Encoding` và không có bộ giải nén; server chỉ nén khi request xin. `HttpClientTransport`
  với `HttpClient` không có `AutomaticDecompression` không gửi header → identity. Kết quả theo nền tảng ở bảng "Nén theo nền tảng".

## Cache config bền (semantic với client — slice 015, FG-019, SDK-02)

`PersistentConfigCache : IConfigCache` (`Runtime/Core`, C# 9, netstandard2.1, không phụ thuộc UnityEngine), opt-in:

```csharp
var client = new GameServerClient(options, new UnityWebRequestTransport(), config: UnityConfigCache.Create(environmentName));
```

`UnityConfigCache.Create(environmentName)` = `new PersistentConfigCache(Path.Combine(Application.persistentDataPath,
"gameserver-config", environmentName))`; tên môi trường phải là tên thư mục đơn (1–64 ký tự `a-z`, `0-9`, `.`, `_`, `-`; không bắt đầu hay kết thúc bằng `.`;
không phải tên thiết bị Windows `con`, `prn`, `aux`, `nul`, `com1`–`com9`, `lpt1`–`lpt9`; **cùng một luật trên mọi nền tảng**, sai →
`ArgumentException`, `null` → `ArgumentNullException`) — **một thư mục cho mỗi môi trường server** (local, dev, beta, live không
dùng chung cache). Luật nằm ở `PersistentConfigCache.FolderNameProblem` (internal, `Runtime/Core`), không dùng
`Path.GetInvalidFileNameChars()`; `localhost:5080` bị từ chối ở mọi nơi (dùng `localhost-5080`). Game đặt tên trong settings của
client (vd. `local`, `live`), không suy ra từ URL. Trên iOS thư mục được loại khỏi backup iCloud/iTunes
(`UnityEngine.iOS.Device.SetNoBackupFlag` trên **thư mục**, vì `File.Replace` tạo file mới và mất cờ đặt trên file; kiểm ở T058). Thành viên:
`FileName` (`gameserver-config.bin`), `Folder`, `Version`, `Files`, `LoadProblem`, `WriteProblem`, `Store(version, files)`.

- **Container v1** (một file mỗi thư mục, little-endian): magic `GSCC`, format version `1` (u16), reserved (u16), độ dài payload
  (u32), payload (version UTF-8 1..256 byte, số file, mỗi file: tên UTF-8 1..1024 byte + nội dung), SHA-256 của payload. Format
  khác = version format khác; version không biết → cache rỗng. Không mã hoá (config client-visible vốn được gửi cho mọi client).
- **Load** (constructor): đọc `gameserver-config.bin`, nếu thiếu hoặc hỏng thì thử `gameserver-config.bin.tmp` hợp lệ; mọi file
  hỏng (rỗng, cắt, thừa byte, sai magic, format lạ, hash sai, UTF-8 sai, tên trùng, không đọc được) → cache rỗng và
  `LoadProblem` nêu lý do; **không bao giờ ném** vì I/O hay format. Độ dài được so với số byte còn lại (không cấp phát theo độ dài
  giả).
- **Store**: bộ nhớ trước (SDK thấy version mới ngay), rồi ghi temp + `Flush(true)` + `File.Replace` (main có) / `File.Move` (main
  chưa có); chết process ở bất kỳ bước nào → lần start sau load bản cũ hoặc bản mới, không bao giờ trộn. Ghi lỗi → giữ trong bộ
  nhớ, `WriteProblem` có lý do, không ném. Store trùng version và nội dung với file đang có → bỏ qua. Giữ một version.
- Tên file config từ server chỉ là dữ liệu trong container, không bao giờ thành đường dẫn. Thư mục chỉ chứa `gameserver-config.bin`
  (và `.tmp` khi đang ghi).
- **Luồng**: không thread-safe; dùng từ main thread (như `GameServerClient`). Ngân sách (SC-006): load và save 1 MiB ≤ 50 ms
  ngoài Unity — đo trên ổ C: (`%TEMP%`, nơi `persistentDataPath` nằm trên Windows): save p95 11–15 ms, load p95 5–9 ms.
- Với cache: lần mở app thứ hai có config trước login; khi version client không đổi (kể cả khi sửa file chỉ server), không tải gì.

**Nén theo nền tảng** (V8, slice 015):

| Nền tảng | `Accept-Encoding` mặc định | Nhận được | Kiểm |
| --- | --- | --- | --- |
| Editor (Windows), Unity 6000.3.22f1 | `deflate, gzip` | gzip, giải nén trước `downloadHandler.data` | T008 (probe), T051 (KestrelDemo E2E: bundle gzip 494 B, parse đúng) — **đạt** |
| Windows player (Mono) | `deflate, gzip` | gzip, giải nén | T008 — **đạt** |
| Android IL2CPP | — | — | T058 — **chờ** (thiết bị, owner) |
| iOS IL2CPP | — | — | T058 — **chờ** (macOS, owner) |

`br` không được Unity gửi cũng không giải được (`Unrecognized content-encoding`), nên server không bao giờ gửi `br` cho Unity. Nhánh
R13 (SDK tự giải gzip) **không** cần cho Editor/Windows: không có `ConfigBody.cs` trong gói. Nền tảng nào gửi `Accept-Encoding`
mà không giải → đặt `GameServer:ConfigCompression=false` cho deployment phục vụ nền tảng đó.

**File mới của gói** (slice 015, `.meta` viết tay, GUID mới, gói vẫn `0.7.0`): `Runtime/Core/AssemblyInfo.cs`
`3e6695fab49449a29555de642e16ecf5`, `CacheFileOps.cs` `bf7415ae02eb4819bcbd4c71c53c1c09`, `ConfigCacheFormat.cs`
`6c6977f541934d66b0d8df4f353e1eb8`, `PersistentConfigCache.cs` `b40d338fb610404aaa389a71317a5e30`, `Runtime/Unity/UnityConfigCache.cs`
`861c6bc8a2c54ea788e6dd388633d9d8`.

## Code sinh từ wire contract (semantic với client — slice 014, FG-021, FG-026, FG-028)

Nguồn: catalog của SampleHost (flavor Local, PCR platform mục 20) qua `--export-wire-contract`, cùng protoc pin của framework.
Mọi file sinh có header `<auto-generated>` "Generated by GameServer.WireGen (framework 0.7.0) …" (route/section) hoặc header
của protoc, UTF-8 không BOM, LF; không timestamp, path, user, máy.

**Layout `Runtime/Generated/`** (`by-root`: protoc output nằm dưới thư mục của proto trong root của nó):

| File | Nội dung |
| --- | --- |
| `gameserver/core/v1/Core.cs` | `core.proto` (root `proto/`, lồng theo thư mục), namespace `GameServer.Protocol.Core.V1` |
| `Identity.cs`, `Economy.cs`, `Iap.cs` | `identity.proto`, `economy.proto`, `iap.proto` (root = thư mục `Protos` của module, nên phẳng) |
| `FrameworkRoutes.cs` | route player-facing của framework (origin `framework`, surface `PlayerApi`/`Host`, profile Player/Public/Dev, không service-only; **không** `gm.*`, không health probe `/health/live`, `/health/ready` — route `Host` không version chỉ dành cho load balancer) |
| `FrameworkSections.cs` | section client-visible của framework: typed key, id và `Table` |

**`FrameworkRoutes`** (namespace `GameServer.Client`; giá trị tương đối với `BaseUrl`, không có `/` đầu; mỗi route có
`…Method` ngay sau nó; thứ tự ordinal theo tên route):

| Member | Giá trị | `…Method` |
| --- | --- | --- |
| `CheatGrant` | `api/v1/cheat/grant` | `POST` (profile Dev: chỉ server Development/Test) |
| `IapPurchase` | `api/v1/iap/purchase` | `POST` |
| `IdentityLink` | `api/v1/identity/link` | `POST` |
| `IdentityLoginGuest` | `api/v1/identity/login/guest` | `POST` |
| `IdentityLoginProvider` | `api/v1/identity/login/provider` | `POST` |
| `IdentityLogout` | `api/v1/identity/logout` | `POST` |
| `IdentityRequestDeletion` | `api/v1/identity/delete-account` | `POST` |
| `IdentitySetDisplayName` | `api/v1/identity/display-name` | `POST` |
| `PlatformConfig` | `api/v1/config/get` | `POST` |
| `PlatformReceipt(string receiptId)` | `"api/v1/player/receipts/" + Uri.EscapeDataString(receiptId)` | `GET` |
| `PlatformSnapshot` | `api/v1/player/snapshot` | `POST` |
| `PlatformTime` | `v1/time` | `GET` |

Helper có tham số nhận tham số theo thứ tự template, tên như trong template (keyword C# thêm `@`), mỗi giá trị qua
`Uri.EscapeDataString`. Hằng route của `GameServerClient` giữ nguyên giá trị và được định nghĩa từ code sinh:
`LoginRoute` = `IdentityLoginGuest`, `ProviderLoginRoute` = `IdentityLoginProvider`, `LinkRoute` = `IdentityLink`,
`LogoutRoute` = `IdentityLogout`, `DeleteAccountRoute` = `IdentityRequestDeletion`, `SnapshotRoute` = `PlatformSnapshot`,
`ConfigRoute` = `PlatformConfig`, `TimeRoute` = `PlatformTime`. `Runtime/Core` và `Runtime/Unity` không có literal route
nào (`SdkRouteTests`: member ↔ catalog, giá trị cũ, quét literal).

**`FrameworkSections`**: `Inventory` (`SectionKey<GameServer.Modules.Economy.Protocol.V1.InventoryState>`) / `InventoryId =
"inventory"`, `Profile` (`…Identity.Protocol.V1.ProfileState`) / `ProfileId = "profile"`, `Wallet`
(`…Economy.Protocol.V1.WalletState`) / `WalletId = "wallet"`, `Table` (ba key trên). Section không kiểu ở server (mapper
không có message type) chỉ sinh `XId` với comment "(registered without its message type: no parser)".

**Typed accessor (`SectionStore`, additive — API cũ giữ nguyên):**

- `SectionKey` (id + `Parser`), `SectionKey<T>` (sealed, `TypedParser`), `SectionTable(params SectionKey[])` (id trùng trong
  một bảng → `ArgumentException`), `SectionRead` { `Absent`, `Unknown`, `Parsed` }.
- Constructor của `SectionStore` đăng ký `FrameworkSections.Table`. `Register(table)`: game đăng ký bảng của mình
  (`GameSections.Table`) **một lần** sau khi tạo client; cùng id + cùng kiểu message lặp lại → không làm gì; cùng id khác kiểu →
  `ArgumentException` và không key nào của bảng được đăng ký. `Clear()` xoá section, giữ bảng.
- `IsKnown(id)`; `Get<T>(SectionKey<T>)` → message đã parse hoặc null khi chưa có; `TryRead(id, out IMessage? message)` →
  `Absent` (chưa nhận), `Unknown` (không bảng nào biết id: giữ raw trong `All`/`TryGet`, **không bao giờ** parse — server mới hơn
  client), `Parsed`.
- `Get<T>(id, parser)`, `TryGet`, `RevOf`, `Set`, `Apply`, `SectionChanged`, `HandlerFailed` không đổi; id lạ vẫn được lưu raw.

**Cặp version (semantic với consumer):** `Grpc.Tools` 2.84.0 (protoc `libprotoc 35.1`) sinh code; `Google.Protobuf` 3.36.2
chạy code đó (`Runtime/Plugins/Google.Protobuf.dll` = version pin CPM của server). `VersionTests` (Architecture) so product
version của dll (phần trước `+`) với `Google.Protobuf` trong `Directory.Packages.props` và pin `Grpc.Tools` của WireGen với CPM;
WireGen so `frameworkVersion`/`protobufVersion` của export và `Grpc.Tools`/`Google.Protobuf` của host consumer với pin của nó
(lệch → exit 1, nêu hai version và bên phải đổi). Đổi một version = đổi cả cặp cùng commit.

**`.meta` (gói UPM):** mọi file và thư mục dưới `sdk/unity/com.gameserver.client/` có `.meta` commit (gói cài qua git URL là
read-only; qua `file:` Unity sẽ tự tạo GUID ngẫu nhiên). File sinh mới trong gói cần `.meta` viết tay (mẫu `MonoImporter` của
`Iap.cs.meta`, GUID mới duy nhất): `FrameworkRoutes.cs.meta` `bd61226bd3304be08b906085cc633b4e`, `FrameworkSections.cs.meta`
`a3b2b6d27b8d450fac078e2d5ef0d744`. `UnityPackageMetaTests` kiểm: mỗi mục có `.meta`, GUID đã ghi không đổi, GUID không trùng,
`package.json` `0.7.0`, tên asmdef. WireGen không bao giờ đọc, ghi hay xoá `.meta`; trong project Unity (KestrelDemo, client
game) Unity tạo `.meta` khi import và game commit nó (tool in "new file … commit .meta").

**Sinh lại (BC-6):** `sdk/GameServer.Client/GameServer.Client.csproj` không còn `Grpc.Tools`/`<Protobuf>` (trước: build SDK sinh
cả `sample_game.proto` vào KestrelDemo). Lệnh từ root framework:

```text
dotnet run --project tools/GameServer.WireGen -- generate --config sdk/GameServer.Client/wiregen.json
dotnet run --project tools/GameServer.WireGen -- generate --config samples/SampleGame/wiregen.json
```

`verify` thay `generate` thì không ghi gì, exit 2 khi file commit lệch; `CommittedOutputTests` chạy `verify` cho hai config này
và config của consumer fixture trong mọi build framework, nên gói và client mẫu không thể lệch catalog.

## Login, request id, pending store và section không đọc được (semantic với client — slice 016, FG-013, FG-022, FG-029)

### Login

- `LoginResponse.IsNewPlayer` (field 8, member sinh): `true` chỉ khi login này tạo player; retry của login đầu bị mất response trả
  `false` (server đã commit). Với server trước 016 luôn `false`. Chỉ để hiển thị.
- **Login sections là một snapshot đầy đủ**: mọi section client-visible kèm `rev`; client boot trên đó, không cần gọi snapshot
  thứ hai (FG-013). Section server không đọc được bị bỏ và liệt kê (dưới).

### Request id

- `GameServerClient.NewRequestId()` → UUID chữ thường dạng D.
- Overload `SendCommandAsync(route, request, parser, string? requestId, ct = default)`: tham số thứ tư **bắt buộc** (`null` = SDK
  sinh), nên mọi lời gọi ba/bốn tham số cũ giữ overload cũ (VERIFY V1: Unity 6000.3.22f1 `-langversion:9.0` và Roslyn C# 9:
  `(r, q, p)`, `(r, q, p, default)`, `(r, q, p, ct)` → overload cũ; `(r, q, p, "id")`, `(r, q, p, null)` → overload mới, không
  CS0121). UUID dạng D mọi hoa thường được chuẩn hoá về chữ thường; giá trị khác (rỗng, dạng `N`, có ngoặc, 37 ký tự) →
  `ArgumentException` trước mọi lời gọi mạng và ghi đĩa. Mọi lần thử gửi cùng id và cùng bytes; cùng id + bytes sau đó (kể cả sau
  khi app khởi động lại) replay command đã commit trong replay window.
- `CommandResult.RequestId` (constructor bốn tham số cũ đặt `""`); `GameServerError.RequestId` có cả trên lỗi do SDK dựng của lời
  gọi (`NETWORK_ERROR`, `INVALID_RESPONSE`); overload `GameServerError.Client(code, message, retryable, requestId)`.

### Pending store

- Opt-in `GameServerClientOptions.PendingCommands` (mặc định null = không ghi gì); `PendingCommandMaxAge` mặc định 23 h, hợp lệ
  trong (0, 168 h]; giữ dưới `GameServer:Commands:ReplayWindowHours` của server (24 h) — quá window, gửi lại sẽ chạy như mới.
- Có store: command được ghi `(id, player của session, route, bytes chính xác, ServerNow)` **trước** lần thử đầu; xoá khi server
  trả command response với mã bất kỳ, hoặc error envelope không retryable (ví dụ `IDEMPOTENCY_PAYLOAD_MISMATCH`); **giữ** khi không
  rõ kết quả (không response, `INVALID_RESPONSE`, mã retryable ở lần cuối như `COMMIT_UNKNOWN_RETRY` hoặc `AGGREGATE_VERSION_AHEAD`,
  huỷ). Login, logout, snapshot, config và time không đụng store. Lỗi đĩa không bao giờ chặn command: entry giữ trong bộ nhớ,
  `WriteProblem` nói lý do.
- Sau khi khởi động lại: `client.PendingCommands` (danh sách **sống** của store: duyệt một bản sao khi gửi lại hoặc bỏ entry trong
  vòng lặp), `StatusOf(entry)` = `OtherPlayer` (player khác hoặc chưa có session), `Expired` (≥ `PendingCommandMaxAge` theo **đồng
  hồ server**, đổi giờ máy không làm thay đổi) hoặc `Resendable`; `ResendPendingAsync(entry, parser)` gửi bytes đã lưu với id đã
  lưu tới route đã lưu (command đã commit trả `Replayed` với receipt gốc), ném `InvalidOperationException` cho entry không
  resendable hoặc không còn trong store; `DiscardPendingCommand(id)` bỏ một entry (id lạ bị bỏ qua). SDK **không bao giờ** tự gửi
  lại.
- **`PersistentPendingCommandStore(folder)`**: một file `{requestId}.pending` mỗi entry. Container v1: magic `GSPC`, version `1`
  (u16), reserved, độ dài payload (u32), payload (requestId 36 byte = tên file, playerId 1..128, route 1..512,
  `recordedAtServerUtcMs` i64, body 0..1 MiB, các chuỗi có tiền tố độ dài u32), rồi SHA-256 của payload; little-endian. **Luật
  version**: format là semantic với client; thay đổi = version mới, file version lạ bị bỏ qua (xoá và nêu trong `LoadProblem`).
  Ghi `{id}.pending.tmp` → `Flush(true)` → `File.Move` sang `{id}.pending` (`File.Replace` khi cùng id ghi lại); xoá bằng
  `File.Delete` không flush. Load xoá file tạm còn sót, bỏ qua, xoá và nêu file hỏng trong `LoadProblem` (`"{file}: {reason}"`), để
  nguyên file có tên không đúng dạng entry chữ thường (Windows khớp pattern không phân biệt hoa thường, VERIFY V9), không bao giờ
  ném vì đĩa. Tên file chỉ từ id đã kiểm, không bao giờ từ dữ liệu server; session token không bao giờ được ghi.
- Unity: `UnityPendingCommandStore.Create(environmentName)` → `persistentDataPath/gameserver-pending/{environmentName}` (luật tên
  của `UnityConfigCache`, cờ no-backup của iOS trên thư mục).
- **Luồng**: đồng bộ trên thread gọi (main thread của Unity), không thread-safe, **một client mỗi thư mục**.

| Process chết… | Lần khởi động sau liệt kê | Gửi lại cho |
| --- | --- | --- |
| trước khi `Record` xong (chỉ file tạm hoặc chưa có gì) | không có (file tạm bị xoá) | — (request chưa đi: không có gì chạy) |
| sau `Record`, trước khi request tới server | entry | chạy một lần |
| sau khi server commit, trước response | entry | `replayed = true`, receipt id gốc |
| sau response, trước khi `Remove` xong | entry | replay (commit) hoặc lại đúng từ chối đó (từ chối không có receipt) |
| sau `Remove` | không có | — |

Số đo (VERIFY V8, entry 4 KiB, 200 lần sau warm-up): T007 (.NET 10, `Flush(true)`): ghi p50 3,08 / p95 6,09 ms, xoá p95 1,52 ms trên
C: (`%TEMP%`, ổ của `persistentDataPath` trên Windows — ổ tham chiếu của SC-008); D: (ổ repository) ghi p95 32,24 ms, xoá p95
0,46 ms (chỉ ghi lại). T061 (`PendingCommandStorePerformanceTests`, `GAMESERVER_PERF=1`, C:): ghi p50 2,29 / p95 5,56 ms, xoá p95
0,26 ms. `Flush(true)` được giữ (không fallback).

### Section không đọc được

- `LoginResponse.unavailable_sections` (field 9), `SnapshotResponse.unavailable_sections` (5), `CommandResponse.unavailable_sections`
  (10): `{id, code, code_number}`. SDK đánh dấu section: `Sections.Unavailable` / `IsUnavailable(id)`, kiểu
  `UnavailableSectionState` (`Id`, `Code`, `CodeNumber`), event `AvailabilityChanged(id)`; section bị đánh dấu **giữ bản tốt cuối**;
  `Set`/`Apply` kế tiếp của nó xoá dấu, `Clear()` xoá mọi dấu.
- **Luật cùng player**: login của **cùng** player giữ bản của các section nó liệt kê là unavailable; login của player khác bắt đầu
  từ store rỗng.
- Ba mã (`ErrorCodes`): `COMMAND_OUTPUT_TOO_LARGE` (23, mã nghiệp vụ trong `CommandResult.Code`, `Response.Details` có `part`/
  `bytes`/`limit`), `AGGREGATE_VERSION_AHEAD` (24, 503, retryable: SDK retry cùng bytes và id với delay 1000 ms rồi mới ném
  `GameServerException`), `AGGREGATE_UNREADABLE` (25, 500, không retry). `CommandResponse.details` (field 11) mang details của mọi
  từ chối HTTP 200.
- Tương thích: SDK trước 016 với server này bỏ qua field mới (section hỏng chỉ đơn giản vắng mặt); SDK này với server trước 016:
  `IsNewPlayer = false`, danh sách unavailable và `details` rỗng.

## Nghiệm thu

| Mục | Trạng thái |
| --- | --- |
| Hành vi SDK với server thật (TestServer): retry cùng bytes, COMMIT_UNKNOWN_RETRY, takeover, gate, config, rev | test tự động `tests/GameServer.Client.Tests` |
| Biên dịch trong Unity 2022.3.62f2 (SDK + Editor hook + sample) | đạt (batch mode, 26/10/2026) |
| CI-02 IL2CPP smoke build Android arm64, stripping High — Unity 2022.3.62f2 (project tạm) | **đạt** 26/10/2026 (APK 9,3 MB, 0 lỗi) sau khi thêm `System.Runtime.CompilerServices.Unsafe.dll` |
| CI-02 IL2CPP smoke build Android arm64, stripping High — Unity 6000.3.22f1 (KestrelDemo) | **đạt** 26/10/2026 (APK development 39 MB, 0 lỗi). E2E trong Unity 6 batch mode qua UnityWebRequest với SampleHost thật (`smoke-build.ps1 -E2E`): login, GetConfig, StartBattle/EndBattle OK, Buy trả mã nghiệp vụ, wallet rev 1 — **đạt** |
| KestrelDemo sinh bằng WireGen (slice 014) — Unity 6000.3.22f1 | **đạt** 03/11/2026: `smoke-build.ps1 -CompileOnly` exit 0; `-E2E` với SampleHost thật exit 0 (login, config, StartBattle/EndBattle OK, Buy trả mã nghiệp vụ, wallet rev 1); không `.meta` nào được tạo hay đổi dưới `sdk/unity` |
| KestrelDemo với cache bền và nén (slice 015) — Unity 6000.3.22f1 Editor | **đạt** 04/11/2026: `-CompileOnly` 0 lỗi/cảnh báo; `-E2E` ba lần exit 0: lần 1 `config downloaded sample-1+9b25b8b6bc0b` (gzip), lần 2 `config from cache sample-1+9b25b8b6bc0b` (không gọi `GetConfig`), lần 3 sau khi sửa `shop.json` `config downloaded sample-1+523717009cea` (đường `File.Replace`); thư mục cache chỉ có `gameserver-config.bin` |
| KestrelDemo với request id và pending store (slice 016 T068) — Unity 6000.3.22f1 Editor (Mono) | **đạt** 04/11/2026: `-CompileOnly` 0 `error CS`/`warning CS` với overload thật (V1); run A qua proxy cắt response battle end (`-e2eDevice t068`): `new player: True`, `EndBattle → pending {id}` sau 5 lần thử, thư mục `gameserver-pending/local` có đúng một `{id}.pending` (173 B); run B thẳng SampleHost: `new player: False`, `pending {id} Resendable`, `resent {id}: replayed=True`, thư mục rỗng, một phần thưởng battle trong ledger cho command đó (V9 Editor) |
| Pending store trong Windows player (slice 016 T068, V9) | **đạt** 04/11/2026: probe tạm Win64 (Mono, development) ghi ba entry 4 KiB (3 file `.pending` 4227 B), nạp lại 3 entry bằng store mới, xoá cả ba → thư mục rỗng, `LoadProblem`/`WriteProblem` null; probe đã xoá |
| Android IL2CPP arm64, stripping High với gói 016 (T068) | **đạt** 04/11/2026: `smoke-build.ps1` exit 0, `[smoke] result=Succeeded errors=0`, APK development 54 MB (`Build/KestrelDemo.apk`) |
| Thiết bị thật Android + iOS với slice 015 + 016 (T069, cùng 015 T058) | **chưa** — owner: năm lần khởi động (cài mới, khởi động lại, response battle end bị cắt, gửi lại, config mới), bước trong `specs/016-login-command/tasks.md` T069 |
| Thiết bị thật Android + iOS (ACC-18) | **chưa** — thủ công: chạy SampleHost, mở KestrelDemo, đặt `serverUrl` trên object `SampleGameClient`, cài APK (`Build/KestrelDemo.apk`) lên máy cùng mạng, kiểm login/battle/shop/retry/config |
| iOS IL2CPP build | **chưa** — Unity 6 trên máy chưa cài module iOS; cần macOS + Xcode |

Chạy CI-02: `sdk/unity/ci/smoke-build.ps1 -Unity "<Unity 6>\Editor\Unity.exe"` (`-Setup` tạo lại scene demo, `-CompileOnly` chỉ biên dịch).
Project không được đang mở trong Editor; trên Windows giữ đường dẫn project ngắn (MAX_PATH).
