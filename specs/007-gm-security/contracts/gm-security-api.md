# Contract — GM security API (007)

JSON; lỗi = Problem Details có `code` (API-11). "CSRF" = bắt buộc header `X-CSRF-Token` hợp lệ + `Sec-Fetch-Site` ≠
`cross-site`, sai → 403 `CSRF_FAILED`. Mọi response GM có `Cache-Control: no-store`.

## Xác thực

| Route | Profile | CSRF | Ghi chú |
| --- | --- | --- | --- |
| `GET /gm/v1/auth/csrf` | Public | — | `{token}`; đặt cookie `gs_gm_csrf`. Lấy lại sau login/logout (token gắn danh tính) |
| `GET /gm/v1/auth/sso/start?returnUrl=` | Public | — | 302 tới IdP (code + PKCE). `returnUrl` chỉ nhận đường dẫn tương đối `/gm/...` |
| `GET /gm/v1/auth/sso/callback` | (handler OIDC) | state/nonce | thành công → cookie `gs_gm` + 302 `returnUrl`; từ chối → 302 `/gm/?error=sso_denied` |
| `POST /gm/v1/auth/login` `{staffId, password}` | Public, **chỉ Development/Test** | CSRF | như slice 004; môi trường khác → 404 |
| `POST /gm/v1/auth/logout` | `gm.access` | CSRF | 204, xoá ticket + cookie. `GET` → 405 |
| `GET /gm/v1/me` | `gm.access` | — | `{staffId, displayName, gameId, environmentId, permissions[]}` |

## Quản lý staff (`X-Request-Id` UUID bắt buộc cho mutation; `reason` 1–500 ký tự)

| Route | Permission | CSRF | Body / Response |
| --- | --- | --- | --- |
| `GET /gm/v1/staff?pageSize=&cursor=` | `staff.read` | — | `{items[StaffView], nextCursor}` (GM-06) |
| `GET /gm/v1/staff/{staffId}` | `staff.read` | — | `StaffView`; không có → 404 |
| `GET /gm/v1/staff/{staffId}/audit?pageSize=&cursor=` | `staff.read` | — | audit `gm_staff_audit` mới nhất trước |
| `POST /gm/v1/staff` | `staff.manage` | CSRF | `{staffId, displayName, email?, issuer?, subject?, roles[], permissions[], reason}` → 201 `StaffView`; trùng → 409 `STAFF_EXISTS` |
| `PUT /gm/v1/staff/{staffId}/grant` | `staff.manage` | CSRF | `{roles[], permissions[], reason}` → thay grant của game/env này |
| `POST /gm/v1/staff/{staffId}/disable` | `staff.manage` | CSRF | `{reason}`; `sessionEpoch++` |
| `POST /gm/v1/staff/{staffId}/enable` | `staff.manage` | CSRF | `{reason}` |
| `POST /gm/v1/staff/{staffId}/sessions/revoke` | `staff.manage` (hoặc chính mình với `gm.access`) | CSRF | `{reason}`; `sessionEpoch++` |
| `POST /gm/v1/ops/dataprotection/rotate` | `ops.keys.rotate` | CSRF | `{reason}` → `{keyId, activatesAtUtc}` |

`StaffView` = `{staffId, displayName, email?, status, disabled, oidcBound, permissions[] (game/env này), updatedAtUtc}` —
không trả `passwordHash`, `sessionEpoch`, subject đầy đủ (chỉ `oidcBound: true/false`).

Lỗi nghiệp vụ: 403 `PERMISSION_NOT_GRANTABLE`, 409 `LAST_STAFF_MANAGER`, 400 `BAD_REQUEST` (permission ngoài
catalog, role lạ, thiếu reason), 409 `IDEMPOTENCY_PAYLOAD_MISMATCH`. Retry cùng `X-Request-Id` + cùng body → trạng thái
hiện tại với `replayed: true`.

## Cookie

| Cookie | Nội dung | Thuộc tính |
| --- | --- | --- |
| `gs_gm` | session id đã `Protect` (Data Protection) | `HttpOnly`, `Secure` (ngoài Development), `SameSite=Lax` (`None` khi CORS bật), `Path=/gm` |
| `gs_gm_csrf` | antiforgery cookie token | như trên |
| cookie correlation/nonce của OIDC | do handler tạo, bảo vệ bằng key ring | `Path=/gm/v1/auth/sso/callback` |

## OpenAPI

`GET /gm/v1/openapi.json` (Development/Test). Mỗi operation có `x-gm-permission` (string hoặc null cho Public) và
`x-gm-csrf` (bool). Bản commit: `docs/api/gm-openapi.json`.
