# PCR — identity

Module framework `identity` (slice 002; version 0.9.0 sau slice 009). Nguồn: IDN-01..12, BAN-01, SEC-02, SEC-13.

## Aggregate

| Aggregate | Collection | Schema | Client-visible | Wire |
| --- | --- | --- | --- | --- |
| `profile` | `pa_profile` | 1 | có | `ProfileState{display_name, session_epoch}` — `ban`, `deleted`, `identities` không gửi |

State: `displayName`, `sessionEpoch`, `ban{reason, untilUtc, scope[], actor}`, `deleted{requestedAtUtc, purgeAfterUtc}`, `identities[]`.

## Collections ngoài transaction (CMD-05)

Tên collection `identities` công bố ở `IdentityCollections.Identities` (module business) để module gm tra mapping.

| Collection | Key | Nội dung | Expiry |
| --- | --- | --- | --- |
| `identities` | `guest:{localId}` | `{playerId, linkedAtUtc, providerAttributes.secretHash}` (ghi KV) | không |
| `identities` | `{provider}:{subject}` (`pgs:{playerId}`, `gamecenter:{teamPlayerId}`, adapter của game) | login lần đầu: `{mappingKey, playerId, linkedAtUtc, attributes}`; link: claim marker `{claimKey, receiptId, playerId, createdAtUtc, attributes}` — đều có `playerId` + `attributes`; **chỉ ghi trong transaction** (`ClaimStore` / claim của command) | không |
| `sessions` | hex SHA-256(token) | `{playerId, epoch, createdAtUtc, lastTouchAtUtc}` | `min(lastTouch + 30 d, created + 90 d)` |

`sessionEpoch` tăng bằng `IPlayerAggregateWriter` (transaction, không receipt).

## Endpoint

| Route | Profile | Ghi chú |
| --- | --- | --- |
| `POST /api/v1/identity/login/guest` | Public (allowlist qua `PublicEndpoints`), Protobuf, gated | `GuestLoginRequest{local_id, secret}` → `LoginResponse` |
| `POST /api/v1/identity/logout` | Player | xoá session hiện tại |
| `POST /api/v1/identity/login/provider` | Public, Protobuf, gated | `ProviderLoginRequest{credential, switch_account}` → `LoginResponse`; gửi kèm `Authorization` của phiên đang có (nếu có) |
| `POST /api/v1/identity/link` | Player command `identity.link` | `LinkIdentityRequest{credential}` → `LinkIdentityResult{provider, already_linked}` |

## Provider identity (slice 009 — IDN-03..06, IDN-11)

`ProviderCredential` = oneof `play_games{server_auth_code}` | `game_center{public_key_url, signature, salt, timestamp
(ms), team_player_id, game_player_id, bundle_id}` | `custom{provider, data}` (adapter của game). Server **không tin**
id trong credential; adapter xác minh với provider, trước mọi transaction.

| Mục | Giá trị (semantic) |
| --- | --- |
| Play Games v2 | POST `https://oauth2.googleapis.com/token` (`authorization_code`, client id/secret, `redirect_uri=""`) → GET `games/v1/players/me`; subject `playerId`; attributes `gamePlayerId`, `originalPlayerId` (nếu có, do Google trả). Mã dùng một lần. Chỉ `invalid_grant` (và 401/403 của `players/me`) là `IDENTITY_PROOF_INVALID`; Google từ chối OAuth client của server (`invalid_client`, `redirect_uri_mismatch`…) → log Error + `PROVIDER_UNAVAILABLE`, không tính vào giới hạn của player |
| Game Center | URL khoá công khai: https, host ∈ `PublicKeyHosts` (mặc định `static.gc.apple.com`), cổng mặc định, path `/public-key/…`, không query/fragment; **không theo redirect**. Chứng chỉ ≤ 16 KiB, chuỗi tới root pin (mặc định DigiCert Trusted Root G4 + DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1), `O=Apple Inc.`, EKU code signing, còn hạn, không kiểm thu hồi; cache theo URL ≤ 24 h và ≤ hạn chứng chỉ; `|now − timestamp| ≤ 10 phút`; `bundle_id` ∈ cấu hình; RSA-SHA256 PKCS#1 trên `teamPlayerID ‖ bundleID ‖ timestamp(UInt64 BE) ‖ salt`; subject `teamPlayerID`; attributes `unverifiedGamePlayerId` (**client tự khai, không nằm trong chữ ký — không dùng để cấp quyền/thưởng**), `bundleId`. Chứng chỉ hết hạn / 404 → `IDENTITY_PROOF_INVALID`; chuỗi hay subject không khớp pin, 5xx, redirect → log Error + `PROVIDER_UNAVAILABLE` (pin cần cập nhật) |
| Thời hạn, bulkhead | mỗi lần xác minh (mọi lời gọi provider) ≤ `min(TimeoutSeconds, 0,8 × GameServer:Limits:PlayerTimeoutSeconds)`; tối đa `MaxConcurrentVerifications` (32) lần xác minh song song mỗi instance; vượt → `PROVIDER_UNAVAILABLE` |
| Giới hạn link | `LinkPerPlayerPerMinute` (10) mỗi player → 429; link dùng chung giới hạn IP với login (proof sai tính là lỗi) |
| Replay chữ ký Game Center | rủi ro chấp nhận: credential Game Center bị lộ (TLS bị phá ở client) dùng lại được trong ≤ 10 phút; không có cache chống replay |
| Mapping key | `{provider}:{subject}`; provider `[a-z][a-z0-9]{1,31}` ≠ `guest`; subject 1–128 ASCII in được, không khoảng trắng |
| Link (`identity.link`) | một transaction {claim mapping, `profile.identities`, receipt}. Đã link → OK `already_linked`; đã có identity khác cùng provider → `PROVIDER_ALREADY_LINKED` (business); mapping của player khác → **409 `IDENTITY_CONFLICT`** details `provider`, không receipt, không đổi gì. Hai player đua cùng identity → một thắng (ACC-03) |
| Retry link | request đã commit được replay **trước** khi xác minh (mã PGS không bị dùng lại); khác body → 409 `IDEMPOTENCY_PAYLOAD_MISMATCH` |
| Login provider | mapping có → player đó (độc lập guest credential, IDN-11); chưa có → player mới, mapping insert trong transaction, đua → player thắng. Rồi như guest: kiểm tra tài khoản → `sessionEpoch++` (+ key vào `identities`) → session → snapshot |
| `SWITCH_ACCOUNT` | phiên gửi kèm hợp lệ (đúng epoch) của player khác player kết quả, `switch_account = false` → **409** details `provider`, `account` = `existing` \| `new`; phiên hiện tại không đổi; token sai/hết hạn bị bỏ qua. Không merge |
| Lỗi | `IDENTITY_PROOF_INVALID` (401, tính vào giới hạn lỗi/IP), `PROVIDER_UNAVAILABLE` (503, retryable, `retryAfterMs` 2000), provider chưa bật / credential thiếu → 400 |
| Adapter của game | đăng ký `IIdentityProviderAdapter` (DI); hai adapter cùng provider, tên sai hoặc `guest` → startup fail |

Giới hạn đã biết: nếu tiến trình chết giữa insert mapping và cập nhật `profile` ở login provider đầu tiên, `identities`
của profile thiếu key cho tới lần login provider kế tiếp (login thêm lại); trong khoảng đó link chính identity đó trả
`IDENTITY_CONFLICT`.

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
| `Identity:Providers:UseFake` | mặc định true chỉ ở Development (`pgs` mã `fake:{id}`, `gamecenter` URL `fake:`); Test phải đặt rõ; true ngoài Development/Test → startup fail |
| `Identity:Providers:TimeoutSeconds` | 5 (1–30; bị chặn ở 80 % `PlayerTimeoutSeconds`) |
| `Identity:Providers:MaxConcurrentVerifications` / `LinkPerPlayerPerMinute` | 32 / 10 (1–1000) |
| `Identity:Providers:PlayGames` | `Enabled`, `ClientId`, `ClientSecret` (secret provider), `TokenUri`, `PlayersMeUri`; bật thiếu id/secret → startup fail |
| `Identity:Providers:GameCenter` | `Enabled`, `PublicKeyHosts[]` (`static.gc.apple.com`; chỉ host apple.com), `BundleIds[]` (bật mà rỗng → startup fail), `MaxTimestampSkewMinutes` 10 (1–10), `CertificateCacheHours` 24 (0–24), `TrustedRootsBase64[]`, `IntermediatesBase64[]` (sai định dạng → startup fail), `RequiredOrganization` `Apple Inc.` |

## Ban, unban, tên hiển thị (slice 008 — GM-07, BAN-01, PRF-01)

| Command | Profile | Touches | Ghi chú |
| --- | --- | --- | --- |
| `identity.ban` `{reason, untilUtc?, scope[]}` | Gm, `player.ban` | `profile` | `untilUtc` phải ở tương lai (400); bỏ `scope` = `[login]`, `scope: []` → 400; scope ≤ 10 phần tử, mỗi phần tử là `login` hoặc nhóm command có trong deployment (= tiền tố operation, vd. `shop`), nhóm không tồn tại → 400; scope có `login` → `sessionEpoch++` (phiên hiện có kết thúc ≤ 60 s và vẫn chết sau unban); audit before/after = ban cũ/mới |
| `identity.unban` `{reason}` | Gm, `player.ban` | `profile` | xoá `ban`; audit before/after |
| `identity.set_display_name` (`POST /api/v1/identity/display-name`, `SetDisplayNameRequest{display_name}` → `SetDisplayNameResult{display_name}`) | Player | `profile` | 2–20 ký tự (grapheme), chữ cái/dấu Unicode, chữ số, `_ - .`, khoảng trắng đơn ở giữa; mỗi từ bắt đầu bằng chữ/số; tối đa 3 dấu trên một ký tự; ký tự vô hình (U+115F, U+1160, U+3164, U+FFA0, U+034F, U+FE00–FE0F, U+180B–180D, U+17B4–17B5) bị từ chối; lưu dạng NFC (trả về trong `display_name`); không duy nhất. Sai định dạng → `DISPLAY_NAME_INVALID`; policy game từ chối → `DISPLAY_NAME_REJECTED` |

Kiểm tra ban (P0, giữ nguyên): login → `ACCOUNT_SUSPENDED` kèm `until` (ISO UTC hoặc `permanent`), ưu tiên trước
`SESSION_REPLACED`; ban nhóm chỉ chặn command của nhóm, không thu hồi phiên; ban hết hạn tự mở.

**Policy tên hiển thị (EXT-02):** `IDisplayNamePolicy.IsAllowed(name)` — nhận tên dạng NFKC (fullwidth/compat đã gập,
vd. `ｂａｄ` → `bad`); thuần, đồng bộ (chạy trong command, CMD-03);
mặc định chấp nhận; game đăng ký implementation riêng (vd. danh sách từ cấm).

Mã lỗi module identity (14000–14099): `DISPLAY_NAME_INVALID` (14000, business), `DISPLAY_NAME_REJECTED` (14001, business),
`PROVIDER_ALREADY_LINKED` (14002, business), `IDENTITY_PROOF_INVALID` (14003, 401), `PROVIDER_UNAVAILABLE` (14004, 503, retryable).
Permission: `player.ban`. `Identity:CacheSeconds` 0–60 (PERF-08).
