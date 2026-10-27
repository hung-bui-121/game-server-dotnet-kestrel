# PCR — gm

Module framework `gm` (slice 004 + 007, version 0.7.0). Nguồn: GM-01/02/03/06, SEC-02/03/04/05/06/07/08/10/14/15,
OPS-04, ACC-09/10/11/24/27/45/49. Ban (GM-07), maintenance toggle: slice 008. GM frontend đầy đủ (GM-01, TBD-21): dev
khác làm dựa trên OpenAPI `docs/api/gm-openapi.json` (owner 2026-10-27); trang tĩnh `/gm/v1/ui` chỉ còn mức P0.

## Permission catalog (semantic — SEC-07)

Mỗi module khai permission của mình trong `ModuleDescriptor.Permissions`; endpoint/command GM dùng permission ngoài
catalog → startup fail (SEC-08).

| Permission | Module sở hữu | Dùng cho |
| --- | --- | --- |
| `gm.access` | gm | `/gm/v1/me`, logout |
| `player.read` | gm, economy (tên dùng chung; catalog là hợp của các module) | lookup, search, receipts, audit, ledger |
| `player.read.pii` | gm | hiện `identities` (mapping key) trong lookup |
| `ops.read` | gm | `GET /gm/v1/health` (chi tiết health, slice 005) |
| `staff.read` | gm | xem danh sách/chi tiết/audit staff |
| `staff.manage` | gm | tạo staff, đổi grant, khoá/mở, thu hồi phiên của người khác |
| `ops.keys.rotate` | gm | lên lịch key Data Protection mới |
| `resource.adjust` | economy | `economy.adjust` |
| `progression.edit` | sample.game | `progression.set` |

Role template (SEC-15) chỉ là seed, được expand thành permission khi tạo staff; module chỉ kiểm permission.

| Role | Permission |
| --- | --- |
| `Support` | `gm.access`, `player.read` |
| `Operator` | + `progression.edit` |
| `EconomyOperator` | + `resource.adjust` |
| `Administrator` | toàn bộ catalog của deployment |

Template không cấp permission mà deployment không có (module chưa đăng ký).

## Collections (ngoài transaction — CMD-05)

| Collection | Key | Nội dung |
| --- | --- | --- |
| `gm_staff` (**chỉ ghi trong transaction**) | `staffId` | `{type: "staff", staffId, displayName, email?, status (invited\|active), disabled, sessionEpoch, oidc?{issuer, subject}, passwordHash? (Dev/Test), grants[{gameId, environmentId, permissions[]}], updatedAtUtc}` |
| `gm_staff_identities` (**chỉ ghi trong transaction**) | `oidc:{sha256(issuer\nsubject)}` / `email:{email thường}` / `managers:{gameId}:{env}` | danh tính SSO đã gắn (nguồn sự thật khi đăng nhập), lời mời email (một lần), danh sách staff đang giữ `staff.manage` (khoá ghi tuần tự cho luật "manager cuối") |
| `gm_sessions` | SHA-256 hex của session id | `{staffId, gameId, environmentId, epoch, createdAtUtc, lastActiveAtUtc, expiresAtUtc}`, expiry = hết hạn tuyệt đối |
| `gm_audit` | `gm_audit::{entryId}` | projection outbox `gm_audit` (xem dưới); index `(playerId, committedAtUtc)` |

Module cũng khai index `receipts(playerId, committedAtUtc DESC, META().id DESC)`,
`pa_profile(state.displayName, playerId, META().id)`, `gm_staff(type, staffId, META().id)` và
`gm_audit(targetStaffId, committedAtUtc DESC, META().id DESC)` cho list/search. `gm_audit` chỉ được ghi bởi projection
(`IKeyValueStore` từ chối collection này).

## Xác thực và phạm vi (semantic)

- Scheme `GmCookie`: cookie `gs_gm` = session id 256-bit **đã bảo vệ** (mã hoá + MAC) bằng key ring Data Protection dùng
  chung (PCR platform mục 14) → ticket `gm_sessions` (lưu theo hash) → `gm_staff`. Cookie `HttpOnly`, `Path=/gm`,
  `SameSite=Lax` (`None; Secure` khi CORS bật), `Secure` ngoài Development. Cookie giả/sửa → 401. Không log id/cookie.
- Phiên hết hạn tuyệt đối `Gm:SessionHours` (8 h) và idle `Gm:IdleMinutes` (60 phút; ghi `lastActiveAtUtc` lười, tối
  đa 5 phút/lần). Ticket mang `epoch` của staff lúc tạo; khoá staff hoặc "thu hồi mọi phiên" tăng `sessionEpoch` → mọi
  phiên cũ 401. Staff `invited` (chưa đăng nhập SSO lần đầu) không đăng nhập local được.
- Permission hiệu lực = grant có `gameId` **và** `environmentId` khớp `GameServer:GameId`/`EnvironmentId`. Grant scope
  khác → không permission → 403. Session tạo ở game/env khác → 401.
- Staff `disabled` hoặc không tồn tại → 401. Cache staff + session per instance ≤ `Gm:CacheSeconds` (mặc định 60, tối
  đa 60 — SEC-10); logout xoá session ngay trên instance xử lý, instance khác chậm tối đa bằng thời gian cache.
- Mutation GM (method khác GET/HEAD/OPTIONS) có `Sec-Fetch-Site: cross-site` → 403 (phần P0 của SEC-05).
- **CSRF (SEC-05)**: mọi request thay đổi dữ liệu xác thực bằng cookie, và mọi endpoint Public dưới `/gm/` có method
  unsafe (local login), phải có header `X-CSRF-Token` hợp lệ (antiforgery: header token + cookie `gs_gm_csrf`), không
  `cross-site`, và `Origin` (nếu có) cùng origin hoặc trong allowlist CORS → nếu không 403 `CSRF_FAILED`. Token gắn với
  phiên (lấy lại sau login/logout qua `GET /gm/v1/auth/csrf`). Client header (test auth) không cần CSRF. Startup fail khi
  có endpoint Public GM unsafe thiếu CSRF. Logout chỉ POST; `GET` → 405 `METHOD_NOT_ALLOWED`.
- **CORS (SEC-06)**: tắt mặc định (same-origin). `Gm:Cors:AllowedOrigins[]` (khớp chính xác scheme+host+port) bật CORS
  có credentials cho `/gm/**`; `*`, path, `/` cuối, origin không tuyệt đối, `http` ngoài Development → startup fail. CORS
  không thay CSRF.
- Mọi response GM có `Cache-Control: no-store` (có thể chứa PII). Cookie luôn `Secure` ngoài Development.
- Cache staff/session chỉ giữ kết quả tìm thấy (token/staffId lạ không làm phình cache).
- Token player trên GM API → 401; cookie GM trên Player API → 401 (scheme tách theo profile, SEC-02).
- Runner kiểm lại: command profile `Gm` chỉ chạy cho actor GM có permission của command; command `Player` chỉ cho
  actor player (phòng thủ nhiều lớp sau metadata endpoint).

## Endpoint (JSON; lỗi = Problem Details có `code`)

| Route | Profile / permission | Ghi chú |
| --- | --- | --- |
| `GET /gm/v1/auth/csrf` | Public | `{token}` + cookie `gs_gm_csrf` |
| `GET /gm/v1/auth/sso/start?returnUrl=` | Public (khi `Gm:Oidc` cấu hình) | 302 tới IdP (code + PKCE); `returnUrl` chỉ nhận đường dẫn `/gm/...` |
| `GET /gm/v1/auth/sso/callback` | Public (handler OIDC) | thành công → cookie + 302 `returnUrl`; từ chối → 302 `/gm/?error=sso_denied` |
| `POST /gm/v1/auth/login` `{staffId, password}` | Public, CSRF (chỉ map ở Development/Test) | 200 `{staffId, displayName, gameId, environmentId, permissions[]}` + cookie; sai → 401 |
| `POST /gm/v1/auth/logout` | `gm.access`, CSRF | 204, xoá ticket + cookie; `GET` → 405 |
| `GET /gm/v1/me` | `gm.access` | như response login |
| `GET /gm/v1/health` | `ops.read` | chi tiết từng health check (OBS-03); 404 nếu host không bật observability |
| `GET /gm/v1/ui` | Public | trang HTML tĩnh same-origin, CSP chặt (`frame-ancestors 'none'`, `connect-src 'self'`) |
| `GET /gm/v1/players/{playerId}` | `player.read` | mọi aggregate (kể cả hidden): `{id, exists, clientVisible, rev, schemaVersion, storedSchemaVersion, sizeBytes, updatedAtUtc, state}`; không có aggregate nào → 404 |
| `GET /gm/v1/players/by-mapping/{mappingKey}` | `player.read` | `identities/{mappingKey}` → như trên; không có → 404 |
| `GET /gm/v1/players/search?displayName=&pageSize=&cursor=` | `player.read` | khớp chính xác `displayName`, sắp theo `playerId` |
| `GET /gm/v1/players/{playerId}/receipts?pageSize=&cursor=` | `player.read` | tóm tắt receipt, mới nhất trước |
| `GET /gm/v1/players/{playerId}/audit?pageSize=&cursor=` | `player.read` | document `gm_audit`, mới nhất trước |
| `GET /gm/v1/receipts/{receiptId}` | `player.read` | receipt đầy đủ; không có → 404 |
| `GET /gm/v1/players/{playerId}/ledger?pageSize=&cursor=` | `player.read` | (module economy) document `ledger`, mới nhất trước |
| `GET /gm/v1/staff?pageSize=&cursor=` | `staff.read` | `{items[StaffView], nextCursor}` theo `staffId` |
| `GET /gm/v1/staff/{staffId}` | `staff.read` | `StaffView` |
| `GET /gm/v1/staff/{staffId}/audit?pageSize=&cursor=` | `staff.read` | audit `gm_staff_audit`, mới nhất trước |
| `POST /gm/v1/staff` `{staffId, displayName?, email? \| issuer+subject, roles[], permissions[], reason}` | `staff.manage` | 201 `StaffView`; email mời → `invited` |
| `PUT /gm/v1/staff/{staffId}/grant` `{roles[], permissions[], reason}` | `staff.manage` | thay grant của game/env này |
| `POST /gm/v1/staff/{staffId}/disable` \| `/enable` `{reason}` | `staff.manage` | khoá tăng `sessionEpoch` |
| `POST /gm/v1/staff/{staffId}/sessions/revoke` `{reason}` | `gm.access` (chính mình) / `staff.manage` (người khác) | `sessionEpoch++` |
| `POST /gm/v1/ops/dataprotection/rotate` `{reason}` | `ops.keys.rotate` | `{keyId, activatesAtUtc, replayed}` |
| `GET /gm/v1/openapi/gm.json` | Public (chỉ Development/Test) | OpenAPI GM: mỗi operation có `x-gm-permission`, `x-gm-csrf` |

`StaffView` = `{staffId, displayName, email, status, disabled, oidcBound, permissions[] (game/env này), updatedAtUtc, replayed}`
— không trả password hash, `sessionEpoch`, subject OIDC. Mutation staff/rotate: `X-Request-Id` UUID bắt buộc, `reason`
1–500 ký tự.
| `POST /gm/v1/players/{playerId}/resources/adjust` | `resource.adjust` | (module economy) xem PCR economy |
| `POST /gm/v1/players/{playerId}/progression` | `progression.edit` | (module sample.game) xem PCR sample-game |

PII: `identities` trong state của `profile` bị bỏ khỏi lookup nếu thiếu `player.read.pii` (GM-02). Receipt/ledger/audit
không chứa secret (hash secret guest nằm ở `identities.providerAttributes`, không bao giờ trả ra).

## Phân trang (semantic — GM-06, ACC-24)

`pageSize` bắt buộc, 1–200; thiếu/sai → 400 `BAD_REQUEST`. Response `{items[], nextCursor}`; `nextCursor = null` ở trang
cuối. Cursor keyset opaque = base64url(`{v: giá trị orderBy, k: key}`); cursor hỏng → 400. Thứ tự: orderBy rồi key
(tie-break ổn định); bộ lọc luôn áp lại ở server nên cursor không mở rộng được phạm vi.

## Command GM (semantic)

`MapGmCommand`: body JSON (`application/json`, ≤ 1 MiB) phải có `reason` 1–500 ký tự (thiếu → 400); header `X-Request-Id`
UUID bắt buộc; actor = staff, player đích = `{playerId}` của route; `receiptId = hash(staffId|playerId|operation|requestId)`.
Player đích phải tồn tại (có `profile`) và không chờ xoá, nếu không → 404 `NOT_FOUND`, không ghi gì (`IGmTargetValidator`
do module gm đăng ký).
Retry cùng `X-Request-Id` → replay (`replayed: true`); cùng id khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`.
Response `{code, codeNumber, receiptId, replayed, configVersion, serverTimeUtcMs, result, updates[{id, baseRev, newRev, state}]}`;
lỗi nghiệp vụ = HTTP 200 + `code`, không receipt, không audit.

## Audit (semantic — SEC-14, ACC-27)

Command gọi `ctx.AddAudit(permission, action, reason, before?, after?)` → outbox kind `gm_audit` trong **cùng receipt**
(commit cùng thay đổi) → projection `gm_audit::{entryId}`. Runner **bắt buộc**: command profile `Gm` commit với đúng
một entry audit; command khác không được có entry `gm_audit` (vi phạm → INTERNAL, không commit — chống quên audit và
giả mạo audit từ command player).

`{entryId, kind, gameId, environmentId, receiptId, playerId, actorId, operation, committedAtUtc,
payload{actorId, permission, action, targetPlayerId, reason, before, after, correlationId}}`

7 trường bắt buộc: actor (`actorId`), permission/action, target (`targetPlayerId`), reason, before/after **hoặc** `receiptId`
(resource adjust tham chiếu receipt — delta nằm ở ledger của receipt đó; progression ghi before/after), thời gian UTC
(`committedAtUtc`), correlation (`correlationId`). Relay inline; lỗi relay → sweeper giao lại (at-least-once, insert-if-absent).

## SSO và quản lý staff (semantic — SEC-03, SEC-10, SEC-15)

- Đăng nhập ngoài Development/Test chỉ qua OIDC (`Gm:Oidc:*` bắt buộc, `ClientSecret` từ secret provider). Chỉ danh tính
  đã gắn (`gm_staff_identities/oidc:…`) với staff không khoá mới được vào; danh tính lạ → từ chối (log bảo mật chỉ có
  issuer + lý do). Lời mời email: lần SSO đầu có `email_verified=true` và email khớp gắn issuer+subject **một lần** trong
  transaction; sau đó email không còn tác dụng.
- MFA: `Gm:Oidc:RequireMfa` (mặc định bật ngoài Dev/Test) đòi claim `amr` chứa một giá trị trong `MfaAmrValues`
  (`mfa otp hwk swk sms fpt face`). IdP ép MFA bằng chính sách mà không phát `amr` → đặt `false` và ghi nhận ở đây.
- `Gm:Oidc:BootstrapManagers[]` (`{staffId, email}` hoặc `{staffId, issuer, subject}`): tạo Administrator khi deployment
  chưa có staff nào giữ `staff.manage` (idempotent).
- Mỗi thay đổi staff (và rotate key) = **một transaction** `IAuditedStore` {staff, identities, managers, `gm_audit`} — có
  ghi thì phải có đúng 1 audit, nếu không rollback. Idempotent theo `X-Request-Id` (`entryId = sha256(actor|op|target|requestId)`);
  cùng id khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`. Audit staff: `kind = gm_staff_audit`, `targetStaffId`, payload
  `{actorId, permission, action, targetStaffId, reason, before, after, correlationId, requestId, bodyHash}`.
- Chống leo thang: chỉ cấp permission người thao tác đang có (403 `PERMISSION_NOT_GRANTABLE`); không được để 0 staff
  không khoá giữ `staff.manage` (409 `LAST_STAFF_MANAGER`); role/permission ngoài catalog → 400; staffId/email trùng →
  409 `STAFF_EXISTS`.
- Thu hồi ≤ 60 s trên mọi instance: staff + ticket cache ≤ `Gm:CacheSeconds`; instance xử lý thay đổi xoá cache ngay.

Mã lỗi module gm (13000–13099): `STAFF_EXISTS` (13000, 409), `LAST_STAFF_MANAGER` (13001, 409),
`PERMISSION_NOT_GRANTABLE` (13002, 403). Core thêm `CSRF_FAILED` (21, 403), `METHOD_NOT_ALLOWED` (22, 405).

## Cấu hình (vận hành)

| Khoá | Mặc định | Ghi chú |
| --- | --- | --- |
| `Gm:SessionHours` | 8 | 1–24 |
| `Gm:CacheSeconds` | 60 | 0–60 |
| `Gm:IdleMinutes` | 60 | 5–480 |
| `Gm:Oidc:Authority`, `ClientId`, `ClientSecret` | — | bắt buộc ngoài Dev/Test; Authority https; ClientSecret qua secret provider |
| `Gm:Oidc:Scopes` | `openid profile email` | |
| `Gm:Oidc:RequireMfa` | `true` ngoài Dev/Test | |
| `Gm:Oidc:BootstrapManagers[]` | — | xem trên |
| `Gm:Cors:AllowedOrigins[]` | rỗng (same-origin) | |
| `Gm:Bootstrap:Staff[]` | — | `{staffId, displayName, password, roles[], permissions[], disabled, gameId?, environmentId?}`; **chỉ Development/Test** (môi trường khác → startup fail). Password hash PBKDF2-SHA256 100 000 vòng |

Production: không có login local; staff đầu tiên qua `Gm:Oidc:BootstrapManagers`, sau đó quản lý qua API staff.
