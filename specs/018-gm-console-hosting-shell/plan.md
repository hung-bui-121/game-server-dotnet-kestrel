# Implementation Plan: GM console hosting and shell (slice 018 gm-console-hosting-shell, GM-2)

**Branch**: `018-gm-console-hosting-shell` | **Date**: 2026-11-06 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/018-gm-console-hosting-shell/spec.md`; design
`AgentMemories/features/gm-tool/01_design.md` §6 (hosting and security), §7, §8, §10, §12, §16 (GM-2 row), §17 (Q16–Q21).

## Summary

Second slice of the GM tool track (GM-2 of GM-1..GM-4), in two phases (Q21).

- **Phase A — backend (US1–US4).** CORS exposes `X-Request-Id`, `Retry-After`, `ETag` and caches preflights 7200 s; a
  permanent pipeline test proves CORS headers on every error answer before any change to `ErrorResponseWriter` (the
  design's H14 was not reproduced by the planning probe). Cookies become `__Host-gs_gm` / `__Host-gs_gm_csrf` (`Path=/`,
  `Secure`) outside Development and `gs_gm_dev` / `gs_gm_dev_csrf` in Development; `SameSite` comes from
  `Gm:Cookies:SameSite` (default `Lax`), no longer from "CORS on". SSO returns to `Gm:Console:BaseUrl` + a validated relative
  path, denials to `BaseUrl#/login?error=sso_denied`; `BaseUrl` is required together with a CORS console origin, and a
  deployment with neither has no console and starts as today (SSO lands on `/gm/v1/me`). `/gm/v1/ui`
  is mapped only in Development/Test. Startup refuses unsafe or broken combinations. Phase A ends with a gate (full suites,
  Couchbase flavor matrix) and a focused security review.
- **Phase B — console (US5–US9).** `gm-console/` npm workspace: `@gameserver/gm-shell` (TypeScript source package: config,
  typed client with CSRF/401/403 handling, session and guard, version-skew warning, catalog parser, internal registry,
  navigation, read pages) and the default app; codegen from `docs/api/gm-openapi.json` with a stale check; `deploy/` with
  one source of static-host headers; Vitest unit/component tests; an end-to-end smoke with `playwright-core` and the system
  Chrome against run-local, on the Vite dev server and on the built app under the reference CSP; docs, PCRs, release notes
  and the owner's requirement rows.

Decisions: [research.md](research.md) (R1–R17, VERIFY V2–V13); shapes: [data-model.md](data-model.md); contracts:
[public-api.md](contracts/public-api.md), [console-hosting.md](contracts/console-hosting.md).

## Technical Context

**Language/Version**: C# / .NET 10 (SDK 10.0.403, ASP.NET Core 10.0.14) for Phase A; TypeScript 5.9 on Node 22.20.0 /
npm 10.9.3 for Phase B.

**Primary Dependencies**: backend — none new (CORS, antiforgery, OIDC from the shared framework). Frontend (exact versions
by T025, research R7) — React 19, Mantine 9 (core, hooks), TanStack Query 5, React Router 7 (hash router), openapi-fetch
0.17, openapi-typescript 7.13, Vite 8, Vitest 4, jsdom 28, Testing Library, ESLint 10 + typescript-eslint 8,
playwright-core 1.64 (system browser only).

**Storage**: none changed. Browser: memory only (no storage APIs).

**Testing**:
- .NET: xUnit v3 + Shouldly through the HTTP pipeline (`GmTestHost`, `TestHost`, `SampleTestHost`, bare `WebApplication`
  over `TestServer` with Staging options for `__Host-`), Couchbase flavor matrix (`SampleHostFlavorTests`), architecture
  tests (versions, exact pins, no tracked bundle);
- frontend: Vitest + jsdom + Testing Library (fake `fetch`), `tsc --noEmit`, ESLint, codegen check, bundle check;
- end-to-end: Vitest (node) + `playwright-core` with `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe` (Edge
  once) against `tools/local/run-local.ps1`.

**Target Platform**: Linux containers for the server; any static host/CDN with custom domain + TLS for the console;
Windows/macOS/Linux dev machines. No Unity change.

**Project Type**: web-service framework + browser console shell (npm workspace).

**Performance Goals**: initial JS+CSS ≤ 500 KB gzip; GETs never preflight; one catalog request per session; first page ≤ 1 s
after sign-in locally; preflight cached 7200 s.

**Constraints**: SEC-04/05/06/10/11/12, design §12 invariants, ARC-01 (no frontend dependency in .NET projects, no Node in
`dotnet build`), CI-01 (codegen check), PCR-01; owner Q16–Q21; ports 5080 (run-local), 5173 (console), 5091/5092 (scratch);
5180 never used; never recurse into `samples/`.

**Scale/Scope**:
- Phase A: ~8 C# files changed, 1 new (`GmConsoleUrls`), ~6 test files changed, 3 new test files, 5 sample config files;
- Phase B: ~45 new TS/TSX files (shell ~35, app 3, e2e 4, scripts 2), 6 deploy files, 2 docs guides, PCR/HTML edits;
- 9 breaking changes (BC-1..BC-9), configuration-level, each with a fix.

## Overview

```text
 Phase A (server)                                                 Phase B (console, gm-console/)
 ┌───────────────────────────────────────────────┐               ┌───────────────────────────────────────────────────┐
 │ GmCors: expose X-Request-Id/Retry-After/ETag, │◄── CORS ──────│ api/createGmClient (openapi-fetch + middleware:   │
 │   max-age 7200, 1 origin outside Development  │   credentials │   credentials, X-CSRF-Token on non-GET, 403       │
 │ UseCors(/gm) → OnStarting headers on errors   │               │   CSRF_FAILED → refresh+retry once, 401/403 hooks,│
 │   (T004 proves; writer unchanged unless red)  │               │   Problem Details → GmProblem)                    │
 │ GmCookies: __Host-gs_gm / gs_gm_dev, Path=/,  │◄── cookies ───│ session/ (config.json → auth/methods → /me →      │
 │   SameSite = Gm:Cookies:SameSite (Lax)        │               │   guard Q19 → catalog; skew banner; logout)       │
 │ GmConsoleUrls: BaseUrl + relative path,       │◄── top-level ─│ pages/login (local form, SSO button)              │
 │   BaseUrl#/login?error=sso_denied             │    SSO        │ catalog/ (operationId map) × me.permissions       │
 │ GmOptionsValidator: SameSite, BaseUrl, origins│               │   → navigation/registry (internal, §7.3 shapes)   │
 │ gm.ui: condition development_tools            │               │ pages/ players, player(+tabs), receipt, staff, ops│
 └───────────────────────────────────────────────┘               │ generated/gm-api.d.ts ← docs/api/gm-openapi.json  │
                                                                 └───────────────────────────────────────────────────┘
 app/ (default, createGmConsole()) → vite build → dist/ (never committed) + config.json + deploy/security-headers.json
 e2e/ (playwright-core, system Chrome) → Vite dev | preview(headers) on :5173 → run-local :5080
```

## Module breakdown

| Module | Responsibility | Depends on | Does NOT own |
| --- | --- | --- | --- |
| Gm.Adapters `GmOptions` (+`GmCookieOptions`, `GmConsoleOptions`) | bind and validate `Gm:Cookies`, `Gm:Console`, CORS count | `GameServerOptions` | redirects |
| Gm.Adapters `Security/GmCookies` | cookie names per environment, write/delete options, antiforgery cookie options | options | session tickets |
| Gm.Adapters `Security/GmCors` | policy (expose, max-age), origin validation | options | CSRF decision |
| Gm.Adapters `Sso/GmConsoleUrls` (new) | accepted return path, success and denial targets | options | OIDC protocol |
| Gm.Adapters `Sso/GmSso` | OIDC wiring; uses `GmConsoleUrls` in start, callback, failures | `GmConsoleUrls`, `GmCookies` | target rules |
| Gm.Adapters `GmRoutes`/`GmModule` | `gm.ui` condition and conditional mapping | route catalog | — |
| Platform.Web `Errors/ErrorResponseWriter` | unchanged unless T004 is red | — | CORS policy |
| shell `config/` | load + validate `config.json` | — | API |
| shell `api/` | typed client + middleware, `GmProblem` | openapi-fetch, generated types | retry of commands |
| shell `session/` | phases, CSRF token, `/me`, guard, skew, sign-in/out | api, config | permissions decisions (server) |
| shell `catalog/` | parse GM document → `GmCatalog` | — | filtering by permission |
| shell `navigation/` | internal registry, visibility rule, menu | catalog, session | page content |
| shell `pages/` | login, players, player (+tabs), receipt, staff, ops, not-available | api, components | mutations (GM-3) |
| shell `components/` | layout, banners, error display, JSON text view, paged list | Mantine | data fetching policy |
| `app/` | default entry, local `config.json` | shell | game code |
| `deploy/` | header source, nginx example, CDN table, config example + schema | — | real infrastructure |
| `e2e/` | browser smoke against run-local | playwright-core, Vite | host lifecycle |
| Architecture tests (.NET) | versions, exact pins, no tracked bundle, no Node in projects | file system | npm |

No cycles: app → shell; shell knows no game; the server knows no console (only its origin and base URL as configuration).

## Constitution Check

| Principle | How this slice meets it | Result |
| --- | --- | --- |
| I Server authoritative | The console decides nothing: permissions, CSRF, scope are checked by the server per request; navigation hiding is UX (design §12.1) | PASS |
| II Explicit registration | `gm.ui` keeps a declaration with a condition; new options bound and validated at startup; the shell registers built-in pages explicitly | PASS |
| III Security deny-by-default | `__Host-` cookies, `Lax` by default, one exact origin, CORS never authenticates, SSO redirects only to configured `BaseUrl` + validated path, unsafe combinations fail startup, strict CSP for the static host, no storage, no HTML from server data | PASS |
| IV Async, bounded load | GETs without preflight, preflight cache 7200 s, one catalog per session, paged lists with `pageSize` 50; GM limiter unchanged | PASS |
| V Stable contract | No route/body change; GM document unchanged; configuration and cookie changes recorded as BC-1..BC-9 with fixes; codegen check | PASS |
| VI Multi-layer tests | Pipeline tests (CORS on errors, cookies, SSO matrix, validator), Couchbase flavor matrix, architecture tests, Vitest unit/component, codegen/bundle checks, browser e2e on two targets | PASS |
| VII Observability | Request id readable cross-origin and shown in every error; SSO denials keep their security log line; no new log of cookies or tokens | PASS |

Requirements check: SEC-04/SEC-06/TBD-05a/TBD-21 and SEC-13 (Q9: per-IP limit on GM auth endpoints only) wording updated in
the owner's file (FR-096); the code has no per-IP GM limit, so nothing exceeds Q9 (the auth limiter itself is a later slice);
GM-01 P0 adjust remains on the Development page (D10). Re-check after design: unchanged.

## Design decisions

Spec defaults D1–D20 and the decisions this plan adds (A1–A10). **Architect rows may be overridden by the owner.**

| # | Decision | Status | Research |
| --- | --- | --- | --- |
| D1 | H14 proven by a permanent test before any writer change | confirmed | R2 |
| D2 | One CORS origin outside Development | confirmed (owner confirmation 2) | R4 |
| D3 | `SameSite=None` only in Development/Test (with CORS, always `Secure`); refused elsewhere because case B is not supported | changed by review T024 m2 (orchestrator default in line with the owner's "case B not supported"; was "accepted outside Development") | R4 |
| D4 | Development names `gs_gm_dev` / `gs_gm_dev_csrf` | confirmed (owner confirmation 1) | R1 |
| D5 | `BaseUrl` required together with a CORS console origin; no console = starts, SSO lands on `/gm/v1/me` (coordinator correction) | confirmed | R4, R5 |
| D6 | Return path rules + append to `BaseUrl` | confirmed | R5 |
| D7 | Denial `BaseUrl#/login?error=sso_denied` | confirmed | R5 |
| D8 | Browser-cache revalidation of the catalog; no manual `If-None-Match` | confirmed | R6 |
| D9 | Internal registry, §7.3 shapes, not exported | confirmed | R8 |
| D10 | GM-2 mutations = sign-in, sign-out (+ CSRF token) | confirmed (owner confirmation 4) | R14 |
| D11 | Ledger tab included | confirmed | — |
| D12 | Sample host flavor values | confirmed | R16 |
| D13 | Version rule; TypeScript 5.9; React Router 7; jsdom 28 | confirmed | R7 |
| D14 | E2E smoke now; host started outside the test | confirmed (owner confirmation 5) | R11 |
| D15 | One CSRF refresh+retry on `403 CSRF_FAILED` | confirmed | — |
| D16 | Fake `fetch`, no MSW | confirmed | R11 |
| D17 | Requirement lines G6 and status edited too | confirmed | R17 |
| D18 | No bump/tag | confirmed | — |
| D19 | run-local console banner line | confirmed | — |
| D20 | Languages per repository rule | confirmed | — |
| A1 | **Architect.** `GmConsoleUrls` singleton owns every redirect rule; `GmSso` keeps the OIDC wiring; the OIDC `RedirectUri` carries only the accepted relative path | new | R5 |
| A2 | **Architect.** Cookie names are resolved from `GameServerOptions` by static members of `GmCookies` (no new service); the handler resolves once per request from DI options | new | R1 |
| A3 | **Architect.** CORS error-status probes live in a test-only module `CorsProbeModule` (throwing, slow and limiter endpoints), added through `GmTestHost.StartAsync(extra:)`, never in the committed document's host | new | R2 |
| A4 | **Architect.** Bare `WebApplication` + `TestServer` (HTTPS base address) with Staging `GameServerOptions` proves `__Host-` names and the antiforgery round trip in-process, because an in-memory Staging `GmTestHost` cannot start (017 T005 note) | new | R1 |
| A5 | **Architect.** Shell state = React context (session) + TanStack Query (reads); no state library | new | — |
| A6 | **Architect.** React Router hash router (`createHashRouter`); `#/login?returnTo=` is not used — the return hash is kept in memory (FR-057) so no PII goes into the URL | new | — |
| A7 | **Architect.** The e2e harness starts Vite (dev or preview) itself on 5173 and checks the API; the .NET host is started by the operator/final task (`run-local.ps1`) | new | R11 |
| A8 | **Architect.** From Phase B every checkpoint runs `npm run verify`; the .NET suite runs at Phase A gate and at close | new | — |
| A9 | **Architect.** The gm-console architecture tests live in `GameServer.Architecture.Tests` and read files only (no Node in `dotnet test`) | new | — |
| A10 | **Architect.** Ops page cards and player tabs are independent queries: one failing card/tab does not hide the others | new | — |

## Design per user story

### US1 — CORS for a console on another origin (FR-001..FR-004) — Phase A

`GmCors.Add` adds `.WithExposedHeaders(ExposedHeaders)` and `.SetPreflightMaxAge(7200 s)`. T004 writes
`GmCorsErrorHeadersTests` first: allowed and foreign origin × {200 `/me`, 304 catalog, 400 search without `pageSize`, 401
`/me` anonymous, 403 missing permission, 403 `CSRF_FAILED`, 404 unknown player and unknown `/gm/v1/nope`, 405 GET logout,
415 text body on a command, 429 limiter (`GmConcurrency=1`, `QueueFactor=0`, two concurrent calls on a waiting probe), 500
throwing probe, 503 timeout (`GmTimeoutSeconds=1`, waiting probe)}. Expected green on `main` (R2); the scripted check
removes the `/gm` `UseCors` branch and sees it red. T006 adds the exposed-header and `Retry-After` assertions after T005.
`GmCorsValidation.Validate` gains the count rule (needs `GameServerOptions.IsDevelopment`, already a parameter). FR-004 is a
guard test (sibling origin with `Sec-Fetch-Site: same-site`).

### US2 — Cookies (FR-010..FR-017) — Phase A

`GmCookies`: `SessionName/CsrfName(options)` = Host names unless `IsDevelopment`; `Session(http, expiresAt)`: `Path=/`,
`HttpOnly`, `SameSite` = setting, `Secure` = `!IsDevelopment || IsHttps || SameSite==None`. `Configure`: antiforgery
`Cookie.Name = CsrfName`, `Path=/`, `SameSite` = setting, `SecurePolicy = (!IsDevelopment || None) ? Always :
SameAsRequest`. Callers switch from the removed constants. Existing pins move to the new rule (CORS on → `Lax`); new
`GmCookiePolicyTests` adds the Development matrix, the `None` combinations, deletion attributes equal to write attributes,
a pre-018 `gs_gm` cookie → 401, and the A4 bare-app Staging proof. Every new assertion is red on the old code (names/path),
satisfying "a test must fail without its fix".

### US3 — SSO return (FR-020..FR-026) — Phase A

`GmOptionsValidator` gains the `BaseUrl` rules. `GmConsoleUrls` implements data-model §4; `GmSso.Map` start: `RedirectUri =
urls.AcceptReturnPath(returnUrl) ?? ""`; `OnTicketReceivedAsync` success: `Redirect(urls.SuccessTarget(context.ReturnUri))`,
denials and `OnRemoteFailureAsync` and the unhandled callback: `Redirect(urls.DeniedTarget())`. `SsoTests` get a second
host configuration with `BaseUrl` and the matrix of SC-003; the legacy expectations (`/gm/v1/me`) stay for the host without
`BaseUrl`. `GmConsoleUrlsTests` covers the no-console defaults per environment (`/gm/v1/ui` with development tools,
`/gm/v1/me` without). Sample host config gains the Local/Dev values (D12) and the `LocalFlavorFactory` users are re-run (R16).

### US4 — `/gm/v1/ui` Dev/Test only (FR-030) — Phase A

Declaration condition + guarded mapping; `CatalogCompositionTests` per flavor; the Couchbase flavor matrix asserts 404 on
Beta/Uat and 200 on Dev.

### Phase A gate

Release build 0 warnings; full suite in memory and with the File store; `SampleHostFlavorTests` (Couchbase); `git diff main
-- docs/api/gm-openapi.json` empty; release notes Phase A; a read-only security review of the Phase A diff by
`tech-lead-reviewer` with fixes (each with a red-first test). Phase B starts only after this.

### US5 — Workspace, toolchain, codegen, dev loop (FR-040..FR-048) — Phase B

T025 fixes the versions (R7). The skeleton renders `MantineProvider` + an empty router; root scripts per contract §8. The
architecture tests (A9) pin versions and tracked files. Codegen produces `gm-api.d.ts`; `check:api` proves staleness
detection with a temporary input copy (V12). ESLint config forbids FR-048 items (`no-restricted-globals`,
`no-restricted-syntax` for `JSXAttribute[name.name='dangerouslySetInnerHTML']`, `no-eval`, `no-new-func`). Dev loop: Vite
`server: { host: 'localhost', port: 5173, strictPort: true }`, `app/public/config.json`, run-local banner line.

### US6 — Session shell (FR-050..FR-062) — Phase B

`config/loadConfig(fetch)` → typed result or field error. `api/createGmClient` middleware: `onRequest` adds
`credentials: 'include'` (client option), CSRF header on non-GET, nothing on GET; `onResponse` maps errors to `GmProblem`
(body Problem Details, `X-Request-Id` header fallback) and calls `onUnauthorized`/`onForbidden`; `403 CSRF_FAILED` →
`csrf.refresh()` and one retry of the same request (body cloned). Network errors → `GmProblem{status:0}`.
`session/SessionProvider` runs the phases of data-model §7, guard, skew, sign-in (CSRF → login → CSRF → me) and sign-out.
Components: login page, layout (header with environment badge, red for `live`), skew banner, footer, block screen, error
display, JSON text view.

### US7 — Navigation and pages (FR-065..FR-076) — Phase B

`catalog/parseCatalog(doc)` → `GmCatalog`. `navigation/registry` with built-in definitions (data-model §9);
`visible(def, me, catalog)`. Hash router: `#/login`, `#/players`, `#/players/:playerId`, `#/receipts/:receiptId`, `#/staff`,
`#/staff/:staffId`, `#/ops`, fallback; a page not visible renders "not available" before any query. `PagedList` (TanStack
`useInfiniteQuery`, `pageSize` 50). Pages call `api.GET` by path with generated types. No mutation control (FR-074).

### US8 — Static host reference (FR-080..FR-085) — Phase B

V3 decides `style-src`. `deploy/security-headers.json` is the source; `deploy.test.ts` (Vitest) checks nginx/table equality
and validates `config.example.json` against `config.schema.json` (tiny hand-written validator for the few rules, or the
same validator the shell uses — no schema library). `vite.config.ts` preview reads the JSON (V11). `scripts/check-dist.mjs`
implements FR-083 and SC-007 (gzip of `index.html`-referenced JS/CSS ≤ 500 KB).

### End-to-end smoke (FR-087) — Phase B

`e2e/harness.ts` (Vite server per target, API precheck, browser launch with `executablePath`, CSP/console collectors, CDP
session for V13); `e2e/smoke.e2e.ts` (scenarios of FR-087). Request assertions read `page.on('request')` headers
(`origin`, `sec-fetch-site`, `x-csrf-token`) — no routing on the main context (routing disables the HTTP cache, which V13
needs); the Q19 scenario uses a second context with a routed `config.json`.

### US9 — Docs and requirements (FR-090..FR-096) — Phase B

PCR gm/platform, new guide, consumer guide §15, HTML, README, environments guide, release notes, requirement rows (owner's
local file, not committed).

## Where the plan narrows or changes the spec

| # | Spec item | Plan | Why |
| --- | --- | --- | --- |
| N1 | FR-002 "changes only for a red status" | T007 is a conditional task: closed with a Note when T004/T006 are green | probe R2 |
| N2 | FR-050 "deploy/config.schema.json describes the same rules" | the shell validator is hand-written; `deploy.test.ts` checks the example against both | no JSON-schema runtime dependency |
| N3 | FR-087 "and once with Edge" | the final task runs the smoke once with `GM_E2E_BROWSER` = Edge, both targets | SC-006 |
| N4 | FR-065 "once per session" | TanStack Query `staleTime: Infinity` for the catalog, cleared on 401/sign-out | — |

## Data flow

1. **Open**: static host → `config.json` → `auth/methods` → `/me` (credentialed, simple GET) → guard → catalog → menu.
2. **Local sign-in**: CSRF (anonymous) → preflight + POST login (cookie set on the CORS answer) → CSRF (bound) → `/me`
   from the answer.
3. **SSO**: top-level start → IdP → callback on the API host (cookie set top-level) → 302 to `BaseUrl#…` → open (1).
4. **Read page**: `api.GET(path)` → server: CORS → profile (cookie, permission, scope) → handler → JSON → Query cache.
5. **Error**: any status → CORS headers (OnStarting) → Problem Details → `GmProblem` → error display (request id).
6. **Sign-out**: POST logout (CSRF) → cookie expired → state cleared → login.

## Performance

- GETs are simple requests; preflight only for the two POSTs, cached 7200 s.
- Catalog ≈ 40 KB (017 document 40 454 bytes), once per session, then 304 revalidations.
- Bundle target ≤ 500 KB gzip (React + Mantine core + Query + Router); routes are small enough that code splitting is not
  needed in GM-2; `check:dist` measures.
- Phase A adds no per-request work besides the CORS headers already evaluated.

## Seams for later work and extension points

- GM-3: `GmOperation` gains `bodySchema`/`parameters`; the client passes `X-Request-Id` through; the error display already
  renders `errors`; the registry gains operation views; the player page gets an "Actions" tab.
- GM-4: export `registry` types and `createGmConsole({ extensions })`, `overrideOperation`; game workspace template using
  `framework/gm-console/packages/shell` (V9 de-risks it now).
- A release asset of the default app per tag (Q18 later) builds the same `app/`.
- Case B would need `Partitioned` cookies or a token exchange — not designed.

## Project Structure

### Documentation (this feature)

```text
specs/018-gm-console-hosting-shell/
├── spec.md                       # US1–US9, FR-001..FR-099 (70), BC-1..BC-9, SC-001..SC-010, D1–D20
├── plan.md                       # this file
├── research.md                   # R1–R17, VERIFY V2–V13, code facts
├── data-model.md                 # options, cookies, CORS, redirects, config.json, state, catalog, registry, headers, e2e vars
├── contracts/public-api.md       # C# options/cookies/CORS/SSO/routes; shell public and internal TS surface; scripts
├── contracts/console-hosting.md  # layout, sequences, guarantees, deployment order, compatibility
├── release-notes-draft.md        # created by T002, completed by T063
└── tasks.md                      # task list
```

### Source code — Phase A (files to change)

```text
src/GameServer.Modules.Gm.Adapters/
├── GmOptions.cs                    # GmCookieOptions, GmConsoleOptions, validator rules
├── Security/GmCookies.cs           # names per environment, Path=/, SameSite setting, Secure rules, antiforgery
├── Security/GmCors.cs              # exposed headers, max-age 7200, one-origin rule
├── Sso/GmSso.cs                    # uses GmConsoleUrls; removes DefaultReturnUrl/DeniedRedirect/SafeReturnUrl
├── Sso/GmConsoleUrls.cs            # NEW
├── GmCookieAuthenticationHandler.cs · GmSessionService.cs · GmModule.cs   # cookie names; gm.ui guarded mapping
└── GmRoutes.cs                     # gm.ui condition
src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs   # ONLY if T004/T006 red (T007)
samples/SampleHost/appsettings.{Local,Dev}.json   # Gm:Cors / Gm:Console values (D12); Beta/Uat/Live unchanged (no console)
```

Tests (Phase A):

```text
tests/GameServer.Gm.Tests/
├── GmCorsErrorHeadersTests.cs (NEW)  + Infrastructure/CorsProbeModule.cs (NEW)
├── GmCookiePolicyTests.cs (NEW)      # Development matrix, None, deletion, pre-018 cookie, bare-app Staging __Host-
├── GmConsoleUrlsTests.cs (NEW)       # unit: accepted/refused paths, targets with/without BaseUrl, sub-path
├── CorsTests.cs · GmAuthTests.cs · CsrfAndSessionTests.cs · SsoTests.cs · GmTestHost.cs   (existing, updated)
└── GmOptionsValidatorTests.cs (NEW)  # SameSite, BaseUrl, origin-count rules (unit, + one host startup report)
tests/SampleGame.Tests/CatalogCompositionTests.cs                                   (gm.ui per flavor)
tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs       (cookies over HTTPS, gm.ui, BaseUrl)
```

### Source code — Phase B (new)

```text
gm-console/
├── package.json · package-lock.json · .npmrc · .nvmrc · tsconfig.base.json · eslint.config.js · vitest.config.ts
├── packages/shell/
│   ├── package.json · tsconfig.json
│   └── src/
│       ├── index.ts                                 # createGmConsole + public types
│       ├── generated/gm-api.d.ts                    # codegen (committed)
│       ├── config/loadConfig.ts (+test)
│       ├── api/client.ts · api/problem.ts (+tests)
│       ├── session/SessionProvider.tsx · session/phases.ts · session/versions.ts · session/csrf.ts (+tests)
│       ├── catalog/parseCatalog.ts (+test, fixture = docs/api/gm-openapi.json)
│       ├── navigation/registry.ts · navigation/builtins.ts · navigation/Menu.tsx (+tests)
│       ├── router.tsx · App.tsx
│       ├── components/Layout.tsx · EnvBadge.tsx · SkewBanner.tsx · BlockScreen.tsx · ErrorDisplay.tsx · JsonView.tsx · PagedList.tsx (+tests)
│       ├── pages/LoginPage.tsx · PlayersPage.tsx · PlayerPage.tsx · ReceiptPage.tsx · StaffListPage.tsx · StaffPage.tsx · OpsPage.tsx · NotAvailable.tsx (+tests)
│       └── test/fakeFetch.ts · test/render.tsx
├── app/  package.json · index.html · vite.config.ts · tsconfig.json · src/main.tsx · public/config.json · src/vite-env.d.ts
├── deploy/ security-headers.json · nginx.conf.example · headers.md · config.example.json · config.schema.json · deploy.test.ts
├── e2e/  vitest.e2e.config.ts · harness.ts · smoke.e2e.ts · README.md
└── scripts/ check-dist.mjs
tests/GameServer.Architecture.Tests/GmConsoleWorkspaceTests.cs (NEW) · VersionTests.cs (extended)
tools/local/run-local.ps1 · run-local.sh · tests/GameServer.Architecture.Tests/RunLocalScriptTests.cs   (banner line)
.gitignore (gm-console node_modules, dist, e2e artifacts) · .editorconfig (2-space TS/JSON/CSS under gm-console)
```

Docs:

```text
docs/pcr/gm.md · docs/pcr/platform.md (§14, new §24) · docs/guides/gm-console.md (NEW) · docs/guides/consumer-guide.md (§15, §11)
docs/guides/environments-and-local-run.md · docs/html/01-gioi-thieu.html · docs/html/03-deploy.html · README.md
specs/018-gm-console-hosting-shell/release-notes-draft.md
Requirements/Game_Server_System_Requirements_v1.2.md   (owner's, gitignored, never committed)
AgentMemories/features/p05-speckit/00_summary.md · AgentMemories/features/gm-tool/00_summary.md   (close)
```

**Structure Decision**: no new .NET project; one new folder `gm-console/` outside the solution.

## Test strategy (per FR and SC)

| FR / SC | Tests |
| --- | --- |
| FR-001 | `CorsTests` (preflight max-age 7200, methods/headers unchanged), `GmCorsErrorHeadersTests` (expose list) |
| FR-002, SC-001 | `GmCorsErrorHeadersTests` (11 statuses × 2 origins; scripted red check) |
| FR-003 | validator unit cases + host startup failure line |
| FR-004 | `CorsTests` sibling `same-site` case |
| FR-010..FR-016, SC-002 | `GmCookiePolicyTests`, updated `GmAuthTests`/`CsrfAndSessionTests`/`CorsTests`, Couchbase flavors over HTTPS |
| FR-017 | compile (removed constants), `GmCookiePolicyTests` names |
| FR-020, FR-021 | validator unit cases + host startup lines; `SampleHostFlavorTests`: Beta without console settings starts, Uat with test console values, Live keeps one violation |
| FR-022..FR-026, SC-003 | `GmConsoleUrlsTests` (unit matrix), `SsoTests` (fake IdP, both configurations, every denial path) |
| FR-030, SC-004 | `CatalogCompositionTests`, `GmAuthTests.Ui_page…` (Development), Couchbase flavors (404) |
| FR-040..FR-043, FR-047 | `GmConsoleWorkspaceTests` (layout, lockfile v3, exact versions, tracked files), `VersionTests` |
| FR-044, SC-005 | `npm run check:api` + scripted stale input (T028) |
| FR-045 | e2e dev target; `RunLocalScriptTests` banner |
| FR-046 | `GmConsoleWorkspaceTests`: no `.csproj`/`.props`/`.targets` mentions `npm`/`node` |
| FR-048 | ESLint run over a temporary fixture file with each forbidden item (T029 Note) |
| FR-050..FR-062 | Vitest: `loadConfig`, `client`, `csrf`, `versions`, `SessionProvider`, `LoginPage`, `Layout`, `BlockScreen`, `ErrorDisplay`, `JsonView` |
| FR-065..FR-076 | Vitest: `parseCatalog` (committed document), `registry`/`Menu`, router not-available, `PagedList`, each page with fake fetch |
| FR-080..FR-084, SC-007 | `deploy.test.ts`, `check-dist.mjs`, e2e preview target (0 violations) |
| FR-085 | guide walkthrough (T065) |
| FR-087, SC-006, SC-008 | e2e smoke (dev + preview, Chrome; Edge once), preflight count, catalog request count, timing |
| FR-090..FR-096, SC-010 | docs walkthrough (T065); requirement rows read back (T062) |
| FR-098, FR-099, SC-009 | VERIFY Notes; final verification (T066, T067) |

## Breaking changes and migration (feeds the release notes)

| BC | What changes | Steps for a game (zh-pvp) |
| --- | --- | --- |
| BC-1 | cookie names/path; one-time sign-out | tell staff |
| BC-2 | removed constants | replace with `GmCookies` members (none used in zh-pvp, T003 checks) |
| BC-3 | CORS no longer implies `SameSite=None` | none (case A) |
| BC-4 | a CORS origin needs `BaseUrl` of the same origin | none (zh-pvp sets no CORS origin); set both together when a console exists |
| BC-5 | one origin outside Development | none expected |
| BC-6 | `/gm/v1/ui` 404 outside Dev/Test | none |
| BC-7 | SSO targets | start SSO from the console |
| BC-8 | `gm.ui` condition | update pinned route lists if any |
| BC-9 | exposed headers, max-age | none |

## Implementation phases (input for tasks.md)

1. **Setup + V2 gate** (Phase A): baseline, release notes skeleton, inventory, CORS error-header test.
2. **US1 CORS**; **US2 cookies**; **US3 SSO + sample config**; **US4 `/gm/v1/ui`** + flavor matrix.
3. **Phase A gate + security review** — blocks everything after.
4. **US5 workspace** (V8 gate first): skeleton, architecture tests, codegen, lint, dev loop.
5. **US6 session shell**; **US7 navigation and pages**.
6. **US8 static host** (V3 gate first), then **end-to-end smoke** (V11, V4, V13).
7. **US9 docs and requirements**.
8. **Close**: V9 scratch, reviewer walkthrough, final verification, Couchbase suite, tech-lead review and fixes, resume.

Dependencies: 1 → 2 (US1 → US2 → US3 → US4, sequential: `GmOptions.cs`, `GmCookies.cs`, `GmModule.cs`, `CorsTests.cs` are
shared) → 3 → 4 → 5 (US6 → US7: shared `App.tsx`, `router.tsx`) → 6 → 7 → 8. Inside Phase B, page tasks (T043–T047) touch
different files and run in parallel.

**Estimated tasks: 70** (Phase A 24: setup 4, US1 6, US2 5, US3 5, US4+gate 4; Phase B 46: US5 7, US6 7, US7 11, US8 4,
e2e 3, US9 7, close 7). tasks.md is authoritative.

## Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| A CORS error path still loses headers on the real pipeline (H14 partly true) | High: console cannot read errors | T004 covers every writer path incl. limiter/timeout/exception; fix only that path (T007) |
| Cookie rename breaks a game's tests or tools silently | Med | BC-1/BC-2 in release notes; T003 greps zh-pvp read-only |
| A deployment without console lands staff on `/gm/v1/me` JSON after SSO | Low | Documented as "no console" mode; the real fix is a console domain (Q16) |
| Development names diverge from production names; a bug only in `__Host-` handling | Med | A4 bare-app test + Couchbase flavor test over HTTPS assert the production names and an antiforgery round trip |
| Toolchain peer/engine conflicts at install (TS 7, Router 8, jsdom 30) | Med | V8 gate with `engine-strict`; R7 candidates |
| Mantine needs inline styles under CSP | Med | V3 gate; documented fallback limited to `style-src` |
| E2E flakiness (ports, host not started, slow first Vite compile) | Med | strict port 5173, API precheck with a clear message, generous first-load timeout, run each target in its own browser |
| `localhost`/`127.0.0.1` mix-ups in dev and docs | Low | one rule in code, config, banner and guide (R3) |
| `LocalFlavorFactory` tests change behaviour with CORS on in the Local config | Low | R16; T019 re-runs them |
| Requirements file is outside git (edits not reviewable by diff) | Low | T062 records before/after text in its Note |
| Bundle exceeds 500 KB gzip | Low | `check:dist` measures; tree-shaken Mantine imports |

## Complexity Tracking

| Item | Why needed | Simpler alternative rejected because |
| --- | --- | --- |
| Development cookie names | Plain HTTP local loop + antiforgery `Always` refusal | `__Host-` everywhere fails on HTTP (R1); a Set-Cookie rewrite is a hidden side effect |
| `GmConsoleUrls` | One place for open-redirect rules and both configurations | Inline in `GmSso` mixes OIDC wiring with URL policy |
| Two e2e targets | Q16 accepts on the dev server; the CSP is only real on the built app | One target would miss either acceptance or the CSP proof |
| Header single source + consistency test | The document must equal what is tested (017 lesson) | Hand-kept nginx/table drift |

## Owner questions (also in the release notes, "decisions awaiting owner override")

1. **D4**: Development cookie names `gs_gm_dev` / `gs_gm_dev_csrf` (production `__Host-…`). OK?
2. **D2 / BC-5**: one console origin per environment outside Development. OK?
3. **D3**: answered by review T024 m2 (orchestrator default): `SameSite=None` is refused outside Development/Test.
4. **D10**: no business mutation in the console until GM-3. OK?
5. **D14**: e2e smoke in GM-2 with the host started outside the test. OK?
