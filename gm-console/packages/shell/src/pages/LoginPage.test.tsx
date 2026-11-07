import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Outlet } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { RedirectToLogin } from '../session/RedirectToLogin';
import { SessionProvider } from '../session/SessionProvider';
import { createFakeFetch, problemReply, type FakeRoute } from '../test/fakeFetch';
import { sessionRoutes } from '../test/fixtures';
import { renderRoutes } from '../test/render';
import { LoginPage } from './LoginPage';

function renderLogin(routes: Record<string, FakeRoute>, path = '/login') {
  const fake = createFakeFetch(sessionRoutes({ 'GET /gm/v1/me': problemReply(401, 'UNAUTHENTICATED'), ...routes }));
  const navigateTop = vi.fn<(url: string) => void>();
  // Like the console's gate (router.tsx): the login route shows the page, any other route redirects to it.
  const view = renderRoutes(
    [
      {
        element: (
          <SessionProvider builtFor={{ frameworkVersion: '0.7.0' }} fetch={fake.fetch} navigateTop={navigateTop}>
            <Outlet />
          </SessionProvider>
        ),
        children: [
          { path: '/login', element: <LoginPage /> },
          { path: '*', element: <RedirectToLogin /> },
        ],
      },
    ],
    path,
  );
  return { fake, navigateTop, view };
}

describe('LoginPage (FR-053, FR-054, US6-2, US6-3)', () => {
  it.each([
    [{ local: true, sso: false }, true, false, false],
    [{ local: false, sso: true }, false, true, false],
    [{ local: true, sso: true }, true, true, false],
    [{ local: false, sso: false }, false, false, true],
  ])('auth methods %j: form %s, SSO %s, none-message %s', async (methods, form, sso, none) => {
    renderLogin({ 'GET /gm/v1/auth/methods': { body: methods } });

    await screen.findByRole('heading', { name: 'GM console' });
    expect(screen.queryByRole('form', { name: 'Local sign-in' }) !== null).toBe(form);
    expect(screen.queryByRole('button', { name: 'Sign in with SSO' }) !== null).toBe(sso);
    expect(screen.queryByText('No sign-in method is enabled on this GM API.') !== null).toBe(none);
  });

  it('starts SSO top-level with the encoded hash of the page the staff member was on', async () => {
    const { navigateTop, view } = renderLogin({ 'GET /gm/v1/auth/methods': { body: { local: false, sso: true } } }, '/players/abc');

    await userEvent.click(await screen.findByRole('button', { name: 'Sign in with SSO' }));

    expect(view.path()).toBe('/login');
    expect(navigateTop).toHaveBeenCalledWith('http://localhost:5080/gm/v1/auth/sso/start?returnUrl=%2F%23%2Fplayers%2Fabc');
  });

  it('starts SSO with the console root when the login page was opened directly', async () => {
    const { navigateTop } = renderLogin({ 'GET /gm/v1/auth/methods': { body: { local: false, sso: true } } });

    await userEvent.click(await screen.findByRole('button', { name: 'Sign in with SSO' }));

    expect(navigateTop).toHaveBeenCalledWith('http://localhost:5080/gm/v1/auth/sso/start?returnUrl=%2F%23%2F');
  });

  it('shows the SSO denial read from the hash query', async () => {
    renderLogin({ 'GET /gm/v1/auth/methods': { body: { local: false, sso: true } } }, '/login?error=sso_denied');

    expect(await screen.findByRole('alert')).toHaveTextContent('The identity provider sign-in was not accepted');
  });

  it('says only "invalid staff id or password" on 401 and keeps no password', async () => {
    renderLogin({ 'POST /gm/v1/auth/login': problemReply(401, 'UNAUTHENTICATED', { detail: 'unknown staff admin2' }) });

    await userEvent.type(await screen.findByLabelText(/Staff id/), 'admin2');
    await userEvent.type(screen.getByLabelText(/Password/), 'secret-value');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/^Invalid staff id or password\.$/);
    expect(screen.queryByText(/unknown staff/)).toBeNull();
    expect(screen.getByLabelText(/Password/)).toHaveValue('');
    expect([...document.querySelectorAll('input')].map((i) => i.value)).not.toContain('secret-value');
  });
});
