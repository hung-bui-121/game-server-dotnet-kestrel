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
- **Phase B — console.** `gm-console/` npm workspace: the framework's React console shell `@gameserver/gm-shell`, the default
  app, the read pages, the static-host reference (`deploy/`) and the local end-to-end loop. *(filled by T031–T056)*
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

*(T023, run by the owner)*

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

*(T031, T038, T049, T053, T056)*

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
| V3 | *(T050)* |
| V4 | *(T055)* |
| V5 | Confirmed in the repository (T014): `__Host-` names + `Path=/` + antiforgery round trip over HTTPS on a Staging bare app; policy `Always` throws on plain HTTP (pinned). |
| V7 | Not run (no Firefox installed). |
| V8 | *(T025)* |
| V9 | *(T064)* |
| V11 | *(T052)* |
| V12 | *(T028)* |
| V13 | *(T056)* |

## Docs touched

*(T057–T063)*

## Tests

*(counts per checkpoint; final counts by T066/T067)*

## Next tag

`v0.8.0` proposed together with 017; no version bump or tag in this slice (D18).
