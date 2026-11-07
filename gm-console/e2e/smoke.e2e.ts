import type { Browser, BrowserContext } from 'playwright-core';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  API,
  BROWSER,
  checkApi,
  CONSOLE_ORIGIN,
  CONSOLE_URL,
  launchBrowser,
  openRecorder,
  PASSWORD,
  STAFF,
  startTarget,
  TARGETS,
  targetSkipReason,
  unexpectedConsoleErrors,
  writeReport,
  type ConsoleRecorder,
} from './harness';

/**
 * Slice 018 T055/T056 (FR-087, SC-006, SC-008, Q19): the console on each target against the running run-local host —
 * cross-origin local sign-in, header, cookies, Staff and Ops pages, a readable request id on an error, sign-out, the
 * wrong-server block screen, CSP violations and console errors, catalog revalidation (V13, preview).
 */
const CATALOG = '/gm/v1/console/openapi.json';
const LOGIN = '/gm/v1/auth/login';
const LOGOUT = '/gm/v1/auth/logout';
/** A player to open under the CSP; without one the player page's "not found" path is rendered (review T068 m5). */
const PLAYER = process.env.GM_E2E_PLAYER ?? 'e2e-no-such-player';

describe.each(TARGETS)('GM console smoke, %s target', (target) => {
  const skip = targetSkipReason(target);
  if (skip) {
    it.skip(skip, () => undefined);
    return;
  }

  let stop: (() => Promise<void>) | undefined;
  let browser: Browser | undefined;
  let context: BrowserContext;
  let recorder: ConsoleRecorder;
  const report: Record<string, unknown> = { target, browser: BROWSER, api: API };

  beforeAll(async () => {
    await checkApi();
    stop = await startTarget(target);
    browser = await launchBrowser();
    report.browserVersion = browser.version();
    context = await browser.newContext();
    recorder = await openRecorder(context);
  });

  afterAll(async () => {
    report.consoleErrors = recorder?.consoleErrors;
    report.apiCalls = recorder?.apiCalls();
    writeReport(`smoke-${target}`, report);
    await browser?.close();
    await stop?.();
  });

  it('1. opens on the login page with the local form and no SSO button', async () => {
    const { page } = recorder;
    await page.goto(CONSOLE_URL);

    await page.getByRole('form', { name: 'Local sign-in' }).waitFor({ timeout: 30_000 });
    expect(await page.getByRole('button', { name: 'Sign in with SSO' }).count()).toBe(0);
  });

  it('2. signs in cross-origin: CSRF on the POST, Origin and Sec-Fetch-Site seen as the console, only POSTs preflighted', async () => {
    const { page } = recorder;
    await page.getByRole('textbox', { name: 'Staff id' }).fill(STAFF);
    await page.locator('input[type=password]').fill(PASSWORD);
    const loginAnswer = page.waitForResponse((r) => r.url() === API + LOGIN);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await loginAnswer;
    const answeredAt = Date.now();
    await page.getByRole('heading', { name: 'Players', level: 1 }).waitFor();
    report.loginToFirstPageMs = Date.now() - answeredAt;

    expect(await page.getByLabel('Environment local').textContent()).toBe('local');
    expect(await page.getByLabel('Game sample').textContent()).toBe('sample');
    expect((await page.getByTestId('staff-name').textContent())?.length).toBeGreaterThan(0);
    report.staffName = await page.getByTestId('staff-name').textContent();

    const login = recorder.requests.find((r) => r.method === 'POST' && r.url === API + LOGIN);
    expect(login?.headers.origin).toBe(CONSOLE_ORIGIN);
    expect(login?.headers['sec-fetch-site']).toBe('same-site');
    expect(login?.headers['x-csrf-token']?.length).toBeGreaterThan(0);
    expect(login?.status).toBe(200);
    report.loginHeaders = { origin: login?.headers.origin, secFetchSite: login?.headers['sec-fetch-site'], csrf: 'present' };

    const gets = recorder.requests.filter((r) => r.method === 'GET' && r.url.startsWith(API));
    expect(gets.every((r) => r.headers['x-csrf-token'] === undefined && r.headers['content-type'] === undefined)).toBe(true);
  });

  it('3. keeps the session in the Development cookie of the API host (HttpOnly, Lax, Path=/) (V4)', async () => {
    const cookies = await context.cookies(API);
    const session = cookies.find((c) => c.name === 'gs_gm_dev');
    expect(session).toMatchObject({ httpOnly: true, sameSite: 'Lax', path: '/' });
    expect(cookies.find((c) => c.name === 'gs_gm_dev_csrf')).toMatchObject({ httpOnly: true, sameSite: 'Lax', path: '/' });
    report.cookies = cookies.map((c) => ({ name: c.name, path: c.path, sameSite: c.sameSite, httpOnly: c.httpOnly, secure: c.secure }));
  });

  it('4. opens Staff → admin and Ops → version, loading the catalog once', async () => {
    const { page } = recorder;
    await page.getByRole('link', { name: 'Staff', exact: true }).click();
    await page.getByRole('link', { name: STAFF, exact: true }).click();
    await page.getByRole('heading', { name: `Staff ${STAFF}` }).waitFor();
    expect(await page.getByRole('table', { name: 'Staff member' }).textContent()).toContain(STAFF);

    await page.getByRole('link', { name: 'Operations' }).click();
    const version = await page.getByTestId('framework-version').textContent();
    expect(version).toMatch(/^\d+\.\d+\.\d+/);
    report.serverFrameworkVersion = version;

    const catalogCalls = recorder.apiCalls().filter((c) => c === `GET ${CATALOG}`);
    report.catalogRequestsBeforeReload = catalogCalls.length;
    expect(catalogCalls).toHaveLength(1);
  });

  it.runIf(target === 'preview')('V13: after a reload the session persists; the catalog revalidation is recorded', async () => {
    const { page } = recorder;
    const before = recorder.requests.length;
    await page.reload();
    await page.getByTestId('framework-version').waitFor({ timeout: 30_000 });

    const catalog = recorder.requests.slice(before).filter((r) => r.url === API + CATALOG && r.method === 'GET');
    report.v13 = catalog.map((r) => ({ ifNoneMatch: r.headers['if-none-match'] ?? null, status: r.status ?? null, etag: r.responseHeaders.etag ?? null }));
    expect(catalog).toHaveLength(1);
    expect(catalog[0]?.headers['if-none-match']?.length).toBeGreaterThan(0);
    expect(catalog[0]?.status).toBe(304);
    expect(await page.getByTestId('staff-name').count()).toBe(1);
  });

  it('4b. renders the player page (or its "not found" path) without a CSP violation', async () => {
    const { page } = recorder;
    await page.goto(`${CONSOLE_URL}#/players/${encodeURIComponent(PLAYER)}`);
    await page.getByRole('heading', { name: `Player ${PLAYER}` }).waitFor();
    await page.getByRole('table', { name: 'Aggregates' }).or(page.getByText('Player not found.')).waitFor();
    report.playerPage = (await page.getByText('Player not found.').count()) > 0 ? 'not found' : 'rendered';
    expect(await recorder.violations()).toEqual([]);
  });

  it('5. reads the request id of an error answer from the page (FR-001 in a browser)', async () => {
    const result = await recorder.page.evaluate(async (api) => {
      const response = await fetch(`${api}/gm/v1/players/does-not-exist`, { credentials: 'include' });
      return { status: response.status, requestId: response.headers.get('X-Request-Id') };
    }, API);

    expect(result.status).toBe(404);
    expect(result.requestId?.length).toBeGreaterThan(0);
    report.errorRequestId = result;
  });

  it('6. signs out to the login page; /me then answers 401', async () => {
    const { page } = recorder;
    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.getByText('You are signed out.').waitFor();

    const status = await page.evaluate(async (api) => (await fetch(`${api}/gm/v1/me`, { credentials: 'include' })).status, API);
    expect(status).toBe(401);
    const logout = recorder.requests.find((r) => r.method === 'POST' && r.url === API + LOGOUT);
    expect(logout?.headers['x-csrf-token']?.length).toBeGreaterThan(0);
    expect(logout?.status).toBe(204);

    const preflights = recorder.requests.filter((r) => r.method === 'OPTIONS' && r.url.startsWith(API));
    report.preflights = preflights.map((r) => `${new URL(r.url).pathname} for ${r.headers['access-control-request-method'] ?? '?'}`);
    expect(preflights.map((r) => new URL(r.url).pathname).sort()).toEqual([LOGIN, LOGOUT]);
    expect(preflights.every((r) => r.headers['access-control-request-method'] === 'POST')).toBe(true);
  });

  it('7. blocks with a config.json of another environment: names both values, loads no catalog (Q19)', async () => {
    const second = await browser!.newContext();
    await second.route('**/config.json', (route) =>
      route.fulfill({ contentType: 'application/json', body: JSON.stringify({ apiBaseUrl: API, gameId: 'sample', environmentId: 'beta' }) }),
    );
    const blocked = await openRecorder(second);
    await blocked.page.goto(CONSOLE_URL);
    await blocked.page.getByRole('textbox', { name: 'Staff id' }).fill(STAFF);
    await blocked.page.locator('input[type=password]').fill(PASSWORD);
    await blocked.page.getByRole('button', { name: 'Sign in' }).click();
    await blocked.page.getByRole('heading', { name: 'Wrong server' }).waitFor();

    const table = await blocked.page.getByRole('table', { name: 'Expected and actual server' }).textContent();
    expect(table).toContain('beta');
    expect(table).toContain('local');
    await blocked.page.waitForTimeout(500);
    expect(blocked.apiCalls().filter((c) => c.endsWith(CATALOG))).toEqual([]);
    report.blockedCalls = blocked.apiCalls();
    report.blockedCspViolations = await blocked.violations();
    if (target === 'preview') {
      expect(await blocked.violations()).toEqual([]);
    }

    report.blockedConsoleErrors = blocked.consoleErrors;
    expect(unexpectedConsoleErrors(blocked.consoleErrors)).toEqual([]);
    await second.close();
  });

  it('8. reports no CSP violation (preview), no unexpected console error and nothing in browser storage (FR-061)', async () => {
    // FR-048 exception: this test asserts the rules (it only counts entries; cookies of the API host are HttpOnly).
    const stored = await recorder.page.evaluate(async () => ({
      // eslint-disable-next-line no-restricted-properties
      storage: window.localStorage.length + window.sessionStorage.length,
      // eslint-disable-next-line no-restricted-properties
      cookie: document.cookie,
      // eslint-disable-next-line no-restricted-properties
      databases: (await window.indexedDB.databases()).length,
    }));
    report.browserStorage = stored;
    expect(stored).toEqual({ storage: 0, cookie: '', databases: 0 });
    const violations = await recorder.violations();
    report.cspViolations = violations;
    report.unexpectedConsoleErrors = unexpectedConsoleErrors(recorder.consoleErrors);
    if (target === 'preview') {
      expect(violations).toEqual([]);
    }

    expect(unexpectedConsoleErrors(recorder.consoleErrors)).toEqual([]);
  });
});
