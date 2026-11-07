/**
 * `vite preview` as the reference static host (slice 018 FR-082, V11, T052): every answer gets the headers of
 * `deploy/security-headers.json` for its path class, and `/config.json` is served from a file like a deployment adds it
 * (the build itself never contains one, FR-083). Preview only; the dev server stays header-free (Vite's dev client needs
 * inline scripts).
 *
 * Environment: `GM_CONSOLE_CONFIG` = the config.json to serve (default `app/public/config.json`); `GM_E2E_API` = the API
 * origin for the CSP `connect-src` (default: the origin of that config's `apiBaseUrl`).
 */
import { readFileSync } from 'node:fs';
import type { ServerResponse } from 'node:http';
import { resolve } from 'node:path';
import type { Plugin } from 'vite';
import { headersFor, loadSecurityHeaders } from '../../deploy/securityHeaders.ts';

const CONFIG_PATH = '/config.json';
const DEFAULT_CONFIG = resolve(import.meta.dirname, '../public/config.json');

export interface StaticHostSettings {
  configFile: string;
  apiOrigin: string;
}

export function staticHostSettings(env: NodeJS.ProcessEnv = process.env): StaticHostSettings {
  const configFile = env.GM_CONSOLE_CONFIG ? resolve(env.GM_CONSOLE_CONFIG) : DEFAULT_CONFIG;
  const config = JSON.parse(readFileSync(configFile, 'utf8')) as { apiBaseUrl?: string };
  const apiOrigin = new URL(env.GM_E2E_API ?? config.apiBaseUrl ?? '').origin;
  return { configFile, apiOrigin };
}

/**
 * Applies the headers when the answer is written, so no later middleware drops them: the static-file middleware passes its own
 * headers (e.g. `Cache-Control`) to `writeHead(status, headers)`, which would win over `setHeader`, so those are overridden.
 */
function withHeaders(res: ServerResponse, headers: Record<string, string>): void {
  const writeHead = res.writeHead.bind(res) as (...args: unknown[]) => ServerResponse;
  const ours = new Set(Object.keys(headers).map((name) => name.toLowerCase()));
  res.writeHead = (...args: unknown[]) => {
    for (const arg of args) {
      if (typeof arg === 'object' && arg !== null && !Array.isArray(arg)) {
        for (const name of Object.keys(arg)) {
          if (ours.has(name.toLowerCase())) {
            delete (arg as Record<string, unknown>)[name];
          }
        }
      }
    }

    for (const [name, value] of Object.entries(headers)) {
      res.setHeader(name, value);
    }

    return writeHead(...args);
  };
}

export function gmStaticHost(): Plugin {
  return {
    name: 'gm-static-host',
    configurePreviewServer(server) {
      const set = loadSecurityHeaders();
      const settings = staticHostSettings();
      server.middlewares.use((req, res, next) => {
        const pathname = new URL(req.url ?? '/', 'http://localhost').pathname;
        withHeaders(res, headersFor(set, pathname, settings.apiOrigin));
        if (pathname === CONFIG_PATH) {
          res.statusCode = 200;
          res.setHeader('Content-Type', 'application/json');
          res.end(readFileSync(settings.configFile));
          return;
        }

        next();
      });
    },
  };
}
