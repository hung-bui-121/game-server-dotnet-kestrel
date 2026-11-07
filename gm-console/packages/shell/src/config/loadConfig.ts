import type { GmConsoleConfig } from '../types';

/** The console's configuration file, next to `index.html` on the static host (FR-050, data-model §5). */
export const CONFIG_FILE = 'config.json';

/** Longest accepted `gameId` / `environmentId`. */
export const MAX_ID_LENGTH = 64;

export type ConfigResult =
  | { ok: true; config: GmConsoleConfig }
  | { ok: false; field: string; message: string };

const ALLOWED_PROTOCOLS: ReadonlySet<string> = new Set(['http:', 'https:']);

/** `apiBaseUrl` pattern, identical to `deploy/config.schema.json` (deploy.test.ts compares them). */
export const API_BASE_URL_PATTERN = '^https?://[^/?#@\\s]+(/[^?#\\s]*)?$';
const SCHEMA_PATTERN = new RegExp(API_BASE_URL_PATTERN);

function fail(field: string, message: string): ConfigResult {
  return { ok: false, field, message };
}

function validateId(record: Record<string, unknown>, field: 'gameId' | 'environmentId'): ConfigResult | string {
  const value = record[field];
  if (typeof value !== 'string' || value.length === 0 || value.length > MAX_ID_LENGTH) {
    return fail(field, `${field} must be a non-empty string of at most ${MAX_ID_LENGTH} characters.`);
  }

  return value;
}

/** The rules of FR-050 (also `deploy/config.schema.json`): a typed config, or the first wrong field. Unknown members are ignored. */
export function validateConfig(value: unknown): ConfigResult {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return fail(CONFIG_FILE, `${CONFIG_FILE} must be a JSON object.`);
  }

  const record = value as Record<string, unknown>;
  const apiBaseUrl = record.apiBaseUrl;
  const urlRule = 'apiBaseUrl must be an absolute http(s) URL without query, fragment or user info.';
  // Same rule as deploy/config.schema.json ("pattern"): lower-case scheme, no whitespace, '?', '#' or '@' in the authority.
  if (typeof apiBaseUrl !== 'string' || !SCHEMA_PATTERN.test(apiBaseUrl)) {
    return fail('apiBaseUrl', urlRule);
  }

  let url: URL;
  try {
    url = new URL(apiBaseUrl);
  } catch {
    return fail('apiBaseUrl', urlRule);
  }

  if (!ALLOWED_PROTOCOLS.has(url.protocol) || url.username !== '' || url.password !== '') {
    return fail('apiBaseUrl', urlRule);
  }

  const gameId = validateId(record, 'gameId');
  if (typeof gameId !== 'string') {
    return gameId;
  }

  const environmentId = validateId(record, 'environmentId');
  if (typeof environmentId !== 'string') {
    return environmentId;
  }

  return { ok: true, config: { apiBaseUrl: apiBaseUrl.replace(/\/+$/, ''), gameId, environmentId } };
}

/**
 * Loads `config.json` from the console's own location (relative to the document, so a console under a sub-path works) with
 * `cache: 'no-store'`, then validates it. Makes exactly one request and never calls the API (FR-050, US6-1).
 */
export async function loadConfig(fetchFn: typeof fetch = fetch, baseUri: string = document.baseURI): Promise<ConfigResult> {
  let response: Response;
  try {
    response = await fetchFn(new URL(CONFIG_FILE, baseUri).href, { cache: 'no-store' });
  } catch (error) {
    return fail(CONFIG_FILE, `${CONFIG_FILE} could not be loaded (${error instanceof Error ? error.message : String(error)}).`);
  }

  if (!response.ok) {
    return fail(CONFIG_FILE, `${CONFIG_FILE} could not be loaded (HTTP ${response.status}).`);
  }

  let value: unknown;
  try {
    value = await response.json();
  } catch {
    return fail(CONFIG_FILE, `${CONFIG_FILE} is not valid JSON.`);
  }

  return validateConfig(value);
}
