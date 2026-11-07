import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * GM-2/GM-3 boundary (D10, FR-074): the only mutations of the console are sign-in, sign-out (session/) and the CSRF token
 * fetch (a GET). A text scan on top of the lint rule (eslint.rules.js) and the client's run-time guard (review T068 M1).
 */
const SOURCE = import.meta.dirname;
const ALLOWED = ['session/'];
const SKIPPED = ['test/', 'generated/'];

/** openapi-fetch method calls (upper case, so `map.delete(` is not one), its untyped `.request(`, computed or template forms. */
const MUTATING_FORMS = [
  /\.(POST|PUT|DELETE|PATCH|request)\s*\(/,
  /\[\s*['"`](POST|PUT|DELETE|PATCH|request)['"`]\s*\]/i,
  /method\s*:\s*['"`]\s*(post|put|delete|patch)/i,
];

/** The two calls session/ may make, with a literal path. */
const SESSION_ALLOWED = ["POST '/gm/v1/auth/login'", "POST '/gm/v1/auth/logout'"];

function sourceFiles(folder: string): string[] {
  return readdirSync(folder, { withFileTypes: true }).flatMap((entry) => {
    const path = join(folder, entry.name);
    return entry.isDirectory() ? sourceFiles(path) : /\.tsx?$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name) ? [path] : [];
  });
}

describe('mutation boundary (D10, FR-074, US7-7)', () => {
  it('no module outside session/ sends POST, PUT, DELETE or PATCH in any form', () => {
    const offenders = sourceFiles(SOURCE)
      .map((file) => relative(SOURCE, file).split(sep).join('/'))
      .filter((file) => !ALLOWED.some((a) => file.startsWith(a)) && !SKIPPED.some((s) => file.startsWith(s)))
      .filter((file) => {
        const text = readFileSync(join(SOURCE, file), 'utf8');
        return MUTATING_FORMS.some((form) => form.test(text));
      });

    expect(offenders).toEqual([]);
  });

  it('session/ sends exactly the sign-in and sign-out mutations, each with a literal path and no other form', () => {
    const session = sourceFiles(join(SOURCE, 'session'))
      .map((file) => readFileSync(file, 'utf8'))
      .join('\n');

    const calls = [...session.matchAll(/\.(POST|PUT|DELETE|PATCH|request)\s*\(\s*([^,)]*)/g)].map((m) => `${m[1]} ${(m[2] ?? '').trim()}`);
    expect(calls.sort()).toEqual(SESSION_ALLOWED);
    expect(MUTATING_FORMS.slice(1).some((form) => form.test(session))).toBe(false);
  });
});
