# Tasks: GM security hardening (P0.5 slice 007)

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/gm-security-api.md](contracts/gm-security-api.md)

**Tests**: bắt buộc (constitution VI: mỗi ACC của slice có test). Tests viết cùng phase với code của story.

## Phase 1: Setup

- [X] T001 Thêm `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.14 và `Microsoft.AspNetCore.OpenApi` 10.0.14 vào `Directory.Packages.props`; tham chiếu OIDC ở `src/GameServer.Modules.Gm.Adapters/GameServer.Modules.Gm.Adapters.csproj`, OpenApi ở `src/GameServer.Platform.Web/GameServer.Platform.Web.csproj`
- [X] T002 [P] Mã lỗi `CSRF_FAILED` (403) trong `src/GameServer.Core/Errors/CoreErrorCodes.cs`; mã `STAFF_EXISTS` (409), `LAST_STAFF_MANAGER` (409), `PERMISSION_NOT_GRANTABLE` (403) trong `src/GameServer.Modules.Gm/GmErrorCodes.cs` đăng ký qua descriptor `gm`

## Phase 2: Foundational (chặn mọi story)

- [X] T003 `KeyValueCollections.EnsureWritable` từ chối `gm_staff`, `gm_staff_identities` (chỉ transaction — TST-04 V15-T2) trong `src/GameServer.Platform/Persistence/KeyValueStore.cs`; test trong `tests/GameServer.Platform.Tests`
- [X] T004 `StaffRecord` thêm `Email`, `Status` (`invited|active`), `SessionEpoch`, `Oidc {Issuer, Subject}`, `UpdatedAtUtc` (data-model) trong `src/GameServer.Modules.Gm/StaffRecord.cs`; permission mới `staff.read`, `staff.manage`, `ops.keys.rotate` trong `GmPermissions.cs`
- [X] T005 `StaffDirectory` chỉ đọc (cache ≤ 60 s) + `Invalidate`; mọi ghi chuyển sang `StaffAdminService` (T020) trong `src/GameServer.Modules.Gm.Adapters/StaffDirectory.cs`
- [X] T006 Shared Data Protection ở `src/GameServer.Platform.Web/Security/`: `KeyRingRepository` (`IXmlRepository`: snapshot RAM + hàng chờ ghi, document `dataprotection/keyring` Insert/Replace CAS ≤ 3 lần), `KeyRingService` (`IHostedLifecycleService`: `StartingAsync` tải/tạo key đầu tiên bằng Insert, làm mới mỗi `RefreshMinutes`, tạo key kế tiếp trước `ActivationLeadHours`, `RotateAsync`), `MasterKeyXmlEncryptor`/`MasterKeyXmlDecryptor` (AES-256-GCM, `masterKeyId`), `DataProtectionSettings` + validation (master key bắt buộc ngoài Dev/Test, base64 32 byte; lead ≥ 2 × refresh); `AddSharedDataProtection` gọi trong `AddGameServer` (`SetApplicationName("gameserver:{gameId}:{env}")`, `DisableAutomaticKeyGeneration`, `SetDefaultKeyLifetime`); collection `dataprotection` khai bởi platform (R6, R7)
- [X] T007 [P] Không sync-over-async: arch test cho namespace `GameServer.Platform.Web.Security` (không `GetAwaiter().GetResult()`/`.Result`) trong `tests/GameServer.Architecture.Tests` — thực hiện bằng BannedApiAnalyzers (cấm `.Result`/`.Wait`/`GetResult` ở compile time cho mọi framework code, constitution IV); thiết kế snapshot + hosted service (R6)

## Phase 3: User Story 2 — Phiên chống đánh cắp + CSRF (P1) 🎯

- [X] T008 [US2] `GmSessionService`: cookie = `Protect(sessionId)` purpose `GameServer.Gm.Session.v1`; `Unprotect` lỗi → null; ticket thêm `epoch`, `lastActiveAtUtc`; idle `Gm:IdleMinutes` (5–480, mặc định 60) với touch lười > 5 phút (Replace CAS, lỗi bỏ qua) trong `src/GameServer.Modules.Gm.Adapters/GmSessionService.cs`, `GmOptions.cs`
- [X] T009 [US2] `GmCookieAuthenticationHandler`: kiểm `epoch == staff.SessionEpoch`, staff `active` và không `disabled`; claim `NameIdentifier = "gmsession:" + sessionKey` (antiforgery gắn theo phiên) trong `GmCookieAuthenticationHandler.cs`
- [X] T010 [US2] CSRF platform: `GmCsrf` (antiforgery `HeaderName = X-CSRF-Token`, cookie `gs_gm_csrf` `HttpOnly`/`Path=/gm`/Secure/SameSite như `gs_gm`), metadata `CsrfRequiredMetadata` + extension `RequireCsrf()`; `ActorSchemeOptions.CookieSchemes` (scheme cookie cần CSRF — gm đăng ký `GmCookie`); `ProfileEnforcementMiddleware`: sau xác thực, method unsafe và (scheme cookie hoặc endpoint `RequireCsrf`) → `IAntiforgery.ValidateRequestAsync`, lỗi → 403 `CSRF_FAILED`; log bảo mật (không log token) trong `src/GameServer.Platform.Web/Security/GmCsrf.cs`, `Middleware/ProfileEnforcementMiddleware.cs`, `Auth/ActorSchemeOptions.cs`
- [X] T011 [US2] `EndpointSecurityValidator`: endpoint Public có route `/gm/` và method unsafe phải có `CsrfRequiredMetadata` → startup fail (deny-by-default) trong `Validation/EndpointSecurityValidator.cs`
- [X] T012 [US2] Endpoint `GET /gm/v1/auth/csrf` (Public; xác thực `GmCookie` tuỳ chọn trước khi `GetAndStoreTokens` → `{token}`); local login `RequireCsrf()`; logout chỉ POST (xoá ticket + cookie, log bảo mật); cookie options dùng chung `GmCookies` (SameSite None khi CORS bật) trong `GmModule.cs`
- [X] T013 [US2] Trang tĩnh P0 `ui/index.html`: lấy CSRF token trước login/mutation và sau login (sửa tối thiểu, FR-020)
- [X] T014 [US2] Test `tests/GameServer.Gm.Tests/CsrfTests.cs` + `SessionTests.cs`: mutation cookie thiếu/sai token → 403 `CSRF_FAILED`; token đúng → OK; cross-site + token đúng → 403; GET logout → 405; cookie chép lại sau logout → 401; cookie sửa byte → 401; idle quá hạn → 401; test-auth header không cần CSRF; helper `GmTestHost.GmClientAsync` lấy token và gửi header

## Phase 4: User Story 3 — Thu hồi ≤ 60 s trên mọi instance (P1)

- [X] T015 [US3] Test `RevocationTests.cs` (2 `TestHost` chung store + `FakeClock`): khoá staff, bỏ `resource.adjust`, thu hồi mọi phiên → sau ≤ 60 s (clock +61 s) 401/403 trên cả hai instance; trước TTL instance xử lý thay đổi đã áp dụng ngay (ACC-11, SEC-10)

## Phase 5: User Story 4 — Key ring dùng chung + rotate (P1)

- [X] T016 [US4] Endpoint `POST /gm/v1/ops/dataprotection/rotate` (`ops.keys.rotate`, CSRF, `reason`, `X-Request-Id`) → `KeyRingService.RotateAsync` (kích hoạt sau 2 × refresh) + audit `ops.keys.rotate` qua `StaffAdminService`/audit writer trong `GmModule.cs`
- [X] T017 [US4] Test `KeyRingTests.cs`: cookie phát hành ở host A dùng ở host B (chung store); rotate rồi tiến clock qua thời điểm kích hoạt + refresh → cookie cũ vẫn hợp lệ, cookie mới dùng key mới; document key ring không chứa key rõ khi có master key; thiếu master key ở Production → startup fail; hai host khởi động cùng lúc chỉ tạo một key đầu tiên (ACC-49, OPS-04)
- [X] T018 [P] [US4] Integration Couchbase `tests/GameServer.Platform.Couchbase.IntegrationTests/KeyRingIntegrationTests.cs`: key ring Insert/Replace CAS, hai repository cùng bucket thấy key của nhau

## Phase 6: User Story 5 — Quản lý staff có audit (P2)

- [X] T019 [US5] `StaffAuditWriter`: entry `gm_staff_audit` (data-model) `entryId = sha256(actor|op|target|requestId)`, `bodyHash`; index `ix_gm_audit_staff (targetStaffId, committedAtUtc DESC, META().id DESC)` khai trong descriptor `gm` trong `src/GameServer.Modules.Gm.Adapters/Staff/StaffAuditWriter.cs`
- [X] T020 [US5] `StaffAdminService`: create (email mời → Insert `email:`; issuer+subject → Insert `oidc:`), set grant, disable/enable (`epoch++` khi disable), revoke sessions (`epoch++`), bind (SSO), bootstrap — mỗi thao tác một `RunTransactionAsync` {staff, identities, audit}; idempotent theo `X-Request-Id` (audit tồn tại → replay; `bodyHash` khác → `IDEMPOTENCY_PAYLOAD_MISMATCH`); chống leo thang (`PERMISSION_NOT_GRANTABLE`), không để 0 staff hoạt động có `staff.manage` (`LAST_STAFF_MANAGER`), permission/role ngoài catalog → 400; invalidate cache local trong `Staff/StaffAdminService.cs`
- [X] T021 [US5] Endpoint `/gm/v1/staff/**` theo contract (list/get/audit phân trang GM-06; create/grant/disable/enable/sessions revoke với `reason` + `X-Request-Id`); `StaffView` không lộ hash/epoch/subject trong `Staff/StaffEndpoints.cs`
- [X] T022 [US5] `GmBootstrapper` → `IHostedLifecycleService.StartingAsync`: seed Dev qua `StaffAdminService` (idempotent: staff có rồi → cập nhật grant/password như cũ); `Gm:Oidc:BootstrapManagers` khi 0 staff hoạt động có `staff.manage` (R11) trong `GmBootstrapper.cs`, `GmOptions.cs`
- [X] T023 [US5] Test `StaffAdminTests.cs`: tạo Support → grant expand + 1 audit đủ 7 trường; cấp quyền mình không có → 403; khoá/hạ staff.manage cuối → 409; `staff.read` sửa → 403; permission lạ → 400; replay cùng id → `replayed`, khác body → 409; email trùng → 409

## Phase 7: User Story 1 — SSO OIDC + cấp phép tường minh (P1)

- [X] T024 [US1] `GmOidcOptions` (`Authority`, `ClientId`, `ClientSecret`, `Scopes`, `RequireMfa` mặc định true ngoài Dev/Test, `MfaAmrValues`, `BootstrapManagers`) + validation: ngoài Dev/Test thiếu Authority/ClientId/ClientSecret → startup fail; `RequireMfa=false` ngoài Dev → log Warning trong `GmOptions.cs`
- [X] T025 [US1] `GmOidcSetup`: scheme `GmOidc` (code + PKCE, `ResponseMode=query`, `CallbackPath=/gm/v1/auth/sso/callback`, `SignInScheme=GmCookie`, cookie correlation/nonce `Path` callback, Secure ngoài Dev); `OnTicketReceived` → `StaffIdentityMatcher` + MFA (`amr` ∩ `MfaAmrValues`) → tạo phiên, đặt cookie, `HandleResponse`, redirect `returnUrl` hợp lệ; từ chối/`OnRemoteFailure` → 302 `/gm/?error=sso_denied` + log bảo mật trong `Sso/GmOidcSetup.cs`
- [X] T026 [US1] `StaffIdentityMatcher`: `oidc:{hash}` → staff; không có + `email_verified` + `email:` → `StaffAdminService.BindAsync` (transaction, Insert `oidc:` một lần; thua race → đọc lại); staff disabled/khác danh tính → từ chối trong `Sso/StaffIdentityMatcher.cs`
- [X] T027 [US1] Endpoint `GET /gm/v1/auth/sso/start?returnUrl=` (Public, `Challenge`) và `GET /gm/v1/auth/sso/callback` (Public, gọi `IAuthenticationRequestHandler.HandleRequestAsync` của `GmOidc`); chỉ map khi OIDC cấu hình trong `GmModule.cs`
- [X] T028 [US1] Test IdP giả `tests/GameServer.Gm.Tests/Infrastructure/FakeOidcProvider.cs` (discovery, JWKS RSA, authorize → code, token → id_token RS256 tuỳ claim) + `SsoTests.cs`: staff gắn danh tính → phiên đúng permission; chưa cấp phép → `sso_denied`, không cookie; lời mời email đã xác minh gắn một lần rồi khớp theo subject; email chưa xác minh → từ chối; thiếu `amr` khi `RequireMfa` → từ chối; state sai → từ chối; `returnUrl` ngoài `/gm/` → `/gm/`; local login ở Production → 404 (ACC-10, SEC-03)

## Phase 8: CORS (SEC-06) + User Story 6 contract

- [X] T029 [P] `GmCors`: `Gm:Cors:AllowedOrigins` → policy `gm` (exact origin, credentials, header `X-CSRF-Token`/`X-Request-Id`/`Content-Type`) áp cho route `/gm`; validation (`*`, path/query, không absolute, `http` ngoài Development → startup fail); bật CORS → cookie `SameSite=None; Secure` trong `src/GameServer.Platform.Web/Security/GmCors.cs`
- [X] T030 [P] Test `CorsTests.cs`: origin trong allowlist có `Access-Control-Allow-Origin` + credentials; origin lạ không có header và mutation bị từ chối; `*` → startup fail; mặc định không có header CORS (ACC-11)
- [X] T031 [US6] OpenAPI: document `gm` (route `/gm/**`), transformer `x-gm-permission`, `x-gm-csrf`, cookie security scheme; `GET /gm/v1/openapi.json` (Public, chỉ Dev/Test); `Accepts`/`Produces` cho endpoint GM trong `src/GameServer.Platform.Web/OpenApi/GmOpenApi.cs` và các module có endpoint GM
- [X] T032 [US6] Test `OpenApiTests.cs`: mọi endpoint GM có trong document với permission/CSRF đúng; so khớp `docs/api/gm-openapi.json` (`UPDATE_OPENAPI=1` ghi lại)

## Phase 9: Polish

- [X] T033 [P] PCR `docs/pcr/gm.md` (v0.7.0: SSO, phiên, CSRF, CORS, staff API, mã lỗi, cấu hình) và `docs/pcr/platform.md` (Data Protection key ring, cấu hình, OpenAPI); `samples/SampleHost/appsettings.json` không chứa secret
- [X] T034 Chạy toàn bộ test (kể cả Couchbase integration) + quickstart thủ công hai instance; cập nhật `AgentMemories/features/p0-speckit/00_summary.md`
- [X] T035 tech-lead-reviewer review; sửa Blocker/Major; commit

## Dependencies

- Phase 1 → Phase 2 → (US2) → US3, US4 → US5 → US1 (SSO dùng `StaffAdminService.BindAsync` và phiên US2) → CORS/OpenAPI → Polish.
- T018, T029, T030 song song được với các task khác file.

## Implementation Strategy

MVP = US2 (phiên + CSRF) vì mọi story khác dựa vào phiên an toàn; sau đó thu hồi, key ring, staff, SSO.
