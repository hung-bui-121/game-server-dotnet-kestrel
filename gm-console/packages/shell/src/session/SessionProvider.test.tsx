import { act, screen, waitFor } from '@testing-library/react';
import { useEffect } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { call } from '../api/client';
import { createFakeFetch, problemReply, type FakeRoute } from '../test/fakeFetch';
import { meBody, sessionRoutes } from '../test/fixtures';
import { renderRoutes } from '../test/render';
import type { GmBuiltFor } from '../types';
import type { SessionState } from './phases';
import { SessionProvider, useSession, type SessionValue } from './SessionProvider';

let session: SessionValue | undefined;

function Probe() {
  const value = useSession();
  useEffect(() => {
    session = value;
  });
  return <div data-testid="phase">{value.state.phase}</div>;
}

function start(routes: Record<string, FakeRoute> = {}, path = '/', builtFor: GmBuiltFor = { frameworkVersion: '0.7.0' }) {
  session = undefined;
  const fake = createFakeFetch(sessionRoutes(routes));
  const navigateTop = vi.fn<(url: string) => void>();
  const view = renderRoutes(
    [
      {
        path: '*',
        element: (
          <SessionProvider builtFor={builtFor} fetch={fake.fetch} navigateTop={navigateTop}>
            <Probe />
          </SessionProvider>
        ),
      },
    ],
    path,
  );
  return { fake, navigateTop, view };
}

async function phase(expected: SessionState['phase']) {
  await waitFor(() => expect(screen.getByTestId('phase')).toHaveTextContent(expected));
}

function current(): SessionValue {
  if (!session) {
    throw new Error('no session');
  }

  return session;
}

function services() {
  const state = current().state;
  if (!('services' in state)) {
    throw new Error('no services');
  }

  return state.services;
}

describe('SessionProvider start (US6-1, FR-050, FR-055)', () => {
  it('starts with config → auth/methods → /me and lands signed out on 401', async () => {
    const { fake } = start({ 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED') });

    await phase('signed-out');
    expect(fake.calls()).toEqual(['GET /config.json', 'GET /gm/v1/auth/methods', 'GET /gm/v1/me']);
  });

  it('makes no API call with an invalid config.json', async () => {
    const { fake } = start({ 'GET /config.json': { body: { apiBaseUrl: 'nope', gameId: 'sample', environmentId: 'local' } } });

    await phase('config-error');
    expect(fake.calls()).toEqual(['GET /config.json']);
    expect(current().state).toMatchObject({ field: 'apiBaseUrl' });
  });

  it('shows an API error, not the login page, when the API does not answer', async () => {
    const { fake } = start({ 'GET /gm/v1/auth/methods': { networkError: true } });

    await phase('api-error');
    expect(fake.calls()).toEqual(['GET /config.json', 'GET /gm/v1/auth/methods']);
  });

  it('blocks on another environment or game with zero further calls (Q19)', async () => {
    for (const other of [{ environmentId: 'beta' }, { gameId: 'zhpvp' }]) {
      const { fake, view } = start({ 'GET /gm/v1/me': { body: meBody(other) } });

      await phase('blocked');
      await new Promise((r) => setTimeout(r, 30));
      expect(fake.calls()).toEqual(['GET /config.json', 'GET /gm/v1/auth/methods', 'GET /gm/v1/me']);
      view.unmount();
    }
  });

  it.each([
    ['0.8.1', 'minor-differs'],
    ['0.7.3', 'same'],
    ['0.7.0+abc', 'same'],
    ['n/a', 'unparsable'],
  ])('compares server framework %s with the build 0.7.0 as %s without blocking', async (frameworkVersion, framework) => {
    start({ 'GET /gm/v1/me': { body: meBody({ frameworkVersion }) } });

    await phase('signed-in');
    expect(current().state).toMatchObject({ skew: { framework, serverFrameworkVersion: frameworkVersion } });
  });

  it('reports a different game version when the build carries one', async () => {
    start({ 'GET /gm/v1/me': { body: meBody({ gameVersion: '2.0.0' }) } }, '/', { frameworkVersion: '0.7.0', gameVersion: '1.0.0' });

    await phase('signed-in');
    expect(current().state).toMatchObject({ skew: { gameDiffers: true, serverGameVersion: '2.0.0' } });
  });
});

describe('local sign-in (FR-054, US6-4)', () => {
  it('runs CSRF → login, takes /me from the login answer and fetches the session token lazily; only the POST carries a token', async () => {
    const { fake, view } = start({ 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED') }, '/login');
    await phase('signed-out');

    let problem: unknown = 'pending';
    await act(async () => {
      problem = await current().signInLocal('admin', 'dev-only-change-me');
    });

    expect(problem).toBeUndefined();
    await phase('signed-in');
    expect(fake.calls()).toEqual([
      'GET /config.json',
      'GET /gm/v1/auth/methods',
      'GET /gm/v1/me',
      'GET /gm/v1/auth/csrf',
      'POST /gm/v1/auth/login',
    ]);
    const withToken = fake.requests.filter((r) => 'x-csrf-token' in r.headers);
    expect(withToken.map((r) => `${r.method} ${r.path}`)).toEqual(['POST /gm/v1/auth/login']);
    expect(withToken[0]?.headers['x-csrf-token']).toBe('csrf-1');
    expect(fake.requests.filter((r) => r.method === 'GET').every((r) => Object.keys(r.headers).length === 0)).toBe(true);
    expect(current().state).toMatchObject({ landing: '/' });
    expect(view.path()).toBe('/login');
  });

  it('fetches the session token lazily: sign-out uses the token fetched after the login, never the anonymous one (T069 m3, r2 n4)', async () => {
    const { fake } = start(
      {
        'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED'),
        'GET /gm/v1/auth/csrf': [{ body: { token: 'anonymous' } }, { body: { token: 'session' } }, { body: { token: 'anonymous-2' } }],
      },
      '/login',
    );
    await phase('signed-out');

    let problem: unknown = 'pending';
    await act(async () => {
      problem = await current().signInLocal('admin', 'dev-only-change-me');
    });
    expect(problem).toBeUndefined();
    await phase('signed-in');
    expect(fake.calls().slice(-2)).toEqual(['GET /gm/v1/auth/csrf', 'POST /gm/v1/auth/login']);

    await act(async () => {
      await current().signOut();
    });
    await phase('signed-out');

    expect(fake.calls().slice(3, 7)).toEqual(['GET /gm/v1/auth/csrf', 'POST /gm/v1/auth/login', 'GET /gm/v1/auth/csrf', 'POST /gm/v1/auth/logout']);
    const posts = fake.requests.filter((r) => r.method === 'POST');
    expect(posts.map((r) => r.headers['x-csrf-token'])).toEqual(['anonymous', 'session']);
  });

  it('returns the 401 problem for invalid credentials and stays signed out', async () => {
    start({ 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED'), 'POST /gm/v1/auth/login': problemReply(401, 'UNAUTHENTICATED') });
    await phase('signed-out');

    let problem: unknown;
    await act(async () => {
      problem = await current().signInLocal('admin', 'wrong');
    });

    expect(problem).toMatchObject({ status: 401 });
    expect(screen.getByTestId('phase')).toHaveTextContent('signed-out');
  });
});

describe('401, 403 and sign-out (FR-057..FR-059, US6-7..US6-9)', () => {
  it('on 401 clears the session, goes to #/login and returns to the previous hash after sign-in', async () => {
    const { fake, view } = start({}, '/players/p-1');
    await phase('signed-in');
    fake.route('GET /gm/v1/staff', problemReply(401, 'UNAUTHENTICATED'));

    await act(async () => {
      await call(services().api.GET('/gm/v1/staff', { params: { query: { pageSize: 50 } } })).catch(() => undefined);
    });

    await phase('signed-out');
    expect(current().state).toMatchObject({ notice: 'expired' });
    expect(view.path()).toBe('/login');

    await act(async () => {
      await current().signInLocal('admin', 'dev-only-change-me');
    });
    await phase('signed-in');
    expect(current().state).toMatchObject({ landing: '/players/p-1' });
  });

  it('handles repeated 401 answers once, without a /me loop', async () => {
    const { fake } = start({}, '/staff');
    await phase('signed-in');
    fake.route('GET /gm/v1/staff', problemReply(401, 'UNAUTHENTICATED'));
    const before = fake.requests.length;

    await act(async () => {
      const api = services().api;
      await Promise.all(
        [1, 2, 3].map(() => call(api.GET('/gm/v1/staff', { params: { query: { pageSize: 50 } } })).catch(() => undefined)),
      );
    });

    await phase('signed-out');
    await new Promise((r) => setTimeout(r, 30));
    expect(fake.calls().slice(before)).toEqual(['GET /gm/v1/staff', 'GET /gm/v1/staff', 'GET /gm/v1/staff']);
  });

  it('on a 403 refreshes /me once per navigation and recomputes from the new permissions', async () => {
    const { fake, view } = start({}, '/staff');
    await phase('signed-in');
    fake.route('GET /gm/v1/staff', problemReply(403, 'FORBIDDEN'));
    fake.route('GET /gm/v1/me', { body: meBody({ permissions: ['gm.access', 'player.read'] }) });
    const before = fake.requests.length;
    const list = () => call(services().api.GET('/gm/v1/staff', { params: { query: { pageSize: 50 } } })).catch(() => undefined);

    await act(async () => {
      await list();
      await list();
    });

    await waitFor(() => expect(fake.calls().slice(before)).toEqual(['GET /gm/v1/staff', 'GET /gm/v1/me', 'GET /gm/v1/staff']));
    await waitFor(() => {
      const state = current().state;
      expect(state.phase === 'signed-in' && state.me.permissions.has('staff.read')).toBe(false);
    });

    await act(async () => {
      await view.router.navigate('/ops');
    });
    await act(async () => {
      await list();
    });
    await waitFor(() => expect(fake.calls().filter((c) => c === 'GET /gm/v1/me')).toHaveLength(3));
  });

  it('signs out with the token, clears the state and shows the login page, also when the session had expired', async () => {
    for (const logout of [{ status: 204 }, problemReply(401, 'UNAUTHENTICATED')]) {
      const { fake, view } = start({ 'POST /gm/v1/auth/logout': logout }, '/ops');
      await phase('signed-in');

      await act(async () => {
        await current().signOut();
      });

      await phase('signed-out');
      expect(current().state).toMatchObject({ notice: 'signed-out' });
      expect(view.path()).toBe('/login');
      const post = fake.requests.find((r) => r.method === 'POST');
      expect(post?.path).toBe('/gm/v1/auth/logout');
      expect(post?.headers['x-csrf-token']).toMatch(/^csrf-/);
      await waitFor(() => expect(fake.calls().at(-1)).toBe('GET /gm/v1/auth/csrf'));
      view.unmount();
    }
  });

  it('starts SSO by a top-level navigation with the remembered hash as returnUrl (US6-3)', async () => {
    const { navigateTop } = start({ 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED') }, '/login');
    await phase('signed-out');

    act(() => current().startSso());
    expect(navigateTop).toHaveBeenLastCalledWith('http://localhost:5080/gm/v1/auth/sso/start?returnUrl=%2F%23%2F');

    act(() => {
      current().rememberReturnTo('/players/a b');
      current().startSso();
    });
    expect(navigateTop).toHaveBeenLastCalledWith('http://localhost:5080/gm/v1/auth/sso/start?returnUrl=%2F%23%2Fplayers%2Fa%20b');

    act(() => {
      current().rememberReturnTo('//evil.example/x');
      current().rememberReturnTo('https://evil.example/x');
      current().startSso();
    });
    expect(navigateTop).toHaveBeenLastCalledWith('http://localhost:5080/gm/v1/auth/sso/start?returnUrl=%2F%23%2Fplayers%2Fa%20b');
  });
});
