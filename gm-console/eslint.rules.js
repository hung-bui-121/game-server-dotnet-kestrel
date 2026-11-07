// Security and boundary lint rules of the GM console (slice 018 FR-048, FR-074; review T068 M1, m4). Shared by eslint.config.js
// and eslint.rules.test.ts, so the test lints its fixtures with exactly these selectors.

const STORAGE_APIS = ['localStorage', 'sessionStorage', 'indexedDB'];
const GLOBAL_OBJECTS = ['window', 'globalThis', 'self'];
const STORAGE_MESSAGE = 'The GM console keeps session, staff and player data in memory only (FR-061): no browser storage.';
const HTML_MESSAGE = 'Server data is rendered as text only (FR-062, FR-076, SEC-12).';
const MUTATION_MESSAGE =
  'GM-2 boundary (D10, FR-074): only session/ sends the sign-in and sign-out mutations; other changes come with the GM-3 forms.';

/** No HTML from strings, no storage through computed access (every file). */
export const securitySyntax = [
  { selector: "JSXAttribute[name.name=/^(dangerouslySetInnerHTML|srcDoc)$/]", message: HTML_MESSAGE },
  { selector: "MemberExpression[property.name=/^(innerHTML|outerHTML|insertAdjacentHTML|srcdoc)$/]", message: HTML_MESSAGE },
  {
    selector: "MemberExpression[computed=true][property.value=/^(innerHTML|outerHTML|insertAdjacentHTML|srcdoc|cookie|write|writeln)$/]",
    message: HTML_MESSAGE,
  },
  { selector: "MemberExpression[computed=true][property.value=/^(localStorage|sessionStorage|indexedDB)$/]", message: STORAGE_MESSAGE },
];

/** Storage globals and document sinks (every file). */
export const restrictedGlobals = STORAGE_APIS.map((name) => ({ name, message: STORAGE_MESSAGE }));

export const restrictedProperties = [
  ...GLOBAL_OBJECTS.flatMap((object) => STORAGE_APIS.map((property) => ({ object, property, message: STORAGE_MESSAGE }))),
  { object: 'document', property: 'cookie', message: 'The console never reads or writes cookies (HttpOnly cookies of the API host, FR-061).' },
  { object: 'document', property: 'write', message: HTML_MESSAGE },
  { object: 'document', property: 'writeln', message: HTML_MESSAGE },
];

const METHOD_NAMES = '/^(POST|PUT|DELETE|PATCH)$/';

/** Destructuring a method out of the client (`const { POST } = api`), not function parameters such as `({ request })`. */
const destructured = ['VariableDeclarator', 'AssignmentExpression'].map((parent) => ({
  selector: `${parent} > ObjectPattern > Property[key.name=/^(POST|PUT|DELETE|PATCH|request)$/]`,
  message: MUTATION_MESSAGE,
}));

/** A call through a computed, non-literal member (`api[m](…)`) hides which method it is. */
const computedCall = {
  selector: "CallExpression > MemberExpression.callee[computed=true][property.type!='Literal']",
  message: MUTATION_MESSAGE,
};

/** Any use of a mutation member, called or not (`api.POST(…)`, `api.POST.call(api, …)`, `const p = api.request`), shell outside session/. */
const anyMutation = [
  { selector: `MemberExpression[property.name=${METHOD_NAMES}]`, message: MUTATION_MESSAGE },
  { selector: "MemberExpression[property.name='request']", message: MUTATION_MESSAGE },
  ...destructured,
  computedCall,
];

/** Forms that hide the method from a reader or a scan (shell source, session/ included). */
const hiddenMutation = [
  { selector: "MemberExpression[computed=true][property.value=/^(POST|PUT|DELETE|PATCH|request)$/i]", message: MUTATION_MESSAGE },
  { selector: "MemberExpression[computed=true][property.type='TemplateLiteral']", message: MUTATION_MESSAGE },
  { selector: "Property[key.name='method'][value.type='Literal'][value.value=/^(post|put|delete|patch)$/i]", message: MUTATION_MESSAGE },
  { selector: "Property[key.name='method'][value.type='TemplateLiteral']", message: MUTATION_MESSAGE },
];

/** session/: only `.POST('/gm/v1/auth/login' | '/gm/v1/auth/logout', …)` with a literal path; no other method, no `.request(`. */
const sessionMutation = [
  { selector: "MemberExpression[property.name=/^(PUT|DELETE|PATCH|request)$/]", message: MUTATION_MESSAGE },
  {
    selector:
      "MemberExpression[property.name='POST']:not(CallExpression[arguments.0.value='/gm/v1/auth/login'] > MemberExpression.callee):not(CallExpression[arguments.0.value='/gm/v1/auth/logout'] > MemberExpression.callee)",
    message: MUTATION_MESSAGE,
  },
  ...destructured,
  computedCall,
];

/** Raw `fetch` would bypass the typed client and its guard: only the client, the config loader and the session's default may use it. */
const FETCH_MESSAGE = 'API calls go through the typed client (api/client.ts) and its GM-2 guard (review T068 r2 n3): no raw fetch.';
const fetchGlobals = [...restrictedGlobals, { name: 'fetch', message: FETCH_MESSAGE }];
const fetchProperties = [...restrictedProperties, ...GLOBAL_OBJECTS.map((object) => ({ object, property: 'fetch', message: FETCH_MESSAGE }))];
const FETCH_ALLOWED = ['packages/shell/src/api/client.ts', 'packages/shell/src/config/loadConfig.ts', 'packages/shell/src/session/SessionProvider.tsx'];

/** Shell production source (tests assert the rules and may build forbidden calls on purpose). */
const SHELL_SOURCE = ['packages/shell/src/**/*.{ts,tsx}'];
const SHELL_TESTS = ['packages/shell/src/**/*.test.{ts,tsx}', 'packages/shell/src/test/**'];
const SESSION_SOURCE = ['packages/shell/src/session/**/*.{ts,tsx}'];

/**
 * Flat-config blocks of the security and boundary rules (no parser settings; eslint.config.js adds those).
 * @returns {import('eslint').Linter.Config[]}
 */
export function boundaryConfigs() {
  return [
    {
      rules: {
        'no-eval': 'error',
        'no-implied-eval': 'error',
        'no-new-func': 'error',
        'no-restricted-syntax': ['error', ...securitySyntax],
        'no-restricted-globals': ['error', ...restrictedGlobals],
        'no-restricted-properties': ['error', ...restrictedProperties],
      },
    },
    {
      files: SHELL_SOURCE,
      ignores: SHELL_TESTS,
      rules: {
        'no-restricted-syntax': ['error', ...securitySyntax, ...anyMutation, ...hiddenMutation],
        'no-restricted-globals': ['error', ...fetchGlobals],
        'no-restricted-properties': ['error', ...fetchProperties],
      },
    },
    {
      files: SESSION_SOURCE,
      ignores: SHELL_TESTS,
      rules: { 'no-restricted-syntax': ['error', ...securitySyntax, ...sessionMutation, ...hiddenMutation] },
    },
    {
      files: FETCH_ALLOWED,
      rules: {
        'no-restricted-globals': ['error', ...restrictedGlobals],
        'no-restricted-properties': ['error', ...restrictedProperties],
      },
    },
  ];
}
