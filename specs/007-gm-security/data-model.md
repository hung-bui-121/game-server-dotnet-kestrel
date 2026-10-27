# Data model — 007 gm-security

Mọi collection nằm trong scope `g_<gameId>` (DAT-07). "TX" = chỉ ghi trong transaction (`EnsureWritable` từ chối KV).

## `gm_staff/{staffId}` (TX — thay đổi so với slice 004)

| Field | Kiểu | Ràng buộc |
| --- | --- | --- |
| `staffId` | string | `^[a-z0-9][a-z0-9._-]{0,63}$`, bất biến |
| `displayName` | string | 1–100 ký tự |
| `email` | string? | lowercase, ≤ 254; bắt buộc khi tạo bằng lời mời |
| `status` | `invited` \| `active` | `invited` cho tới khi gắn danh tính OIDC (Development local → `active`) |
| `disabled` | bool | khoá → `sessionEpoch++` |
| `sessionEpoch` | int64 | ≥ 0; tăng khi khoá hoặc thu hồi mọi phiên |
| `oidc` | `{issuer, subject}`? | gắn một lần (R3) |
| `passwordHash` | string? | chỉ Development/Test (PBKDF2, slice 004) |
| `grants[]` | `{gameId, environmentId, permissions[]}` | permission thuộc catalog khi gán |
| `updatedAtUtc` | string | ISO UTC |

State: `invited` → (SSO lần đầu, email đã xác minh khớp) → `active`; `disabled` bật/tắt bất kỳ lúc nào.

## `gm_staff_identities/{key}` (TX — mới)

| Key | Nội dung | Ràng buộc |
| --- | --- | --- |
| `oidc:{sha256hex(issuer + "\n" + subject)}` | `{staffId, issuer, subject, boundAtUtc}` | Insert một lần; nguồn sự thật khi đăng nhập |
| `email:{lowercase email}` | `{staffId, createdAtUtc}` | Insert khi mời; trùng → `STAFF_EXISTS` |

## `gm_sessions/{sha256hex(sessionId)}` (KV — thay đổi)

`{staffId, gameId, environmentId, epoch, createdAtUtc, lastActiveAtUtc, expiresAtUtc}`; document expiry = `expiresAtUtc`
(tuyệt đối, mặc định 8 h). Hợp lệ khi: game/env khớp, `now < expiresAtUtc`, `now − lastActiveAtUtc < IdleMinutes`
(mặc định 60), `epoch == staff.sessionEpoch`, staff không `disabled`. `lastActiveAtUtc` cập nhật khi > 5 phút (CAS, lỗi bỏ qua).

## `gm_audit/gm_audit::{entryId}` (TX — mở rộng)

Bản audit staff (cùng collection với audit player của slice 004):
`{entryId, kind: "gm_staff_audit", gameId, environmentId, actorId, operation, committedAtUtc, targetStaffId,
payload{actorId, permission, action, targetStaffId, reason, before, after, correlationId, requestId, bodyHash}}`.
`entryId = sha256hex(actorId|operation|targetStaffId|requestId)`. Index mới `(targetStaffId, committedAtUtc DESC, META().id DESC)`.
Operation: `staff.create`, `staff.update`, `staff.disable`, `staff.enable`, `staff.revoke_sessions`, `staff.bind`,
`staff.bootstrap`, `ops.keys.rotate` (target = `system`).

## `dataprotection/keyring` (KV, CAS — mới, platform)

`{keys: [string (XML element)], rev: int64, updatedAtUtc}`. Mỗi phần tử là XML key của Data Protection, phần khoá bí
mật bọc trong `<encryptedKey masterKeyId nonce tag>` (AES-256-GCM). Ghi bằng Insert (key đầu tiên) hoặc Replace có CAS
(thêm key); xung đột CAS → đọc lại, gộp, ghi lại (≤ 3 lần).

## Cấu hình mới (PCR gm + platform)

| Khoá | Mặc định | Ghi chú |
| --- | --- | --- |
| `Gm:IdleMinutes` | 60 | 5–480 |
| `Gm:Oidc:Authority`, `ClientId` | — | bắt buộc ngoài Dev/Test |
| `Gm:Oidc:ClientSecret` | — | secret provider (OPS-08) |
| `Gm:Oidc:Scopes` | `openid profile email` | |
| `Gm:Oidc:RequireMfa` | `true` ngoài Dev/Test | R2 |
| `Gm:Oidc:MfaAmrValues` | `mfa otp hwk swk sms fpt face` | |
| `Gm:Oidc:BootstrapManagers[]` | — | `{email}` hoặc `{issuer, subject}` (R11) |
| `Gm:Cors:AllowedOrigins[]` | rỗng | R9 |
| `DataProtection:MasterKey` | — | base64 32 byte, secret provider; bắt buộc ngoài Dev/Test |
| `DataProtection:MasterKeyId` | `k1` | |
| `DataProtection:KeyLifetimeDays` | 90 | 7–365 |
| `DataProtection:ActivationLeadHours` | 48 | ≥ 2 × `RefreshMinutes` |
| `DataProtection:RefreshMinutes` | 5 | 1–60 |
