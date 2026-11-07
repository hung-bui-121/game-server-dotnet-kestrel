# Release notes draft — slice 018 gm-console-hosting-shell (GM-2)

Framework `0.7.0` + branch `018-gm-console-hosting-shell` (unreleased). Second slice of the GM tool track (GM-2 of GM-1..GM-4,
design `AgentMemories/features/gm-tool/01_design.md` §6–§8, §16). Started in T002 (2026-11-06, framework `caeecf6`); extended
by the story checkpoints T010, T015, T020, T023, T031, T038, T049, T053 and T056; final pass in T063.

## Summary

- **Phase A — backend.** A GM console on another origin of the same site (case A) can use the GM API: CORS exposes
  `X-Request-Id`, `Retry-After` and `ETag` and caches preflights 7200 s; every `/gm/**` answer to the configured origin,
  errors included, carries the CORS headers (proven by a permanent pipeline test); the session and CSRF cookies are
  `__Host-gs_gm` / `__Host-gs_gm_csrf` (`Path=/`, `Secure`) outside Development and `gs_gm_dev` / `gs_gm_dev_csrf` in
  Development, with `SameSite` from `Gm:Cookies:SameSite` (default `Lax`) instead of "CORS on"; SSO returns to
  `Gm:Console:BaseUrl` plus a validated console path and lands denials on `BaseUrl#/login?error=sso_denied`; `/gm/v1/ui` is
  mapped only where development tools are on (Development/Test).
- **Phase B — console.** New `gm-console/` npm workspace (outside the .NET solution; `dotnet build`/`test` need no Node): the
  framework's React + TypeScript + Vite + Mantine console shell `@gameserver/gm-shell` (typed client generated from
  `docs/api/gm-openapi.json`, session with the Q19 guard and version-skew warning, catalog-driven navigation, read pages for
  players, receipts, staff and ops; the only writes are sign-in and sign-out), the default app `@gameserver/gm-console-default`
  (build once, deploy with a `config.json` per environment), the static-host reference `deploy/` (one header source, strict
  CSP with `style-src 'self'`), `npm run verify` and an end-to-end smoke on the system Chrome/Edge against run-local.
- **No route, request/response body, proto, Couchbase or Unity change; the GM document `docs/api/gm-openapi.json` is
  unchanged; every staff member signs in again once** (new cookie names, BC-1).

## Breaking changes

| # | Change | Who is affected | How it is surfaced | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | Cookie names and path: `__Host-gs_gm` / `__Host-gs_gm_csrf` (`Path=/`, `Secure`) outside Development, `gs_gm_dev` / `gs_gm_dev_csrf` (`Path=/`) in Development (FR-012, FR-013) | Every signed-in staff member; tools or scripts that read or send `gs_gm` | 401 until the next sign-in (once per browser) | Sign in again; scripts use the new names (PCR gm) |
| BC-2 | Public constants `GmCookieAuthenticationHandler.CookieName` and `GmCookies.CsrfCookieName` replaced by `GmCookies` name members (FR-017) | Game code or tests that referenced them | Compile error | Use `GmCookies.SessionName(options)` / `CsrfName(options)` or the constants of the environment |
| BC-3 | CORS origins no longer imply `SameSite=None`; `Gm:Cookies:SameSite` decides, default `Lax` (FR-010) | A deployment that relies on a cross-site console (case B, unsupported) | Session cookie not sent cross-site; `Gm:Cookies:SameSite = None` fails startup outside Development/Test | Move to case A |
| BC-4 | A host that sets `Gm:Cors:AllowedOrigins` must also set `Gm:Console:BaseUrl` with the same origin (FR-020, FR-021) | Only hosts that already configured a CORS console origin (none known; zh-pvp sets none) | Startup fails with one line | Add the console URL of that origin; hosts without CORS/console settings need nothing |
| BC-5 | Outside Development, at most one CORS origin (FR-003) | A host that lists several console origins | Startup failure line | Keep the environment's one console origin |
| BC-6 | `/gm/v1/ui` only where development tools are on (FR-030) | Staff who opened it on beta/uat/live (it could not sign in there) | 404 | Use the console |
| BC-7 | SSO targets: with `BaseUrl`, `returnUrl` is a console path and success/denial redirect to the console; without it the defaults are `/gm/v1/ui` (development tools on) or `/gm/v1/me` (elsewhere), denial + `?error=sso_denied`, instead of the 404 `/gm/` (FR-022..FR-025) | Bookmarks or tools that start SSO with `returnUrl=/gm/...` on a host that sets `BaseUrl`; staff landing after SSO on a host without console | Redirect to the console, or to `/gm/v1/me` instead of a 404 | Start SSO from the console when one exists |
| BC-8 | Route catalog: `gm.ui` gains the condition `development_tools` (FR-030) | Game tests that pin the mapped routes per flavor | Pinned list differs on Staging/Production flavors | Remove `gm.ui` from non-development pins |
| BC-9 | CORS answers gain `Access-Control-Expose-Headers`; preflight cache 600 s → 7200 s (FR-001) | None known (additive) | New headers | — |

Phase B adds no breaking change for an existing consumer: `gm-console/` is a new folder outside the .NET solution; the
repository gains `.gitignore`/`.editorconfig` entries for it; the run-local banner of the sample host gains one line
(`  GM console   http://localhost:5173  (cd gm-console; npm ci; npm run dev)`), pinned by `RunLocalScriptTests`.

## First-consumer migration (zh-pvp-server)

Checked read-only (`E:\ProjGits\zh-pvp-server`, T003): no reference to `gs_gm`, `CookieName`, `CsrfCookieName`,
`DefaultReturnUrl`, `Gm:Cors` or `gm.ui` in `src/` or `tests/`; `src/ZhPvp.Host/Properties/launchSettings.json` opens
`gm/v1/ui` in its `local` profile (`ASPNETCORE_ENVIRONMENT=Local`, a Development flavor: the page stays there, BC-6 does not
apply).

1. **No new setting is needed to bump** in any environment: Beta/Uat/Live without `Gm:Cors`/`Gm:Console` start as today (no
   console; SSO lands on `/gm/v1/me`).
2. Optional: add `Gm:Cors:AllowedOrigins = ["http://localhost:5173"]` and `Gm:Console:BaseUrl = "http://localhost:5173/"` to
   the Local (and Dev) flavor to run the default console locally.
3. When a console domain exists (Q16), set the console origin and `Gm:Console:BaseUrl` together per environment (BC-4).
4. Tell staff they will sign in again once (BC-1).
5. Optional, later: the console CI job of consumer guide §15 (Q18) once a static host exists.

## Phase A

### US1 — CORS for a console on another origin of the same site

- **Exposed headers** (BC-9, FR-001): the GM policy (`gs-gm`, only when `Gm:Cors:AllowedOrigins` is set) adds
  `Access-Control-Expose-Headers: X-Request-Id,Retry-After,ETag` (`GmCors.ExposedHeaders`) to actual answers, so a console
  reads the request id of every error, the retry delay of 429/503 and the catalog `ETag`.
- **Preflight cache** 600 s → **7200 s** (`GmCors.PreflightMaxAge`); allowed methods (`GET,POST,PUT,DELETE`), allowed
  request headers (`Content-Type,X-CSRF-Token,X-Request-Id`), exact-origin matching and credentials unchanged.
- **One console origin outside Development** (BC-5, FR-003): startup fails with
  `Gm:Cors:AllowedOrigins lists <n> origins in '<DeploymentEnvironment>'; list the one console origin of this environment.`
  Development accepts several. Each origin must be exactly the serialized origin a browser sends (review T024 m1): an
  explicit default port (`https://gm.x.com:443`), user info, a path, query, fragment or trailing slash fails with
  `Gm:Cors:AllowedOrigins '<value>' must be an origin (scheme://host[:port]) without default port, user info, path, query or trailing slash.`
  (CORS and the CSRF `Origin` check compare strings, so such a value could never match).
- **CORS headers on every answer, errors included** (FR-002, H14): proven by the permanent pipeline test
  `GmCorsErrorHeadersTests` for 200, 304, 400, 401, 403, 403 `CSRF_FAILED`, 404 (endpoint and routing), 405, 415, 429 (GM
  limiter), 500 (exception middleware) and 503 (GM request timeout), for the configured origin and none for another origin.
  **H14 was not reproduced on ASP.NET Core 10.0.14**: `CorsMiddleware` applies its result in `Response.OnStarting`, which
  `ErrorResponseWriter`'s `Response.Clear()` keeps; `ErrorResponseWriter` and the pipeline are unchanged (T007). The test is
  red when the `/gm` `UseCors` branch is removed.
- **Sibling origin of the same site** (FR-004, no behaviour change): a cookie mutation from another origin with
  `Sec-Fetch-Site: same-site` and a valid token is still 403 `CSRF_FAILED` (pinned by a test).

### US2 — cookies

- **Names, path and `Secure`** (BC-1, FR-012, FR-013), from `GmCookies` (FR-017):

  | Deployment environment | Session cookie | CSRF cookie | `Path` | `Secure` |
  | --- | --- | --- | --- | --- |
  | outside Development | `__Host-gs_gm` (`GmCookies.HostSessionName`) | `__Host-gs_gm_csrf` (`HostCsrfName`) | `/` | always (antiforgery policy `Always`) |
  | Development | `gs_gm_dev` (`DevelopmentSessionName`) | `gs_gm_dev_csrf` (`DevelopmentCsrfName`) | `/` | session: on HTTPS or with `SameSite=None`; CSRF: `SameAsRequest` (`Always` with `None`) |

  All `HttpOnly`, no `Domain`. `GmCookies.SessionName(GameServerOptions)` / `CsrfName(GameServerOptions)` return the names of a
  deployment; `GmCookies.Session(HttpContext, DateTimeOffset?)` is used for every write and deletion (local sign-in, SSO
  callback, logout), so a deletion carries the attributes of the write (FR-014). OIDC correlation/nonce cookies are
  unchanged (FR-016).
- **Why Development keeps names without `__Host-`**: a `__Host-` cookie must be `Secure`, and ASP.NET Core antiforgery
  throws on a plain-HTTP request when its cookie policy is `Always`; run-local (also `-Lan`) and the in-process test clients
  use plain HTTP. The names also differ from the pre-018 `gs_gm` / `gs_gm_csrf` (`Path=/gm`), so a leftover cookie is
  never sent ahead of the new one (research R1, pinned by `GmCookiePolicyTests`).
- **`SameSite` decoupled from CORS** (BC-3, FR-010, FR-011): new setting `Gm:Cookies:SameSite` (`GmOptions.Cookies`,
  `GmCookieOptions.SameSite`): `Lax` (default) or `None`, case-insensitive. Startup lines:
  `Gm:Cookies:SameSite '<value>' is not supported; use Lax (default) or None.` and
  `Gm:Cookies:SameSite=None needs Gm:Cors:AllowedOrigins: SameSite=None is only for a console on another site (case B, unsupported).`
  **`None` is allowed only in Development/Test** (review T024 m2; case B is not supported): elsewhere startup fails with
  `Gm:Cookies:SameSite=None is refused in '<DeploymentEnvironment>': a console on another site (case B) is not supported; use Lax (default) with a console on the same site.`
  `None` makes both cookies `Secure` (in Development over plain HTTP the CSRF route then fails: `None` needs HTTPS).
- **One-time sign-out** (FR-015): pre-018 cookies are ignored (no fallback read, no migration); their server-side tickets
  expire as usual; every staff member signs in again once.
- **Removed** (BC-2): `GmCookieAuthenticationHandler.CookieName` and `GmCookies.CsrfCookieName`.

### US3 — SSO returns to the console

- **New setting `Gm:Console:BaseUrl`** (`GmOptions.Console`, `GmConsoleOptions.BaseUrl`; BC-4, FR-020, FR-021): the console root
  of this environment. Rules (startup lines):
  - `Gm:Console:BaseUrl '<value>' must be an absolute http(s) URL whose path ends with '/', without query, fragment or user info.`
    (also refused: characters outside printable ASCII, since the value is written into `Location` headers);
  - `Gm:Console:BaseUrl '<value>' is not https; plain http is allowed only in Development.`
  - `Gm:Console:BaseUrl origin '<origin>' is not in Gm:Cors:AllowedOrigins; add it (one console origin per environment).`
    (scheme, host and port compared without case);
  - `Gm:Cors:AllowedOrigins is set but Gm:Console:BaseUrl is not; set the console URL of that origin (SSO returns there).`
  - **Neither setting = no console**: valid in every environment, with or without SSO (a game bumps without new settings). An
    empty value counts as unset.
- **SSO targets** (BC-7, FR-022..FR-025), owned by the new public singleton `GmConsoleUrls` (`Sso/GmConsoleUrls.cs`, registered
  by `GmSso.Add`; members `AcceptReturnPath`, `SuccessTarget`, `DeniedTarget`, constants `DevelopmentDefault` `/gm/v1/ui`,
  `NoConsoleDefault` `/gm/v1/me`, `DeniedQuery` `?error=sso_denied`, `DeniedFragment` `#/login?error=sso_denied`):

  | Case | With `Gm:Console:BaseUrl` = `B` | Without (no console) |
  | --- | --- | --- |
  | accepted `returnUrl` = `r` | `B` + `r` without its leading `/` (a sub-path console keeps its path) | `r` (must start with `/gm/`) |
  | missing or refused `returnUrl` | `B` | `/gm/v1/ui` with development tools, else `/gm/v1/me` |
  | every denial (no issuer/subject, no MFA, not a staff member, disabled, remote failure such as a forged state, unhandled callback) | `B#/login?error=sso_denied` | that default + `?error=sso_denied` |

  Accepted `r`: 1..512 characters, each printable ASCII (0x21–0x7E), none of `//`, `\`, `..`, `%2e` (any case; browsers
  resolve `%2e%2e`, `.%2e`, `%2e.` as `..`). **With a console only `/` or `/#<fragment>`** (the console's hash route: no other
  path segment, no query, so a sub-path console is never left); without a console a path under `/gm/` (review T024 M1). The OIDC
  properties keep only the relative path (`/` when refused, never empty: the OIDC handler would otherwise keep the start URL
  itself); the absolute target is built in the callback and the value is checked again there. The security log line of a
  denial is unchanged.
- **Removed** (BC-7): `GmSso.DefaultReturnUrl`, `GmSso.DeniedRedirect`, `GmSso.SafeReturnUrl`.
- **Sample host** (D12): `appsettings.Local.json` and `appsettings.Dev.json` set `Gm:Cors:AllowedOrigins = ["http://localhost:5173"]`
  and `Gm:Console:BaseUrl = "http://localhost:5173/"`; `Beta`, `Uat`, `Live` set none (no console yet, Q16).
- **zh-pvp steps**: none needed to bump; see "First-consumer migration" above.

### US4 — `/gm/v1/ui` only with development tools

- **`gm.ui`** (BC-6, BC-8, FR-030): `GmRoutes.Ui` is declared with `condition: RouteConditions.DevelopmentTools` and
  `GmModule.MapGmApi` maps it only where development tools are allowed (Development/Test, like `gm.auth_login`); on
  Staging/Production flavors `/gm/v1/ui` answers 404. Content, CSP and `no-store` of the page are unchanged; it stays out of
  the GM document. Game tests that pin the mapped routes of a Staging/Production flavor drop `gm.ui`.
- **Flavor matrix** (`SampleHostFlavorTests`, Couchbase): Dev serves the page and sets `gs_gm_dev_csrf` (`path=/`); Beta
  (committed file, no console settings) starts, `/gm/v1/ui` 404, `__Host-gs_gm_csrf` (`path=/; secure; samesite=lax;
  httponly`) over HTTPS, no CORS for any origin; Uat with a console origin from deployment settings answers its preflight
  (max-age 7200) and no other; Live still reports only the `couchbases://` violation.

### Phase A gate

T023 (orchestrator, 2026-11-06): Release build 0 warnings; full suite including Couchbase 3011 tests, 2984 passed, 27
skipped, 0 failed (after `ConfigPhasePerformanceTests` became opt-in, see "Test infrastructure"); `docs/api/gm-openapi.json`
and the protos unchanged. Security review T024: r1 CONDITIONAL (1 Major, 4 minor, all fixed below), r2 YES; carried over to
Phase B: every hash parameter is encoded into one API path segment and the console never navigates to a URL taken from the
hash (done in US6/US7: `api/paths.ts`, tests with `..%2F..%2Fstaff`, `../staff`, `..`).

### Review (T024) r1 — security review findings, all fixed

Verdict CONDITIONAL; each fix has a test that is red with the fix disabled (scripted, reverted).

- **M1 — open redirect through encoded dots** (`GmConsoleUrls`): `%2e%2e`, `.%2e`, `%2e.` passed the `..` rule and browsers
  resolve them, so a sub-path console could be left (`/%2e%2e/other/`) and, without a console, `/gm/%2e%2e/x` landed on `/x`;
  a console also accepted any path and query. Now `%2e` is refused in any case, and with a console only `/` or
  `/#<fragment>` is accepted. Tests: `GmConsoleUrlsTests` (hostile list + `A_console_accepts_only_its_root_and_hash_routes`),
  `SsoTests.A_sub_path_console_is_never_left_through_the_return_url`, `SsoTests.A_forged_state_or_an_external_return_url_gets_nowhere`
  (`/gm/%2e%2e/x`, `/gm/.%2E/x`). Spec FR-022, US3 scenario 2, data-model §4 and contract §4 narrowed accordingly.
- **m1 — origin form** (`GmCorsValidation`): see US1. Tests: `GmOptionsValidatorTests.An_origin_not_in_its_serialized_form_fails`,
  `CorsTests.Bad_origins_fail_startup` (`https://gm.example.com:443`).
- **m2 — `SameSite=None` outside Development/Test** refused (see US2, BC-3, D3). Test:
  `GmOptionsValidatorTests.Samesite_none_is_refused_outside_development_and_test`.
- **m3 — session cookie name outside Development**: `GmCookiePolicyTests.Outside_development_the_session_cookie_is_read_ended_and_deleted_by_its_host_name`
  runs local sign-in, `/me`, sign-in again (ending the previous session) and logout on a `Test` deployment (development
  tools, `__Host-` names) over HTTPS; red when the authentication handler, `GmSessionService.EndCurrentAsync` or the logout
  delete uses the Development name. `The_names_follow_the_deployment_environment` pins the names for Development, Test,
  Staging and Production.
- **m4** — the `GmConsoleUrls` class comment states the real rule.

### Test infrastructure

- `ConfigPhasePerformanceTests.The_phase_on_the_sample_config_stays_within_the_budget` (slice 015) is now opt-in
  (`GAMESERVER_PERF=1`), like the other timing budgets; budget unchanged (median ≤ 5 × 20 ms). It failed the full Couchbase
  run of the Phase A gate and is the likely cause of the unexplained Platform.Web failure of 017: its median varied 12–130 ms
  over 13 runs of the test alone (one failure) and 3–33 ms inside its project, so it is timing noise, not a regression.

## Phase B

### US5 — workspace, toolchain, codegen, dev loop

- New npm workspace `gm-console/` (outside the .NET solution; `dotnet build`/`dotnet test` need no Node, FR-046):
  root `@gameserver/gm-console-workspace`, shell `packages/shell` = `@gameserver/gm-shell` (TypeScript source through
  `exports`, runtime libraries as exact `peerDependencies`), default app `app` = `@gameserver/gm-console-default`
  (`createGmConsole()` only). All three versions = `GameServerFrameworkVersion` (0.7.0), pinned by
  `VersionTests.The_gm_console_packages_carry_the_declared_version`; bump them with `Directory.Build.props` and the Unity
  package.
- `.npmrc` `engine-strict=true`, `save-exact=true`; `.nvmrc` `22.20.0`; `engines.node >=22.20.0`; lockfile v3 committed;
  `node_modules`, `dist` and e2e artifacts ignored (`.gitignore`) and never tracked (`GmConsoleWorkspaceTests`).
- Versions (rule FR-043: newest stable ≥ 14 days old that runs on Node 22.20, peers satisfied; table in tasks T025):
  React 19.3.1, Mantine 9.7.3, TanStack Query 5.104.5, React Router 7.18.4, openapi-fetch 0.17.0, openapi-typescript 7.13.0,
  TypeScript 5.9.3, Vite 8.3.5, @vitejs/plugin-react 6.1.2, Vitest 5.0.5, jsdom 29.1.1, Testing Library (react 16.3.3, dom
  10.4.2, user-event 14.6.8, jest-dom 7.0.1), ESLint 10.13.0 + typescript-eslint 8.72.0 + react-hooks 7.1.1, playwright-core
  1.64.1, postcss 8.5.31 + postcss-preset-mantine 1.18.0, @types/react(-dom) 19.3.0, @types/node 22.20.5, globals 17.13.0.
  Not newer because: TypeScript 6/7 are outside the typescript-eslint and openapi-typescript peers; React Router 8 and jsdom
  30 need Node ≥ 22.22. jsdom 29 accepts Node 22.20, so it replaces the planned 28.
- **npm 10.9.3 workaround**: the root `package.json` has `"overrides": { "vitest": "5.0.5" }`. Without it `npm install`
  fails with `Cannot read properties of null (reading 'edgesOut')` (Arborist peer-set bug) through vite's optional peer chain
  `@vitejs/devtools` → `@vitejs/devtools-vitest` → `vitest@*` → `@vitest/browser-playwright`; any vitest 4/5 with vite 8
  hits it. A game workspace root (GM-4) needs the same override.
- Codegen: `npm run gen:api` writes `packages/shell/src/generated/gm-api.d.ts` from `docs/api/gm-openapi.json`
  (openapi-typescript, LF, no timestamp); `npm run check:api` fails when the file is not the generator's output or differs
  from git.
- Lint (FR-048): `dangerouslySetInnerHTML`, `innerHTML`, `eval`, implied eval, `new Function` and the storage APIs
  (`localStorage`, `sessionStorage`, `indexedDB`, also through `window`/`globalThis`/`self`) are errors.
- Dev loop: `tools/local/run-local.ps1` (or `.sh`), then `cd gm-console; npm ci; npm run dev` → `http://localhost:5173`
  (strict port, no proxy), `app/public/config.json` = `{ apiBaseUrl: http://localhost:5080, gameId: sample, environmentId:
  local }`. The run-local banner gains `  GM console   http://localhost:5173  (cd gm-console; npm ci; npm run dev)`.
- `npm run verify` = `check:api`, `lint`, `typecheck`, `test`, `build`, `check:dist`.

### US6 — session shell

- `config.json` (next to `index.html`, `cache: 'no-store'`): `apiBaseUrl` absolute http(s) without query, fragment or user
  info (trailing `/` ignored), `gameId`/`environmentId` 1–64 characters, unknown members ignored. An invalid or missing file
  shows the wrong field and makes no API call. Read relative to the document, so a console under a sub-path works.
- Typed client: `openapi-fetch` over the generated types, `credentials: 'include'`; GETs carry **no** added header (no
  preflight); other methods carry `X-CSRF-Token` (and `Content-Type` only with a body); `403 CSRF_FAILED` on a mutation →
  one token refresh + one retry with the same body (D15); 401 → login, keeping the page in memory (never in the URL); 403 →
  shown + one `/me` refresh per navigation; no answer → "No answer from the GM API at <origin>", never "signed out".
- Path parameters are encoded into exactly one segment; empty, `.` and `..` values are refused before any request, so a hash
  value cannot change which endpoint is called (Phase A review carry-over).
- Start: `config.json` → `auth/methods` → `/me` → guard. `/me` with another `gameId`/`environmentId` than `config.json`
  → block screen naming both, nothing else loads, sign-out only (Q19). Framework `major.minor` differing from the build →
  warning banner; different game version → footer line; nothing blocks on versions.
- Local sign-in: CSRF → `POST /auth/login`; `/me` is the login answer; the anonymous token is dropped and the session-bound
  token is fetched lazily before the next mutation (review T068 m3). 401 →
  "Invalid staff id or password." SSO: top-level `{api}/gm/v1/auth/sso/start?returnUrl=<encoded '/#…'>`; the
  `#/login?error=sso_denied` fragment shows the denial.
- Sign-out: `POST /auth/logout` with the token (401 = already ended), memory cleared, login page, new anonymous CSRF token.
- Header: environment badge (red for `live`, plus a fixed red strip), game id, staff name, sign-out; footer: versions.
- Nothing is stored in the browser (lint-enforced); Mantine's colour-scheme manager is replaced by an in-memory one (T048).

### US7 — navigation and read pages

- After the guard the console loads `/gm/v1/console/openapi.json` once per session (no custom header; TanStack Query
  `staleTime: Infinity`, dropped on 401/sign-out) and parses its operations by `operationId`. A catalog failure shows an error
  screen with retry.
- Navigation from an internal registry (design §7.3 shapes, not exported in GM-2, D9): a page or tab is listed exactly when the
  staff member holds its permission and the catalog serves its routes; a hidden page reached by URL says "not available" and
  calls nothing. `#/` and unknown hashes open Players (else the first visible page).
- Pages (hash routes): `#/players` (lookup by player id, by mapping key — encoded in the API path —, search by display
  name, each mode per its route; the key and the name never enter the URL), `#/players/{id}` (aggregates, deletion, state
  JSON; tabs Receipts, Audit, Ledger — Ledger only with `economy.ledger`, D11), `#/receipts/{id}`, `#/staff`,
  `#/staff/{id}` (+ Audit tab), `#/ops` (health, version, gate cards). Paged lists: `pageSize=50`, "Load more" with the
  cursor; a failed page keeps loaded rows. Player tabs and Ops cards are independent reads (A10).
- **No business mutation (D10)**: the only writes are sign-in, sign-out and the CSRF token fetch; a source-scan test fails when
  any module outside `session/` sends POST/PUT/DELETE/PATCH. Staff, gate, key, resource, ban and game commands come with
  the generic forms of GM-3.
- Server data is rendered as text only (JSON views are `JSON.stringify(value, null, 2)` in a `<pre>`).

### US8 — static host reference

- `gm-console/deploy/`: `security-headers.json` (the single source), `nginx.conf.example`, `headers.md` (CDN table and notes),
  `config.example.json`, `config.schema.json`; `deploy.test.ts` fails when the nginx example or the table differ from the JSON.
- Headers: CSP `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' {{apiOrigin}}; img-src 'self' data:;
  font-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`, HSTS one year,
  `nosniff`, `Referrer-Policy: no-referrer`, COOP `same-origin`, `Permissions-Policy`; `no-cache` for `/`,
  `/index.html`, `/config.json`; `public, max-age=31536000, immutable` for `/assets/*`. They must be HTTP response headers.
- **V3: `style-src 'self'` holds** (no `'unsafe-inline'`): the shell runs Mantine without global classes, without runtime
  CSS variables (the default theme's are in the static `styles.css`) and without responsive style props. A game theme (GM-4)
  must ship its variables as static CSS.
- The build is environment-free: relative asset URLs (`base: './'`, a console may live under a sub-path), no `config.json`
  in `dist/` (`app/public/` serves the dev loop only), no local API origin; `npm run check:dist` enforces it and the
  500 KB gzip budget (now 185.2 KB). `npm run preview` serves `dist/` with the reference headers and a `config.json`
  (`GM_CONSOLE_CONFIG`, `GM_E2E_API`).

### End-to-end smoke

- `gm-console/e2e/` (`npm run e2e`, run-local started first): Vitest + `playwright-core` with the system Chrome (Edge through
  `GM_E2E_BROWSER`), never a browser download; targets `dev` (Vite dev server) and `preview` (built app under the reference
  headers), both on `http://localhost:5173`. Results (Chrome 157 and Edge 156, both targets green): cross-origin sign-in with
  `Origin: http://localhost:5173`, `Sec-Fetch-Site: same-site`, the CSRF token on the POST; `Lax`, HttpOnly, `Path=/`
  Development cookies; one catalog request; only the login and logout POSTs preflighted; readable `X-Request-Id` on a 404;
  sign-out then `/me` 401; the Q19 block screen with a `beta` `config.json`; 0 CSP violations; first page about 0.1 s after
  the login answer; after a reload the catalog is revalidated with `If-None-Match` → 304.

### Review (T068) and fixes (T069)

Tech-lead review T068: CONDITIONAL (2 Major, 8 minor, all accepted); docs walkthrough T065: passed with 6 minor doc findings.
All fixed in T069; each code fix has a test shown red with the fix undone (scripted, restored).

- **M1 — mutation boundary** now has three layers: lint rules on the AST (`gm-console/eslint.rules.js`: any `.POST/.PUT/.DELETE/
  .PATCH/.request(`, computed `api['POST']`, template members, `method: 'post'` or a template method in shell source; in
  `session/` only `.POST('/gm/v1/auth/login' | '/gm/v1/auth/logout')` with a literal path), the source-scan test, and a
  **run-time guard** in the client middleware: a non-GET/HEAD request whose path (relative to the `apiBaseUrl` path) is not
  the login or logout route is refused before it is sent with `MUTATION_NOT_ALLOWED`. **GM-3 replaces this allowlist with the
  catalog check of its generic forms.**
- **M2 — dependency confusion**: `gm-console/.npmrc` gains `@gameserver:registry=https://registry.invalid/` (workspaces still link
  locally; a registry lookup of the scope fails); `GmConsoleWorkspaceTests` pins the app's `@gameserver/gm-shell` dependency =
  framework version, the lockfile link to `packages/shell` and the lockfile versions. A framework bump updates every
  `package.json` version, the app's shell dependency, then `npm install` in `gm-console`.
- m1 a network failure on the CSRF retry is "No answer from the GM API"; m2 concurrent token refreshes share one request and a
  sign-in/out never reuses an older one; **m3 the session token is fetched lazily after a sign-in** (a failed fetch no longer
  hides a live session behind the login page); m4 lint also forbids `document.cookie`, `document.write/writeln`, `outerHTML`,
  `insertAdjacentHTML`, `srcdoc`/`srcDoc`, computed storage access; the smoke checks `document.cookie === ''` and no IndexedDB
  database; m5 the smoke asserts V13 (304), no CSP violation on the blocked screen and renders the player page under the CSP;
  m6 `GM_E2E_API` reaches the preview target through a generated `config.json` (the dev target is skipped for another API) and
  run-local prints the console line only on port 5080; m7 static-host path classes are relative to the console base path
  (sub-path nginx adaptation in `deploy/headers.md`); m8 exact versions exclude prereleases.
- Docs walkthrough: wait for run-local's ready line, one expected 401 in the browser console, what each page shows, stop
  `npm run dev` before `npm run preview` (local `config.json` values, `Remove-Item Env:GM_CONSOLE_CONFIG`), the Chrome default
  path, the 500 KB gzip budget versus Vite's raw size.
- Review r2 (YES) follow-ups: lint also forbids any use or destructuring of `POST/PUT/DELETE/PATCH/request` members, calls
  through a non-literal computed member and raw `fetch` in shell source (except the client, the config loader and the
  session's default); a sign-in then sign-out test pins the lazy session token; docs: a console's origin serves nothing but
  the console (a sub-path is for a dedicated host, e.g. `https://gm.beta.example.com/console/`), a game root needs its own
  `.npmrc` scope line; spec FR-052/FR-054 amended.
- **Carried to GM-3 (no code change now)**: `parseCatalog` reads a missing or unknown `x-gm-kind` as `query`; GM-3 must treat an
  unknown kind on a non-GET operation as `command` (confirmation, reason, request id).

 (listed for review; the spec was amended where noted)

- `config.json` is read relative to `index.html` and the app is built with relative asset URLs (`base: './'`), so a console
  under a sub-path works; `dist/` never contains `config.json` (`copyPublicDir: false`, `app/public/` serves the dev loop).
- Local sign-in takes `/me` from the login answer (no extra `/me` call) and fetches the session token lazily: spec FR-052 and
  FR-054 amended in place (T034/T069, review r2).
- A session phase `api-error` (no usable answer at start) shows "No answer from the GM API at <origin>" with retry instead of
  the login page.
- Path parameters: one encoded segment each; empty, `.` and `..` refused before any request (`INVALID_PATH_PARAMETER`).
- Mantine runs without global classes, runtime CSS variables or responsive style props (V3), with an in-memory colour-scheme
  manager (its default uses `localStorage`).
- npm 10.9.3 needs `"overrides": { "vitest": "5.0.5" }` at the workspace root (Arborist bug through vite's optional peers).
- The e2e smoke ignores only the browser's own "Failed to load resource" log of the 401/404 answers it causes on purpose.

### Requirements (owner's file, not committed)

`Requirements/Game_Server_System_Requirements_v1.2.md` (T062): rows TBD-05a, SEC-04, SEC-06, TBD-21 to the case-A decision
with `Chốt 2026-11-05`; G6, the status line and the TBD summary sentence restated; SEC-13 and the appendix line "Rate limit
GM API" narrowed to Q9 (per-IP ≤ 30/min only on `/gm/v1/auth/*`; other GM routes keep the `gs-gm` concurrency limiter; the
code has no per-IP GM limiter, so nothing exceeds Q9). **GM-01 is not edited**: it still says "P0 … resource adjust" in the
GM frontend, while D10 keeps every business mutation for GM-3 (the Development page `/gm/v1/ui` still offers the P0 adjust);
the owner decides whether GM-01's P0 wording changes.

## Decisions awaiting owner override

Each is applied; owner confirmation pending (plan "Owner questions").

1. **D4**: Development cookie names `gs_gm_dev` / `gs_gm_dev_csrf` (production `__Host-…`) — applied, owner confirmation pending.
2. **D2 / BC-5**: one console origin per environment outside Development — applied, owner confirmation pending.
3. **D3**: changed by review T024 m2 (orchestrator default, in line with the owner's "case B not supported"): `SameSite=None`
   is refused outside Development/Test. Owner may still override.
4. **D10**: no business mutation in the console until GM-3 — applied, owner confirmation pending.
5. **D14**: end-to-end smoke in GM-2 with the host started outside the test — applied, owner confirmation pending.

## Verification items

| Item | Result |
| --- | --- |
| V2 | Confirmed on the real pipeline (T004): CORS headers on all 12 tested answers for the configured origin, none for another; no writer change (T007). |
| V3 | Answered (T050): 2 violations first (Mantine's CSS-variables `<style>`, `SimpleGrid`'s responsive `<style>`); after R10 option (1) 0 violations → `style-src 'self'` kept. |
| V4 | Re-confirmed in the e2e smoke (T055): `gs_gm_dev`/`gs_gm_dev_csrf` set on the CORS answer, sent on credentialed same-site fetches (`Lax`), deleted by sign-out; Chrome 157 and Edge 156. |
| V5 | Confirmed in the repository (T014): `__Host-` names + `Path=/` + antiforgery round trip over HTTPS on a Staging bare app; policy `Always` throws on plain HTTP (pinned). |
| V7 | Not run (no Firefox installed). |
| V8 | Answered (T025): versions above; one npm 10.9.3 Arborist bug worked around with `overrides` (no older line needed). |
| V9 | Confirmed (T064, scratch): a game workspace `["gm-console", "framework/gm-console/packages/shell"]` installs the shell's exact peers once at the game root (one React) and builds; friction: the shell's `tsconfig.json` extends `../../tsconfig.base.json`, so a game consumes the whole `framework/gm-console` folder (a submodule does), never `packages/shell` alone. |
| V11 | Confirmed (T052) with a preview plugin; it must override the headers the static middleware passes to `writeHead` (assets showed `no-cache` before). |
| V12 | Confirmed (T028): two runs byte-identical, LF, no timestamp; `gen:api` still normalizes to LF as a guard. |
| V13 | Confirmed (T056): the browser cache revalidates the catalog with `If-None-Match` and gets 304 under credentialed CORS (Chrome 157, Edge 156). |

## Docs touched

- `docs/pcr/gm.md` (T057): cookies per environment, `Gm:Cookies`, `Gm:Console`, CORS rules, SSO targets, startup lines,
  `gm.ui` condition, new section "Console hosting (slice 018 — GM-2)".
- `docs/pcr/platform.md` (T058): §9 (`/gm/v1/ui` Dev/Test), §14 CORS, new §24 "GM console: hosting và shell" with the
  Breaking (slice 018) list.
- `docs/guides/gm-console.md` (new, T059): local loop, configuration per environment, static-host deployment, skew, security,
  tests, troubleshooting, GM-3/GM-4.
- `docs/guides/consumer-guide.md` (T060): §11 bump lines for BC-1, BC-2, BC-4, BC-8; new §15 "GM console for a game" (Q18 CI
  recipe).
- `docs/html/01-gioi-thieu.html`, `docs/html/03-deploy.html` §11, `README.md`, `docs/guides/environments-and-local-run.md`
  (T061).
- `gm-console/deploy/headers.md`, `gm-console/e2e/README.md` (new, with the code).
- Owner's `Requirements/` file (T062, not committed).

## Tests

- Phase A: see the checkpoints above (Gm.Tests 333 after the review fixes; full gate 3011 / 2984 passed / 27 skipped).
- Phase B, .NET: `GameServer.Architecture.Tests` 80 (72 passed, 8 opt-in `RunLocalScriptTests` skipped; +11:
  `GmConsoleWorkspaceTests` 9, `VersionTests` 2); the opt-in run-local banner case green on PowerShell and Bash.
- Phase B, console (`npm run verify`): check:api, lint, typecheck, Vitest 20 files / 164 tests (shell 149, deploy 15), build,
  check:dist 185.2 KB gzip.
- End-to-end (`npm run e2e`, run-local running, both targets in one run): 2 files, 19 passed + 1 skipped (the V13 step runs
  on the preview target only), on Chrome 157 and on Edge 156.
- Final counts (full .NET suite in memory, File store and Couchbase; `npm ci` + `verify`; e2e) are recorded by T066/T067.

## Next tag

`v0.8.0` proposed together with 017; no version bump or tag in this slice (D18).
