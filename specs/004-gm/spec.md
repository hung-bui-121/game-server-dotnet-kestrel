# Feature Specification: GM backend + minimal frontend (P0 slice 004)

**Feature Branch**: `004-gm`

**Created**: 2026-10-26

**Status**: Draft

**Input**: "004 GM — GM backend permission/scope/audit + frontend tối thiểu (login Development, player lookup, resource
adjust). Nguồn: GM-01/02/03/06, SEC-02/03 (Development local auth)/07/08/14/15, ACC-09/10/24/27/45."

## Scope note

- SSO/MFA (SEC-03 provider), CSRF antiforgery (SEC-05), CORS (SEC-06), thu hồi ≤ 60 s đầy đủ (SEC-10), GM-07 ban,
  thay quyền staff qua UI, maintenance toggle là **P0.5** — không thuộc slice này. Vì vậy ACC-27 ở P0 được kiểm cho
  *resource adjust* và *progression edit*; audit "đổi quyền staff" và "bật maintenance" đi cùng tính năng P0.5 của chúng.
- TBD-21 (stack GM frontend) còn Mở: frontend P0 là **một trang HTML/JS tĩnh** phục vụ same-origin (TBD-05a), không
  chọn framework.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - GM đăng nhập và chỉ làm được việc mình được cấp (Priority: P1)

Ở Development, staff đăng nhập bằng tài khoản local (seed từ config); server cấp cookie session. Mỗi request GM kiểm
permission và phạm vi `gameId`/`environmentId` ở backend; thiếu quyền hoặc sai phạm vi → 403. Token player không dùng
được ở GM API và ngược lại.

**Acceptance Scenarios**:

1. **Given** staff Support (role → `player.read`), **When** gọi lookup, **Then** 200; **When** gọi adjust, **Then** 403 (ACC-10).
2. **Given** staff có grant cho game khác hoặc environment khác, **When** gọi bất kỳ API có permission, **Then** 403 (SEC-07, ACC-10).
3. **Given** session GM, **When** gọi Player API, **Then** 401; **Given** token player, **When** gọi GM API, **Then** 401 (ACC-09).
4. **Given** staff bị `disabled`, **When** dùng session cũ, **Then** 401 sau ≤ 60 s (cache).
5. **Given** role template, **When** seed staff, **Then** grant lưu permission đã expand; module chỉ kiểm permission (SEC-15).

### User Story 2 - Tra cứu player (Priority: P1)

GM tìm player theo `playerId`, mapping key (`guest:…`) hoặc display name; xem từng aggregate với `rev`, kích thước,
`schemaVersion`; ban/deleted; receipt và ledger có phân trang. PII (identity mapping) chỉ hiện với `player.read.pii`.

**Acceptance Scenarios**:

1. **Given** player có dữ liệu, **When** GM `player.read` lookup theo id hoặc `guest:{localId}`, **Then** thấy mọi aggregate
   kèm `rev`/kích thước, không thấy `identities` (ACC-45, GM-02).
2. **Given** GM có thêm `player.read.pii`, **When** lookup, **Then** thấy `identities`.
3. **Given** list ledger/receipt/search, **When** thiếu `pageSize` hoặc `pageSize > 200`, **Then** 400 Problem Details (GM-06, ACC-24).
4. **Given** nhiều trang, **When** đi theo `nextCursor`, **Then** lấy hết không trùng.

### User Story 3 - Điều chỉnh resource và progression có audit (Priority: P1)

GM điều chỉnh resource của player qua command Economy (reason bắt buộc, idempotent theo `X-Request-Id`), tạo receipt
(actor GM), ledger và audit trong outbox của cùng receipt. Sửa progression cần permission riêng `progression.edit`,
audit ghi before/after. Player thấy thay đổi qua resync.

**Acceptance Scenarios**:

1. **Given** `resource.adjust`, **When** adjust gold +100 reason "compensation", **Then** ví +100, receipt actor GM, ledger
   `sourceType = gm_adjust`, audit đủ 7 trường tham chiếu `receiptId` (không chép delta) (GM-03, SEC-14, ACC-27, ACC-45).
2. **Given** adjust thiếu reason, `itemId` lạ, hoặc trừ quá số dư, **When** gửi, **Then** 400 / `UNKNOWN_ITEM` / `INSUFFICIENT_FUNDS`, không đổi.
3. **Given** retry cùng `X-Request-Id`, **When** gửi lại, **Then** replay, không cộng hai lần.
4. **Given** `progression.edit`, **When** đặt stage = 3, **Then** progression đổi, audit có before/after.
5. **Given** relay inline tắt, **When** adjust, **Then** audit vẫn xuất hiện sau khi sweeper chạy (ACC-27).

### User Story 4 - Trang GM tối thiểu (Priority: P2)

Một trang web same-origin cho phép đăng nhập (Development), tra player, xem aggregate, điều chỉnh resource. Frontend
chỉ là tiện ích; backend quyết định quyền.

**Acceptance Scenarios**:

1. **Given** host chạy, **When** mở `/gm/v1/ui`, **Then** trang tải được (HTML) và gọi các API ở trên.

### Edge Cases

- Cookie từ request cross-site (`Sec-Fetch-Site: cross-site`) cho mutation → 403.
- Session GM hết hạn (8 h) → 401.
- GM adjust trên player chưa tồn tại → aggregate mặc định, vẫn hợp lệ.
- Search display name rỗng → 400.

## Requirements *(mandatory)*

- **FR-001**: Scheme `GmCookie` (SEC-02): cookie `gs_gm` (HttpOnly, SameSite=Lax, Secure khi HTTPS) → `gm_sessions`
  (hash, expiry 8 h) → `gm_staff`; cache ≤ 60 s; staff disabled → 401.
- **FR-002**: `gm_staff/{staffId}` = `{displayName, disabled, passwordHash (Development), grants[{gameId, environmentId, permissions[]}]}`;
  permission hiệu lực = grant khớp `GameServer:GameId`/`EnvironmentId` (SEC-07).
- **FR-003**: Role template → permission đã expand khi seed (SEC-15): Support `{gm.access, player.read}`, Operator
  `{gm.access, player.read, progression.edit}`, EconomyOperator `{gm.access, player.read, resource.adjust}`, Administrator =
  mọi permission trong catalog.
- **FR-004**: Development local login `POST /gm/v1/auth/login` (Public, chỉ Development/Test) với staff seed từ
  `Gm:Bootstrap:Staff`; logout `POST /gm/v1/auth/logout`; `GET /gm/v1/me`.
- **FR-005**: Startup validator: endpoint GM phải có permission thuộc catalog (SEC-08).
- **FR-006**: Lookup `GET /gm/v1/players/{playerId}`, `GET /gm/v1/players/by-mapping/{mappingKey}`,
  `GET /gm/v1/players/search?displayName=&pageSize=&cursor=` (`player.read`); `identities` chỉ với `player.read.pii` (GM-02).
- **FR-007**: `GET /gm/v1/players/{playerId}/ledger|receipts?pageSize=&cursor=` (`player.read`); `pageSize` bắt buộc,
  1–200, cursor keyset (GM-06).
- **FR-008**: `POST /gm/v1/players/{playerId}/resources/adjust` `{resource, delta, reason}` + `X-Request-Id`
  (`resource.adjust`) → command `economy.adjust` (Touches `wallet`, `inventory`) ledger `gm_adjust`, audit (GM-03).
- **FR-009**: `POST /gm/v1/players/{playerId}/progression` `{level?, xp?, stage?, reason}` (`progression.edit`) → command
  `progression.set` của SampleGame, audit before/after.
- **FR-010**: Audit = outbox `gm_audit` → projection `gm_audit::{entryId}` trong `gm_audit` với 7 trường SEC-14: actor,
  permission/action, target, reason, before/after hoặc `receiptId`, thời gian UTC, correlation.
- **FR-011**: Mutation GM bị từ chối khi `Sec-Fetch-Site = cross-site` (phần P0 của SEC-05).
- **FR-012**: Trang tĩnh `GET /gm/v1/ui` (Public allowlist của module GM).
- **FR-013**: Platform: `IPagedQueryStore` (lọc bằng một trường, sắp theo một trường, keyset cursor) — in-memory và Couchbase N1QL.

## Success Criteria *(mandatory)*

- **SC-001**: 100% endpoint GM từ chối khi thiếu permission/sai scope trong bộ test.
- **SC-002**: Mọi mutation GM có đúng một audit đủ 7 trường.
- **SC-003**: Mọi list GM từ chối thiếu/vượt `pageSize`.

## Assumptions

- Production chưa có GM login cho tới khi SSO (P0.5); `gm_staff` ở Production do ops tạo bằng script.
- PII ở P0 = `identities` (mapping key chứa local id).
