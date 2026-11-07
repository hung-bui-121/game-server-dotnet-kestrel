// Typed GM API (slice 018 FR-044, research R9): openapi-typescript over the committed GM document.
//   node scripts/gen-api.mjs           writes packages/shell/src/generated/gm-api.d.ts
//   node scripts/gen-api.mjs --check   fails when the file on disk is not the generator's output, or differs from git
// Output is normalized to LF (the repository stores text as LF, .gitattributes).
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const workspace = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const input = resolve(workspace, '../docs/api/gm-openapi.json');
const output = resolve(workspace, 'packages/shell/src/generated/gm-api.d.ts');
const outputName = relative(workspace, output).replaceAll('\\', '/');
const check = process.argv.includes('--check');

function generate() {
  const require = createRequire(import.meta.url);
  const packageFile = require.resolve('openapi-typescript/package.json');
  const bin = JSON.parse(readFileSync(packageFile, 'utf8')).bin;
  const cli = join(dirname(packageFile), typeof bin === 'string' ? bin : bin['openapi-typescript']);
  const folder = mkdtempSync(join(tmpdir(), 'gm-api-'));
  try {
    const target = join(folder, 'gm-api.d.ts');
    execFileSync(process.execPath, [cli, input, '--output', target], { stdio: ['ignore', 'ignore', 'inherit'] });
    return readFileSync(target, 'utf8').replace(/\r\n/g, '\n');
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

const generated = generate();

if (!check) {
  writeFileSync(output, generated);
  console.log(`gen:api: wrote ${outputName} from docs/api/gm-openapi.json`);
  process.exit(0);
}

const current = existsSync(output) ? readFileSync(output, 'utf8') : null;
if (current !== generated) {
  writeFileSync(output, generated);
  console.error(
    `check:api: ${outputName} is stale (it is not the openapi-typescript output of docs/api/gm-openapi.json). ` +
      'It has been regenerated: review and commit it.',
  );
  process.exit(1);
}

try {
  execFileSync('git', ['diff', '--exit-code', '--stat', '--', output], { cwd: workspace, stdio: 'inherit' });
} catch {
  console.error(`check:api: ${outputName} differs from the committed file: commit the regenerated types.`);
  process.exit(1);
}

console.log(`check:api: ${outputName} is up to date.`);
