# Data model: GM console hosting and shell (slice 018 gm-console-hosting-shell, GM-2)

No persisted document, collection, index or proto changes. The entities below are configuration, HTTP headers and
in-memory browser state.

## 1. Server configuration (section `Gm`, `GmOptions`)

| Key | Type / default | Rule (startup failure unless noted) | FR |
| --- | --- | --- | --- |
| `Gm:Cookies:SameSite` | string, `Lax` | `Lax` or `None` (case-insensitive); `None` only in Development/Test (review T024 m2) and needs ≥ 1 CORS origin | FR-010, FR-011 |
| `Gm:Cors:AllowedOrigins[]` | list, empty | existing rules (no `*`, `https` outside Development) + each entry exactly its serialized origin (no default port, user info, path, query, fragment or trailing slash; review T024 m1) + at most 1 entry outside Development | FR-003 |
| `Gm:Console:BaseUrl` | string, none | absolute `http(s)`, path ends `/`, no query/fragment/user info; `https` outside Development; origin ∈ `Gm:Cors:AllowedOrigins` (= the one origin outside Development); required when `Gm:Cors:AllowedOrigins` is set; optional otherwise (no console) | FR-020, FR-021 |

Startup lines (text fixed by the tasks, data-model contract):

```
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

Sample host values (D12):

| Flavor | `Gm:Cors:AllowedOrigins` | `Gm:Console:BaseUrl` |
| --- | --- | --- |
| Local, Dev | `["http://localhost:5173"]` | `http://localhost:5173/` |
| Beta / Uat / Live | none (no console yet, Q16) | none |

## 2. Cookies

| Environment | Session cookie | CSRF cookie | `Path` | `Secure` | `SameSite` | `Domain` |
| --- | --- | --- | --- | --- | --- | --- |
| outside Development | `__Host-gs_gm` | `__Host-gs_gm_csrf` | `/` | always (antiforgery `Always`) | setting | none |
| Development | `gs_gm_dev` | `gs_gm_dev_csrf` | `/` | session: HTTPS or `None`; CSRF: `SameAsRequest` (`Always` with `None`) | setting | none |
| pre-018 (ignored) | `gs_gm` | `gs_gm_csrf` | `/gm` | — | — | — |

All `HttpOnly`. Session value, ticket and expiry unchanged (`Gm:SessionHours`, `Gm:IdleMinutes`). OIDC correlation/nonce
cookies unchanged (`Path=/gm/v1/auth/sso/callback`, `Lax`).

## 3. CORS answer (policy `gs-gm`, only when origins are configured)

| Request | Headers added (allowed origin only) |
| --- | --- |
| Preflight `OPTIONS` | `Access-Control-Allow-Origin`, `-Allow-Credentials: true`, `-Allow-Methods: GET,POST,PUT,DELETE`, `-Allow-Headers: Content-Type,X-CSRF-Token,X-Request-Id`, `-Max-Age: 7200` |
| Actual request, any status | `Access-Control-Allow-Origin`, `-Allow-Credentials: true`, `-Expose-Headers: X-Request-Id,Retry-After,ETag` |

## 4. SSO redirects

| Case | `Gm:Console:BaseUrl` set (`B`) | not set (no console; `D` = `/gm/v1/ui` with development tools, `/gm/v1/me` without) |
| --- | --- | --- |
| success, accepted `returnUrl=r` | `B` + `r` without leading `/` | `r` (must start with `/gm/`) |
| success, missing/refused `returnUrl` | `B` | `D` |
| any denial | `B` + `#/login?error=sso_denied` | `D` + `?error=sso_denied` |

Accepted `r`: 1..512 characters, each 0x21–0x7E, none of `//`, `\`, `..`, `%2e` (any case); with a console `r` is `/` or
`/#<fragment>` (no other path, no query), without a console it starts with `/gm/` (review T024 M1).

## 5. Console configuration `config.json` (static host, per environment)

```json
{ "apiBaseUrl": "https://gm-api.beta.example.com", "gameId": "zhpvp", "environmentId": "beta" }
```

| Member | Rule |
| --- | --- |
| `apiBaseUrl` | absolute `http`/`https` URL, no query, fragment or user info; a trailing `/` is ignored; its origin must be in the static host's CSP `connect-src` |
| `gameId`, `environmentId` | non-empty strings, ≤ 64 characters; compared with `/me` exactly (Q19) |
| other members | ignored (forward compatibility) |

`app/public/config.json` (local dev, committed): `{ "apiBaseUrl": "http://localhost:5080", "gameId": "sample",
"environmentId": "local" }`. `deploy/config.schema.json` = JSON Schema 2020-12 of the rules; `deploy/config.example.json` =
the Beta example above.

## 6. Build-time stamp

```ts
declare const __GM_BUILT_FOR__: { frameworkVersion: string; gameVersion?: string };
```

`frameworkVersion` = `packages/shell/package.json` `version` (= `GameServerFrameworkVersion`); `gameVersion` =
environment variable `GM_CONSOLE_GAME_VERSION` at build time when set. Comparison (FR-056): `major.minor` of the text before
the first `+` or `-`.

## 7. Browser state (memory only, FR-061)

| State | Source | Cleared on |
| --- | --- | --- |
| `config` | `config.json` | never (page lifetime) |
| `authMethods` | `GET /gm/v1/auth/methods` | page reload |
| `me` (`GmMe`) | `/me` or local login answer | 401, sign-out |
| `csrfToken` | `GET /gm/v1/auth/csrf` | sign-in, sign-out, 401 (refetched) |
| `catalog` (`GmCatalog`) | `GET /gm/v1/console/openapi.json` | 401, sign-out |
| `returnTo` (hash) | navigation before a 401 / login | after use |
| query cache (TanStack Query) | page reads | 401, sign-out (`queryClient.clear()`) |

Session phases: `loading-config` → `config-error` | `checking` → `signed-out` | `signed-in` → `blocked` (game/env
mismatch) | `ready` (catalog loaded) | `catalog-error`.

## 8. Catalog operation (subset parsed in GM-2; GM-3 extends)

```ts
interface GmOperation {
  routeName: string;            // operationId
  method: 'GET' | 'POST' | 'PUT' | 'DELETE';
  template: string;             // path key, e.g. /gm/v1/players/{playerId}
  permission: string;           // x-gm-permission ('public' or a permission)
  kind: 'command' | 'action' | 'query';   // x-gm-kind
  target: 'player' | 'global';  // x-gm-target
  paged: boolean;               // x-gm-paged === true
  summary?: string;
}
interface GmCatalog { operations: ReadonlyMap<string, GmOperation>; }
```

An operation without `operationId` is skipped (017 guarantees one); missing `x-gm-*` keys default to `query`/`global`/
`false`.

## 9. Page registry (internal, design §7.3 shapes)

| Page id | Path (hash) | Group | `requiredPermission` | `requiredRoutes` |
| --- | --- | --- | --- | --- |
| `players` | `#/players` | Players | `player.read` | `gm.player` (search modes need their own route; each mode hidden without it) |
| `player` | `#/players/:playerId` | (not in menu) | `player.read` | `gm.player` |
| player tab `receipts` | tab | — | `player.read` | `gm.player_receipts` |
| player tab `audit` | tab | — | `player.read` | `gm.player_audit` |
| player tab `ledger` | tab | — | `player.read` | `economy.ledger` |
| `receipt` | `#/receipts/:receiptId` | (not in menu) | `player.read` | `gm.receipt` |
| `staff` | `#/staff` | Staff | `staff.read` | `gm.staff_list` |
| `staff-detail` | `#/staff/:staffId` | (not in menu) | `staff.read` | `gm.staff` (audit tab: `gm.staff_audit`) |
| `ops` | `#/ops` | Operations | `ops.read` | any of `gm.health`, `gm.ops_version`, `gm.ops_gate` (cards individually) |
| `login` | `#/login` | — | — | — |

Player-search modes: id → `gm.player`; mapping key → `gm.player_by_mapping`; display name → `gm.player_search`.

## 10. Static-host header set (`deploy/security-headers.json`)

```json
{
  "placeholders": { "apiOrigin": "Origin of config.json apiBaseUrl, e.g. https://gm-api.beta.example.com" },
  "all": {
    "Content-Security-Policy": "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' {{apiOrigin}}; img-src 'self' data:; font-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
    "Strict-Transport-Security": "max-age=31536000",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
    "Cross-Origin-Opener-Policy": "same-origin",
    "Permissions-Policy": "camera=(), microphone=(), geolocation=()"
  },
  "document": { "paths": ["/", "/index.html", "/config.json"], "headers": { "Cache-Control": "no-cache" } },
  "asset": { "paths": ["/assets/*"], "headers": { "Cache-Control": "public, max-age=31536000, immutable" } }
}
```

`style-src` may become `'self' 'unsafe-inline'` by V3 (T050); the file, the nginx example, the table and the PCR change
together (FR-082 test).

## 11. End-to-end harness variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `GM_E2E_API` | `http://localhost:5080` | API origin of the run-local host |
| `GM_E2E_TARGET` | `dev,preview` | targets to run |
| `GM_E2E_BROWSER` | `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe` | browser executable (Edge: `C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe`) |
| `GM_E2E_STAFF`, `GM_E2E_PASSWORD` | `admin`, `dev-only-change-me` | Local flavor seed staff |

Console port 5173 (`strictPort`); port 5180 is never used.
