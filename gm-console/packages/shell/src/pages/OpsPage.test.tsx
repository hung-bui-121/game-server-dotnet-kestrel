import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { problemReply, type FakeRoute } from '../test/fakeFetch';
import { READ_ROUTES } from '../test/fixtures';
import { renderPage } from '../test/renderPage';
import { OpsPage } from './OpsPage';

const ROUTES: Record<string, FakeRoute> = {
  'GET /gm/v1/health': {
    body: { status: 'Healthy', durationMs: 3.5, entries: [{ name: 'store', status: 'Healthy', description: null, durationMs: 1.2, tags: [], data: {} }] },
  },
  'GET /gm/v1/ops/version': {
    body: { gameId: 'sample', environmentId: 'local', instanceId: 'i-1', gameVersion: '1.0.0', frameworkVersion: '0.7.0+abc' },
  },
  'GET /gm/v1/ops/gate': { body: { maintenance: { enabled: false, message: null }, minClientVersions: { android: { minVersion: '1.2.0' } }, rev: 4 } },
};

const only = (...names: string[]) =>
  Object.fromEntries(Object.entries(READ_ROUTES).filter(([name]) => (!name.startsWith('gm.health') && !name.startsWith('gm.ops_')) || names.includes(name))) as typeof READ_ROUTES;

function renderOps(routes: Record<string, FakeRoute>, operations = READ_ROUTES) {
  return renderPage((v) => <OpsPage api={v.api} me={v.me} catalog={v.catalog} />, '/ops', '/ops', { routes, operations });
}

describe('OpsPage (FR-072, FR-074, A10)', () => {
  it('shows health, version and gate', async () => {
    renderOps(ROUTES);

    expect(await screen.findByTestId('health-status')).toHaveTextContent('Status: Healthy (3.5 ms)');
    expect(screen.getByRole('table', { name: 'Health checks' })).toHaveTextContent('store');
    expect(await screen.findByTestId('framework-version')).toHaveTextContent('0.7.0+abc');
    expect(await screen.findByTestId('maintenance')).toHaveTextContent('Maintenance: off');
    expect(screen.getByLabelText('Minimum client versions').textContent).toContain('1.2.0');
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });

  it.each([
    ['gm.health', 'Health'],
    ['gm.ops_version', 'Version'],
    ['gm.ops_gate', 'Gate'],
  ])('shows the %s card alone when it is the only one served', async (route, card) => {
    const { fake } = renderOps(ROUTES, only(route));

    expect(await screen.findByRole('region', { name: card })).toBeInTheDocument();
    expect(screen.getAllByRole('region')).toHaveLength(1);
    expect(fake.requests).toHaveLength(1);
  });

  it('keeps the other cards when one fails', async () => {
    renderOps({ ...ROUTES, 'GET /gm/v1/health': problemReply(503, 'UNAVAILABLE') });

    expect(await screen.findByRole('alert')).toHaveTextContent('UNAVAILABLE');
    expect(await screen.findByTestId('framework-version')).toHaveTextContent('0.7.0+abc');
    expect(await screen.findByTestId('maintenance')).toBeInTheDocument();
  });
});
