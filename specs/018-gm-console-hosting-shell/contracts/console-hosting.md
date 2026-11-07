# Contract: console hosting — what a console and its static host can rely on (slice 018, GM-2)

## 1. Layout (case A)

```
https://gm.<env>.<domain>        static host: index.html, assets/*, config.json, headers of deploy/security-headers.json
        │  fetch, credentials: 'include'   (Sec-Fetch-Site: same-site)
        ▼
https://gm-api.<env>.<domain>    reverse proxy → Kestrel, /gm/** only; TLS terminated with forwarded headers (OPS-02)
```

Server settings of the environment: `Gm:Cors:AllowedOrigins = ["https://gm.<env>.<domain>"]`,
`Gm:Console:BaseUrl = "https://gm.<env>.<domain>/"`, `Gm:Cookies:SameSite` unset (`Lax`), `Gm:Oidc:*` as today.
Console file: `config.json = { "apiBaseUrl": "https://gm-api.<env>.<domain>", "gameId": "<game>", "environmentId": "<env>" }`.
Local: console `http://localhost:5173`, API `http://localhost:5080` (sample host Local flavor). A console on another site
(CDN default domain) is not supported. **No console** (neither `Gm:Cors:AllowedOrigins` nor `Gm:Console:BaseUrl`, today's
beta/uat/live): the API works as before, SSO lands on `/gm/v1/me` (or `/gm/v1/ui` where development tools are on).

## 2. Sequences

**Open the console**

```
GET  /config.json                         (static host, no-store)
GET  {api}/gm/v1/auth/methods             → { local, sso }
GET  {api}/gm/v1/me                       → 200 GmMe | 401 → login page
     guard: me.gameId/environmentId == config ? continue : block screen
GET  {api}/gm/v1/console/openapi.json     → 200 (browser cache revalidates with ETag → 304)
GET  {api}/gm/v1/auth/csrf                → { token } + CSRF cookie   (before the first mutation)
```

**Local sign-in (Development/Test)**

```
GET  {api}/gm/v1/auth/csrf                → token T1 (anonymous)
POST {api}/gm/v1/auth/login  X-CSRF-Token: T1, JSON {staffId, password}   (preflight once per 7200 s)
     → 200 GmMe + Set-Cookie session
GET  {api}/gm/v1/auth/csrf                → token T2 (bound to the new session), fetched lazily before the next mutation
                                            (T069 / review m3: not right after the login)
```

**SSO**

```
top-level  GET {api}/gm/v1/auth/sso/start?returnUrl=%2F%23%2Fplayers%2Fabc  → 302 IdP
IdP → GET {api}/gm/v1/auth/sso/callback?code=…   → Set-Cookie session, 302 https://gm.<env>.<domain>/#/players/abc
                                                 → denial: 302 https://gm.<env>.<domain>/#/login?error=sso_denied
```

**Sign-out**

```
POST {api}/gm/v1/auth/logout  X-CSRF-Token: T2   → 204 + Set-Cookie session expired (same name, Path=/, Secure)
GET  {api}/gm/v1/auth/csrf                        → new anonymous token
```

## 3. Guarantees of the GM API to the console

| Guarantee | Since |
| --- | --- |
| CORS headers for the configured origin on every `/gm/**` answer, errors included; `X-Request-Id`, `Retry-After`, `ETag` readable | 018 (FR-001, FR-002) |
| GET calls need no custom header (no preflight) | 018 (FR-051) |
| Problem Details on every error: `code`, `codeNumber`, `requestId`, `retryable`, optional `detail`, `details`, `errors` | 004, 017 |
| `401` = no/expired session; `403 FORBIDDEN` = permission/scope; `403 CSRF_FAILED` = token/origin | 007 |
| Session cookie `SameSite=Lax` with CORS on (unless `Gm:Cookies:SameSite=None`) | 018 (FR-010) |
| SSO lands on `BaseUrl` + the console path, or `BaseUrl#/login?error=sso_denied` | 018 (FR-022..FR-024) |
| `/me` carries `gameId`, `environmentId`, `frameworkVersion`, `gameVersion` | 013, 017 |
| `/gm/v1` changes additively within a version; breaking changes in release notes | PCR gm |

## 4. Guarantees of the static host (reference `deploy/`)

| Path class | Headers |
| --- | --- |
| every answer | CSP (data-model §10, `connect-src 'self' <apiOrigin>`), HSTS, `nosniff`, `Referrer-Policy: no-referrer`, COOP `same-origin`, `Permissions-Policy` |
| `/`, `/index.html`, `/config.json` | `Cache-Control: no-cache` |
| `/assets/*` (hashed) | `Cache-Control: public, max-age=31536000, immutable` |

Headers must be HTTP response headers (a `<meta>` CSP cannot carry `frame-ancestors`). Hash routing: the host needs no SPA
rewrite.

## 5. Deployment order and skew (Q19)

1. Deploy the server (new framework/game version).
2. Deploy the console built from the same commit.
3. Between 1 and 2 the old console runs against the new server: the catalog drives navigation at run time; a page compiled
   against an older document may get 400/404 (backend stays safe); the console shows a warning when `major.minor` of the
   framework differs, and blocks only on another game or environment.

## 6. Compatibility promise (PCR gm)

- Cookie names, `Path` and attributes per environment, `Gm:Cookies`, `Gm:Console`, the redirect targets and the exposed
  headers are semantic PCR entries: a change is a breaking change with a release note.
- `config.json` members are additive; the console ignores unknown members.
- The denial fragment `#/login?error=sso_denied` and the hash route `#/login` are part of this contract (D7).
