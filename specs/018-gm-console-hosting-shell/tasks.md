# Tasks: GM console hosting and shell — a GM API usable from a console on another origin of the same site and the framework's React console shell with its default app, read pages, static-host reference and local end-to-end loop (slice 018 gm-console-hosting-shell, GM-2)

**Input**: [plan.md](plan.md) (design, spec defaults D1–D20, architect decisions A1–A10, narrowings N1–N4, breaking changes
BC-1..BC-9), [spec.md](spec.md) (US1–US9, FR-001..FR-099, SC-001..SC-010), [research.md](research.md) (R1–R17, VERIFY
V2–V13, pins R15, sample-config trap R16), [data-model.md](data-model.md), [contracts/public-api.md](contracts/public-api.md),
[contracts/console-hosting.md](contracts/console-hosting.md). Design input: `AgentMemories/features/gm-tool/01_design.md`
§6–§8, §10, §12, §16–§18. **Where this file and plan.md differ, this file wins** (see Planning notes at the end).

**Two phases (owner Q21).** **Phase A = T001–T024** (backend: US1–US4, gate, security review). **Phase B = T025–T070**
(console: US5–US9, end-to-end smoke, close). **No Phase B task starts before T024 is done** (Phase A gate green and its
review findings fixed).

**Architect decisions applied** (owner questions 1–5 of the plan stay open; the release notes track their status, T002):
- **H14 test-first** (D1): the CORS-on-errors behaviour is proven on the real pipeline (T004) before any change; the
  planning probe says ASP.NET Core 10 already keeps the headers (`OnStarting`), so `ErrorResponseWriter` changes only for a
  status the test shows red (T007).
- **Cookies** (D4, A2): `__Host-gs_gm` / `__Host-gs_gm_csrf` outside Development; `gs_gm_dev` / `gs_gm_dev_csrf` in
  Development; `Path=/`; `SameSite` from `Gm:Cookies:SameSite` (default `Lax`); names from `GmCookies` members.
- **SSO** (D5–D7, A1): `GmConsoleUrls` owns the return/denial targets; `Gm:Console:BaseUrl` required together with a CORS
  console origin (same origin); neither = no console, startup unaffected, SSO lands on `/gm/v1/me` (`/gm/v1/ui` with
  development tools); denial `BaseUrl#/login?error=sso_denied`.
- **Console** (D8–D11, D15, A5, A6, A10): memory-only state (React context + TanStack Query), hash router, catalog by the
  browser cache (no manual `If-None-Match`), internal registry (design §7.3 shapes, not exported), mutations limited to
  sign-in/sign-out, one CSRF refresh+retry on `403 CSRF_FAILED`, Ledger tab included.
- **Toolchain** (D13, D16, A9): exact versions chosen by T025 (TypeScript 5.9, React Router 7, jsdom 28), `engine-strict`,
  fake `fetch` instead of MSW, architecture tests in .NET read files only.
- **Static host and e2e** (D14, A7): one header source `deploy/security-headers.json`; e2e = Vitest (node) +
  `playwright-core` + system Chrome, two targets (dev server, built app under the reference headers), run-local started
  outside the test.
- **No version bump, tag or push** (D18).

**Tests**: mandatory (constitution VI). Each story phase writes its tests before or with the code they cover and ends
green. Every fix has a test that is red without it; where a task adds a guard test for behaviour that already exists, it
records a scripted check that the test turns red when the guarded code is disabled (017 review lesson). Couchbase tests skip
with a message when `COUCHBASE_CONNSTR` is not set; the Couchbase project runs at the Phase A gate (flavor matrix) and in
full at the end (T067). Scenario ids (`US3-2`) are the spec's acceptance scenarios. The committed GM document
`docs/api/gm-openapi.json` must not change in this slice (SC-009); `OpenApiTests` staying green proves it.

**Format**: `- [ ] T0NN [P] [USn] description with paths`, then `Done when:` (verifiable) and the FR/SC/BC ids covered.
`[P]` = different files and no open dependency. Record findings as `Note:` lines under the task.

**MVP = Phase A (T001–T024)**: a console on another origin of the same site can sign in (SSO), read every answer
including errors, and the cookies follow Q20. It ships alone (backend only; the Development page still works).

**[VERIFY]**: a runtime fact from research.md to confirm on the pinned versions **before** the task that depends on it.
Scratch checks run in a throw-away project under the session scratch folder (never committed); V2 is a permanent test.
Record the result as a `Note:`. When the fact is wrong, apply the stated fallback and record the deviation (contract, docs).

| Item | Fact | Fallback | Check | Used by |
| --- | --- | --- | --- | --- |
| V2 | CORS headers are on every `/gm/**` answer for the allowed origin, errors included | Fix only the red path (writer or pipeline) | T004 (**gate**, permanent) | T006, T007 |
| V3 | Mantine runs under `style-src 'self'` | `style-src 'self' 'unsafe-inline'` (styles only) | T050 (**gate**) | T051 |
| V4 | `Lax` cookie on credentialed same-site fetch; `Set-Cookie` on CORS answers applied; deletion applied | — (answered for Chrome 157 / Edge 156 in planning, R1) | T055 (re-confirm) | — |
| V5 | Antiforgery with `__Host-` works on HTTPS; policy `Always` throws on HTTP | — (answered in planning, R1) | T014 (permanent tests) | T012 |
| V7 | Firefox case B | not run (no Firefox) | — | — |
| V8 | Exact versions satisfy Node 22.20 (`engine-strict`) and peer ranges | Older line of the same package | T025 (**gate**) | T026 |
| V9 | A game workspace consumes `packages/shell` from a submodule path | Document `file:` + explicit peer install | T064 (scratch) | GM-4 |
| V11 | `vite preview` applies configured headers to every path class | Small Node static server in `e2e/` | T052 | T054 |
| V12 | `openapi-typescript` output deterministic, LF | LF normalization in `gen:api` | T028 | T028 |
| V13 | Browser revalidates the catalog (304) under credentialed CORS | Catalog once per session, no ETag reliance | T056 | — |

**Environment**: `"$HOME/.dotnet10/dotnet.exe"`; build `build GameServer.slnx -c Release`; test
`test --solution GameServer.slnx -c Release --no-build` (one class: `--project X --filter-class "*Name"`). Couchbase
variables as in `AgentMemories/features/p05-speckit/00_summary.md` ("Environment"; password from the user variable
`COUCHBASE_PW`, never printed). Node 22.20.0, npm 10.9.3 (`gm-console/`: `npm ci`, `npm run verify`, `npm run e2e`).
Browsers: `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe` (157), `C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe`
(156) — **never download a browser** (corporate network). **Never grep recursively over `samples/`** (Unity `Library/`
under KestrelDemo): search `src/`, `tests/`, `templates/`, `tools/`, `modules/`, `docs/`, `gm-console/` and, by project
folder only, `samples/SampleHost`, `samples/SampleGame/SampleGame`, `samples/SampleGame/SampleGame.Adapters`. Ports: run-local
5080, console 5173, scratch 5091/5092; **5180 belongs to the owner and is never bound or probed**. `.specify/feature.json`
points to `specs/018-gm-console-hosting-shell`. Repository docs are in Vietnamese, except the consumer guide and the release
notes (English); keys, routes and code stay literal. **Never edit `E:\ProjGits\*`** (read-only checks only). The owner's
`Requirements/` file is gitignored: edit it (T062), never commit it. No commit is part of any task (the orchestrator commits;
no Claude signature lines).

## Phase 1: Setup (Phase A)

- [x] T001 Baseline on branch `018-gm-console-hosting-shell` (base `47f7468`): Release build of `GameServer.slnx`; the
  `GameServer.Gm.Tests` project alone (count, duration); record into the scratch folder (not committed):
  (a) totals and the Gm.Tests wall time (reference for T066);
  (b) SHA-256 of `docs/api/gm-openapi.json` (SC-009 reference);
  (c) `git status` (only `specs/018-gm-console-hosting-shell/` and the owner's `tasks/` untracked).
  Done when: 0 warnings, 0 failures; the values recorded as a Note. (FR-099)
  Note (2026-11-06, HEAD `caeecf6` = base `47f7468` + docs): Release build 0 warnings / 0 errors (39 s);
  `GameServer.Gm.Tests` alone: 197 total, 196 passed, 1 skipped (opt-in perf), 0 failed, **31.6 s** (reference for T066);
  SHA-256 of `docs/api/gm-openapi.json` = `5d274034c2a93852ef31a22e986a4b74a775c36342d25a0fd1ec35544af342b6`;
  `git status`: only `?? tasks/` (the 018 spec folder is committed in `caeecf6`). Recorded in the session scratch folder.
- [x] T002 [P] Release notes draft `specs/018-gm-console-hosting-shell/release-notes-draft.md` (FR-095), created now and
  extended by every story checkpoint (T010, T015, T020, T023, T031, T038, T049, T053, T056) before the final pass (T063).
  English, like the 013–017 drafts. Skeleton:
  - summary: GM-2 of the GM tool track; Phase A backend (cookies, CORS, SSO return, Dev/Test-only page), Phase B console
    shell + default app + static-host reference; "no route/body/proto/Couchbase/Unity change; the GM document is
    unchanged; every staff member signs in again once";
  - BC-1..BC-9 table from the spec;
  - first-consumer migration (zh-pvp) from the spec (no new setting needed to bump; Local values optional; console origin + `BaseUrl` together once a console exists;
    staff sign in again; CI job later);
  - "Decisions awaiting owner override": plan owner questions 1–5, each "applied, owner confirmation pending";
  - "Verification items" (V2–V13 results), "Docs touched", "Tests", "Next tag" (`v0.8.0` proposed with 017, not part of
    the slice).
  Done when: the file exists with every section and the BC table. (FR-095)
  Note: created with summary, BC-1..BC-9 table, zh-pvp migration (with the T003 findings), Phase A/B story placeholders,
  owner questions 1–5 ("applied, owner confirmation pending"), verification table, docs, tests, next tag.
- [x] T003 [P] **Inventory of pins and traps** (read-only; research R15, R16): grep `src/`, `tests/`, `templates/`,
  `tools/`, `modules/`, `docs/`, `README.md` and the project folders of `samples/SampleHost`, `samples/SampleGame/*` for
  `gs_gm`, `CookieName`, `CsrfCookieName`, `Path=/gm`/`path=/gm`, `samesite`, `SameSite`, `DefaultReturnUrl`,
  `DeniedRedirect`, `SafeReturnUrl`, `sso_denied`, `/gm/v1/ui`, `UiRoute`, `gm.ui`, `Gm:Cors`, `Access-Control`,
  `SetPreflightMaxAge`, `StagingSecrets`, `LocalFlavorFactory<` (users of the real sample config), `GM UI`; locate where
  the existing `GmOptionsValidator` cases live; check `tests/ConsumerFixture` (fixture host flavors, GM module since 017,
  any cookie/route pin); read-only grep of `E:\ProjGits\zh-pvp-server\src` and `tests` for `gs_gm`, `CookieName`,
  `DefaultReturnUrl`, `gm/v1/ui`, `Gm:Cors`.
  Done when: a Note lists every pin with file:line and its updating task (R15's table confirmed or corrected), every
  `LocalFlavorFactory<SampleHost …>` test class (re-run by T019), and the zh-pvp findings (BC-2 exposure). (BC-1, BC-2,
  BC-8)
  Note: R15 confirmed, plus pins it missed (all updated by T013 unless stated):
  `GmCookieAuthenticationHandler.cs:27,36` (`CookieName`), `GmSessionService.cs:119`, `GmModule.cs:230,256`, `GmSso.cs:26-27,
  78,96,105-112,157,164,166,174` (T013/T017), `GmCookies.cs:17,26,42,45`, `GmCors.cs:29` (T005), `GmRoutes.cs:38` (T021);
  tests: `CsrfAndSessionTests.cs:90,99,108,114,124,130` (`CookieName` + jar URI `http://localhost/gm/`), `:156-164` (path
  `/gm`, `CsrfCookieName`); `GmTestHost.cs:104`; `GmAuthTests.cs:41-42`; `CorsTests.cs:32-49` (+ every `CorsTests` host
  with an origin needs `Gm:Console:BaseUrl` after T016); `SsoTests.cs:84,94,101,114,124,133` (`DeniedRedirect`, T018),
  `:136-138` (`DefaultReturnUrl`, T018); **`tests/GameServer.Observability.Tests/LoggingTests.cs:69`** and
  **`tests/GameServer.Platform.Web.Tests/RequestLogPrivacyTests.cs:85`** (`CookieName`, T013 — not in R15);
  `src/GameServer.TestKit/GmBrowser.cs:12` (doc comment `gs_gm`, T013); `CatalogCompositionTests.cs:35` (T021);
  `RunLocalScriptTests.cs:98` (T030, Phase B). No `GmOptionsValidator` test file exists: unit cases live in
  `GmAuthTests.cs:220-227` and `SsoTests.cs:162-175` (kept there; new rules go to the new `GmOptionsValidatorTests.cs`).
  `tests/ConsumerFixture`: no `Gm` section, no cookie/route pin; its `LocalFlavorFactory<Program>` is the fixture host.
  Docs pins (Phase B, T057): `docs/pcr/gm.md:60-61,72,78,94,97`; `README.md:52,117`; `run-local.ps1:279`, `run-local.sh:246`.
  `LocalFlavorFactory<SampleHost Program>` users (re-run by T019): `FrameworkModuleCoverageTests` (Architecture.Tests),
  `SdkRouteTests` (Client.Tests), `LateEndpointValidationTests`, `LocalFlavorFactoryStartupFailureTests`, `WireCatalogTests`
  (Platform.Web.Tests), `SampleHostSmokeTests` (Sample.Counter.Tests), `CatalogCompositionTests` (SampleGame.Tests).
  Staging/Production starts: `StagingSecrets.Create()` users `SampleHostFlavorTests`, `CouchbaseProvisioningTests`,
  `CatalogCompositionTests` — none sets `Gm:Cors`, so FR-021 adds no violation.
  zh-pvp (read-only): no `gs_gm`, `CookieName`, `DefaultReturnUrl`, `Gm:Cors`, `gm.ui` in `src/`/`tests/`; only
  `src/ZhPvp.Host/Properties/launchSettings.json:6` `launchUrl: gm/v1/ui` in its `local` profile (Local = Development
  flavor, page kept) → BC-2 exposure none.
- [x] T004 [P] **[VERIFY] V2 — decision gate for CORS headers on error answers (permanent test)**: new
  `tests/GameServer.Gm.Tests/Infrastructure/CorsProbeModule.cs` (test-only GM module, added only through
  `GmTestHost.StartAsync(extra:)`, A3): `GET /gm/v1/probe-cors/throw` (throws `InvalidOperationException`),
  `GET /gm/v1/probe-cors/wait` (awaits a gate the test releases; `gm.access`), declared routes, `AsGmEndpoint`. New
  `tests/GameServer.Gm.Tests/GmCorsErrorHeadersTests.cs`: host with `Gm:Cors:AllowedOrigins:0 = https://gm.example.com`;
  for `Origin: https://gm.example.com` and `Origin: https://evil.example.com`, assert presence/absence of
  `Access-Control-Allow-Origin` (= the origin) and `Access-Control-Allow-Credentials: true` on:
  200 (`/me`, signed-in browser), 304 (catalog with its `ETag`), 400 (`/gm/v1/players/search` without `pageSize`), 401
  (`/me` anonymous), 403 (staff without `player.read` on a player read), 403 `CSRF_FAILED` (cookie POST without token),
  404 (unknown player; unknown `/gm/v1/nope`), 405 (`GET` logout), 415 (`text/plain` body on `identity.unban`), 429
  (second host with `GameServer:Limits:GmConcurrency = 1`, `QueueFactor = 0`: two concurrent `wait` calls), 500 (`throw`),
  503 (third host with `GameServer:Limits:GmTimeoutSeconds = 1`: one `wait` call never released).
  Run on the **current code** (no production change). Scripted check (not committed): comment out the `/gm` `UseCors` line
  in `GameServerApplicationExtensions.cs:74`, rebuild, the allowed-origin cases are red; restore, rebuild, green.
  Done when: the test is committed-ready and the Note records per status green/red on current code and the scripted check.
  Note (V2): `GmCorsErrorHeadersTests` (12 tests: 200, 304, 400, 401, 403, 403 `CSRF_FAILED`, 404 endpoint + routing, 405,
  415, 429, 500, 503; each asserts status, error code and both origins) **all green on the current code**. The signed-in
  browser runs over HTTPS (pre-018 code makes the antiforgery cookie `Always`-Secure as soon as CORS is on). Scripted check:
  `app.UseWhen(... UseCors ...)` commented out in `GameServerApplicationExtensions.cs:74`, rebuild → **12/12 red**; restored
  (`git checkout`), rebuild → 12/12 green. **Gate: no red status → T007 closes with "no change".**
  **Gate**: all green → T007 closes with "no change" (design H14 recorded as not reproduced); any red → T007 fixes exactly
  those paths. (FR-002, SC-001, FR-098)

## Phase 2: US1 — A console on another origin of the same site reads every GM answer, errors included (P1, H14, H16) — Phase A

- [x] T005 [US1] `src/GameServer.Modules.Gm.Adapters/Security/GmCors.cs`: `ExposedHeaders` (`X-Request-Id`, `Retry-After`,
  `ETag`) and `PreflightMaxAge` (7200 s) members (contract §3); policy `.WithExposedHeaders(...)`,
  `.SetPreflightMaxAge(PreflightMaxAge)`; doc comment updated (no more "same-origin default (TBD-05a)" wording, case A).
  Tests in `tests/GameServer.Gm.Tests/CorsTests.cs`: preflight from the allowed origin has `Access-Control-Max-Age: 7200`,
  `Allow-Methods` and `Allow-Headers` unchanged; an actual GET from it has `Access-Control-Expose-Headers` with the three
  names (red before the change).
  Done when: the new assertions are red on the old code and green now; `CorsTests` otherwise unchanged except the SameSite
  test (handled by T013). (FR-001, BC-9)
  Note: `CorsTests.Preflights_are_cached_two_hours_and_answers_expose_the_console_headers` red on the old code
  (`Access-Control-Max-Age` "600"), green after the change; `Allow-Methods` = `GET,POST,PUT,DELETE`, `Allow-Headers` =
  `Content-Type,X-CSRF-Token,X-Request-Id` pinned unchanged.
- [x] T006 [US1] Extend `GmCorsErrorHeadersTests` (T004): every allowed-origin case also asserts the exposed-header list;
  429 and 503 assert `Retry-After` present; every error case asserts `X-Request-Id` present (the console reads it, FR-062).
  Done when: green after T005 (or red only where T004 was red, fixed by T007). (FR-001, FR-002, SC-001)
  Note: green (12/12). Scripted check: `.WithExposedHeaders(...)` removed → 12/12 red (and the T005 test red); restored.
- [x] T007 [US1] **Conditional** (gate T004/T006): for each status red in T004/T006, fix the path that writes it without
  CORS headers (`src/GameServer.Platform.Web/Errors/ErrorResponseWriter.cs` or the pipeline order in
  `GameServerApplicationExtensions.cs`), smallest change, no CORS logic duplicated when the middleware can apply it; the red
  case of T004 is the test. If none is red: no source change.
  Done when: every T004/T006 case green; Note = "no change, H14 not reproduced on ASP.NET Core 10.0.14 (OnStarting)" or the
  list of fixed paths with their red-first evidence. (FR-002, N1)
  Note: **no change, H14 not reproduced on ASP.NET Core 10.0.14 (OnStarting)**. `ErrorResponseWriter.cs` and
  `GameServerApplicationExtensions.cs` untouched.
- [x] T008 [US1] One console origin outside Development: `GmCorsValidation.Validate` (`GmCors.cs`) adds the line of
  data-model §1 when `!gameServer.IsDevelopment && AllowedOrigins.Count > 1`. Tests: new
  `tests/GameServer.Gm.Tests/GmOptionsValidatorTests.cs` (unit: Production with 2 origins → the line; Development with 2 →
  none; Production with 1 → none) and one host case in `CorsTests` (Development host with two origins starts).
  Done when: unit cases red before, green after. (FR-003, BC-5)
  Note: `GmOptionsValidatorTests.Outside_development_one_console_origin_is_allowed` red before, green after; the Development
  unit case and `CorsTests.A_development_host_accepts_two_console_origins` green before and after (no regression guard).
- [x] T009 [P] [US1] Guard test for the CSRF `Origin` rule with a sibling of the same site (`CorsTests`): signed-in
  HTTPS browser, `POST /gm/v1/staff/{id}/sessions/revoke` with `Origin: https://evil.example.com`,
  `Sec-Fetch-Site: same-site`, a valid token → 403 `CSRF_FAILED` and no `Access-Control-Allow-Origin`; same with the allowed
  origin → 200 with CORS headers. No production change. Scripted check (not committed): removing the trusted-origin
  comparison in `Csrf.cs:65-69` turns the sibling case green→red as expected.
  Done when: green; the scripted check recorded. (FR-004)
  Note: `CorsTests.A_sibling_origin_of_the_same_site_cannot_send_cookie_mutations` green. Scripted check: the Origin
  comparison of `Csrf.cs:65-69` removed → red (sibling gets 200); restored (`git checkout`).
- [x] T010 [US1] **Checkpoint US1**: `GameServer.Gm.Tests` green (Release); `OpenApiTests` green (document unchanged);
  release notes US1 section (exposed headers, max-age, one-origin rule, H14 outcome of T007).
  Done when: all hold; Note with counts. (FR-001..FR-004)
  Note: `GameServer.Gm.Tests` 214 total, 213 passed, 1 skipped, 0 failed (31.8 s); `OpenApiTests` green (document
  unchanged); release notes US1 written.

## Phase 3: US2 — The session cookies resist cookie tossing and keep `SameSite=Lax` with CORS on (P2, Q20, H15) — Phase A

- [x] T011 [US2] Options: `GmCookieOptions` and `GmConsoleOptions` classes, `GmOptions.Cookies`, `GmOptions.Console`
  (`src/GameServer.Modules.Gm.Adapters/GmOptions.cs`, contract §1); validator rules for `Gm:Cookies:SameSite` (value;
  `None` needs CORS origins) with the data-model §1 lines. Unit cases in `GmOptionsValidatorTests` (`lax`, `LAX`, `None`
  with/without origins, `Strict`, empty).
  Done when: unit cases red before, green after; no behaviour uses the option yet. (FR-010, FR-011)
  Note: `GmCookieOptions` (`SameSite`, default `"Lax"`; internal `IsNone`/`IsSupported`) and `GmConsoleOptions` (`BaseUrl`)
  added; unit cases (`Lax`/`lax`/`LAX`/`None`/`none` valid; `Strict`, `Unspecified`, empty, `" Lax"` refused; `None` without
  origins refused in Development and Production, accepted with one) — 6 red before the rules, all green after. Decision:
  the value is matched exactly without case (no trimming), so `" Lax"` is refused.
- [x] T012 [US2] `src/GameServer.Modules.Gm.Adapters/Security/GmCookies.cs` per contract §2 and plan US2: name constants and
  `SessionName/CsrfName(GameServerOptions)`; `Session(http, expiresAt)` with `Path=/`, `HttpOnly`, `SameSite` from the
  option, `Secure = !IsDevelopment || IsHttps || SameSite == None`; `Configure`: antiforgery `Cookie.Name = CsrfName`,
  `Path = "/"`, `SameSite` from the option, `SecurePolicy = (!IsDevelopment || None) ? Always : SameAsRequest`; CSRF
  trusted origins unchanged. Remove `CsrfCookieName`; doc comment states case A, `__Host-`, Development names and why
  (research R1).
  Done when: the module compiles with T013; behaviour proven by T013/T014. (FR-010..FR-014, FR-017, BC-2, BC-3)
  Note: `GmCookies` per contract §2 (four name constants, `SessionName`/`CsrfName`, `Session`, `Configure`); `CsrfCookieName`
  removed; doc comment states case A, `__Host-`, the Development names and why.
- [x] T013 [US2] Callers and pinned tests: `GmCookieAuthenticationHandler.cs` (remove `CookieName`, read
  `GmCookies.SessionName(options)`), `GmSessionService.cs:119`, `GmModule.cs:230, 256`, `Sso/GmSso.cs:164`; update
  `tests/GameServer.Gm.Tests/GmTestHost.cs` `SessionCookie` (by `GmCookies.DevelopmentSessionName`), `GmAuthTests.cs:41-42`
  (`path=/`), `CsrfAndSessionTests.cs:154-169` (`gs_gm_dev_csrf`, `path=/`), `CorsTests.cs:32-49` → renamed
  `Configured_origins_get_credentialed_cors_and_cookies_stay_lax` (CORS on → `samesite=lax`; with
  `Gm:Cookies:SameSite = None` → `samesite=none; secure` over HTTPS). Apply T003's other pins.
  Done when: Release build 0 warnings; `GameServer.Gm.Tests` green; every changed expectation was red on the old code.
  (FR-010, FR-012..FR-014, FR-017, BC-1..BC-3)
  Note: callers switched (`GmCookieAuthenticationHandler` uses its injected `GameServerOptions`; `GmSessionService`,
  `GmModule` login/logout, `GmSso` callback). Test pins: `GmTestHost.SessionCookie` (+ new `SetCookie`, `CookieAttributes`
  for exact attribute matches — `ShouldContain("path=/")` on the raw string would also match `path=/gm`), `GmAuthTests`,
  `CsrfAndSessionTests` (test renamed `Cookies_are_http_only_lax_with_the_development_names_on_the_site_root`; jar URI
  `http://localhost/`), `CorsTests` (renamed `Configured_origins_get_credentialed_cors_and_cookies_stay_lax`, second host
  with `SameSite=None`), `Observability.Tests/LoggingTests.cs:69`, `Platform.Web.Tests/RequestLogPrivacyTests.cs:85`,
  `GmBrowser.cs` doc comment. Scripted checks: path `/gm` restored → 5 Gm tests red; "always None" restored → the CorsTests
  case red; restored → green. Release build 0 warnings.
- [x] T014 [US2] New `tests/GameServer.Gm.Tests/GmCookiePolicyTests.cs`:
  (a) Development over HTTP: CSRF route → `gs_gm_dev_csrf=…; path=/; samesite=lax; httponly` (no `secure`); local login →
  `gs_gm_dev=…; path=/; samesite=lax; httponly`; logout → `gs_gm_dev` expired with the same `path`/`samesite`;
  (b) Development over HTTPS (`Browser(https: true)`): session cookie `secure`;
  (c) `Gm:Cookies:SameSite = None` + CORS origin, HTTPS: both cookies `samesite=none; secure`;
  (d) a pre-018 cookie `gs_gm=<valid protected value of a live session>` with `Path=/gm` → `/me` 401 (FR-015: build the
  value through the session service, send it under the old name);
  (e) bare `WebApplication` + `TestServer` with base address `https://localhost/` (A4): `GameServerOptions`
  `DeploymentEnvironment = Staging`, `GmCookies.Configure` + the GM options binding only, endpoints that call
  `IAntiforgery.GetAndStoreTokens`, `IsRequestValidAsync` and append/delete the session cookie with `GmCookies.Session`:
  `Set-Cookie` names `__Host-gs_gm_csrf` / `__Host-gs_gm`, each `path=/; secure; samesite=lax; httponly`, no `domain`;
  a POST with header token + cookie validates; the deletion carries `secure` and `path=/`;
  (f) over plain HTTP the same Staging bare app answers the CSRF route with a server error (documents why Development keeps
  unprefixed names; pins research R1).
  Done when: (a)–(e) red on the old code and green now; (f) green; Note records V5 as confirmed in the repository.
  (FR-012..FR-016, SC-002, FR-098)
  Note: `GmCookiePolicyTests` 6 tests green. Scripted check with the pre-018 names (`gs_gm`/`gs_gm_csrf` everywhere) and
  `Path=/gm`: (a)–(e) red (5), (f) green; restored → 6/6 green. The bare app clears configuration sources and logging
  providers (no machine/host file reaches it). **V5 confirmed in the repository.**
- [x] T015 [US2] **Checkpoint US2**: `GameServer.Gm.Tests` and `GameServer.Platform.Web.Tests` green (Release);
  release notes US2 (BC-1..BC-3, one-time sign-out, Development names and why).
  Done when: all hold; Note with counts. (FR-010..FR-017)
  Note: Release build 0 warnings; `GameServer.Gm.Tests` 232 (231 passed, 1 skipped); `GameServer.Platform.Web.Tests` 942
  (939 passed, 3 skipped); `GameServer.Observability.Tests` 69/69; release notes US2 written.

## Phase 4: US3 — SSO sign-in returns to the console, never anywhere else (P3, H6, Q16) — Phase A

- [x] T016 [US3] `GmOptionsValidator` rules for `Gm:Console:BaseUrl` (data-model §1: shape, `https` outside Development,
  origin ∈ CORS origins, required when CORS origins are set). Unit cases in `GmOptionsValidatorTests` (each line; valid
  Production config with both; **Production with OIDC and neither setting → no line** (zh-pvp bump case); Production with a
  CORS origin and no `BaseUrl` → the line; `BaseUrl` without a CORS origin → the origin line; sub-path
  `https://cdn.example.com/gm/` valid; trailing-slash missing refused; `?x`/`#x`/user info refused; origin comparison
  case-insensitive on scheme/host). Host case: Development host with a bad `BaseUrl` fails startup with the line.
  Done when: the new failing-rule cases red before, green after; the no-console case green before and after. (FR-020,
  FR-021, BC-4)
  Note: 18 new failing-rule cases red before the rules (incl. the host startup case), green after; the no-console cases
  (`A_deployment_without_console_settings_starts`, Production with OIDC and neither setting) green before and after. Decisions:
  (1) an **empty** `BaseUrl` counts as unset (a configuration layer clearing it), so with a CORS origin it reports the "not
  set" line, without one nothing; (2) the shape rule also refuses characters outside printable ASCII (the value is written into
  `Location` headers; same rule as FR-022 for paths); (3) origins are compared through `Uri.GetLeftPart(Authority)`, so case of
  scheme/host and a default port do not matter. `CorsTests` hosts with an origin now use `GmTestHost.WithConsole`;
  `Bad_origins_fail_startup` now asserts the origin's own line (`Gm:Cors:AllowedOrigins '<origin>'`) because the new "BaseUrl
  not set" line also names `Gm:Cors:AllowedOrigins` and would have kept the old assertion green without the origin rule.
- [x] T017 [US3] New `src/GameServer.Modules.Gm.Adapters/Sso/GmConsoleUrls.cs` (contract §4, data-model §4, research R5),
  registered by `GmSso.Add`; `GmSso.cs`: start stores `urls.AcceptReturnPath(returnUrl)` (relative) as `RedirectUri`;
  `OnTicketReceivedAsync` success → `urls.SuccessTarget(context.ReturnUri)`; every denial in `OnTicketReceivedAsync`,
  `OnRemoteFailureAsync` and the unhandled callback → `urls.DeniedTarget()`; remove `DefaultReturnUrl`, `DeniedRedirect`,
  `SafeReturnUrl`. New unit `tests/GameServer.Gm.Tests/GmConsoleUrlsTests.cs`: accepted `/`, `/#/players/abc`,
  `/#/x?y=1`; refused `https://evil.example/x`, `//evil.example`, `/\evil`, `/a/../b`, `/é`, `/` + 512 chars, empty,
  `javascript:alert(1)`, a string with a space; targets with `BaseUrl` (root and sub-path) and without (legacy `/gm/` rule;
  defaults `/gm/v1/ui` and `/gm/v1/ui?error=sso_denied` with Development options, `/gm/v1/me` and
  `/gm/v1/me?error=sso_denied` with Staging/Production options).
  Done when: unit matrix green; build 0 warnings. (FR-022..FR-026, BC-7)
  Note: `GmConsoleUrlsTests` 34 cases green; build 0 warnings. **Deviation from plan US3 (`RedirectUri = ... ?? ""`)**: the
  start endpoint stores `"/"` for a missing/refused value, because the OIDC handler replaces an empty `RedirectUri` with the
  start URL itself (`/gm/v1/auth/sso/start?returnUrl=<refused value>`), which then passes the path rule with a console and
  lands on `BaseUrl` + that path. `"/"` lands on `BaseUrl` with a console and is refused by the `/gm/` rule without one (→
  default). Scripted check: `?? ""` → 3 `SsoTests` red. `SuccessTarget` re-checks the value (defence in depth).
- [x] T018 [US3] `tests/GameServer.Gm.Tests/SsoTests.cs`: keep the existing host (no `BaseUrl`, legacy) and update its pins
  (`:136-138` → `/gm/v1/ui`; `/gm/v1/me` success kept); add a host with `Gm:Console:BaseUrl = https://gm.example.com/` and
  `Gm:Cors:AllowedOrigins:0 = https://gm.example.com`: `returnUrl=/#/players/abc` → `https://gm.example.com/#/players/abc`;
  the hostile values of SC-003 → `https://gm.example.com/`; no MFA, disabled staff, unknown identity, forged state →
  `https://gm.example.com/#/login?error=sso_denied`; the session cookie is set on the success answer
  (`gs_gm_dev`, Development); `_idp.RejectedTokenRequests` stays 0.
  Done when: green; the new cases red on the old code. (FR-022..FR-025, SC-003, US3-1..US3-3, US3-6)
  Note: `SsoTests` 12 green: legacy pins now `/gm/v1/ui` (default, also for a console path and a missing `returnUrl`) and
  `/gm/v1/ui?error=sso_denied`; success to `/gm/v1/me` kept; new: return to `https://gm.example.com/#/players/abc` with the
  `gs_gm_dev` cookie on the callback answer, missing `returnUrl` → root, 8 hostile values → root, 4 denials (no MFA, disabled,
  unknown identity, forged state) → `#/login?error=sso_denied` without cookie, unhandled callback → denial target in both
  configurations; `RejectedTokenRequests` 0. Scripted check with the pre-018 `GmSso` (cookie-name line adapted): 9/12 red.
- [x] T019 [US3] Sample host configuration (D12, research R16): `samples/SampleHost/appsettings.Local.json` and
  `appsettings.Dev.json` gain `Gm:Cors:AllowedOrigins = ["http://localhost:5173"]` and
  `Gm:Console:BaseUrl = "http://localhost:5173/"`; `appsettings.Beta.json`, `appsettings.Uat.json`,
  `appsettings.Live.json` stay without console values (D12). `tests/Shared/StagingSecrets.cs` and the other
  Staging/Production host starts found by T003 need no change (no console = valid). Re-run every
  `LocalFlavorFactory<SampleHost …>` test class listed by T003 and `GameServer.Platform.Web.Tests`.
  Done when: those suites green; no Staging/Production start gains a violation; Note lists the files changed. (FR-021,
  FR-045, BC-4)
  Note: changed `samples/SampleHost/appsettings.Local.json` and `appsettings.Dev.json` (both `DeploymentEnvironment`
  Development, so `http` is valid); Beta/Uat/Live untouched. Re-run green: Architecture.Tests 69 (61 passed, 8 opt-in
  `RunLocalScriptTests` skipped), Client.Tests 234 (232, 2 skipped), Platform.Web.Tests 942 (939, 3 skipped),
  Sample.Counter.Tests 20/20, SampleGame.Tests 282 (276, 6 skipped). No Staging/Production start sets `Gm:Cors` (T003), so
  none gains a violation; the Couchbase starts are proven by T022.
- [x] T020 [US3] **Checkpoint US3**: `GameServer.Gm.Tests`, `SampleGame.Tests`, `GameServer.Platform.Web.Tests` green
  (Release); release notes US3 (BC-4, BC-7, zh-pvp steps).
  Done when: all hold; Note with counts. (FR-020..FR-026)
  Note: `GameServer.Gm.Tests` 293 (292 passed, 1 skipped); `SampleGame.Tests` 282 (276, 6 skipped);
  `GameServer.Platform.Web.Tests` 942 (939, 3 skipped); release notes US3 written.

## Phase 5: US4 — The static GM page exists only where development tools exist (P4, H9, Q17) + Phase A gate

- [x] T021 [US4] `GmRoutes.Ui` gains `condition: RouteConditions.DevelopmentTools` (contract §5); `GmModule.MapGmApi` maps it
  inside the existing `AllowsDevelopmentTools` block. Update `tests/SampleGame.Tests/CatalogCompositionTests.cs` (route list
  per flavor: `gm.ui` only with development tools) and keep `GmAuthTests.Ui_page_is_served_with_a_strict_policy`.
  Done when: green; the composition change red on the old code. (FR-030, SC-004, BC-6, BC-8)
  Note: `CatalogCompositionTests` 24 green (new assertion `gm.ui` mapped ⇔ development tools && GM API); with the old
  declaration and unconditional mapping restored → 6 red (Beta/Uat/Live × player on/off with GM on); restored → green.
  `GmAuthTests.Ui_page_is_served_with_a_strict_policy` green; `FrameworkRouteNames` (Local composition) keeps `gm.ui`.
- [x] T022 [US2] [US3] [US4] Couchbase flavor matrix `tests/GameServer.Platform.Couchbase.IntegrationTests/SampleHostFlavorTests.cs`:
  Dev: `/gm/v1/ui` 200, CSRF route sets `gs_gm_dev_csrf; path=/`; Beta (sample config, **no console settings**, SSO from
  the staging settings; client with HTTPS base address): starts, `/gm/v1/ui` 404, CSRF route sets
  `__Host-gs_gm_csrf; path=/; secure; samesite=lax; httponly`, no CORS headers for any origin; Uat (test settings add
  `Gm:Cors:AllowedOrigins:0 = https://gm.uat.example.com`, `Gm:Console:BaseUrl = https://gm.uat.example.com/`): a preflight
  from that origin answers CORS with max-age 7200 and one from another origin none; Live: the process test still reports
  exactly the `couchbases://` violation (no console settings needed).
  Done when: green against local Couchbase (`COUCHBASE_*` set, PowerShell). (FR-012, FR-021, FR-030, SC-002, SC-004)
  Note: `SampleHostFlavorTests` 5/5 green against local Couchbase (1 m 29 s, not skipped). Dev: `/gm/v1/ui` 200,
  `gs_gm_dev_csrf` = `path=/; samesite=lax; httponly`; Beta and Uat: `/gm/v1/ui` 404, `__Host-gs_gm_csrf` = `path=/; secure;
  samesite=lax; httponly` over an HTTPS client (`WebApplicationFactoryClientOptions`, `HandleCookies = false`), preflight
  (`OPTIONS` logout) gets CORS + `Max-Age: 7200` only on Uat from `https://gm.uat.example.com` (test settings), none from
  other origins or on Beta; Live unchanged (one `couchbases://` violation, comment added). No Couchbase revert run: the
  same `gm.ui` and cookie changes were shown red in memory (T021, T014).
- [x] T023 **Phase A gate**: Release build 0 warnings; full suite in memory (no `COUCHBASE_*`) and with
  `GAMESERVER_TEST_STORE=File`; `GameServer.Platform.Couchbase.IntegrationTests` project; `git diff main --
  docs/api/gm-openapi.json` empty (SHA of T001 (b)); `git diff main -- proto src/*/Protos sdk` empty; release notes Phase A
  complete; `git status` lists only intended files.
  Done when: all hold; counts recorded against T001. (FR-099 partial, SC-009)
  Note (test infrastructure, from the gate's full Couchbase run: 1 failure of 2971): `GameServer.Platform.Web.Tests.ConfigPhasePerformanceTests.The_phase_on_the_sample_config_stays_within_the_budget`
  (slice 015, unrelated to 018; likely the unexplained Platform.Web failure of 017) is timing noise, not only parallel
  load: median over 13 runs of the test alone 12.0–129.5 ms (one failure at 129.5 > 100), over 3 runs of its project
  3.0–33.3 ms. A non-parallel collection would not help, so it is now opt-in (`GAMESERVER_PERF=1`, like
  `GmCatalogPerformanceTests` and the 1 MiB case of the same class); budget unchanged (median ≤ 5 × 20 ms). With the
  variable set it runs (median 1.8 ms); without it it skips with the opt-in message.
  **Gate result (orchestrator, 2026-11-06):** Release build 0 warnings; full suite incl. Couchbase 3011 total, 2984 passed, 27 skipped, 0 failed (first run 1 failure = `ConfigPhasePerformanceTests`, made opt-in above). T024 review r1 CONDITIONAL (1 Major + 4 minor, fixed), r2 **YES**; carry-over to Phase B review: every hash-route parameter goes through `encodeURIComponent` before it enters an API path, never navigate to a URL taken from the hash.
- [x] T024 **Phase A security review** (after T023): `tech-lead-reviewer`, read-only, gets spec, plan, this file and
  `git diff --stat main`; focus: cookie names/attributes per environment and every write/delete site, antiforgery policy per
  environment, `SameSite` decoupling, CORS exposure and origin rules, `GmConsoleUrls` open-redirect rules (both
  configurations, every denial path), validator lines, `/gm/v1/ui` mapping, no secret or cookie value logged, tests that would
  catch a regression. Fix every accepted finding with a test that fails without the fix (scripted revert check), rerun the
  affected projects and T023's in-memory suite.
  Done when: findings list (Blocker/Major/Minor) recorded; accepted ones fixed; suites green. **Phase B may start.**
  (constitution governance)
  Note — **Review (T024) r1** (verdict CONDITIONAL; fixes by the implementer, task left for the orchestrator to close). Each
  fix has a test shown red with the fix disabled (scripted edit, rebuild, run, byte-for-byte restore):
  - **M1** `GmConsoleUrls`: `%2e` (any case) refused like `..`; with a console only `/` or `/#<fragment>` accepted (no path,
    no query); without a console the `/gm/` rule stays. Tests: `GmConsoleUrlsTests` hostile list (+ `/%2e%2e/x`, `/.%2e/x`,
    `/%2E./x`, `/gm/%2E%2E/x`, `/gm/v1/.%2e/x`, `/?x=1`), `A_console_accepts_only_its_root_and_hash_routes`,
    `Without_a_console_a_gm_path_keeps_its_query`, `SsoTests.A_sub_path_console_is_never_left_through_the_return_url` (real
    start + callback, `BaseUrl` `https://gm.example.com/console/`), `SsoTests.A_forged_state_or_an_external_return_url_gets_nowhere`
    (+ `/gm/%2e%2e/x`, `/gm/.%2E/x`). Red checks: `%2e` rule off → 3 `GmConsoleUrlsTests` + the legacy SSO test red (with a
    console the shape rule alone already refuses those paths); console shape rule off → 6 `GmConsoleUrlsTests` + the
    sub-path SSO test red. Spec FR-022 + US3 scenario 2 + independent test, data-model §4, contract §4 narrowed.
  - **m1** `GmCorsValidation.Validate`: an origin must equal `uri.GetLeftPart(Authority)` (case-insensitive) and have no
    user info; line text now names default port and user info. Tests: `GmOptionsValidatorTests.An_origin_not_in_its_serialized_form_fails`
    (8 cases), `An_origin_in_its_serialized_form_is_valid` (4), `CorsTests.Bad_origins_fail_startup` (+ `:443`). Red check: old
    condition → 4 unit cases (`:443`, `:80`, two user-info forms) + the `:443` host case red. Data-model §1 updated.
  - **m2** `GmOptionsValidator`: `SameSite=None` refused where `!AllowsDevelopmentTools`, one line
    (`Gm:Cookies:SameSite=None is refused in '<env>': a console on another site (case B) is not supported; use Lax (default)
    with a console on the same site.`). Tests: `Samesite_none_is_refused_outside_development_and_test` (Staging, Production),
    `Samesite_none_needs_a_cors_origin` now Development + Test. Red check: rule off → both cases red. Spec FR-011, D3, BC-3,
    owner question 3; plan D3 + owner question 3; data-model §1; contract §1; PCR `docs/pcr/gm.md` (cookie and CORS
    bullets only — the rest of the PCR stays T057); release notes.
  - **m3**: `GmCookiePolicyTests.Outside_development_the_session_cookie_is_read_ended_and_deleted_by_its_host_name` (real
    `GmTestHost` with `DeploymentEnvironment = Test`: development tools, `__Host-` names, HTTPS: sign-in, `/me`, sign-in again
    ends the old session, logout deletion attributes) and `The_names_follow_the_deployment_environment` (Development, Test,
    Staging, Production). Red checks: the Development name hard-coded in the handler, in `GmSessionService.EndCurrentAsync`
    or in the logout delete → the test red each time. Chosen over the stubbed bare app: same coverage through the real
    pipeline, no stub of sealed services.
  - **m4**: `GmConsoleUrls` class comment states the real rule.
  - Suites after the fixes: Release build 0 warnings; Gm.Tests 333 (332, 1 skipped); Platform.Web.Tests 942 (938, 4 skipped);
    Architecture.Tests 69 (61, 8 skipped); SampleGame.Tests 282 (276, 6 skipped); Client.Tests 234 (232, 2 skipped);
    Sample.Counter.Tests 20/20; Observability.Tests 69/69; Fixture.Tests 40/40; `SampleHostFlavorTests` (Couchbase) 5/5;
    `docs/api/gm-openapi.json` SHA unchanged.

## Phase 6: US5 — A developer runs the console locally against run-local, cross-origin, in two commands (P5, Q16, Q18) — Phase B

Starts only after T024.

- [ ] T025 **[VERIFY] V8 — decision gate for dependency versions**: re-run the registry query of research R7 (`npm view
  <pkg> time --json`, `engines`, `peerDependencies`) for every package of contract §8 and plan Technical Context plus
  `@types/react`, `@types/react-dom`, `postcss`, `postcss-preset-mantine`, `globals`; pick per package the newest stable
  version published ≥ 14 days before today whose `engines` accepts Node 22.20.0 and whose peers are satisfied by the other
  picks (TypeScript 5.9.x, React Router 7.x, jsdom ≤ 28.x unless a newer one accepts 22.20).
  Done when: a Note records the table (package, version, publish date, engines, peer check) — the only source of versions for
  T026. (FR-043, FR-098)
- [ ] T026 [US5] Workspace skeleton `gm-console/` (research R8, contract §6–§8): root `package.json` (private, `version`
  = `GameServerFrameworkVersion` 0.7.0, workspaces, scripts of contract §8 with placeholders for later tasks, `engines.node
  >=22.20.0`, devDependencies: TypeScript, ESLint stack, Vitest, jsdom, Testing Library, openapi-typescript,
  playwright-core), `.npmrc` (`engine-strict=true`, `save-exact=true`), `.nvmrc` (`22.20.0`), `tsconfig.base.json` (strict,
  `noUncheckedIndexedAccess`, `moduleResolution: "bundler"`, `jsx: "react-jsx"`), `eslint.config.js`,
  `vitest.config.ts`; `packages/shell` (`@gameserver/gm-shell`, `exports: { ".": "./src/index.ts" }`, exact
  `peerDependencies`, `src/index.ts` with `createGmConsole` rendering `MantineProvider` + an empty hash router);
  `app` (`@gameserver/gm-console-default`, exact `dependencies` = the shell's peers + `@gameserver/gm-shell`, `index.html`,
  `src/main.tsx` = `createGmConsole()`, `vite.config.ts` with `server: { host: 'localhost', port: 5173, strictPort: true }`
  and PostCSS for Mantine); `.gitignore` entries (`gm-console/**/node_modules/`, `gm-console/**/dist/`,
  `gm-console/e2e/.artifacts/`); `.editorconfig` section (2 spaces for `gm-console/**/*.{ts,tsx,js,mjs,cjs,css,html,json}`).
  Run `npm install` once to produce `package-lock.json`, then from a clean folder `npm ci`.
  Done when: `npm ci`, `npm run typecheck`, `npm run lint`, `npm run test` (0 tests allowed with `passWithNoTests`) and
  `npm run build` succeed; `git status` shows no `node_modules`/`dist`. (FR-040..FR-043, FR-045)
- [ ] T027 [P] [US5] .NET architecture tests (A9) `tests/GameServer.Architecture.Tests/GmConsoleWorkspaceTests.cs` and
  `VersionTests.cs`: (a) the versions of `gm-console/package.json`, `packages/shell/package.json`, `app/package.json` equal
  `RepositoryTooling.DeclaredFrameworkVersion()` (message names both and the fix "bump with Directory.Build.props and the
  Unity package"); (b) every value in `dependencies`, `devDependencies`, `peerDependencies` of those files is an exact
  semver (`^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$`) or the workspace's own package; (c) `package-lock.json` exists with
  `lockfileVersion` 3; (d) `git ls-files gm-console` contains no `node_modules/` or `dist/` path (skip with a message
  outside a git checkout, like `VersionTests`); (e) no `*.csproj`, `*.props`, `*.targets` mentions `npm` or `node ` (FR-046);
  (f) the shell's `src/` has no import from `app/` or `../..` outside the package (text scan of `import … from`).
  Done when: green; each assertion shown red once with a temporary edit (scripted, reverted). (FR-041..FR-043, FR-046,
  FR-047)
- [ ] T028 [US5] Codegen (research R9, V12): `gen:api` and `check:api` scripts; generate
  `packages/shell/src/generated/gm-api.d.ts` from `../docs/api/gm-openapi.json`; run `gen:api` twice and diff (V12);
  scripted staleness check: copy the document to the scratch folder, change one `summary`, run the generator on the copy
  into the committed path, `check:api`'s diff step exits non-zero naming the file; restore and re-run green.
  Done when: generated file committed-ready (LF, no timestamp); `npm run check:api` green; Note records V12 and the red run.
  (FR-044, SC-005, FR-098)
- [ ] T029 [US5] Lint rules (FR-048) in `eslint.config.js`: `no-restricted-syntax` for `JSXAttribute[name.name=
  'dangerouslySetInnerHTML']`, `no-restricted-globals` / `no-restricted-properties` for `localStorage`, `sessionStorage`,
  `indexedDB` (also as `window.*`), `no-eval`, `no-implied-eval`, `no-new-func`, React hooks rules, typescript-eslint
  recommended-type-checked; `verify` script chains contract §8 (with `check:dist` added by T052). Scripted check: a temporary
  file under `packages/shell/src/` using each forbidden item makes `npm run lint` fail once per item; delete it.
  Done when: `npm run lint` green on the repository; the Note lists each rule's red run. (FR-048)
- [ ] T030 [US5] Dev loop: `app/public/config.json` (data-model §5 local values); `tools/local/run-local.ps1` and
  `run-local.sh` print one extra banner line after "GM UI": `  GM console   http://localhost:5173  (cd gm-console; npm ci; npm run dev)`;
  update `tests/GameServer.Architecture.Tests/RunLocalScriptTests.cs:98` area to expect it. Manual check: start run-local
  (`-Port 5080`), `npm run dev`, open `http://localhost:5173` in the system Chrome: the skeleton loads `config.json` (no API
  call yet).
  Done when: `RunLocalScriptTests` green; the manual check recorded. (FR-045, D19)
- [ ] T031 [US5] **Checkpoint US5**: `npm ci && npm run verify` (without `check:dist` until T052) green; architecture tests
  green; release notes US5 (workspace, versions table of T025, codegen, dev loop).
  Done when: all hold. (FR-040..FR-048, SC-005)

## Phase 7: US6 — Staff sign in, see which game and environment they are on, and sign out (P6, Q19) — Phase B

Shared test helpers first used here: `packages/shell/src/test/fakeFetch.ts` (route table `METHOD path` → status, headers,
JSON body; records requests incl. headers and `credentials`) and `test/render.tsx` (Mantine + Query + router providers).

- [ ] T032 [P] [US6] `packages/shell/src/config/loadConfig.ts` (+ `loadConfig.test.ts`): `GET /config.json` with
  `cache: 'no-store'`; validation of data-model §5 (FR-050) returning `{ ok: true, config }` or `{ ok: false, field,
  message }`; trailing `/` of `apiBaseUrl` removed. Tests: valid local and Beta examples, missing file (404), not JSON,
  each invalid member, unknown members ignored, no other request made.
  Done when: green. (FR-050, US6-1)
- [ ] T033 [P] [US6] `packages/shell/src/api/problem.ts` and `api/client.ts` (+ tests) per contract §6 and plan US6:
  `createGmClient` over `openapi-fetch` with `credentials: 'include'`, base `apiBaseUrl`; middleware: GET untouched (assert
  no added header), non-GET + `X-CSRF-Token` (+ `Content-Type` with a body), caller headers (`X-Request-Id`) passed through;
  response → `GmProblem` (Problem Details members, `errors`, `requestId` from body else `X-Request-Id` header); fetch
  rejection → `GmProblem{ status: 0 }`; 401 → `onUnauthorized` once; 403 non-CSRF → `onForbidden`; 403 `CSRF_FAILED` on a
  non-GET → `csrf.refresh()` then one retry with the same body and headers, second failure returned.
  Done when: tests cover each rule (incl. "GET sends no custom header", "retry happens once", body replay). (FR-051,
  FR-052, FR-057, FR-058, FR-062, US6-7, US6-8)
- [ ] T034 [US6] `packages/shell/src/session/` (`csrf.ts`, `versions.ts`, `phases.ts`, `SessionProvider.tsx` + tests):
  phases of data-model §7; start = config → `auth/methods` → `/me` (401 → `signed-out`); guard (FR-055) → `blocked`; skew
  (FR-056: `compareFrameworkVersions(builtFor, me)` → `same | minor-differs | unparsable`, game version info); sign-in
  local (CSRF → POST login → CSRF → me from the answer); sign-out (POST logout with token; 401 treated as done; clear
  state and query cache; new anonymous CSRF); 401 handling (keep the current hash as `returnTo` in memory, at most one
  `/me` per navigation); 403 → one `/me` refresh. `__GM_BUILT_FOR__` declared in `src/env.d.ts` with a test default.
  Done when: tests: call order of sign-in (exact list and only POST carries the token), guard blocks with zero further calls,
  `0.8.1`/`0.7.0` warns, `0.7.3`/`0.7.0` does not, `0.7.0+abc` parses, game-version info, logout on expired session, no loop
  after repeated 401. (FR-052, FR-054..FR-059, US6-4..US6-9)
- [ ] T035 [US6] `pages/LoginPage.tsx` (+ test): auth-methods variants (US6-2), local form (staff id, password;
  invalid-credentials message without detail), SSO button → `window.location.assign(<api>/gm/v1/auth/sso/start?returnUrl=
  <encodeURIComponent('/' + returnHash or '#/')>)` (injected `navigate` for the test), `error=sso_denied` message read from
  the hash query; no password kept after submit.
  Done when: tests for the four variants, the SSO URL (including an encoded hash), the denial message, a 401 sign-in.
  (FR-053, FR-054, US6-2, US6-3)
- [ ] T036 [US6] `components/Layout.tsx`, `EnvBadge.tsx` (red for `live`), `SkewBanner.tsx`, `BlockScreen.tsx` (expected vs
  actual game/env, only sign-out), footer (built-for and server versions, game version info) (+ tests).
  Done when: tests: badge colour for `live` vs `beta`, banner only on `minor-differs`/`unparsable`, block screen content and
  that it renders no navigation. (FR-055, FR-056, FR-060, US6-5, US6-6)
- [ ] T037 [P] [US6] `components/ErrorDisplay.tsx` and `JsonView.tsx` (+ tests): Problem Details view (title, code,
  codeNumber, detail, `errors` list by field, request id), network failure text with the API origin; `JsonView` renders
  `JSON.stringify(value, null, 2)` in a `<pre>` as text. Test: a value containing `<img src=x onerror=alert(1)>` appears as
  text and creates no `img` element.
  Done when: green. (FR-062, FR-076, US7-6)
- [ ] T038 [US6] **Checkpoint US6**: `npm run verify` green; release notes US6 (session behaviour, guard, skew, CSRF retry,
  D15).
  Done when: all hold. (FR-050..FR-062)

## Phase 8: US7 — Staff browse the framework's read pages from a navigation built from their permissions and the catalog (P7) — Phase B

- [ ] T039 [P] [US7] `packages/shell/src/catalog/parseCatalog.ts` (+ test): `GmCatalog` of data-model §8 from an OpenAPI
  object; defaults for missing `x-gm-*`; operations without `operationId` skipped. Test input = the committed
  `docs/api/gm-openapi.json` read from disk: every operation of the document is in the catalog, keyed by route name
  (count compared with the document's own operation count, not hard-coded), `gm.player_receipts` paged query `player.read`, `economy.adjust` command `player` target; a minimal
  document without `x-gm-*` parses with defaults.
  Done when: green. (FR-065)
- [ ] T040 [US7] `navigation/registry.ts`, `navigation/builtins.ts`, `navigation/Menu.tsx` (+ tests): internal registry with
  `addPage`/`addPlayerTab` (design §7.3 shapes, FR-067), built-in definitions of data-model §9, `isVisible(def, me,
  catalog)`; menu grouped and ordered; player-search modes visible per their route. Not exported from `src/index.ts`
  (assert in a test that the public module exports only contract §6 names).
  Done when: tests: `player.read`-only staff sees Players only; without `economy.ledger` in the catalog the Ledger tab is
  absent; ops cards individually; the export list. (FR-066, FR-067, US7-1)
- [ ] T041 [US7] `router.tsx`, `App.tsx`, `pages/NotAvailable.tsx` (+ tests): hash routes of plan US7, session-phase gating
  (config error, signed-out → login, blocked → block screen, ready → layout), a hidden page by URL renders "not available"
  with **zero** API calls, `returnTo` restored after sign-in, unknown hash → home (`#/players` when visible, else the first
  visible page, else an empty-permissions message); catalog loaded once (`staleTime: Infinity`, cleared on 401/sign-out)
  with an error screen + retry.
  Done when: tests for each gate, the zero-call rule, `returnTo`, a single catalog request across three navigations, the
  catalog error screen. (FR-057, FR-065, FR-066, SC-008)
- [ ] T042 [P] [US7] `components/PagedList.tsx` (+ test): TanStack `useInfiniteQuery`, `pageSize=50`, "Load more" with
  `cursor = nextCursor`, button hidden when null, a failed later page keeps loaded rows and shows `ErrorDisplay`.
  Done when: test with a fake two-page list and a failing second page. (FR-073, US7-4)
- [ ] T043 [P] [US7] `pages/PlayersPage.tsx` (+ test): three modes (player id → `gm.player`; mapping key → `gm.player_by_mapping`
  with the key URL-encoded in the path; display name → `gm.player_search`, paged), each mode shown per FR-066; a hit
  navigates to `#/players/{playerId}`; inputs kept in component state only.
  Done when: tests: each mode calls the right path; after a mapping-key or display-name search `location.hash` contains
  neither value; 404 → "player not found". (FR-061, FR-068, US7-2)
- [ ] T044 [P] [US7] `pages/PlayerPage.tsx` (+ test): aggregates table (data-model fields), deletion state, `JsonView` per
  state; tabs Receipts (`PagedList` of `gm.player_receipts`, each row links `#/receipts/{receiptId}`), Audit
  (`gm.player_audit`), Ledger (`economy.ledger`, FR-066); tabs independent (A10).
  Done when: tests: the three tabs with fake pages; Ledger absent without the route; a failing Audit tab leaves Receipts
  working; 404 page. (FR-069, US7-3)
- [ ] T045 [P] [US7] `pages/ReceiptPage.tsx` (+ test): `gm.receipt` → `JsonView`; 404 → "receipt not found".
  Done when: green. (FR-070)
- [ ] T046 [P] [US7] `pages/StaffListPage.tsx`, `pages/StaffPage.tsx` (+ tests): `PagedList` of `gm.staff_list` (staff id,
  display name, status, roles/permissions summary from `StaffView`), detail from `gm.staff`, audit tab `gm.staff_audit`
  (paged, `JsonView` rows).
  Done when: tests with fake pages; no control for create/grant/disable/enable/revoke. (FR-071, FR-074, US7-5, US7-7)
- [ ] T047 [P] [US7] `pages/OpsPage.tsx` (+ test): cards health (`gm.health`: status, entries table), version
  (`gm.ops_version`), gate (`gm.ops_gate`), each under FR-066 and independent (A10); no maintenance or min-version control.
  Done when: tests: each card alone, a failing card does not hide the others. (FR-072, FR-074, US7-5, US7-7)
- [ ] T048 [US7] Default app wiring: `app/vite.config.ts` `define` of `__GM_BUILT_FOR__` (shell `package.json` version;
  `GM_CONSOLE_GAME_VERSION` when set), `app/src/main.tsx`; shell `createGmConsole(options?)` mounts the providers, router and
  pages; a test of `createGmConsole` mounting into a given element; a source scan test that no page module calls
  `api.POST`/`PUT`/`DELETE` except `session/` (FR-074 boundary pinned).
  Done when: `npm run build` produces `app/dist`; the boundary test is red with a temporary `api.POST` in a page (scripted,
  reverted). (FR-042, FR-056, FR-074, FR-075, US7-7)
- [ ] T049 [US7] **Checkpoint US7**: `npm run verify` green; manual run against run-local in the system Chrome
  (`npm run dev`): sign in as `admin`, open Players (search a display name of a player created with the Unity sample client
  or skip if none), Staff → `admin`, Ops; Note with what was seen. Release notes US7 (pages, D10 boundary, D11).
  Done when: all hold. (FR-065..FR-076)

## Phase 9: US8 — Ops deploy the default app on a static host with strict security headers (P8, §6.6, Q18) — Phase B

- [ ] T050 **[VERIFY] V3 — decision gate for `style-src`** (research R10): build `app` with the pages of Phase 8; serve
  `app/dist` from a scratch Node server on `http://localhost:5173` (the configured console origin; dev server stopped) with
  the strict CSP of data-model §10
  (`connect-src 'self' http://localhost:5080`); open it with `playwright-core` + system Chrome against the running run-local
  host, render the login page, sign in and open Staff, a staff detail (tabs), Ops (cards, table) and an error (alert);
  collect `securitypolicyviolation` events and console errors.
  Done when: Note records the violations (directive, source) and the decision: `style-src 'self'` kept (0 violations, maybe
  after the R10 option (1) change in `createGmConsole`) or fallback `'self' 'unsafe-inline'`; the decision feeds T051.
  (FR-081, FR-098)
- [ ] T051 [US8] `gm-console/deploy/` (FR-080, data-model §10): `security-headers.json` (with the V3 decision),
  `nginx.conf.example` (`server` block, `location = /index.html`, `location = /config.json`, `location /assets/`, `add_header
  … always;` lines from the JSON, `{{apiOrigin}}` shown as `https://gm-api.beta.example.com` with a comment), `headers.md`
  (table: path class, header, value; CDN notes: response headers not `<meta>`, custom domain + TLS, no PSL default domain),
  `config.example.json` (data-model §5 Beta), `config.schema.json` (JSON Schema 2020-12); `deploy/deploy.test.ts`: nginx
  and table equal the JSON (parsed), the example satisfies the schema rules and the shell's `loadConfig` validator, the CSP
  string has exactly the directives of FR-081.
  Done when: green; a deliberate edit of one header in the nginx example makes the test red (scripted, reverted). (FR-080,
  FR-081, FR-082)
- [ ] T052 [US8] Preview with headers and bundle check: `app/vite.config.ts` `preview` = `localhost:5173`, `strictPort`,
  headers from `deploy/security-headers.json` (`apiOrigin` from `GM_E2E_API`, default `http://localhost:5080`) per path class
  (a small Vite plugin middleware for `preview` if `preview.headers` cannot vary per path); V11: curl `/`, `/index.html`,
  `/config.json`, one `/assets/*` file and record the headers. `scripts/check-dist.mjs` (FR-083, SC-007): inline `<script>`
  in `index.html`, non-relative `src`/`href`, any file containing `localhost:5080`, any `config.json` in `dist/`, gzip size
  of the JS+CSS referenced by `index.html` ≤ 500 KB (prints the size); add to `verify`.
  Done when: `npm run build && npm run check:dist` green with the size recorded; each rule shown red once with a temporary
  file in `dist/` (scripted); V11 Note (or the fallback server in `e2e/` and the reason). (FR-082..FR-084, SC-007, FR-098)
- [ ] T053 [US8] **Checkpoint US8**: `npm run verify` green (incl. `check:dist`); release notes US8 (deploy files, V3
  decision, bundle size).
  Done when: all hold. (FR-080..FR-085)

## Phase 10: End-to-end smoke (US5–US8) — Phase B

Precondition for T055/T056: the run-local host is running on 5080 (`tools/local/run-local.ps1`, Local flavor with the T019
values, File store; no `-Reset`, the owner's local data stays) and nothing else listens on 5173.

- [ ] T054 [US5] [US6] `gm-console/e2e/` harness (research R11, data-model §11, A7): `vitest.e2e.config.ts` (node
  environment, sequential, timeouts 60 s per test, 120 s first load), `harness.ts`: API precheck
  (`GET {GM_E2E_API}/gm/v1/auth/methods` → else fail "start tools/local/run-local.ps1 (Local flavor) first"), Vite server
  per target started programmatically (`createServer` for `dev`, `preview` for `preview` after `npm run build`) on
  5173 with `strictPort`, `chromium.launch({ executablePath: GM_E2E_BROWSER, headless: true })` with the Chrome default and
  a clear error when the file is missing (never a download), an init script recording `securitypolicyviolation` events,
  console error collection, request log (`page.on('request')` → method, url, headers), a CDP session for V13; `e2e/README.md`
  (how to run, variables, Edge path). Root script `e2e`.
  Done when: `npm run e2e` with a trivial test (open the console, login page visible) passes on both targets; with the host
  stopped it fails with the hint. (FR-087)
- [ ] T055 [US5] [US6] [US7] [US8] `gm-console/e2e/smoke.e2e.ts` (FR-087), per target:
  1. open `http://localhost:5173/` → login page with the local form (`local: true`) and no SSO button (`sso: false`);
  2. sign in `admin` / `dev-only-change-me` → header shows `local` and `sample` and the staff name; the request log shows the
     POST login with `origin: http://localhost:5173`, `sec-fetch-site: same-site`, an `x-csrf-token` header, and exactly the
     sign-in/sign-out POSTs preflighted (no `OPTIONS` for GETs, SC-006);
  3. `context.cookies('http://localhost:5080')` has `gs_gm_dev` (`httpOnly`, `sameSite: Lax`, `path: /`) (V4 re-confirm);
  4. Staff → `admin` row → detail page shows `admin`; Ops → version card shows the framework version;
  5. in the page, `fetch(api + '/gm/v1/players/does-not-exist', { credentials: 'include' })` → status 404 and a readable
     `X-Request-Id` header (FR-001 in a browser);
  6. sign out → login page; `fetch(api + '/gm/v1/me', { credentials: 'include' })` → 401;
  7. second browser context with `config.json` routed to `environmentId: "beta"` → sign in → block screen naming `beta` and
     `local`, no catalog request (Q19);
  8. preview target only: zero CSP violations and zero console errors over steps 1–7; dev target: zero console errors other
     than Vite's.
  Also record the time from the login answer to the first page render (SC-008) and the number of catalog requests (1).
  Done when: both targets green on Chrome; Note with timings, request counts and V4. (FR-087, FR-082, SC-006, SC-008,
  US5-4, US6-2, US6-4, US6-5, US6-9, US7-1, US7-5, US8-2)
- [ ] T056 [US7] V13 and Edge: in the smoke (preview target), after step 4 reload the page and sign-in state persists
  (cookie); through the CDP session record whether the catalog request is revalidated with `If-None-Match` and answered 304
  (`Network.responseReceivedExtraInfo` status); if not, Note the fallback (once per session, FR-065 unchanged). Run the whole
  smoke once with `GM_E2E_BROWSER = C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe`. Release notes e2e
  section.
  Done when: Notes for V13 and the Edge run (green). (FR-065, FR-087, SC-006, FR-098)

## Phase 11: US9 — The docs and the requirements describe the console hosting as built (P9) — Phase B

Every value written in a doc is copied from the tested source (code constants, `deploy/security-headers.json`, test
expectations), never retyped from the design.

- [ ] T057 [P] [US9] `docs/pcr/gm.md` (Vietnamese): header lines 4–6 (console = GM-2 shipped shell; `/gm/v1/ui` Dev/Test
  only, removed later); "Xác thực và phạm vi" (cookie names per environment, `Path=/`, `Secure`, `SameSite` from
  `Gm:Cookies:SameSite`, one-time sign-out; CORS: expose list, max-age 7200, one origin outside Development, headers on every
  error answer, sibling-origin rule); endpoint table (`/auth/csrf` cookie name, `/auth/sso/start` return rule,
  `/auth/sso/callback` targets, `/gm/v1/ui` Dev/Test); "Cấu hình" table (`Gm:Cookies:SameSite`, `Gm:Console:BaseUrl`,
  `Gm:Cors:AllowedOrigins` count rule, startup lines of data-model §1); "Route catalog" (`gm.ui` condition); a new section
  "Console hosting (slice 018 — GM-2)" with the guarantees of contracts/console-hosting.md §3 and §6 and the denial fragment.
  Done when: a read-through finds no `Path=/gm`, no "`None` khi CORS bật", no "`/gm/v1/ui` … trang P0"; every value equals
  code/tests. (FR-090)
- [ ] T058 [P] [US9] `docs/pcr/platform.md`: §14 CORS paragraph (exposed headers, max-age, errors keep headers through the
  middleware's `OnStarting`, outcome of T007), §9/§214 line for `/gm/v1/ui` (Dev/Test), new **§24 "GM console: hosting và
  shell (slice 018 — GM-2)"**: workspace and versions rule (T025 table), codegen and `check:api`, `verify`, static-host header
  source and V3 decision, `check:dist`, e2e (how, browsers, ports), Breaking (slice 018) list BC-1..BC-9.
  Done when: read-through against code and `deploy/`. (FR-091)
- [ ] T059 [P] [US9] New `docs/guides/gm-console.md` (Vietnamese): 1 overview (case A diagram); 2 local loop (run-local +
  `npm ci` + `npm run dev`, `admin` seed, `localhost` only); 3 configuration per environment (server keys, `config.json`,
  CSP `connect-src`, sample values); 4 deploy a static host (build once, `dist/` + `config.json` + headers from `deploy/`,
  custom domain + TLS, no CDN default domain, TLS at the proxy needs forwarded headers for the CSRF route); 5 deploy order and
  version skew (Q19); 6 security invariants (design §12 short list); 7 tests (`verify`, `e2e`, Edge); 8 troubleshooting
  (`127.0.0.1`, LAN, old cookies after upgrade, `SameSite=None` needs HTTPS, CORS error seen as network error, port 5173
  busy); 9 what comes in GM-3/GM-4.
  Done when: the file exists and each command in it was run once while writing (Note). (FR-092, FR-085)
- [ ] T060 [P] [US9] `docs/guides/consumer-guide.md` (English): new section 15 "GM console for a game" — Q18 CI recipe for a
  game without pages (Node 22.20 in that job only, `npm ci --prefix framework/gm-console`, `npm run build -w app`, add the
  environment's `config.json`, apply `deploy/` headers, upload), per-environment server keys, deploy order, one-time sign-out,
  game tests pinning `gs_gm`/`gm.ui`; section 11 bump checklist lines for BC-1, BC-2, BC-4, BC-8; a pointer that pages of the
  game wait for GM-4.
  Done when: read-through; the recipe commands run once on this repository (`framework/gm-console` = `gm-console`). (FR-093,
  FR-085)
- [ ] T061 [P] [US9] `docs/html/01-gioi-thieu.html` (4.12 GM API: console shell and default app, cookies, Dev/Test page;
  module table line 543), `docs/html/03-deploy.html` §11 (GM: SSO, CORS, cookie, console host — values from the PCR),
  `README.md` (lines 52, 117: GM console + Dev/Test page), `docs/guides/environments-and-local-run.md` (banner line, `local`
  profile still opens `/gm/v1/ui`, console loop link); update the HTML "updated" stamps as the pages do.
  Done when: each page renders (open in the system Chrome) and has no stale line from the spec's list. (FR-094)
- [ ] T062 [US9] Owner's requirements file `Requirements/Game_Server_System_Requirements_v1.2.md` (gitignored; edit, never
  commit; research R17): rows TBD-05a (line 707), SEC-04 (442), SEC-06 (444), TBD-21 (725) to the case-A wording with status
  `Chốt 2026-11-05`; G6 (line 95), status line 7 and line 697 restated; SEC-13 (line 451) and the appendix line "Rate limit
  GM API" (line 816) to Q9: per-IP hard limit ≤ 30/min only on the GM auth endpoints (`/gm/v1/auth/*`), other GM routes keep
  the concurrency limiter, `Chốt 2026-11-05` (wording in research R17). Do not edit GM-01 (reported in the release notes).
  Done when: the Note quotes before/after of each line; `git status` does not list the file. (FR-096, D17)
- [ ] T063 [US9] Release notes final pass `specs/018-gm-console-hosting-shell/release-notes-draft.md`: every FR group,
  BC-1..BC-9 final, migration steps, owner questions 1–5, V2–V13 results, docs touched, test counts
  placeholder for T066.
  Done when: a read-through finds every BC id; nothing marked TODO. (FR-095)

## Phase 12: Verification, review and close — Phase B

- [ ] T064 [VERIFY] V9 — GM-4 seam (scratch, never committed): in the session scratch folder create a game-shaped layout
  `game/package.json` (`private`, workspaces `["gm-console", "framework/gm-console/packages/shell"]`), `game/framework` = a
  copy (not a link) of this repository's `gm-console/packages/shell` under `framework/gm-console/packages/shell`, and a
  minimal `game/gm-console` Vite app importing `@gameserver/gm-shell` and calling `createGmConsole()`; `npm install` then
  `npm run build -w gm-console`.
  Done when: Note records success, the hoisting result (one React copy) and any friction for GM-4, or the fallback taken.
  (FR-041, FR-098)
- [ ] T065 Reviewer walkthrough (story 9; after T063): a fresh agent with only `docs/guides/gm-console.md` and consumer guide
  §15 (no source reading) runs the console locally against run-local, signs in, opens two pages, then simulates a
  static-host deployment (`npm run build -w app`, a scratch `config.json`, `npm run preview` with the headers) and signs in
  there. Findings are fixed in the docs.
  Done when: the walkthrough succeeds without help, or every finding is fixed and recorded. (SC-010, FR-092, FR-093)
- [ ] T066 Final verification (after T065): Release build of `GameServer.slnx` 0 warnings; full suite in memory and with
  `GAMESERVER_TEST_STORE=File`; consumer fixture suites; in `gm-console/` from a clean `node_modules`: `npm ci`, `npm run
  verify`; start run-local (Local flavor, background PowerShell, port 5080), `npm run e2e` (both targets, Chrome), stop the
  host; `git diff main -- docs/api/gm-openapi.json proto src/*/Protos sdk` empty; `git status` lists only intended files
  (no `node_modules`, `dist`, e2e artifacts, `Requirements/`); Gm.Tests wall time within 10 % of T001 (a).
  Done when: all hold; counts recorded against T001. (FR-099, SC-005, SC-006, SC-009)
- [ ] T067 Couchbase full suite (after T066; PowerShell in the background, variables from the resume file, password never
  printed), including `SampleHostFlavorTests`.
  Done when: 0 failures; totals and duration recorded; a known flaky class (TelemetryTests) rerun alone before reporting.
  (FR-099, SC-009)
- [ ] T068 Tech-lead review (after T066): `tech-lead-reviewer` gets spec, plan, this file and `git diff --stat main`; focus:
  Phase A unchanged since T024 or re-reviewed; shell security invariants (design §12: no storage, no HTML from data, CSRF only
  on non-GET, no PII in URL, guard before any page call, 401/403 handling without loops), client middleware retry
  correctness (body replay once), catalog/navigation visibility, mutation boundary (FR-074), header source and CSP, e2e
  assertions actually exercising cross-origin, docs equal code, versions and lockfile.
  Done when: a findings list (Blocker/Major/Minor) is recorded. (constitution governance)
- [ ] T069 Review fixes (after T068): fix every accepted finding, each with a test that fails without the fix (scripted
  revert check); rerun the affected projects/workspaces, then `npm run verify`, the in-memory .NET suite and the e2e smoke.
  Done when: every accepted finding closed or recorded as an owner decision (release notes); suites green. (FR-099)
- [ ] T070 Resume and design status (after T069): `AgentMemories/features/p05-speckit/00_summary.md` gains an 018 row (DONE
  n/n, review outcome, test counts) and "Technical notes from 018" (Development cookie names and why, H14 outcome, e2e
  harness and browser paths, toolchain pins); `AgentMemories/features/gm-tool/00_summary.md` "Bước kế tiếp" moves to GM-3 and
  records GM-2 results (V2–V13 notes, decisions awaiting the owner).
  Done when: both files updated. (handover)

## Dependencies

**Phase order**: Setup (T001–T004) → US1 (T005–T010) → US2 (T011–T015) → US3 (T016–T020) → US4 + gate (T021–T024)
→ **[Phase B]** US5 (T025–T031) → US6 (T032–T038) → US7 (T039–T049) → US8 (T050–T053) → E2E (T054–T056) → US9
(T057–T063) → Close (T064–T070).

**Gates**: T004 (V2) blocks T006, T007; **T024 blocks every Phase B task**; T025 (V8) blocks T026; T050 (V3) blocks T051.

**Task dependencies** (beyond phase order):

| Task | After |
| --- | --- |
| T002, T003, T004 | T001 |
| T005 | T004 |
| T006 | T004, T005 |
| T007 | T006 |
| T008 | T001 |
| T009 | T001 |
| T011 | T008 (same test file `GmOptionsValidatorTests.cs`) |
| T012 | T011 |
| T013 | T012 |
| T014 | T013 |
| T016 | T011 |
| T017 | T016 |
| T018 | T017 |
| T019 | T016, T003 |
| T021 | T013 (`GmModule.cs`) |
| T022 | T019, T021 |
| T023 | T022 |
| T024 | T023 |
| T026 | T025 |
| T027, T028, T029, T030 | T026 |
| T032, T033 | T029 |
| T034 | T032, T033 |
| T035, T036 | T034 |
| T037 | T029 |
| T039 | T028 |
| T040 | T039, T034 |
| T041 | T040, T035, T036 |
| T042 | T037 |
| T043–T047 | T041, T042 |
| T048 | T043–T047 |
| T050 | T049 |
| T051 | T050 |
| T052 | T051 |
| T054 | T052 |
| T055 | T054 |
| T056 | T055 |
| T057–T061 | T056 |
| T062 | T024 (any time after Phase A) |
| T063 | T057–T062 |
| T064 | T026 (any time after the skeleton) |
| T065 → T066 → T067; T068 after T066; T069 after T068; T070 after T069 | |

**Parallel opportunities** (different files, no open dependency):
- Setup: T002, T003, T004 together after T001.
- US1: T008 and T009 in parallel with T005–T007 (different files; T008/T009 both touch `CorsTests.cs` for one host case
  each — **run T008's host case and T009 one after the other if one agent does both**).
- US5: T027, T028, T029, T030 together after T026.
- US6: T032, T033, T037 together; T035 and T036 together after T034.
- US7: T039 with T042; T043–T047 together after T041/T042.
- US9: T057–T061 together; T062 any time after T024; T064 any time after T026.

**Shared files to sequence** (not [P] against each other): `GmCors.cs` (T005, T008), `GmOptions.cs` (T011, T016), `GmCookies.cs` (T012), `GmModule.cs` (T013, T021), `GmSso.cs` (T013, T017), `CorsTests.cs` (T005, T008, T009, T013),
`GmOptionsValidatorTests.cs` (T008, T011, T016), `SsoTests.cs` (T018), `SampleHostFlavorTests.cs` (T022),
`gm-console/package.json` (T026, T028, T029, T052, T054), `eslint.config.js` (T026, T029), `app/vite.config.ts` (T026, T048,
T052), `packages/shell/src/index.ts` (T026, T040, T048), `router.tsx`/`App.tsx` (T041 only; pages register through
`builtins.ts` = T040), release notes (checkpoints only).

## Implementation strategy

1. **MVP = Phase A (T001–T024)**: a console on another origin of the same site works against the API (SSO return, cookies,
   CORS on every answer); the Development page still works; shippable alone.
2. **Increment B1 (US5, US6)**: workspace, typed client, sign-in/out, guard and skew — a console that signs in.
3. **Increment B2 (US7)**: read pages and navigation — the first useful console.
4. **Increment B3 (US8, e2e)**: static-host reference and the browser proof.
5. **Docs and close (US9, Phase 12).**

Each checkpoint (T010, T015, T020, T023, T031, T038, T049, T053, T056) leaves the suites green, so the work can stop at any
checkpoint with a consistent branch.

## Implementation risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| T004 finds a real H14 path (e.g. limiter or timeout writes before CORS headers apply) | High | T007 fixes exactly that path with its red case; no blanket header copy |
| The bare-app Staging test (T014 (e)) does not reflect the real host composition | Med | T022 repeats the cookie assertions on real Staging flavors over HTTPS (Couchbase) |
| `LocalFlavorFactory` tests change with CORS on in the Local config | Low | T019 re-runs them; cookies stay `Lax` (FR-010) |
| npm install fails on engines/peers | Med | T025 gate; `engine-strict` makes it fail early |
| Mantine violates `style-src 'self'` | Med | T050 gate + documented fallback |
| Vite preview cannot set per-path headers | Low | T052 fallback server in `e2e/` |
| E2E flakiness (first Vite compile, host not running, port taken) | Med | precheck with a hint, strict port, generous first-load timeout, sequential targets |
| Docs drift from code (017 lesson) | Med | docs copy values from tested sources; T065 walkthrough; T068 review |
| Requirements edits invisible to git | Low | T062 quotes before/after in its Note |
| Phase B started before Phase A review fixes | High | T024 blocks every Phase B task (dependency table) |

## FR coverage

| FR | Tasks |
| --- | --- |
| FR-001 | T005, T006, T055 |
| FR-002 | T004, T006, T007 |
| FR-003 | T008 |
| FR-004 | T009 |
| FR-010, FR-011 | T011, T012, T013, T014 |
| FR-012, FR-013 | T012, T013, T014, T022 |
| FR-014, FR-015, FR-016 | T013, T014 |
| FR-017 | T012, T013 |
| FR-020, FR-021 | T016, T019, T022 |
| FR-022..FR-026 | T017, T018 |
| FR-030 | T021, T022 |
| FR-040..FR-043 | T025, T026, T027 |
| FR-044 | T028 |
| FR-045 | T019, T026, T030, T055 |
| FR-046, FR-047 | T027 |
| FR-048 | T029 |
| FR-050 | T032 |
| FR-051, FR-052 | T033, T034 |
| FR-053, FR-054 | T034, T035 |
| FR-055, FR-056 | T034, T036, T048, T055 |
| FR-057, FR-058, FR-059 | T033, T034, T041, T055 |
| FR-060 | T036 |
| FR-061 | T029, T043 |
| FR-062 | T033, T037 |
| FR-065 | T039, T041, T056 |
| FR-066, FR-067 | T040, T041 |
| FR-068..FR-072 | T043, T044, T045, T046, T047 |
| FR-073 | T042 |
| FR-074 | T046, T047, T048 |
| FR-075 | T048 |
| FR-076 | T037 |
| FR-080, FR-081 | T050, T051 |
| FR-082 | T051, T055 |
| FR-083, FR-084 | T052 |
| FR-085 | T059, T060 |
| FR-087 | T054, T055, T056 |
| FR-090..FR-094 | T057, T058, T059, T060, T061 |
| FR-095 | T002, T063 |
| FR-096 | T062 |
| FR-098 | T004, T014, T025, T028, T050, T052, T055, T056, T064 |
| FR-099 | T001, T023, T066, T067, T069 |

| SC | Tasks |
| --- | --- |
| SC-001 | T004, T006 |
| SC-002 | T014, T022 |
| SC-003 | T017, T018 |
| SC-004 | T021, T022 |
| SC-005 | T028, T031, T066 |
| SC-006 | T055, T056, T066 |
| SC-007 | T052 |
| SC-008 | T041, T055 |
| SC-009 | T023, T066, T067 |
| SC-010 | T065 |

## Planning notes (gaps between spec, plan and code, resolved here)

1. **H14 may need no code**: T007 is conditional (plan N1). The permanent test of T004 is the deliverable either way; its
   scripted red check proves it can fail.
2. **Staging `__Host-` proof in-process**: an in-memory Staging `GmTestHost` cannot start (store/test-auth/secret checks,
   017 T005 note), so T014 (e) uses a bare app with the GM cookie configuration, and T022 repeats the assertions on real
   Staging flavors (Couchbase).
3. **`CorsProbeModule` isolation**: the throwing/waiting probe routes must not reach the committed GM document; they are
   added only through `GmTestHost.StartAsync(extra:)` (as 017's `ProbeGmModule`), so `OpenApiTests` is untouched.
4. **Limiter and timeout cases need their own hosts** (T004): `GmConcurrency = 1`/`QueueFactor = 0` and
   `GmTimeoutSeconds = 1` would disturb the other cases.
5. **`GmOptionsValidatorTests.cs` is new** and shared by T008, T011 and T016 (sequenced); existing validator cases in
   `CorsTests` and `SsoTests` stay where they are.
6. **Sample flavor values** (T019): only Local/Dev get console values; Beta/Uat/Live stay without, so the flavor matrix
   (T022) proves that a game bumps without new settings; Uat gets console values from test settings to prove CORS.
7. **run-local is not restarted by tests**: the e2e harness starts only Vite; T030/T049/T055/T066 start run-local by hand
   or in a background PowerShell (no `-Reset`, the owner's sample data stays).
8. **No `config.json` routing on the main e2e context**: Playwright routing disables the HTTP cache, which V13 observes;
   the Q19 scenario uses a second context.
9. **Catalog `If-None-Match`**: the design's step 10.3 is not implemented as written (D8); `ETag` is still exposed.
10. **Requirements file** is the owner's and gitignored (T062): never staged.
11. **Version bump/tag** stay outside the slice (D18); the shell/app versions follow `GameServerFrameworkVersion` 0.7.0 and
    move with the next bump (`VersionTests` will require all of them together).
12. **SEC-13 needs no code task**: Q9 limits the per-IP 30/min rule to the GM auth endpoints; the code has no per-IP GM
    limiter at all (`GameServerBuilder.cs:346-351`: concurrency `gs-gm` only), so nothing exceeds Q9. T062 edits the rows;
    implementing the auth-endpoint limiter is a later slice.
