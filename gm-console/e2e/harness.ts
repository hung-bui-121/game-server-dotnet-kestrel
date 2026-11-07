/**
 * E2E harness (slice 018 T054, data-model §11, A7): checks the GM API, starts Vite (dev server or built preview with the
 * reference headers) on localhost:5173, launches the **system** browser through playwright-core (never a download), and
 * records what the browser sent and saw: requests with their real headers, CORS preflights, raw statuses (304), CSP
 * violations and console errors. The .NET host is started outside the test (tools/local/run-local.ps1).
 */
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium, type Browser, type BrowserContext, type CDPSession, type Page } from 'playwright-core';
import { build, createServer, preview } from 'vite';

export type Target = 'dev' | 'preview';

export const CONSOLE_URL = 'http://localhost:5173/';
export const CONSOLE_ORIGIN = 'http://localhost:5173';
/** The API origin of the committed local config.json (app/public/config.json) and of run-local's default port. */
export const DEFAULT_API = 'http://localhost:5080';
export const API = (process.env.GM_E2E_API ?? DEFAULT_API).replace(/\/+$/, '');
export const TARGETS = (process.env.GM_E2E_TARGET ?? 'dev,preview').split(',').map((t) => t.trim()) as Target[];
export const CHROME = 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe';
export const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
export const BROWSER = process.env.GM_E2E_BROWSER ?? CHROME;
export const STAFF = process.env.GM_E2E_STAFF ?? 'admin';
export const PASSWORD = process.env.GM_E2E_PASSWORD ?? 'dev-only-change-me';

const APP = resolve(import.meta.dirname, '../app');
const LOCAL_CONFIG = resolve(APP, 'public/config.json');
const CONFIG_FILE = resolve(APP, 'vite.config.ts');
export const ARTIFACTS = resolve(import.meta.dirname, '.artifacts');
const API_PROBE_TIMEOUT_MS = 5_000;

/** Fails with the hint when the GM API does not answer (the host is started outside the test). */
export async function checkApi(): Promise<void> {
  try {
    const response = await fetch(`${API}/gm/v1/auth/methods`, { signal: AbortSignal.timeout(API_PROBE_TIMEOUT_MS) });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
  } catch (error) {
    throw new Error(
      `The GM API does not answer at ${API} (${error instanceof Error ? error.message : String(error)}): ` +
        'start tools/local/run-local.ps1 (Local flavor) first, or set GM_E2E_API.',
      { cause: error },
    );
  }
}

/**
 * Why a target cannot run against `GM_E2E_API` (review T068 m6): the dev server always serves app/public/config.json, which
 * calls ${DEFAULT_API}; the preview target serves a config.json written for `GM_E2E_API`.
 */
export function targetSkipReason(target: Target): string | undefined {
  return target === 'dev' && API !== DEFAULT_API
    ? `dev target skipped: it serves app/public/config.json (${DEFAULT_API}); GM_E2E_API=${API} runs on the preview target only`
    : undefined;
}

/** The config.json the preview target serves: the local game and environment ids with `apiBaseUrl` = `GM_E2E_API`. */
export function writeE2eConfig(): string {
  const local = JSON.parse(readFileSync(LOCAL_CONFIG, 'utf8')) as Record<string, unknown>;
  mkdirSync(ARTIFACTS, { recursive: true });
  const file = resolve(ARTIFACTS, 'config.json');
  writeFileSync(file, JSON.stringify({ ...local, apiBaseUrl: API }, null, 2));
  return file;
}

/** Starts the console on 5173 (strict port); returns its close function. The preview target builds first. */
export async function startTarget(target: Target): Promise<() => Promise<void>> {
  process.env.GM_E2E_API = API;
  if (target === 'dev') {
    const server = await createServer({ root: APP, configFile: CONFIG_FILE, logLevel: 'warn' });
    await server.listen();
    return () => server.close();
  }

  process.env.GM_CONSOLE_CONFIG = writeE2eConfig();
  await build({ root: APP, configFile: CONFIG_FILE, logLevel: 'warn' });
  const server = await preview({ root: APP, configFile: CONFIG_FILE, logLevel: 'warn' });
  return () =>
    new Promise<void>((done, fail) => {
      server.httpServer.close((error) => (error ? fail(error) : done()));
    });
}

/** The system browser (GM_E2E_BROWSER, default Chrome); a clear error when the file is missing. Never downloads. */
export async function launchBrowser(): Promise<Browser> {
  if (!existsSync(BROWSER)) {
    throw new Error(`Browser not found at ${BROWSER}. Set GM_E2E_BROWSER to Chrome or Edge (e.g. ${EDGE}); the smoke never downloads a browser.`);
  }

  return chromium.launch({ executablePath: BROWSER, headless: true });
}

export interface SentRequest {
  id: string;
  method: string;
  url: string;
  type: string;
  headers: Record<string, string>;
  status?: number;
  responseHeaders: Record<string, string>;
}

export interface ConsoleRecorder {
  page: Page;
  cdp: CDPSession;
  /** Every network request of the page as Chrome sent it (CORS preflights included), in order. */
  requests: SentRequest[];
  consoleErrors: string[];
  /** `securitypolicyviolation` events collected in the page so far. */
  violations(): Promise<{ directive: string; blocked: string; source: string }[]>;
  /** Requests to the GM API (path + method), preflights as `OPTIONS`. */
  apiCalls(): string[];
}

function lower(headers: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(headers).map(([name, value]) => [name.toLowerCase(), value]));
}

/** A page of the console with the CSP collector (init script), console errors and a CDP network log. */
export async function openRecorder(context: BrowserContext): Promise<ConsoleRecorder> {
  await context.addInitScript(() => {
    const events: { directive: string; blocked: string; source: string }[] = [];
    (window as unknown as { __gmCsp: typeof events }).__gmCsp = events;
    document.addEventListener('securitypolicyviolation', (e) => {
      events.push({ directive: e.violatedDirective, blocked: e.blockedURI, source: e.sourceFile });
    });
  });
  const page = await context.newPage();
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      consoleErrors.push(message.text());
    }
  });
  page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));

  const cdp = await context.newCDPSession(page);
  const requests: SentRequest[] = [];
  const byId = new Map<string, SentRequest>();
  const entry = (id: string) => {
    let request = byId.get(id);
    if (!request) {
      request = { id, method: '', url: '', type: '', headers: {}, responseHeaders: {} };
      byId.set(id, request);
      requests.push(request);
    }

    return request;
  };
  cdp.on('Network.requestWillBeSent', (event) => {
    const request = entry(event.requestId);
    request.method = event.request.method;
    request.url = event.request.url;
    request.type = event.type ?? '';
    request.headers = { ...lower(event.request.headers), ...request.headers };
  });
  cdp.on('Network.requestWillBeSentExtraInfo', (event) => {
    const request = entry(event.requestId);
    request.headers = { ...request.headers, ...lower(event.headers) };
  });
  cdp.on('Network.responseReceivedExtraInfo', (event) => {
    const request = entry(event.requestId);
    request.status = event.statusCode;
    request.responseHeaders = lower(event.headers);
  });
  await cdp.send('Network.enable');

  return {
    page,
    cdp,
    requests,
    consoleErrors,
    violations: () => page.evaluate(() => (window as unknown as { __gmCsp: { directive: string; blocked: string; source: string }[] }).__gmCsp),
    apiCalls: () =>
      requests.filter((r) => r.url.startsWith(API)).map((r) => `${r.method} ${new URL(r.url).pathname}`),
  };
}

/**
 * Console errors that are not the browser's own log of an HTTP error the smoke causes on purpose (401 of `/me` when signed
 * out, 404 of the unknown player): Chrome always logs those as "Failed to load resource", and no page code can silence it.
 */
export function unexpectedConsoleErrors(errors: string[]): string[] {
  return errors.filter((text) => !/^Failed to load resource: the server responded with a status of (401|404)\b/.test(text));
}

/** Writes a JSON report next to the smoke (e2e/.artifacts, never committed). */
export function writeReport(name: string, report: unknown): void {
  mkdirSync(ARTIFACTS, { recursive: true });
  writeFileSync(resolve(ARTIFACTS, `${name}.json`), JSON.stringify(report, null, 2));
}
