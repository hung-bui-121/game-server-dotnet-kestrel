# GM console end-to-end smoke

Slice 018 (FR-087). Vitest in Node drives the **system** Chrome or Edge through `playwright-core` (no browser is ever
downloaded) against a running run-local host, on two targets, both on `http://localhost:5173`:

- `dev`: the Vite dev server (`npm run dev`), no CSP;
- `preview`: the built app (`vite build`, then `vite preview`) with the reference static-host headers of
  `deploy/security-headers.json` (CSP `connect-src 'self' <GM_E2E_API>`).

## Run

1. Start the host (Local flavor, port 5080) in another terminal and leave it running:

   ```powershell
   tools/local/run-local.ps1
   ```

2. In `gm-console/` (after `npm ci`), with nothing else on port 5173:

   ```powershell
   npm run e2e
   ```

   The harness starts and stops Vite itself. If the GM API does not answer, the run fails with
   "start tools/local/run-local.ps1 (Local flavor) first".

## Variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `GM_E2E_API` | `http://localhost:5080` | API origin of the run-local host; the preview target serves a generated `e2e/.artifacts/config.json` for it; any other value than the default skips the dev target (it always serves `app/public/config.json`) |
| `GM_E2E_TARGET` | `dev,preview` | targets to run (comma-separated) |
| `GM_E2E_BROWSER` | `C:/Program Files (x86)/Google/Chrome/Application/chrome.exe` | browser executable; Edge: `C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe` |
| `GM_E2E_STAFF`, `GM_E2E_PASSWORD` | `admin`, `dev-only-change-me` | Local flavor seed staff |
| `GM_E2E_PLAYER` | `e2e-no-such-player` | player id opened under the CSP (default: the "player not found" path) |

Port 5173 is the console's (strict port); never use 5180.

## What it checks (`smoke.e2e.ts`)

1. login page with the local form and no SSO button;
2. local sign-in: the POST carries `X-CSRF-Token`, Chrome sends `Origin: http://localhost:5173` and
   `Sec-Fetch-Site: same-site`; header shows `local`, `sample` and the staff name; GETs carry no custom header;
3. cookies `gs_gm_dev` / `gs_gm_dev_csrf` on the API host: HttpOnly, `SameSite=Lax`, `Path=/`;
4. Staff → `admin` → detail; Ops → framework version; exactly one catalog request;
4b. the player page (`GM_E2E_PLAYER`, else its "not found" path) renders with 0 CSP violations;
5. a 404 fetched from the page exposes `X-Request-Id`;
6. sign-out → login page, `/me` then 401; only the login and logout POSTs were preflighted;
7. a second browser context whose `config.json` names `beta`: block screen naming `beta` and `local`, no catalog request, 0 CSP
   violations (preview);
8. no CSP violation (preview), no console error except the browser's own "Failed to load resource" for the 401/404
   answers the smoke causes on purpose, and nothing stored: `localStorage`/`sessionStorage` empty, `document.cookie === ''`
   on the console origin, `indexedDB.databases()` empty.

Preview only (V13, asserted): after a reload the session persists and the catalog is revalidated by the browser cache
(`If-None-Match` → 304), read through the Chrome DevTools Protocol.

Each run writes `e2e/.artifacts/smoke-<target>.json` (timings, headers, cookies, preflights, catalog revalidation, console
errors); the folder is ignored by git.
