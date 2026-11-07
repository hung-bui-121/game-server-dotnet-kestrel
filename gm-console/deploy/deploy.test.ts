import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { API_BASE_URL_PATTERN, validateConfig } from '../packages/shell/src/config/loadConfig';
import { API_ORIGIN_PLACEHOLDER, headersFor, loadSecurityHeaders } from './securityHeaders';

/** Slice 018 FR-080..FR-082: the nginx example, the CDN table and the config files agree with their single sources. */

const here = import.meta.dirname;
const read = (name: string) => readFileSync(resolve(here, name), 'utf8');
const set = loadSecurityHeaders();

/** The API origin the nginx example shows for `{{apiOrigin}}`. */
const EXAMPLE_API_ORIGIN = 'https://gm-api.beta.example.com';

/** The CSP directives of FR-081, in order. */
const FR081_DIRECTIVES = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self'",
  "connect-src 'self' {{apiOrigin}}",
  "img-src 'self' data:",
  "font-src 'self'",
  "manifest-src 'self'",
  "frame-ancestors 'none'",
  "base-uri 'none'",
  "form-action 'none'",
];

interface NginxLocation {
  location: string;
  headers: Record<string, string>;
  lines: number;
}

function parseNginx(text: string): NginxLocation[] {
  const locations: NginxLocation[] = [];
  for (const match of text.matchAll(/^\s*location\s+(=\s+)?(\S+)\s*\{([\s\S]*?)^\s*\}/gm)) {
    const body = match[3] ?? '';
    const headers: Record<string, string> = {};
    for (const header of body.matchAll(/add_header\s+(\S+)\s+"([^"]*)"\s+always;/g)) {
      headers[header[1]!] = header[2]!.replaceAll(EXAMPLE_API_ORIGIN, API_ORIGIN_PLACEHOLDER);
    }

    locations.push({ location: (match[1] ? '= ' : '') + match[2], headers, lines: (body.match(/add_header/g) ?? []).length });
  }

  return locations;
}

function parseTable(text: string): { pathClass: string; paths: string; header: string; value: string }[] {
  return text
    .split('\n')
    .filter((line) => /^\| (all|document|asset) \|/.test(line))
    .map((line) => {
      const [pathClass = '', paths = '', header = '', value = ''] = line.split(' | ').map((cell) => cell.replace(/^\|\s*|\s*\|$/g, '').trim());
      return { pathClass, paths, header, value: value.replace(/^`|`$/g, '') };
    });
}

/** The few JSON Schema 2020-12 keywords config.schema.json uses (no schema library, plan N2). */
function schemaErrors(schema: Record<string, unknown>, value: unknown): string[] {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return ['not an object'];
  }

  const record = value as Record<string, unknown>;
  const errors = (schema.required as string[]).filter((name) => !(name in record)).map((name) => `${name} required`);
  for (const [name, rule] of Object.entries(schema.properties as Record<string, Record<string, unknown>>)) {
    const member = record[name];
    if (member === undefined) {
      continue;
    }

    if (typeof member !== 'string') {
      errors.push(`${name} type`);
      continue;
    }

    if (typeof rule.minLength === 'number' && member.length < rule.minLength) errors.push(`${name} minLength`);
    if (typeof rule.maxLength === 'number' && member.length > rule.maxLength) errors.push(`${name} maxLength`);
    if (typeof rule.pattern === 'string' && !new RegExp(rule.pattern).test(member)) errors.push(`${name} pattern`);
  }

  return errors;
}

describe('security-headers.json (FR-081)', () => {
  it('has exactly the CSP directives of FR-081', () => {
    expect(set.all['Content-Security-Policy']!.split(';').map((d) => d.trim())).toEqual(FR081_DIRECTIVES);
  });

  it('has the other headers of FR-081 and the two cache classes', () => {
    expect(Object.keys(set.all)).toEqual([
      'Content-Security-Policy',
      'Strict-Transport-Security',
      'X-Content-Type-Options',
      'Referrer-Policy',
      'Cross-Origin-Opener-Policy',
      'Permissions-Policy',
    ]);
    expect(set.document).toEqual({ paths: ['/', '/index.html', '/config.json'], headers: { 'Cache-Control': 'no-cache' } });
    expect(set.asset).toEqual({ paths: ['/assets/*'], headers: { 'Cache-Control': 'public, max-age=31536000, immutable' } });
  });

  it('gives each path its headers with the API origin filled', () => {
    expect(headersFor(set, '/index.html', 'http://localhost:5080')['Cache-Control']).toBe('no-cache');
    expect(headersFor(set, '/assets/index-abc.js', 'http://localhost:5080')['Cache-Control']).toBe('public, max-age=31536000, immutable');
    expect(headersFor(set, '/other', 'http://localhost:5080')['Cache-Control']).toBeUndefined();
    expect(headersFor(set, '/', 'http://localhost:5080')['Content-Security-Policy']).toContain("connect-src 'self' http://localhost:5080;");
  });
});

describe('nginx.conf.example equals security-headers.json (FR-082)', () => {
  const locations = parseNginx(read('nginx.conf.example'));
  const withAll = (extra: Record<string, string> = {}) => ({ ...set.all, ...extra });

  it('has one location per path class with exactly its headers, all with "always"', () => {
    expect(locations.map((l) => l.location)).toEqual(['= /', '= /index.html', '= /config.json', '/assets/', '/']);
    for (const location of locations) {
      expect(Object.keys(location.headers)).toHaveLength(location.lines);
    }

    for (const name of ['= /', '= /index.html', '= /config.json']) {
      expect(locations.find((l) => l.location === name)?.headers).toEqual(withAll(set.document.headers));
    }

    expect(locations.find((l) => l.location === '/assets/')?.headers).toEqual(withAll(set.asset.headers));
    expect(locations.find((l) => l.location === '/')?.headers).toEqual(withAll());
  });
});

describe('headers.md equals security-headers.json (FR-082)', () => {
  const rows = parseTable(read('headers.md'));

  it('lists every header of every class with its value and paths', () => {
    const expected = [
      ...Object.entries(set.all).map(([header, value]) => ({ pathClass: 'all', paths: 'every answer', header, value })),
      ...Object.entries(set.document.headers).map(([header, value]) => ({
        pathClass: 'document',
        paths: set.document.paths.map((p) => `\`${p}\``).join(', '),
        header,
        value,
      })),
      ...Object.entries(set.asset.headers).map(([header, value]) => ({
        pathClass: 'asset',
        paths: set.asset.paths.map((p) => `\`${p}\``).join(', '),
        header,
        value,
      })),
    ];

    expect(rows).toEqual(expected);
  });
});

describe('config.example.json and config.schema.json (FR-050, plan N2)', () => {
  const schema = JSON.parse(read('config.schema.json')) as Record<string, unknown>;
  const example = JSON.parse(read('config.example.json')) as unknown;

  it('the example satisfies the schema and the shell validator', () => {
    expect(schema.$schema).toBe('https://json-schema.org/draft/2020-12/schema');
    expect(schemaErrors(schema, example)).toEqual([]);
    expect(validateConfig(example)).toEqual({ ok: true, config: example });
  });

  it('the schema pattern is the shell pattern', () => {
    const properties = schema.properties as Record<string, { pattern?: string }>;
    expect(properties.apiBaseUrl?.pattern).toBe(API_BASE_URL_PATTERN);
  });

  it.each([
    { apiBaseUrl: 'https://gm-api.example.com?x=1', gameId: 'g', environmentId: 'e' },
    { apiBaseUrl: 'https://gm-api.example.com#x', gameId: 'g', environmentId: 'e' },
    { apiBaseUrl: 'https://user@gm-api.example.com', gameId: 'g', environmentId: 'e' },
    { apiBaseUrl: 'ftp://gm-api.example.com', gameId: 'g', environmentId: 'e' },
    { apiBaseUrl: 'HTTPS://gm-api.example.com', gameId: 'g', environmentId: 'e' },
    { apiBaseUrl: 'https://gm-api.example.com', gameId: '', environmentId: 'e' },
    { apiBaseUrl: 'https://gm-api.example.com', gameId: 'g', environmentId: 'e'.repeat(65) },
    { apiBaseUrl: 'https://gm-api.example.com', gameId: 'g' },
  ])('schema and shell both refuse %j', (config) => {
    expect(schemaErrors(schema, config)).not.toEqual([]);
    expect(validateConfig(config)).toMatchObject({ ok: false });
  });
});
