# Feature Specification: Identity & Session — guest (P0 slice 002)

**Feature Branch**: `002-identity-session`

**Created**: 2026-10-26

**Status**: Draft

**Input**: User description: "002 identity/session (guest) — slice thứ 2 của P0: guest login bằng localId + secret, playerId do server cấp, single session + takeover qua sessionEpoch, Bearer token, thứ tự lỗi ACCOUNT_DELETED → ACCOUNT_SUSPENDED → SESSION_REPLACED → UNAUTHENTICATED, ban trong pa_profile, rate limit login, scheme player tách GM. Nguồn: Requirements v1.2 rev 4."

Nguồn yêu cầu: `Requirements/Game_Server_System_Requirements_v1.2.md` rev 4. Phụ thuộc slice 001 (runner,
snapshot, error catalog, profile aggregate). Liên kết PGS/Game Center (IDN-03..06, IDN-11), xoá tài khoản
(PRIV-01), thao tác GM ban (GM-07) thuộc P0.5 — ngoài slice này.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Guest đăng nhập và nhận dữ liệu ban đầu (Priority: P1)

Client mới cài sinh `localId` và một secret ngẫu nhiên ≥ 256-bit (lưu Keychain/Keystore), gọi login. Lần đầu
server tạo player mới với `playerId` do server cấp; các lần sau trả đúng player đó. Response gồm session token,
`playerId`, snapshot mọi section client-visible kèm `rev`, `configVersion`, thời gian server và thời điểm hết hạn
session. Mọi request Player sau đó gửi token trong `Authorization: Bearer`.

**Why this priority**: Không có login thì không có actor thật cho mọi command (IDN-01, IDN-02, IDN-07, ACC-01).

**Independent Test**: Login guest mới → dùng token gọi command counter và snapshot → login lại cùng credential →
cùng `playerId`, dữ liệu còn nguyên.

**Acceptance Scenarios**:

1. **Given** `localId` chưa từng thấy, **When** login với secret S, **Then** tạo player mới, trả token, `playerId`
   opaque, snapshot mặc định (`rev = 0`), `configVersion`, `serverTimeUtc` (IDN-01, IDN-07, SMP-01 phần login).
2. **Given** player đã có, **When** login lại với cùng `localId` + S, **Then** cùng `playerId`, snapshot phản ánh
   dữ liệu đã commit (ACC-01).
3. **Given** player đã có, **When** login với `localId` đúng nhưng secret khác, **Then** 401 `UNAUTHENTICATED`, thông
   điệp giống mọi lỗi xác thực khác, không lộ việc `localId` tồn tại (IDN-02, ACC-02).
4. **Given** hai thiết bị login lần đầu cùng `localId` + S cùng lúc, **When** cả hai xử lý, **Then** đúng một player
   được tạo và cả hai nhận cùng `playerId` (IDN-01 "thua race → trả player thắng").
5. **Given** token hợp lệ, **When** logout, **Then** token không dùng được nữa (401) và login lại vẫn ra player cũ (ACC-01).
6. **Given** `playerId` của người khác, **When** client đưa vào payload/query, **Then** bị bỏ qua; dữ liệu chỉ đọc/ghi
   theo actor của token (ACC-02, API-12, IDN-09).

---

### User Story 2 - Một phiên mỗi player (takeover) (Priority: P1)

Mỗi login mới làm phiên cũ của cùng player mất hiệu lực. Request bằng phiên cũ nhận 401 `SESSION_REPLACED`
(không retryable). Hiệu lực lan tới mọi instance trong ≤ 60 s (cache per instance).

**Why this priority**: Mô hình session đã chốt (TBD-04a, IDN-08, IDN-10, ACC-20).

**Independent Test**: Hai host chung store: login A trên instance 1, login B (cùng credential) trên instance 2; token
A bị từ chối `SESSION_REPLACED` trên cả hai instance trong ≤ 60 s.

**Acceptance Scenarios**:

1. **Given** phiên A đang dùng, **When** cùng player login lại (phiên B), **Then** request bằng A → 401
   `SESSION_REPLACED`, `retryable = false`; B dùng được (IDN-10).
2. **Given** hai instance có cache session/profile 60 s, **When** takeover xảy ra trên instance khác, **Then** A bị từ
   chối trên instance cũ chậm nhất sau 60 s (ACC-20).
3. **Given** session quá 30 ngày không dùng hoặc quá 90 ngày kể từ tạo, **When** dùng token, **Then** 401
   `UNAUTHENTICATED` (IDN-08).
4. **Given** session được dùng lần cuối cách > 24 h, **When** có request hợp lệ, **Then** hạn trượt được gia hạn
   (lazy touch) nhưng không vượt 90 ngày tuyệt đối (IDN-08).

---

### User Story 3 - Thứ tự kiểm tra trạng thái tài khoản (Priority: P1)

Mỗi request Player kiểm tra theo thứ tự: tài khoản đã yêu cầu xoá → `ACCOUNT_DELETED`; bị cấm đăng nhập →
`ACCOUNT_SUSPENDED` kèm `until`; phiên cũ → `SESSION_REPLACED`; token lạ/hết hạn → `UNAUTHENTICATED`. Login cũng
từ chối tài khoản bị xoá/cấm. Cấm theo nhóm command chỉ chặn command thuộc nhóm đó.

**Why this priority**: Contract lỗi mà SDK phải xử lý (IDN-10, BAN-01 phần P0, ACC-31 phần kiểm tra).

**Independent Test**: Ghi trực tiếp `ban`/`deleted` vào `pa_profile` (công cụ test), kiểm tra mã lỗi và thứ tự.

**Acceptance Scenarios**:

1. **Given** profile có `ban {scope: login, until}` chưa hết hạn, **When** login hoặc dùng token, **Then**
   `ACCOUNT_SUSPENDED` kèm `until`, ưu tiên trước `SESSION_REPLACED` (BAN-01).
2. **Given** ban đã hết hạn, **When** login, **Then** thành công.
3. **Given** profile có `ban {scope: [counter]}`, **When** gọi `counter.increment`, **Then** `ACCOUNT_SUSPENDED`; snapshot
   và command nhóm khác vẫn chạy; session không bị thu hồi.
4. **Given** profile có `deleted`, **When** login hoặc dùng token, **Then** 401 `ACCOUNT_DELETED` (ưu tiên cao nhất).

---

### User Story 4 - Giới hạn login và tách scheme (Priority: P2)

Login bị giới hạn theo `localId` (≤ 10/phút) và theo IP chỉ với lần thất bại (≤ 60/phút); tổng request login/IP
> 600/phút chỉ phát cảnh báo (metric), không chặn — nhiều thiết bị sau CGNAT không bị chặn oan. Token player không
được chấp nhận trên GM API và ngược lại.

**Why this priority**: SEC-13 (ACC-52 phần login), SEC-02 (ACC-09).

**Independent Test**: 11 login/phút cùng `localId` → lần 11 bị 429; 61 lần sai secret/phút từ một IP → 429; 100
login hợp lệ khác `localId` cùng IP → không 429; token player gọi GM → 401.

**Acceptance Scenarios**:

1. **Given** 10 login trong 1 phút cho một `localId`, **When** login lần 11, **Then** 429 `RATE_LIMITED` + `Retry-After`,
   không lộ tồn tại tài khoản (SEC-13).
2. **Given** 60 lần login thất bại từ một IP trong 1 phút, **When** login lần 61 (kể cả hợp lệ), **Then** 429.
3. **Given** nhiều `localId` khác nhau login hợp lệ từ cùng IP, **When** vượt 600/phút, **Then** không chặn, metric cảnh báo tăng.
4. **Given** token player, **When** gọi GM endpoint, **Then** 401; **Given** danh tính GM, **When** gọi Player endpoint, **Then** 401 (SEC-02, ACC-09).

### Edge Cases

- `localId` rỗng/quá dài (> 64)/ký tự lạ; secret ngắn hơn 32 byte → 400, không tạo player.
- Token sai định dạng, header `Authorization` không phải Bearer, token trong body → 401 (IDN-10).
- Session document đã bị xoá (logout) nhưng còn trong cache instance khác → hết hiệu lực ≤ 60 s.
- Profile chưa tồn tại (player mới) → epoch tăng từ 0 lên 1 khi login đầu (Insert qua writer).
- Mất secret khi chưa liên kết provider → không có API khôi phục bằng `localId` (IDN-12).
- Token, secret, pepper không xuất hiện trong log (LOG-05).
- Đồng hồ server dịch ở Development (FakeClock) ảnh hưởng hạn session và ban.

## Requirements *(mandatory)*

### Functional Requirements

**Guest credential và player**

- **FR-001**: Login guest nhận `localId` (1–64 ký tự `[A-Za-z0-9_-]`) và `secret` (≥ 32 byte); server lưu
  `HMAC-SHA256(pepper, secret)` trong mapping, không lưu secret; pepper từ secret configuration (OPS-08) (IDN-02).
- **FR-002**: Mapping `identities` key `guest:{localId}` → `{playerId, linkedAt, providerAttributes{secretHash}}`;
  tạo player lần đầu = Insert mapping; thua race → đọc lại mapping và xác minh secret (IDN-01, IDN-04).
- **FR-003**: `playerId` là chuỗi opaque do server sinh (`p_` + 26 ký tự base32 ngẫu nhiên 128-bit), ổn định (IDN-01).
- **FR-004**: Sai secret → 401 `UNAUTHENTICATED` với cùng thông điệp và thời gian xử lý tương đương trường hợp
  khác (so sánh constant-time) (IDN-02, SEC-13).
- **FR-005**: Không có API khôi phục bằng `localId` (IDN-12).

**Session**

- **FR-010**: Token = 256-bit ngẫu nhiên, base64url; lưu `sessions/{hex(SHA-256(token))}` =
  `{playerId, epoch, createdAtUtc, lastTouchAtUtc}`; không lưu token gốc (IDN-08).
- **FR-011**: Mỗi login tăng `pa_profile.sessionEpoch` bằng `IPlayerAggregateWriter` (transaction, không receipt);
  session mới mang epoch mới (IDN-08).
- **FR-012**: Hạn session: trượt 30 ngày, tuyệt đối 90 ngày; document có expiry tương ứng; lazy touch khi lần
  touch trước cách > 24 h (IDN-08, DAT-03).
- **FR-013**: Token chỉ nhận ở `Authorization: Bearer` (IDN-10).
- **FR-014**: Kiểm tra mỗi request Player theo thứ tự: `deleted` → 401 `ACCOUNT_DELETED`; ban scope login còn hạn →
  403 `ACCOUNT_SUSPENDED` (kèm `until`); `epoch < profile.sessionEpoch` → 401 `SESSION_REPLACED`; token lạ/hết hạn →
  401 `UNAUTHENTICATED` (IDN-10, BAN-01).
- **FR-015**: Cache session và profile per instance ≤ 60 s (cấu hình); takeover/thu hồi có hiệu lực ≤ 60 s (IDN-08).
- **FR-016**: Logout xoá session document và cache cục bộ (ACC-01).
- **FR-017**: Login trả `session_token`, `player_id`, `sections` (snapshot client-visible), `config_version`,
  `server_time_utc_ms`, `session_expires_at_utc_ms` (IDN-07, SMP-01 phần login, CFG-03 phần login: chỉ version).

**Ban**

- **FR-020**: `pa_profile.ban = {reason, untilUtc, scope: "login" | [command groups], actor}`; nhóm command = phần
  trước dấu chấm của operation (BAN-01).
- **FR-021**: Ban scope login kiểm tại login và mọi request; ban scope command kiểm trước command trong nhóm qua
  hook guard của pipeline, không thu hồi session (BAN-01, EXT-04).

**Rate limit và scheme**

- **FR-030**: Login ≤ 10/phút/`localId` (chặn); thất bại ≤ 60/phút/IP (chặn); > 600 request login/phút/IP chỉ tăng
  metric `login_ip_burst_warnings_total`; vượt → 429 `RATE_LIMITED` + `Retry-After` (SEC-13).
- **FR-031**: Player endpoint chỉ xác thực bằng scheme player (Bearer); GM endpoint chỉ bằng scheme GM; scheme thử
  nghiệm header chỉ ở Development/test (SEC-02, SEC-16).
- **FR-032**: Login là endpoint Public trong allowlist (SEC-09) nhưng vẫn qua gate maintenance/version (GATE-01/02 slot).

**Error codes (core, thêm vào catalog)**: `ACCOUNT_SUSPENDED` (403), `ACCOUNT_DELETED` (401), `SESSION_REPLACED`
(401), `IDENTITY_CONFLICT` (409, dùng P0.5), `SWITCH_ACCOUNT` (409, dùng P0.5).

### Key Entities

- **Identity mapping**: `identities/guest:{localId}` → player + hash secret + thời điểm link.
- **Session**: `sessions/{hash(token)}` → player, epoch, thời điểm tạo/touch; có expiry.
- **Profile (aggregate `profile`)**: `displayName`, `sessionEpoch`, `ban`, `deleted`, `identities[]`.

## Success Criteria *(mandatory)*

- **SC-001**: 100% login guest hợp lệ trả cùng `playerId` cho cùng credential qua nhiều lần login và cài lại (giữ secret).
- **SC-002**: 0 trường hợp đọc/ghi được dữ liệu player khác chỉ bằng `localId` hoặc `playerId` của họ.
- **SC-003**: Sau takeover, phiên cũ bị từ chối trên mọi instance trong ≤ 60 s.
- **SC-004**: Login hợp lệ từ ≥ 100 thiết bị sau cùng một IP trong một phút không bị chặn.
- **SC-005**: 0 token/secret/pepper xuất hiện trong log của bộ test.

## Assumptions

- Pepper đọc từ `Identity:GuestPepper` (biến môi trường/secret provider); Development có giá trị mặc định chỉ dùng cho dev.
- HMAC-SHA256 đủ cho secret entropy ≥ 256-bit (không cần KDF chậm).
- Session/identity là document ngoài transaction, ghi bằng Insert/CAS (CMD-05); platform cung cấp store
  key-value không-transaction cho collection không phải `pa_*`/`receipts`.
- Rate limiter per instance (budget tổng = giá trị × số instance, SEC-13/PERF-05).
- `playerId` của GM API vẫn lấy từ route (GM chọn player đích); actor GM thật thuộc slice 004.
