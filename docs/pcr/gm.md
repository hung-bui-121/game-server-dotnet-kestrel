# PCR — gm

Module framework `gm` (slice 004, version 0.4.0). Nguồn: GM-01/02/03/06, SEC-02/03/05 (phần P0)/07/08/10/14/15,
ACC-09/10/24/27/45. SSO/MFA, CSRF token, CORS, quản lý staff qua UI, ban (GM-07), maintenance toggle: **P0.5**.

## Permission catalog (semantic — SEC-07)

Mỗi module khai permission của mình trong `ModuleDescriptor.Permissions`; endpoint/command GM dùng permission ngoài
catalog → startup fail (SEC-08).

| Permission | Module sở hữu | Dùng cho |
| --- | --- | --- |
| `gm.access` | gm | `/gm/v1/me`, logout |
| `player.read` | gm | lookup, search, receipts, audit, ledger |
| `player.read.pii` | gm | hiện `identities` (mapping key) trong lookup |
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
| `gm_staff` | `staffId` | `{staffId, displayName, disabled, passwordHash?, grants[{gameId, environmentId, permissions[]}]}` |
| `gm_sessions` | SHA-256 hex của session id | `{staffId, gameId, environmentId, createdAtUtc, expiresAtUtc}`, expiry 8 h |
| `gm_audit` | `gm_audit::{entryId}` | projection outbox `gm_audit` (xem dưới); index `(playerId, committedAtUtc)` |

Module cũng khai index `receipts(playerId, committedAtUtc)` và `pa_profile(state.displayName, playerId)` cho list/search.

## Xác thực và phạm vi (semantic)

- Scheme `GmCookie`: cookie `gs_gm` (HttpOnly, SameSite=Lax, Secure khi HTTPS, Path=`/gm`) → `gm_sessions` → `gm_staff`.
  Session id 256-bit, chỉ lưu hash; không log.
- Permission hiệu lực = grant có `gameId` **và** `environmentId` khớp `GameServer:GameId`/`EnvironmentId`. Grant scope
  khác → không permission → 403. Session tạo ở game/env khác → 401.
- Staff `disabled` hoặc không tồn tại → 401. Cache staff + session per instance ≤ `Gm:CacheSeconds` (mặc định 60, tối
  đa 60 — SEC-10); logout xoá session ngay trên instance xử lý, instance khác chậm tối đa bằng thời gian cache.
- Mutation GM (method khác GET/HEAD/OPTIONS) có `Sec-Fetch-Site: cross-site` → 403 (phần P0 của SEC-05).
- Token player trên GM API → 401; cookie GM trên Player API → 401 (scheme tách theo profile, SEC-02).
- Runner kiểm lại: command profile `Gm` chỉ chạy cho actor GM có permission của command; command `Player` chỉ cho
  actor player (phòng thủ nhiều lớp sau metadata endpoint).

## Endpoint (JSON; lỗi = Problem Details có `code`)

| Route | Profile / permission | Ghi chú |
| --- | --- | --- |
| `POST /gm/v1/auth/login` `{staffId, password}` | Public (chỉ map ở Development/Test) | 200 `{staffId, displayName, gameId, environmentId, permissions[]}` + cookie; sai → 401 |
| `POST /gm/v1/auth/logout` | `gm.access` | 204, xoá session + cookie |
| `GET /gm/v1/me` | `gm.access` | như response login |
| `GET /gm/v1/ui` | Public | trang HTML tĩnh same-origin, CSP chặt (`frame-ancestors 'none'`, `connect-src 'self'`) |
| `GET /gm/v1/players/{playerId}` | `player.read` | mọi aggregate (kể cả hidden): `{id, exists, clientVisible, rev, schemaVersion, storedSchemaVersion, sizeBytes, updatedAtUtc, state}`; không có aggregate nào → 404 |
| `GET /gm/v1/players/by-mapping/{mappingKey}` | `player.read` | `identities/{mappingKey}` → như trên; không có → 404 |
| `GET /gm/v1/players/search?displayName=&pageSize=&cursor=` | `player.read` | khớp chính xác `displayName`, sắp theo `playerId` |
| `GET /gm/v1/players/{playerId}/receipts?pageSize=&cursor=` | `player.read` | tóm tắt receipt, mới nhất trước |
| `GET /gm/v1/players/{playerId}/audit?pageSize=&cursor=` | `player.read` | document `gm_audit`, mới nhất trước |
| `GET /gm/v1/receipts/{receiptId}` | `player.read` | receipt đầy đủ; không có → 404 |
| `GET /gm/v1/players/{playerId}/ledger?pageSize=&cursor=` | `player.read` | (module economy) document `ledger`, mới nhất trước |
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
Retry cùng `X-Request-Id` → replay (`replayed: true`); cùng id khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`.
Response `{code, codeNumber, receiptId, replayed, configVersion, serverTimeUtcMs, result, updates[{id, baseRev, newRev, state}]}`;
lỗi nghiệp vụ = HTTP 200 + `code`, không receipt, không audit.

## Audit (semantic — SEC-14, ACC-27)

Command gọi `ctx.AddAudit(permission, action, reason, before?, after?)` → outbox kind `gm_audit` trong **cùng receipt**
(commit cùng thay đổi) → projection `gm_audit::{entryId}`:

`{entryId, kind, gameId, environmentId, receiptId, playerId, actorId, operation, committedAtUtc,
payload{actorId, permission, action, targetPlayerId, reason, before, after, correlationId}}`

7 trường bắt buộc: actor (`actorId`), permission/action, target (`targetPlayerId`), reason, before/after **hoặc** `receiptId`
(resource adjust tham chiếu receipt — delta nằm ở ledger của receipt đó; progression ghi before/after), thời gian UTC
(`committedAtUtc`), correlation (`correlationId`). Relay inline; lỗi relay → sweeper giao lại (at-least-once, insert-if-absent).

## Cấu hình (vận hành)

| Khoá | Mặc định | Ghi chú |
| --- | --- | --- |
| `Gm:SessionHours` | 8 | 1–24 |
| `Gm:CacheSeconds` | 60 | 0–60 |
| `Gm:Bootstrap:Staff[]` | — | `{staffId, displayName, password, roles[], permissions[], disabled, gameId?, environmentId?}`; **chỉ Development/Test** (môi trường khác → startup fail). Password hash PBKDF2-SHA256 100 000 vòng |

Production: `gm_staff` do ops tạo bằng script cho tới khi có SSO (P0.5); không có login local.
