import createClient, { type Client, type Middleware } from 'openapi-fetch';
import type { paths } from '../generated/gm-api';
import type { GmConsoleConfig, GmProblem } from '../types';
import { gmPathSerializer, InvalidPathParameterError } from './paths';
import {
  CSRF_FAILED,
  GmApiError,
  INVALID_PATH_PARAMETER,
  MUTATION_NOT_ALLOWED,
  NO_ANSWER_STATUS,
  networkProblem,
  problemFromResponse,
  problemOf,
} from './problem';

/** Header carrying the antiforgery token on every non-GET call (SEC-05, FR-051). */
export const CSRF_HEADER = 'X-CSRF-Token';

/** Methods that never carry the token nor any added header: they stay CORS simple requests (no preflight, FR-051). */
const SAFE_METHODS: ReadonlySet<string> = new Set(['GET', 'HEAD']);

/**
 * GM-2 mutation boundary at run time (D10, FR-074; review T068 M1): the only non-GET calls are sign-in and sign-out. Any other
 * mutation is refused before it is sent, whatever API was used to build it. GM-3 replaces this allowlist with the catalog
 * check of its generic forms (confirmation, reason, request id).
 */
export const GM2_ALLOWED_MUTATIONS: ReadonlySet<string> = new Set(['/gm/v1/auth/login', '/gm/v1/auth/logout']);

/** The request path relative to the path of `apiBaseUrl` (an API behind a path prefix keeps the same rule). */
function apiPath(requestUrl: string, apiBaseUrl: string): string {
  const pathname = new URL(requestUrl).pathname;
  const prefix = new URL(apiBaseUrl).pathname.replace(/\/+$/, '');
  return prefix !== '' && pathname.startsWith(prefix + '/') ? pathname.slice(prefix.length) : pathname;
}

/** The in-memory token source (session/csrf.ts). */
export interface GmCsrf {
  get(): Promise<string>;
  refresh(): Promise<string>;
}

export interface GmClientOptions {
  config: GmConsoleConfig;
  /** Tests inject a fake. */
  fetch?: typeof fetch;
  csrf: GmCsrf;
  /** Any 401 (FR-057). */
  onUnauthorized(): void;
  /** Any 403 other than `CSRF_FAILED` (FR-058). */
  onForbidden(problem: GmProblem): void;
}

/** The typed GM API client: `openapi-fetch` over the generated `paths` (FR-075). */
export type GmClient = Client<paths>;

/** The GM document route for consoles (slice 017): not part of the document itself, so it has its own typed path. */
export const CATALOG_PATH = '/gm/v1/console/openapi.json';

export interface CatalogPaths {
  [CATALOG_PATH]: {
    parameters: { query?: never; header?: never; path?: never; cookie?: never };
    get: {
      parameters: { query?: never; header?: never; path?: never; cookie?: never };
      requestBody?: never;
      responses: { 200: { headers: Record<string, unknown>; content: { 'application/json': unknown } } };
    };
  };
}

export type GmCatalogClient = Client<CatalogPaths>;

async function readProblem(response: Response): Promise<GmProblem> {
  let body: unknown;
  try {
    body = await response.clone().json();
  } catch {
    body = undefined;
  }

  return problemFromResponse(response, body);
}

/**
 * The shell middleware (FR-051, FR-052, FR-057, FR-058): GET untouched; other methods get the CSRF token (Content-Type is set by
 * openapi-fetch only with a body; caller headers such as `X-Request-Id` pass through); `403 CSRF_FAILED` on a mutation → one
 * token refresh and one retry of the same request (body and headers replayed from a copy taken before sending); 401 and other
 * 403 answers are reported once per answer; a fetch rejection becomes a `status: 0` problem.
 */
export function createGmMiddleware(options: GmClientOptions): Middleware {
  const copies = new WeakMap<Request, Request>();

  return {
    async onRequest({ request }) {
      if (SAFE_METHODS.has(request.method)) {
        return undefined;
      }

      const path = apiPath(request.url, options.config.apiBaseUrl);
      if (!GM2_ALLOWED_MUTATIONS.has(path)) {
        throw new GmApiError({
          status: NO_ANSWER_STATUS,
          code: MUTATION_NOT_ALLOWED,
          retryable: false,
          errors: {},
          detail: `${request.method} ${path} is not available in this console (only sign-in and sign-out send changes in GM-2).`,
        });
      }

      request.headers.set(CSRF_HEADER, await options.csrf.get());
      copies.set(request, request.clone());
      return request;
    },

    async onResponse({ request, response, options: fetchOptions }) {
      let final = response;
      const copy = copies.get(request);
      copies.delete(request);
      if (response.status === 403 && copy) {
        const problem = await readProblem(response);
        if (problem.code === CSRF_FAILED) {
          copy.headers.set(CSRF_HEADER, await options.csrf.refresh());
          try {
            final = await fetchOptions.fetch(copy);
          } catch (error) {
            // The retry is sent outside openapi-fetch's error handling: a network failure is still "no answer" (review m1).
            throw new GmApiError(networkProblem(options.config.apiBaseUrl, error));
          }
        }
      }

      if (final.status === 401) {
        options.onUnauthorized();
      } else if (final.status === 403) {
        const problem = await readProblem(final);
        if (problem.code !== CSRF_FAILED) {
          options.onForbidden(problem);
        }
      }

      return final === response ? undefined : final;
    },

    onError({ error }) {
      return new GmApiError(networkProblem(options.config.apiBaseUrl, error));
    },
  };
}

function clientOptions(options: GmClientOptions) {
  const fetchFn = options.fetch ?? ((input: RequestInfo | URL, init?: RequestInit) => globalThis.fetch(input, init));
  return {
    baseUrl: options.config.apiBaseUrl,
    fetch: fetchFn,
    credentials: 'include' as const,
    pathSerializer: gmPathSerializer,
  };
}

/** The typed client of the GM API with the shell middleware (contract public-api §6, internal). */
export function createGmClient(options: GmClientOptions): GmClient {
  const client = createClient<paths>(clientOptions(options));
  client.use(createGmMiddleware(options));
  return client;
}

/** The client of the catalog route, with the same middleware (no custom header: the browser cache revalidates, FR-065). */
export function createCatalogClient(options: GmClientOptions): GmCatalogClient {
  const client = createClient<CatalogPaths>(clientOptions(options));
  client.use(createGmMiddleware(options));
  return client;
}

/** Thrown values of a call → `GmApiError`. */
function toApiError(error: unknown): GmApiError {
  if (error instanceof GmApiError) {
    return error;
  }

  if (error instanceof InvalidPathParameterError) {
    return new GmApiError({
      status: NO_ANSWER_STATUS,
      code: INVALID_PATH_PARAMETER,
      retryable: false,
      errors: {},
      detail: error.message,
    });
  }

  return new GmApiError(problemOf(error));
}

/** Awaits an `openapi-fetch` call: its data on success, else throws `GmApiError` with the Problem Details (FR-062). */
export async function call<T>(pending: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  let result: { data?: T; error?: unknown; response: Response };
  try {
    result = await pending;
  } catch (error) {
    throw toApiError(error);
  }

  if (result.response.ok) {
    return result.data as T;
  }

  throw new GmApiError(problemFromResponse(result.response, result.error));
}
