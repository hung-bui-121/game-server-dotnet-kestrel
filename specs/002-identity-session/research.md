# Research: Identity & Session (002)

## R1. Store không-transaction cho `identities`, `sessions`

- **Decision**: interface mới `IKeyValueStore` (Platform.Persistence): `GetAsync`, `InsertAsync(key, content, expiry?)`
  → bool, `ReplaceAsync(existing, content, expiry?)` → bool (CAS; false khi CAS lệch), `RemoveAsync(key)`. Từ chối
  collection `pa_*`/`receipts` (ném `InvalidOperationException`) — các collection đó chỉ ghi qua runner/writer (CMD-05).
  Couchbase: KV `InsertAsync`/`ReplaceAsync(cas)`/`RemoveAsync` với `Majority` và `Expiry`. In-memory: lưu
  `expiresAt` theo `IClock` của store, coi document hết hạn là không tồn tại.
- **Rationale**: CMD-05 tách document trong transaction và ngoài transaction; module (Identity) cần ghi
  session/mapping mà không chạm store transaction (arch test).

## R2. Tăng `sessionEpoch` — `IPlayerAggregateWriter`

- **Decision**: `IPlayerAggregateWriter.UpdateAsync<TState>(playerId, aggregateId, mutate, ct)` chạy một transaction
  đọc–sửa–ghi một aggregate (rev++, budget kích thước), **không receipt** (DAT-09: writer cho login epoch/purge).
  Cancellation chỉ trước commit.

## R3. Xác thực theo profile endpoint (SEC-02)

- **Decision**: bỏ `UseAuthentication()` mặc định; `ProfileEnforcementMiddleware` gọi `AuthenticateAsync` với danh
  sách scheme của profile (`ActorSchemeOptions.PlayerSchemes` / `GmSchemes`). Identity đăng ký `PlayerBearer` vào
  Player; scheme thử nghiệm vào cả hai (chỉ Development). Handler thất bại có mã cụ thể (`SESSION_REPLACED`…) ghi mã
  vào `HttpContext.Items`; middleware trả đúng mã đó.
- **Rationale**: token player không bao giờ được thử trên GM endpoint và ngược lại — SEC-02 thành cấu trúc, không
  phải quy ước.

## R4. Cache ≤ 60 s

- **Decision**: `ExpiringCache<TKey, TValue>` (Platform) dùng `IClock` → test được bằng `FakeClock`. TTL
  `Identity:CacheSeconds` mặc định 60 (PERF-08, IDN-08).

## R5. Ban theo nhóm command — hook guard

- **Decision**: `ICommandGuard.CheckAsync(actor, descriptor, ct) → ErrorCode?` (Platform), runner gọi mọi guard ngay
  sau limiter per-player, trước replay/transaction (EXT-04: hook nằm trong pipeline chung). Identity đăng ký
  `BanCommandGuard` (nhóm = phần trước dấu chấm của operation).

## R6. Rate limit login (SEC-13)

- **Decision**: `FixedWindowCounter` (Platform, theo `IClock`, per instance): `localId` 10/phút (đếm mọi lần thử, chặn
  khi vượt); IP thất bại 60/phút (kiểm trước, tăng khi thất bại); IP tổng 600/phút chỉ tăng metric
  `login_ip_burst_warnings_total`. IP = `Connection.RemoteIpAddress` (ForwardedHeaders trusted proxy ở P0.5, OPS-02).

## R7. Mã lỗi và chi tiết

- **Decision**: thêm vào core: 16 `ACCOUNT_SUSPENDED` (403), 17 `ACCOUNT_DELETED` (401), 18 `SESSION_REPLACED` (401),
  19 `IDENTITY_CONFLICT` (409), 20 `SWITCH_ACCOUNT` (409). `ErrorEnvelope` thêm `map<string,string> details = 9`
  (thêm field mới, không breaking) — `ACCOUNT_SUSPENDED` mang `until`. `GameServerException.Details`.

## R8. Secret và token

- **Decision**: secret hash = `HMAC-SHA256(pepper, secret)`, so sánh `CryptographicOperations.FixedTimeEquals`. Token =
  32 byte `RandomNumberGenerator`, base64url; key session = hex SHA-256(token). `playerId` = `p_` + base32 (Crockford,
  lowercase) 16 byte ngẫu nhiên.
- **Rationale**: secret có entropy ≥ 256-bit nên HMAC đủ; không cần PBKDF2/Argon2.

## R9. Public endpoint của module

- **Decision**: `ModuleDescriptor.PublicEndpoints` — module khai route Public của mình; allowlist hiệu lực =
  `GameServer:PublicEndpointAllowlist` ∪ khai báo module; PCR module ghi route. Login guest là Public nhưng có
  `GateMetadata` để vẫn qua maintenance/version gate.
