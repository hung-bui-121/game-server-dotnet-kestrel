import { describe, expect, it, vi } from 'vitest';
import { createFakeFetch, problemReply, type FakeRoute } from '../test/fakeFetch';
import type { GmProblem } from '../types';
import { call, createCatalogClient, createGmClient, CATALOG_PATH, type GmClient } from './client';
import { GmApiError, NETWORK_ERROR, INVALID_PATH_PARAMETER } from './problem';

const CONFIG = { apiBaseUrl: 'http://localhost:5080', gameId: 'sample', environmentId: 'local' };

function setup(routes: Record<string, FakeRoute> = {}) {
  const fake = createFakeFetch(routes);
  let tokens = 0;
  const csrf = {
    get: vi.fn(() => Promise.resolve(`token-${tokens === 0 ? ++tokens : tokens}`)),
    refresh: vi.fn(() => Promise.resolve(`token-${++tokens}`)),
  };
  const onUnauthorized = vi.fn<() => void>();
  const onForbidden = vi.fn<(problem: GmProblem) => void>();
  const options = { config: CONFIG, fetch: fake.fetch, csrf, onUnauthorized, onForbidden };
  return { fake, csrf, onUnauthorized, onForbidden, api: createGmClient(options), catalog: createCatalogClient(options) };
}

async function rejection(pending: Promise<unknown>): Promise<GmProblem> {
  try {
    await pending;
  } catch (error) {
    expect(error).toBeInstanceOf(GmApiError);
    return (error as GmApiError).problem;
  }

  throw new Error('expected a rejection');
}

describe('createGmClient (FR-051, FR-052, FR-057, FR-058, FR-062)', () => {
  it('sends GET with credentials and no added header (no preflight)', async () => {
    const { fake, api, csrf } = setup({ 'GET /gm/v1/me': { body: { staffId: 'admin' } } });

    await call(api.GET('/gm/v1/me'));

    expect(fake.requests).toHaveLength(1);
    expect(fake.requests[0]).toMatchObject({ url: 'http://localhost:5080/gm/v1/me', credentials: 'include' });
    expect(fake.requests[0]?.headers).toEqual({});
    expect(csrf.get).not.toHaveBeenCalled();
  });

  it('adds the CSRF token to non-GET calls, Content-Type only with a body, and passes caller headers through', async () => {
    const { fake, api } = setup({
      'POST /gm/v1/auth/logout': { status: 204 },
      'POST /gm/v1/auth/login': { body: { staffId: 'admin' } },
    });

    await call(api.POST('/gm/v1/auth/logout'));
    await call(api.POST('/gm/v1/auth/login', { body: { staffId: 'admin', password: 'p' } }));
    await call(api.POST('/gm/v1/auth/login', { body: { staffId: 'admin', password: 'p' }, headers: { 'X-Request-Id': 'req-1' } }));

    expect(fake.requests[0]?.headers).toEqual({ 'x-csrf-token': 'token-1' });
    expect(fake.requests[1]?.headers).toEqual({ 'x-csrf-token': 'token-1', 'content-type': 'application/json' });
    expect(fake.requests[1]?.body).toBe('{"staffId":"admin","password":"p"}');
    expect(fake.requests[2]?.headers).toEqual({ 'x-csrf-token': 'token-1', 'content-type': 'application/json', 'x-request-id': 'req-1' });
    expect(fake.requests.every((r) => r.credentials === 'include')).toBe(true);
  });

  it('on 403 CSRF_FAILED refreshes the token and retries the mutation once with the same body and headers', async () => {
    const { fake, api, csrf, onForbidden } = setup({
      'POST /gm/v1/auth/login': [problemReply(403, 'CSRF_FAILED'), { body: { staffId: 'admin' } }],
    });

    const me = await call(api.POST('/gm/v1/auth/login', { body: { staffId: 'admin', password: 'p' } }));

    expect(me).toEqual({ staffId: 'admin' });
    expect(csrf.refresh).toHaveBeenCalledTimes(1);
    expect(fake.calls()).toEqual(['POST /gm/v1/auth/login', 'POST /gm/v1/auth/login']);
    expect(fake.requests[1]?.body).toBe(fake.requests[0]?.body);
    expect(fake.requests[0]?.headers['x-csrf-token']).toBe('token-1');
    expect(fake.requests[1]?.headers).toEqual({ 'x-csrf-token': 'token-2', 'content-type': 'application/json' });
    expect(onForbidden).not.toHaveBeenCalled();
  });

  it('retries only once: a second CSRF failure is returned', async () => {
    const { fake, api, csrf } = setup({ 'POST /gm/v1/auth/logout': problemReply(403, 'CSRF_FAILED') });

    const problem = await rejection(call(api.POST('/gm/v1/auth/logout')));

    expect(problem).toMatchObject({ status: 403, code: 'CSRF_FAILED' });
    expect(fake.calls()).toEqual(['POST /gm/v1/auth/logout', 'POST /gm/v1/auth/logout']);
    expect(csrf.refresh).toHaveBeenCalledTimes(1);
  });

  it('never retries a GET', async () => {
    const { fake, api, csrf } = setup({ 'GET /gm/v1/me': problemReply(403, 'CSRF_FAILED') });

    await rejection(call(api.GET('/gm/v1/me')));

    expect(fake.requests).toHaveLength(1);
    expect(csrf.refresh).not.toHaveBeenCalled();
  });

  it('reports 401 once per answer and other 403 answers to onForbidden', async () => {
    const { api, onUnauthorized, onForbidden } = setup({
      'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED'),
      'GET /gm/v1/staff': problemReply(403, 'FORBIDDEN'),
    });

    await rejection(call(api.GET('/gm/v1/me')));
    expect(onUnauthorized).toHaveBeenCalledTimes(1);

    const problem = await rejection(call(api.GET('/gm/v1/staff', { params: { query: { pageSize: 50 } } })));
    expect(problem.code).toBe('FORBIDDEN');
    expect(onForbidden).toHaveBeenCalledTimes(1);
    expect(onForbidden.mock.calls[0]?.[0]).toMatchObject({ status: 403, code: 'FORBIDDEN' });
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('maps Problem Details with field errors, and takes the request id from the header when the body has none', async () => {
    const { api } = setup({
      'GET /gm/v1/players/search': {
        status: 400,
        headers: { 'X-Request-Id': 'from-header' },
        body: { title: 'Invalid', code: 'VALIDATION_FAILED', codeNumber: 4001, detail: 'd', errors: { pageSize: ['must be 1..100'] } },
      },
    });

    const problem = await rejection(call(api.GET('/gm/v1/players/search', { params: { query: { pageSize: 50, displayName: 'x' } } })));

    expect(problem).toEqual({
      status: 400,
      code: 'VALIDATION_FAILED',
      codeNumber: 4001,
      title: 'Invalid',
      detail: 'd',
      retryable: false,
      errors: { pageSize: ['must be 1..100'] },
      requestId: 'from-header',
    });
  });

  it('turns a fetch rejection into a status 0 problem naming the API origin', async () => {
    const { api, onUnauthorized } = setup({ 'GET /gm/v1/me': { networkError: true } });

    const problem = await rejection(call(api.GET('/gm/v1/me')));

    expect(problem).toMatchObject({ status: 0, code: NETWORK_ERROR, detail: 'No answer from the GM API at http://localhost:5080.' });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('loads the catalog with no custom header and the default cache mode', async () => {
    const { fake, catalog } = setup({ [`GET ${CATALOG_PATH}`]: { body: { openapi: '3.1.1', paths: {} } } });

    await call(catalog.GET(CATALOG_PATH));

    expect(fake.requests[0]?.headers).toEqual({});
    expect(fake.requests[0]?.cache).toBe('default');
  });
});

describe('path parameters (Phase A review carry-over: a hash value never changes the endpoint)', () => {
  it.each([
    ['../staff', '/gm/v1/players/..%2Fstaff'],
    ['..%2F..%2Fstaff', '/gm/v1/players/..%252F..%252Fstaff'],
    ['../../gm/v1/staff', '/gm/v1/players/..%2F..%2Fgm%2Fv1%2Fstaff'],
    ['a/b?c#d', '/gm/v1/players/a%2Fb%3Fc%23d'],
    ['%2e%2e', '/gm/v1/players/%252e%252e'],
    ['pgs:abc', '/gm/v1/players/pgs%3Aabc'],
  ])('player id %j stays one segment of the player route', async (playerId, path) => {
    const { fake, api } = setup();

    await rejection(call(api.GET('/gm/v1/players/{playerId}', { params: { path: { playerId } } })));

    expect(fake.requests).toHaveLength(1);
    expect(fake.requests[0]?.path).toBe(path);
    expect(new URL(fake.requests[0]!.url).pathname.split('/')).toHaveLength(5);
  });

  it.each(['..', '.', ''])('refuses the dot or empty segment %j before any request', async (playerId) => {
    const { fake, api } = setup();

    const problem = await rejection(call(api.GET('/gm/v1/players/{playerId}/receipts', { params: { path: { playerId }, query: { pageSize: 50 } } })));

    expect(problem).toMatchObject({ status: 0, code: INVALID_PATH_PARAMETER });
    expect(fake.requests).toHaveLength(0);
  });
});

type Send = (api: GmClient) => Promise<{ data?: unknown; error?: unknown; response: Response }>;

describe('GM-2 mutation guard (D10, FR-074, review T068 M1)', () => {
  it.each<[string, Send]>([
    ['POST staff disable', (api) =>
      api.POST('/gm/v1/staff/{staffId}/disable', { params: { path: { staffId: 's1' }, header: { 'X-Request-Id': 'r' } }, body: { reason: 'r' } })],
    ['PUT gate maintenance', (api) =>
      api.PUT('/gm/v1/ops/gate/maintenance', { params: { header: { 'X-Request-Id': 'r' } }, body: { enabled: true, reason: 'r' } as never })],
    ['request("post") ban', (api) =>
      api.request('post', '/gm/v1/players/{playerId}/ban', { params: { path: { playerId: 'p' }, header: { 'X-Request-Id': 'r' } }, body: {} as never })],
    ["['POST'] resources adjust", (api) =>
      api['POST']('/gm/v1/players/{playerId}/resources/adjust', { params: { path: { playerId: 'p' }, header: { 'X-Request-Id': 'r' } }, body: {} as never })],
  ])('refuses %s before sending anything', async (_name, send) => {
    const { fake, api, csrf } = setup();

    const problem = await rejection(call(send(api)));

    expect(problem).toMatchObject({ status: 0, code: 'MUTATION_NOT_ALLOWED' });
    expect(fake.requests).toHaveLength(0);
    expect(csrf.get).not.toHaveBeenCalled();
  });

  it('allows sign-in and sign-out, also behind an API path prefix', async () => {
    const fake = createFakeFetch({ 'POST /api-prefix/gm/v1/auth/logout': { status: 204 }, 'POST /api-prefix/gm/v1/staff': { status: 200, body: {} } });
    const options = {
      config: { ...CONFIG, apiBaseUrl: 'https://gm-api.example.com/api-prefix' },
      fetch: fake.fetch,
      csrf: { get: () => Promise.resolve('t'), refresh: () => Promise.resolve('t') },
      onUnauthorized: vi.fn(),
      onForbidden: vi.fn(),
    };
    const api = createGmClient(options);

    await call(api.POST('/gm/v1/auth/logout'));
    const refused = await rejection(call(api.POST('/gm/v1/staff', { params: { header: { 'X-Request-Id': 'r' } }, body: {} as never })));

    expect(fake.calls()).toEqual(['POST /api-prefix/gm/v1/auth/logout']);
    expect(refused.code).toBe('MUTATION_NOT_ALLOWED');
  });
});

describe('CSRF retry without an answer (review T068 m1)', () => {
  it('reports "no answer from the GM API" when the retried mutation gets no answer', async () => {
    const { fake, api } = setup({ 'POST /gm/v1/auth/login': [problemReply(403, 'CSRF_FAILED'), { networkError: true }] });

    const problem = await rejection(call(api.POST('/gm/v1/auth/login', { body: { staffId: 'admin', password: 'p' } })));

    expect(problem).toMatchObject({ status: 0, code: NETWORK_ERROR, detail: 'No answer from the GM API at http://localhost:5080.' });
    expect(fake.calls()).toEqual(['POST /gm/v1/auth/login', 'POST /gm/v1/auth/login']);
  });
});
