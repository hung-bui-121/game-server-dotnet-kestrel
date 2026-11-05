# Thiết kế GM tool: GM console (frontend) + GM API — framework shell, game mở rộng

**Ngày:** 2026-11-05 · **Trạng thái:** BẢN SỬA 1 theo quyết định owner 2026-11-05 (bản nháp đầu cùng ngày đã thay hẳn) ·
**Người viết:** system-architect
**Đầu vào:** `Requirements/Game_Server_System_Requirements_v1.2.md` (GM-01..07, SEC-02..15, ACC-45, API-04, CI-01, TBD-05a,
TBD-21), code `main` = `1b3053b` (tag `v0.7.0`), pilot `E:\ProjGits\zh-pvp-server` (framework `v0.7.0`), catalog chức năng GM
legacy `D:\MyProjects\AI\refactor_gmtool\gmtool-v2\docs\` (chỉ đọc).

**Chiến lược đã chốt (không mở lại):** framework sở hữu GM backend base + frontend **shell**; game mở rộng qua **extension
slot**, không kế thừa OOP, không fork template. **Form chung sinh từ GM OpenAPI** cho mọi GM command. Chỉ thứ đã chứng minh
là chung mới vào framework (vòng FG-NNN → slice → tag → game bump). Owner tự làm GM frontend.

**Quyết định owner 2026-11-05:**

| # | Quyết định | Ghi chú |
| --- | --- | --- |
| Q1 / TBD-21 | **React + TypeScript + Vite + Mantine** | khác khuyến nghị đầu (Blazor) |
| Q2 | Chrome/Edge desktop; Firefox best-effort | như đề xuất |
| Q3 | **Console host riêng (CDN / web server)**, không do Kestrel phục vụ | khác khuyến nghị đầu; mở lại giả định slice 007 (same-origin, `SameSite=Lax`, không CORS) → §6 |
| Q5–Q15 | chấp nhận mặc định của architect | Q4 (đường dẫn `/gm/v1/ui/`) không còn ý nghĩa → thay bằng §6.1/D6 |

---

## §0. Tóm tắt

1. **Một nguồn sự thật = GM OpenAPI do host đang chạy sinh ra**, kèm vài extension `x-gm-*`. Không endpoint "menu" riêng:
   nav = `GET /gm/v1/me` (permission + version) × catalog (OpenAPI). Catalog phục vụ ở **mọi environment** cho staff có
   `gm.access` (`GET /gm/v1/console/openapi.json`).
2. **GM-1 (backend contract, không phụ thuộc stack/hosting) làm ngay:** hiện *không GM command nào* có request body, path
   param hay `X-Request-Id` trong OpenAPI; OpenAPI chỉ có ở Development/Test; không `operationId`; list không mô tả
   phân trang. GM-1 sửa hết, thêm DataAnnotations do server kiểm, nguồn lựa chọn động, snapshot OpenAPI cho game.
3. **Hosting: console tĩnh trên host riêng, cùng *site* với GM API** (khuyến nghị, "trường hợp A"): ví dụ
   `https://gm.beta.example.com` → `https://gm-api.beta.example.com`. Khác origin nên cần CORS có credentials, nhưng cùng
   registrable domain nên cookie **vẫn `SameSite=Lax`**, không phụ thuộc chính sách cookie bên thứ ba của trình duyệt.
   Console ở domain khác hẳn (CDN mặc định, "trường hợp B") **không hỗ trợ** — vỡ trên Firefox (Total Cookie Protection).
4. **Thay đổi backend cho cross-origin (GM-2):** tách cấu hình `SameSite` khỏi "CORS bật"; cookie `__Host-` + `Path=/`;
   SSO quay về **origin console** (`Gm:Console:BaseUrl`, sửa luôn 404 `/gm/`); header CORS phải còn trên response lỗi (hiện
   `ErrorResponseWriter` xoá mất); `Access-Control-Expose-Headers`; trang tĩnh `/gm/v1/ui` chỉ còn ở Development/Test.
5. **Đóng gói:** framework ship shell **dạng source** trong `framework/gm-console/` (npm workspace), cùng **app mặc định**.
   **Framework không commit bundle** (console không còn được Kestrel nhúng). Game không có trang riêng: CI của game build
   app mặc định từ source submodule + `config.json` theo env → deploy lên static host. Game có trang riêng: app của game
   trong workspace npm ở root game, tham chiếu shell qua đường dẫn submodule.
6. **Build một lần, deploy nhiều env:** `config.json` (cùng origin console) chứa `apiBaseUrl`, `gameId`, `environmentId`;
   console **chặn** nếu `/me` trả game/env khác (chống trỏ nhầm live/beta). Version skew: chỉ cảnh báo (framework minor
   khác) / thông tin (game version khác); backend vẫn tự bảo vệ.
7. **Codegen:** `openapi-typescript` (type) + `openapi-fetch` (client typed theo path) từ `docs/api/gm-openapi.json` đã commit;
   CI: backend test chứng minh file commit = host, job console sinh lại type + `git diff --exit-code` + `tsc --noEmit`.
8. Slice: **GM-1 `gm-api-contract`** (bắt đầu ngay) → **GM-2 `gm-console-hosting-shell`** (CORS/cookie/SSO + shell + app mặc
   định) → **GM-3 `gm-generic-forms`** → **GM-4 `gm-console-extension`**.

---

## §1. Hiện trạng đã kiểm (và chỗ mâu thuẫn)

| # | Hiện trạng (file) | Hệ quả | Sửa ở |
| --- | --- | --- | --- |
| H1 | `GmCommandEndpointFactory.MapGmCommandCore` map `MapPost(pattern, async (HttpContext http) => …)`, tự đọc body | OpenAPI **không có `requestBody`, path param `playerId`, header `X-Request-Id`** cho mọi GM command (`ban`, `unban`, `deletion/cancel`, `resources/adjust`, `progression`) — đã kiểm `docs/api/gm-openapi.json` | GM-1 |
| H2 | `GmOpenApi.Add` chỉ đăng ký khi `AllowsDevelopmentTools`; route `platform.gm_openapi` Public, Dev/Test | Beta/uat/live không có catalog lúc chạy | GM-1 |
| H3 | Validation body imperative trong lambda `toDomain` | Schema không mang ràng buộc → form không hiện luật; chép sang TS sẽ lệch | GM-1 |
| H4 | Tag = `GameServer.Gm.Tests`/`GmModule`, không `operationId` | Không khoá ổn định, không nhóm menu | GM-1 |
| H5 | search/receipts/audit/ledger đọc `pageSize`/`cursor`/`displayName` thủ công, không `Produces<…>` | Trang list chung không biết tham số (GM-06) | GM-1 |
| H6 | `GmSso.DefaultReturnUrl = "/gm/"`, `DeniedRedirect = "/gm/?error=sso_denied"`, `SafeReturnUrl` chỉ nhận đường dẫn cùng host bắt đầu `/gm/`; không endpoint nào phục vụ `/gm/` | Sau SSO → 404; và với console host riêng, không có cách quay về origin console | GM-2 |
| H7 | `MapGmCommand` bắt buộc `{playerId}` | Thao tác GM toàn cục (legacy: controlsystem, eventgm, giftcode, mailgm broadcast) là endpoint thường + `IAuditedStore`; form chung phải hỗ trợ loại "action" | GM-1 (contract), helper ở slice giftcode |
| H8 | `docs/api/gm-openapi.json` sinh từ test host framework (có route SampleGame, counter) | Game không có cách chuẩn sinh + diff OpenAPI GM của mình (CI-01) | GM-1 |
| H9 | `GmUi` (trang tĩnh `/gm/v1/ui`) CSP `script-src 'unsafe-inline'`, Public ở mọi env | Bề mặt thừa khi đã có console | GM-2 (chỉ Dev/Test) |
| H10 | `GmRoles.Operator` cứng `progression.edit` (permission SampleGame) | Game không mở rộng role template | hoãn (Q11) |
| H11 | `BanBody.Scope`, `AdjustResourceBody.Resource` có giá trị hợp lệ động | Cần `x-gm-options` | GM-1 |
| H12 | `docs/pcr/gm.md` dòng 4–5, `p05-speckit/00_summary.md`, auto-memory `gm-frontend-out-of-scope.md` nói "frontend giao dev khác" | Lỗi thời | GM-1 (docs) |
| H13 | SEC-13 "GM per-IP ≤ 30/phút chặn cứng"; code chỉ có concurrency limiter `gs-gm` (50) | Hiện thực đúng chữ sẽ chặn SPA | Q9 (đã chốt: chỉ endpoint auth) |
| H14 | `ErrorResponseWriter.WriteAsync` gọi `context.Response.Clear()` | Xoá cả header `Access-Control-Allow-*` do CORS middleware đặt trước đó → console cross-origin **không đọc được** Problem Details (trình duyệt báo lỗi CORS thay vì 400/401/403/429/503) | GM-2 |
| H15 | `GmCookies`: `SameSite = Cors.Enabled ? None : Lax` | Bật CORS (bắt buộc cho console host riêng) ép `SameSite=None`, dù trường hợp A dùng được `Lax` an toàn hơn | GM-2 |
| H16 | CORS policy không có `Access-Control-Expose-Headers` | Console không đọc được `X-Request-Id` (correlation), `Retry-After`, `ETag` | GM-2 |

Có sẵn và **đúng cho cross-origin**, giữ nguyên: `CsrfValidation.IsValidAsync` từ chối mutation cookie có `Origin` không
phải same-origin và không nằm trong `Gm:Cors:AllowedOrigins` (chặn cả trang trên subdomain anh em cùng site); `IsCrossSite`
dùng `Sec-Fetch-Site`; CORS exact origin + credentials + validator (không wildcard, https ngoài Development); token CSRF
gắn session (`gmsession:{key}`); ticket server-side; `x-gm-permission`, `x-gm-csrf`; `GmPage<T>` keyset (GM-06);
`GmCommandResponse` thống nhất; `OpenApiTests` so bản commit với host.

Pilot zh-pvp **chưa có GM command nào của game**; dấu vết cúp/súng/rương ở `zhpvp_audit` (FG-017).

---

## §2. Ranh giới framework ↔ game

### 2.1. Backend

| Thành phần | Framework | Game | Lý do |
| --- | --- | --- | --- |
| Auth GM (local dev, OIDC SSO, session, CSRF, cookie, CORS) | ✔ (có; GM-2 chỉnh cho cross-origin) | cấu hình `Gm:*` theo env | Bảo mật đồng nhất, game không nới được. |
| Staff, grant, role template, staff audit | ✔ | cấp quyền qua API | SEC-15. Role template của game: hoãn (Q11). |
| Player lookup/search/view, receipts, audit, ledger | ✔ | — | Module framework. |
| Ops: gate, maintenance, min client version, health, version, key rotate | ✔ | — | Vận hành chung. |
| Ban/unban/huỷ xoá, economy adjust | ✔ | — | Module framework. |
| Catalog GM (OpenAPI + `x-gm-*`) mọi env | ✔ GM-1 | — | Nền shell + form chung. |
| Metadata trình bày (`WithSummary`, `WithTags`, `WithGmConfirm`, `[GmOptions]`, `ProducesGmPage<T>`, `WithGmQueryParameter`) | ✔ GM-1 | dùng khi map route GM | Một cách khai báo. |
| DataAnnotations trên body + server kiểm | ✔ GM-1 trong `MapGmCommand` | gắn attribute | Luật một chỗ. |
| Nguồn lựa chọn của module framework (`economy.resource_options`, `identity.ban_scopes`) | ✔ GM-1 | endpoint lựa chọn của module game | Proven-common (adjust, giftcode, mail). |
| GM command/query của game | — | ✔ `MapGmCommand` / `MapDeclared` | Nghiệp vụ game. |
| Helper thao tác GM toàn cục (`MapGmAction`) | slice giftcode | — | Chưa có caller framework. |
| Snapshot/diff OpenAPI GM của game (CI-01) | ✔ TestKit GM-1 | test + file commit | Game chứng minh contract không đổi ngầm; đầu vào codegen TS. |
| Cấu hình console theo env (`Gm:Console:BaseUrl`, `Gm:Cors:AllowedOrigins`, `Gm:Cookies:SameSite`) | ✔ GM-2 (option + validator) | giá trị theo env | — |

### 2.2. Frontend

| Thành phần | Framework (`framework/gm-console/`) | Game | Lý do |
| --- | --- | --- | --- |
| Shell: đăng nhập local/SSO, đăng xuất, hết phiên, CSRF, env banner, guard game/env, version check | ✔ `packages/shell` | — | Bảo mật + nhất quán. |
| Layout, nav theo permission, router, tìm player toàn cục | ✔ | thêm page/menu | — |
| API client (openapi-fetch + middleware CSRF/request id/Problem Details) | ✔ | dùng, với type sinh từ doc của game | — |
| Trang chung (player, receipts, audit, ledger, staff, ops) | ✔ | — | Tương ứng API framework. |
| Form chung cho mọi operation trong catalog | ✔ | 0 dòng code | Mục tiêu chính. |
| **App mặc định** (không extension) | ✔ `app/` (source, không commit bundle) | CI build + deploy kèm `config.json` | Game không trang riêng vẫn có console. |
| Trang chuyên biệt module framework (giftcode, mail, inventory economy) | ✔ cùng slice module | — | Module framework → UI framework. |
| Trang chuyên biệt của game | — | ✔ app của game | Viết ở game; promote khi game thứ hai cần. |
| Override form chung của một route / tab player | cơ chế ✔ | ✔ dùng | Theo route name. |
| Cấu hình static host (header, CSP, cache, TLS, domain) | tham chiếu ✔ `deploy/` | ✔ áp dụng | Hạ tầng thuộc deploy của game. |

---

## §3. Tổng quan kiến trúc (A)

Console là SPA tĩnh trên host riêng (CDN/web server) cùng site với GM API. Shell đọc `config.json` để biết API, đọc quyền
+ version từ `/me`, catalog từ OpenAPI của host; dựng menu + form chung; game chỉ thêm code khi cần trang riêng (composition
lúc build). Backend quyết định mọi thứ trên từng request.

```
  https://gm.beta.example.com  (static host / CDN)            https://gm-api.beta.example.com  (reverse proxy → Kestrel)
 ┌──────────────────────────────────────────┐               ┌───────────────────────────────────────────────────────┐
 │ index.html · assets/*.js|css (hash)       │               │ CORS (exact origin, credentials) ─ preflight          │
 │ config.json {apiBaseUrl, gameId, envId}   │               │ /gm/v1/auth/methods · /auth/csrf · /auth/sso/*        │
 │ header: CSP, HSTS, nosniff, no-referrer   │               │ /gm/v1/me (perm + version)                            │
 │                                           │  fetch        │ /gm/v1/console/openapi.json (gm.access, ETag)         │
 │  Shell (framework, React+Mantine)         │  credentials  │ /gm/v1/players/* receipts/* staff/* ops/*             │
 │   Session · CSRF · gmClient · Catalog     │◄─────────────►│ /gm/v1/... GM command/query của game                  │
 │   Nav · SchemaForm · built-in pages       │  cookie       │   MapGmCommand → ICommandRunner (receipt + audit)     │
 │  Game extension (tuỳ chọn)                │  __Host-gs_gm │   action → IAuditedStore (gm_audit)                   │
 └──────────────────────────────────────────┘  SameSite=Lax  │        ▼  Couchbase                                   │
          ▲ SSO: top-level redirect về console origin         └───────────────────────────────────────────────────────┘
 Build: framework/gm-console/packages/shell ─┬─► framework/gm-console/app (mặc định)  ─► dist/ (CI game, không commit)
                                             └─► <game>/gm-console (khi có trang riêng) ─► dist/
```

**Kiểu kiến trúc:** SPA thin-client trên JSON API cross-origin same-site, metadata-driven UI, composition lúc build.

---

## §4. Phân rã module (B)

### Backend (C#)

| Module | Trách nhiệm | Phụ thuộc | KHÔNG sở hữu |
| --- | --- | --- | --- |
| `Platform.Web/OpenApi/GmOpenApi` (mở rộng, GM-1) | Sinh tài liệu GM (`operationId`, tag, param, body, ràng buộc, `x-gm-*`), mọi env; cache bytes + ETag. | Microsoft.AspNetCore.OpenApi | Quyền; menu. |
| `Platform.Web/Commands/GmCommandEndpointFactory` (mở rộng, GM-1) | Metadata OpenAPI cho command + kiểm DataAnnotations trước `toDomain`. | `ICommandRunner` | Luật nghiệp vụ. |
| `Platform.Web/Endpoints/GmPresentation` (mới, GM-1) | Metadata: confirm, phân trang, query param, nguồn lựa chọn. | — | Render. |
| `Modules.Gm.Adapters` (GM-1 + GM-2) | `gm.console_catalog`, `gm.auth_methods`, `/me` thêm version (GM-1); `GmConsoleOptions`, cookie, CORS expose, SSO return về console (GM-2). | Platform.Web | Bundle console. |
| `Platform.Web/Errors/ErrorResponseWriter` (GM-2) | Giữ header CORS khi ghi lỗi. | — | Chính sách CORS. |
| `TestKit/GmOpenApiSnapshot` (mới, GM-1) | Ghi/so tài liệu GM của host game với bản commit. | TestHost | — |

### Frontend (`framework/gm-console/`, npm workspace)

| Module | Trách nhiệm | Phụ thuộc | KHÔNG sở hữu |
| --- | --- | --- | --- |
| `packages/shell` → `session` | `config.json`, `/me`, guard game/env, CSRF token trong bộ nhớ, 401/403. | gmClient | Quyền thật. |
| `shell/api` | `createGmClient` (openapi-fetch + middleware: `credentials: 'include'`, `X-CSRF-Token` + `X-Request-Id` cho mutation, Problem Details). | openapi-fetch | Retry tự động command. |
| `shell/catalog` | Parse OpenAPI lúc chạy → `GmOperation` (resolve `$ref`). | — | Lọc quyền. |
| `shell/navigation` | Menu = built-in + extension + operation global, lọc theo `me.permissions` và route có trong catalog. | catalog, registry | — |
| `shell/forms` | SchemaForm + query view (subset §9), confirm, kết quả. | catalog, Mantine form | Validation thật. |
| `shell/pages` | Trang built-in (login, player, receipts, audit, ledger, staff, ops). | api (type sinh từ doc framework) | Trang game. |
| `shell/extensibility` | Registry page / player tab / override. | — | Nội dung extension. |
| `shell/generated` | Type sinh từ `docs/api/gm-openapi.json` của framework (commit). | openapi-typescript | Type của game. |
| `app/` | Entry point mặc định (`createGmConsole({})`). | shell | — |
| `deploy/` | Header tham chiếu (nginx + danh sách cho CDN), `config.example.json`, JSON Schema của `config.json`. | — | Hạ tầng thật. |
| `<game>/gm-console/` (game, tuỳ chọn) | Entry point + page của game + type sinh từ doc của game. | shell (workspace) | Shell. |

Không phụ thuộc vòng: app → shell; shell không biết game; backend không biết frontend.

---

## §5. Backend: contract GM-1 (C)

### 5.1. Extension trên mỗi operation (transformer tự suy ra)

| Khoá | Giá trị | Nguồn |
| --- | --- | --- |
| `operationId` | `RouteDeclaration.Name` (vd `economy.adjust`) | declaration |
| `tags` | `[<area>]` = segment đầu tên route, trừ khi có `WithTags` | declaration |
| `summary` / `description` | `WithSummary`/`WithDescription`; trống → shell hiện route name | module |
| `x-gm-permission`, `x-gm-csrf` | đã có | — |
| `x-gm-kind` | `command` · `action` (mutation khác) · `query` (GET) | metadata + method |
| `x-gm-operation` | tên command | `CommandOperationMetadata` |
| `x-gm-target` | `player` nếu template có `{playerId}`, ngược lại `global` | template |
| `x-gm-confirm` | `simple` (mặc định mutation) · `typed` | `WithGmConfirm` |
| `x-gm-paged` | `true` cho list GM-06 | `ProducesGmPage<T>()` |
| `x-gm-options` (property) | route name của endpoint trả `GmOptionList` | `[GmOptions("…")]` |

Mọi GM command: `requestBody` = schema `TBody` (`reason` required 1..500), path param từ template, header `X-Request-Id`
(uuid, required), `200` = `GmCommandResponse`. Lỗi DataAnnotations = Problem Details `400 BAD_REQUEST` + `errors: { "<jsonName>":
["…"] }`.

### 5.2. Chữ ký đề xuất (chỉ signature)

```csharp
namespace GameServer.Platform.Web.Endpoints;

public enum GmConfirm
{
    Simple,
    Typed,
}

public sealed record GmConfirmMetadata(GmConfirm Confirm);

public sealed record GmPagedMetadata(Type ItemType);

/// <summary>Property values come from the GM GET route named <see cref="RouteName"/> (x-gm-options).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class GmOptionsAttribute : Attribute
{
    public GmOptionsAttribute(string routeName);

    public string RouteName { get; }
}

public sealed record GmOption(string Value, string Label);

public sealed record GmOptionList(IReadOnlyList<GmOption> Items);

public static class GmPresentationExtensions
{
    public static TBuilder WithGmConfirm<TBuilder>(this TBuilder builder, GmConfirm confirm)
        where TBuilder : IEndpointConventionBuilder;

    /// <summary>Produces&lt;GmPage&lt;T&gt;&gt;, required pageSize (1..200) and optional cursor query parameters, GmPagedMetadata.</summary>
    public static TBuilder ProducesGmPage<TBuilder, T>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder;

    /// <summary>Documents a query parameter the handler reads by hand (e.g. displayName of player search).</summary>
    public static TBuilder WithGmQueryParameter<TBuilder>(this TBuilder builder, string name, bool required, int? maxLength = null)
        where TBuilder : IEndpointConventionBuilder;
}
```

```csharp
namespace GameServer.Modules.Gm;

/// <summary>Pre-login answer (Public, allowlist SEC-09): which sign-in buttons the login page shows. Nothing else.</summary>
public sealed record GmAuthMethods(bool Local, bool Sso);

/// <summary>Additive members: versions for the console's compatibility check (§7.5).</summary>
public sealed record GmMeResponse(
    string StaffId,
    string DisplayName,
    string GameId,
    string EnvironmentId,
    IReadOnlyList<string> Permissions)
{
    public string FrameworkVersion { get; init; }

    public string GameVersion { get; init; }
}

// New routes (GmRoutes):
//   gm.auth_methods     GET /gm/v1/auth/methods          Public (allowlist SEC-09)
//   gm.console_catalog  GET /gm/v1/console/openapi.json  Gm, gm.access, every environment, ETag
```

```csharp
namespace GameServer.TestKit;

/// <summary>CI-01 OpenAPI diff for a game: writes or verifies the committed GM document of the game's host.</summary>
public static class GmOpenApiSnapshot
{
    public static Task WriteAsync(TestHost host, string committedPath, CancellationToken cancellationToken);

    /// <summary>Empty when equal; otherwise the first differing line, so the test message names what changed.</summary>
    public static Task<string> DiffAsync(TestHost host, string committedPath, CancellationToken cancellationToken);
}
```

Thay đổi hành vi:

- `MapGmCommandCore`: `.Accepts<TBody>("application/json")`, mô tả path param + header; sau deserialize chạy
  `Validator.TryValidateObject(…, validateAllProperties: true)` → 400 + `errors`. Không annotation → không đổi hành vi.
- `GmOpenApi` đăng ký mọi env; route dev Public cũ giữ cho tooling; `gm.console_catalog` trả **cùng tài liệu**, không lọc
  quyền. Tài liệu **không** chứa version (để snapshot không đổi mỗi release) — version nằm ở `/me`.
- Schema transformer: `IGmCommandBody.Reason` → required/minLength 1/maxLength 500; DataAnnotations → keyword JSON Schema
  (V1); `[GmOptions]` → `x-gm-options`.
- Endpoint lựa chọn: `economy.resource_options` `GET /gm/v1/economy/resources` (`player.read`), `identity.ban_scopes`
  `GET /gm/v1/players/ban-scopes` (`player.ban`); `[GmOptions]` trên `AdjustResourceBody.Resource`, `BanBody.Scope`.
- Annotate route framework: `ProducesGmPage` cho search/receipts/audit/ledger/staff list, `WithGmQueryParameter("displayName")`,
  `WithGmConfirm(Typed)` cho ban, cancel deletion, rotate key, maintenance, economy adjust (Q7); summary cho mọi route.

---

## §6. Hosting cross-origin và bảo mật (GM-2)

### 6.1. Bố trí domain — hai trường hợp

"Site" = scheme + registrable domain (eTLD+1, theo Public Suffix List). Cookie `SameSite` xét **site**, CORS xét **origin**.

| | **A. Cùng site, khác origin (KHUYẾN NGHỊ)** | **B. Khác site (domain khác hẳn / domain mặc định CDN)** |
| --- | --- | --- |
| Ví dụ | `https://gm.beta.example.com` → `https://gm-api.beta.example.com` | `https://zhpvp-gm.cdnvendor.net` → `https://gm-api.beta.example.com` |
| Request console → API | `Sec-Fetch-Site: same-site` | `cross-site` (ngữ cảnh bên thứ ba) |
| Cookie phiên | `SameSite=Lax` được gửi trên fetch same-site → giữ `Lax` | Bắt buộc `SameSite=None; Secure` = cookie bên thứ ba |
| Chrome/Edge | ✔ | phụ thuộc chính sách cookie bên thứ ba (người dùng/doanh nghiệp chặn được, Incognito chặn) |
| Firefox (best-effort) | ✔ | ✘ mặc định: Total Cookie Protection phân vùng cookie theo site top-level → cookie đặt lúc SSO (top-level trên host API) không có trong vùng của console → 401 lặp (V7) |
| Kết luận | hỗ trợ | **không hỗ trợ trong GM-2**; nếu bắt buộc cần thiết kế riêng (đổi mã một lần + cookie `Partitioned`/CHIPS, hoặc bearer token) — hoãn |

Lưu ý: domain mặc định của CDN/PaaS (`*.cloudfront.net`, `*.azurewebsites.net`, `*.azureedge.net`, `*.pages.dev`,
`*.web.app`, `*.github.io`…) nằm trong Public Suffix List → hai subdomain của chúng là **khác site**. Console phải dùng
**custom domain + TLS** dưới domain công ty. Mặc định đề xuất (Q16): live `gm.<domain>` → `gm-api.<domain>`; env khác
`gm.<env>.<domain>` → `gm-api.<env>.<domain>`; `gm-api.*` là hostname riêng trỏ cùng deployment, reverse proxy chỉ chuyển
`/gm/**` (ops có thể giới hạn IP/VPN cho GM mà không chạm player API).

### 6.2. Cookie (GM-2)

| Thuộc tính | Hiện tại (007) | GM-2 |
| --- | --- | --- |
| Tên | `gs_gm`, `gs_gm_csrf` | `__Host-gs_gm`, `__Host-gs_gm_csrf` (chống cookie tossing từ subdomain anh em; prefix buộc `Secure`, `Path=/`, không `Domain`) — Q20 |
| `Domain` | không đặt (host-only) | không đặt: cookie chỉ thuộc host GM API; console **không bao giờ** cần đọc/gửi nó tới host khác |
| `Path` | `/gm` | `/` (bắt buộc bởi `__Host-`); cookie có thể đi kèm request `/api/**` cùng host nhưng `PlayerBearer` bỏ qua (SEC-02 không đổi) |
| `SameSite` | `Cors.Enabled ? None : Lax` | cấu hình riêng `Gm:Cookies:SameSite` = `Lax` (mặc định, trường hợp A) \| `None` (chỉ khi CORS bật; validator cảnh báo "trường hợp B không hỗ trợ Firefox") |
| `Secure` | bỏ được chỉ ở Development HTTP | giữ; Development `http://localhost`: không dùng prefix `__Host-` (cookie tên cũ) vì prefix bắt buộc `Secure` |
| `HttpOnly` | ✔ | ✔ |
| OIDC correlation/nonce | `Path=/gm/v1/auth/sso/callback`, `Lax` | không đổi (callback là điều hướng top-level) |

### 6.3. CORS (GM-2)

- `Gm:Cors:AllowedOrigins` theo env = **đúng một** origin console của env đó (`https://gm.beta.example.com`); exact match,
  https ngoài Development (validator có sẵn). Không wildcard, không danh sách chung cho mọi env.
- `AllowCredentials`; methods `GET, POST, PUT, DELETE`; allowed headers `Content-Type, X-CSRF-Token, X-Request-Id`;
  **thêm** `Access-Control-Expose-Headers: X-Request-Id, Retry-After, ETag`; `Access-Control-Max-Age` 600 s → 7200 s
  (giảm preflight; Chrome giới hạn 7200).
- **Header CORS phải có trên mọi response lỗi** (H14): `ErrorResponseWriter` giữ lại `Access-Control-*` + `Vary` khi
  `Clear()`; áp cho lỗi từ endpoint, `ProfileEnforcementMiddleware` (401/403), gate (503), rate limiter (429), timeout
  (503) (V2).
- Preflight không xác thực (đã đúng: CORS trước authentication). Shell **không** gửi header tuỳ biến trên GET → GET là
  simple request, không preflight; mutation (JSON + header) preflight một lần mỗi 2 giờ/route.
- CORS không phải authentication (SEC-06): quyền và CSRF vẫn kiểm như cũ.

### 6.4. CSRF (không đổi cơ chế, đổi luồng client)

1. Sau khi có phiên (SSO quay về hoặc local login) và sau logout: `GET {api}/gm/v1/auth/csrf` với `credentials: 'include'`
   → JSON `{ token }` (đọc được nhờ CORS) + cookie `__Host-gs_gm_csrf`.
2. Token giữ trong bộ nhớ (không storage); mọi request không phải GET gửi `X-CSRF-Token`.
3. Server: `IsCrossSite` (Sec-Fetch-Site) → `Origin` phải là same-origin hoặc ∈ allowlist → antiforgery token bound
   session. Trường hợp A: một trang trên subdomain anh em (`same-site`) bị chặn ở bước Origin; không đọc được token vì CORS.
4. 400 antiforgery → shell lấy lại token một lần rồi báo lỗi (không lặp).

### 6.5. SSO và return URL (GM-2, sửa H6)

- Cấu hình mới `Gm:Console:BaseUrl` (vd `https://gm.beta.example.com/`); validator: origin của nó ∈ `Gm:Cors:AllowedOrigins`,
  https ngoài Development.
- Console bắt đầu SSO bằng **điều hướng top-level** (không fetch): `location.assign({api}/gm/v1/auth/sso/start?returnUrl=
  <đường dẫn tương đối của console, mã hoá, vd %2F%23%2Fplayers%2Fabc>)`.
- `SafeReturnUrl` mới: chỉ nhận **đường dẫn tương đối** (bắt đầu `/`, không `//`, không `\`, ký tự in được, ≤ 512); đích =
  `BaseUrl` + đường dẫn. Không bao giờ nhận URL tuyệt đối từ query (open redirect). Thiếu/không hợp lệ → `BaseUrl`.
- `DeniedRedirect` = `BaseUrl` + `#/login?error=sso_denied`. Khi `Gm:Console:BaseUrl` không cấu hình (local không có
  console): giữ hành vi cũ nhưng trỏ `/gm/v1/ui` (trang tĩnh Dev).
- Callback đặt cookie phiên trên host API trong ngữ cảnh top-level → redirect 302 về console → console fetch `/me` (same-site,
  cookie `Lax` được gửi).
- Logout: `POST /gm/v1/auth/logout` (CSRF) → server xoá ticket + `Set-Cookie` hết hạn (trình duyệt nhận trên response CORS
  credentialed, V4) → console về `#/login`.

### 6.6. Console static host: header và CSP (trách nhiệm của host tĩnh)

Framework ship `gm-console/deploy/` (nginx snippet + bảng header cho CDN). Header phải đặt ở **response HTTP** (thẻ `<meta>`
không hỗ trợ `frame-ancestors`):

```
Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' https://gm-api.beta.example.com;
  img-src 'self' data:; font-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'
Strict-Transport-Security: max-age=31536000
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
Cross-Origin-Opener-Policy: same-origin
Permissions-Policy: camera=(), microphone=(), geolocation=()
Cache-Control: no-cache                                    (index.html, config.json)
Cache-Control: public, max-age=31536000, immutable         (assets/* có hash)
```

- `connect-src` = `'self'` (đọc `config.json`) + đúng `apiBaseUrl` của env → CSP **theo env**, cùng nguồn với `config.json`.
- `style-src 'self'`: Mantine chèn thẻ `<style>` cho CSS variables → cấu hình để biến nằm trong file CSS tĩnh, hoặc nới
  `style-src 'self' 'unsafe-inline'` (chỉ style, script vẫn chặt) — V3 quyết định khi dựng.
- Không CDN bên thứ ba, không font Google, không analytics; Vite build không inline script (V3).
- Hash routing (`/#/players/abc`) → host tĩnh không cần rewrite SPA.

### 6.7. Dev loop

`run-local` (Kestrel `http://localhost:5080`) + `npm run dev` (Vite `http://localhost:5173`); flavor local đặt
`Gm:Cors:AllowedOrigins=["http://localhost:5173"]`, `Gm:Console:BaseUrl=http://localhost:5173/`; `config.json` dev trỏ
`http://localhost:5080`. `localhost` khác port vẫn cùng site → chạy **đúng đường cross-origin của production** (CORS,
CSRF, Lax), không dùng proxy Vite.

---

## §7. Đóng gói, extension, nâng cấp, version skew

### 7.1. Cấu trúc framework

```
framework/gm-console/
  package.json            workspace root (dev framework): ["packages/*", "app"], engines node, packageManager npm
  .nvmrc                  Node LTS hiện hành (V8)
  packages/shell/         @gameserver/gm-shell — TS source (không prebuild), peerDependencies cố định version:
                          react, react-dom, @mantine/core|hooks|form, @tanstack/react-query, react-router, openapi-fetch
  app/                    @gameserver/gm-console-default — Vite app: main.tsx = createGmConsole({})
  deploy/                 nginx.conf.example, headers.md, config.example.json, config.schema.json
```

Framework CI thêm job Node: `npm ci`, codegen + diff, lint, `tsc --noEmit`, Vitest, `vite build` (chỉ kiểm, **không commit
`dist/`**). `dotnet build` của framework và game không cần Node.

### 7.2. Game không có trang riêng (tầng 0)

Job console trong CI của game: `npm ci --prefix framework/gm-console` → `npm run build -w app` → `dist/` + `config.json`
của env (`apiBaseUrl`, `gameId`, `environmentId`) + header theo `deploy/` → upload lên static host. App mặc định không chứa
gì riêng của game: mọi GM command/query của game hiện qua form chung từ catalog lúc chạy. Node chỉ có trong job này.
(Q18: framework phát hành sẵn zip theo tag là phương án sau, khi nhiều game muốn bỏ Node.)

### 7.3. Game có trang riêng (tầng 1)

```
<game>/package.json        { "private": true, "workspaces": ["gm-console", "framework/gm-console/packages/shell"] }
<game>/package-lock.json   lockfile của game
<game>/gm-console/         Vite app: main.tsx = createGmConsole({ extensions: [zhpvpExtension] })
                           src/generated/gm-api.d.ts  ← sinh từ <game>/docs/api/gm-openapi.json (commit)
```

Workspace npm ở root game gom shell (đường dẫn submodule) + app → một bộ `node_modules`, dependency của shell được hoist
(tránh lỗi `file:` symlink thiếu dependency) (V9).

Contract extension (TypeScript — stack đã chốt; chỉ type):

```ts
export interface GmConsoleExtension {
  id: string;
  register(registry: GmConsoleRegistry): void;
}

export interface GmConsoleRegistry {
  addPage(page: GmPageDef): void;
  addPlayerTab(tab: GmPlayerTabDef): void;
  /** Replaces the generic form / query view of one catalog operation. */
  overrideOperation(routeName: string, component: React.ComponentType<GmOperationViewProps>): void;
}

export interface GmPageDef {
  id: string;
  title: string;
  group: string;
  path: string;                 // hash route under #/x/<path>
  requiredPermission: string;   // UX only; the API still checks
  requiredRoutes?: string[];    // hidden when a route is missing from the runtime catalog
  component: React.ComponentType<GmPageProps>;
  order?: number;
}

export interface GmPlayerTabDef extends Omit<GmPageDef, 'path' | 'group' | 'component'> {
  component: React.ComponentType<GmPlayerTabProps>;
}

export interface GmPageProps { api: GmClient; me: GmMe; catalog: GmCatalog; }
export interface GmPlayerTabProps extends GmPageProps { playerId: string; }
export interface GmOperationViewProps extends GmPageProps { operation: GmOperation; playerId?: string; }

export interface GmConsoleConfig { apiBaseUrl: string; gameId: string; environmentId: string; }

export interface GmMe {
  staffId: string; displayName: string; gameId: string; environmentId: string;
  permissions: ReadonlySet<string>; frameworkVersion: string; gameVersion: string;
}

export type GmOperationKind = 'command' | 'action' | 'query';

export interface GmOperation {
  routeName: string; method: string; template: string; kind: GmOperationKind; permission: string;
  target: 'player' | 'global'; group: string; summary?: string; confirm: 'none' | 'simple' | 'typed';
  paged: boolean; bodySchema?: JsonSchema; parameters: GmParameter[];
}

export interface GmCatalog { operations: ReadonlyMap<string, GmOperation>; }

/** Typed client = openapi-fetch Client<paths> + shell middleware (credentials, CSRF, request id, Problem Details). */
export type GmClient<Paths extends {} = FrameworkPaths> = import('openapi-fetch').Client<Paths>;

export interface GmProblem {
  status: number; code: string; codeNumber: number; detail?: string; retryable: boolean;
  errors: Record<string, string[]>; requestId?: string;
}

export declare function createGmConsole(options: { extensions?: GmConsoleExtension[] }): void;
```

Trang built-in của shell đăng ký qua **cùng** registry (dog-food).

### 7.4. Nâng cấp khi game bump tag framework

1. Bump submodule lên tag mới (C# như hiện nay).
2. Backend test `GmOpenApiSnapshot` đỏ nếu route framework đổi → chạy chế độ ghi, review diff, commit
   `docs/api/gm-openapi.json` của game.
3. `npm install` ở root game (lockfile cập nhật nếu peer dependency của shell đổi — release note liệt kê).
4. Sinh lại type, `tsc --noEmit`: thay đổi breaking của shell hoặc của API mà trang game dùng → lỗi compile.
5. Deploy **server trước, console sau** từ cùng một commit.

Release note framework thêm mục "GM console shell" (breaking + dependency đổi). Shell không có version riêng: version =
tag framework.

### 7.5. Version skew (console deploy riêng)

- Form chung và menu đọc catalog **lúc chạy** → luôn khớp server.
- Trang built-in/trang game được compile với một bản tài liệu → có thể lệch trong khoảng giữa hai lần deploy. Build nhúng
  `builtFor = { frameworkVersion, gameVersion }` (framework version từ `package.json` của shell = tag; game version từ CI).
- Sau `/me`:
  - `gameId`/`environmentId` ≠ `config.json` → **chặn** (màn hình lỗi "console trỏ sai server") — chống thao tác nhầm env.
  - framework `major.minor` khác → **banner cảnh báo** "console build cho framework X, server Y; một số trang có thể lỗi".
  - game version khác → dòng thông tin ở footer.
- Không chặn vì version: backend vẫn từ chối an toàn (400/404) khi shape lệch; `/gm/v1` chỉ thay đổi additive trong một
  version (PCR), breaking ghi release note.

---

## §8. Codegen client TypeScript

- Công cụ: **`openapi-typescript`** (sinh `paths`/`components`/`operations` dạng type, không runtime; hỗ trợ OpenAPI 3.1 —
  tài liệu hiện là `3.1.1`) + **`openapi-fetch`** (client ~6 KB typed theo `paths`, cùng tác giả).
- Gọi API có type theo **path**: `api.POST('/gm/v1/players/{playerId}/ban', { params: { path: { playerId } }, body })` — đổi
  template/shape → lỗi compile sau khi sinh lại. Route name chỉ dùng cho catalog lúc chạy (form chung, override).
- Hai bộ type: shell dùng type từ tài liệu framework (`packages/shell/src/generated/`); app của game dùng type từ tài liệu
  game (superset). Trang built-in ẩn khi route không có trong catalog (game không đăng ký module đó).
- CI (framework và game), theo thứ tự:
  1. Backend test: `GmOpenApiSnapshot.DiffAsync` = rỗng (file commit = host).
  2. Job console: `openapi-typescript docs/api/gm-openapi.json -o <generated>/gm-api.d.ts` → `git diff --exit-code`
     (type commit = file commit) → `tsc --noEmit` → test → build.
  Chuỗi này chứng minh client ↔ tài liệu ↔ server.
- `int64` (xp, amount) sinh thành `number` → mất chính xác trên 2^53 (V6): editor giữ dạng chuỗi; giá trị thực tế của game
  nằm dưới ngưỡng — ghi nhận, không xử lý trong GM-1.

---

## §9. Form chung sinh từ OpenAPI (GM-3)

### 9.1. Phạm vi

| Loại operation | Render |
| --- | --- |
| `command`, `target=player` | Trang player, tab "Actions", nhóm theo tag; `playerId` lấy từ ngữ cảnh. |
| `command`/`action`, `target=global` | Menu bên, nhóm theo tag. |
| `query`, `target=player` | Tab "Views": form tham số + bảng (nếu `x-gm-paged`) hoặc JSON viewer. |
| `query`, `target=global` | Menu bên, cùng cách render. |

### 9.2. Subset schema (ngoài subset → editor JSON thô + hiện schema)

`string` (`maxLength`, `pattern`, `format: date-time` → chọn giờ **UTC**, `uuid`); `integer`/`number` (min/max; `int64`
nhập dạng chuỗi); `boolean`; `enum`; nullable = tuỳ chọn ("trống = không đổi"); `x-gm-options` (Select, cache theo phiên);
mảng primitive/enum (MultiSelect); **mảng object phẳng** (bảng dòng — reward list); object lồng 1 cấp. Không hỗ trợ:
`oneOf`/`anyOf`, dictionary, lồng sâu > 2, upload file. `reason` luôn tách cuối form: Textarea bắt buộc 1..500. Renderer tự
viết trên `@mantine/form` (không RJSF): subset nhỏ, kiểm soát UX confirm/reason.

### 9.3. Thao tác nguy hiểm

- Mọi mutation: hộp xác nhận tóm tắt env (`live` đỏ), game, player, route name, field đã điền, reason.
- `typed`: gõ lại `playerId` (player) hoặc route name (global). Danh sách Q7.
- Nút chỉ hiện khi có permission (UX); 403 → làm mới `/me`.
- Không đưa mapping key (PII) lên URL/hash; chỉ hiển thị cái server trả.

### 9.4. Idempotency và kết quả

- `X-Request-Id` sinh khi mở hộp xác nhận, **giữ** khi gửi lại sau lỗi mạng/timeout/`retryable`, sinh mới khi sửa form sau
  câu trả lời dứt khoát. Không retry tự động.
- Hiện `code`/`codeNumber`; HTTP 200 + `code ≠ OK` = cảnh báo + `details`; `replayed`; link receipt; `result`/`updates`/
  `unavailable` bằng JSON viewer (text node). `COMMIT_UNKNOWN_RETRY` → hướng dẫn xem receipts trước khi gửi lại.
- Lỗi mạng không đọc được status (CORS) = "không xác định kết quả" → cùng hướng dẫn như trên.

---

## §10. Luồng dữ liệu (D)

**Trạng thái ở đâu:** server giữ mọi thứ có thẩm quyền (ticket `gm_sessions`, grant `gm_staff`, receipt, audit). Trình duyệt
chỉ giữ trong bộ nhớ: config, `GmMe`, catalog, CSRF token, form đang nhập, cache lựa chọn. `localStorage` chỉ tuỳ chọn UI.
Cookie phiên thuộc host API, JS không đọc.

1. **Mở console:** static host trả `index.html` + asset → `GET /config.json` (cùng origin) → `GET {api}/gm/v1/auth/methods`
   → `GET {api}/gm/v1/me` (credentials): 200 → bước 3; 401 → login.
2. **Đăng nhập:** SSO = điều hướng top-level tới host API (§6.5) → quay về `BaseUrl#…`. Local (Development) =
   `GET /auth/csrf` → `POST /auth/login` (CSRF, CORS). Sau đó `GET /auth/csrf` lại.
3. **Guard + nav:** so `/me` với config (§7.5) → song song catalog (`If-None-Match`) → menu/tab theo permission + route có thật.
4. **Command:** form → confirm (request id) → preflight (cache) → `POST {api}/…` (`X-CSRF-Token`, `X-Request-Id`, JSON) →
   server: CORS → CSRF/Origin → permission → DataAnnotations → `toDomain` → target validator → `ICommandRunner` → kết quả.
5. **Trang game:** component nhận `api` (typed theo doc game) + `playerId` → GET → render.
6. **Hết phiên / mất quyền:** 401 → login, giữ hash để quay lại; 403 → báo + làm mới `/me` (SEC-10 ≤ 60 s).

---

## §11. TBD-21 — ĐÃ QUYẾT 2026-11-05

| Hạng mục | Quyết định | Ghi chú |
| --- | --- | --- |
| Stack | **React + TypeScript + Vite + Mantine** (owner) | Mantine bản major hiện hành (V8) |
| Thư viện đi kèm (mặc định architect, đổi được trong GM-2) | TanStack Query, React Router (hash router), `@mantine/form`, openapi-typescript + openapi-fetch, Vitest + Testing Library, ESLint | Không thêm state manager (Query + context đủ) |
| Trình duyệt | Chrome/Edge 2 bản mới nhất, desktop ≥ 1280 px; Firefox mới nhất best-effort; không Safari/mobile (owner) | Firefox chỉ chạy được ở trường hợp A (§6.1) |
| Hosting | **Static host riêng (CDN/web server)**, cùng site với GM API (trường hợp A) (owner + khuyến nghị A) | Domain cụ thể: Q16 |

Requirement cần sửa theo (file `Requirements/` của owner, gitignored — làm trong GM-2): TBD-05a (mở lại → chốt "console host
riêng, cùng site, CORS allowlist theo env"), SEC-04 (Lax giữ được khi khác origin cùng site; `__Host-`), SEC-06, TBD-21 (Chốt
2026-11-05).

---

## §12. Bất biến bảo mật frontend

1. **Backend quyết định mọi thứ** (GM-05/06, SEC-07); ẩn menu/nút chỉ là UX.
2. **Cookie** `HttpOnly`, `Secure`, host-only, `SameSite=Lax` (trường hợp A); JS không đọc. Không token/session id trong
   `localStorage`/`sessionStorage`/IndexedDB.
3. **CSRF** (SEC-05): token trong bộ nhớ, `X-CSRF-Token` trên mọi request không phải GET; logout POST; kiểm `Origin` và
   `Sec-Fetch-Site` giữ nguyên.
4. **CORS** (SEC-06): một origin console chính xác mỗi env, credentials, không wildcard; CORS không phải auth.
5. **CSP chặt trên static host** (§6.6): `script-src 'self'`, `connect-src` chỉ `'self'` + API của env, `frame-ancestors
   'none'`; không CDN/analytics bên thứ ba; bundle không chứa secret (SEC-11) — chỉ `config.json` công khai (URL + id).
6. **Encode theo ngữ cảnh** (SEC-12): không `dangerouslySetInnerHTML` với dữ liệu server (lint rule cấm); JSON viewer render
   text.
7. **PII** (GM-02): chỉ hiện cái server trả; không lưu dữ liệu player bền; mapping key không lên URL; không log body response.
8. **Reason + request id** cho mọi mutation; không retry tự động command.
9. **Xác nhận** mọi mutation, gõ lại cho `typed`; banner đỏ cố định ở `live`; **chặn** khi `/me` khác game/env của config.
10. **Hết phiên/thu hồi** (SEC-10): 401 → login; không vòng lặp tự đăng nhập.
11. **SSO return** chỉ về `Gm:Console:BaseUrl` + đường dẫn tương đối (không open redirect).
12. Local login chỉ hiện khi `auth/methods.local = true`.

---

## §13. Quyết định thiết kế (E)

**D1. Catalog = OpenAPI của host, không endpoint menu riêng.** → *Lý do:* một nguồn, đã có `x-gm-permission` + test so bản
commit. → *Thay thế:* DTO catalog riêng. → *Đánh đổi:* frontend resolve `$ref`.

**D2. Catalog lúc chạy ở mọi env (`gm.access`).** → *Lý do:* game thêm `MapGmCommand` → deploy server → form xuất hiện, kể cả
khi console là app mặc định không rebuild; quan trọng hơn khi console deploy riêng. → *Thay thế:* chỉ type lúc build. →
*Đánh đổi:* staff `gm.access` thấy toàn bộ mặt API GM (Q5 chấp nhận).

**D3. DataAnnotations là luật input khai báo, server kiểm.** → *Lý do:* form và server cùng nguồn. → *Đánh đổi:* luật nhiều
field chỉ báo khi gửi.

**D4. Composition lúc build, hai tầng (app mặc định / app game), shell là source trong workspace.** → *Lý do:* khớp
submodule + tag; compiler bắt lỗi khi bump; không cần npm registry. → *Thay thế:* runtime plugin (CSP, version skew),
fork template (drift), publish npm package (thêm registry + quy trình). → *Đánh đổi:* game có trang riêng phải quản lý
workspace npm.

**D5. Gọi API có type theo path (openapi-fetch); route name chỉ cho catalog/override.** → *Lý do:* công cụ chuẩn, compile-time
bắt lệch template/shape; không cần generator tự viết. → *Thay thế:* gọi theo route name qua catalog (bản đầu) — chỉ bắt lỗi
lúc chạy. → *Đánh đổi:* URL xuất hiện trong code trang, nhưng có type.

**D6. Console ở root static host, hash routing.** → *Lý do:* host tĩnh không cần rewrite; SSO return là đường dẫn tương
đối. → *Đánh đổi:* URL có `#`.

**D7. Không `MapGmAction` lúc này.** → *Lý do:* chưa có caller framework; giftcode thiết kế theo contract §5.1.

**D8. `gm.auth_methods` Public.** → *Lý do:* tường minh, test được. → *Đánh đổi:* một dòng allowlist SEC-09.

**D9. Trường hợp A (cùng site), không hỗ trợ B.** → *Lý do:* giữ `SameSite=Lax`, chạy trên Firefox, không phụ thuộc chính
sách cookie bên thứ ba. → *Thay thế:* B với `SameSite=None` (vỡ Firefox), B với CHIPS/bearer (thiết kế lại SEC-04). →
*Đánh đổi:* cần custom domain + TLS cho static host.

**D10. `SameSite` tách khỏi "CORS bật"; cookie `__Host-`.** → *Lý do:* CORS bắt buộc khi khác origin nhưng `Lax` vẫn dùng được
cùng site; `__Host-` chặn cookie tossing từ subdomain anh em. → *Đánh đổi:* `Path=/` (cookie GM đi kèm request player cùng
host, vô hại); đổi tên cookie = đăng xuất mọi phiên một lần khi deploy GM-2.

**D11. Build một lần, `config.json` theo env, guard game/env.** → *Lý do:* một artifact cho mọi env; chống trỏ nhầm live. →
*Thay thế:* biến môi trường lúc build (mỗi env một build). → *Đánh đổi:* thêm một file cấu hình phải deploy đúng.

**D12. Framework không commit bundle; game build từ source submodule.** → *Lý do:* không còn Kestrel nhúng; build tái tạo từ
tag đã pin; không file sinh lớn trong git. → *Thay thế:* release asset theo tag (Q18). → *Đánh đổi:* Node trong job console
của game.

**D13. Version skew: chặn chỉ khi sai game/env, còn lại cảnh báo.** → *Lý do:* backend tự bảo vệ; chặn theo version làm hỏng
vận hành khi deploy lệch vài phút. → *Đánh đổi:* trang compile cũ có thể lỗi 400 trong khoảng lệch.

---

## §14. Hiệu năng (F)

- Catalog: sinh một lần sau start, cache bytes + `ETag`; client gửi `If-None-Match`. Ước tính 50–200 KB, nén.
- Asset hash + `immutable`; `index.html`/`config.json` `no-cache`. Bundle mục tiêu ≤ 500 KB gzip (React + Mantine + Query).
- Preflight: chỉ mutation, cache 7200 s; GET không header tuỳ biến → không preflight.
- Màn player: 4–6 GET song song; tab extension tải lười. Concurrency limiter `gs-gm` (50) đủ; per-IP 30/phút chỉ endpoint
  auth (Q9).
- List luôn `pageSize` (console mặc định 50, tối đa 200), "Tải thêm" theo cursor.

---

## §15. Điểm mở rộng và phần hoãn (G)

**Điểm mở rộng:** `GmConsoleExtension` (page, player tab, override theo route name); `[GmOptions]` + route lựa chọn;
`WithGmConfirm`; tag/summary OpenAPI.

| Hoãn | Vì sao | Khi nào |
| --- | --- | --- |
| `MapGmAction` | chưa có caller framework | slice giftcode |
| Trang giftcode, mail đính kèm, event | module chưa có | cùng slice module |
| Viewer inventory/wallet đẹp | JSON viewer đủ | FG từ pilot |
| Role template của game (H10) | không chặn console | FG (Q11) |
| Trường hợp B (khác site) | §6.1 | khi có yêu cầu bắt buộc |
| Release asset console theo tag | build từ source đủ cho 1 game | Q18 |
| E2E Playwright console ↔ host | cần shell ổn định | GM-3/GM-4 |
| i18n, theme theo game, thao tác hàng loạt | chưa có yêu cầu | — |

---

## §16. Phân kỳ — slice Spec Kit

Số slice = "next free number" lúc bắt đầu; nhãn GM-1..4 để tham chiếu.

| Slice | Phạm vi framework | zh-pvp |
| --- | --- | --- |
| **GM-1 `gm-api-contract`** — backend, không phụ thuộc stack/hosting, **bắt đầu ngay** | Xem danh sách chốt ở cuối file (§19). | Bump; test snapshot + commit `docs/api/gm-openapi.json` của game; thêm `gm.auth_methods` vào allowlist nếu cấu hình game liệt kê; (tuỳ owner) GM command đầu tiên của game có DataAnnotations; ghi FG nếu vướng. |
| **GM-2 `gm-console-hosting-shell`** | Phần backend: `GmConsoleOptions` (`BaseUrl`), `Gm:Cookies:SameSite`, cookie `__Host-`, CORS expose + max-age, header CORS trên response lỗi (H14), SSO return về console (H6), validator, `gm.ui` chỉ Dev/Test, sửa requirement TBD-05a/SEC-04/SEC-06/TBD-21, PCR. Phần frontend: workspace `gm-console/`, shell (session, client, catalog, nav, guard, version check, login local/SSO), trang built-in **chỉ đọc** + ops/staff đọc, app mặc định, codegen + CI Node, `deploy/` header tham chiếu, dev loop. Spec Kit có thể chia 2 phase (backend trước, kiểm bằng integration test có `Origin`). | Bump; cấu hình `Gm:Console`/`Gm:Cors` theo env; job CI build app mặc định + deploy static host (dev trước); thử SSO ở beta khi có IdP. |
| **GM-3 `gm-generic-forms`** | SchemaForm + query view, confirm/typed, request id/retry, kết quả; ops writes + staff management. | Bump; **kiểm lời hứa chính**: GM command của game có form, 0 dòng frontend. |
| **GM-4 `gm-console-extension`** | Registry công khai (page, player tab, override), template `templates/gm-console` (root `package.json` workspace + app), ví dụ SampleGame, tài liệu "thêm trang GM". | Chỉ khi cần: `gm-console/` của game với trang đầu tiên (vd viewer `zhpvp_audit`); FG "promote khi game thứ hai cần". |
| Sau | giftcode (`MapGmAction` + trang), MSG-01 (mail), GM-05 (event) | theo bump |

---

## §17. Câu hỏi

### Đã chốt 2026-11-05

Q1 React + TS + Vite + Mantine · Q2 Chrome/Edge desktop, Firefox best-effort · Q3 console host riêng · Q5 catalog mọi
staff `gm.access`, không lọc · Q6 thiếu quyền → ẩn · Q7 typed confirm: ban, huỷ xoá, rotate key, maintenance, economy
adjust; `live` banner đỏ · Q8 server kiểm DataAnnotations · Q9 per-IP 30/phút chỉ endpoint auth · Q10 promote: module
framework → framework, game → khi game thứ hai cần · Q11 role template game hoãn · Q12 `gm.auth_methods` Public · Q13 GM-1
trước giftcode · Q14 Mantine · Q15 tách GM-2/GM-3. Q4 (`/gm/v1/ui/`) không còn ý nghĩa.

### Mới (không chặn GM-1)

| # | Câu hỏi | Mặc định |
| --- | --- | --- |
| Q16 | Bố trí domain | Trường hợp A: live `gm.<domain>` → `gm-api.<domain>`; env khác `gm.<env>.<domain>` → `gm-api.<env>.<domain>`; một registrable domain công ty (không thuộc PSL); CDN dùng custom domain + TLS; `gm-api.*` hostname riêng, proxy chỉ chuyển `/gm/**` |
| Q17 | Trang tĩnh `/gm/v1/ui` | GM-2 giới hạn Development/Test; xoá khi console ổn định |
| Q18 | Game không trang riêng lấy console thế nào | CI game build app mặc định từ source submodule (Node chỉ trong job console); release asset theo tag để sau |
| Q19 | Chính sách version skew | Chặn chỉ khi `/me` khác game/env của `config.json`; cảnh báo khi framework minor khác; thông tin khi game version khác; deploy server trước console |
| Q20 | Cookie `__Host-` + `Path=/` (đổi tên cookie → mọi staff đăng nhập lại một lần khi deploy GM-2) | Có |
| Q21 | GM-2 một slice hay hai | Một slice, hai phase (backend → shell); tách nếu tasks quá lớn |

---

## §18. Cần xác minh ([VERIFY])

| # | Nội dung | Slice |
| --- | --- | --- |
| V1 | Microsoft.AspNetCore.OpenApi .NET 10 xuất DataAnnotations (`[Range]`, `[StringLength]`, `[RegularExpression]`, `[Description]`) vào schema của type khai qua `Accepts<T>()`; `int64` sinh ra kiểu gì (number hay number\|string). | GM-1 |
| V2 | Header CORS có mặt trên mọi response lỗi sau khi sửa `ErrorResponseWriter`: lỗi endpoint, 401/403 `ProfileEnforcementMiddleware`, 503 gate, 429 rate limiter, 503 timeout, 405. | GM-2 |
| V3 | Mantine (bản hiện hành) với `style-src 'self'` (CSS variables trong file tĩnh?) và Vite build không sinh inline script; nếu không được → `style-src 'self' 'unsafe-inline'`. | GM-2 |
| V4 | Cookie `Lax` được gửi trên fetch credentialed cùng site khác origin, và `Set-Cookie` (đăng nhập local, logout) trên response CORS credentialed được áp — Chrome, Edge, Firefox bản hiện hành. | GM-2 |
| V5 | ASP.NET antiforgery chạy với cookie `__Host-` + `Path=/`; Development HTTP dùng tên không prefix. | GM-2 |
| V6 | `int64` > 2^53 qua JSON trong TS (xp, amount): editor chuỗi; có cần server ghi số lớn dạng chuỗi cho GM không. | GM-3 |
| V7 | Firefox Total Cookie Protection làm vỡ trường hợp B như mô tả (chỉ để ghi lý do không hỗ trợ). | GM-2 (tuỳ chọn) |
| V8 | Version hiện hành lúc dựng: Node LTS, Mantine major, React Router, openapi-typescript/openapi-fetch (hỗ trợ 3.1.1). | GM-2 |
| V9 | npm workspaces ở root game nhận đường dẫn `framework/gm-console/packages/shell` (submodule) và hoist dependency của shell đúng. | GM-2/GM-4 |
| V10 | `PublicEndpointAllowlist`: route Public mới của platform (`gm.auth_methods`) có cần game thêm vào cấu hình không. | GM-1 |

---

## §19. Phạm vi chốt của GM-1 `gm-api-contract` (đầu vào speckit-specify)

Backend + TestKit + docs; không đụng cookie, CORS, SSO, hosting, frontend.

- `MapGmCommand`: khai body (`Accepts<TBody>`), path param từ template, header `X-Request-Id` (uuid, required) trong OpenAPI;
  `200 GmCommandResponse`.
- `MapGmCommand`: kiểm DataAnnotations của `TBody` sau deserialize, trước `toDomain` → `400 BAD_REQUEST` Problem Details
  có `errors` theo tên JSON; không annotation = hành vi cũ.
- Schema: `reason` của `IGmCommandBody` required, 1..500; DataAnnotations → keyword JSON Schema.
- Operation transformer: `operationId` = route name; `tags` = area (trừ `WithTags`); `x-gm-kind`, `x-gm-operation`,
  `x-gm-target`, `x-gm-confirm`, `x-gm-paged`; property `x-gm-options`.
- API trình bày mới: `GmConfirm` + `WithGmConfirm`, `ProducesGmPage<T>`, `WithGmQueryParameter`, `[GmOptions]`, `GmOption`,
  `GmOptionList`.
- Annotate route GM framework: list paged có type (search, receipts, audit, ledger, staff list), `displayName`, summary mọi
  route, `Typed` cho ban / cancel deletion / rotate key / maintenance / economy adjust; `[GmOptions]` trên
  `AdjustResourceBody.Resource`, `BanBody.Scope`; DataAnnotations trên body framework hiện có (`SetProgressionBody` của
  SampleGame làm mẫu).
- Endpoint lựa chọn: `economy.resource_options` (`GET /gm/v1/economy/resources`, `player.read`), `identity.ban_scopes`
  (`GET /gm/v1/players/ban-scopes`, `player.ban`).
- GM OpenAPI đăng ký mọi env; route mới `gm.console_catalog` `GET /gm/v1/console/openapi.json` (Gm, `gm.access`), bytes cache
  + `ETag`/`If-None-Match`; route dev Public cũ giữ.
- `gm.auth_methods` `GET /gm/v1/auth/methods` (Public, allowlist SEC-09) → `{ local, sso }`.
- `GmMeResponse` thêm `frameworkVersion`, `gameVersion` (additive).
- TestKit `GmOpenApiSnapshot` (`WriteAsync`/`DiffAsync`); framework test dùng nó thay/cùng `OpenApiTests`; regen
  `docs/api/gm-openapi.json`.
- Docs: PCR gm (route mới, allowlist, x-gm-*, quy ước annotate), PCR platform, module-authoring + consumer guide (cách khai
  GM route cho form chung, snapshot), `docs/html/02-them-command.html` (mục GM command), release note draft; sửa các dòng
  "frontend giao dev khác" (H12).
- VERIFY trong slice: V1, V10.
- zh-pvp (sau tag): bump, test snapshot + commit tài liệu của game.
