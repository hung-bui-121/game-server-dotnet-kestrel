# Quickstart — 007 gm-security

## Test tự động (mọi máy)

```powershell
& "$HOME\.dotnet10\dotnet.exe" test --project tests/GameServer.Gm.Tests -c Release
```

| Kịch bản | Test chứng minh | Kỳ vọng |
| --- | --- | --- |
| SSO với IdP giả: staff được cấp phép / chưa cấp / lời mời email / thiếu MFA / state sai | `SsoTests` | phiên chỉ cho staff cấp phép; lời mời gắn một lần; thiếu `amr` → từ chối |
| CSRF thiếu/sai/cross-site; GET logout | `CsrfTests` | 403 `CSRF_FAILED`; 405 |
| Cookie chép lại sau logout; cookie sửa byte | `SessionTests` | 401 |
| Khoá staff / bỏ permission / thu hồi phiên trên 2 instance | `RevocationTests` (2 `TestHost`, `FakeClock` +61 s) | 401/403 trên cả hai ≤ 60 s (ACC-11) |
| Cookie phát hành ở A dùng ở B; rotate key | `KeyRingTests` | hợp lệ trước/sau rotate (ACC-49); key trong store không ở dạng rõ |
| CORS: origin trong/ngoài allowlist; cấu hình `*` | `CorsTests` | chỉ origin cấu hình có header; `*` → startup fail |
| Quản lý staff: leo thang, admin cuối, audit, replay | `StaffAdminTests` | 403/409; đúng 1 audit/thay đổi |
| OpenAPI đủ endpoint GM | `OpenApiTests` | khớp `docs/api/gm-openapi.json` |

## Integration Couchbase (máy có Couchbase)

```powershell
$env:COUCHBASE_CONNSTR="couchbase://localhost"; $env:COUCHBASE_USERNAME="Administrator"
$env:COUCHBASE_PASSWORD=[Environment]::GetEnvironmentVariable('COUCHBASE_PW','User'); $env:COUCHBASE_BUCKET="gameserver-it"
& "$HOME\.dotnet10\dotnet.exe" test --project tests/GameServer.Platform.Couchbase.IntegrationTests -c Release
```

Kỳ vọng: key ring lưu/đọc/CAS trên Couchbase; staff + audit commit cùng transaction.

## Hai instance thật (ACC-49 thủ công)

1. `./tools/local/start-hosts.ps1 -Count 2` (Development, local login).
2. Lấy CSRF token rồi đăng nhập ở `http://localhost:5080/gm/v1/auth/login`; gọi `GET http://localhost:5081/gm/v1/me`
   bằng cùng cookie → 200.
3. `POST /gm/v1/ops/dataprotection/rotate`; sau 10 phút, cookie cũ vẫn 200 ở cả hai instance.

## SSO thật (thủ công, P0.5)

Đăng ký app OIDC ở IdP công ty (redirect `https://<host>/gm/v1/auth/sso/callback`), đặt `Gm:Oidc:*` và
`Gm:Oidc:ClientSecret` qua secret provider, `Gm:Oidc:BootstrapManagers` = email admin đầu tiên; đăng nhập với MFA.
