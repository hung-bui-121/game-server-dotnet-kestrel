# Feature Specification: Maintenance / version gate, ban, display name (P0.5 slice 008)

**Feature Branch**: `008-gate-ban`

**Created**: 2026-10-27

**Status**: Draft

**Input**: "008 gate-ban — P0.5: GATE-01 (maintenance flag theo game/env, GM bật/tắt với `ops.maintenance`, audit,
503 `MAINTENANCE` trước auth, cache ≤ 10 s), GATE-02 (`minClientVersion` per platform trong document `gate`, 426
`UPDATE_REQUIRED` kèm URL cập nhật), BAN-01 phần P0.5 + GM-07 (ban/unban với reason, thời hạn, scope, audit,
`player.ban`), PERF-08 (TTL cache ghi PCR), PRF-01 (display name do server validate, profanity hook là policy của game).
Nghiệm thu ACC-30, ACC-31. GM frontend ngoài phạm vi (owner 2026-10-27): chỉ GM API + OpenAPI."

## Scope note

- P0 đã có: slot middleware gate + mã `MAINTENANCE`/`UPDATE_REQUIRED`, header `X-Client-Version`/`X-Platform`/
  `X-Build-Id` từ SDK, trường `ban` trong `pa_profile` + kiểm tra ở login/session/command (`ACCOUNT_SUSPENDED` ưu tiên
  trước `SESSION_REPLACED`), sự kiện SDK `Maintenance`/`UpdateRequired`/`AccountBlocked`.
- Slice này thêm: document `gate` + GM API sửa nó có audit; evaluator thật; GM command ban/unban; command đổi tên hiển
  thị + policy; TTL cache có kiểm tra cấu hình.
- Không gồm: PGS/Game Center (009), xoá tài khoản (010).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Ops bật bảo trì (Priority: P1)

Ops bật cờ bảo trì kèm thông điệp và giờ dự kiến kết thúc; trong vòng 10 giây mọi request Player API (kể cả login) nhận
503 `MAINTENANCE` trước khi xác thực, còn GM API và health vẫn chạy. Tắt cờ thì client dùng lại bình thường.

**Acceptance Scenarios**:

1. **Given** ops có `ops.maintenance`, **When** bật bảo trì (message, expectedEndUtc, reason), **Then** trong ≤ 10 s trên mọi
   instance Player API trả 503 `MAINTENANCE` có message + giờ dự kiến + `retryAfterMs`; GM API, `/health/*` vẫn 200 (ACC-30).
2. **Given** bảo trì đang bật, **When** request Player không có token, **Then** vẫn 503 `MAINTENANCE` (không 401).
3. **Given** thao tác bật/tắt, **When** xem audit, **Then** có đúng một bản ghi cho mỗi thay đổi (actor, lý do, before/after).
4. **Given** thiếu `ops.maintenance`, **When** bật, **Then** 403.

### User Story 2 - Buộc cập nhật client cũ (Priority: P1)

Ops đặt phiên bản client tối thiểu cho từng nền tảng kèm URL cập nhật; client cũ hơn nhận 426 `UPDATE_REQUIRED` kèm URL
trước mọi nghiệp vụ; SDK phát sự kiện cập nhật cho game.

**Acceptance Scenarios**:

1. **Given** `minClientVersion[android] = 1.3.0`, **When** client android 1.2.9 gọi API, **Then** 426 `UPDATE_REQUIRED` với
   `updateUrl`, `minClientVersion`; client 1.3.0 và ios 1.0.0 vẫn qua (ACC-30).
2. **Given** min đặt cho nền tảng, **When** client không gửi hoặc gửi phiên bản sai định dạng, **Then** 426.
3. **Given** SDK nhận 426, **When** xử lý, **Then** sự kiện `UpdateRequired` có URL cập nhật; SDK không retry.

### User Story 3 - GM ban / unban player (Priority: P1)

GM có `player.ban` khoá player (lý do, thời hạn hoặc vĩnh viễn, scope `login` hoặc nhóm command), có audit; ban scope
login thu hồi phiên hiện có trong ≤ 60 s; unban thì đăng nhập lại được.

**Acceptance Scenarios**:

1. **Given** player đang có phiên, **When** GM ban scope `login` tới T, **Then** login trả `ACCOUNT_SUSPENDED` kèm `until` = T,
   phiên hiện có bị từ chối ≤ 60 s trên mọi instance, có audit (ACC-31).
2. **Given** ban scope `shop`, **When** player mua hàng, **Then** `ACCOUNT_SUSPENDED`; command nhóm khác và phiên vẫn hoạt động.
3. **Given** player đã bị ban, **When** GM unban, **Then** login được, phiên mới hoạt động; audit ghi before/after.
4. **Given** ban hết hạn, **When** login, **Then** thành công không cần unban.
5. **Given** thiếu `player.ban` hoặc player không tồn tại, **Then** 403 / 404, không ghi gì.

### User Story 4 - Player đặt tên hiển thị (Priority: P2)

Player đổi tên hiển thị; server kiểm độ dài 2–20 ký tự, tập ký tự cho phép, và policy của game (vd. lọc từ cấm).

**Acceptance Scenarios**:

1. **Given** tên hợp lệ, **When** đổi, **Then** `profile.displayName` cập nhật, section `profile` trả về rev mới.
2. **Given** tên 1 ký tự, 21 ký tự, có ký tự điều khiển/emoji, hoặc khoảng trắng đầu/cuối, **Then** `DISPLAY_NAME_INVALID`, không đổi.
3. **Given** game thay policy (từ cấm "badword"), **When** đặt tên chứa từ đó, **Then** `DISPLAY_NAME_REJECTED`.

### Edge Cases

- Document `gate` chưa tồn tại → không bảo trì, không min version.
- Store không đọc được khi làm mới cache gate → giữ giá trị cache cũ (fail-open cho gate), log Warning, health báo.
- Ban với `until` trong quá khứ hoặc scope rỗng → 400.
- Hai GM ban/unban cùng lúc → command pipeline (transaction) tuần tự hoá, audit đúng thứ tự commit.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** (GATE-01): Document `gate/gate` `{maintenance{enabled, message, expectedEndUtc}, minClientVersions{platform:
  {minVersion, updateUrl}}, rev}`; chỉ sửa qua GM API trong transaction có audit (`IAuditedStore`), permission
  `ops.maintenance`, `X-Request-Id` + `reason` bắt buộc.
- **FR-002**: Evaluator thật đọc `gate` với cache ≤ `GameServer:Gate:CacheSeconds` (mặc định 10, tối đa 10); maintenance →
  503 `MAINTENANCE` với details `message`, `expectedEndUtc`, `retryAfterMs` (tới giờ dự kiến, tối thiểu 30 s); kiểm trước
  authentication trên endpoint có `GateMetadata` (Player API + login); GM, health, Dev không bị chặn.
- **FR-003** (GATE-02): Platform (header `X-Platform`, so khớp không phân biệt hoa thường) có min → `X-Client-Version`
  (dạng `a.b[.c[.d]]` số) nhỏ hơn, thiếu hoặc sai → 426 `UPDATE_REQUIRED` với details `minClientVersion`, `updateUrl`.
  Maintenance ưu tiên trước version.
- **FR-004**: `GET /gm/v1/ops/gate` (`ops.read`), `PUT /gm/v1/ops/gate/maintenance`, `PUT /gm/v1/ops/gate/min-client-version`
  (`ops.maintenance`); validate URL https (ngoài Development), phiên bản đúng định dạng, message ≤ 500.
- **FR-005** (GM-07): GM command `identity.ban` `{reason, untilUtc?, scope[]}` và `identity.unban` `{reason}`
  (`player.ban`) qua pipeline command (receipt + audit before/after); ban có scope `login` tăng `sessionEpoch`.
- **FR-006** (PRF-01): Player command `profile.set_display_name`: 2–20 ký tự (grapheme), chữ cái/số Unicode, khoảng trắng
  đơn, `_ - .`; không khoảng trắng đầu/cuối hay liên tiếp; policy `IDisplayNamePolicy` (EXT-02, thay được) quyết định từ
  cấm; mã `DISPLAY_NAME_INVALID`, `DISPLAY_NAME_REJECTED` (business, HTTP 200).
- **FR-007** (PERF-08): TTL cache ghi PCR và được kiểm khi khởi động: session/profile (`Identity:CacheSeconds` ≤ 60),
  staff/permission (`Gm:CacheSeconds` ≤ 60), gate (≤ 10). Config không cache TTL.
- **FR-008**: OpenAPI GM cập nhật; SDK có test xử lý `MAINTENANCE`/`UPDATE_REQUIRED` từ server thật (sự kiện + details).

### Key Entities

- **Gate**: maintenance (bật, thông điệp, giờ dự kiến kết thúc), min client version + URL cập nhật theo nền tảng.
- **Ban** (đã có trong `profile`): lý do, đến khi, scope, actor.

## Success Criteria *(mandatory)*

- **SC-001**: Bật/tắt bảo trì có hiệu lực trên 2 instance trong ≤ 10 s ở 100% lần chạy test.
- **SC-002**: 100% request Player của client dưới min version bị chặn trước nghiệp vụ; client đạt min không bị ảnh hưởng.
- **SC-003**: Ban scope login thu hồi phiên hiện có trong ≤ 60 s; mỗi ban/unban có đúng 1 audit.
- **SC-004**: 100% tên hiển thị không hợp lệ trong bộ test bị từ chối, không thay đổi dữ liệu.

## Assumptions

- `retryAfterMs` của bảo trì = thời gian tới `expectedEndUtc` (tối thiểu 30 s, tối đa 1 h); không có giờ dự kiến → 5 phút.
- Platform hợp lệ: chuỗi `[a-z0-9_-]{1,32}` (vd. `android`, `ios`, `editor`).
- Policy tên mặc định chỉ kiểm định dạng; game cung cấp danh sách từ cấm qua policy riêng.
