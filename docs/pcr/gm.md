# PCR — gm

Module framework `gm` (slice 004 + 007, version 0.8.0 sau slice 010). Nguồn: GM-01/02/03/06, SEC-02/03/04/05/06/07/08/10/14/15,
OPS-04, ACC-09/10/11/24/27/45/49. Ban (GM-07), maintenance toggle: slice 008. GM console (GM-01, TBD-21): owner tự xây console
GM chung thành các slice framework GM-1..GM-4, dựng từ GM document (quyết định 2026-11-05, thiết kế
`AgentMemories/features/gm-tool/01_design.md`); slice 017 (GM-1) là hợp đồng backend. Trang tĩnh `/gm/v1/ui` vẫn là trang P0.

Đúng với framework v0.7.0 (phát hành 2026-11-04) + nhánh `017-gm-api-contract` (chưa phát hành).

## Permission catalog (semantic — SEC-07)

Mỗi module khai permission của mình trong `ModuleDescriptor.Permissions`; endpoint/command GM dùng permission ngoài
catalog → startup fail (SEC-08).

| Permission | Module sở hữu | Dùng cho |
| --- | --- | --- |
| `gm.access` | gm | `/gm/v1/me`, logout, catalog GM `/gm/v1/console/openapi.json` (slice 017) |
| `player.read` | gm, economy (tên dùng chung; catalog là hợp của các module) | lookup, search, receipts, audit, ledger |
| `player.read.pii` | gm | hiện `identities` (mapping key) trong lookup |
| `ops.read` | gm | `GET /gm/v1/health` (chi tiết health, slice 005), `GET /gm/v1/ops/gate`, `GET /gm/v1/ops/version` (slice 013) |
| `staff.read` | gm | xem danh sách/chi tiết/audit staff |
| `staff.manage` | gm | tạo staff, đổi grant, khoá/mở, thu hồi phiên của người khác |
| `ops.maintenance` | gm | bật/tắt bảo trì, đặt `minClientVersion` (GATE-01/02) |
| `player.ban` | identity | ban/unban player (GM-07); huỷ yêu cầu xoá tài khoản (slice 010) |
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

Deletion treatment (slice 010): `gm_staff`, `gm_staff_identities`, `gm_sessions` = `NoPlayerData`; `gm_audit` (platform) bị
purge ghi đè player id bằng anonymous id (PCR platform mục 16). Index `receipts(playerId, committedAtUtc DESC, META().id DESC)`
từ slice 010 do **platform** tạo (module gm không còn khai).

Module cũng khai index
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
  không thay CSRF: frontend ở origin cấu hình gửi mutation `cross-site` được (cookie `SameSite=None; Secure`) nhưng vẫn
  phải có `X-CSRF-Token`; mọi origin khác bị chặn.
- Đăng nhập lại (local hoặc SSO) khi trình duyệt còn phiên → phiên cũ bị huỷ ở server. Endpoint login nhận CSRF token
  lấy khi đang đăng nhập (token gắn phiên).
- Mọi response GM có `Cache-Control: no-store` (có thể chứa PII). Ngoại lệ duy nhất (slice 017): catalog route
  `/gm/v1/console/openapi.json` ghi đè thành `private, no-cache` + `ETag` (document không chứa dữ liệu player/staff, console
  revalidate bằng 304). Cookie luôn `Secure` ngoài Development.
- Cache staff/session chỉ giữ kết quả tìm thấy (token/staffId lạ không làm phình cache).
- Token player trên GM API → 401; cookie GM trên Player API → 401 (scheme tách theo profile, SEC-02).
- Runner kiểm lại: command profile `Gm` chỉ chạy cho actor GM có permission của command; command `Player` chỉ cho
  actor player (phòng thủ nhiều lớp sau metadata endpoint).

## Endpoint (JSON; lỗi = Problem Details có `code`)

| Route | Profile / permission | Ghi chú |
| --- | --- | --- |
| `GET /gm/v1/auth/csrf` | Public | `{token}` + cookie `gs_gm_csrf` |
| `GET /gm/v1/auth/methods` | Public (allowlist qua declaration, slice 017) | `{local, sso}`: `local` = route login local được map (development tools), `sso` = `Gm:Oidc` đã cấu hình (cấu hình, không phải IdP sống). Không trả gì khác (không version, staff, setting) |
| `GET /gm/v1/auth/sso/start?returnUrl=` | Public (khi `Gm:Oidc` cấu hình) | 302 tới IdP (code + PKCE); `returnUrl` chỉ nhận đường dẫn `/gm/...` |
| `GET /gm/v1/auth/sso/callback` | Public (handler OIDC) | thành công → cookie + 302 `returnUrl`; từ chối → 302 `/gm/?error=sso_denied` |
| `POST /gm/v1/auth/login` `{staffId, password}` | Public, CSRF (chỉ map ở Development/Test) | 200 `{staffId, displayName, gameId, environmentId, permissions[], frameworkVersion, gameVersion}` + cookie; sai → 401 |
| `POST /gm/v1/auth/logout` | `gm.access`, CSRF | 204, xoá ticket + cookie; `GET` → 405 |
| `GET /gm/v1/me` | `gm.access` | như response login; `frameworkVersion` (informational, như `ops/version`) và `gameVersion` của instance trả lời (slice 017, D9) |
| `GET /gm/v1/console/openapi.json` | `gm.access` | (slice 017) GM document của host, mọi môi trường; `ETag` mạnh, `Cache-Control: private, no-cache`, 304 khi `If-None-Match` khớp; xem mục "GM document và hợp đồng console" |
| `GET /gm/v1/health` | `ops.read` | chi tiết từng health check (OBS-03); 404 nếu host không bật observability |
| `GET /gm/v1/ui` | Public | trang HTML tĩnh same-origin, CSP chặt (`frame-ancestors 'none'`, `connect-src 'self'`) |
| `GET /gm/v1/players/{playerId}` | `player.read` | `{playerId, aggregates[], deletion}`: mọi aggregate (kể cả hidden): `{id, exists, clientVisible, rev, schemaVersion, storedSchemaVersion, sizeBytes, updatedAtUtc, state}`; `deletion` (slice 010, null khi không có yêu cầu xoá) = `{state: pending\|purging\|purged, requestedAtUtc, purgeAfterUtc, purgeStartedAtUtc, purgedAtUtc}`. Player đã purge → **200** `aggregates: []`, `deletion.state = purged`, không dữ liệu cá nhân, không bao giờ trả anonymous id (D7). Không aggregate **và** không purge record → 404 |
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
| `GET /gm/v1/ops/gate` | `ops.read` | `{maintenance, minClientVersions, rev}` |
| `GET /gm/v1/ops/version` | `ops.read` | (slice 013, FR-055) `{gameId, environmentId, instanceId, gameVersion, frameworkVersion}` của instance trả lời: version informational của entry assembly của host (`unknown` nếu không đọc được) và của framework (`0.7.0+{sha}`); instance id như tên file log và `service.instance.id`. Không audit. Endpoint Public và health probe không lộ version (OBS-03) |
| `PUT /gm/v1/ops/gate/maintenance` `{enabled, message?, expectedEndUtc?, reason}` | `ops.maintenance` | audit `ops.gate.maintenance` (before/after) |
| `PUT /gm/v1/ops/gate/min-client-version` `{platform, minVersion? (null = bỏ), updateUrl? (https), reason}` | `ops.maintenance` | audit `ops.gate.min_client_version` |
| `POST /gm/v1/players/{playerId}/ban` `{reason, untilUtc?, scope[]?}` | `player.ban` | (module identity) command `identity.ban`; không `untilUtc` = vĩnh viễn, không scope = `login` |
| `POST /gm/v1/players/{playerId}/unban` `{reason}` | `player.ban` | (module identity) command `identity.unban` |
| `POST /gm/v1/players/{playerId}/deletion/cancel` `{reason}` | `player.ban`, CSRF | (module identity, slice 010) command `identity.cancel_deletion`; `result = {before: DeletionInfo}`; 409 `DELETION_NOT_PENDING` / `DELETION_GRACE_OVER` (grace hết, purge đã bắt đầu, hoặc đã purge); xem PCR identity |
| `GET /gm/v1/openapi/gm.json` | Public (chỉ Development/Test) | cùng GM document với catalog route (giữ nguyên, route platform `platform.gm_openapi`) |

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

`MapGmCommand`: body JSON (`application/json`, ≤ 1 MiB) phải có `reason` 1–500 ký tự, không chỉ khoảng trắng; DataAnnotations
của body được kiểm trước `toDomain` (slice 017; lỗi → 400 `BAD_REQUEST` có `errors`, xem mục "GM document và hợp đồng
console"); header `X-Request-Id` UUID bắt buộc; actor = staff, player đích = `{playerId}` của route; `receiptId = hash(staffId|playerId|operation|requestId)`.
Player đích phải tồn tại (có `profile`) và không chờ xoá, nếu không → 404 `NOT_FOUND`, không ghi gì (`IGmTargetValidator`
do module gm đăng ký). Ngoại lệ (slice 010): `identity.cancel_deletion` được nhắm player đang chờ xoá; player đã purge (chỉ còn
purge record) → 409 `DELETION_GRACE_OVER`. Lookup `by-mapping` và `search` của player đã purge → 404 / rỗng (mapping và profile
đã xoá).
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
- Email đã xác minh: claim `Gm:Oidc:EmailVerifiedClaim` (mặc định `email_verified`) phải là `true`. Microsoft Entra ID
  không gửi claim này mặc định → cấu hình optional claim, hoặc tạo staff bằng issuer + subject.
- Log từ chối SSO: lý do, issuer, khoá danh tính đã băm (`oidc:{sha256}`) — không subject, không token.
- `Gm:Oidc:BootstrapManagers[]` (`{staffId, email}` hoặc `{staffId, issuer, subject}`): tạo Administrator khi deployment
  chưa có staff nào giữ `staff.manage` (idempotent).
- Mỗi thay đổi staff (và rotate key) = **một transaction** `IAuditedStore` {staff, identities, managers, `gm_audit`} — có
  ghi thì phải có đúng 1 audit, nếu không rollback. Idempotent theo `X-Request-Id` (`entryId = sha256(actor|op|target|requestId)`);
  cùng id khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`. Audit staff: `kind = gm_staff_audit`, `targetStaffId`, payload
  `{actorId, permission, action, targetStaffId, reason, before, after, correlationId, requestId, bodyHash}`.
- Chống leo thang: chỉ cấp permission người thao tác đang có, và chỉ đổi grant/khoá/mở/thu hồi phiên của staff có tập
  permission nằm trong tập của mình (403 `PERMISSION_NOT_GRANTABLE`); không được để 0 staff **đăng nhập được** (active,
  không khoá) giữ `staff.manage` — lời mời chưa dùng không tính (409 `LAST_STAFF_MANAGER`); role/permission ngoài catalog
  → 400; staffId/email trùng → 409 `STAFF_EXISTS`.
- Bootstrap: manager cấu hình đã tồn tại nhưng không phải manager dùng được (khoá, thiếu `staff.manage`) → startup fail
  ngoài Development/Test. Seed Development: request id theo nội dung seed → seed không đổi thì không ghi lại khi restart.
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

## Route catalog và section (slice 014 — FG-026, FG-027, FG-028)

29 route `gm.*` khai trong `GmRoutes` (`src/GameServer.Modules.Gm.Adapters/GmRoutes.cs`, `Routes = GmRoutes.All`; slice 017
thêm `gm.auth_methods`, `gm.console_catalog`); permission chép từ lời gọi `AsGmEndpoint(...)` trước đây; route Public được
allowlist qua declaration (không còn `PublicEndpoints`). **Allowlist Public (SEC-09)** của module gm: `gm.auth_csrf`,
`gm.auth_login`, `gm.sso_start`, `gm.sso_callback`, `gm.ui`, `gm.auth_methods` — được allowlist qua declaration, game không
cần cấu hình gì (`GameServer:PublicEndpointAllowlist` mặc định hay tường minh đều được, V10). Điều kiện hosting: `gm.auth_login` chỉ map khi `development_tools` (Development/Test),
`gm.sso_start`/`gm.sso_callback` chỉ khi `gm.sso` (`AddRouteCondition("gm.sso", …)` = `Gm:Oidc` đã cấu hình); route có
condition tắt không map và không bị endpoint check đòi. Bảng route lấy từ export của SampleHost (`--environment Local
--export-wire-contract`, format `gameserver.wire-contract/1`). Endpoint check lúc khởi động, quy tắc đặt tên và điều
kiện hosting: PCR platform mục 20. Surface `GmApi` không sinh hằng số Unity. `gm.staff_revoke_sessions` cần `gm.access`
(handler tự kiểm `staff.manage` khi thu hồi phiên của người khác). OpenAPI GM (`/gm/v1/openapi/{documentName}.json`) là
route `platform.gm_openapi` của platform (PCR platform mục 20). Route GM của module khác (`identity.ban`,
`economy.adjust`, `progression.set`, …) nằm ở PCR của module đó.

| Route | Method | Template | Profile | Operation | Permission | Condition |
| --- | --- | --- | --- | --- | --- | --- |
| `gm.auth_csrf` | GET | `/gm/v1/auth/csrf` | Public | — | — | — |
| `gm.auth_login` | POST | `/gm/v1/auth/login` | Public | — | — | `development_tools` |
| `gm.auth_logout` | POST | `/gm/v1/auth/logout` | Gm | — | `gm.access` | — |
| `gm.auth_methods` | GET | `/gm/v1/auth/methods` | Public | — | — | — |
| `gm.console_catalog` | GET | `/gm/v1/console/openapi.json` | Gm | — | `gm.access` | — |
| `gm.health` | GET | `/gm/v1/health` | Gm | — | `ops.read` | — |
| `gm.me` | GET | `/gm/v1/me` | Gm | — | `gm.access` | — |
| `gm.ops_gate` | GET | `/gm/v1/ops/gate` | Gm | — | `ops.read` | — |
| `gm.ops_gate_maintenance` | PUT | `/gm/v1/ops/gate/maintenance` | Gm | — | `ops.maintenance` | — |
| `gm.ops_gate_min_client_version` | PUT | `/gm/v1/ops/gate/min-client-version` | Gm | — | `ops.maintenance` | — |
| `gm.ops_rotate_keys` | POST | `/gm/v1/ops/dataprotection/rotate` | Gm | — | `ops.keys.rotate` | — |
| `gm.ops_version` | GET | `/gm/v1/ops/version` | Gm | — | `ops.read` | — |
| `gm.player` | GET | `/gm/v1/players/{playerId}` | Gm | — | `player.read` | — |
| `gm.player_audit` | GET | `/gm/v1/players/{playerId}/audit` | Gm | — | `player.read` | — |
| `gm.player_by_mapping` | GET | `/gm/v1/players/by-mapping/{mappingKey}` | Gm | — | `player.read` | — |
| `gm.player_receipts` | GET | `/gm/v1/players/{playerId}/receipts` | Gm | — | `player.read` | — |
| `gm.player_search` | GET | `/gm/v1/players/search` | Gm | — | `player.read` | — |
| `gm.receipt` | GET | `/gm/v1/receipts/{receiptId}` | Gm | — | `player.read` | — |
| `gm.sso_callback` | GET | `/gm/v1/auth/sso/callback` | Public | — | — | `gm.sso` |
| `gm.sso_start` | GET | `/gm/v1/auth/sso/start` | Public | — | — | `gm.sso` |
| `gm.staff` | GET | `/gm/v1/staff/{staffId}` | Gm | — | `staff.read` | — |
| `gm.staff_audit` | GET | `/gm/v1/staff/{staffId}/audit` | Gm | — | `staff.read` | — |
| `gm.staff_create` | POST | `/gm/v1/staff` | Gm | — | `staff.manage` | — |
| `gm.staff_disable` | POST | `/gm/v1/staff/{staffId}/disable` | Gm | — | `staff.manage` | — |
| `gm.staff_enable` | POST | `/gm/v1/staff/{staffId}/enable` | Gm | — | `staff.manage` | — |
| `gm.staff_grant` | PUT | `/gm/v1/staff/{staffId}/grant` | Gm | — | `staff.manage` | — |
| `gm.staff_list` | GET | `/gm/v1/staff` | Gm | — | `staff.read` | — |
| `gm.staff_revoke_sessions` | POST | `/gm/v1/staff/{staffId}/sessions/revoke` | Gm | — | `gm.access` | — |
| `gm.ui` | GET | `/gm/v1/ui` | Public | — | — | — |

Module không có aggregate client-visible.

## GM document và hợp đồng console (slice 017 — GM-1)

**Catalog route** `gm.console_catalog` (`GET /gm/v1/console/openapi.json`, Gm, `gm.access`, không nằm trong document):

- GM document (OpenAPI `3.1.1`, document `gm`, mọi endpoint `/gm/**` không bị loại) đăng ký ở **mọi môi trường**; sinh **lười**
  ở request catalog đầu tiên (hoặc lời gọi kit), qua `IOpenApiDocumentProvider` không cần request, rồi giữ cho cả process
  (`GmCatalogDocument`, Platform.Web). Nhiều request đầu đồng thời dùng chung một lần sinh; lần sinh lỗi được giữ 30 giây
  (`GmCatalogDocument.FailureHold`: mọi request trong khoảng đó nhận cùng lỗi, 500 `INTERNAL`), sau đó request kế tiếp sinh lại. Mỗi lần sinh một dòng Information category `GameServer.Gm.Catalog`:
  `GM catalog generated: {Operations} operations, {Bytes} bytes, {ElapsedMs} ms.`
- Bytes chuẩn: JSON của document viết lại bằng System.Text.Json, thụt lề, LF, đúng một newline cuối, UTF-8 không BOM — giống
  hệt file commit và output của kit. `ETag` mạnh = `"` + 32 hex đầu của SHA-256(bytes) + `"`.
- 200: `Content-Type: application/json`, bytes, `ETag`, `Cache-Control: private, no-cache`. `If-None-Match` khớp (danh sách
  phân cách dấu phẩy, tag `W/` so theo giá trị, `*`) → 304 không body, cùng hai header. Không phiên → 401 `UNAUTHENTICATED`;
  thiếu `gm.access` hoặc grant game/env khác → 403. Mọi staff `gm.access` nhận cùng bytes (không lọc theo permission, Q5).
- Document không có `servers`, không version framework/game, không giá trị cấu hình (FR-008): cùng composition → cùng bytes ở
  mọi môi trường, chỉ khác route có điều kiện (`gm.auth_login` khi có development tools, SSO khi có `Gm:Oidc`).
- Route Dev/Test `platform.gm_openapi` (`/gm/v1/openapi/{documentName}.json`, Public) giữ nguyên, trả cùng document.
- CORS cho console khác origin (expose `ETag`) là việc của GM-2.

**Bootstrap của console** (thứ tự design §10): `GET /gm/v1/auth/methods` → đăng nhập → `GET /gm/v1/auth/csrf` →
`GET /gm/v1/me` (có `frameworkVersion`, `gameVersion`) → `GET /gm/v1/console/openapi.json` (kèm `If-None-Match` khi có bản).

**Khoá `x-gm-*` của operation** (framework sở hữu; game không thêm khoá, chỉ dùng presentation API):

| Khoá / member | Luật | Có ở |
| --- | --- | --- |
| `operationId` | tên route declaration (`RouteDeclarationMetadata`, không có thì tra catalog theo method + template); duy nhất | mọi operation |
| `tags` | `[area]` = phần tên route trước dấu `.` đầu tiên, trừ khi endpoint có `WithTags(...)` | mọi operation |
| `summary` | `WithSummary(...)` (tiếng Anh, một câu) | mọi route GM của framework; route game tuỳ chọn nhưng nên có (console hiện nó) |
| path parameter | mọi `{name}` của template, `required`, `string`; `playerId` thêm `pattern ^[A-Za-z0-9_-]{1,64}$` | operation có tham số template |
| header `X-Request-Id` | `required`, `string`, `format: uuid` | GM command; action có `WithGmRequestId()` |
| query parameter | `pageSize` (bắt buộc, integer 1..200), `cursor` (tuỳ chọn) của `ProducesGmPage<T>`; tham số khai bằng `WithGmQueryParameter` | list phân trang, tham số đọc tay |
| `x-gm-permission`, `x-gm-csrf` | như trước (permission hoặc `public`; method unsafe và profile Gm hoặc `RequireCsrf`) | mọi operation |
| `x-gm-kind` | `command` (có `CommandOperationMetadata`) · `query` (GET) · `action` (method khác) | mọi operation |
| `x-gm-operation` | tên operation của command | `x-gm-kind = command` |
| `x-gm-target` | `player` khi template có `{playerId}`, ngược lại `global` | mọi operation |
| `x-gm-confirm` | `typed` khi có `WithGmConfirm(GmConfirm.Typed)`, ngược lại `simple` | operation không phải GET |
| `x-gm-paged` | `true` | list có `ProducesGmPage<T>` |

Bảng kind/target/confirm của operation framework: `contracts/gm-document.md` §2 của slice 017. Xác nhận kiểu gõ (Q7):
`identity.ban`, `identity.cancel_deletion`, `economy.adjust`, `gm.ops_rotate_keys`, `gm.ops_gate_maintenance`. Header
`X-Request-Id` trên 8 action framework (đều đi qua `StaffEndpoints.Change`): `gm.staff_create`, `gm.staff_grant`,
`gm.staff_disable`, `gm.staff_enable`, `gm.staff_revoke_sessions`, `gm.ops_gate_maintenance`, `gm.ops_gate_min_client_version`,
`gm.ops_rotate_keys`. List phân trang: `gm.player_search` (`GmPlayerSearchItem`, thêm `displayName` 1..64),
`gm.player_receipts` (`GmReceiptSummary`), `gm.staff_list` (`StaffView`) có item typed; `gm.player_audit`, `gm.staff_audit`,
`economy.ledger` item tự do (`JsonObject`: document lưu trữ trả nguyên, D10).

**Schema của request body** (body của endpoint `/gm/**` có `Accepts<T>`; response schema giữ như generator):

- `required` = đúng các property server đòi (thay list của generator, vốn liệt kê mọi tham số positional): property có
  `[Required]`, **mọi value type không nullable được đọc từ JSON và không có giá trị mặc định** (`int`, `long`, `bool`, enum… —
  vắng thì đọc ra giá trị mặc định nên server từ chối sự vắng mặt, có hay không có `[Required]`; tham số constructor có mặc định
  không có `[Required]` và member tính toán chỉ-get vẫn tuỳ chọn; `[Required]` tường minh luôn kiểm có mặt), và `reason` của `IGmCommandBody`; áp cả cho object lồng (`ProbeNested.inner`).
  `null` bị bỏ khỏi `type` của property `[Required]`; `[Required]` trên chuỗi thêm `minLength: 1` và `pattern: "\\S"` (server từ
  chối `""` và chuỗi chỉ khoảng trắng). Muốn tuỳ chọn: khai nullable (`int?`).
- `reason`: `type: string`, `minLength: 1`, `maxLength: 500`, `pattern: "\\S"` (JSON; regex `\S`, = luật server).
- Keyword từ DataAnnotations: `[Range]` → `minimum`/`maximum` (biên `long.MaxValue` viết chính xác); `[StringLength]`,
  `[MinLength]`, `[MaxLength]`, `[Length]` → `minLength`/`maxLength` (chuỗi) hoặc `minItems`/`maxItems` (collection);
  `[RegularExpression("p")]` → `pattern: "^(?:p)?$"` (.NET so khớp **toàn bộ** chuỗi và cho `""` qua; `pattern` của JSON Schema
  là tìm kiếm, nên được bọc lại); `[Description]` → `description`. Viết ở property schema (nơi dùng), không bao giờ ở
  component dùng chung.
- `[GmOptions("<route>")]` → `x-gm-options: "<route>"` trên property, hoặc trên `items` của collection.
- Số giữ dạng của generator (`type: ["integer","string"]`, `format`, `pattern`); enum là `type: string` + `enum` = đúng các chuỗi
  `GameServerJson.Options` ghi (camelCase), do schema transformer viết — HTTP JSON options của host không bị đổi.

**Kiểm body ở server** (`MapGmCommand` và `GmHttp.ReadJsonAsync<T>`): cùng một lần duyệt contract System.Text.Json của body
(property **và** tham số positional, object lồng và item của list object tới độ sâu 3) cho cả document lẫn server, nên hai bên
không lệch. Value type không nullable (có hay không `[Required]`) được kiểm **có mặt** trong JSON (thiếu số đọc ra 0) → thiếu
thì `errors["<path>"] = ["The <name> field is required."]`, kể cả trong object lồng (`nested.inner`, `items[0].inner`). Pattern
của `[RegularExpression]` phải có cùng nghĩa trong regex .NET và JavaScript (console kiểm bằng JavaScript): tránh cú pháp chỉ
.NET có (lookbehind phức tạp, `(?<name>…)` kiểu .NET cũ, class Unicode `\p{…}` không có cờ `u`). Câu trả lời:

```json
{ "type": "https://errors.gameserver/bad_request", "title": "The request is invalid.", "status": 400, "code": "BAD_REQUEST", "codeNumber": 1,
  "requestId": "…", "retryable": false,
  "errors": { "level": ["The field level must be between 1 and 2147483647."],
              "reason": ["reason is required (1..500 characters, not only white space)."] } }
```

`errors`: khoá = JSON path (`level`, `box.label`, `items[1].quantity`), thứ tự ordinal, mỗi field mọi message (message tiếng
Anh của attribute, không text exception). Chỉ có khi luật body hỏng; JSON sai, thiếu `X-Request-Id` và luật `toDomain` (luật
chéo field) giữ dạng cũ (không `errors`). Đếm và log như mọi `BAD_REQUEST`. Console map `errors["<path>"]` vào field cùng path.

**Option route**: route GM `GET` (surface GmApi) trả `GmOptionList { items: [{ value, label }] }`; property body trỏ tới bằng
`[GmOptions]`. Tên không phải route GM GET của host (sai tên, route POST, surface Player, route service-only, condition không
giữ) → startup fail, một dòng mỗi (endpoint, property). Framework: `economy.resource_options`, `identity.ban_scopes` (PCR
economy, identity). Server vẫn kiểm giá trị gửi lên. Permission của option route phải là `gm.access` hoặc đúng permission của
endpoint dùng body đó, nếu không startup fail (một dòng): ai chạy được command thì tải được lựa chọn. `economy.resource_options`
cần `gm.access` (tên resource không nhạy cảm; mặc định do orchestrator chọn, chờ owner); `identity.ban_scopes` cần
`player.ban` (= `identity.ban`). Cùng lần kiểm khi khởi động chạy thử mỗi validation attribute của body (regex sai, toán hạng
`[Range]` không đổi được kiểu…) → một dòng startup mỗi attribute hỏng thay vì 500 ở request đầu.

**Giới hạn đã biết của schema enum** (review r2 m2): enum `[Flags]` (ghi ra danh sách tên phân cách dấu phẩy) và property có
converter riêng (`[JsonConverter]`) giữ schema của generator (thường `integer`), không có danh sách `enum` — console hiện ô nhập
tự do.

**Giới hạn đã biết** (quyết định chờ owner, review 017 M4): server kiểm body tới độ sâu 3 và không kiểm phần tử của body là mảng
ở gốc; giữ body GM ≤ 3 cấp, gốc là object.

**Quy ước annotation** (module-authoring guide): dùng tập attribute hỗ trợ, viết `[property: …]` hoặc trên tham số positional
(cả hai được thi hành); nullable = tuỳ chọn; luật chéo field ở `toDomain`; attribute tự viết được thi hành nhưng document không
hiện. Framework chỉ annotate luật đã thi hành chính xác (A10): `AdjustResourceBody.Resource` `[Required]`,
`MaintenanceBody.Message` `[StringLength(500)]` (501 ký tự → 400 có `errors.message`); `CreateStaffBody`, `StaffGrantBody`,
`MinClientVersionBody`, `GmLoginRequest` giữ kiểm tra viết tay (trim/lower-case/luật chéo).

**Cam kết tương thích** (ARC-08): trong `/gm/v1`, document chỉ thay đổi kiểu thêm (operation mới, member tuỳ chọn mới, giá trị
`x-gm-*` mới). Xoá/đổi tên operation id, path, member bắt buộc, hoặc đổi kind/target của route có sẵn là breaking change ghi
trong release note. Document commit của game bằng đúng cái host phục vụ (`GmOpenApiSnapshot.DiffAsync` rỗng trong CI của game).

## Aggregate hỏng và details của từ chối (slice 016 — FG-029, FG-016, BC-3, BC-11)

- **GM player view** (`GET /gm/v1/players/{playerId}`, schema `GmPlayerView` trong `docs/api/gm-openapi.json`): thêm
  `unreadable[]` (luôn có, rỗng khi mọi aggregate đọc được, theo thứ tự id), mỗi phần tử `{id, code, reason,
  storedSchemaVersion, codeSchemaVersion}` (`code` = `AGGREGATE_VERSION_AHEAD` | `AGGREGATE_UNREADABLE`). Aggregate hỏng không có
  trong `aggregates`. `profile` hỏng được liệt kê và `deletion = null`; player vẫn tồn tại.
- **GM command response** (schema `GmCommandResponse`, mọi route `MapGmCommand`): thêm `details` (object chuỗi → chuỗi; rỗng khi
  commit) mang `details` của mọi từ chối HTTP 200, kể cả mã module (ví dụ `part`/`bytes`/`limit` của
  `COMMAND_OUTPUT_TOO_LARGE`), và `unavailable[]` `{id, code}` (replay có aggregate không đọc được lúc này; bình thường rỗng).
- GM command chạm aggregate hỏng và target validator GM trả lỗi JSON GM có `code`/`codeNumber` 24 hoặc 25, `details.aggregate`;
  không ghi gì.
- Chỉ thêm member JSON (frontend GM không phải sửa); lần sinh lại đầu tiên của OpenAPI thêm schema đầy đủ của hai response này
  (trước đây các operation chỉ có `200: OK`).
