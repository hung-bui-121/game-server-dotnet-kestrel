import { screen } from '@testing-library/react';
import { act } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createGmConsole } from './index';
import { createFakeFetch, problemReply } from './test/fakeFetch';
import { sessionRoutes } from './test/fixtures';

afterEach(() => {
  vi.unstubAllGlobals();
  document.body.replaceChildren();
});

describe('createGmConsole (contract public-api §6, FR-042)', () => {
  it('mounts the console into the given element and starts with config.json', async () => {
    const fake = createFakeFetch(sessionRoutes({ 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED') }));
    vi.stubGlobal('fetch', fake.fetch);
    const root = document.createElement('section');
    document.body.append(root);

    await act(async () => {
      createGmConsole({ root });
      await Promise.resolve();
    });

    expect(await screen.findByRole('form', { name: 'Local sign-in' })).toBeInTheDocument();
    expect(root.contains(screen.getByRole('form', { name: 'Local sign-in' }))).toBe(true);
    // StrictMode runs the start effect twice in development; the cancelled run is ignored.
    expect(fake.calls()[0]).toBe('GET /config.json');
    expect(new Set(fake.calls())).toEqual(new Set(['GET /config.json', 'GET /gm/v1/auth/methods', 'GET /gm/v1/me']));
    expect(window.location.hash).toBe('#/login');
  });

  it('refuses to start without a root element', () => {
    expect(() => createGmConsole()).toThrow(/no #root element/);
  });
});
