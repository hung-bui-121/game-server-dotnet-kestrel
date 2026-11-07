import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { problemReply } from '../test/fakeFetch';
import { READ_ROUTES } from '../test/fixtures';
import { renderPage, type PageFixtureOptions } from '../test/renderPage';
import { PlayersPage } from './PlayersPage';

const player = (playerId: string) => ({ body: { playerId, aggregates: [], deletion: null } });

function renderPlayers(options: PageFixtureOptions = {}) {
  return renderPage((v) => <PlayersPage api={v.api} me={v.me} catalog={v.catalog} />, '/players', '/players', options);
}

describe('PlayersPage (FR-068, FR-061, US7-2)', () => {
  it('looks a player up by id and opens #/players/{playerId}', async () => {
    const { fake, view } = renderPlayers({ routes: { 'GET /gm/v1/players/p-1': player('p-1') } });

    await userEvent.type(screen.getByLabelText('Player id'), 'p-1');
    await userEvent.click(screen.getAllByRole('button', { name: 'Find' })[0]!);

    await waitFor(() => expect(view.path()).toBe('/players/p-1'));
    expect(fake.calls()).toEqual(['GET /gm/v1/players/p-1']);
  });

  it('looks a mapping key up with the key encoded in the API path and never puts it in the URL', async () => {
    const { fake, view } = renderPlayers({ routes: { 'GET /gm/v1/players/by-mapping/pgs%3Asecret-key': player('p-9') } });

    await userEvent.type(screen.getByLabelText('Mapping key'), 'pgs:secret-key');
    await userEvent.click(screen.getAllByRole('button', { name: 'Find' })[1]!);

    await waitFor(() => expect(view.path()).toBe('/players/p-9'));
    expect(fake.calls()).toEqual(['GET /gm/v1/players/by-mapping/pgs%3Asecret-key']);
    expect(view.path()).not.toContain('secret');
    expect(window.location.hash).not.toContain('secret');
  });

  it('searches a display name (paged) and links each hit; the name never goes into the URL', async () => {
    const { fake, view } = renderPlayers({
      routes: { 'GET /gm/v1/players/search': { body: { items: [{ playerId: 'p-2', displayName: 'Alice Secret' }], nextCursor: null } } },
    });

    await userEvent.type(screen.getByLabelText('Display name'), 'Alice Secret');
    await userEvent.click(screen.getByRole('button', { name: 'Search' }));

    const link = await screen.findByRole('link', { name: 'p-2' });
    expect(link).toHaveAttribute('href', '/players/p-2');
    expect(fake.requests[0]?.search).toBe('?displayName=Alice%20Secret&pageSize=50');
    expect(view.path()).toBe('/players');
    expect(window.location.hash).not.toContain('Alice');

    await userEvent.click(link);
    expect(view.path()).toBe('/players/p-2');
  });

  it('says "player not found" on 404', async () => {
    renderPlayers({ routes: { 'GET /gm/v1/players/nobody': problemReply(404, 'PLAYER_NOT_FOUND') } });

    await userEvent.type(screen.getByLabelText('Player id'), 'nobody');
    await userEvent.click(screen.getAllByRole('button', { name: 'Find' })[0]!);

    expect(await screen.findByRole('alert')).toHaveTextContent('Player not found.');
  });

  it('shows each mode only when its route is served', () => {
    const operations = Object.fromEntries(Object.entries(READ_ROUTES).filter(([name]) => name !== 'gm.player_by_mapping' && name !== 'gm.player_search')) as typeof READ_ROUTES;
    renderPlayers({ operations });

    expect(screen.getByLabelText('Player id')).toBeInTheDocument();
    expect(screen.queryByLabelText('Mapping key')).toBeNull();
    expect(screen.queryByLabelText('Display name')).toBeNull();
  });
});
