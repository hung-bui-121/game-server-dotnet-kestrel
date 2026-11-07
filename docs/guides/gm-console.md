# GM console: chạy local, cấu hình, deploy host tĩnh

> Đúng với framework v0.7.0 + slice 018 `gm-console-hosting-shell` (GM-2, chưa phát hành).

GM console là trang web cho staff vận hành game: đăng nhập, xem player, receipt, staff, health/version/gate. Console là
**site tĩnh riêng** (HTML + JS + CSS), gọi GM API của server bằng `fetch`. Mọi quyết định (permission, CSRF, phạm vi) do
server kiểm trên từng request; console chỉ hiển thị. Mã nguồn ở `gm-console/` (npm workspace, ngoài solution .NET:
`dotnet build` và `dotnet test` không cần Node).

Tài liệu liên quan: PCR `docs/pcr/gm.md` (contract GM API, cookie, CORS, SSO), `docs/pcr/platform.md` mục 24 (workspace,
codegen, header host tĩnh, e2e), consumer guide `docs/guides/consumer-guide.md` mục 15 (CI của game), header tham chiếu
`gm-console/deploy/` (`headers.md`, `nginx.conf.example`).

## 1. Tổng quan

Console và GM API nằm **cùng site, khác origin** (trường hợp A, cách duy nhất được hỗ trợ):

```text
https://gm.<env>.<domain>        host tĩnh: index.html, assets/*, config.json, header của deploy/security-headers.json
        │  fetch, credentials: 'include'   (Sec-Fetch-Site: same-site)
        ▼
https://gm-api.<env>.<domain>    reverse proxy → Kestrel, chỉ /gm/**; TLS kết thúc ở proxy (ForwardedHeaders)
```

- "Site" = scheme + domain đăng ký (eTLD+1). Hai subdomain của cùng domain công ty là cùng site, nên cookie phiên
  `SameSite=Lax` của GM API vẫn được gửi kèm `fetch` của console.
- Một console trên **site khác** (ví dụ domain mặc định của CDN/PaaS: `*.cloudfront.net`, `*.azureedge.net`, `*.pages.dev`,
  `*.web.app`, `*.github.io`) là trường hợp B: **không hỗ trợ** (cookie `Lax` không được gửi, Firefox phân vùng cookie).
- Local: console `http://localhost:5173`, API `http://localhost:5080`. Khác port nhưng cùng site.
- Thứ tự khi mở console: `config.json` (cùng thư mục với `index.html`) → `GET /gm/v1/auth/methods` → `GET /gm/v1/me`
  (401 → trang đăng nhập) → so game/môi trường → `GET /gm/v1/console/openapi.json` (catalog, một lần mỗi phiên) → menu.
- Trang tĩnh cũ `/gm/v1/ui` chỉ còn ở Development/Test (404 ở beta, uat, live).
- Các trang (GM-2, chỉ đọc): **Players** = ô tìm player (theo player id, mapping key hoặc display name; trang trống lúc đầu là
  bình thường) → trang player (aggregate, trạng thái xoá, tab Receipts / Audit / Ledger); **Staff** = danh sách staff → chi tiết
  + tab Audit; **Operations** = thẻ health, version, gate. Menu chỉ hiện trang mà staff có quyền và server có route.

## 2. Chạy local

Cần .NET SDK của `global.json` và Node **22.20.0** (`gm-console/.nvmrc`; `engine-strict` từ chối Node cũ hơn), npm 10.9.

1. Chạy host mẫu (flavor `Local`, file store, không cần Couchbase), để terminal đó chạy:

   ```powershell
   ./tools/local/run-local.ps1          # bash: tools/local/run-local.sh
   ```

   **Chờ dòng `… is ready (flavor Local, …)`** trước khi mở console (lần đầu khoảng 30 s vì build .NET); mở sớm hơn thì
   console báo "No answer from the GM API". Khối URL in thêm dòng (chỉ khi host chạy cổng 5080, cổng mà `config.json` local
   gọi):

   ```text
     GM console   http://localhost:5173  (cd gm-console; npm ci; npm run dev)
   ```

2. Terminal khác:

   ```powershell
   cd gm-console
   npm ci
   npm run dev
   ```

3. Mở `http://localhost:5173` (**`localhost`, không dùng `127.0.0.1`**), đăng nhập staff seed `admin` /
   `dev-only-change-me` (chỉ có ở Development/Test). Trên trang đăng nhập, console của trình duyệt có **một** dòng
   `401 (Unauthorized)` là bình thường (`GET /gm/v1/me` trước khi đăng nhập). Sau đăng nhập console mở Players (ô tìm kiếm,
   chưa có kết quả); Staff hiện danh sách staff, Operations hiện health/version/gate.

Không có proxy Vite: trình duyệt gọi thẳng `http://localhost:5080` với CORS, CSRF và cookie như production. Flavor `Local`
và `Dev` của host mẫu đã cấu hình origin console (mục 3.1). Cổng 5173 cố định (`strictPort`): server chỉ cho phép đúng
origin đó. Dừng: Ctrl+C ở cả hai terminal.

## 3. Cấu hình theo môi trường

### 3.1 Server (section `Gm` của host)

| Key | Giá trị | Luật (sai → host không khởi động, exit 1) |
| --- | --- | --- |
| `Gm:Cors:AllowedOrigins` | danh sách origin console; rỗng = không console | origin đúng dạng `scheme://host[:port]` (không port mặc định, user info, path, query, dấu `/` cuối), không wildcard, `https` ngoài Development, **đúng một** origin ngoài Development |
| `Gm:Console:BaseUrl` | URL gốc console, ví dụ `https://gm.beta.example.com/` | URL tuyệt đối `http(s)`, path kết thúc `/`, không query/fragment/user info; `https` ngoài Development; origin phải nằm trong `Gm:Cors:AllowedOrigins`; bắt buộc khi đặt CORS |
| `Gm:Cookies:SameSite` | `Lax` (mặc định) hoặc `None` | `None` chỉ ở Development/Test và cần CORS origin; mọi giá trị khác bị từ chối |

Hai key `Gm:Cors:AllowedOrigins` và `Gm:Console:BaseUrl` **đi cùng nhau**: có console thì đặt cả hai, không console thì không
đặt key nào (host chạy như trước, SSO về `/gm/v1/me`, hoặc `/gm/v1/ui` nếu có development tools).

| Flavor host mẫu | `Gm:Cors:AllowedOrigins` | `Gm:Console:BaseUrl` |
| --- | --- | --- |
| `Local`, `Dev` | `["http://localhost:5173"]` | `http://localhost:5173/` |
| `Beta`, `Uat`, `Live` | không đặt (chưa có domain console) | không đặt |

Ví dụ một môi trường có console (biến môi trường, `:` thành `__`):

```text
Gm__Cors__AllowedOrigins__0=https://gm.beta.example.com
Gm__Console__BaseUrl=https://gm.beta.example.com/
```

Dòng lỗi khi khởi động (nguyên văn trong `GmOptionsValidator` / `GmCorsValidation`):

```text
Gm:Cookies:SameSite '<value>' is not supported; use Lax (default) or None.
Gm:Cookies:SameSite=None needs Gm:Cors:AllowedOrigins: SameSite=None is only for a console on another site (case B, unsupported).
Gm:Cookies:SameSite=None is refused in '<DeploymentEnvironment>': a console on another site (case B) is not supported; use Lax (default) with a console on the same site.
Gm:Cors:AllowedOrigins '<value>' must be an origin (scheme://host[:port]) without default port, user info, path, query or trailing slash.
Gm:Cors:AllowedOrigins lists <n> origins in '<DeploymentEnvironment>'; list the one console origin of this environment.
Gm:Console:BaseUrl '<value>' must be an absolute http(s) URL whose path ends with '/', without query, fragment or user info.
Gm:Console:BaseUrl '<value>' is not https; plain http is allowed only in Development.
Gm:Console:BaseUrl origin '<origin>' is not in Gm:Cors:AllowedOrigins; add it (one console origin per environment).
Gm:Cors:AllowedOrigins is set but Gm:Console:BaseUrl is not; set the console URL of that origin (SSO returns there).
```

Cookie GM (server tự đặt, không có key cấu hình tên):

| Môi trường | Phiên | CSRF | Thuộc tính |
| --- | --- | --- | --- |
| ngoài Development | `__Host-gs_gm` | `__Host-gs_gm_csrf` | `Path=/`, `Secure`, `HttpOnly`, không `Domain`, `SameSite` theo `Gm:Cookies:SameSite` |
| Development | `gs_gm_dev` | `gs_gm_dev_csrf` | `Path=/`, `HttpOnly`; phiên `Secure` khi HTTPS hoặc `SameSite=None` |

SSO: console mở `{apiBaseUrl}/gm/v1/auth/sso/start?returnUrl=<đường dẫn console đã mã hoá>` bằng điều hướng top-level;
callback về `BaseUrl` + đường dẫn đó (chỉ nhận `/` hoặc `/#…`), mọi lần bị từ chối về `BaseUrl#/login?error=sso_denied`.
Redirect URI đăng ký ở IdP không đổi: `https://<host GM API>/gm/v1/auth/sso/callback`.

### 3.2 Console: `config.json`

File duy nhất khác nhau giữa các môi trường, đặt **cạnh `index.html`** trên host tĩnh (console đọc nó tương đối với
`index.html`, nên console dưới một sub-path cũng chạy):

```json
{ "apiBaseUrl": "https://gm-api.beta.example.com", "gameId": "zhpvp", "environmentId": "beta" }
```

| Member | Luật |
| --- | --- |
| `apiBaseUrl` | URL tuyệt đối `http`/`https` (scheme chữ thường), không query, fragment, user info; dấu `/` cuối được bỏ qua |
| `gameId`, `environmentId` | chuỗi 1..64 ký tự; phải trùng `gameId`/`environmentId` của `/gm/v1/me`, khác → màn hình "Wrong server" |
| member khác | bỏ qua |

Luật đầy đủ: `gm-console/deploy/config.schema.json` (JSON Schema 2020-12), ví dụ `gm-console/deploy/config.example.json`.
File sai hoặc thiếu → console báo field sai và không gọi API. Local: `gm-console/app/public/config.json` =
`{ "apiBaseUrl": "http://localhost:5080", "gameId": "sample", "environmentId": "local" }` (chỉ dùng cho `npm run dev`).

### 3.3 CSP `connect-src`

CSP của host tĩnh có `connect-src 'self' {{apiOrigin}}`: thay `{{apiOrigin}}` bằng **origin** của `apiBaseUrl` trong
`config.json` của môi trường đó (ví dụ `https://gm-api.beta.example.com`). Như `config.json`, CSP là theo môi trường.

## 4. Deploy host tĩnh

1. Build **một lần** cho mọi môi trường (Node 22.20 chỉ cần ở bước này):

   ```powershell
   cd gm-console
   npm ci
   npm run build -w app        # → gm-console/app/dist/
   npm run check:dist          # không inline script, không URL bên thứ ba, không localhost:5080, không config.json, ≤ 500 KB gzip
   ```

   Ngân sách là **≤ 500 KB gzip** cho JS+CSS mà `index.html` tải (`check:dist` in ra, hiện khoảng 185 KB). Vite in kích thước
   chưa nén (khoảng 510 kB cho file JS) và có thể cảnh báo chunk lớn: không sao.

   `dist/` không bao giờ chứa `config.json` và không chứa giá trị môi trường nào; asset dùng đường dẫn tương đối
   (`./assets/...`), nên host được ở gốc domain hoặc dưới sub-path. Không commit `dist/`.

2. Với mỗi môi trường: upload `dist/` + `config.json` của môi trường đó lên host tĩnh.
3. Đặt **header HTTP response** theo `gm-console/deploy/security-headers.json` (bảng `headers.md`, ví dụ
   `nginx.conf.example`). Thẻ `<meta>` không thay được (CSP trong `<meta>` không có `frame-ancestors`).

| Đường dẫn | Header |
| --- | --- |
| mọi response | `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' {{apiOrigin}}; img-src 'self' data:; font-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`, `Strict-Transport-Security: max-age=31536000`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`, `Permissions-Policy: camera=(), microphone=(), geolocation=()` |
| `/`, `/index.html`, `/config.json` | `Cache-Control: no-cache` |
| `/assets/*` | `Cache-Control: public, max-age=31536000, immutable` |

Các đường dẫn trên **tính từ thư mục gốc của console** (base path), không phải gốc domain. Console ở gốc domain: dùng nguyên
như bảng và `nginx.conf.example`. Console dưới sub-path (ví dụ `https://gm.beta.example.com/console/`, `Gm:Console:BaseUrl` có
path `/console/`): thêm tiền tố vào mọi `location` (`location = /console/`, `location = /console/index.html`,
`location = /console/config.json`, `location /console/assets/`, và `location /console/` thay cho `location /`), giá trị header
giữ nguyên; trên CDN áp quy tắc cho cùng các đường dẫn có tiền tố. Origin của console **chỉ phục vụ console**: sub-path chỉ dành cho một host riêng của console, không bao giờ là origin dùng chung của CDN/app khác (mọi trang khác trên cùng origin đọc được token CSRF bằng `credentials` và gửi được mutation GM).

4. Domain: **custom domain dưới domain công ty + TLS**, cùng site với GM API (mục 1). Không dùng domain mặc định của CDN.
5. Console dùng hash routing (`/#/players/...`): host không cần rule rewrite SPA.
6. GM API sau reverse proxy kết thúc TLS: bật `GameServer:Hosting:ForwardedHeaders` (`Enabled`, `KnownProxies` hoặc
   `KnownNetworks`). Ngoài Development route CSRF (`GET /gm/v1/auth/csrf`) cần request được nhận là HTTPS (antiforgery
   `Always`); thiếu ForwardedHeaders → đăng nhập lỗi.

Thử deploy trên máy: `npm run preview` phục vụ `app/dist/` trên `http://localhost:5173` với đúng header của
`security-headers.json` và một `config.json` lấy từ file (giống bước upload). **Dừng `npm run dev` trước** (cổng 5173 cố
định). Với host local, có thể bỏ `GM_CONSOLE_CONFIG`: mặc định là `app/public/config.json` =
`{ "apiBaseUrl": "http://localhost:5080", "gameId": "sample", "environmentId": "local" }`. Một file khác phải có đúng các
giá trị của host đang chạy (khác `gameId`/`environmentId` → "Wrong server"):

```powershell
cd gm-console
npm run build -w app
$env:GM_CONSOLE_CONFIG = "D:\path\to\config.json"   # tuỳ chọn, đường dẫn tuyệt đối
npm run preview
Remove-Item Env:GM_CONSOLE_CONFIG                      # sau khi thử xong
```

`connect-src` lấy origin của `apiBaseUrl` trong file đó (ghi đè bằng `GM_E2E_API`). CI của game không có trang riêng:
consumer guide mục 15.

## 5. Thứ tự deploy và lệch version

1. Deploy **server trước** (version framework/game mới).
2. Deploy **console sau**, build từ cùng commit.
3. Giữa hai bước, console cũ chạy với server mới: menu và catalog đọc lúc chạy nên luôn khớp server; một trang build theo
   tài liệu cũ có thể nhận 400/404 (server vẫn an toàn).

Console so `/gm/v1/me` với bản build:

| Khác nhau | Console làm gì |
| --- | --- |
| `gameId` hoặc `environmentId` ≠ `config.json` | **chặn**: màn hình "Wrong server" nêu cả hai giá trị, không tải gì khác, chỉ có nút Sign out |
| `major.minor` của framework (`/me.frameworkVersion` so với version shell lúc build) | banner cảnh báo nêu cả hai version, không chặn |
| version game (khi build có `GM_CONSOLE_GAME_VERSION`) | một dòng thông tin ở footer |
| version không đọc được | banner cảnh báo |

Khi nâng cấp lên framework có slice 018, cookie đổi tên (`gs_gm` `Path=/gm` → `__Host-gs_gm` `Path=/`): **mọi staff phải
đăng nhập lại một lần**. Cookie cũ nằm trong trình duyệt đến khi hết hạn và bị bỏ qua.

## 6. Bất biến bảo mật

- Server quyết định mọi thứ; ẩn menu/nút chỉ là UX. Trang ẩn mở bằng URL hiện "not available" và không gọi API.
- Cookie `HttpOnly`, host-only, `SameSite=Lax`; JavaScript không đọc. Console không dùng `localStorage`, `sessionStorage`,
  IndexedDB, `document.cookie` (lint chặn cả dạng `window.x`, `globalThis.x`, `globalThis['x']`; smoke e2e kiểm sau khi chạy:
  storage rỗng, `document.cookie === ''` trên origin console, `indexedDB.databases()` rỗng).
- Token CSRF chỉ trong bộ nhớ, chỉ gửi `X-CSRF-Token` trên request không phải GET; GET không có header tuỳ biến (không
  preflight). `403 CSRF_FAILED` → lấy token mới, thử lại đúng một lần.
- CORS: một origin console chính xác mỗi môi trường; CORS không phải xác thực.
- CSP chặt trên host tĩnh (`script-src 'self'`, `style-src 'self'`, `connect-src` chỉ API của môi trường), không CDN/font/
  analytics bên thứ ba; bundle không chứa secret.
- Dữ liệu server hiển thị dạng text (JSON qua `JSON.stringify`). Lint chặn `dangerouslySetInnerHTML`, `srcDoc`/`srcdoc`,
  `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`/`writeln`, `eval`, `new Function`, chuỗi trong
  `setTimeout`.
- Mapping key và display name không bao giờ lên URL; tham số route được mã hoá vào đúng một đoạn path (giá trị `.`, `..`,
  rỗng bị từ chối trước khi gửi).
- 401 → về trang đăng nhập, giữ trang đang xem trong bộ nhớ; không vòng lặp tự đăng nhập. 403 → hiện lỗi, đọc lại `/me`
  một lần.
- SSO chỉ quay về `Gm:Console:BaseUrl` + đường dẫn tương đối; `live` có dải đỏ cố định trên header.
- GM-2 không có thao tác ghi nghiệp vụ: chỉ đăng nhập, đăng xuất, lấy token CSRF. Ba lớp kiểm: lint (mọi `.POST/.PUT/
  .DELETE/.PATCH/.request(`, truy cập kiểu `api['POST']`, `method: 'post'` hoặc template; trong `session/` chỉ `.POST` với
  đường dẫn literal `/gm/v1/auth/login` hoặc `/gm/v1/auth/logout`), test quét mã nguồn, và guard lúc chạy trong client: request
  không phải GET/HEAD tới đường dẫn khác hai route đó bị từ chối trước khi gửi (`MUTATION_NOT_ALLOWED`; GM-3 thay bằng kiểm
  theo catalog).

## 7. Test

```powershell
cd gm-console
npm ci
npm run verify   # check:api, lint, typecheck, test (Vitest), build, check:dist
```

- `npm run check:api` sinh lại `packages/shell/src/generated/gm-api.d.ts` từ `docs/api/gm-openapi.json` và báo lỗi khi file
  commit đã cũ (`npm run gen:api` để ghi lại, rồi commit).
- Smoke e2e (Chrome hoặc Edge của máy, không bao giờ tải browser): chạy run-local (mục 2) rồi `npm run e2e`. Hai target
  `dev` và `preview` (bản build với header tham chiếu, kiểm 0 vi phạm CSP). Browser mặc định
  `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe`; nếu Chrome nằm ở `C:/Program Files/…` thì đặt
  `GM_E2E_BROWSER` tới đúng file. `GM_E2E_API` khác `http://localhost:5080` (run-local `-Port` khác): target `preview` dùng
  một `config.json` sinh cho API đó, target `dev` bị bỏ qua (dev luôn dùng `app/public/config.json`). Edge:

  ```powershell
  $env:GM_E2E_BROWSER = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"; npm run e2e
  ```

  Biến khác (`GM_E2E_API`, `GM_E2E_TARGET`, `GM_E2E_STAFF`, `GM_E2E_PASSWORD`): `gm-console/e2e/README.md`.
- Phía .NET: `GameServer.Architecture.Tests` kiểm version các `package.json` = `GameServerFrameworkVersion`, dependency pin
  chính xác, lockfile v3, không commit `node_modules`/`dist`, không project .NET nào gọi npm.

## 8. Xử lý sự cố

| Triệu chứng | Nguyên nhân và cách sửa |
| --- | --- |
| "No answer from the GM API at http://localhost:5080." | API chưa chạy, hoặc trình duyệt chặn vì CORS (trình duyệt giấu response, console thấy như lỗi mạng). Kiểm: run-local đang chạy; `Gm:Cors:AllowedOrigins` đúng **origin** của console (scheme, host, port, không `/` cuối); `apiBaseUrl` trong `config.json`; CSP `connect-src` của host tĩnh có origin API |
| Console của trình duyệt có một dòng `Failed to load resource: … 401 (Unauthorized)` trên trang đăng nhập | bình thường: console hỏi `GET /gm/v1/me` trước khi đăng nhập và nhận 401; trình duyệt luôn ghi dòng đó |
| Mở `http://127.0.0.1:5173` thì lỗi | `127.0.0.1` và `localhost` là hai site khác nhau: origin không có trong CORS, cookie `Lax` không gửi. Dùng `http://localhost:5173` |
| Console trên máy khác qua LAN (`run-local -Lan`) | console `localhost` gọi API `192.168.x.y` là khác site: không hỗ trợ. Chạy console trên cùng máy với host |
| Sau nâng cấp vẫn bị đưa về trang đăng nhập | bình thường một lần: cookie cũ `gs_gm` (`Path=/gm`) bị bỏ qua, đăng nhập lại |
| `Gm:Cookies:SameSite=None` ở Development: đăng nhập lỗi | `None` làm cả hai cookie `Secure`; trên HTTP thường antiforgery (`Always`) lỗi ở route CSRF. `None` cần HTTPS và chỉ dành cho trường hợp B (không hỗ trợ); dùng `Lax` |
| Ngoài Development: đăng nhập local/SSO lỗi ở bước CSRF sau proxy | thiếu `GameServer:Hosting:ForwardedHeaders` (mục 4) |
| `Error: Port 5173 is already in use` | một `npm run dev`/`npm run preview` khác đang chạy; dừng nó. Không đổi cổng: origin `http://localhost:5173` là origin được server cho phép |
| Màn hình "Wrong server" | `config.json` (`gameId`/`environmentId`) trỏ khác server; sửa `config.json` hoặc `apiBaseUrl` |
| Banner "Version mismatch" | console build cho framework khác `major.minor` server: deploy console build từ cùng commit với server |
| `npm ci` báo `EBADENGINE` | Node khác 22.20+: dùng Node theo `gm-console/.nvmrc` |

## 9. Phần đến sau

- **GM-3**: form command chung sinh từ catalog (`x-gm-*`): xác nhận, gõ lại cho thao tác nguy hiểm, `reason`, request id,
  không tự retry; các thao tác staff, gate, key, resource, ban của framework và command của game.
- **GM-4**: trang riêng của game: workspace npm ở root repo game gồm app của game và `framework/gm-console/packages/shell`,
  `createGmConsole({ extensions })`, registry trang/tab được export. Lưu ý đã thấy khi thử (slice 018 T064): dùng cả thư
  mục `framework/gm-console` của submodule (tsconfig của shell kế thừa `tsconfig.base.json`), app game tự `define`
  `__GM_BUILT_FOR__`, theme riêng phải nằm trong CSS tĩnh (CSP `style-src 'self'`), root của game cần `.npmrc` riêng với
  `@gameserver:registry=https://registry.invalid/` (chống dependency confusion).
- Bản zip console mặc định phát hành theo tag (game khỏi cần Node) là phương án sau (Q18).
