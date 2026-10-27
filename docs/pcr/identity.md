# PCR — identity

Module framework `identity` (slice 002, version 0.2.0). Nguồn: IDN-01..12, BAN-01, SEC-02, SEC-13.

## Aggregate

| Aggregate | Collection | Schema | Client-visible | Wire |
| --- | --- | --- | --- | --- |
| `profile` | `pa_profile` | 1 | có | `ProfileState{display_name, session_epoch}` — `ban`, `deleted`, `identities` không gửi |

State: `displayName`, `sessionEpoch`, `ban{reason, untilUtc, scope[], actor}`, `deleted{requestedAtUtc, purgeAfterUtc}`, `identities[]`.

## Collections ngoài transaction (CMD-05)

Tên collection `identities` công bố ở `IdentityCollections.Identities` (module business) để module gm tra mapping.

| Collection | Key | Nội dung | Expiry |
| --- | --- | --- | --- |
| `identities` | `guest:{localId}` | `{playerId, linkedAtUtc, providerAttributes.secretHash}` | không |
| `sessions` | hex SHA-256(token) | `{playerId, epoch, createdAtUtc, lastTouchAtUtc}` | `min(lastTouch + 30 d, created + 90 d)` |

`sessionEpoch` tăng bằng `IPlayerAggregateWriter` (transaction, không receipt).

## Endpoint

| Route | Profile | Ghi chú |
| --- | --- | --- |
| `POST /api/v1/identity/login/guest` | Public (allowlist qua `PublicEndpoints`), Protobuf, gated | `GuestLoginRequest{local_id, secret}` → `LoginResponse` |
| `POST /api/v1/identity/logout` | Player | xoá session hiện tại |

## Quy tắc (semantic)

| Mục | Giá trị |
| --- | --- |
| `localId` | `^[A-Za-z0-9_-]{1,64}$` |
| Secret | 32–256 byte; lưu `HMAC-SHA256(pepper, secret)`; so sánh constant-time |
| Pepper | `Identity:GuestPepper` từ secret provider; thiếu ngoài Development/Test → startup fail |
| `playerId` | `p_` + 26 ký tự base32 (128-bit ngẫu nhiên) |
| Token | 256-bit, base64url, chỉ ở `Authorization: Bearer`; không log, không lưu dạng gốc |
| Session | một session/player (takeover qua epoch); touch lười khi > 24 h |
| Thứ tự kiểm tra | `ACCOUNT_DELETED` (401) → `ACCOUNT_SUSPENDED` (403, `details.until`) → `SESSION_REPLACED` (401) → `UNAUTHENTICATED` (401) |
| Ban theo nhóm | `scope` chứa nhóm command (phần trước dấu chấm của operation) → command nhóm đó bị `ACCOUNT_SUSPENDED`; session giữ nguyên; retry của command đã commit vẫn replay |
| Recovery bằng `localId` | không có (IDN-12) |

## Vận hành (non-breaking)

| Tham số | Mặc định |
| --- | --- |
| Cache session/profile per instance (`Identity:CacheSeconds`) | 60 s → takeover/thu hồi ≤ 60 s |
| Login / `localId` / phút | 10 (chặn) |
| Login thất bại / IP / phút | 60 (chặn) |
| Login / IP / phút | 600 (chỉ metric `login_ip_burst_warnings_total`) |
| Metrics | `login_total{result}`, `login_ip_burst_warnings_total`, `session_replaced_total` |

## Ban, unban, tên hiển thị (slice 008 — GM-07, BAN-01, PRF-01)

| Command | Profile | Touches | Ghi chú |
| --- | --- | --- | --- |
| `identity.ban` `{reason, untilUtc?, scope[]}` | Gm, `player.ban` | `profile` | `untilUtc` phải ở tương lai (400); scope ≤ 10 phần tử, `login` hoặc nhóm command `[a-z][a-z0-9_]{0,31}` (= tiền tố operation, vd. `shop`); scope có `login` → `sessionEpoch++` (phiên hiện có kết thúc ≤ 60 s và vẫn chết sau unban); audit before/after = ban cũ/mới |
| `identity.unban` `{reason}` | Gm, `player.ban` | `profile` | xoá `ban`; audit before/after |
| `identity.set_display_name` (`POST /api/v1/identity/display-name`, `SetDisplayNameRequest{display_name}` → `SetDisplayNameResult{display_name}`) | Player | `profile` | 2–20 ký tự (grapheme), chữ cái/dấu Unicode, chữ số, `_ - .`, khoảng trắng đơn ở giữa; không duy nhất. Sai định dạng → `DISPLAY_NAME_INVALID`; policy game từ chối → `DISPLAY_NAME_REJECTED` |

Kiểm tra ban (P0, giữ nguyên): login → `ACCOUNT_SUSPENDED` kèm `until` (ISO UTC hoặc `permanent`), ưu tiên trước
`SESSION_REPLACED`; ban nhóm chỉ chặn command của nhóm, không thu hồi phiên; ban hết hạn tự mở.

**Policy tên hiển thị (EXT-02):** `IDisplayNamePolicy.IsAllowed(name)` — thuần, đồng bộ (chạy trong command, CMD-03);
mặc định chấp nhận; game đăng ký implementation riêng (vd. danh sách từ cấm).

Mã lỗi module identity (14000–14099): `DISPLAY_NAME_INVALID` (14000, business), `DISPLAY_NAME_REJECTED` (14001, business).
Permission: `player.ban`. `Identity:CacheSeconds` 0–60 (PERF-08).
