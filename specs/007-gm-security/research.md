# Research — 007 gm-security

Mọi quyết định dưới đây trích ID yêu cầu của `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4.

## R1 — Luồng SSO (SEC-03)

- **Decision**: handler `OpenIdConnect` của ASP.NET Core, scheme `GmOidc`, `ResponseType=code`, `UsePkce=true`,
  `CallbackPath=/gm/v1/auth/sso/callback`. Bắt đầu bằng `GET /gm/v1/auth/sso/start?returnUrl=` (Public) → `Challenge`.
  Không dùng cookie authentication của ASP.NET: `OnTicketReceived` khớp danh tính (R3), kiểm MFA (R2), tạo phiên GM
  của framework (R4), đặt cookie `gs_gm`, `HandleResponse()` và redirect tới `returnUrl` (chỉ đường dẫn tương đối
  bắt đầu `/gm/`, khác → `/gm/`). Từ chối → redirect `/gm/?error=sso_denied` (không lộ lý do chi tiết cho người dùng; lý
  do nằm trong log bảo mật). Cookie correlation/nonce của handler được bảo vệ bằng key ring dùng chung (R6) nên callback
  về instance khác vẫn hợp lệ.
- Cấu hình `Gm:Oidc:{Authority, ClientId, ClientSecret, Scopes[], CallbackPath, RequireMfa, MfaAmrValues[]}`. Ngoài
  Development/Test: thiếu `Authority`/`ClientId`/`ClientSecret` → startup fail (không có cách đăng nhập GM nào khác).
  `ClientSecret` chỉ đọc từ secret provider (cơ chế OPS-08 của slice 005 đã fail khi secret nằm trong file cấu hình).
- **Rationale**: chuẩn, có PKCE/state/nonce sẵn; không phải viết OIDC tay.
- **Alternatives**: SAML (không có handler chuẩn trong .NET); dùng cookie auth của ASP.NET làm phiên (không đáp ứng
  "ticket server-side + thu hồi ≤ 60 s" mà không thêm `ITicketStore`, và trùng với phiên GM đã có).

## R2 — Bằng chứng MFA (SEC-03)

- **Decision**: `RequireMfa` mặc định `true` ngoài Development/Test; đạt khi claim `amr` của id_token giao với
  `MfaAmrValues` (mặc định `mfa`, `otp`, `hwk`, `swk`, `sms`, `fpt`, `face`). IdP không phát `amr` (vd. Google
  Workspace ép MFA bằng chính sách) → ops đặt `RequireMfa=false`, ghi trong PCR. Startup log Warning khi tắt ngoài Dev.
- **Alternatives**: luôn tin IdP (không kiểm được); `acr` (giá trị không chuẩn giữa các IdP).

## R3 — Khớp danh tính và lời mời email (SEC-03)

- **Decision**: collection `gm_staff_identities` (chỉ ghi trong transaction):
  - `oidc:{sha256hex(issuer + "\n" + subject)}` → `{staffId, issuer, subject, boundAtUtc}` — nguồn sự thật cho việc khớp.
  - `email:{lowercase email}` → `{staffId}` — lời mời; Insert khi tạo staff (trùng → 409 `STAFF_EXISTS`).
  Đăng nhập: tra `oidc:` → staff. Không có → nếu id_token có `email_verified=true` và `email:` tồn tại → transaction
  {Insert `oidc:…`, Replace staff (ghi `oidc`, xoá trạng thái mời), Insert audit `staff.bind`} — Insert bảo đảm gắn một
  lần (đăng nhập đồng thời: một bên commit, bên kia đọc lại và khớp theo `oidc:`). Staff đã gắn danh tính khác → từ chối.
- **Rationale**: issuer + subject ổn định và duy nhất; email chỉ dùng một lần để mời.
- **Alternatives**: khớp theo email mọi lần (email tái sử dụng → chiếm quyền); để admin nhập subject (khó vận hành).

## R4 — Phiên và cookie (SEC-04, SEC-10)

- **Decision**: session id 256-bit; cookie `gs_gm` = `IDataProtector("GameServer.Gm.Session.v1").Protect(id)` (mã hoá +
  MAC bằng key ring dùng chung). Ticket trong `gm_sessions/{sha256hex(id)}` (KV, ngoài transaction — không phải dữ
  liệu authoritative của player):
  `{staffId, gameId, environmentId, epoch, createdAtUtc, lastActiveAtUtc, expiresAtUtc}`; expiry document = hết hạn
  tuyệt đối. Idle 60 phút: `lastActiveAtUtc` cập nhật lười (Replace có CAS khi cách lần trước > 5 phút; lỗi CAS bỏ qua).
- Cookie: `HttpOnly`, `Secure` ngoài Development, `SameSite=Lax` (`None` khi CORS bật — R9), `Path=/gm`.
- **Rationale**: cookie bị chép vẫn bị thu hồi phía server; cookie giả/sửa không giải mã được.

## R5 — Thu hồi ≤ 60 s (SEC-10, PERF-08)

- **Decision**: staff có `sessionEpoch`; khoá staff hoặc "thu hồi mọi phiên" → `epoch++` trong transaction. Xác thực
  mỗi request: ticket.epoch ≠ staff.epoch → 401. Cache staff và ticket per instance ≤ `Gm:CacheSeconds` (≤ 60, đã có);
  permission đọc từ staff mỗi request (qua cache) nên đổi quyền có hiệu lực ≤ 60 s không cần đăng nhập lại. Logout và
  thu hồi xoá cache trên instance xử lý.
- **Rationale**: không cần kênh push giữa instance; giới hạn bằng TTL (TBD-05b).
- **Alternatives**: query mọi ticket của staff để xoá (cần index, chậm, vẫn phải chờ cache).

## R6 — Key ring dùng chung (OPS-04, ACC-49)

- **Decision**: `AddDataProtection().SetApplicationName("gameserver:{gameId}:{environmentId}")` ở Platform.Web; key ring
  lưu trong **một document** `dataprotection/keyring` (`{keys: [xml…], rev}`), sửa bằng Insert/Replace có CAS qua
  `IKeyValueStore` (store-agnostic → in-memory cho test, Couchbase cho chạy thật).
- `IXmlRepository` là API đồng bộ; cấm sync-over-async (PERF-01) nên:
  - `GetAllElements()` trả **snapshot** trong RAM, không I/O.
  - `DataProtectionKeyRingService` (`IHostedLifecycleService`): `StartingAsync` (trước khi server nhận request) tải key
    ring; chưa có key → tạo key đầu tiên bằng Insert (instance khởi động đồng thời: một bên thắng, bên kia tải lại);
    sau đó làm mới snapshot mỗi 5 phút (`RefreshMinutes`).
  - `DisableAutomaticKeyGeneration()`: request không bao giờ tạo key. Service tạo key kế tiếp **trước khi kích hoạt**
    `ActivationLeadHours` = 48 h (mọi instance đã tải trước khi key được dùng); `StoreElement` ghi vào snapshot + hàng chờ,
    service await ghi Couchbase ngay sau `CreateNewKey`.
  - Rotate ngay (ops): `POST /gm/v1/ops/dataprotection/rotate` (`ops.keys.rotate`) tạo key kích hoạt sau
    2 × `RefreshMinutes` (10 phút) — không dùng ngay để instance khác kịp tải.
- Thời hạn key 90 ngày (`KeyLifetimeDays`); key cũ giữ trong ring để giải mã (không xoá tự động) → cookie cũ dùng được
  ít nhất tới hết hạn tuyệt đối của phiên (grace, ACC-49).
- **Rationale**: không sync-over-async, mọi instance biết key trước khi nó hoạt động; một document nên không cần list/query.
- **Alternatives**: `PersistKeysToFileSystem` trên file share (không có ở Couchbase-only deployment); gọi Couchbase đồng
  bộ trong `GetAllElements` (vi phạm PERF-01).

## R7 — Mã hoá key at rest (OPS-04)

- **Decision**: `IXmlEncryptor` AES-256-GCM với khoá chính `Gm:DataProtection:MasterKey` (base64 32 byte, secret provider);
  XML lưu `<encryptedKey masterKeyId nonce tag>ciphertext</encryptedKey>`, decryptor (`IXmlDecryptor`, ctor
  `IServiceProvider`) lấy khoá theo `masterKeyId` (chuẩn bị cho xoay khoá chính ở slice 013). Ngoài Development/Test thiếu
  hoặc sai độ dài → startup fail. Development/Test không có khoá chính → lưu không mã hoá + log Warning.
- **Alternatives**: `ProtectKeysWithCertificate` (cần quản lý certificate trên Linux — ops nặng hơn); DPAPI (Windows only).

## R8 — CSRF (SEC-05)

- **Decision**: `IAntiforgery` với `HeaderName = "X-CSRF-Token"`, cookie `gs_gm_csrf` (`HttpOnly`, `Path=/gm`, cùng chính
  sách `Secure`/`SameSite` với `gs_gm`). Validate bằng **endpoint filter** gắn tự động cho mọi endpoint GM có method
  thay đổi dữ liệu (kể cả `POST /gm/v1/auth/login` local và logout) — chạy sau xác thực nên token gắn với staff (V8:
  antiforgery tự động của Minimal API chỉ áp cho form, nên phải validate tường minh). Lỗi → 403 `CSRF_FAILED`.
- Lấy token: `GET /gm/v1/auth/csrf` (Public) → `{token}`; token gắn danh tính → client lấy lại sau login/logout.
- Kiểm `Sec-Fetch-Site` ≠ `cross-site` giữ nguyên (slice 004). Startup validator: endpoint GM unsafe thiếu metadata
  CSRF → fail (deny-by-default).
- Logout chỉ map `POST`; `GET` → 405 do routing.

## R9 — CORS (SEC-06)

- **Decision**: tắt mặc định. `Gm:Cors:AllowedOrigins[]` khác rỗng → policy `gm` (origin khớp chính xác, `AllowCredentials`,
  method/header tối thiểu gồm `X-CSRF-Token`, `X-Request-Id`) áp cho nhóm route `/gm`; cookie `gs_gm`/`gs_gm_csrf`
  chuyển `SameSite=None; Secure`. Validation: `*`, có path/query, không phải origin tuyệt đối, `http` ngoài Development
  → startup fail.

## R10 — Quản lý staff có audit (SEC-14, SEC-15)

- **Decision**: `StaffAdminService` chạy mỗi thay đổi trong **một transaction** của `ITransactionalDocumentStore`:
  {Replace/Insert `gm_staff`, (Insert/… `gm_staff_identities`), Insert `gm_audit/gm_audit::{entryId}`}. `entryId =
  sha256(actor|operation|targetStaffId|X-Request-Id)` → retry cùng `X-Request-Id` gặp `DocumentExistsException` →
  trả trạng thái hiện tại (`replayed: true`); cùng id khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` (so `bodyHash` lưu
  trong audit). `gm_staff` và `gm_staff_identities` thêm vào danh sách collection chỉ ghi bằng transaction
  (`KeyValueCollections.EnsureWritable`) — tránh lỗi ghi lẫn KV/transaction đã thấy ở TST-04 V15-T2. Bootstrap seed
  (Dev) chuyển sang cùng service.
- Chống leo thang: chỉ cấp permission người thao tác đang có; không được để deployment còn 0 staff hoạt động có
  `staff.manage` (khoá, hạ quyền, kể cả tự mình) → 409 `LAST_STAFF_MANAGER`.
- Permission mới: `staff.read`, `staff.manage`, `ops.keys.rotate` (module gm). Role `Administrator` = toàn catalog.
- **Complexity**: constitution III yêu cầu mutation GM "đi qua pipeline command" — pipeline command chạy trên aggregate
  player; staff không phải player. Service riêng giữ đúng các bảo đảm (một transaction, audit cùng transaction,
  idempotent theo `X-Request-Id`) — ghi ở Complexity Tracking.

## R11 — Bootstrap staff quản lý đầu tiên (SEC-03)

- **Decision**: `Gm:Oidc:BootstrapManagers[]` (`{email}` hoặc `{issuer, subject}`) áp dụng ở `StartingAsync` khi deployment
  có 0 staff hoạt động có `staff.manage`: tạo lời mời role `Administrator`, audit actor `system:bootstrap`; có rồi → bỏ
  qua (idempotent, nhiều instance cùng khởi động an toàn nhờ Insert `email:`/`oidc:`).

## R12 — OpenAPI (deliverable #8, FR-020)

- **Decision**: `Microsoft.AspNetCore.OpenApi` document `gm` chỉ gồm route `/gm/**`; operation transformer thêm
  `x-gm-permission`, `x-gm-csrf`, security scheme cookie `gs_gm`. Endpoint GM khai `Accepts`/`Produces`. Phục vụ
  `GET /gm/v1/openapi.json` ở Development/Test (Public allowlist). Bản commit `docs/api/gm-openapi.json`; test so sánh
  (đặt `UPDATE_OPENAPI=1` để ghi lại) và kiểm mọi endpoint GM có mặt.

## R13 — Test SSO và nhiều instance

- **Decision**: IdP giả lập trong test (discovery, JWKS RSA, `authorize` trả code, `token` trả id_token ký RS256 với
  `amr`/`email_verified` tuỳ test); handler OIDC dùng `BackchannelHttpHandler` trỏ vào IdP giả. Trình duyệt mô phỏng
  bằng client theo `Location` + cookie container. ACC-11/ACC-49 dùng hai `TestHost` chung một store in-memory và
  `FakeClock` (vượt TTL cache 60 s); integration Couchbase chạy lại bộ ACC-49 trên store thật khi có `COUCHBASE_CONNSTR`.

## R14 — Mã lỗi mới (API-11)

| Code | HTTP | Dùng |
| --- | --- | --- |
| `CSRF_FAILED` | 403 | thiếu/sai CSRF token hoặc cross-site |
| `STAFF_EXISTS` | 409 | staffId hoặc email mời đã dùng |
| `LAST_STAFF_MANAGER` | 409 | thao tác làm deployment mất người quản lý staff cuối cùng |
| `PERMISSION_NOT_GRANTABLE` | 403 | cấp permission người thao tác không có |
