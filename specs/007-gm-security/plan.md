# Implementation Plan: GM security hardening (P0.5 slice 007)

**Branch**: `007-gm-security` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/007-gm-security/spec.md`

## Summary

Hardening GM cho Production: SSO OIDC (code + PKCE, MFA qua `amr`) với cấp phép tường minh và lời mời email gắn danh
tính một lần; cookie `gs_gm` chỉ mang session id đã bảo vệ bằng Data Protection, ticket server-side có epoch + idle;
antiforgery `X-CSRF-Token` cho mọi mutation GM; CORS allowlist tuỳ chọn; thu hồi ≤ 60 s qua `sessionEpoch` + TTL cache;
key ring Data Protection dùng chung trên Couchbase (một document, CAS), mã hoá AES-256-GCM bằng master key, tạo key
trước 48 h không sync-over-async; quản lý staff qua GM API trong transaction có audit; OpenAPI cho GM API (frontend do
dev khác làm — owner 2026-10-27). Chi tiết quyết định: [research.md](research.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403, `net10.0`)

**Primary Dependencies**: ASP.NET Core Minimal API (Kestrel); `Microsoft.AspNetCore.Authentication.OpenIdConnect`
10.0.14 (mới); `Microsoft.AspNetCore.OpenApi` 10.0.14 (mới); Data Protection + Antiforgery + CORS (shared framework);
CouchbaseNetClient 3.10.0 (pin)

**Storage**: Couchbase (`gm_staff`, `gm_staff_identities` mới, `gm_sessions`, `gm_audit`, `dataprotection` mới); store
in-memory cho unit test

**Testing**: xUnit v3 + Shouldly; `TestHost` (TestServer) hai instance chung store; IdP OIDC giả lập; `FakeClock`;
integration Couchbase khi có `COUCHBASE_CONNSTR`; arch test

**Target Platform**: Linux server (baseline), Windows dev

**Project Type**: web-service framework (module GM + platform)

**Performance Goals**: thêm ≤ 1 KV read/request GM khi cache trúng 0 (ticket + staff cache 60 s); key ring không có I/O
trên đường request

**Constraints**: thu hồi ≤ 60 s (SEC-10, TBD-05b); không sync-over-async (PERF-01); secret chỉ từ secret provider
(OPS-08); không đổi contract Player API

**Scale/Scope**: vài chục staff/deployment; 1 key / 90 ngày

## Constitution Check

| Nguyên tắc | Đáp ứng | Kết quả |
| --- | --- | --- |
| I Server authoritative, một transaction | Staff + audit trong một transaction; không ghi `pa_*` | PASS |
| II Module đăng ký tường minh | Permission/collection/index mới khai trong descriptor `gm`; collection `dataprotection` khai bởi platform | PASS |
| III Security deny-by-default | Endpoint mới có profile; startup validator mới: endpoint GM unsafe thiếu CSRF → fail; permission + scope kiểm ở backend; audit staff | PASS với ngoại lệ ở Complexity Tracking (staff không đi pipeline command player) |
| IV Async, tải có giới hạn | Key ring snapshot + hosted service async (không sync-over-async); cache TTL có số; body limit GM giữ 1 MiB | PASS |
| V Contract ổn định | Mã lỗi mới vào catalog; PCR gm + platform; OpenAPI GM commit | PASS |
| VI Kiểm thử | Test cho ACC-10/11/49 + test integration Couchbase cho key ring/staff | PASS |
| VII Observability | Log bảo mật có cấu trúc (login/CSRF/logout/thu hồi), không log token/cookie/secret | PASS |

Re-check sau Phase 1: không đổi.

## Project Structure

### Documentation (this feature)

```text
specs/007-gm-security/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/gm-security-api.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code (repository root)

```text
src/GameServer.Core/Errors/CoreErrorCodes.cs                     # CSRF_FAILED (+ mã staff ở module gm)
src/GameServer.Platform/Persistence/KeyValueStore.cs             # gm_staff, gm_staff_identities: chỉ transaction
src/GameServer.Platform.Web/Security/
├── SharedDataProtection.cs          # AddSharedDataProtection: app name, encryptor, repository, options, validation
├── KeyRingRepository.cs             # IXmlRepository snapshot + pending writes (dataprotection/keyring)
├── KeyRingService.cs                # IHostedLifecycleService: load, create initial/next key, refresh, rotate
├── MasterKeyXmlEncryptor.cs         # AES-256-GCM IXmlEncryptor / IXmlDecryptor
├── GmCsrf.cs                        # antiforgery options + endpoint filter + metadata
└── GmCors.cs                        # allowlist policy + validation
src/GameServer.Platform.Web/Validation/EndpointSecurityValidator.cs   # GM unsafe endpoint thiếu CSRF → fail
src/GameServer.Platform.Web/OpenApi/GmOpenApi.cs                      # document "gm" + transformers
src/GameServer.Modules.Gm/                    # StaffRecord (+email, status, epoch, oidc), permissions mới, errors
src/GameServer.Modules.Gm.Adapters/
├── GmSessionService.cs              # protected cookie, epoch, idle
├── GmCookieAuthenticationHandler.cs # epoch check
├── Sso/GmOidcSetup.cs               # OIDC scheme, events (match, MFA, session, redirect)
├── Sso/StaffIdentityMatcher.cs      # oidc:/email: lookup + bind transaction
├── Staff/StaffAdminService.cs       # transaction + audit + idempotency + anti-escalation
├── Staff/StaffEndpoints.cs          # /gm/v1/staff/**
├── GmBootstrapper.cs                # dev seed + BootstrapManagers qua StaffAdminService
├── GmModule.cs                      # csrf, sso, logout POST, ops rotate, OpenAPI endpoint
└── ui/index.html                    # sửa tối thiểu: lấy/gửi CSRF token
docs/api/gm-openapi.json             # OpenAPI GM (commit)
docs/pcr/gm.md, docs/pcr/platform.md # cập nhật
tests/GameServer.Gm.Tests/           # Sso, Csrf, Session, Revocation, KeyRing, Cors, StaffAdmin, OpenApi
tests/GameServer.Gm.Tests/Infrastructure/FakeOidcProvider.cs
tests/GameServer.Platform.Couchbase.IntegrationTests/  # key ring + staff transaction trên Couchbase
tests/GameServer.Architecture.Tests/  # không sync-over-async trong Security/
```

**Structure Decision**: giữ cấu trúc module 2 project (ARC-01). Data Protection, CSRF, CORS, OpenAPI ở Platform.Web
vì dùng chung cho mọi module có endpoint GM; nghiệp vụ staff/SSO ở module `gm`.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| Constitution III "mutation GM đi qua pipeline command": quản lý staff dùng `StaffAdminService` thay vì `ICommandRunner` | Pipeline command chạy trên aggregate player (`pa_*`, receipt theo player); staff không phải player | Biến staff thành "player aggregate" giả làm sai mô hình dữ liệu và quota aggregate. Service riêng giữ đủ bảo đảm của nguyên tắc: một transaction, audit 7 trường cùng transaction, idempotent theo `X-Request-Id` |
