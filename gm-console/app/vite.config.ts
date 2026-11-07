// Vite config of the default GM console app (slice 018): dev server and preview on localhost:5173 (FR-045), environment-free
// build (FR-084) with relative asset URLs (a console may live under a sub-path), the build stamp (FR-056), and a preview that
// serves the reference static-host headers (FR-082).
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { gmStaticHost } from './vite-plugins/staticHost.ts';

const CONSOLE_HOST = 'localhost';
const CONSOLE_PORT = 5173;
/** Minified size warning of Vite (kB); the real budget is check:dist's 500 KB gzip (SC-007). */
const CHUNK_WARNING_KB = 800;

/** `frameworkVersion` = the shell package version (= GameServerFrameworkVersion); `gameVersion` from GM_CONSOLE_GAME_VERSION. */
function builtFor(): { frameworkVersion: string; gameVersion?: string } {
  const shellPackage = createRequire(import.meta.url).resolve('@gameserver/gm-shell/package.json');
  const { version } = JSON.parse(readFileSync(shellPackage, 'utf8')) as { version: string };
  const gameVersion = process.env.GM_CONSOLE_GAME_VERSION;
  return gameVersion ? { frameworkVersion: version, gameVersion } : { frameworkVersion: version };
}

export default defineConfig({
  base: './',
  plugins: [react(), gmStaticHost()],
  define: { __GM_BUILT_FOR__: JSON.stringify(builtFor()) },
  server: { host: CONSOLE_HOST, port: CONSOLE_PORT, strictPort: true },
  preview: { host: CONSOLE_HOST, port: CONSOLE_PORT, strictPort: true },
  // public/ (the local config.json) serves the dev loop only: dist/ never contains a config.json (FR-083).
  build: { copyPublicDir: false, chunkSizeWarningLimit: CHUNK_WARNING_KB },
});
