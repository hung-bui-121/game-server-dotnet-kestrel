/**
 * Reads `security-headers.json`, the one source of the static host's headers (slice 018 FR-080, research R12), for the Vite
 * preview server (T052) and the consistency test. Node only.
 */
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

export interface PathClass {
  paths: string[];
  headers: Record<string, string>;
}

export interface SecurityHeaderSet {
  placeholders: Record<string, string>;
  all: Record<string, string>;
  document: PathClass;
  asset: PathClass;
}

export const SECURITY_HEADERS_FILE = resolve(import.meta.dirname, 'security-headers.json');

/** Placeholder of the API origin in the CSP `connect-src`. */
export const API_ORIGIN_PLACEHOLDER = '{{apiOrigin}}';

export function loadSecurityHeaders(file: string = SECURITY_HEADERS_FILE): SecurityHeaderSet {
  return JSON.parse(readFileSync(file, 'utf8')) as SecurityHeaderSet;
}

function matches(pattern: string, pathname: string): boolean {
  return pattern.endsWith('/*') ? pathname.startsWith(pattern.slice(0, -1)) : pathname === pattern;
}

/** `document`, `asset`, or undefined (any other path: the `all` headers only). */
export function pathClassOf(set: SecurityHeaderSet, pathname: string): 'document' | 'asset' | undefined {
  if (set.document.paths.some((p) => matches(p, pathname))) {
    return 'document';
  }

  return set.asset.paths.some((p) => matches(p, pathname)) ? 'asset' : undefined;
}

/** The headers of one answer, with `{{apiOrigin}}` filled. */
export function headersFor(set: SecurityHeaderSet, pathname: string, apiOrigin: string): Record<string, string> {
  const pathClass = pathClassOf(set, pathname);
  const headers = { ...set.all, ...(pathClass ? set[pathClass].headers : {}) };
  return Object.fromEntries(Object.entries(headers).map(([name, value]) => [name, value.replaceAll(API_ORIGIN_PLACEHOLDER, apiOrigin)]));
}
