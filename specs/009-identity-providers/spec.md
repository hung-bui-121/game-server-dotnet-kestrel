# Feature Specification: Provider identities — Play Games / Game Center login, link, conflict, switch (P0.5 slice 009)

**Feature Branch**: `009-identity-providers`

**Created**: 2026-10-27

**Status**: Draft

**Input**: "009 identity-providers — P0.5: IDN-03 (adapter xác minh Google Play Games Services v2 và Apple Game Center,
`IIdentityProviderAdapter` cho provider khác), IDN-04 (mapping key `pgs:{playerId}`, `gamecenter:{teamPlayerId}`), IDN-05
(link cần phiên hợp lệ + chứng minh sở hữu; race giải bằng insert mapping trong transaction), IDN-06 (409
`IDENTITY_CONFLICT`, không chuyển quyền/gộp), IDN-11 (login provider trả đúng player đã map kể cả sau cài lại; thiết bị
đang có phiên của player khác → `SWITCH_ACCOUNT`, không merge), SDK login/link provider. Nghiệm thu ACC-01 (provider),
ACC-03, ACC-29. Provider thật kiểm thủ công; test tự động dùng verifier giả và chữ ký Game Center tự tạo."

## Scope note

- P0 đã có: mapping `identities/guest:{localId}` (insert-or-verify), `profile.identities[]`, login guest + single session
  takeover, mã `IDENTITY_CONFLICT`/`SWITCH_ACCOUNT` trong catalog, SDK phát `AccountBlocked` cho `SWITCH_ACCOUNT`.
- Slice này thêm: xác minh PGS v2 và Game Center; login bằng provider; link provider vào player hiện tại; conflict và
  chuyển tài khoản; adapter cho provider của game; SDK `LoginWithProviderAsync`, `LinkProviderAsync`.
- Không gồm: unlink, gộp tài khoản, recovery thủ công (TBD-04c), provider khác ngoài adapter mẫu (mục 2.5).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Liên kết tài khoản game với Play Games / Game Center (Priority: P1)

Player đang chơi bằng guest liên kết tài khoản Play Games (Android) hoặc Game Center (iOS) để giữ tiến trình. Server tự
xác minh với Google/Apple, không tin id do client gửi.

**Acceptance Scenarios**:

1. **Given** player guest có phiên hợp lệ, **When** link bằng chứng minh PGS/Game Center hợp lệ, **Then** mapping
   `pgs:{id}`/`gamecenter:{teamPlayerId}` trỏ về player, `profile.identities` có key, có receipt (ACC-01).
2. **Given** identity đã thuộc player khác, **When** link, **Then** 409 `IDENTITY_CONFLICT` kèm provider; không đổi gì ở
   cả hai player (ACC-03).
3. **Given** hai player link cùng identity cùng lúc trên hai instance, **Then** đúng một bên thành công, bên kia 409
   (ACC-03).
4. **Given** chứng minh sai (chữ ký sai, hết hạn, bundle khác, URL khoá công khai không thuộc Apple, mã PGS không đổi
   được), **Then** 401 `IDENTITY_PROOF_INVALID`, không ghi gì.
5. **Given** player đã link chính identity đó, **When** link lại, **Then** OK (`alreadyLinked`), không thay đổi.
6. **Given** player đã có một identity PGS khác, **When** link PGS thứ hai, **Then** `PROVIDER_ALREADY_LINKED`.
7. **Given** response bị mất, **When** SDK gửi lại cùng request, **Then** replay kết quả đã commit (không xác minh lại mã
   PGS dùng một lần).

### User Story 2 - Đăng nhập bằng provider sau khi cài lại (Priority: P1)

Player cài lại app (mất guest secret) đăng nhập Play Games/Game Center và nhận đúng player cũ.

**Acceptance Scenarios**:

1. **Given** identity đã link với player P, **When** login provider trên thiết bị mới, **Then** token mới của P, snapshot
   của P, phiên cũ của P bị thay (single session) (ACC-29).
2. **Given** identity chưa map, **When** login provider không có phiên, **Then** tạo player mới với mapping đó.
3. **Given** player P bị ban login / đã xoá, **When** login provider, **Then** `ACCOUNT_SUSPENDED` / `ACCOUNT_DELETED`.

### User Story 3 - Chuyển tài khoản, không gộp (Priority: P1)

Thiết bị đang có phiên guest của player A; người chơi đăng nhập provider thuộc player B. Server không gộp; client hỏi
người chơi có chuyển sang B không.

**Acceptance Scenarios**:

1. **Given** phiên của A (gửi kèm), identity map tới B, **When** login provider không xác nhận chuyển, **Then** 409
   `SWITCH_ACCOUNT`, phiên A vẫn sống, B không đổi (ACC-29).
2. **Given** như trên, **When** login provider với xác nhận chuyển, **Then** đăng nhập vào B; A giữ nguyên dữ liệu.
3. **Given** phiên của A, identity chưa map, **When** login provider không xác nhận, **Then** `SWITCH_ACCOUNT` (tài khoản
   mới); client nên dùng link nếu muốn giữ tiến trình A.
4. **Given** phiên gửi kèm là của chính player được map, **Then** login bình thường.

### User Story 4 - Game thêm provider riêng (Priority: P3)

Game đăng ký adapter cho provider khác (vd. Facebook) qua `IIdentityProviderAdapter` mà không sửa framework.

**Acceptance Scenarios**:

1. **Given** game đăng ký adapter `custom` provider `acme`, **When** client login/link với credential `custom{provider:
   acme}`, **Then** framework dùng adapter đó, mapping `acme:{subject}`.
2. **Given** hai adapter cùng tên provider, **Then** startup fail.

### Edge Cases

- Google/Apple không trả lời hoặc 5xx → 503 `PROVIDER_UNAVAILABLE` (retryable), không ghi gì.
- Provider chưa bật trong cấu hình → 400.
- Game Center: `timestamp` lệch > 10 phút, chứng chỉ hết hạn, không chuỗi tới root đã pin, subject không phải Apple →
  `IDENTITY_PROOF_INVALID`; chứng chỉ được cache ≤ 24 h theo URL.
- Phiên gửi kèm login provider không hợp lệ/hết hạn → bỏ qua (coi như không có phiên).
- Login provider lần đầu đua nhau trên hai instance → một mapping thắng, cả hai trả cùng player.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** (IDN-03): `IIdentityProviderAdapter` xác minh credential → identity đã xác minh `{provider, subject,
  attributes}`; adapter đăng ký tường minh, trùng provider → startup fail. Built-in: `pgs`, `gamecenter`; adapter giả
  `fake` chỉ Development/Test.
- **FR-002** (IDN-03, V18): PGS v2: đổi server auth code tại Google OAuth (client id/secret từ secret provider), gọi
  `games/v1/players/me` lấy `playerId`; `gamePlayerId`, `originalPlayerId` lưu trong attributes.
- **FR-003** (IDN-03, V19): Game Center: `publicKeyURL` https trên host Apple được phép (mặc định `static.gc.apple.com`,
  path `/public-key/`, không query, không theo redirect); `gamePlayerID` không được ký → lưu là chưa xác minh; chứng chỉ tải về (≤ 16 KiB, cache ≤ 24 h)
  phải chuỗi tới root đã pin, subject Apple Inc., EKU code signing, còn hạn; `|now − timestamp| ≤ 10 phút`;
  `bundleId` thuộc cấu hình; RSA-SHA256 PKCS#1 trên `teamPlayerID ‖ bundleId ‖ timestamp(uint64 BE) ‖ salt`.
- **FR-004** (IDN-04): Mapping key `{provider}:{subject}`; subject 1–128 ký tự in được, không khoảng trắng.
- **FR-005** (IDN-05, IDN-06): Player command `identity.link`: một transaction {insert mapping, `profile.identities`,
  receipt}; mapping của player khác → 409 `IDENTITY_CONFLICT` details `provider`; đã link → OK `alreadyLinked`; đã có
  identity khác cùng provider → `PROVIDER_ALREADY_LINKED` (business). Retry đã commit → replay, không xác minh lại.
- **FR-006** (IDN-11): `POST /api/v1/identity/login/provider` (Public, gated): xác minh → mapping (chưa có → tạo player,
  insert mapping trong transaction; thua race → player thắng) → account checks → epoch +1 → phiên mới → snapshot.
- **FR-007** (IDN-11): Login provider kèm `Authorization` của phiên hợp lệ thuộc player khác player kết quả (hoặc tài khoản
  mới) và không có `switch_account = true` → 409 `SWITCH_ACCOUNT` details `provider`, `account` (`existing`|`new`).
- **FR-008** (SEC-13, PERF-03): Login và link provider dùng chung giới hạn lỗi theo IP với guest; chỉ lỗi do proof của
  client tính là lỗi (lỗi cấu hình server / pin → `PROVIDER_UNAVAILABLE`, log Error). Link giới hạn theo player; mỗi lần
  xác minh có deadline < timeout request và bulkhead theo instance.
- **FR-009** (SDK-02): SDK `LoginWithProviderAsync(credential, switchAccount)` (không retry khi mơ hồ, như guest) và
  `LinkProviderAsync(credential)` (command, retry cùng bytes); `SWITCH_ACCOUNT` giữ phiên hiện tại.
- **FR-010**: Mã lỗi identity: `PROVIDER_ALREADY_LINKED` (14002, 200), `IDENTITY_PROOF_INVALID` (14003, 401),
  `PROVIDER_UNAVAILABLE` (14004, 503, retryable). PCR identity/platform/unity-sdk cập nhật.

### Key Entities

- **Identity mapping** `identities/{provider}:{subject}`: player, thời điểm link, attributes của provider.
- **Verified identity**: provider, subject, attributes (vd. `gamePlayerId`).

## Success Criteria *(mandatory)*

- **SC-001**: 100% lần link đồng thời cùng identity từ hai player trên hai instance cho đúng một bên thành công (ACC-03).
- **SC-002**: Login provider sau cài lại trả đúng player đã link ở 100% test (ACC-29).
- **SC-003**: 0 trường hợp gộp/chuyển dữ liệu giữa player khi conflict hoặc switch.
- **SC-004**: 100% chứng minh giả mạo trong bộ test (chữ ký, thời gian, host, chuỗi chứng chỉ, bundle) bị từ chối.

## Assumptions

- Root/intermediate của chứng chỉ Game Center hiện tại (DigiCert Trusted Root G4 → DigiCert Trusted G4 Code Signing
  RSA4096 SHA384 2021 CA1) được pin mặc định, cấu hình đè được; không kiểm thu hồi (root đã pin, cache ≤ 24 h).
- Mã PGS chỉ dùng một lần: sau `SWITCH_ACCOUNT` client lấy mã mới để xác nhận chuyển.
- Login/link với provider thật (ACC-03, ACC-29 trên thiết bị) kiểm thủ công trước release; CI dùng verifier giả.
