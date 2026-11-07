# GM console static host — response headers

Source: [`security-headers.json`](security-headers.json) (slice 018, FR-080..FR-082). `deploy.test.ts` fails when this table,
[`nginx.conf.example`](nginx.conf.example) and the JSON differ. Replace `{{apiOrigin}}` with the origin of the environment's
`config.json` `apiBaseUrl` (for example `https://gm-api.beta.example.com`): the CSP is per environment, like `config.json`.

| Path class | Paths | Header | Value |
| --- | --- | --- | --- |
| all | every answer | Content-Security-Policy | `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' {{apiOrigin}}; img-src 'self' data:; font-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'` |
| all | every answer | Strict-Transport-Security | `max-age=31536000` |
| all | every answer | X-Content-Type-Options | `nosniff` |
| all | every answer | Referrer-Policy | `no-referrer` |
| all | every answer | Cross-Origin-Opener-Policy | `same-origin` |
| all | every answer | Permissions-Policy | `camera=(), microphone=(), geolocation=()` |
| document | `/`, `/index.html`, `/config.json` | Cache-Control | `no-cache` |
| asset | `/assets/*` | Cache-Control | `public, max-age=31536000, immutable` |

Paths are **relative to the console's base path**, not the domain root. A console at the domain root uses them as written
(and `nginx.conf.example` as is). A console under a sub-path (for example `https://gm.beta.example.com/console/`,
`Gm:Console:BaseUrl` with path `/console/`) prefixes every path class: `/console/`, `/console/index.html`, `/console/config.json`,
`/console/assets/*`; in nginx `location = /console/`, `location = /console/index.html`, `location = /console/config.json`,
`location /console/assets/` and `location /console/` in place of `location /`, with the same header values.

**The console's origin must serve nothing but the console**: a sub-path is for a dedicated console host, never a shared CDN or
application origin. Any other page on that origin could fetch the CSRF token with `credentials: 'include'` and send GM
mutations as the signed-in staff member.

## CDN and static-host notes

- Set these as **HTTP response headers** (CDN response-header policy, edge rules, bucket metadata …). A `<meta>` CSP cannot
  carry `frame-ancestors` and is not a substitute.
- Use a **custom domain under the company domain, with TLS**, on the same site as the GM API (`gm.<env>.<domain>` →
  `gm-api.<env>.<domain>`). CDN/PaaS default domains (`*.cloudfront.net`, `*.azureedge.net`, `*.pages.dev`, `*.web.app`,
  `*.github.io` …) are on the Public Suffix List: a console there is on another site than the API (case B) and is not
  supported (`SameSite=Lax` cookies are not sent).
- `style-src 'self'` holds: the console injects no `<style>` element (V3, slice 018 T050). Scripts are `'self'` only.
- Upload `dist/` once per release (the build is the same for every environment) and add the environment's `config.json`
  next to `index.html`; `config.json` and `index.html` are `no-cache`, hashed assets are immutable.
- The console uses hash routing (`/#/players/…`): no SPA rewrite rule is needed; any other path may answer 404.
