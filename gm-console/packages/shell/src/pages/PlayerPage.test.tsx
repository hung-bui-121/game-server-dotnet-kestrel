import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { problemReply, type FakeRoute } from '../test/fakeFetch';
import { READ_ROUTES } from '../test/fixtures';
import { renderPage } from '../test/renderPage';
import { PlayerPage } from './PlayerPage';

const PLAYER = {
  playerId: 'p-1',
  aggregates: [
    {
      id: 'profile',
      exists: true,
      clientVisible: true,
      rev: 7,
      schemaVersion: 2,
      storedSchemaVersion: 1,
      sizeBytes: 321,
      updatedAtUtc: '2026-11-06T10:00:00Z',
      state: { level: 3, name: '<b>x</b>' },
    },
    { id: 'wallet', exists: false, clientVisible: false, rev: 0, schemaVersion: 1, storedSchemaVersion: 0, sizeBytes: 0, updatedAtUtc: null, state: null },
  ],
  deletion: { state: 'Requested', requestedAtUtc: '2026-11-01T00:00:00Z', purgeAfterUtc: '2026-12-01T00:00:00Z', purgeStartedAtUtc: null, purgedAtUtc: null },
};

const page = (items: unknown[]) => ({ body: { items, nextCursor: null } });

function renderPlayer(routes: Record<string, FakeRoute>, operations = READ_ROUTES, path = '/players/p-1') {
  return renderPage((v) => <PlayerPage api={v.api} me={v.me} catalog={v.catalog} />, path, '/players/:playerId', { routes, operations });
}

const allRoutes: Record<string, FakeRoute> = {
  'GET /gm/v1/players/p-1': { body: PLAYER },
  'GET /gm/v1/players/p-1/receipts': page([
    { receiptId: 'r-1', operation: 'economy.adjust', actorType: 'staff', actorId: 'admin', resultCode: 'OK', committedAtUtc: 't', aggregates: [], outboxPending: false, outboxDeadLetter: false, correlationId: 'c' },
  ]),
  'GET /gm/v1/players/p-1/audit': page([{ action: 'identity.ban', staffId: 'admin' }]),
  'GET /gm/v1/players/p-1/ledger': page([{ resource: 'gold', delta: 5 }]),
};

describe('PlayerPage (FR-069, US7-3)', () => {
  it('shows every aggregate, the deletion state and each state as JSON text', async () => {
    renderPlayer(allRoutes);

    const table = await screen.findByRole('table', { name: 'Aggregates' });
    const rows = within(table).getAllByRole('row').map((r) => r.textContent);
    expect(rows[1]).toBe('profileyesyes72 / 13212026-11-06T10:00:00Z');
    expect(rows[2]).toBe('walletnono01 / 00—');
    expect(screen.getByLabelText('Deletion state').textContent).toContain('"state": "Requested"');
    expect(screen.getByLabelText('State profile').textContent).toBe(JSON.stringify(PLAYER.aggregates[0]!.state, null, 2));
    expect(screen.getByLabelText('State wallet').textContent).toBe('null');
    expect(document.querySelector('b')).toBeNull();
  });

  it('has Receipts, Audit and Ledger tabs, each loading its own list; receipts link to the receipt page', async () => {
    const { fake, view } = renderPlayer(allRoutes);

    await screen.findByRole('link', { name: 'r-1' });
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Receipts', 'Audit', 'Ledger']);

    await userEvent.click(screen.getByRole('tab', { name: 'Audit' }));
    expect((await screen.findByLabelText('Audit entry 1')).textContent).toContain('identity.ban');
    await userEvent.click(screen.getByRole('tab', { name: 'Ledger' }));
    expect((await screen.findByLabelText('Ledger entry 1')).textContent).toContain('gold');
    expect(fake.calls()).toEqual([
      'GET /gm/v1/players/p-1',
      'GET /gm/v1/players/p-1/receipts',
      'GET /gm/v1/players/p-1/audit',
      'GET /gm/v1/players/p-1/ledger',
    ]);

    await userEvent.click(screen.getByRole('tab', { name: 'Receipts' }));
    await userEvent.click(await screen.findByRole('link', { name: 'r-1' }));
    expect(view.path()).toBe('/receipts/r-1');
  });

  it('has no Ledger tab without economy.ledger in the catalog', async () => {
    const operations = Object.fromEntries(Object.entries(READ_ROUTES).filter(([name]) => name !== 'economy.ledger')) as typeof READ_ROUTES;
    renderPlayer(allRoutes, operations);

    await screen.findByRole('table', { name: 'Aggregates' });
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Receipts', 'Audit']);
  });

  it('keeps Receipts working when the Audit tab fails', async () => {
    renderPlayer({ ...allRoutes, 'GET /gm/v1/players/p-1/audit': problemReply(500, 'INTERNAL') });

    await userEvent.click(await screen.findByRole('tab', { name: 'Audit' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('INTERNAL');
    await userEvent.click(screen.getByRole('tab', { name: 'Receipts' }));
    expect(await screen.findByRole('link', { name: 'r-1' })).toBeInTheDocument();
  });

  it('says "player not found" on 404 and shows no tabs', async () => {
    renderPlayer({ 'GET /gm/v1/players/p-1': problemReply(404, 'PLAYER_NOT_FOUND') });

    expect(await screen.findByRole('alert')).toHaveTextContent('Player not found.');
    expect(screen.queryAllByRole('tab')).toHaveLength(0);
  });

  it.each([
    ['/players/..%2F..%2Fstaff', '/gm/v1/players/..%2F..%2Fstaff'],
    ['/players/..%2Fstaff', '/gm/v1/players/..%2Fstaff'],
    ['/players/a%3Fb%23c', '/gm/v1/players/a%3Fb%23c'],
  ])('a hash parameter %s stays the player id of one API path segment', async (path, apiPath) => {
    const { fake } = renderPlayer({}, READ_ROUTES, path);

    await screen.findByRole('alert');
    expect(fake.requests.map((r) => r.path)).toEqual([apiPath]);
    expect(fake.requests.some((r) => r.path.startsWith('/gm/v1/staff'))).toBe(false);
  });

  it('calls nothing for a dot-segment player id', async () => {
    const { fake } = renderPlayer({}, READ_ROUTES, '/players/%2E%2E');

    expect(await screen.findByRole('alert')).toHaveTextContent('INVALID_PATH_PARAMETER');
    expect(fake.requests).toHaveLength(0);
  });
});
