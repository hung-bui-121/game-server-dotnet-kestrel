// Bundle check of the built console (slice 018 FR-083, SC-007): node scripts/check-dist.mjs <dist>
// Fails on: an inline <script> in index.html; a src/href that is not a relative or root path; any file containing the local
// API origin (localhost:5080); any config.json (the API origin comes only from the deployed config.json); JS+CSS loaded by
// index.html above 500 KB gzip. Prints the gzip size.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, join, relative, resolve } from 'node:path';
import { gzipSync } from 'node:zlib';

const BUDGET_BYTES = 500 * 1024;
const ENVIRONMENT_VALUES = ['localhost:5080'];

const dist = resolve(process.argv[2] ?? 'app/dist');
const failures = [];

function files(folder) {
  return readdirSync(folder).flatMap((name) => {
    const path = join(folder, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

let html;
try {
  html = readFileSync(join(dist, 'index.html'), 'utf8');
} catch {
  console.error(`check:dist: ${join(dist, 'index.html')} not found (run npm run build first).`);
  process.exit(1);
}

for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
  if (!/\bsrc\s*=/.test(match[1]) || match[2].trim() !== '') {
    failures.push(`index.html has an inline <script> (SEC-11: script-src 'self' only): ${match[0].slice(0, 80)}`);
  }
}

const references = [...html.matchAll(/\b(src|href)\s*=\s*["']([^"']*)["']/gi)].map((m) => m[2]);
for (const url of references) {
  const relativeOrRoot = (url.startsWith('/') && !url.startsWith('//')) || (!/^[a-z][a-z0-9+.-]*:/i.test(url) && !url.startsWith('//'));
  if (!relativeOrRoot) {
    failures.push(`index.html references '${url}': only relative or root paths are allowed (no third-party or data: URL).`);
  }
}

for (const file of files(dist)) {
  const name = relative(dist, file).replaceAll('\\', '/');
  if (basename(file) === 'config.json') {
    failures.push(`${name}: a config.json is in the build; it is deployed per environment, never built in (FR-084).`);
  }

  const text = readFileSync(file, 'latin1');
  for (const value of ENVIRONMENT_VALUES) {
    if (text.includes(value)) {
      failures.push(`${name} contains '${value}': the build must be environment-free (FR-084).`);
    }
  }
}

const loaded = [
  ...[...html.matchAll(/<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["']/gi)].map((m) => m[1]),
  ...[...html.matchAll(/<link\b[^>]*>/gi)]
    .map((m) => m[0])
    .filter((tag) => /\brel\s*=\s*["'](stylesheet|modulepreload)["']/i.test(tag))
    .map((tag) => /\bhref\s*=\s*["']([^"']+)["']/i.exec(tag)?.[1])
    .filter(Boolean),
];
let gzipBytes = 0;
for (const url of loaded) {
  const path = join(dist, url.replace(/^\.?\//, ''));
  try {
    gzipBytes += gzipSync(readFileSync(path)).length;
  } catch {
    failures.push(`index.html loads '${url}', which is not in the build.`);
  }
}

const kb = (gzipBytes / 1024).toFixed(1);
console.log(`check:dist: ${loaded.length} JS/CSS files loaded by index.html, ${kb} KB gzip (budget ${BUDGET_BYTES / 1024} KB).`);
if (gzipBytes > BUDGET_BYTES) {
  failures.push(`JS+CSS loaded by index.html is ${kb} KB gzip, above the ${BUDGET_BYTES / 1024} KB budget (SC-007).`);
}

if (failures.length > 0) {
  for (const failure of failures) {
    console.error(`check:dist: ${failure}`);
  }

  process.exit(1);
}

console.log('check:dist: ok');
