import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { versionSkew, type VersionSkew } from '../session/versions';
import { createTestQueryClient, TestProviders } from '../test/render';
import type { GmMe } from '../types';
import { BlockScreen } from './BlockScreen';
import { EnvBadge } from './EnvBadge';
import { Layout } from './Layout';
import { SkewBanner } from './SkewBanner';

function show(ui: ReactElement) {
  return render(<TestProviders client={createTestQueryClient()}>{ui}</TestProviders>);
}

const me = (overrides: Partial<GmMe> = {}): GmMe => ({
  staffId: 'admin',
  displayName: 'Admin Person',
  gameId: 'sample',
  environmentId: 'local',
  permissions: new Set(['gm.access']),
  frameworkVersion: '0.7.0+abc',
  gameVersion: '1.0.0',
  ...overrides,
});

const skew = (server = '0.7.0+abc', built: { frameworkVersion: string; gameVersion?: string } = { frameworkVersion: '0.7.0' }): VersionSkew =>
  versionSkew(built, me({ frameworkVersion: server }));

describe('EnvBadge (FR-060)', () => {
  it('is red for live and not for beta', () => {
    show(
      <>
        <EnvBadge environmentId="live" />
        <EnvBadge environmentId="beta" />
      </>,
    );

    expect(screen.getByLabelText('Environment live')).toHaveAttribute('data-tone', 'danger');
    expect(screen.getByLabelText('Environment beta')).toHaveAttribute('data-tone', 'normal');
  });
});

describe('SkewBanner (FR-056, US6-6)', () => {
  it.each([
    ['0.7.3', false],
    ['0.8.1', true],
    ['garbage', true],
  ])('server %s: banner %s', (server, shown) => {
    show(<SkewBanner skew={skew(server)} />);
    expect(screen.queryByRole('status') !== null).toBe(shown);
  });

  it('names both versions', () => {
    show(<SkewBanner skew={skew('0.8.1')} />);
    expect(screen.getByRole('status')).toHaveTextContent('built for framework 0.7.0; the server runs 0.8.1');
  });
});

describe('Layout (FR-060)', () => {
  it('shows environment, game, staff, sign-out, navigation and the version footer', async () => {
    const onSignOut = vi.fn();
    show(
      <Layout me={me()} skew={skew()} navigation={<a href="#/players">Players</a>} onSignOut={onSignOut}>
        <p>page</p>
      </Layout>,
    );

    expect(screen.getByLabelText('Environment local')).toHaveTextContent('local');
    expect(screen.getByLabelText('Game sample')).toHaveTextContent('sample');
    expect(screen.getByTestId('staff-name')).toHaveTextContent('Admin Person');
    expect(within(screen.getByRole('navigation', { name: 'Pages' })).getByRole('link', { name: 'Players' })).toBeInTheDocument();
    expect(screen.getByRole('contentinfo')).toHaveTextContent('Console built for framework 0.7.0 · server framework 0.7.0+abc, game 1.0.0');
    expect(screen.queryByRole('note')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(onSignOut).toHaveBeenCalledTimes(1);
  });

  it('marks live with a fixed red strip', () => {
    show(
      <Layout me={me({ environmentId: 'live' })} skew={skew()} navigation={null} onSignOut={vi.fn()}>
        <p>page</p>
      </Layout>,
    );

    expect(screen.getByRole('note')).toHaveTextContent('LIVE environment');
  });

  it('adds an informational line when the game version differs from the build', () => {
    show(
      <Layout me={me()} skew={skew('0.7.0', { frameworkVersion: '0.7.0', gameVersion: '0.9.0' })} navigation={null} onSignOut={vi.fn()}>
        <p>page</p>
      </Layout>,
    );

    expect(screen.getByTestId('game-version-info')).toHaveTextContent('built for 0.9.0, the server runs 1.0.0');
    expect(screen.queryByRole('status')).toBeNull();
  });
});

describe('BlockScreen (FR-055, US6-5)', () => {
  it('names expected and actual game and environment and offers only sign-out', async () => {
    const onSignOut = vi.fn();
    show(<BlockScreen config={{ apiBaseUrl: 'http://x', gameId: 'sample', environmentId: 'local' }} me={me({ environmentId: 'beta' })} onSignOut={onSignOut} />);

    const table = screen.getByRole('table', { name: 'Expected and actual server' });
    expect(within(table).getByRole('row', { name: /Environment/ })).toHaveTextContent('Environmentlocalbeta');
    expect(within(table).getByRole('row', { name: /Game/ })).toHaveTextContent('Gamesamplesample');
    expect(screen.queryByRole('navigation')).toBeNull();
    expect(screen.queryAllByRole('link')).toHaveLength(0);
    expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Sign out']);
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(onSignOut).toHaveBeenCalledTimes(1);
  });
});
