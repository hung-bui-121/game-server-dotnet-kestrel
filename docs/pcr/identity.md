# PCR — identity

Module framework `identity` (slice 002, version 0.2.0). Nguồn: IDN-01..12, BAN-01, SEC-02, SEC-13.

## Aggregate

| Aggregate | Collection | Schema | Client-visible | Wire |
| --- | --- | --- | --- | --- |
| `profile` | `pa_profile` | 1 | có | `ProfileState{display_name, session_epoch}` — `ban`, `deleted`, `identities` không gửi |

State: `displayName`, `sessionEpoch`, `ban{reason, untilUtc, scope[], actor}`, `deleted{requestedAtUtc, purgeAfterUtc}`, `identities[]`.

## Collections ngoài transaction (CMD-05)

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
