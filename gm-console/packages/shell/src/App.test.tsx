import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { GmConsoleApp } from './App';
import { CATALOG_PATH } from './api/client';
import { createFakeFetch, problemReply, type FakeRoute } from './test/fakeFetch';
import { catalogDocument, CONFIG, meBody, READ_ROUTES, sessionRoutes } from './test/fixtures';

const CATALOG_ROUTE = `GET ${CATALOG_PATH}`;

function startConsole(path: string, routes: Record<string, FakeRoute> = {}) {
  const fake = createFakeFetch(
    sessionRoutes({
      [CATALOG_ROUTE]: { body: catalogDocument(READ_ROUTES), headers: { ETag: '"c1"' } },
      'GET /gm/v1/staff': { body: { items: [], nextCursor: null } },
      'GET /gm/v1/ops/version': { body: { gameId: 'sample', environmentId: 'local', instanceId: 'i', gameVersion: '1', frameworkVersion: '0.7.0' } },
      'GET /gm/v1/health': { body: { status: 'Healthy', durationMs: 1, entries: [] } },
      'GET /gm/v1/ops/gate': { body: { maintenance: { enabled: false }, minClientVersions: {}, rev: 1 } },
      ...routes,
    }),
  );
  let router: ReturnType<typeof createMemoryRouter> | undefined;
  render(
    <GmConsoleApp
      builtFor={{ frameworkVersion: '0.7.0' }}
      fetch={fake.fetch}
      navigateTop={vi.fn()}
      createRouter={(r) => (router = createMemoryRouter(r, { initialEntries: [path] }))}
    />,
  );
  const path_ = () => router!.state.location.pathname;
  const navigate = (to: string) => act(async () => void (await router!.navigate(to)));
  return { fake, path: path_, navigate };
}

const START = ['GET /config.json', 'GET /gm/v1/auth/methods', 'GET /gm/v1/me'];
const signedOut = { 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED') };

describe('console gate and routes (FR-050, FR-055, FR-057, FR-065, FR-066, SC-008)', () => {
  it('shows the config error with the field and calls no API', async () => {
    const { fake } = startConsole('/players', { 'GET /config.json': { body: { ...CONFIG, gameId: '' } } });

    expect(await screen.findByRole('alert')).toHaveTextContent('Field: gameId');
    expect(fake.calls()).toEqual(['GET /config.json']);
  });

  it('shows "no answer from the GM API" with retry, never the login page, when the API is down', async () => {
    const { fake } = startConsole('/players', { 'GET /gm/v1/auth/methods': { networkError: true } });

    expect(await screen.findByRole('alert')).toHaveTextContent('No answer from the GM API at http://localhost:5080.');
    expect(screen.queryByRole('form', { name: 'Local sign-in' })).toBeNull();
    fake.route('GET /gm/v1/auth/methods', { body: { local: true, sso: false } });
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('heading', { name: 'Players' })).toBeInTheDocument();
  });

  it('signed out on a deep link: login, then back to that page after sign-in', async () => {
    const { path } = startConsole('/staff/admin', { ...signedOut, 'GET /gm/v1/staff/admin': { body: { staffId: 'admin', displayName: 'Admin', email: null, status: 'Active', disabled: false, oidcBound: false, permissions: [], updatedAtUtc: null } } });

    await screen.findByRole('form', { name: 'Local sign-in' });
    expect(path()).toBe('/login');
    await userEvent.type(screen.getByLabelText(/Staff id/), 'admin');
    await userEvent.type(screen.getByLabelText(/Password/), 'dev-only-change-me');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Staff admin' })).toBeInTheDocument();
    expect(path()).toBe('/staff/admin');
  });

  it('blocks on another environment: block screen, no catalog, no page call', async () => {
    const { fake } = startConsole('/players', { 'GET /gm/v1/me': { body: meBody({ environmentId: 'beta' }) } });

    expect(await screen.findByRole('heading', { name: 'Wrong server' })).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 30));
    expect(fake.calls()).toEqual(START);
  });

  it('a hidden page reached by URL says "not available" and calls nothing', async () => {
    const { fake } = startConsole('/staff', { 'GET /gm/v1/me': { body: meBody({ permissions: ['gm.access', 'player.read'] }) } });

    expect(await screen.findByRole('alert')).toHaveTextContent('This page is not available');
    expect(fake.calls()).toEqual([...START, CATALOG_ROUTE]);
    expect(screen.queryByRole('link', { name: 'Staff' })).toBeNull();
  });

  it('loads the catalog once across navigations and shows the staff member in the header', async () => {
    const { fake, navigate } = startConsole('/staff');

    expect(await screen.findByRole('heading', { name: 'Staff' })).toBeInTheDocument();
    expect(screen.getByTestId('staff-name')).toHaveTextContent('Admin');
    await navigate('/ops');
    expect(await screen.findByRole('heading', { name: 'Operations' })).toBeInTheDocument();
    await navigate('/players');
    expect(await screen.findByRole('heading', { name: 'Players' })).toBeInTheDocument();
    await navigate('/staff');
    await screen.findByRole('heading', { name: 'Staff' });

    expect(fake.calls().filter((c) => c === CATALOG_ROUTE)).toHaveLength(1);
    const catalogRequest = fake.requests.find((r) => r.path === CATALOG_PATH)!;
    expect(catalogRequest.headers).toEqual({});
    expect(catalogRequest.credentials).toBe('include');
  });

  it.each([
    [['gm.access', 'player.read', 'staff.read'], '/players'],
    [['gm.access', 'staff.read', 'ops.read'], '/staff'],
  ])('sends #/ and unknown hashes to the first visible page (%j → %s)', async (permissions, home) => {
    const { path, navigate } = startConsole('/', { 'GET /gm/v1/me': { body: meBody({ permissions }) } });

    await waitFor(() => expect(path()).toBe(home));
    await navigate('/no/such/page');
    await waitFor(() => expect(path()).toBe(home));
  });

  it('says so when the permissions include no page', async () => {
    startConsole('/', { 'GET /gm/v1/me': { body: meBody({ permissions: ['gm.access'] }) } });

    expect(await screen.findByRole('alert')).toHaveTextContent('Your permissions do not include any page');
  });

  it('shows a catalog error with retry', async () => {
    const { fake } = startConsole('/players', { [CATALOG_ROUTE]: problemReply(500, 'INTERNAL') });

    expect(await screen.findByRole('alert')).toHaveTextContent('The GM catalog could not be loaded');
    expect(screen.queryByRole('navigation')).toBeNull();
    fake.route(CATALOG_ROUTE, { body: catalogDocument(READ_ROUTES) });
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByRole('heading', { name: 'Players' })).toBeInTheDocument();
  });

  it('on 401 in a page goes to the login page and drops the catalog', async () => {
    const { fake, path } = startConsole('/staff', { 'GET /gm/v1/staff': problemReply(401, 'UNAUTHENTICATED') });

    expect(await screen.findByText('Your session has ended. Sign in again to continue.')).toBeInTheDocument();
    expect(path()).toBe('/login');
    expect(fake.calls().filter((c) => c === 'GET /gm/v1/me')).toHaveLength(1);

    await userEvent.type(screen.getByLabelText(/Staff id/), 'admin');
    await userEvent.type(screen.getByLabelText(/Password/), 'dev-only-change-me');
    fake.route('GET /gm/v1/staff', { body: { items: [], nextCursor: null } });
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('heading', { name: 'Staff' })).toBeInTheDocument();
    expect(fake.calls().filter((c) => c === CATALOG_ROUTE)).toHaveLength(2);
  });

  it('signs out from the header back to the login page', async () => {
    const { fake, path } = startConsole('/ops');

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    expect(await screen.findByText('You are signed out.')).toBeInTheDocument();
    expect(path()).toBe('/login');
    expect(fake.calls()).toContain('POST /gm/v1/auth/logout');
  });

  it('injects no <style> element on any built-in page, so the static host keeps style-src self (V3, FR-081)', async () => {
    const { navigate } = startConsole('/players');

    await screen.findByRole('heading', { name: 'Players' });
    for (const [to, heading] of [['/staff', 'Staff'], ['/ops', 'Operations'], ['/players', 'Players']] as const) {
      await navigate(to);
      await screen.findByRole('heading', { name: heading });
    }

    expect(document.querySelectorAll('style')).toHaveLength(0);
  });

  it('a hash parameter cannot change the endpoint: ..%2F..%2Fstaff stays the player id', async () => {
    const { fake } = startConsole('/players/..%2F..%2Fstaff');

    expect(await screen.findByText('Player not found.')).toBeInTheDocument();
    const pageCalls = fake.requests.map((r) => r.path).filter((p) => !['/config.json', '/gm/v1/auth/methods', '/gm/v1/me', CATALOG_PATH].includes(p));
    expect(pageCalls).toEqual(['/gm/v1/players/..%2F..%2Fstaff']);
  });
});
