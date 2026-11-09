# Thiết kế GM tool: một URL cho mọi game × môi trường — GM control plane trung tâm (phương án C)

**Ngày:** 2026-11-08 · **Trạng thái:** BẢN SỬA 1 theo trả lời owner 2026-11-08 (bản đầu cùng ngày khuyến nghị A′ — owner chọn
C); cập nhật 2026-11-09 theo trả lời owner (gate email domain, IdP chưa chọn → OIDC chuẩn, một CP + khoá live riêng, Q28–Q32) ·
**THIẾT KẾ ĐÃ CHẤP NHẬN — sẵn sàng spec CP-1** (§14) · **Người viết:** system-architect
**Đầu vào:** yêu cầu owner 2026-11-08: *"tôi muốn một url duy nhất cho tất cả môi trường, việc ai access vào được game nào và môi
trường nào (mỗi game có 4 môi trường: dev, beta, uat, live) sẽ do admin phân quyền"*; trả lời owner cùng ngày (Q22 "C control
plane trung tâm"; Q23 "sso cho mọi staff"; Q24 "member khi thực hiện login (sso) sẽ qua một gate (gate đó kiểm tra domain sau khi
đăng nhập) để biết được mình có những quyền gì trên các project"; Q25 "dùng chung"); `01_design.md`; slice 017/018; code `main`
= `757272c`; `Requirements/Game_Server_System_Requirements_v1.2.md`.

**Owner đã chốt (2026-11-08):**

| # | Quyết định |
| --- | --- |
| Q22 | **C — GM control plane trung tâm**: một service giữ danh bạ staff và grant theo (game, env); nơi duy nhất admin cấp quyền |
| Q23 | SSO cho mọi staff (IdP công ty; IdP cụ thể chưa chọn — xem bảng 2026-11-09). Break-glass: không có (Q28, 2026-11-09) |
| Q24 | Đăng nhập SSO qua **một gate**: sau đăng nhập kiểm **domain email** (chỉ domain công ty) rồi trả về các project (game × env) và quyền của member |
| Q25 | Một origin chung; trang của game ở đường dẫn theo game (GM-4) |

**Owner chốt thêm (2026-11-09):**

| # | Quyết định | Ảnh hưởng thiết kế |
| --- | --- | --- |
| Gate | "Domain" = **domain email công ty** (vd `a@vng.com.vn` được vào, `a@gmail.com` bị từ chối); grant theo game × env do admin cấp **trong control plane**; member mới **0 quyền** | Khớp §3.1 (C7), không đổi |
| IdP | **Chưa chọn** → thiết kế theo **OIDC chuẩn** (discovery, code + PKCE); kiểm MFA **cấu hình được theo IdP** (claim `amr` hoặc `acr`); test local bằng OIDC giả/dev hoặc local login ở Development; chọn IdP = quyết định cấu hình sau | §3.1, §5.1, V-C4, CP-2 sửa theo |
| Deploy CP | **Một instance** cho mọi game/env, **khoá ký live tách riêng** | Khớp §2.1, §2.2 B1 |
| Break-glass (Q28) | **Không có** — fail closed, chờ khôi phục; local login chỉ Local/Dev | §2.3 bỏ runbook chuyển Standalone khẩn cấp |
| Q29 | **Có**: grant **thêm** quyền trên live cần admin thứ hai duyệt, người đề xuất xác thực lại; dev/beta/uat một admin; gỡ quyền/khoá có hiệu lực ngay | §3.2 |
| Q30 | KMS/HSM **chưa có** → v1 khoá ký trong secret configuration, khoá live riêng, sau abstraction `ITokenSigner` có điểm cắm KMS; **chuyển KMS là bắt buộc trước khi live dùng CP** | §2.2 B2, §5.3 — thực thi bằng **luật khởi động** (R-KMS) |
| Q31 | Cấu hình sau: `Cp:AllowedEmailDomains` và **đúng 2** `Cp:BootstrapAdmins`; test local dùng giá trị giả | §3.1, §3.2 — validator |
| Q32 | **Có**, cùng origin: `gm.<domain>/` = console tĩnh, `gm.<domain>/cp/**` định tuyến tới CP; local Vite proxy `/cp` | §4.2, CP-3; bỏ phương án `cp.gm.<domain>` |

**Đã kiểm trong code:**

| # | Sự thật | File |
| --- | --- | --- |
| F1 | Grant đã có scope `StaffGrant {GameId, EnvironmentId, Permissions[]}` | `Modules.Gm/StaffRecord.cs` |
| F2 | Handler cookie dựng principal từ `ActorClaims.ActorType = gm`, `ActorId`, `Permission` (đã expand cho game/env của deployment); authorization chỉ đọc các claim này | `GmCookieAuthenticationHandler.cs:52`, `Platform.Web/Auth/ActorClaims.cs` |
| F3 | Role là template, expand theo catalog của deployment (`GmRoles.Expand`, Administrator = cả catalog) | `Modules.Gm/GmRoles.cs` |
| F4 | Code có sẵn để tái dùng cho phiên của control plane: OIDC + MFA (`RequireMfa`, `MfaAmrValues`), bind identity qua email đã xác minh, cookie `__Host-` + antiforgery, bootstrap manager | `Sso/*`, `Security/*`, `GmOptions.cs`, `GmBootstrapper.cs` |
| F5 | `GameServerOptions.IsProduction` có sẵn; `/me` chưa lộ ra | `GameServerOptions.cs:83`, `GmModule.cs` |
| F6 | Chưa có package JWT nào trong solution | `*.csproj` |

---

## §0. Thiết kế (tóm tắt)

1. **`GameServer.GmControlPlane`** là host mới **trong repo framework** (game-agnostic: game chỉ là dữ liệu cấu hình), deploy
   **một lần cho cả công ty**, chạy trong vùng live. Dữ liệu ở Couchbase của nó (bucket riêng trên cluster live).
2. **Gate** = cửa đăng nhập của control plane (CP): OIDC với IdP công ty → bắt buộc MFA + `email_verified` + domain email trong
   allowlist → tự tạo member **không có quyền** ở lần đăng nhập đầu → trả danh sách project (game × env) member được cấp.
3. Console và CP **cùng một origin** `https://gm.<domain>` (static ở `/`, CP ở `/cp/**` qua path routing) → phiên CP là cookie
   `__Host-gs_cp` cùng origin, tái dùng cơ chế cookie + CSRF của 018.
4. Console xin **access token ngắn hạn theo target**: `POST /cp/v1/tokens {gameId, environmentId}` → JWT ES256, `aud =
   gm:<gameId>:<envId>`, `perm` = quyền đã expand, `sid`, TTL **60 s** (= SEC-10). Token chỉ ở bộ nhớ.
5. Console gọi **thẳng** GM API của deployment với `Authorization: Bearer` (không proxy). API host **không cần cookie** → không
   còn ràng buộc cùng site cho host API; không CSRF cho API; CORS một origin, không credentials.
6. Deployment thêm chế độ `Gm:Mode = ControlPlane`: scheme `GmBearer` xác minh chữ ký bằng **khoá công khai pin trong cấu hình**
   (không gọi CP lúc chạy), `aud` phải bằng game/env hằng của deployment (ARC-10), giao `perm` với catalog của mình → dựng **đúng
   các claim F2** → kiểm quyền per request và audit giữ nguyên tại deployment.
7. **Khoá ký tách theo tầng**: một khoá cho live, một cho non-live; deployment live chỉ pin khoá live. v1 khoá trong secret
   configuration; CP Production **không khởi động** nếu có target live mà khoá live chưa ở KMS (R-KMS, Q30).
8. Thu hồi ≤ 60 s **không cần danh sách thu hồi**: CP kiểm member/grant/phiên ở **mỗi lần cấp token**; token cũ hết hạn ≤ 60 s.
9. CP sập ⇒ không cấp token ⇒ GM mọi env (kể cả live) dừng trong ≤ 60 s — **fail closed**, player không ảnh hưởng. Không có
   break-glass, chờ khôi phục (owner chốt Q28, 2026-11-09).
10. Live: mọi grant **thêm** quyền cần **người thứ hai duyệt** + xác thực lại (step-up); gỡ quyền/khoá member có hiệu lực ngay.
11. Audit: CP ghi mọi thay đổi member/grant/duyệt/phiên; deployment ghi GM action với actor = staff id của CP + `sid` → nối được.
12. Chế độ `Standalone` (018 nguyên trạng: cookie, SSO riêng, `gm_staff`) **giữ** cho Local/Dev/Test và deployment chưa chuyển;
    một deployment chỉ chạy một chế độ. Slice: **CP-1 token auth ở deployment → CP-2 control plane + gate → CP-3 console đa target
    → CP-4 quản trị quyền** → GM-3 → GM-4.

---

## §1. Đã cân nhắc (rút gọn)

| Phương án | Vì sao không chọn |
| --- | --- |
| **A′** console liên bang, quyền ở `gm_staff` từng deployment, matrix fan-out | Không có một nơi duy nhất quản trị/audit/thu hồi; admin cần phiên trên từng target; owner chọn C (Q22). Ưu điểm còn giữ trong C: **deployment vẫn tự kiểm quyền per request**. |
| **B** claim/nhóm IdP `gm:<game>:<env>:<role>` | Grant nằm ở IdP (IT quản), chỉ đọc lúc đăng nhập → trái SEC-10; nhóm bùng nổ; không biểu diễn được permission ngoài role. Phần dùng lại: IdP chỉ là nguồn **danh tính** cho gate. |
| **C với CP làm reverse proxy** (console → CP → deployment) | Xem §4.1: CP nằm trong đường dữ liệu mọi request, phải với tới mọi deployment, thêm code proxy; lợi ích (không preflight, token không vào trình duyệt) không đủ bù. Giữ làm phương án tăng cường mạng sau này (§11). |

---

## §2. Control plane: hình dạng, lưu trữ, môi trường, blast radius, sẵn sàng

### 2.1. Vị trí và đóng gói

| Hạng mục | Quyết định |
| --- | --- |
| Repo | Framework: `src/GameServer.GmControlPlane` (host Kestrel) + `tests/GameServer.GmControlPlane.Tests`. Game-agnostic: biết "project" chỉ qua file cấu hình. |
| Tái dùng | `Platform.Persistence` (KV + transaction + audit outbox), `Platform.Web` (errors, CORS, security profile, rate limit), observability, phần OIDC/cookie/CSRF của `Modules.Gm.Adapters` (F4) — tách các lớp dùng chung ra nếu cần, không sao chép. Tham chiếu `Modules.Gm` cho `GmRoles` và hợp đồng token. |
| Deploy | Một **repo mỏng của công ty** (vd `gm-control-plane`) theo đúng mẫu game: submodule framework@tag + `config/` + CI. Repo này cũng build site console (app mặc định); bundle game ở `/<gameId>/` do pipeline release của game đẩy lên. |
| Instance | **Một CP production** quản cả 4 env của mọi game (đúng "một URL"). Deployment env của CP = `Production`. Không có CP riêng cho live (sẽ thành hai nơi quản trị — trái Q22); cô lập live bằng khoá ký tách tầng + duyệt hai người. CP Development chỉ để phát triển/test framework. |
| HA | ≥ 2 instance stateless sau load balancer; trạng thái ở Couchbase. |
| Lưu trữ | Couchbase **cluster live**, bucket riêng `gmcp` (không chung bucket game). Collection: `members`, `identities` (`oidc:{iss}:{sub}`), `grants`, `pending_grants`, `sessions` (ticket), `catalog_cache`, `cp_audit`. Mọi ghi qua transaction + audit cùng transaction (như SEC-14). |
| Project registry | **File cấu hình** của CP (`config/projects.json`, theo CFG-01: config là file, không trang config): `{gameId, gameName, environmentId, tier, apiBaseUrl, consolePath}`. Thêm game/env = PR cấu hình + redeploy CP + site (CSP sinh từ cùng file). |

### 2.2. Blast radius và giảm thiểu

CP bị chiếm (hoặc khoá ký bị lộ) ⇒ phát được token cho **mọi live**. Đây là cái giá của "một nơi". Giảm thiểu:

| # | Biện pháp | Chặn được gì |
| --- | --- | --- |
| B1 | **Khoá ký tách tầng**: `live` và `nonlive`; deployment live chỉ pin khoá live | Lộ khoá non-live (hoặc lỗi chỉ ở luồng non-live) không chạm live |
| B2 | Khoá trong **KMS/HSM** (CP gọi `Sign`, khoá không rời KMS); v1 (owner Q30: chưa có KMS): khoá trong secret configuration (OPS-08) qua `ITokenSigner` provider `SecretConfiguration`; **R-KMS** (luật khởi động, chọn thay vì checklist vì máy kiểm được, không quên được): ở Production, nếu `projects.json` có target `tier = live` mà `Cp:Signing:Live:Provider = SecretConfiguration` → CP **không khởi động**, dòng lỗi nêu cách sửa. Non-live được dùng secret configuration. Development/Test: không áp | Đọc trộm file/biến môi trường không lấy được khoá (với KMS); tắt khoá trên KMS = dừng phát hành tức thời |
| B3 | **Khoá công khai pin ở deployment** (không JWKS động) | Kẻ chiếm CP không "đẩy" được khoá mới cho deployment tin |
| B4 | Token **60 s**, `aud` một target, `typ = gm-at+jwt`, `alg` allowlist ES256 | Token bị lấy chỉ dùng được ≤ 60 s, một target; không nhầm với id token OIDC |
| B5 | **MFA bắt buộc** ở gate (`amr`), `email_verified`, domain allowlist, nên thêm ràng buộc tenant/issuer của IdP | Tài khoản ngoài công ty hoặc chưa MFA không vào |
| B6 | **Hai người cho grant live** + step-up (`max_age` ≤ 10 phút) cho người yêu cầu và người duyệt; không ai tự duyệt/tự cấp cho mình | Một admin bị chiếm phiên không tự mở live |
| B7 | Audit bất biến ở CP + log phát hành token (`jti`, `sid`, target) + cảnh báo khi cấp quyền live | Phát hiện và truy vết |
| B8 | CP chỉ nhận traffic qua `gm.<domain>`; endpoint admin có rate limit; CP không có route Dev ở Production | Thu hẹp bề mặt |

Dev bị chiếm (deployment hoặc pipeline dev) **không** ảnh hưởng live: dev không giữ khoá nào, chỉ có khoá công khai.

### 2.3. Sẵn sàng

- CP sập: console không xin được token → mọi target 401 sau ≤ 60 s. **Fail closed** cho GM; player API không phụ thuộc CP.
- IdP sập: không đăng nhập mới; phiên CP còn hạn vẫn xin token được (CP không gọi IdP khi cấp token).
- **Không break-glass** (owner chốt Q28, 2026-11-09): không mật khẩu ở beta/uat/live, không chuyển khẩn cấp sang Standalone; GM chờ
  CP/IdP khôi phục. Local login chỉ ở Local/Dev. Vì vậy HA của CP (≥ 2 instance, giám sát, cảnh báo) là yêu cầu vận hành, không tuỳ chọn.
- Deployment **không gọi CP lúc chạy** (khoá pin) → CP chậm không làm chậm GM API; chỉ việc xin token phụ thuộc CP.

---

## §3. Gate, member, quản trị

### 3.1. Luồng gate

```
console  →  top-level GET /cp/v1/auth/sso/start?returnUrl=/#/…        (cùng origin)
CP       →  IdP (OIDC code + PKCE, client bí mật của CP)
IdP      →  GET /cp/v1/auth/sso/callback
CP gate:    id_token hợp lệ (issuer từ discovery) · MFA theo Cp:Oidc:Mfa (xem dưới) · email_verified
            · domain(email) ∈ Cp:AllowedEmailDomains (so khớp chính xác phần sau '@', không phân biệt hoa thường,
              không nhận subdomain ngầm; rỗng ở Production → không khởi động)
            · (khuyến nghị) issuer/tenant ∈ Cp:AllowedIssuers
            → identity (iss, sub) đã bind? → member đó
            → chưa: member có email trùng (lời mời của admin)? bind một lần (như StaffIdentityMatcher)
            → chưa: TỰ TẠO member, 0 grant
            → member bị khoá → từ chối
            → Set-Cookie __Host-gs_cp (HttpOnly, Secure, Lax, Path=/) → 302 /#/…
console  →  GET /cp/v1/me → { member, projects: [{gameId, envId, tier, apiBaseUrl, consolePath, permissions[]}], cpPermissions[] }
            0 project → màn "Bạn chưa được cấp quyền project nào — liên hệ admin" + tên các admin của game (nếu có)
```

Tự tạo member không vi phạm SEC-03: thuộc công ty chỉ cho **danh tính**, mọi quyền project vẫn cần grant tường minh.
Ví dụ (owner 2026-11-09): `a@vng.com.vn` qua gate → member 0 quyền; `a@gmail.com` → từ chối, không tạo member.

**IdP chưa chọn → OIDC chuẩn, mọi thứ là cấu hình** (`Cp:Oidc`):

| Khoá | Ý nghĩa |
| --- | --- |
| `Authority` | URL issuer; endpoint lấy từ `/.well-known/openid-configuration` (discovery) |
| `ClientId`, `ClientSecret` (secret config) | client bí mật của CP; luồng authorization code + **PKCE** bắt buộc |
| `Scopes` | mặc định `openid profile email` |
| `Mfa.Claim` | `amr` (mảng, khớp bất kỳ giá trị trong `Mfa.Values`) hoặc `acr` (chuỗi, khớp một giá trị trong `Mfa.Values`) |
| `Mfa.Values` | vd `["mfa","otp","hwk"]` cho `amr`; vd `["urn:…:mfa"]` cho `acr` |
| `Mfa.RequestAcr` | tuỳ chọn: gửi `acr_values` khi IdP hỗ trợ ép MFA |
| `EmailVerifiedClaim` | mặc định `email_verified` (một số IdP dùng tên khác hoặc không phát → cấu hình rõ, validator buộc khai) |

Validator lúc khởi động: thiếu `Mfa.Claim`/`Mfa.Values` ở Production → không khởi động. Local/Dev: OIDC giả (server OIDC dev
chạy local hoặc `BackchannelHandler` trong test) hoặc local login (chỉ Development). Đổi IdP = đổi cấu hình, không đổi code.

### 3.2. Quyền của chính control plane

| Role CP (template) | Permission CP | Phạm vi |
| --- | --- | --- |
| `SuperAdmin` | `cp.members.read`, `cp.members.manage` (khoá, thu hồi phiên, mời), `cp.admins.manage`, `cp.grants.manage`, `cp.grants.approve`, `cp.audit.read` | mọi game |
| `ProjectAdmin` | `cp.members.read`, `cp.grants.manage`, `cp.grants.approve`, `cp.audit.read` | một game (4 env) |
| (member thường) | — | chỉ xem `/cp/v1/me` |

Luật (owner Q29, 2026-11-09): không ai sửa grant của **chính mình**; grant live **thêm** quyền → `pending_grants`, cần người thứ
hai có `cp.grants.approve` trên game đó (≠ người đề xuất); người đề xuất **xác thực lại** (step-up, `max_age` ≤ 10 phút) khi đề
xuất, người duyệt cũng step-up khi duyệt (bổ sung của architect); dev/beta/uat: một admin, hiệu lực ngay; **gỡ** quyền, khoá
member, thu hồi phiên (mọi env) → hiệu lực ngay, một người. Không cấp role CP qua grant project.

**Bootstrap (owner Q31):** `Cp:BootstrapAdmins` = **đúng 2** email (validator ở Production: đúng 2, khác nhau, domain ∈
`Cp:AllowedEmailDomains`; Development/Test ≥ 1, giá trị giả) → khi CP chưa có `SuperAdmin` nào, người đó được `SuperAdmin` ở lần
đăng nhập gate đầu (giống `GmBootstrapper`; audit actor `system:bootstrap`). Hai SuperAdmin cũng là điều kiện để luật hai người
cho live chạy được ngay từ đầu. Giá trị thật cấu hình sau.

### 3.3. Grant project và catalog quyền

- Grant lưu ở CP: `{memberId, gameId, environmentId, template?, permissions[] (đã expand), catalogVersion}` — giữ SEC-15 ("lưu
  permission đã expand").
- Expand cần catalog của target → deployment thêm route `gm.permission_catalog` `GET /gm/v1/console/permissions` (profile GM,
  permission `gm.catalog.read`) trả `{permissions[], templates: {Support: [...], …}, frameworkVersion, gameVersion}`. CP gọi
  **server-to-server** bằng token tự cấp (`sub = system:control-plane`, `perm = [gm.catalog.read]`) **chỉ lúc admin sửa grant**;
  cache trong `catalog_cache`; target không với tới → dùng bản cache, ghi rõ tuổi.
- Catalog có quyền mới (module mới) → admin thấy "catalog có N quyền mới" khi mở grant; Administrator **không tự** nhận quyền mới
  (phải áp lại, live thì qua duyệt).
- Deployment luôn **giao** `perm` của token với catalog của mình (như `GmRoles.Expand` không cấp quyền deployment không có).

---

## §4. Console ↔ API

### 4.1. Bearer trực tiếp (chọn) vs CP reverse proxy

| | **Bearer trực tiếp (CHỌN)** | CP reverse proxy |
| --- | --- | --- |
| Đường dữ liệu | console → deployment | console → CP → deployment |
| CP trong hot path | không (chỉ xin token, ~1 lần/45 s/target đang mở) | mọi request GM |
| Mạng | không cần CP với tới deployment (trừ catalog lúc admin sửa) | CP phải với tới mọi deployment (dev mạng riêng?) |
| Code | handler xác minh token (nhỏ) | proxy đầy đủ: header, timeout, lỗi, kích thước, Problem Details, rate limit theo người |
| Preflight | có cho mọi URL mới (header `Authorization`), cache 7200 s/URL | không |
| Token trong trình duyệt | có (bộ nhớ, 60 s, một target) | không |
| Kết luận | đơn giản, deployment tự quyết, CP nhẹ | để dành nếu muốn GM API chỉ mở cho mạng của CP |

### 4.2. Phiên, CSRF, CORS, refresh

| Hạng mục | Thiết kế |
| --- | --- |
| Phiên CP | Cookie `__Host-gs_cp` + `__Host-gs_cp_csrf` cùng origin console, ticket server-side (`sessions`), 8 h tuyệt đối / 60 phút idle (như `GmOptions`). Mọi `POST /cp/**` cần `X-CSRF-Token` (antiforgery, tái dùng 018) + kiểm `Origin` = chính nó. CP **không bật CORS**. |
| Token target | `POST /cp/v1/tokens` → `{accessToken, expiresAt}` + `Cache-Control: no-store`. Shell giữ trong `TargetScope` (bộ nhớ); làm mới khi còn < 15 s hoặc khi API trả 401; single-flight mỗi target; F5 → xin lại bằng cookie CP (không bật SSO lại). |
| API (ControlPlane mode) | Chỉ nhận `Authorization: Bearer`; **bỏ qua cookie** → không CSRF (trang khác site không gắn được header này; CORS chặn đọc). Không antiforgery trên route GM ở chế độ này. |
| CORS deployment | Đúng một origin `https://gm.<domain>` (D2 giữ nguyên chữ), **`AllowCredentials = false`**, allowed headers `Authorization, Content-Type, X-Request-Id`, expose `X-Request-Id, Retry-After, ETag`, max-age 7200. Development thêm `http://localhost:5173`. |
| Cùng site? | **Không cần** cho host API (không cookie). Host API live có thể ở domain bất kỳ (vẫn HTTPS). Chỉ console + CP phải cùng origin. → Câu hỏi domain Q24 cũ không còn chặn. |
| Không storage | Không token/phiên trong `localStorage`/`sessionStorage`/IndexedDB (giữ §12.2). |

### 4.3. XSS trên origin chung

Origin `gm.<domain>` giữ token của mọi target đang mở + cookie CP → XSS ở đây = hành động như staff trên mọi project họ có (token
lấy ra dùng được ≤ 60 s; cookie CP `HttpOnly` không lấy ra được). Biện pháp:

- CSP giữ của 018 (`script-src 'self'`, `style-src 'self'`, `frame-ancestors 'none'`, `base-uri 'none'`, `form-action 'self'`
  cho SSO) + **`connect-src 'self' <mọi apiBaseUrl trong projects.json>`** (sinh từ file) → fetch ra ngoài bị chặn.
- **Trusted Types**: `require-trusted-types-for 'script'; trusted-types 'none'` (React không cần sink HTML khi đã cấm
  `dangerouslySetInnerHTML`) — [VERIFY V-C3] với Mantine/Vite build.
- Chỉ pipeline release ghi site; build nhánh dev chỉ chạy `localhost`; không script bên thứ ba.
- Hoãn: **DPoP** (token gắn khoá WebCrypto không xuất được) — làm token lấy cắp vô dụng ngoài trình duyệt (§11).

### 4.4. Số phận code 018

| Thành phần 018 | ControlPlane mode | Standalone mode |
| --- | --- | --- |
| Cookie `__Host-gs_gm`/`gs_gm_dev`, `GmCookieAuthenticationHandler`, ticket `gm_sessions` | không dùng | giữ |
| Antiforgery + `Origin` check trên GM API | không dùng (bearer) | giữ |
| SSO per deployment (`Gm:Oidc`), `GmConsoleUrls`, `Gm:Console:BaseUrl`, return/denial | không dùng (cấu hình bị validator từ chối) | giữ |
| Local login (Dev/Test) | không dùng | giữ |
| `gm_staff`, route `/gm/v1/staff*`, `GmBootstrapper` managers | route **không map**; dữ liệu cũ để nguyên (nguồn để import một lần) | giữ |
| `gm.auth_methods` | trả `{local:false, sso:false, controlPlane:true}` (additive) | giữ |
| CORS (expose, max-age, header trên lỗi) | giữ, đổi credentials/headers | giữ |
| Cookie + CSRF + OIDC + bootstrap như **thư viện** | **tái dùng trong CP** cho phiên `gs_cp` | — |
| Shell: `createGmClient`, session, guard, skew, catalog, nav, trang đọc | giữ; middleware chọn bearer hoặc cookie theo mode | giữ (chế độ một target, `config.json`) |

---

## §5. Hợp đồng token và tin cậy CP ↔ deployment

### 5.1. Token (JWT/JWS — chọn JWT thay PASETO vì thư viện .NET chuẩn `Microsoft.IdentityModel.JsonWebTokens`)

```
header : { alg: "ES256", typ: "gm-at+jwt", kid: "live-2026-11" }
payload: { iss: "https://gm.<domain>/cp",
           aud: "gm:zhpvp:live",
           sub: "m_7f3k…"            (memberId CP, ổn định; không phải email)
           name: "Nguyễn An",
           sid: "s_…"                 (phiên CP)
           jti: "…", iat, nbf, exp   (exp − iat ≤ 60 s)
           perm: ["gm.access","player.read",…]   (đã expand cho đúng target)
           mfa: true, auth_time }    (CP đã kiểm MFA theo Cp:Oidc:Mfa; token không chép amr/acr của IdP)
```

### 5.2. Xác minh ở deployment (mỗi request, không I/O)

`typ` đúng · `alg` ∈ {ES256} · `kid` ∈ khoá pin · chữ ký · `iss` khớp · `aud == gm:<GameId>:<EnvironmentId>` (hằng, ARC-10 —
token chỉ để đối chiếu) · `nbf/exp` (lệch giờ ≤ 5 s) · `perm ∩ catalog` · dựng `ActorType = gm`, `ActorId = sub`, `Permission`,
`gs:display_name = name`, `gs:gm_session = sid` (F2) → authorization, `x-gm-permission`, audit **không đổi**.

### 5.3. Khoá

- CP: `nonlive` cho tier dev/beta/uat, `live` cho tier live (theo `projects.json`); mỗi tầng một khoá đang dùng + một khoá kế.
- Cấu hình ký theo tầng: `Cp:Signing:{NonLive|Live}:{Provider, ActiveKeyId, Keys[]}`; `Provider` = `SecretConfiguration` (v1, khoá
  PEM trong secret config, OPS-08) hoặc một provider KMS (điểm cắm, cài khi có hạ tầng). Khoá live và non-live là hai khoá khác
  nhau (validator từ chối trùng). R-KMS (§2.2 B2) chặn live trên `SecretConfiguration` ở Production.
- Deployment: `Gm:ControlPlane:SigningKeys = [{kid, publicKeyPem}]` (khoá hiện tại + khoá kế) trong file cấu hình env.
- Xoay vòng: thêm khoá kế vào deployment (rolling deploy) → CP chuyển sang khoá kế → sau ≥ 60 s gỡ khoá cũ. Sự cố: gỡ `kid` bị lộ
  ở deployment (deploy) + tắt khoá trên KMS.
- CP vẫn phát `/cp/.well-known/jwks.json` cho công cụ/kiểm tra; deployment **không** đọc lúc chạy.

### 5.4. Thu hồi

Mỗi lần `POST /cp/v1/tokens` CP kiểm: phiên CP còn hạn và chưa thu hồi · member không khoá · `sessionEpoch` khớp · grant cho
target còn · tier live: phiên CP được tạo bằng đăng nhập có MFA (theo `Cp:Oidc:Mfa`). Khoá member/gỡ grant/thu hồi phiên → token cũ chết khi hết hạn ≤ 60 s (SEC-10) — không cần
introspection hay revocation list (đều tạo phụ thuộc lúc chạy).

### 5.5. `gm_staff` và `StaffGrant` ở ControlPlane mode

Không còn là nguồn quyền; không projection/cache (token mang đủ). Hiển thị tên actor trong audit lấy từ claim `name` lúc ghi
(snapshot) + `sub`. Giữ nguyên dữ liệu cũ để import một lần vào CP lúc chuyển (§9).

### 5.6. Interface (C) — chỉ signature

```csharp
// GameServer.Modules.Gm — shared by deployments (validate) and the control plane (issue)
public enum GmAuthMode { Standalone, ControlPlane }

public static class GmAccessTokenContract
{
    public const string TokenType = "gm-at+jwt";
    public const string Algorithm = "ES256";
    public const string PermissionClaim = "perm";
    public const string SessionClaim = "sid";
    public const string NameClaim = "name";
    public const string CatalogReadPermission = "gm.catalog.read";
    public static string Audience(string gameId, string environmentId);
}

// GameServer.Modules.Gm.Adapters — deployment side
public sealed class GmControlPlaneOptions
{
    public string? Issuer { get; set; }
    public List<GmSigningKey> SigningKeys { get; set; }
    public int ClockSkewSeconds { get; set; }
}
public sealed record GmSigningKey(string KeyId, string PublicKeyPem);
public sealed class GmBearerAuthenticationHandler /* : AuthenticationHandler<…> — scheme "GmBearer" */ { }
public sealed record GmPermissionCatalogResponse(IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, IReadOnlyList<string>> Templates, string FrameworkVersion, string GameVersion);
// GmMeResponse gains: public string DeploymentEnvironment { get; init; }

// GameServer.GmControlPlane
public enum EnvironmentTier { Dev, Beta, Uat, Live }
public enum SigningTier { NonLive, Live }
public sealed record ProjectTarget(string GameId, string GameName, string EnvironmentId, EnvironmentTier Tier, Uri ApiBaseUrl, string ConsolePath);
public sealed record Member(string MemberId, string Email, string DisplayName, StaffOidcIdentity? Identity, bool Disabled, long SessionEpoch);
public sealed record TargetGrant(string MemberId, string GameId, string EnvironmentId, string? Template, IReadOnlyList<string> Permissions, string CatalogVersion);
public sealed record PendingGrant(string PendingId, TargetGrant Proposed, string RequestedBy, string Reason, DateTimeOffset RequestedAt);
public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public interface IProjectRegistry { IReadOnlyList<ProjectTarget> All { get; } ProjectTarget? Find(string gameId, string environmentId); }
public interface ITokenSigner { string KeyId(SigningTier tier); Task<string> SignAsync(SigningTier tier, ReadOnlyMemory<byte> signingInput, CancellationToken ct); }
// One signer per tier, chosen by Cp:Signing:<Tier>:Provider; v1 ships SecretConfiguration only, KMS providers plug in here.
public interface ISigningKeyProvider { string Name { get; } bool IsHardwareBacked { get; } string ActiveKeyId { get; } Task<byte[]> SignAsync(ReadOnlyMemory<byte> signingInput, CancellationToken ct); }
public interface IAccessTokenIssuer { Task<IssuedToken> IssueAsync(string sessionKey, string gameId, string environmentId, CancellationToken ct); }
public interface IGateAdmission { Task<Member?> AdmitAsync(string issuer, string subject, string? email, bool emailVerified, IReadOnlyCollection<string> amr, string correlationId, CancellationToken ct); }
public interface IPermissionCatalogClient { Task<GmPermissionCatalogResponse> GetAsync(ProjectTarget target, CancellationToken ct); }
public interface IGrantService
{
    Task<GrantChangeResult> ProposeAsync(GrantChange change, CancellationToken ct);   // live + adds permission → pending
    Task<GrantChangeResult> ApproveAsync(string pendingId, CpActor approver, CancellationToken ct);
    Task<GrantChangeResult> RevokeAsync(string memberId, string gameId, string environmentId, CpActor actor, string reason, CancellationToken ct);
}
```

### 5.7. Route CP (`/cp/v1`, cùng origin console)

| Route | Profile | Ghi chú |
| --- | --- | --- |
| `GET auth/sso/start`, `GET auth/sso/callback` | Public (allowlist) | gate §3.1 |
| `GET auth/csrf`, `POST auth/logout` | phiên CP | |
| `GET me` | phiên CP | member + projects + quyền CP |
| `POST tokens` | phiên CP + CSRF | §5.4 |
| `GET/POST admin/members`, `POST admin/members/{id}/disable|enable|revoke-sessions` | `cp.members.*` | |
| `GET/PUT admin/members/{id}/grants/{gameId}/{envId}` | `cp.grants.manage` (scope game) | live thêm quyền → pending |
| `GET admin/approvals`, `POST admin/approvals/{id}/approve|reject` | `cp.grants.approve` + step-up | |
| `GET admin/audit` | `cp.audit.read` | |
| `GET .well-known/jwks.json` | Public | công cụ, không dùng lúc chạy |

---

## §6. UX console

| Hạng mục | Thiết kế |
| --- | --- |
| Vào | `https://gm.<domain>/` → chưa có phiên CP → nút "Đăng nhập SSO" → gate → **project picker**: bảng game × (dev, beta, uat, live), chỉ ô được cấp mới bấm được, kèm tóm tắt quyền |
| Danh sách target | Từ `GET /cp/v1/me` (đã lọc theo grant) — không còn `targets.json` tĩnh ở site; `projects.json` của CP là nguồn duy nhất, CSP sinh từ cùng file |
| Route | `#/t/<gameId>/<envId>/…`; game có bundle riêng → `/<gameId>/#/t/…` (Q25, GM-4). Đổi target = `TargetScope` mới (key theo target): client, token, `/me`, catalog, `QueryClient` riêng → không lẫn dữ liệu live/dev |
| Guard thay Q19 | `/me` của deployment phải khớp `gameId/envId` của project → khác thì **chặn** (registry trỏ nhầm) |
| Đánh dấu env | dev xám, beta xanh dương, uat cam, **live đỏ** (viền + banner + tiêu đề tab). Đỏ khi `tier = live` **hoặc** `/me.deploymentEnvironment = Production`. Xác nhận gõ lại trên target đỏ gồm `<gameId>/<envId>` |
| Version skew | Theo target: `builtFor` vs `/me.frameworkVersion`; major khác → chặn target; minor → cảnh báo/thông tin. Lệch dev/live là thường trực → chỉ banner ở target lệch |
| Quyền × catalog | Nav = `perm` của target × operation có trong catalog của target (luật 018); trang extension khai `requiredOperations` (GM-4) |
| Admin | `#/admin/…` trong app mặc định (gọi `/cp/v1/admin`): member, matrix grant theo game (4 cột env), hàng đợi duyệt live, audit, thu hồi |
| Chế độ một target | `config.json` có mặt và không có CP (`/cp/v1/me` 404) → hành vi 018 (Local/Dev Standalone) |

---

## §7. Audit và tương quan

| Nơi | Ghi gì | Khoá tương quan |
| --- | --- | --- |
| CP `cp_audit` (bất biến, cùng transaction với thay đổi) | đăng nhập gate (thành công/từ chối + lý do), tạo/khoá member, đề xuất/duyệt/từ chối/gỡ grant (before/after, reason), thu hồi phiên, bootstrap | `memberId`, `sid`, `requestId` |
| CP log phát hành token (log, có retention, không phải audit SEC-14) | `jti`, `sid`, `sub`, target, `exp` | `jti`, `sid` |
| Deployment `gm_audit` (như hôm nay, SEC-14) | GM action; actor = `sub` (memberId CP), `name` snapshot, permission, target, reason, receipt | thêm `sid` (+ `jti`) vào correlation |

Truy vết "ai đã ban player X trên live": `gm_audit` live → `sub` + `sid` → `cp_audit` (phiên đó đăng nhập lúc nào, MFA, grant nào
đang có, ai duyệt grant đó).

---

## §8. Quyết định thiết kế (E)

**C1. Control plane trong repo framework, một instance cho công ty.** → *Lý do:* game-agnostic, tái dùng Platform + code GM;
deploy bằng repo mỏng như game. → *Thay thế:* repo/sản phẩm riêng; IdP thương mại làm authz. → *Đánh đổi:* framework thêm một host
phải bảo trì.

**C2. Token ngắn hạn theo target, bearer trực tiếp, không proxy.** → *Lý do:* CP ngoài hot path, deployment tự quyết, bỏ ràng buộc
cùng site của host API. → *Thay thế:* proxy (§4.1); cookie per host (A′). → *Đánh đổi:* preflight mỗi URL; token ở trình duyệt
(60 s, một target).

**C3. Khoá công khai pin, không JWKS lúc chạy, khoá tách live/non-live.** → *Lý do:* deployment không phụ thuộc CP lúc chạy; kẻ
chiếm CP không cài khoá mới. → *Đánh đổi:* xoay khoá cần deploy cấu hình.

**C4. Thu hồi bằng TTL 60 s + kiểm ở mỗi lần cấp.** → *Lý do:* đúng SEC-10 không cần introspection. → *Đánh đổi:* console làm mới
token mỗi ~45 s/target đang mở; CP sập = GM dừng ≤ 60 s.

**C5. Deployment giữ kiểm quyền và audit; token chỉ thay nguồn danh tính + grant.** → *Lý do:* claim giống hệt F2 → toàn bộ
authorization/route/audit không đổi; giao `perm` với catalog. → *Đánh đổi:* grant hiển thị ở CP có thể nhiều hơn quyền thật (catalog
cũ) — UI ghi rõ.

**C6. Grant lưu đã expand; catalog lấy từ deployment khi admin sửa.** → *Lý do:* giữ SEC-15, Administrator không tự nở quyền. →
*Đánh đổi:* thêm route `gm.permission_catalog` + CP gọi deployment lúc quản trị.

**C7. Gate tự tạo member không quyền.** → *Lý do:* đúng mô tả owner (đăng nhập rồi mới biết quyền), giữ SEC-03 (quyền tường minh).
→ *Thay thế:* chỉ cho đăng nhập khi đã được mời. → *Đánh đổi:* bảng member có người 0 quyền.

**C8. Hai người cho grant live (thêm quyền); gỡ quyền một người.** → *Lý do:* giới hạn admin bị chiếm phiên; thu hồi không được chậm.

**C9. Console + CP cùng origin (path `/cp/**`) — owner chốt Q32.** → *Lý do:* phiên CP không cần CORS, cookie cùng origin. →
*Thay thế (đã bỏ):* CP ở `cp.gm.<domain>`. → *Đánh đổi:* static host/reverse proxy phải định tuyến `/cp/**` và không cache nó;
local: Vite proxy `/cp` → CP.

**C11. R-KMS là luật khởi động, không phải checklist (Q30).** → *Lý do:* máy kiểm được, không phụ thuộc trí nhớ người release;
CP vẫn phục vụ dev/beta/uat bằng secret configuration trong khi chờ KMS. → *Thay thế:* checklist release; readiness probe đỏ. →
*Đánh đổi:* thêm target live vào `projects.json` trước khi có KMS làm CP Production không khởi động — đúng ý định.

**C10. Một chế độ mỗi deployment (`Gm:Mode`).** → *Lý do:* không có hai đường đăng nhập song song trên live. → *Đánh đổi:* chuyển
chế độ = deploy.

---

## §9. Ảnh hưởng tới quyết định 017/018, GM-3/GM-4, migration

| Quyết định | Trạng thái mới |
| --- | --- |
| 018 **D2 / BC-5** một origin CORS ngoài Development | Giữ; giá trị `https://gm.<domain>` cho mọi env; ControlPlane mode: `AllowCredentials=false`, thêm header `Authorization` |
| 018 **D3** `SameSite=None` chỉ Dev/Test | Chỉ còn nghĩa ở Standalone |
| 018 **D4** / **Q20** tên cookie `__Host-gs_gm`, `gs_gm_dev` | Standalone; CP dùng `__Host-gs_cp`, `__Host-gs_cp_csrf` (Development: `gs_cp_dev…`) |
| 018 **D5/D6/D7** `Gm:Console:BaseUrl`, return path, fragment từ chối | Standalone; validator từ chối `Gm:Oidc`/`Gm:Console` khi `Gm:Mode=ControlPlane`; CP có return path riêng (cùng origin, chỉ `/` hoặc `/#…`) |
| 018 **D10** không mutation nghiệp vụ tới GM-3 | Không đổi |
| 018 **D15** làm mới CSRF + thử lại một lần | Chỉ cho request tới CP (và Standalone) |
| 018 **D14** e2e với host chạy ngoài test | Không đổi; thêm host CP |
| **Q19** guard + skew | Guard so `/me` với project của CP; skew theo target |
| 01 **D9 / Q16 / G6** case A (cùng site) | Chỉ còn bắt buộc cho console + CP (cùng origin); **host API không cần** cùng site |
| 01 **D11** / 018 FR-050 `config.json` | Chỉ chế độ một target (Standalone); ControlPlane: project từ `/cp/v1/me` |
| 01 §12.2–12.3 (không token trong JS; CSRF mọi mutation) | Sửa: access token 60 s **trong bộ nhớ** được phép; CSRF chỉ cho cookie (CP, Standalone) |
| 01 §6.6 CSP | `connect-src` = `'self'` + mọi `apiBaseUrl` của `projects.json`; thêm Trusted Types |
| 017 **D9** version trong `/me` | Giữ, cần thiết hơn; `/me` thêm `deploymentEnvironment`; ControlPlane: `/me` dựng từ token |
| 007 `gm_sessions`, route `staff*`, `GmBootstrapper` managers | Standalone; không map ở ControlPlane |
| Requirements | SEC-03 (gate + domain allowlist + MFA ở CP), SEC-04/05 (phiên CP cookie; API bearer), SEC-06 (không credentials ở ControlPlane), SEC-07 (grant lưu ở CP, token mang permission đã expand cho một (game, env), deployment kiểm per request), SEC-10 (TTL 60 s), SEC-14 (audit CP), G6, TBD-05a; thêm requirement mới cho CP (owner sửa file). ARC-10 **không đổi**. |
| **GM-3** `gm-generic-forms` | Form dùng client bearer của `TargetScope`; không CSRF ở ControlPlane; gõ lại có `<game>/<env>` trên target đỏ; **bỏ trang quản lý staff khỏi GM-3** (chuyển sang CP-4) |
| **GM-4** `gm-console-extension` | Bundle game ở `/<gameId>/` cùng origin (Q25), chỉ pipeline release; `requiredOperations`; dùng token provider của shell (cùng origin với `/cp`) |

**Migration zh-pvp (đang pin `v0.7.0`):**

1. Bump `v0.8.0` (017 + 018, tag đề xuất từ trước) — không đổi hành vi, Standalone.
2. Bump tag có CP-1: vẫn Standalone mặc định; không cấu hình mới.
3. Khi CP production có (CP-2..CP-4): khai 4 target zh-pvp trong `projects.json`; import grant hiện có từ `gm_staff` (công cụ một
   lần đọc `/gm/v1/staff` từng env bằng phiên Standalone của admin → đề xuất grant ở CP; live đi qua duyệt) — hoặc cấp lại tay (ít
   staff).
4. Chuyển từng env: dev → beta → uat → live: `Gm:Mode=ControlPlane`, `Gm:ControlPlane:{Issuer, SigningKeys}`, CORS origin chung,
   bỏ `Gm:Oidc`/`Gm:Console`. Staff đăng nhập lại qua gate.
5. Local/Dev flavor của zh-pvp có thể giữ Standalone (local login) mãi.

**Deployment không có CP:** Standalone = 018 nguyên trạng, hỗ trợ ở mọi env cho tới khi owner quyết định cấm ngoài Dev/Test (đề
xuất: cấm ở Production sau khi mọi game đã chuyển — quyết định sau, không cần bây giờ).

---

## §10. Slice và test local (Q16: chưa có domain)

Số slice = "next free number" lúc bắt đầu.

| # | Slice | Phạm vi | Test local |
| --- | --- | --- | --- |
| 1 | **CP-1 `gm-token-auth`** (deployment) | `GmAuthMode`, `GmControlPlaneOptions` + validator (một mode; khoá pin hợp lệ; cấm `Gm:Oidc`/`Gm:Console` ở ControlPlane), `GmBearerAuthenticationHandler`, `GmAccessTokenContract`, giao `perm` × catalog, `/me` từ token + `deploymentEnvironment`, route `gm.permission_catalog`, CORS không credentials, `staff*`/`auth*` theo mode, `sid` trong audit correlation, TestKit `GmTestTokens` (ký bằng khoá test). PCR gm + platform, OpenAPI snapshot. | Hoàn toàn trong test host (in-memory/File/Couchbase), không cần CP: token ký bằng khoá test. |
| 2 | **CP-2 `gm-control-plane`** (host mới) | `GameServer.GmControlPlane`: gate OIDC chuẩn (discovery, code + PKCE; MFA cấu hình `amr`/`acr`, `email_verified`, domain/issuer allowlist; test cả hai kiểu MFA bằng OIDC giả), member tự tạo, bootstrap SuperAdmin, phiên `gs_cp` + CSRF (tái dùng), `projects.json`, grant (lưu đã expand, chưa có UI), `POST tokens` (ES256, tách tầng, `ITokenSigner` khoá từ secret config), `cp_audit`, `/cp/v1/me`, flavor Local (đăng nhập local chỉ Development). | CP `:5070` (Development, local login hoặc OIDC giả qua `BackchannelHandler`) + run-local `:5080` ở ControlPlane mode pin khoá dev. Tích hợp: token CP → GM API 200; khoá member → 401 sau ≤ 60 s. |
| 3 | **CP-3 `gm-console-multi-target`** | Shell: project picker từ `/cp/v1/me`, `TargetRouter`, `TargetScope` (token provider single-flight, client bearer), guard/skew/`EnvFrame`, đăng nhập CP; giữ chế độ một target; `deploy/`: path routing `/cp/**`, CSP sinh từ `projects.json`, Trusted Types. | Vite `:5173` proxy `/cp` → `:5070` (đúng topology production: cùng origin), hai deployment `:5080` (dev) và `:5091` (Production flavor → khung đỏ). Bearer nên **không** còn vấn đề cookie trùng giữa các port localhost. E2E Chrome/Edge. |
| 4 | **CP-4 `gm-access-admin`** | API + UI quản trị: member, matrix grant theo game, lấy catalog từ deployment (service token), template + diff quyền mới, duyệt hai người + step-up cho live, khoá/thu hồi, audit; công cụ import từ `gm_staff`. | Như CP-3; target không với tới = tắt một host; step-up giả bằng `max_age` ở OIDC giả. |
| 5 | **GM-3 `gm-generic-forms`** | Như kế hoạch, trên `TargetScope`; bỏ quản lý staff. Có thể chạy song song CP-4 sau CP-3 (khác module). | Như CP-3. |
| 6 | **GM-4 `gm-console-extension`** | + `requiredOperations`, bundle `/<gameId>/`, quy tắc deploy release. | Bundle game ở sub-path qua cùng Vite/preview. |
| — | zh-pvp cutover (phía game) | §9 bước 3–4 | Cần CP production + domain cho console/CP (chỉ console + CP cần tên miền) |

Thứ tự: CP-1 → CP-2 → CP-3 → (CP-4 ∥ GM-3) → GM-4. CP-1 trước vì nhỏ, test độc lập, khoá hợp đồng token sớm.

---

## §11. Hiệu năng (F), mở rộng và phần hoãn (G)

**Hiệu năng:** xác minh token ES256 ~ vài chục µs, không I/O. Cấp token: 1 đọc phiên + member + grant (KV, cache ≤ 5 s cho
member/grant tối đa vẫn trong 60 s) — 50 staff × 3 target mở ≈ 3 token/s, không đáng kể. Preflight: mỗi URL GM mới 1 OPTIONS
(cache 7200 s) — chấp nhận cho lưu lượng GM; nếu thành vấn đề → proxy (§4.1). Catalog theo target cache bằng ETag.

| Hoãn | Vì sao | Khi nào |
| --- | --- | --- |
| DPoP (token gắn khoá trình duyệt) | thêm độ phức tạp cả hai phía | sau CP-3 nếu review bảo mật yêu cầu |
| CP làm proxy / GM API chỉ mở cho mạng CP | §4.1 | khi muốn ẩn GM API khỏi internet |
| KMS provider cụ thể (AWS/GCP/Azure/Vault) cho `ISigningKeyProvider` | công ty chưa có KMS (Q30) | **bắt buộc trước khi live dùng CP** (R-KMS chặn) |
| JWKS động cho non-live | pin đủ đơn giản | nếu xoay khoá thường xuyên |
| Cấm Standalone ngoài Dev/Test | đang migration | sau khi mọi game chuyển |
| Nhóm IdP làm điều kiện gate thêm | domain allowlist đủ (Q24) | nếu cần loại nhóm nội bộ |
| Origin preview cho beta/uat | dev console chạy localhost | khi QA cần |

---

## §12. Cần xác minh ([VERIFY])

| # | Nội dung | Slice |
| --- | --- | --- |
| V-C1 | `Microsoft.IdentityModel.JsonWebTokens` (version hiện hành, ES256, `typ` kiểm được) — package mới đầu tiên cho JWT; giữ ngoài project Application (ARC-01) | CP-1 |
| V-C2 | Platform host khởi động được cho một host **không phải game** (CP): `GameId`/`EnvironmentId` bắt buộc → dùng hằng `gm-control-plane`/`company` hay cần nới | CP-2 |
| V-C3 | Trusted Types (`require-trusted-types-for 'script'`) với React 19 + Mantine 9 + build Vite: 0 vi phạm? | CP-3 |
| V-C4 | Khi owner chọn IdP: claim MFA (`amr` hay `acr`, giá trị nào), có `email_verified` không, claim tenant/issuer để khoá tổ chức → điền `Cp:Oidc`. CP-2 chỉ cần chạy đúng với OIDC giả cho cả hai kiểu `amr`/`acr` | CP-2 (code), sau (cấu hình) |
| V-C5 | Static host/CDN định tuyến `/cp/**` tới CP (cùng origin) và không cache response của nó | CP-3 |

---

## §13. Câu hỏi cho owner — ĐÃ TRẢ LỜI HẾT

| # | Trả lời (owner) | Ngày |
| --- | --- | --- |
| Q22 | C — control plane trung tâm | 2026-11-08 |
| Q23 | SSO cho mọi staff; IdP chưa chọn → OIDC chuẩn, chọn sau bằng cấu hình | 2026-11-08 / 2026-11-09 |
| Q24 | Gate kiểm domain email công ty; member mới 0 quyền; admin cấp grant trong CP | 2026-11-08 / 2026-11-09 |
| Q25 | Một origin chung, trang game theo đường dẫn | 2026-11-08 |
| Q28 | Không break-glass, fail closed, chờ khôi phục; local login chỉ Local/Dev | 2026-11-09 |
| Q29 | Live thêm quyền cần admin thứ hai duyệt, người đề xuất xác thực lại; non-live một admin; gỡ/khoá ngay | 2026-11-09 |
| Q30 | Chưa có KMS → v1 secret configuration + khoá live riêng + điểm cắm KMS; KMS bắt buộc trước live (R-KMS, luật khởi động) | 2026-11-09 |
| Q31 | `Cp:AllowedEmailDomains` + đúng 2 `Cp:BootstrapAdmins`, cấu hình sau; test dùng giá trị giả | 2026-11-09 |
| Q32 | Cùng origin: `gm.<domain>/` console, `gm.<domain>/cp/**` → CP; local Vite proxy `/cp` | 2026-11-09 |

Còn chờ từ trước (không chặn CP-1): 018 D2 (xác nhận được luôn với giá trị chung), D3, D4, D10, D14; 017 D9, D15.

---

## §14. Bước kế tiếp

1. **Tag `v0.8.0`** cho 017 + 018 (đã merge `main`, chưa bump/tag theo D15/D18) — zh-pvp bump một mốc ổn định trước khi hợp đồng
   token vào framework.
2. **Spec CP-1 `gm-token-auth`** (speckit-specify, số slice kế tiếp): phạm vi §10 hàng 1 — `GmAuthMode`, `GmControlPlaneOptions` +
   validator, `GmBearerAuthenticationHandler`, `GmAccessTokenContract`, `perm ∩ catalog`, `/me` từ token + `deploymentEnvironment`,
   `gm.permission_catalog`, CORS không credentials, route `staff*`/`auth*` theo mode, `sid` trong audit, TestKit `GmTestTokens`;
   VERIFY V-C1. Không cần CP, không cần domain.
3. Sau CP-1: CP-2 `gm-control-plane` (gate OIDC chuẩn, R-KMS, validator `Cp:AllowedEmailDomains`/`Cp:BootstrapAdmins`) → CP-3 →
   (CP-4 ∥ GM-3) → GM-4 → zh-pvp cutover (cần KMS cho live).
