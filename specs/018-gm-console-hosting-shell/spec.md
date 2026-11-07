# Feature Specification: GM console hosting and shell — a GM API usable from a console on another origin of the same site (CORS, `__Host-` cookies, SSO return to the console, Dev/Test-only static page) and the framework's React console shell with its default app, read pages, static-host reference and local end-to-end loop (slice 018, GM-2)

**Feature Branch**: `018-gm-console-hosting-shell`

**Created**: 2026-11-06

**Status**: Draft

**Input**: GM tool design `AgentMemories/features/gm-tool/01_design.md` (revision 1, 2026-11-05): §6 (6.1–6.7 hosting and
security) is the core of this slice, with §7 (packaging, tiers, upgrade, version skew), §8 (TypeScript codegen), §10 (data
flow), §11 (TBD-21 and the requirement rows to edit), §12 (frontend security invariants), §14 (performance), §16 (GM-2 row),
§17 (Q16–Q21) and §18 (V2–V5, V7–V9). Summary `AgentMemories/features/gm-tool/00_summary.md` (owner answers Q16–Q21, "Ghi
chú cho GM-2"). Previous slice `specs/017-gm-api-contract/` (GM-1, merged into `main` = `47f7468`). Requirements
`Requirements/Game_Server_System_Requirements_v1.2.md` (owner's file, gitignored): GM-01, SEC-04, SEC-05, SEC-06, SEC-10,
SEC-11, SEC-12, SEC-13, TBD-05a, TBD-21, CI-01, ACC-11.

Owner decisions already taken (not reopened here):

- **Stack and browsers (Q1, Q2, TBD-21):** React + TypeScript + Vite + Mantine. Chrome and Edge desktop; Firefox
  best-effort; no Safari, no mobile.
- **Hosting (Q3, D9):** the console is a static site on its own host. Case A (other origin, **same site**, e.g.
  `https://gm.beta.example.com` → `https://gm-api.beta.example.com`) is the supported layout, so the session cookie stays
  `SameSite=Lax`. Case B (cross-site, e.g. a CDN default domain) is not supported.
- **Q16:** there is no domain yet. This slice is built and accepted with **local testing**: the Vite dev server on
  `http://localhost:5173` calls the host started by `run-local` on `http://localhost:5080`, cross-origin for real, no
  proxy. Every domain-related value is per-environment configuration; a real domain later needs no code change.
- **Q17:** from this slice `/gm/v1/ui` is served only in Development/Test; it is removed later.
- **Q18:** a game with no pages of its own builds the framework's default app from the submodule source in its CI and adds
  a per-environment `config.json`.
- **Q19:** the console blocks when `/me` reports another game or environment than its `config.json`; it warns when the
  framework minor version differs; the server is deployed first.
- **Q20:** cookies use the `__Host-` prefix with `Path=/`. Every staff member is signed out once when this ships.
- **Q21:** one slice, two phases. **Phase A** = backend (cookie, CORS, error-answer headers, SSO return URL, exposed
  headers, `/gm/v1/ui` Dev/Test only). **Phase B** = the shell. Phase B starts only when Phase A is green.
- **Process:** slices run autonomously with no owner review stop; the orchestrator commits; commit lines carry no Claude
  signature.

Decisions taken by default in this spec are marked **[DEFAULT]** and listed again under Assumptions, so the owner can
override them. Lessons of 017 applied throughout: the documents must equal the server (every doc value comes from the
tested source), every fix has a test that fails without it, and no host-wide side effect is added only to make a document
right.

## Scope note

- Verified on `main` = `47f7468` (2026-11-06; branch `018-gm-console-hosting-shell` created from it). Two runtime probes
  were run during planning in throw-away projects under the session scratch folder (research R1, R2): Chrome 157 and
  Edge 156 against a two-port `localhost` setup, and ASP.NET Core 10 (SDK 10.0.403) antiforgery and CORS behaviour.
- **Already in place (007, 013, 017):**
  - GM session cookie (`gs_gm`, server-side ticket, Data Protection), antiforgery cookie (`gs_gm_csrf`) and header
    `X-CSRF-Token`, `Sec-Fetch-Site` and `Origin` checks, both cookies `HttpOnly`, `Path=/gm`
    (`src/GameServer.Modules.Gm.Adapters/Security/GmCookies.cs`).
  - CORS for `/gm/**` with an exact-origin allowlist `Gm:Cors:AllowedOrigins`, credentials, methods `GET, POST, PUT,
    DELETE`, headers `Content-Type, X-CSRF-Token, X-Request-Id`, preflight cache 10 minutes, applied in the pipeline
    before authentication (`GmCors.cs`, `GameServerApplicationExtensions.cs:74`); allowed origins are also trusted by the
    CSRF `Origin` check (`Csrf.cs:45-70`). Startup refuses `*`, paths, a trailing `/`, and `http` origins outside
    Development.
  - OIDC sign-in (`GmSso.cs`): authorization code + PKCE, `response_mode=query`, correlation/nonce cookies on the callback
    path, return URL restricted to `/gm/...` paths.
  - Console bootstrap routes of 017: `GET /gm/v1/auth/methods` (Public), `GET /gm/v1/me` (with `frameworkVersion`,
    `gameVersion`), `GET /gm/v1/console/openapi.json` (the GM document, `gm.access`, strong `ETag`, `Cache-Control:
    private, no-cache`, 304), every framework read route described in `docs/api/gm-openapi.json` (OpenAPI 3.1.1,
    `operationId` = route name, `x-gm-*` keys, typed pages).
  - Static same-origin page `/gm/v1/ui` (`GmUi.cs`, embedded `ui/index.html`), Public in every environment.
- **Gaps this slice closes (design §1):**
  - H6: `GmSso.DefaultReturnUrl = "/gm/"` and `DeniedRedirect = "/gm/?error=sso_denied"` (`GmSso.cs:26-27`) point at a
    path no endpoint serves (404), and a console on another origin cannot be returned to.
  - H9: `/gm/v1/ui` is Public in every environment (`GmRoutes.cs:215`, `GmModule.cs:168`).
  - H15: `SameSite = Cors.Enabled ? None : Lax` (`GmCookies.cs:26, 45`): turning CORS on (needed by any console on another
    origin) forces `SameSite=None` although case A works with `Lax`.
  - H16: the policy has no `Access-Control-Expose-Headers`, so a console cannot read `X-Request-Id`, `Retry-After` or
    `ETag`; the preflight cache is 10 minutes (`GmCors.cs:78-83`).
  - Q20: no `__Host-` prefix; `Path=/gm` (`GmCookies.cs:29, 43`).
  - There is no console: no workspace, shell, default app, codegen, static-host reference or browser test.
- **Design claim not reproduced — H14** ("`ErrorResponseWriter.WriteAsync` calls `Response.Clear()` and wipes the CORS
  headers", design §1 and §6.3): on ASP.NET Core 10 the CORS middleware applies its headers to actual responses in a
  `Response.OnStarting` callback, which `Response.Clear()` does not remove. The planning probe (research R2) answered 500,
  401 (written after `Clear()` inside and outside the CORS branch) and 404 with every CORS header. This slice therefore
  **proves the behaviour with a permanent pipeline test first** (FR-002, V2) and changes `ErrorResponseWriter` only for a
  status that test shows without the headers.
- **Not included:** generic forms, confirmation dialogs, request-id policy in the browser, every business mutation in the
  console (GM-3, FR-074); the public extension registry, `overrideOperation`, templates and the "add a GM page" guide
  (GM-4; this slice keeps an internal registry of the same shape, FR-067); case B (cross-site console); a published console
  bundle or release asset (Q18 later); i18n, themes, Safari/mobile; the per-IP GM auth limit of SEC-13/Q9; `MapGmAction`. See Out
  of Scope.

## User Scenarios & Testing *(mandatory)*

Phase A = US1–US4 (backend). Phase B = US5–US9 (console, static host, docs). Phase B starts only after the Phase A gate
(tasks T023–T024) is green.

### User Story 1 - A console on another origin of the same site reads every GM answer, errors included (Priority: P1) — H14, H16, SEC-06 (Phase A)

The console at `https://gm.beta.example.com` (locally `http://localhost:5173`) calls the GM API at
`https://gm-api.beta.example.com` (locally `http://localhost:5080`) with `credentials: 'include'`. Every answer the browser
receives, success or Problem Details error, carries the CORS headers for that one configured origin, so the console can
show the error code, the field errors and the request id. It can read `X-Request-Id`, `Retry-After` and `ETag`. Mutations
are preflighted once per two hours per route. Any other origin, including a sibling page of the same site, gets nothing.

**Why this priority**: without it no console on another origin works at all; a browser reports an opaque CORS failure
instead of a 401, 403 or 400 and the console cannot tell "signed out" from "network down".

**Independent Test**:
- Start the GM test host with `Gm:Cors:AllowedOrigins:0 = https://gm.example.com`. Send requests with
  `Origin: https://gm.example.com` that answer 200, 304, 400, 401, 403, 404, 405, 415, 429, 500 and 503: each answer has
  `Access-Control-Allow-Origin: https://gm.example.com`, `Access-Control-Allow-Credentials: true` and
  `Access-Control-Expose-Headers` listing `X-Request-Id`, `Retry-After`, `ETag`. The same requests with
  `Origin: https://evil.example.com` have none of them.
- A preflight from the allowed origin answers `Access-Control-Max-Age: 7200`.

**Acceptance Scenarios**:

1. **Given** an allowed origin, **When** any `/gm/**` request from it is answered, whatever the status and whichever part
   of the pipeline writes it (endpoint, profile enforcement, rate limiter, request timeout, exception middleware), **Then**
   the answer has `Access-Control-Allow-Origin` = that origin, `Access-Control-Allow-Credentials: true` and the exposed
   header list.
2. **Given** an answer with `Retry-After` (429, 503) or `ETag` (catalog), **When** the console reads it, **Then** the
   header is readable (exposed).
3. **Given** a preflight from the allowed origin, **When** it is answered, **Then** it carries `Access-Control-Max-Age:
   7200` and the existing methods and headers.
4. **Given** a page on a sibling host of the same site (`https://evil.example.com`, `Sec-Fetch-Site: same-site`), **When**
   it sends a cookie mutation with the session cookie, **Then** the answer is 403 `CSRF_FAILED` and carries no CORS header.
5. **Given** a deployment outside Development, **When** `Gm:Cors:AllowedOrigins` lists more than one origin, **Then**
   startup fails with one line naming the count and the rule "one console origin per environment".

---

### User Story 2 - The session cookies resist cookie tossing and keep `SameSite=Lax` with CORS on (Priority: P2) — Q20, H15, SEC-04 (Phase A)

Outside Development the GM cookies are `__Host-gs_gm` and `__Host-gs_gm_csrf`: `Secure`, `HttpOnly`, `Path=/`, no
`Domain`, `SameSite` from the new setting `Gm:Cookies:SameSite` (default `Lax`) and no longer from "CORS on". A sibling
subdomain cannot plant or overwrite them. In Development (local loop, in-process tests) plain HTTP must keep working, so
the names are `gs_gm_dev` and `gs_gm_dev_csrf` with the same path and attributes except `Secure` on plain HTTP.

**Why this priority**: case A depends on `Lax` with CORS on; `__Host-` closes the cookie-tossing gap of a shared parent
domain. The cookie rename signs every staff member out once (owner accepted, Q20).

**Independent Test**:
- Development host over `http`: local sign-in answers `Set-Cookie: gs_gm_dev=…; path=/; samesite=lax; httponly`, the CSRF
  route answers `gs_gm_dev_csrf=…; path=/; samesite=lax; httponly`, with CORS on or off.
- A host built with a Staging deployment environment over `https`: the CSRF route answers
  `__Host-gs_gm_csrf=…; path=/; secure; samesite=lax; httponly`; a token from it validates a mutation; sign-in sets
  `__Host-gs_gm` with the same attributes; logout expires it with the same name, path and `secure`.
- `Gm:Cookies:SameSite = None` without CORS origins fails startup; with CORS origins both cookies are
  `samesite=none; secure`.

**Acceptance Scenarios**:

1. **Given** a deployment outside Development, **When** any GM cookie is written or deleted, **Then** its name has the
   `__Host-` prefix and it is `Secure`, `HttpOnly`, `Path=/`, without `Domain`.
2. **Given** Development, **When** any GM cookie is written, **Then** its name is `gs_gm_dev` or `gs_gm_dev_csrf`,
   `HttpOnly`, `Path=/`, `Secure` when the request is HTTPS or `SameSite` is `None`.
3. **Given** CORS origins and the default setting, **When** a GM cookie is written, **Then** it is `SameSite=Lax`.
4. **Given** `Gm:Cookies:SameSite` not `Lax` or `None` (case-insensitive), or `None` without any CORS origin, **When** the
   host starts, **Then** startup fails with one line each.
5. **Given** a browser that still holds a pre-018 cookie (`gs_gm`, `Path=/gm`), **When** it calls the GM API, **Then**
   the cookie is ignored (401 until a new sign-in), and nothing else changes.
6. **Given** logout, **When** the answer is written, **Then** the session cookie is expired with exactly the name, `Path`,
   `Secure` and `SameSite` it was written with (a `__Host-` deletion without them is ignored by browsers).

---

### User Story 3 - SSO sign-in returns to the console, never anywhere else (Priority: P3) — H6, Q16, §6.5 (Phase A)

The console starts SSO by a top-level navigation to `{api}/gm/v1/auth/sso/start?returnUrl=<console path>`. After the
identity provider, the callback sets the session cookie on the API host and redirects to `Gm:Console:BaseUrl` plus that
relative path (for example `https://gm.beta.example.com/#/players/abc`). A denied sign-in lands on
`<BaseUrl>#/login?error=sso_denied`. An absolute or protocol-relative return URL is never followed.

**Why this priority**: outside Development/Test sign-in is SSO only (SEC-03); today it ends on a 404 (`/gm/`) of the API
host, and a console on another origin has no way back.

**Independent Test**:
- Fake identity provider (as in `SsoTests`), `Gm:Console:BaseUrl = https://gm.example.com/` and the matching CORS origin:
  `returnUrl=/#/players/abc` → 302 `https://gm.example.com/#/players/abc`; `https://evil.example/x`, `//evil.example`,
  `/\evil`, `/../x`, `/%2e%2e/x`, `/?x=1`, `/other/`, a non-ASCII path and a 513-character path → 302
  `https://gm.example.com/` (a sub-path `BaseUrl` is never left); no MFA, disabled staff,
  unknown identity, forged state → 302 `https://gm.example.com/#/login?error=sso_denied`.
- Without `Gm:Console:BaseUrl` (no console in this deployment): a missing return URL → `/gm/v1/ui` where development
  tools are on, `/gm/v1/me` elsewhere; denied → the same path + `?error=sso_denied`; `/gm/v1/me` still accepted as today.
- A configuration with a CORS console origin but no `Gm:Console:BaseUrl`, or with a `BaseUrl` whose origin is not the
  configured CORS origin, fails startup. A Staging configuration with SSO and neither setting (zh-pvp beta today) starts.

**Acceptance Scenarios**:

1. **Given** `Gm:Console:BaseUrl`, **When** a sign-in succeeds, **Then** the answer is 302 to `BaseUrl` + the accepted
   relative return path (without its leading `/`), or to `BaseUrl` when the return path is missing or refused.
2. **Given** a return path, **When** it is longer than 512 characters, contains a character outside printable ASCII
   (0x21–0x7E), or contains `//`, `\`, `..` or `%2e` (any case), or — with a console — is anything but `/` or
   `/#<fragment>` (no other path, no query; review T024 M1), **Then** it is refused (no error page; the default target is
   used).
3. **Given** any denial (no issuer/subject, no MFA, not a staff member, disabled, failed remote authentication, rejected
   callback), **When** the callback answers, **Then** it is 302 to `BaseUrl#/login?error=sso_denied` with no reason in the
   URL (the reason stays in the security log as today).
4. **Given** `Gm:Console:BaseUrl`, **When** the host starts, **Then** it must be an absolute `http(s)` URL with a path
   ending in `/`, without query, fragment or user info, `https` outside Development, and its origin must be in
   `Gm:Cors:AllowedOrigins`; otherwise startup fails with one line per fault.
5. **Given** `Gm:Cors:AllowedOrigins` (a console origin), **When** `Gm:Console:BaseUrl` is not set, **Then** startup
   fails; **Given** neither setting, **When** the host starts in any environment (SSO configured or not), **Then** it
   starts: the deployment has no console and the API works as before.
6. **Given** no `Gm:Console:BaseUrl`, **When** SSO is configured and used, **Then** the legacy rule applies (return paths
   under `/gm/`), with the default `/gm/v1/ui` where development tools are on and `/gm/v1/me` elsewhere, and the denial
   `<that default>?error=sso_denied`.

---

### User Story 4 - The static GM page exists only where development tools exist (Priority: P4) — H9, Q17 (Phase A)

`/gm/v1/ui` stays the Development/Test convenience page (run-local banner, `local` launch profile) and is not mapped in
Staging or Production.

**Why this priority**: it is unused surface outside Development/Test (it signs in only through the local login, which is
Development/Test only) and the console replaces it.

**Independent Test**: the Local flavor answers 200 on `/gm/v1/ui`; the Beta, Uat and Live flavors answer 404; the route
catalog lists `gm.ui` with the condition `development_tools`.

**Acceptance Scenarios**:

1. **Given** development tools on, **When** `/gm/v1/ui` is requested, **Then** the page is served exactly as today.
2. **Given** development tools off, **When** `/gm/v1/ui` is requested, **Then** the answer is 404 and the route is not
   mapped.

---

### User Story 5 - A developer runs the console locally against run-local, cross-origin, in two commands (Priority: P5) — Q16, Q18, §6.7, §7.1, §8 (Phase B)

A developer runs `tools/local/run-local.ps1` and, in `gm-console/`, `npm ci` then `npm run dev`. The console at
`http://localhost:5173` loads its `config.json`, talks to `http://localhost:5080` with real CORS, CSRF and cookies (no Vite
proxy). The workspace pins every dependency exactly, commits its lockfile, never commits a built bundle, and regenerates
the typed client from the committed GM document with a check that fails when the generated types are stale.

**Why this priority**: every later story and slice (GM-3, GM-4) builds on the workspace, the typed client and this loop.

**Independent Test**:
- From a clean clone: `npm ci` in `gm-console/` succeeds on Node 22.20 / npm 10.9 with `engine-strict`; `npm run verify`
  (codegen check, lint, typecheck, unit tests, build, bundle check) is green.
- Edit one summary in a copy of `docs/api/gm-openapi.json` used as input: `npm run check:api` fails naming the generated
  file; restore: green.
- `dotnet build GameServer.slnx` and `dotnet test` need no Node.
- Architecture tests: every `gm-console` `package.json` uses exact versions; the shell, app and root versions equal
  `GameServerFrameworkVersion`; no `dist/` or `node_modules/` file is tracked.

**Acceptance Scenarios**:

1. **Given** the repository, **When** `gm-console/` is read, **Then** it is an npm workspace (`packages/shell` =
   `@gameserver/gm-shell`, `app` = `@gameserver/gm-console-default`) with `package-lock.json`, `.npmrc`
   (`engine-strict`, `save-exact`) and `.nvmrc`.
2. **Given** the shell package, **When** another workspace imports it, **Then** it imports TypeScript source through the
   package `exports` (no build step) and finds its runtime libraries as exact `peerDependencies`; the shell never imports
   from `app/`.
3. **Given** the committed GM document, **When** `npm run check:api` runs, **Then** it regenerates
   `packages/shell/src/generated/gm-api.d.ts` with `openapi-typescript` and fails when the file differs from the committed
   one (`git diff --exit-code`).
4. **Given** the run-local host (Local flavor) and `npm run dev`, **When** the console signs in, **Then** the calls are
   cross-origin (`Origin: http://localhost:5173`, `Sec-Fetch-Site: same-site`) and succeed; the Local flavor of the sample
   host configures that origin and `Gm:Console:BaseUrl = http://localhost:5173/`.

---

### User Story 6 - Staff sign in, see which game and environment they are on, and sign out (Priority: P6) — §6.4, §7.5, §10, §12, Q19 (Phase B)

The console reads `config.json`, asks `/gm/v1/auth/methods` and shows the local form (Development/Test) and/or the SSO
button. After sign-in it reads `/me`; if `/me` names another game or environment than `config.json` it blocks with a
"wrong server" screen; if the framework minor version differs from the one it was built for it shows a warning banner.
The header always shows the environment (red for `live`) and the staff member; sign-out ends the server session.
Expired sessions return to the login page; nothing about the session is stored in the browser.

**Why this priority**: the shell's security contract (design §12) lives here; the pages of US7 rely on it.

**Independent Test**:
- Component tests with a fake `fetch`: login variants (`{local:true,sso:false}`, `{false,true}`, `{true,true}`,
  `{false,false}`), local sign-in calls CSRF → login → CSRF again → `/me` in this order with `X-CSRF-Token` only on the
  POST; `/me` with another `environmentId` shows the block screen and makes no further call; framework `0.8.1` against a
  console built for `0.7.0` shows the warning; a 401 on any call returns to `#/login` keeping the hash.
- End-to-end smoke (US5 loop): sign-in with `admin` / `dev-only-change-me`, header shows `local` and `sample`, sign-out
  then `/me` is 401.

**Acceptance Scenarios**:

1. **Given** a missing or invalid `config.json`, **When** the console starts, **Then** it shows which field is wrong and
   makes no API call.
2. **Given** `/gm/v1/auth/methods`, **When** the login page renders, **Then** the local form shows exactly when `local`
   is true, the SSO button exactly when `sso` is true, and a message when both are false.
3. **Given** the SSO button, **When** it is pressed, **Then** the browser navigates top-level to
   `{apiBaseUrl}/gm/v1/auth/sso/start?returnUrl=<encodeURIComponent("/" + saved hash)>`; **Given**
   `#/login?error=sso_denied`, **Then** the login page shows the denial.
4. **Given** a local sign-in, **When** it succeeds, **Then** the console fetches a new CSRF token (tokens are bound to the
   session) and `/me`; when it fails with 401 it says the credentials are invalid without saying which part.
5. **Given** `/me`, **When** its `gameId` or `environmentId` differs from `config.json`, **Then** the console shows the
   block screen naming both values, loads no catalog and no page, and offers only sign-out (Q19).
6. **Given** `/me`, **When** the `major.minor` of `frameworkVersion` differs from the build's, **Then** a warning banner
   shows both; a different `gameVersion` (when the build carries one) is an informational footer line; neither blocks.
7. **Given** any API answer 401, **When** the console receives it, **Then** it clears the session state and goes to
   `#/login`, returning to the previous hash after the next sign-in; there is no automatic sign-in loop.
8. **Given** a 403 that is not `CSRF_FAILED`, **When** it is received, **Then** the console shows it and refreshes `/me`
   once (permissions may have changed, SEC-10); **Given** `403 CSRF_FAILED` on a mutation, **Then** it fetches a new token
   and retries that mutation once.
9. **Given** sign-out, **When** it is pressed, **Then** `POST /gm/v1/auth/logout` (with the token) runs, the state is
   cleared and the login page shows, also when the session had already expired.
10. **Given** the shell, **When** its source is checked, **Then** it uses no `localStorage`, `sessionStorage`, IndexedDB,
    `dangerouslySetInnerHTML`, `eval` or `new Function` (lint), and puts no mapping key or display name in the URL.

---

### User Story 7 - Staff browse the framework's read pages from a navigation built from their permissions and the catalog (Priority: P7) — §2.2, §7.3, GM-01, GM-02, GM-06 (Phase B)

After the guard the console loads the catalog (`/gm/v1/console/openapi.json`) once. The navigation lists the built-in
read pages whose permission the staff member holds and whose routes the host serves: player lookup and search, player
detail with receipts, audit and ledger, receipt, staff list/detail/audit, and ops health/version/gate. Paged lists load
more with the cursor. Errors show the code, the field errors and the request id. No page offers any mutation except sign-in
and sign-out; mutations come with the generic forms of GM-3.

**Why this priority**: it is the first useful console (GM-01 P0.5 read side) and proves the catalog × permission model the
generic forms reuse.

**Independent Test**:
- Component tests: a staff with `player.read` only sees Players, not Staff or Ops; a catalog without `economy.ledger`
  hides the Ledger tab; direct navigation to `#/staff` without `staff.read` shows "not available" and calls nothing;
  a receipts list loads a second page with `cursor`; a 400 with `errors: { pageSize: [...] }` lists the field.
- End-to-end smoke: Staff list → `admin` → detail; Ops → version.

**Acceptance Scenarios**:

1. **Given** the catalog and `/me`, **When** the navigation renders, **Then** a page or tab is listed exactly when the
   staff member holds its permission and every route it needs is in the catalog.
2. **Given** the Players page, **When** a player id, a mapping key (`pgs:`, `gamecenter:`, `guest:`) or a display name is
   searched, **Then** the matching route is called (`gm.player`, `gm.player_by_mapping`, paged `gm.player_search`) and a
   hit opens `#/players/{playerId}`; the mapping key and the display name never appear in the URL.
3. **Given** a player, **When** its page opens, **Then** it shows every aggregate (id, exists, client-visible, rev,
   schema versions, size, updated at), the deletion state and each state as a JSON text view, with tabs Receipts, Audit
   and (when served) Ledger; a 404 shows "player not found".
4. **Given** a paged list, **When** "Load more" is pressed, **Then** the next page is requested with `pageSize=50` and the
   last `nextCursor`, appended, and the button disappears when `nextCursor` is null; a failed page keeps the loaded items.
5. **Given** the Staff and Ops pages, **When** they open, **Then** they show the staff list/detail/audit and the health,
   version and gate reads respectively.
6. **Given** any page, **When** server data is shown, **Then** it is rendered as text (never as HTML).
7. **Given** GM-2, **When** any page renders, **Then** it offers no control for staff create/grant/disable/enable/revoke,
   gate maintenance or minimum client version, key rotation, resource adjust, ban/unban, deletion cancel or game commands.

---

### User Story 8 - Ops deploy the default app on a static host with strict security headers, built once for every environment (Priority: P8) — §6.6, §7.2, Q18, SEC-11 (Phase B)

The framework ships `gm-console/deploy/`: one machine-readable header set (CSP, HSTS, `nosniff`, `Referrer-Policy`, COOP,
`Permissions-Policy`, cache rules) with an nginx example and a CDN table generated from or checked against it, plus
`config.example.json` and its JSON Schema. The built app runs under that CSP with zero violations, contains no inline
script, no third-party origin and no environment value; `config.json` is the only per-environment file.

**Why this priority**: the console is only as safe as its host headers (design §12.5); a game's CI (Q18) needs one
documented recipe.

**Independent Test**:
- Unit test: `nginx.conf.example` and `headers.md` list exactly the headers of `security-headers.json`.
- `npm run check:dist` after a build: no inline `<script>`, no absolute `http(s)://` URL of
  a third-party origin, no `localhost:5080` or other environment value.
- End-to-end smoke on the built app served with the reference headers (Vite preview on port 5173): sign-in and one page
  work; zero `securitypolicyviolation` events.

**Acceptance Scenarios**:

1. **Given** `deploy/security-headers.json`, **When** the nginx example or the CDN table is read, **Then** they carry the
   same headers and values (test-enforced), with `connect-src 'self' <apiOrigin>` filled per environment.
2. **Given** the built app under the reference headers, **When** it signs in and opens a page against the run-local host,
   **Then** no CSP violation is reported.
3. **Given** a game without pages of its own (Q18), **When** its CI follows the guide, **Then** it runs `npm ci` and the
   app build in `framework/gm-console`, adds the environment's `config.json` and headers, and uploads `dist/`; Node is
   needed only in that job.

---

### User Story 9 - The docs and the requirements describe the console hosting as built (Priority: P9) — PCR-01, design §11 (Phase B)

The PCRs (gm, platform), a new GM console guide (run locally, configure per environment, deploy a static host, version
skew, troubleshooting), the consumer guide section for games (Q18), the HTML pages that describe GM, the README and the
release notes draft describe the slice; the owner's requirement rows TBD-05a, SEC-04, SEC-06 and TBD-21 read "decided
2026-11-05" with the case-A wording.

**Why this priority**: every behaviour of Phase A is a configuration contract for ops; stale lines (`Path=/gm`,
`SameSite=None` with CORS, `/gm/v1/ui` as the P0 page) would mislead.

**Independent Test**: a reviewer agent with only the guide runs the console locally against run-local and simulates a
static-host deployment (built app + reference headers + `config.json`) without help.

**Acceptance Scenarios**:

1. **Given** the PCR gm, **When** it is read, **Then** the cookie names and attributes per environment, `Gm:Cookies`,
   `Gm:Console`, the CORS rules, the SSO return rule and the Dev/Test-only `/gm/v1/ui` match the code and tests.
2. **Given** the release notes draft, **When** it is read, **Then** it lists every breaking change with who is affected and
   the fix, including the one-time sign-out.
3. **Given** the owner's requirements file, **When** rows TBD-05a, SEC-04, SEC-06 and TBD-21 are read, **Then** they state
   the case-A decision with status `Chốt 2026-11-05`.

---

### Edge Cases

**Hosting and cookies**

- **`localhost` vs `127.0.0.1`**: `http://127.0.0.1:5173` → `http://localhost:5080` is cross-site (different hosts, no
  registrable-domain relation); `Lax` cookies are not sent. The dev loop, the e2e harness and the docs use `localhost`
  everywhere; the guide lists it under troubleshooting.
- **run-local `-Lan`** (LAN IP): a console on `http://localhost:5173` against `http://192.168.x.y:5080` is cross-site and
  unsupported; Development cookies are not `Secure` on plain HTTP so a console served from the same LAN host works.
- **Pre-018 cookies** (`gs_gm`, `gs_gm_csrf`, `Path=/gm`) stay in browsers until they expire (session cookie ≤
  `Gm:SessionHours`); new names never collide with them (Development names differ too, FR-013).
- **Development with `Gm:Cookies:SameSite = None`** needs HTTPS (both cookies become `Secure`; ASP.NET antiforgery refuses
  a `Secure`-only cookie policy on plain HTTP, research R1). Documented, not checked at startup (the scheme is per request).
- **TLS terminated by a proxy** outside Development: the CSRF route needs `IsHttps` (antiforgery policy `Always`, as since
  007), which requires the forwarded-headers configuration (`GameServer:ForwardedHeaders`, OPS-02); the guide repeats it.
- **A console under a sub-path** (`https://cdn.company.com/gm/`): `BaseUrl` keeps its path; return paths are appended to
  it, never resolved against the origin root.

**CORS**

- **Preflight of a path that does not exist**: answered by the CORS middleware (204) like any `/gm/**` path; the actual
  request then gets 404 with CORS headers.
- **`Vary: Origin`**: ASP.NET Core sends it only for multi-origin policies; GM answers are `no-store` and the catalog is
  `private`, so a single-origin policy needs none.
- **Catalog revalidation**: the console never sets `If-None-Match` itself (not a CORS-safelisted header; it would preflight
  every GET); the browser cache revalidates with the `ETag` (design §10 step 3 corrected, research R6).

**Console**

- **Session expired while a page loads**: the first 401 wins; concurrent 401s do not stack navigations.
- **`/me` succeeds but the catalog fails** (network, 5xx): an error screen with retry; the navigation is not guessed.
- **Catalog without a route a page needs** (a game without the economy module): the page/tab is hidden; a bookmarked hash
  shows "not available".
- **Network failure without a readable status** (API down, CORS refused): "no answer from the GM API at <origin>"; never
  shown as "signed out".
- **Two browser tabs**: each keeps its own CSRF token in memory; tokens are per session (both valid); sign-out in one tab
  makes the other's next call 401 → login.
- **Big integers** (`int64` > 2^53 in aggregate states): shown through `JSON.stringify` of what `JSON.parse` produced;
  precision loss above 2^53 is accepted for read views (design V6, GM-3 handles editors).

## Requirements *(mandatory)*

### Functional Requirements

**CORS (US1; H14, H16; SEC-06, ACC-11)** — Phase A

- **FR-001** (H16): When `Gm:Cors:AllowedOrigins` is set, the GM CORS policy MUST add
  `Access-Control-Expose-Headers: X-Request-Id, Retry-After, ETag` to actual responses and answer preflights with
  `Access-Control-Max-Age: 7200`. Allowed methods (`GET, POST, PUT, DELETE`), allowed request headers (`Content-Type,
  X-CSRF-Token, X-Request-Id`), exact-origin matching and credentials stay as they are.
- **FR-002** (H14, V2): Every `/gm/**` response to a request whose `Origin` is allowed MUST carry
  `Access-Control-Allow-Origin` (that origin), `Access-Control-Allow-Credentials: true` and the exposed-header list, for
  every status: 2xx, 304, endpoint errors (400, 404, 409), profile enforcement (401, 403, 405, 415), the rate limiter
  (429), the request timeout (503) and the exception middleware (500). A request from any other origin MUST get none of
  them. **[DEFAULT]** This is proven by a permanent pipeline test written first against the current code; production code
  (`ErrorResponseWriter` or the pipeline) changes only for a status that test shows without the headers, and each such
  change has that red test.
- **FR-003** **[DEFAULT]**: Outside Development, `Gm:Cors:AllowedOrigins` MUST hold at most one origin (one console per
  environment, design §6.3); more fails startup with one line naming the count. Development accepts several (a developer
  may run two consoles). The existing origin rules stay.
- **FR-004** (SEC-05, SEC-06): CORS MUST stay outside authentication and CSRF: a cookie mutation whose `Origin` is not
  allowed and not the API's own origin is refused 403 `CSRF_FAILED` also when `Sec-Fetch-Site` is `same-site` (a sibling
  host of the same site); allowed origins still need the token. No behaviour change; a permanent test pins it.

**Cookies (US2; Q20, H15; SEC-04, SEC-05, V5)** — Phase A

- **FR-010** (H15): A new setting `Gm:Cookies:SameSite` (`Lax` default, or `None`; case-insensitive) MUST set the
  `SameSite` attribute of the session and CSRF cookies, independently of whether CORS origins are configured. Any other
  value fails startup.
- **FR-011**: `Gm:Cookies:SameSite = None` MUST be refused at startup when `Gm:Cors:AllowedOrigins` is empty, and MUST
  make both cookies `Secure` in every environment (so `None` without `Secure` cannot be produced). Outside Development and
  Test, `None` MUST fail startup with one line: case B is not supported (owner decision; review T024 m2, D3).
- **FR-012** (Q20): Outside Development the cookie names MUST be `__Host-gs_gm` (session) and `__Host-gs_gm_csrf`
  (antiforgery), both `Secure`, `HttpOnly`, `Path=/`, without `Domain`; the antiforgery cookie policy stays `Always`.
- **FR-013** **[DEFAULT]** (V5, research R1): In Development the names MUST be `gs_gm_dev` and `gs_gm_dev_csrf`, both
  `HttpOnly`, `Path=/`, without `Domain`; the session cookie is `Secure` when the request is HTTPS or `SameSite` is `None`;
  the antiforgery cookie policy is `SameAsRequest` (or `Always` when `SameSite` is `None`). Reason: ASP.NET Core
  antiforgery throws on plain HTTP when its policy is `Always`, a `__Host-` cookie must be `Secure`, and plain HTTP must keep
  working for run-local (also `-Lan`) and the in-process test clients. The names differ from the pre-018 `gs_gm`/`gs_gm_csrf`
  so that a leftover `Path=/gm` cookie can never be sent ahead of the new one.
- **FR-014**: Every write and every deletion of a GM cookie (local sign-in, SSO callback, logout, CSRF route) MUST use the
  same name, `Path`, `Secure` and `SameSite` for the deployment, so a `__Host-` deletion is accepted by browsers.
- **FR-015** (Q20): Pre-018 cookies MUST be ignored (no fallback read, no migration); the server-side tickets they point to
  expire as usual. Every staff member signs in again once (release note).
- **FR-016**: The OIDC correlation and nonce cookies (`Path=/gm/v1/auth/sso/callback`, `Lax`) MUST stay unchanged.
- **FR-017**: The cookie names of a deployment MUST come from one public place in the GM module (`GmCookies`), used by the
  authentication handler, the session service, the endpoints, the tests and the docs. The public constants
  `GmCookieAuthenticationHandler.CookieName` and `GmCookies.CsrfCookieName` are replaced (breaking, BC-2).

**SSO return to the console (US3; H6, Q16; SEC-03, design §6.5)** — Phase A

- **FR-020**: A new setting `Gm:Console:BaseUrl` MUST be an absolute `http`/`https` URL whose path ends with `/`, without
  query, fragment or user info, `https` outside Development, and whose origin (`scheme://host[:port]`) is listed in
  `Gm:Cors:AllowedOrigins`. Each fault fails startup with one line.
- **FR-021** **[DEFAULT]**: `Gm:Console:BaseUrl` MUST be set when `Gm:Cors:AllowedOrigins` is set (a console exists), and
  then its origin MUST be the configured console origin (FR-020); otherwise startup fails. Without either setting the
  deployment has no console: startup does not fail in any environment, whether SSO is configured or not, and the API works
  as before (Q16: no console domain exists yet; a game bumps without new settings).
- **FR-022** (open redirect): With `Gm:Console:BaseUrl`, the `returnUrl` of `GET /gm/v1/auth/sso/start` MUST be accepted
  only when it has 1..512 characters, all printable ASCII (0x21–0x7E), contains none of `//`, `\`, `..` or `%2e` (any
  case: browsers resolve `%2e%2e`, `.%2e`, `%2e.` as `..`), and is `/` or `/#<fragment>` — the hash route the console
  sends; no other path segment and no query (review T024 M1). The redirect target is `BaseUrl` + the value without its
  leading `/`. A missing or refused value gives `BaseUrl`. An absolute URL from input is never used.
- **FR-023**: A successful callback MUST set the session cookie (FR-012/FR-013) and answer 302 to the target of FR-022.
- **FR-024**: Every denial (no issuer/subject, no MFA, identity not a staff member of the deployment, disabled staff, remote
  failure such as a forged state, callback not handled) MUST answer 302 to `BaseUrl#/login?error=sso_denied`; no reason in
  the URL; the security log line stays as today.
- **FR-025**: Without `Gm:Console:BaseUrl` (no console) the legacy rule MUST apply: return paths under `/gm/` with the
  FR-022 character rules; default `/gm/v1/ui` where development tools are on and `/gm/v1/me` elsewhere (FR-030: no page
  there; `/me` proves the sign-in); denial = that default + `?error=sso_denied` (the old `/gm/` targets answered 404).
- **FR-026**: No Public route MUST disclose `Gm:Console:BaseUrl` or the CORS origins (they appear only in redirects and in
  the CORS answer to the allowed origin).

**Static GM page (US4; H9, Q17)** — Phase A

- **FR-030**: The route `gm.ui` MUST be declared with the condition `development_tools` and mapped only where development
  tools are allowed; elsewhere `/gm/v1/ui` answers 404. Its Development/Test behaviour (content, CSP, `no-store`) is
  unchanged.

**Workspace, toolchain and codegen (US5; §7.1, §8, Q18; CI-01)** — Phase B

- **FR-040**: `gm-console/` (repository root; `framework/gm-console/` seen from a game's submodule) MUST be an npm workspace
  root: `package.json` (private, workspaces `packages/*` and `app`, scripts), `package-lock.json` (committed, lockfile
  version 3), `.npmrc` (`engine-strict=true`, `save-exact=true`), `.nvmrc` (`22.20.0`), `engines.node` `>=22.20.0`.
- **FR-041**: `packages/shell` (`@gameserver/gm-shell`, private) MUST export TypeScript source through `exports` (no build
  step, no `dist/`), declare its runtime libraries (React, React DOM, Mantine core/hooks, TanStack Query, React Router,
  openapi-fetch) as exact `peerDependencies`, and import nothing from `app/` or from relative paths outside the package
  (GM-4 consumes it from a game workspace through the submodule path).
- **FR-042**: `app` (`@gameserver/gm-console-default`) MUST be the default console: a Vite app whose entry calls
  `createGmConsole()` and nothing game-specific; its build output `dist/` MUST never be tracked (gitignore + architecture
  test, design D12).
- **FR-043**: Every dependency version in every `gm-console` `package.json` MUST be exact (no `^`, `~`, ranges, tags or
  URLs); versions are chosen by the rule of research R7 (newest stable release published at least 14 days before the
  install that satisfies the Node engine) and recorded.
- **FR-044** (§8): `openapi-typescript` MUST generate `packages/shell/src/generated/gm-api.d.ts` from
  `docs/api/gm-openapi.json` (committed, LF, no timestamp); the API client is `openapi-fetch` typed with it. Scripts:
  `gen:api` (generate), `check:api` (generate, then `git diff --exit-code` on the generated file), `typecheck`
  (`tsc --noEmit` per workspace), `lint`, `test`, `build`, `check:dist` (FR-083), and `verify` running all of them in order.
- **FR-045** (§6.7, Q16): `npm run dev` MUST serve the default app on `http://localhost:5173` (strict port) calling the API
  origin of `app/public/config.json` (`http://localhost:5080`, gameId `sample`, environmentId `local`) with no proxy. The
  sample host's Local and Dev flavors MUST set `Gm:Cors:AllowedOrigins = ["http://localhost:5173"]` and
  `Gm:Console:BaseUrl = "http://localhost:5173/"`; its Beta, Uat and Live flavors set neither (no console domain yet,
  Q16), like a game that bumps without new settings.
- **FR-046** (design §7.1): `dotnet build` and `dotnet test` of the solution MUST NOT need Node; no project invokes npm.
- **FR-047**: The `version` of the workspace root, the shell and the app MUST equal `GameServerFrameworkVersion`
  (architecture test next to the 013 `VersionTests`); the shell's version is the "built for" framework version (FR-056).
- **FR-048** (§12.2, §12.6, SEC-12): Lint MUST fail on `dangerouslySetInnerHTML`, `eval`, `new Function`,
  `localStorage`, `sessionStorage` and `indexedDB` in `gm-console` source (tests included except where a test asserts the
  rule).

**Session shell (US6; §6.4, §7.5, §10, §12; Q19)** — Phase B

- **FR-050**: The console MUST load `GET /config.json` from its own origin with `cache: 'no-store'` and validate it:
  `apiBaseUrl` absolute `http(s)` URL without query, fragment or user info; `gameId`, `environmentId` non-empty strings of
  at most 64 characters; unknown members ignored. A missing or invalid file shows an error screen naming the field and
  makes no API call. `deploy/config.schema.json` describes the same rules.
- **FR-051**: Every API call MUST use `credentials: 'include'` and the configured `apiBaseUrl`. `GET` calls MUST NOT add
  any request header (no preflight, design §6.3); non-GET calls add `X-CSRF-Token` and, with a body,
  `Content-Type: application/json`; `X-Request-Id` is added only when the caller passes one (policy belongs to GM-3).
- **FR-052** (§6.4; amended by T034/T069): The CSRF token MUST be fetched with `GET /gm/v1/auth/csrf` before the first
  mutation and kept in memory only; a sign-in or sign-out discards the held token (the next mutation fetches one; sign-out
  also fetches an anonymous one); on `403 CSRF_FAILED` a mutation fetches a new token and is retried once; a second failure
  is shown.
- **FR-053**: The login page MUST follow `GET /gm/v1/auth/methods` (US6 scenario 2), start SSO by a top-level navigation
  with the saved hash as `returnUrl` (scenario 3), and show the `sso_denied` message.
- **FR-054** (amended by T034/T069): A local sign-in MUST run CSRF → `POST /gm/v1/auth/login` (its answer is the staff
  member; no extra `/me` call); the anonymous token is discarded and the session-bound token is fetched before the next
  mutation; a 401 says "invalid staff id or password".
- **FR-055** (Q19): After `/me`, a `gameId` or `environmentId` different from `config.json` MUST show the block screen,
  load nothing else and offer only sign-out.
- **FR-056** (Q19, §7.5): The build MUST embed `builtFor = { frameworkVersion, gameVersion? }` (shell package version; game
  version from the build variable `GM_CONSOLE_GAME_VERSION` when set). A different `major.minor` of `/me.frameworkVersion`
  (compared on the part before `+` or `-`) MUST show a warning banner with both values; a different `gameVersion` an
  informational footer line; unparsable versions are shown as text with the warning. Nothing blocks on versions.
- **FR-057** (SEC-10): A 401 on any call MUST clear the in-memory session (staff, catalog, token) and go to `#/login`,
  returning to the previous hash after the next sign-in; at most one `/me` check per navigation (no loop).
- **FR-058**: A 403 other than `CSRF_FAILED` MUST be shown and trigger one `/me` refresh; the navigation is recomputed from
  the new permissions.
- **FR-059**: Sign-out MUST post `/gm/v1/auth/logout` with the token, then clear the state and show the login page, also
  when the call answers 401.
- **FR-060**: The header MUST always show the environment id and game id from `/me` (red for `live`, design Q7) and the
  staff display name, with sign-out.
- **FR-061** (§12.2, §12.7, GM-02): The console MUST NOT store session data, staff data or player data in browser storage
  and MUST NOT put a mapping key or display name into the URL.
- **FR-062** (API-04): Errors MUST be shown from Problem Details: title, `code`/`codeNumber`, `detail`, every entry of
  `errors` by field, and the request id (body `requestId`, else the exposed `X-Request-Id` header). A failure without a
  readable answer shows "no answer from the GM API at <origin>". Server strings are rendered as text only.

**Navigation and read pages (US7; §2.2, §7.3; GM-01, GM-02, GM-06)** — Phase B

- **FR-065** (D1, D2): After the guard the console MUST load `/gm/v1/console/openapi.json` once per session (credentials,
  no manual `If-None-Match`; the browser cache revalidates with the `ETag`) and parse its operations by `operationId`
  (method, path, `x-gm-permission`, `x-gm-kind`, `x-gm-target`, `x-gm-paged`). A failure shows an error screen with retry.
- **FR-066**: The navigation MUST list a built-in page or player tab exactly when the staff member holds its
  `requiredPermission` and every route of its `requiredRoutes` is in the catalog; a hidden page reached by URL shows "not
  available" and calls nothing.
- **FR-067** **[DEFAULT]** (GM-4 seam): Built-in pages and player tabs MUST be registered through an internal registry with
  the page and tab shapes of design §7.3 (`id`, `title`, `group`, `path`, `requiredPermission`, `requiredRoutes`,
  `component`, `order`); the registry is not exported in this slice and `createGmConsole()` takes no extension yet (GM-4
  exports it, adds `overrideOperation` and extension options additively).
- **FR-068**: Players page: lookup by player id (`gm.player`), by mapping key (`gm.player_by_mapping`) and search by
  display name (`gm.player_search`, paged); a hit opens `#/players/{playerId}`; FR-061 applies.
- **FR-069**: Player page (`gm.player`): aggregates, deletion state, state JSON text views; tabs Receipts
  (`gm.player_receipts`), Audit (`gm.player_audit`), Ledger (`economy.ledger`); 404 → "player not found".
- **FR-070**: Receipt page `#/receipts/{receiptId}` (`gm.receipt`): JSON text view; reached from the receipts tab.
- **FR-071**: Staff pages: list (`gm.staff_list`, paged), detail (`gm.staff`), audit tab (`gm.staff_audit`, paged).
- **FR-072**: Ops page: cards for health (`gm.health`), version (`gm.ops_version`) and gate (`gm.ops_gate`), each under
  FR-066.
- **FR-073** (GM-06): Paged lists MUST request `pageSize=50`, follow `nextCursor` with "Load more", stop when it is null,
  and keep loaded items when a later page fails.
- **FR-074** **[DEFAULT]** (GM-2/GM-3 boundary): The only mutations the console performs in this slice are the local
  sign-in, the sign-out and the CSRF token fetch. No page renders a control for any other GM mutation; they come with the
  generic forms of GM-3 (confirmation, typed confirmation, reason, request id, retry rules, design §9.3–§9.4).
- **FR-075** (D5): Pages MUST call the API through the typed client by path (`api.GET('/gm/v1/players/{playerId}', …)`), so
  a changed template or response shape breaks `typecheck` after `gen:api`.
- **FR-076**: JSON views MUST render `JSON.stringify(value, null, 2)` as text.

**Static host reference (US8; §6.6, §7.2, §12.5; SEC-11)** — Phase B

- **FR-080**: `gm-console/deploy/` MUST contain `security-headers.json` (the single source: headers per path class
  `document` = `index.html`/`config.json`, `asset` = hashed files; CSP with the placeholder `{{apiOrigin}}`),
  `nginx.conf.example`, `headers.md` (CDN table), `config.example.json`, `config.schema.json`.
- **FR-081**: The reference CSP MUST be `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'
  {{apiOrigin}}; img-src 'self' data:; font-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none';
  form-action 'none'`, with the `style-src` fallback `'self' 'unsafe-inline'` only if V3 shows Mantine cannot run without
  it (scripts stay `'self'`). Other headers: `Strict-Transport-Security: max-age=31536000`, `X-Content-Type-Options:
  nosniff`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`, `Permissions-Policy: camera=(),
  microphone=(), geolocation=()`; `Cache-Control: no-cache` for documents, `public, max-age=31536000, immutable` for
  assets.
- **FR-082**: A unit test MUST prove that `nginx.conf.example` and `headers.md` carry exactly the headers of
  `security-headers.json`; the end-to-end smoke MUST run the built app under those headers with zero CSP violations.
- **FR-083** (SEC-11): `check:dist` MUST fail when the built `dist/index.html` has an inline `<script>` or a `src`/`href`
  that is not a relative or root path, or when any file of `dist/` contains `localhost:5080` or is a `config.json` (the API
  origin comes only from the deployed `config.json`). Loading from third-party origins is excluded by the CSP and proven by
  the end-to-end smoke (FR-082).
- **FR-084**: The build MUST be environment-free: the same `dist/` serves every environment with its own `config.json` and
  CSP `connect-src`.
- **FR-085** (Q18): The consumer path for a game without pages MUST be documented: in the game's CI, `npm ci` in
  `framework/gm-console`, `npm run build -w app`, add `config.json`, apply the headers, upload `dist/`.

**End-to-end smoke (US5–US8)** — Phase B

- **FR-087**: An end-to-end smoke MUST drive the system Chrome (default
  `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe`, override `GM_E2E_BROWSER`, Edge
  `C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe` documented) through `playwright-core`, with no browser
  download, against a running run-local host, in two targets: the Vite dev server and the built app served with the
  reference headers, both on `http://localhost:5173`. It covers cross-origin local sign-in (CSRF token on the POST,
  `Origin`/`Sec-Fetch-Site` seen by the host as `http://localhost:5173`/`same-site`), `/me` in the header, the Staff list →
  detail and Ops version pages, a readable `X-Request-Id` on an error answer, sign-out then 401, the Q19 block screen with a
  mismatching `config.json`, and zero CSP violations on the built target.

**Docs and requirements (US9; PCR-01, design §11)** — Phase B

- **FR-090**: `docs/pcr/gm.md` MUST describe cookies per environment, `Gm:Cookies:SameSite`, `Gm:Console:BaseUrl`, the CORS
  rules (expose, max-age, one origin, headers on errors), the SSO return and denial targets, `/gm/v1/ui` Dev/Test only,
  and drop "`/gm/v1/ui` is the P0 page" and "`SameSite=None` when CORS is on".
- **FR-091**: `docs/pcr/platform.md` §14 (CORS) and a new section 24 (GM console hosting and shell: workspace, codegen,
  versions, static host headers, e2e) MUST match the code.
- **FR-092**: A new guide `docs/guides/gm-console.md` (Vietnamese, like the other guides) MUST cover the local loop,
  per-environment configuration (server and `config.json`), static-host deployment with the headers, TLS/forwarded
  headers, version skew, the one-time sign-out, and troubleshooting (`localhost` vs `127.0.0.1`, LAN, cookie names).
- **FR-093**: `docs/guides/consumer-guide.md` MUST gain section 15 "GM console for a game" (Q18 CI recipe, settings per
  environment, deploy order server then console) and bump-checklist lines (section 11).
- **FR-094**: `docs/html/01-gioi-thieu.html` (4.12 GM API, module table), `docs/html/03-deploy.html` (section 11),
  `README.md` (GM rows) and `docs/guides/environments-and-local-run.md` MUST describe the console and the Dev/Test-only
  page.
- **FR-095**: `specs/018-gm-console-hosting-shell/release-notes-draft.md` MUST list every change, BC-1..BC-9, migration
  steps for zh-pvp and the decisions awaiting owner override.
- **FR-096** (design §11): The owner's `Requirements/Game_Server_System_Requirements_v1.2.md` (gitignored, never committed)
  MUST have rows TBD-05a, SEC-04, SEC-06 and TBD-21 updated to the case-A decision with status `Chốt 2026-11-05`, plus the
  lines that restate them (assumption G6, the status line and the TBD summary sentence), and the SEC-13 row and its
  appendix line ("Rate limit GM API") updated to Q9: per-IP hard limit ≤ 30/min only on the GM auth endpoints
  (`/gm/v1/auth/*`), not on the whole GM API (`Chốt 2026-11-05`).

**Verification and release (all stories)**

- **FR-098**: VERIFY items V2, V3, V4, V5, V8, V9, V11, V12, V13 (research) MUST be answered with a recorded note before the
  tasks that depend on them; V7 (Firefox) is recorded as not run.
- **FR-099**: The slice MUST end with a Release build at 0 warnings, the full .NET suite in memory, with the File store and
  with Couchbase, `npm ci` + `npm run verify` in `gm-console/`, and the end-to-end smoke on both targets; no version bump or
  tag (proposed next tag `v0.8.0`, together with 017).

### Key Entities

- **Console origin**: the one `scheme://host[:port]` of an environment's console; in `Gm:Cors:AllowedOrigins` and the
  origin of `Gm:Console:BaseUrl`.
- **GM cookies**: session (`__Host-gs_gm` / `gs_gm_dev`) and antiforgery (`__Host-gs_gm_csrf` / `gs_gm_dev_csrf`).
- **Console configuration (`config.json`)**: `{ apiBaseUrl, gameId, environmentId }`, per environment, on the console's
  static host.
- **Built-for stamp**: `{ frameworkVersion, gameVersion? }` embedded at build time.
- **Catalog operation**: the parsed view of one GM document operation (`routeName`, `method`, `template`, `permission`,
  `kind`, `target`, `paged`).
- **Page definition**: a registry entry (design §7.3 shape) for a built-in page or player tab.
- **Security header set**: `deploy/security-headers.json`, the source of the static-host headers.

## Backward compatibility and breaking changes (to consumers of `main` = v0.7.0 + 017)

No route, permission, request or response body changes; no persisted document, proto, Couchbase or Unity change; the GM
document `docs/api/gm-openapi.json` is unchanged (`gm.ui` was already excluded from description). The changes are in
cookies, configuration, redirects and response headers.

| # | Change | Who is affected | How it is surfaced | Fix |
| --- | --- | --- | --- | --- |
| BC-1 | Cookie names and path: `__Host-gs_gm` / `__Host-gs_gm_csrf` (`Path=/`, `Secure`) outside Development, `gs_gm_dev` / `gs_gm_dev_csrf` (`Path=/`) in Development (FR-012, FR-013) | Every signed-in staff member; tools or scripts that read or send `gs_gm` | 401 until the next sign-in (once per browser) | Sign in again; scripts use the new names (PCR gm) |
| BC-2 | Public constants `GmCookieAuthenticationHandler.CookieName` and `GmCookies.CsrfCookieName` replaced by `GmCookies` name members (FR-017) | Game code or tests that referenced them | Compile error | Use `GmCookies.SessionName(options)` / `CsrfName(options)` or the constants of the environment |
| BC-3 | CORS origins no longer imply `SameSite=None`; `Gm:Cookies:SameSite` decides, default `Lax` (FR-010) | A deployment that relies on a cross-site console (case B, unsupported) | Session cookie not sent cross-site; `Gm:Cookies:SameSite = None` fails startup outside Development/Test (review T024 m2) | Move to case A |
| BC-4 | A host that sets `Gm:Cors:AllowedOrigins` must also set `Gm:Console:BaseUrl` with the same origin (FR-020, FR-021) | Only hosts that already configured a CORS console origin (none known; zh-pvp sets none) | Startup fails with one line | Add the console URL of that origin; hosts without CORS/console settings need nothing |
| BC-5 | Outside Development, at most one CORS origin (FR-003) | A host that lists several console origins | Startup failure line | Keep the environment's one console origin |
| BC-6 | `/gm/v1/ui` only where development tools are on (FR-030) | Staff who opened it on beta/uat/live (it could not sign in there) | 404 | Use the console |
| BC-7 | SSO targets: with `BaseUrl`, `returnUrl` is a console path and success/denial redirect to the console; without it the defaults are `/gm/v1/ui` (development tools on) or `/gm/v1/me` (elsewhere), denial + `?error=sso_denied`, instead of the 404 `/gm/` (FR-022..FR-025) | Bookmarks or tools that start SSO with `returnUrl=/gm/...` on a host that sets `BaseUrl`; staff landing after SSO on a host without console | Redirect to the console, or to `/gm/v1/me` instead of a 404 | Start SSO from the console when one exists |
| BC-8 | Route catalog: `gm.ui` gains the condition `development_tools` (FR-030) | Game tests that pin the mapped routes per flavor | Pinned list differs on Staging/Production flavors | Remove `gm.ui` from non-development pins |
| BC-9 | CORS answers gain `Access-Control-Expose-Headers`; preflight cache 600 s → 7200 s (FR-001) | None known (additive) | New headers | — |

## First-consumer migration (zh-pvp-server after the next tag)

Checked read-only on 2026-11-06 (`E:\ProjGits\zh-pvp-server`): `src/ZhPvp.Host/ZhPvpModules.cs:38` adds `GmModule`;
`appsettings.Local.json` seeds a local admin; `appsettings.{Beta,Uat,Live}.json` set no `Gm` section (OIDC must come from
the environment's secret provider today). After the bump the game:

1. needs **no new setting** to bump in any environment: Beta/Uat/Live without `Gm:Cors`/`Gm:Console` start as today (no
   console; SSO lands on `/gm/v1/me`);
2. optionally adds `Gm:Cors:AllowedOrigins = ["http://localhost:5173"]` and `Gm:Console:BaseUrl = "http://localhost:5173/"`
   to its Local (and Dev) flavor to run the default console locally;
3. when a console domain exists (Q16), sets the console origin and `Gm:Console:BaseUrl` together per environment (BC-4);
4. tells staff they will sign in again once (BC-1);
5. optionally adds the console CI job of consumer guide §15 (Q18) once a static host exists.

## Dependencies and seams with other slices

- **007 gm-security**: cookie, CSRF, CORS and SSO mechanisms are kept; this slice changes names, path, `SameSite` source,
  exposed headers and redirect targets. The CSRF `Origin` and `Sec-Fetch-Site` checks are unchanged (FR-004).
- **011 local-env / 013 consumer-readiness**: run-local (port 5080, banner), `LocalFlavorFactory<T>` reads the sample
  host's real `appsettings.Local.json` (the new CORS and console values reach those tests), version single source
  (`VersionTests`).
- **017 gm-api-contract**: the console consumes `gm.auth_methods`, `/me` versions, `gm.console_catalog` (ETag) and the
  committed `docs/api/gm-openapi.json` (codegen input). Nothing in 017 changes.
- **GM-3 `gm-generic-forms`**: reuses the client middleware (CSRF retry, optional `X-Request-Id`), the catalog parser
  (extends `GmOperation` with schemas), the error display (`errors` map) and the registry; adds every mutation UI.
- **GM-4 `gm-console-extension`**: exports the registry and `createGmConsole({ extensions })`, adds `overrideOperation`,
  the game template and the "add a GM page" guide; consumes `@gameserver/gm-shell` from `framework/gm-console/packages/shell`
  (V9 checks the workspace path here).
- **ops-readiness**: TLS release gate and deployment manifests; the static host itself is the game's infrastructure.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For the allowed origin, 100% of the 11 tested statuses (200, 304, 400, 401, 403, 404, 405, 415, 429, 500,
  503) carry `Access-Control-Allow-Origin`, `Access-Control-Allow-Credentials` and the three exposed headers; for another
  origin, 0%.
- **SC-002**: 100% of GM cookie writes and deletions in the cookie matrix (Development over HTTP and HTTPS, Staging over
  HTTPS, `SameSite` `Lax`/`None`, CORS on/off) have the name and attributes of FR-012/FR-013; a pre-018 cookie gets 401.
- **SC-003**: In the SSO matrix, 0 of the hostile return values (absolute, `//`, `\`, `..`, non-ASCII, 513 characters,
  `javascript:`) is followed, and 100% of the denial paths land on the denial target.
- **SC-004**: `/gm/v1/ui` answers 200 on Local and Dev and 404 on Beta, Uat and Live.
- **SC-005**: From a clean clone on Node 22.20 / npm 10.9, `npm ci` and `npm run verify` in `gm-console/` are green; a
  one-line change of the GM document makes `check:api` fail.
- **SC-006**: The end-to-end smoke is green on both targets with the system Chrome (and once with Edge), with 0 CSP
  violations on the built target, and GET calls cause 0 preflights (only the sign-in and sign-out POSTs are preflighted).
- **SC-007**: The default app's initial JavaScript + CSS is ≤ 500 KB gzip (design §14), measured by `check:dist`.
- **SC-008**: After sign-in, the console makes exactly one catalog request per session and the time from the sign-in
  answer to the first rendered page is ≤ 1 s on the reference machine against run-local.
- **SC-009**: No regression: the full .NET suite (in memory, File store, Couchbase) is green and `docs/api/gm-openapi.json`
  is byte-identical to `main`.
- **SC-010**: A reviewer agent with only `docs/guides/gm-console.md` runs the console locally and simulates a static-host
  deployment without help (or every finding is fixed in the guide).

## Assumptions

Decisions taken by default (owner may override; each is referenced where it applies):

- **D1 — H14 is proven, not assumed** (FR-002): the planning probe contradicts the design's claim; a permanent pipeline
  test decides whether `ErrorResponseWriter` changes at all (017 lesson: no change without a test that fails without it).
- **D2 — one CORS origin outside Development** (FR-003): design §6.3 "exactly one console origin per environment";
  Development may list several.
- **D3 — `SameSite=None` only in Development/Test** (FR-011, changed by review T024 m2): refused without CORS origins,
  always `Secure`, and refused outright outside Development/Test because case B is not supported (owner decision; default
  taken by the orchestrator). Before the review: accepted outside Development.
- **D4 — Development cookie names `gs_gm_dev` / `gs_gm_dev_csrf` without prefix** (FR-013): browsers accept `__Host-`
  from `http://localhost` (probe), but ASP.NET antiforgery refuses a `Secure`-only policy on plain HTTP, and plain HTTP is
  needed for run-local, `-Lan` and the in-process tests; new names avoid the `Path=/gm` leftover.
- **D5 — `Gm:Console:BaseUrl` required only together with a CORS console origin** (FR-021, coordinator correction
  2026-11-06): no console domain exists yet (Q16), so a deployment without console settings must start; SSO there lands on
  `/gm/v1/me`. The literal alternative "required whenever `Gm:Oidc` is configured" is not used: OIDC is already mandatory
  where development tools are off (`GmOptions.cs:129-135`), so it would require `BaseUrl` on every Staging/Production host.
- **D6 — return path rules** (FR-022): design §6.5 plus `..` refused and the path appended to `BaseUrl` (a console under a
  sub-path keeps it).
- **D7 — denial target `BaseUrl#/login?error=sso_denied`** (FR-024): design §6.5 as written; ties the server to the
  console's hash route `#/login`, documented in the PCR.
- **D8 — catalog revalidation by the browser cache** (FR-065): no manual `If-None-Match` (not CORS-safelisted; every GET
  would preflight), contrary to design §10 step 3.
- **D9 — internal registry with the design §7.3 shapes, not exported** (FR-067): GM-4 exports it additively; freezing
  the extension API now without a consumer would repeat the "promote only what is proven" rule backwards.
- **D10 — GM-2 mutations = sign-in and sign-out only** (FR-074): every other mutation needs the confirmation, typed
  confirmation, reason and request-id rules of design §9.3–§9.4, which are GM-3's subject; hand-written forms now would be
  replaced. `/gm/v1/ui` keeps adjust in Development/Test meanwhile (GM-01 P0 minimum).
- **D11 — Ledger tab included** (FR-069): `economy.ledger` is a framework read and proves the "hidden when the route is
  missing" rule on a game without the economy module.
- **D12 — sample host flavor values** (FR-045): Local/Dev use `http://localhost:5173`; Beta/Uat/Live set no console
  values (as a game today), so the flavor matrix proves the "no console" start; tests supply example `https` values where a
  console is needed (T022).
- **D13 — dependency selection rule and TypeScript 5.9** (FR-043): newest stable ≥ 14 days old satisfying Node 22.20;
  TypeScript 7 and 6 are excluded by the peer ranges of `typescript-eslint` (< 6.1) and `openapi-typescript` (`^5`);
  React Router 8 and jsdom 30 require Node ≥ 22.22 and are excluded (research R7).
- **D14 — end-to-end smoke in GM-2** (FR-087): design §15 deferred browser tests to GM-3/GM-4; the orchestrator's brief
  requires a smoke now. Runner: Vitest (node environment) + `playwright-core`; the run-local host is started outside the
  test (the final task starts and stops it).
- **D15 — CSRF retry on `403 CSRF_FAILED`** (FR-052): design §6.4 says "400 antiforgery"; the server answers 403
  `CSRF_FAILED` (`CoreErrorCodes.cs:32`, `ProfileEnforcementMiddleware.cs:145`).
- **D16 — fake `fetch`, no MSW** (plan): `openapi-fetch` accepts a `fetch` function; one less dependency.
- **D17 — requirement lines beyond the four rows** (FR-096): G6 (line 95) and the status sentences (lines 7, 697) restate
  TBD-05a/TBD-21 and are edited together for consistency; SEC-13 (lines 451, 816) follows Q9 (coordinator correction
  2026-11-06).
- **D18 — no version bump or tag in the slice**: next tag proposed `v0.8.0` with 017 (FR-099).
- **D19 — run-local prints a console line** (plan): one banner line pointing to `gm-console` (`npm run dev`).
- **D20 — languages**: specs, consumer guide and release notes in English; PCRs, the new GM console guide and HTML pages
  in Vietnamese (repository rule); code, keys and routes literal.

Other assumptions:

- Chrome 157 and Edge 156 are the reference browsers (installed); Firefox is not installed, so V7 and the Firefox column of
  V4 are not run (best-effort per owner).
- The reference machine and timings are those of 013–017.
- The corporate network blocks browser downloads; the npm registry is reachable (checked 2026-11-06).

## Owner confirmations

No `[NEEDS CLARIFICATION]` remains; every open point has a default. The owner should confirm:

1. **D4**: Development cookie names `gs_gm_dev` / `gs_gm_dev_csrf` (not `__Host-`), since the local loop runs on plain HTTP.
2. **D2 / BC-5**: one CORS origin per environment outside Development.
3. **D3**: `SameSite=None` refused outside Development/Test (review T024 m2, in line with "case B not supported").
4. **D10**: no business mutation in the console until GM-3.
5. **D14**: end-to-end smoke now, with the host started outside the test.

## Plan traps (for the plan, 2026-11-06)

Numbering used by research.md.

1. `__Host-` on `http://localhost` vs ASP.NET antiforgery `Always` on HTTP → R1, V5.
2. CORS headers on error answers (`OnStarting`) → R2, V2.
3. `localhost` vs `127.0.0.1` vs LAN IP, same-site across ports → R3.
4. `SameSite`/`Secure` combinations and the antiforgery policy → R4.
5. Return-URL composition with a hash route and a console sub-path → R5.
6. `If-None-Match` is not CORS-safelisted → R6, V13.
7. Toolchain peers and Node engines (TypeScript 7, React Router 8, jsdom 30) → R7, V8.
8. Shell as source package for a game workspace (GM-4) → R8, V9.
9. Codegen determinism and line ends (`.gitattributes eol=lf`, `core.autocrlf=true` on the owner's machine) → R9, V12.
10. Mantine style injection vs `style-src 'self'` → R10, V3.
11. `LocalFlavorFactory<SampleHost Program>` reads the real Local config; pinned cookie/SSO/route tests → R15, R16.
12. Requirements text that contradicts case A → R17.

## Out of Scope

- GM-3: generic forms, confirmation and typed confirmation, reason, request-id generation and retry policy in the browser,
  every business mutation in the console, option selects, query views of game operations.
- GM-4: public extension registry, `createGmConsole({ extensions })`, `overrideOperation`, game console template, SampleGame
  example pages, "add a GM page" guide.
- Case B (cross-site console: `Partitioned` cookies, token exchange), Firefox verification, Safari/mobile, i18n, themes.
- A published console bundle or release asset per tag (Q18 later), a CI system (the repository has none; checks are npm
  scripts and .NET tests).
- An HTTPS dev certificate for run-local; `MapGmAction`; game role templates.
- The per-IP limit of SEC-13 as decided by Q9 (≤ 30/min per IP, GM auth endpoints only): not implemented today (the GM
  API has only the concurrency limiter `gs-gm`, `GameServerBuilder.cs:346-351`, nothing per IP), so nothing limits more than
  Q9 allows and no alignment task is needed; adding the auth-endpoint limiter is a later slice. The requirement rows are
  edited here (FR-096).
- Version bump, tag, push; edits of game repositories.

## Design claims re-checked on `main` (`47f7468`) — conflicts found

- **H14 not reproduced (design §1, §6.3, brief)**: `ErrorResponseWriter.cs:45` calls `context.Response.Clear()`, but the
  CORS middleware (`GameServerApplicationExtensions.cs:74`, `UseCors` in a `/gm` branch) writes its headers in
  `OnStarting`; the probe (research R2) kept them on 500/401/404. Handled by D1.
- **H6 confirmed**: `GmSso.cs:26-27` (`/gm/`, `/gm/?error=sso_denied`) and `:105-112` (`SafeReturnUrl` requires `/gm/`);
  no route serves `/gm/` (`GmRoutes.cs:192-267`). `tests/GameServer.Gm.Tests/SsoTests.cs:136-138` pin the default.
- **H15 confirmed**: `GmCookies.cs:22-28` and `:40-46`; pinned by `tests/GameServer.Gm.Tests/CorsTests.cs:32-49`
  (`samesite=none` when CORS is on).
- **H16 confirmed**: `GmCors.cs:78-83` (no exposed headers, `SetPreflightMaxAge(10 minutes)`).
- **H9 confirmed**: `GmRoutes.cs:215` (no condition), `GmModule.cs:168` (mapped unconditionally).
- **Path pins**: `GmCookies.cs:29, 43` (`/gm`); tests `GmAuthTests.cs:41-42`, `CsrfAndSessionTests.cs:157-164`.
- **Design §6.2 vs Q20/local loop**: the design keeps the old unprefixed names in Development; with `Path=/` the old name
  would coexist with a leftover `Path=/gm` cookie (D4 renames).
- **Design §6.4 step 4** ("400 antiforgery → refetch once") vs the server's 403 `CSRF_FAILED` (`CoreErrorCodes.cs:32`) —
  D15.
- **Design §6.3 vs §10 step 3**: "GET sends no custom header" vs "catalog with `If-None-Match`" (not CORS-safelisted) — D8.
- **Design §6.5 vs Q17**: "without `BaseUrl`, return to `/gm/v1/ui`" cannot work where `/gm/v1/ui` is no longer mapped —
  FR-025 lands on `/gm/v1/me` there (D5).
- **Design §11 libraries vs toolchain**: React Router 8.x needs Node ≥ 22.22.0 (installed 22.20.0); TypeScript 7 is outside
  the peer ranges of `typescript-eslint` 8.72 and `openapi-typescript` 7.13 — D13.
- **Design §15** defers browser tests to GM-3/GM-4; the brief requires a smoke now — D14.
- **Requirements (owner's file)**: line 95 (G6: "same-origin; other origin → `SameSite=None`"), line 442 (SEC-04 same-origin
  baseline, `None` when cross-origin), line 707 (TBD-05a "Same-origin; SameSite=Lax; không CORS"), line 725 (TBD-21 "Mở"),
  lines 7 and 697 ("TBD-21 còn Mở") contradict case A — updated by FR-096. Lines 451/816 (SEC-13: "GM API giữ per-IP chặn
  cứng ≤ 30/phút") predate Q9 (owner accepted the defaults Q5–Q15 on 2026-11-05: the limit applies to the GM auth endpoints
  only) — updated by FR-096; the code has no per-IP GM limit, so nothing to align. Line 427 (GM-01 P0 minimum includes "resource adjust" in the frontend): the
  GM-2 console has no adjust (D10); `/gm/v1/ui` keeps it in Development/Test.
- **Docs**: `docs/pcr/gm.md:6` ("`/gm/v1/ui` vẫn là trang P0"), `:60-62` and `:76-79` (`SameSite=None` with CORS, `Path=/gm`),
  `:94-97` (`/gm/` targets), `:103` (`/gm/v1/ui` Public everywhere); `docs/pcr/platform.md:214`; `README.md:52, 117`;
  `docs/html/01-gioi-thieu.html:543, 661`; `docs/html/03-deploy.html:732-733` — updated by FR-090..FR-094.
