# Research: GM console hosting and shell (slice 018 gm-console-hosting-shell, GM-2)

**Date**: 2026-11-06 · **Spec**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md)

Each item: decision, rationale, alternatives, and how it is checked. Two probes ran during planning in throw-away projects
under the session scratch folder (never in the repository): a Node two-port server driven by `playwright-core` 1.65.0 with
the system Chrome 157.0.8103.38 and Edge 156.0.4314.58 (R1, R3), and ASP.NET Core 10 apps on SDK 10.0.403 (R1, R2).
Firefox is not installed.

## VERIFY items

| Item | Fact | Status after planning | Fallback | Check (task) |
| --- | --- | --- | --- | --- |
| V2 | CORS headers are on every `/gm/**` error answer for the allowed origin | Probe: yes (`OnStarting`); not yet on the real pipeline | Change only the writer/pipeline path whose status lacks them | T004 (gate, permanent test) |
| V3 | Mantine runs under `style-src 'self'` (no injected `<style>` needed) | Open | `style-src 'self' 'unsafe-inline'` (styles only) | T050 (gate) |
| V4 | `Lax` cookie sent on credentialed same-site cross-port fetch; `Set-Cookie` on a CORS response applied; deletion applied | **Answered** for Chrome 157 / Edge 156 (R1) | — | Re-confirmed by the e2e smoke (T055) |
| V5 | Antiforgery with `__Host-` + `Path=/` | **Answered**: works on HTTPS; policy `Always` throws on HTTP (R1) | Development names (FR-013) | T014 (permanent tests) |
| V7 | Firefox Total Cookie Protection breaks case B | Not run (no Firefox) | — | Recorded only |
| V8 | Exact versions satisfying Node 22.20 and peer ranges | Snapshot below (R7) | Older line of the same package | T025 (gate) |
| V9 | A game workspace consumes `packages/shell` from a submodule path with hoisting | Open | Document `file:` + explicit peer install | T064 (scratch) |
| V11 | `vite preview` applies configured headers to `index.html`, assets and `config.json` | Open | A 40-line Node static server in `e2e/` applying `security-headers.json` | T052 |
| V12 | `openapi-typescript` output is deterministic and LF | Open | Post-process (normalize line ends) in `gen:api` | T028 |
| V13 | The browser revalidates the catalog with `If-None-Match` under credentialed CORS (server answers 304) | Open | Treat the catalog as once-per-session (no ETag reliance) | T056 |

## R1 — `__Host-` cookies on `http://localhost` and ASP.NET antiforgery (trap 1, V4, V5)

Probe A (browsers): API on `http://localhost:5091`, console page on `http://localhost:5092`; credentialed `fetch` from the
console; cookies set on the CORS answer and on a top-level 302 (SSO-like).

| Observation | Chrome 157 | Edge 156 |
| --- | --- | --- |
| `Sec-Fetch-Site` of console → API fetch | `same-site` | `same-site` |
| `__Host-x; Path=/; Secure; HttpOnly; SameSite=Lax` set by the CORS answer, sent back on GET and POST | yes | yes |
| `Secure` non-prefixed cookie from `http://localhost` | accepted, sent | accepted, sent |
| Same cookies set during a top-level 302 on the API host, then sent by the console's fetch | yes | yes |
| Deletion (`Max-Age=0` with the same `Path`/`Secure`) | applied | applied |
| `X-Request-Id` exposed on a 400 answer readable by `fetch` | yes | yes |
| Preflight for POST with `content-type: application/json` + `x-csrf-token` | one `OPTIONS` | one `OPTIONS` |

Probe B (server, ASP.NET Core 10): antiforgery with `Cookie.Name = "__Host-gs_gm_csrf"`, `Path = "/"`:

- `SecurePolicy = Always` + plain HTTP request → `InvalidOperationException: The antiforgery system has the configuration
  value AntiforgeryOptions.Cookie.SecurePolicy = Always, but the current request is not an SSL request.` for both
  `GetAndStoreTokens` and `IsRequestValidAsync`.
- `SecurePolicy = SameAsRequest` + HTTP → `Set-Cookie: __Host-gs_gm_csrf=…; path=/; samesite=lax; httponly` (no `secure`)
  — browsers reject a `__Host-` cookie without `Secure`.
- A session cookie written by our code with `Secure = true` on an HTTP answer is emitted as written.

**Decision**: outside Development, `__Host-gs_gm` / `__Host-gs_gm_csrf`, `Secure`, antiforgery `Always` (unchanged since
007 for non-Development). In Development, names `gs_gm_dev` / `gs_gm_dev_csrf` (FR-013): session `Secure` when HTTPS or
`SameSite=None`, antiforgery `SameAsRequest` (or `Always` with `None`).

**Rationale**: the browsers would accept `__Host-` locally, but the antiforgery cookie cannot be both `Secure`-only and
served on plain HTTP; run-local must work on plain HTTP (also `-Lan`, where the host is not `localhost` and `Secure`
cookies are rejected), and the in-process clients use `System.Net.CookieContainer`, which never sends a `Secure` cookie to
an `http` URI. New names (not `gs_gm`) because the old cookie with `Path=/gm` would be sent before a new `Path=/` cookie
of the same name (longer path first) and shadow it.

**Alternatives**: (a) `run-local` with a dev HTTPS certificate — trust prompts on corporate machines, Vite would need HTTPS
too (an `http` console calling an `https` API is cross-site under schemeful same-site), more moving parts; (b) rewrite the
antiforgery `Set-Cookie` to add `Secure` — a hidden response rewrite (017 lesson: no side effect only to make a rule look
right); (c) a configuration switch for the prefix — more surface for no current need. (a) stays possible later without code
change to the cookie rule (Development over HTTPS already gets `Secure`).

**Check**: T014 (Development matrix over HTTP/HTTPS, bare app with Staging options over HTTPS for the `__Host-` names and an
antiforgery round trip), T022 (Couchbase flavors Beta/Uat over HTTPS), T055 (browser).

## R2 — CORS headers on error answers (trap 2, V2, H14)

Probe C (ASP.NET Core 10): policy with one origin, credentials, exposed headers, max-age 7200; an outer middleware that
catches an exception, calls `Response.Clear()` and writes 500; an inner middleware (after `UseCors`) that calls
`Response.Clear()` and writes 401. With `Origin` allowed: 200, 500 (thrown in the endpoint), 401 and 404 (no route) all had
`Access-Control-Allow-Origin`, `-Allow-Credentials` and `-Expose-Headers`; the preflight had `-Max-Age: 7200`.

Reason: for actual requests `CorsMiddleware` registers `Response.OnStarting(...)` and applies the evaluated result when the
headers are sent; `Response.Clear()` resets headers and body but not the `OnStarting` callbacks.

**Decision**: write the pipeline test first against current `main` code (T004) for every status of FR-002, including the
real rate limiter (`GameServer:Limits:GmConcurrency = 1`, queue 0, two concurrent calls on a probe endpoint), the real
timeout (`GmTimeoutSeconds = 1` on a probe endpoint that waits) and an exception (probe endpoint). If all are green,
`ErrorResponseWriter` stays as it is and the design claim H14 is recorded as not reproduced; the test stays as a guard. To
prove the test can fail, a scripted check removes the `/gm` CORS branch temporarily and sees it red. If a status is red,
only that path is fixed.

**Alternatives**: copy `Access-Control-*` headers before `Clear()` in the writer (design) — would be dead code if the test
is green, and a second source of CORS headers.

## R3 — Same-site across ports; `localhost`, `127.0.0.1`, LAN (trap 3)

Site = scheme + registrable domain; `localhost` ports share a site (probe: `same-site`). `127.0.0.1` and `localhost` are
different sites. A LAN IP console calling `localhost` is cross-site. **Decision**: everything local uses `localhost`
(config.json, Vite `host: 'localhost'`, run-local banner, e2e); the guide's troubleshooting lists the two traps. The CSRF
`Origin` check compares origins exactly, so `http://localhost:5173` must be the configured origin (not `127.0.0.1`).

## R4 — `SameSite` setting and startup rules

`Gm:Cookies:SameSite` ∈ {`Lax`, `None`} (FR-010). `Strict` is not offered: not in the design; the session is created
during the SSO top-level redirect from the identity provider and would need extra analysis. Rules (all in
`GmOptionsValidator`, one report):

| Fault | Where |
| --- | --- |
| value not `Lax`/`None` | every environment |
| `None` and no CORS origin | every environment |
| more than one CORS origin | outside Development |
| `BaseUrl` not absolute `http(s)`, path not ending `/`, query/fragment/user info | every environment |
| `BaseUrl` `http` | outside Development |
| `BaseUrl` origin not in `Gm:Cors:AllowedOrigins` | every environment |
| CORS origin set and `BaseUrl` missing | every environment |
| neither set (no console) | allowed everywhere, with or without SSO (no line) |

`None` ⇒ both cookies `Secure` everywhere; in Development over HTTP the antiforgery policy `Always` then throws on the CSRF
route (R1) — documented ("`None` needs HTTPS"); the scheme is per request, so startup cannot check it.

## R5 — Return URL with a hash route and a sub-path console

The console uses hash routing (design D6), so its own path is `/`; a deep link is `/#/players/abc`. Rules (FR-022): length
1..512, printable ASCII, starts with `/`, no `//`, `\`, `..`. Target = `BaseUrl` + value without its leading `/`
(`https://cdn.example.com/gm/` + `#/players/abc`). `new Uri(base, "/#/x")` would drop the base path, so string
concatenation on a validated value is used. `#` and `?` are allowed characters. The OIDC `RedirectUri` property keeps the
**relative** value; the absolute target is built in `OnTicketReceived` (the OIDC handler never sees an absolute URL from
input). Denial = `BaseUrl` + `#/login?error=sso_denied`. Without `BaseUrl` (no console, any environment): today's `/gm/`
rule with the FR-022 character rules; default `/gm/v1/ui` where development tools are on, `/gm/v1/me` elsewhere (the page is
not mapped there, `/me` shows the signed-in staff); denial = that default + `?error=sso_denied`.

## R6 — Catalog caching without a preflight (trap 6, V13)

`If-None-Match` is not a CORS-safelisted request header; setting it on the catalog GET makes the browser preflight it and
requires it in `Access-Control-Allow-Headers`. The catalog answers `Cache-Control: private, no-cache` and a strong `ETag`
(017), so the browser HTTP cache stores it and revalidates it by itself (conditional headers added by the cache do not
trigger a preflight); a 304 is served from cache as a 200 to `fetch`. **Decision**: the shell calls the catalog without
custom headers (`cache: 'default'`) once per session; `ETag` is exposed (FR-001) so a later slice can compare catalog
versions. V13 checks in the e2e smoke, through the Chrome DevTools Protocol (`Network.responseReceivedExtraInfo`), that a
second load of the console revalidates with 304; if not, the console still works (one 200 per session).

## R7 — Toolchain and dependency versions (trap 7, V8)

Installed: Node 22.20.0, npm 10.9.3. Rule (FR-043): the newest stable version published ≥ 14 days before the install,
compatible with Node 22.20 (`engine-strict`) and with the other packages' peer ranges. Snapshot from the npm registry on
2026-11-06 (cut-off 2026-10-23); T025 re-runs the query and records the final list:

| Package | Candidate | Note |
| --- | --- | --- |
| `react`, `react-dom` | 19.3.1 | Mantine needs ≥ 19.2 |
| `@mantine/core`, `@mantine/hooks` | 9.7.3 | 9.7.5 is 2 days old; `@mantine/form` not needed before GM-3 |
| `postcss`, `postcss-preset-mantine` | 8.5.31, 1.18.0 | Mantine CSS setup |
| `@tanstack/react-query` | 5.104.5 | |
| `react-router` | 7.18.x (≥ 14 days) | 8.x needs Node ≥ 22.22.0 |
| `openapi-typescript` | 7.13.0 | peer `typescript ^5.x`; OpenAPI 3.1 supported |
| `openapi-fetch` | 0.17.0 | same author |
| `typescript` | 5.9.3 | 7.0 outside `typescript-eslint` (< 6.1) and `openapi-typescript` (`^5`) peers |
| `vite`, `@vitejs/plugin-react` | 8.3.5, 6.1.2 | Node `^20.19 \|\| >=22.12` |
| `vitest` | 4.1.12 | 5.x is 4 days old |
| `jsdom` | 28.1.0 | 30.x needs Node ≥ 22.22.2, 29.x ≥ 22.13 (ok but check date) |
| `@testing-library/react`, `/user-event`, `/jest-dom` | 16.3.3, 14.6.8, 7.0.1 | |
| `eslint`, `typescript-eslint`, `eslint-plugin-react-hooks` | 10.13.0, 8.72.0, 7.1.1 | |
| `playwright-core` | 1.64.1 | never downloads browsers; system Chrome via `executablePath` |
| `@types/react`, `@types/react-dom` | newest 19.x ≥ 14 days | React Router 7 peers |

`.npmrc`: `engine-strict=true` (a package that needs a newer Node fails at install, not at run), `save-exact=true`.

## R8 — Workspace layout and the GM-4 seam (trap 8, V9)

```
gm-console/
  package.json          private root: workspaces ["packages/*", "app"], scripts, devDependencies (tooling, e2e)
  package-lock.json  .npmrc  .nvmrc  tsconfig.base.json  eslint.config.js  vitest.workspace / configs
  packages/shell/       @gameserver/gm-shell: package.json (exports "./src/index.ts"), src/**, src/generated/gm-api.d.ts
  app/                  @gameserver/gm-console-default: index.html, src/main.tsx, public/config.json, vite.config.ts
  deploy/               security-headers.json, nginx.conf.example, headers.md, config.example.json, config.schema.json
  e2e/                  smoke tests (Vitest node env + playwright-core), harness
  scripts/              check-dist.mjs, gen-api helpers (Node, no dependency)
```

The shell is consumed as source (`exports` → `./src/index.ts`, `moduleResolution: "bundler"`), so Vite compiles it inside
the app (and inside a game app in GM-4). Runtime libraries are exact `peerDependencies` of the shell and exact
`dependencies` of the app (npm 7+ installs peers; one copy of React hoisted). The shell imports only its own source and its
peers. GM-4 adds a game root `package.json` with workspaces `["gm-console", "framework/gm-console/packages/shell"]` (design
§7.3); V9 builds such a scratch layout once (T064) to catch hoisting problems early.

Public surface of the shell in this slice: `createGmConsole(options?)` and the types `GmConsoleConfig`, `GmMe`,
`GmProblem`, `GmBuiltFor`. The registry (`GmConsoleRegistry`, `GmPageDef`, `GmPlayerTabDef`) is internal (D9).

## R9 — Codegen determinism (trap 9, V12)

`openapi-typescript docs/api/gm-openapi.json -o packages/shell/src/generated/gm-api.d.ts` writes a header comment without
a timestamp and the same output for the same input. `.gitattributes` has `* text=auto eol=lf`, and the owner's machine has
`core.autocrlf=true`: the working copy is LF (eol=lf wins), so `git diff --exit-code` compares LF with LF. V12 runs the
generator twice and diffs; if output differs by line ends, `gen:api` normalizes to LF. `x-gm-*` keys are ignored by the
generator (they are read at run time from the catalog, not from types).

Shell types come from the **framework's** committed document, which includes the sample routes (`progression.set`,
`counter.gm_read`); the shell uses only framework routes. A game app (GM-4) generates its own types from its own committed
document (017 snapshot).

## R10 — Mantine and the CSP (trap 10, V3)

Mantine ships its component CSS as static files (`@mantine/core/styles.css`) and sets per-component CSS variables through
React `style` props, which React applies through the CSSOM (not blocked by CSP). Two features may inject `<style>`
elements: the theme CSS variables of `MantineProvider` and responsive style props (`InlineStyles`). V3 (T050) builds the app
with the components the pages use (AppShell, NavLink, Table, Tabs, Card, Button, TextInput, PasswordInput, Alert, Badge,
Code), serves it under the strict CSP and counts `securitypolicyviolation` events. Order of fallbacks: (1)
`MantineProvider` options that move variables into static CSS / avoid responsive props in the shell; (2) `style-src 'self'
'unsafe-inline'` (styles only; `script-src` stays `'self'`), recorded in the PCR and `headers.md`.

## R11 — Frontend test strategy

- **Unit/component**: Vitest + jsdom + Testing Library; one shared `fakeFetch` helper (route table → `Response`) injected
  into `createGmClient({ fetch })` and into the config loader — no MSW, no network.
- **Static checks**: `tsc --noEmit` for every workspace (strict, `noUncheckedIndexedAccess`), ESLint (TypeScript, hooks,
  restricted syntax/globals of FR-048).
- **Codegen check**: `check:api`.
- **Bundle check**: `scripts/check-dist.mjs` (FR-083, SC-007 gzip size).
- **End-to-end**: `e2e/` with Vitest in node environment and `playwright-core` launching the system browser by
  `executablePath` (FR-087); `GM_E2E_TARGET=dev|preview` (both by default), `GM_E2E_API` (default
  `http://localhost:5080`), `GM_E2E_BROWSER`. The harness starts Vite (dev or preview) on 5173 with `strictPort`, checks
  `GET {api}/gm/v1/auth/methods` first and fails with "start tools/local/run-local.ps1" when absent. The CSP collector is an
  init script listening to `securitypolicyviolation` plus console messages. Port 5180 is never used (owner's).

## R12 — One source for static-host headers

`deploy/security-headers.json`:

```json
{
  "placeholders": { "apiOrigin": "origin of config.json apiBaseUrl" },
  "all": { "Content-Security-Policy": "default-src 'none'; … connect-src 'self' {{apiOrigin}}; …", "Strict-Transport-Security": "max-age=31536000", "...": "..." },
  "document": { "Cache-Control": "no-cache" },
  "asset": { "Cache-Control": "public, max-age=31536000, immutable" }
}
```

`document` = `/`, `/index.html`, `/config.json`; `asset` = `/assets/*`. A Vitest test parses `nginx.conf.example`
(`add_header <Name> "<value>" always;` lines per `location`) and the table of `headers.md` and compares them with the JSON
(FR-082). The preview server of the e2e reads the same JSON with `apiOrigin` = the e2e API origin (V11).

## R13 — Version skew

`/me.frameworkVersion` is the informational version (`0.7.0+<sha>` or `0.7.0`); `builtFor.frameworkVersion` is the shell
`package.json` version (pinned to `GameServerFrameworkVersion` by an architecture test). Compare `major.minor` of the part
before `+`/`-`. `gameVersion` comes from `GM_CONSOLE_GAME_VERSION` at build (Vite `define`), absent in the framework's own
build. Q19: only game/environment block; versions warn/inform.

## R14 — GM-2 / GM-3 mutation boundary (D10)

| Mutation | Route | GM-2 | Reason |
| --- | --- | --- | --- |
| Local sign-in | `gm.auth_login` | yes | needed to use the console |
| Sign-out | `gm.auth_logout` | yes | session hygiene (SEC-05 POST) |
| CSRF token | `gm.auth_csrf` (GET, sets cookie) | yes | prerequisite |
| Staff create / grant / disable / enable / revoke sessions | `gm.staff_*` | GM-3 | reason + request id + confirmation (design §9.3–§9.4) |
| Gate maintenance / min client version | `gm.ops_gate_*` | GM-3 | typed confirm (maintenance, Q7) |
| Key rotation | `gm.ops_rotate_keys` | GM-3 | typed confirm |
| Resource adjust, ban, unban, cancel deletion, game commands | `economy.adjust`, `identity.*`, game | GM-3 | generic command form, options, typed confirm |

The client middleware (CSRF header + one retry, optional `X-Request-Id`) is built and unit-tested now because sign-in and
sign-out need it; GM-3 adds the request-id policy.

## R15 — Tests and files that pin what this slice changes (trap 11)

| Pin | File:line | Updating task |
| --- | --- | --- |
| `samesite=none` when CORS is on | `tests/GameServer.Gm.Tests/CorsTests.cs:32-49` | T013 |
| `path=/gm`, `samesite=lax` on session cookie | `tests/GameServer.Gm.Tests/GmAuthTests.cs:41-42` | T013 |
| CSRF/session cookie attributes and `GmCookies.CsrfCookieName` | `tests/GameServer.Gm.Tests/CsrfAndSessionTests.cs:154-169` | T013 |
| `GmSso.DefaultReturnUrl` default target | `tests/GameServer.Gm.Tests/SsoTests.cs:136-138` | T018 |
| SSO success to `/gm/v1/me` (legacy, kept) | `SsoTests.cs:102, 113` | T018 (unchanged expectation) |
| Session cookie helper by name | `tests/GameServer.Gm.Tests/GmTestHost.cs:102-104` | T013 |
| `gm.ui` in the route list | `tests/SampleGame.Tests/CatalogCompositionTests.cs:35, 76` | T021 |
| Staging/Live flavor starts | `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs:45-108` (settings `tests/Shared/StagingSecrets.cs:28-30`) | T019, T022 |
| run-local banner | `tests/GameServer.Architecture.Tests/RunLocalScriptTests.cs:98` | T030 |
| Framework version single source | `tests/GameServer.Architecture.Tests/VersionTests.cs` | T027 |

T003 completes this table (grep of `src/`, `tests/`, `templates/`, `tools/`, `modules/`, `docs/`, and by project folder
`samples/SampleHost`, `samples/SampleGame/*`; never recursive over `samples/`).

## R16 — Sample host configuration reaches tests (trap 11)

`LocalFlavorFactory<SampleHost Program>` loads `samples/SampleHost/appsettings.Local.json` (016 trap). Adding
`Gm:Cors:AllowedOrigins` and `Gm:Console:BaseUrl` there turns CORS on in those tests; with FR-010 the cookies stay `Lax`, and
the CSRF trusted origins gain `http://localhost:5173` only. T019 lists the factory users and re-runs them.
`samples/SampleHost/appsettings.{Beta,Uat,Live}.json` stay without console values (D12): `SampleHostFlavorTests` and the
Live process test keep exactly their current violations and prove a game bumps without new settings.

## R17 — Requirements wording (owner's file)

Rows and lines to edit (FR-096), current text → decided text (Vietnamese, the file's language):

- TBD-05a (line 707): "Same-origin; `SameSite=Lax`; không CORS — Chốt 2026-10-22" → "Console host tĩnh riêng, **cùng
  site** khác origin (trường hợp A); cookie `__Host-` + `SameSite=Lax`; CORS một origin console theo environment; khác site
  không hỗ trợ — Chốt 2026-11-05 (mở lại từ 2026-10-22)".
- SEC-04 (line 442): baseline same-origin → case A, `__Host-` + `Path=/`, `SameSite` cấu hình (`Lax` mặc định), `None`
  chỉ khi có CORS và luôn `Secure`.
- SEC-06 (line 444): add "đúng một origin console mỗi environment (ngoài Development); header CORS có trên mọi response
  lỗi; expose `X-Request-Id`, `Retry-After`, `ETag`".
- TBD-21 (line 725): "Chưa có / Mở" → "React + TypeScript + Vite + Mantine; Chrome/Edge desktop, Firefox best-effort;
  static host riêng cùng site — Chốt 2026-11-05".
- G6 (line 95), status line 7, line 697: restate the above.
- SEC-13 (line 451) and appendix "Rate limit GM API" (line 816): "GM API giữ per-IP chặn cứng ≤ 30/phút" → "GM: per-IP
  chặn cứng ≤ 30/phút chỉ cho endpoint auth GM (`/gm/v1/auth/*`: login, csrf, methods, SSO start/callback); các route GM
  khác chỉ có concurrency limiter `gs-gm` — Q9 chốt 2026-11-05". Code check: no per-IP GM limiter exists
  (`GameServerBuilder.cs:346-351` concurrency only), so nothing to align; the auth limiter is a later slice.

GM-01 (line 427) is reported, not edited.

## Code facts used by the plan

- `GmCookies.Session(HttpContext, DateTimeOffset?)` builds the session `CookieOptions`; `GmCookies.Configure` sets
  antiforgery options and CSRF trusted origins (`GmCookies.cs:19-53`).
- Cookie write/delete sites: `GmModule.cs:230` (login), `:256` (logout), `GmSso.cs:164` (SSO); reads:
  `GmCookieAuthenticationHandler.cs:36`, `GmSessionService.cs:119`.
- CORS policy registered only when origins exist (`GmCors.cs:73-85`), applied by `UseWhen("/gm", UseCors)` after routing,
  before gate, profile enforcement, timeouts and the rate limiter (`GameServerApplicationExtensions.cs:70-78`).
- Error writers: `ExceptionMiddleware`, `ProfileEnforcementMiddleware` (405 `:46`, 415 `:54`, CSRF `:145`), rate limiter
  `OnRejected` (`GameServerBuilder.cs:352-354`), request timeout (`GameServerBuilder.cs:363`) — all call
  `ErrorResponseWriter.WriteAsync` (`ErrorResponseWriter.cs:28-106`).
- Limits are configurable (`GameServer:Limits:GmConcurrency`, `QueueFactor`, `GmTimeoutSeconds`), used by T004.
- `GameServerOptions.IsDevelopment` = `DeploymentEnvironment == Development`; `AllowsDevelopmentTools` = Development or Test
  (`GameServerOptions.cs:85-90`); `TestHost` runs `Development` with EnvironmentId `test` (`TestHost.cs:72-73`).
- `GmRoutes.Ui` has no condition (`GmRoutes.cs:215`); route conditions: `RouteConditions.DevelopmentTools`
  (`GameServerBuilder.cs:240`), used by `gm.auth_login` (`GmRoutes.cs:195-196`).
- Framework version: `Directory.Build.props:13` `GameServerFrameworkVersion` 0.7.0; Unity package 0.7.0; `VersionTests`.
- No CI system in the repository (no `.github/`); checks are .NET tests and npm scripts.
