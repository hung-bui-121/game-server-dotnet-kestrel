import { resolve } from 'node:path';
import { Linter } from 'eslint';
import tseslint from 'typescript-eslint';
import { describe, expect, it } from 'vitest';
import { boundaryConfigs } from './eslint.rules.js';

/**
 * The security and GM-2 boundary lint rules on fixtures (FR-048, FR-074; review T068 M1, m4): each forbidden form is an error at
 * the path where it would be written, and the allowed forms are not.
 */
const linter = new Linter({ configType: 'flat', cwd: import.meta.dirname });
const config = [
  { files: ['**/*.{ts,tsx}'], languageOptions: { parser: tseslint.parser, parserOptions: { ecmaFeatures: { jsx: true } } } },
  ...boundaryConfigs(),
];

function errors(code: string, file: string): string[] {
  return linter
    .verify(code, config, { filename: resolve(import.meta.dirname, file) })
    .map((m) => `${m.ruleId ?? 'parse'}: ${m.message}`);
}

const PAGE = 'packages/shell/src/pages/Probe.tsx';
const SESSION = 'packages/shell/src/session/Probe.ts';
const SHELL_TEST = 'packages/shell/src/pages/Probe.test.tsx';

describe('GM-2 mutation boundary in shell source (M1)', () => {
  it.each([
    ["api.POST('/gm/v1/staff', {});", PAGE],
    ["api.PUT('/gm/v1/ops/gate/maintenance', {});", PAGE],
    ["api.DELETE('/x');", PAGE],
    ["api.PATCH('/x');", PAGE],
    ["api.request('post', '/gm/v1/players/{playerId}/ban', {});", PAGE],
    ["api['POST']('/gm/v1/staff', {});", PAGE],
    ['api[`POST`]("/gm/v1/staff", {});', PAGE],
    ["void fetch(url, { method: 'post' });", PAGE],
    ['void fetch(url, { method: `POST` });', PAGE],
    ["api.PUT('/gm/v1/auth/login', {});", SESSION],
    ["api.POST('/gm/v1/staff', {});", SESSION],
    ['api.POST(path, {});', SESSION],
    ['api.POST(`/gm/v1/auth/login`, {});', SESSION],
    ["api.request('post', '/gm/v1/auth/login', {});", SESSION],
    ["api['POST']('/gm/v1/auth/login', {});", SESSION],
    // r2 n3: bypasses of the call-only selectors.
    ["const { POST } = api; void POST('/gm/v1/staff', {});", PAGE],
    ["let r; ({ request: r } = api); void r;", PAGE],
    ["void api.POST.call(api, '/gm/v1/staff', {});", PAGE],
    ['const send = api.request; void send;', PAGE],
    ["void api[m]('/gm/v1/staff', {});", PAGE],
    ["const { POST } = api; void POST('/gm/v1/auth/login', {});", SESSION],
    ["void api.POST.call(api, '/gm/v1/auth/login', {});", SESSION],
    ["void api[m]('/gm/v1/auth/login', {});", SESSION],
  ])('%s in %s is an error', (code, file) => {
    expect(errors(`declare const api: any; declare const url: string; declare const path: string; declare const m: string;\n${code}`, file).join('\n')).toMatch(
      /no-restricted-syntax: GM-2 boundary/,
    );
  });

  it.each([
    ["api.GET('/gm/v1/staff', {});", PAGE],
    ['copies.delete(key);', PAGE],
    ["api.POST('/gm/v1/auth/login', { body });", SESSION],
    ["api.POST('/gm/v1/auth/logout');", SESSION],
    ["api.POST('/gm/v1/staff', {});", SHELL_TEST],
    ["api.POST('/gm/v1/staff', {});", 'e2e/probe.e2e.ts'],
    ['void fetch(url);', 'packages/shell/src/api/client.ts'],
    ['void globalThis.fetch(url);', 'packages/shell/src/session/SessionProvider.tsx'],
    ['void fetch(url);', 'packages/shell/src/config/loadConfig.ts'],
    ['export function onRequest({ request }: { request: Request }) { return request.url; }', 'packages/shell/src/api/client.ts'],
  ])('%s in %s is allowed', (code, file) => {
    expect(errors(`declare const api: any; declare const url: string; declare const copies: any; declare const key: string; declare const body: any;\n${code}`, file)).toEqual([]);
  });
});

describe('no raw fetch in shell source outside the client, config loader and session default (r2 n3)', () => {
  it.each([
    ["void fetch(url, { method: verb });", PAGE],
    ["void fetch(url, { method: 'GET' });", PAGE],
    ['void window.fetch(url);', PAGE],
    ['void globalThis.fetch(url);', 'packages/shell/src/navigation/Probe.ts'],
    ['void self.fetch(url);', 'packages/shell/src/session/csrf.ts'],
  ])('%s in %s is an error', (code, file) => {
    expect(errors(`declare const url: string; declare const verb: string;\n${code}`, file).join('\n')).toMatch(/typed client/);
  });
});

describe('storage and HTML sinks everywhere (FR-048, m4)', () => {
  it.each([
    'localStorage.getItem("x");',
    'window.sessionStorage.clear();',
    "globalThis['localStorage'].getItem('x');",
    "const w = window; void w['indexedDB'];",
    'self.indexedDB.open("x");',
    'void document.cookie;',
    'document.cookie = "a=b";',
    "document['cookie'] = 'a=b';",
    'document.write("<b>x</b>");',
    'document.writeln("x");',
    'element.innerHTML = "x";',
    'element.outerHTML = "x";',
    'element.insertAdjacentHTML("beforeend", "x");',
    'frame.srcdoc = "<b>x</b>";',
    "element['innerHTML'] = 'x';",
    'eval("1");',
    'new Function("return 1");',
  ])('%s is an error', (code) => {
    const found = errors(`declare const element: HTMLElement; declare const frame: HTMLIFrameElement;\n${code}`, 'packages/shell/src/components/Probe.ts');
    expect(found.length).toBeGreaterThan(0);
    expect(found.every((m) => !m.startsWith('parse'))).toBe(true);
  });

  it.each([
    ['<div dangerouslySetInnerHTML={{ __html: x }} />', 'packages/shell/src/components/Probe.tsx'],
    ['<iframe srcDoc={x} />', 'packages/shell/src/components/Probe.tsx'],
  ])('%s is an error', (jsx, file) => {
    expect(errors(`declare const x: string;\nexport const y = ${jsx};`, file).join('\n')).toMatch(/no-restricted-syntax/);
  });

  it('allows text rendering', () => {
    expect(errors('declare const element: HTMLElement;\nelement.textContent = "x";', 'packages/shell/src/components/Probe.ts')).toEqual([]);
  });
});
