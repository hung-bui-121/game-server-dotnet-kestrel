import { Card, Stack, Title } from '@mantine/core';
import { useNavigate } from 'react-router';
import { call } from '../api/client';
import { problemOf } from '../api/problem';
import { LookupForm } from '../components/LookupForm';
import { hasRoute, type GmPageProps } from '../navigation/registry';
import type { GmProblem } from '../types';
import { playerPath } from './paths';
import { PlayerNameSearch } from './PlayerNameSearch';

/** Mapping-key prefixes of the identity module (US7-2). */
export const MAPPING_KEY_HINT = 'pgs:…, gamecenter:… or guest:…';

/**
 * Players (FR-068, US7-2): lookup by player id (`gm.player`), by mapping key (`gm.player_by_mapping`, the key encoded in the
 * API path) and search by display name (`gm.player_search`); each mode only when its route is served. A hit opens
 * `#/players/{playerId}`; the mapping key and the display name never go into the URL (FR-061).
 */
export function PlayersPage({ api, catalog }: GmPageProps) {
  const navigate = useNavigate();

  const open = async (lookup: () => Promise<{ playerId: string }>): Promise<GmProblem | undefined> => {
    try {
      const player = await lookup();
      await navigate(playerPath(player.playerId));
      return undefined;
    } catch (error) {
      return problemOf(error);
    }
  };

  return (
    <Stack>
      <Title order={1} size="h2">
        Players
      </Title>
      {hasRoute(catalog, 'gm.player') && (
        <Card withBorder>
          <LookupForm
            label="Player id"
            notFound="Player not found."
            onLookup={(playerId) => open(() => call(api.GET('/gm/v1/players/{playerId}', { params: { path: { playerId } } })))}
          />
        </Card>
      )}
      {hasRoute(catalog, 'gm.player_by_mapping') && (
        <Card withBorder>
          <LookupForm
            label="Mapping key"
            description={MAPPING_KEY_HINT}
            notFound="No player has this mapping key."
            onLookup={(mappingKey) =>
              open(() => call(api.GET('/gm/v1/players/by-mapping/{mappingKey}', { params: { path: { mappingKey } } })))
            }
          />
        </Card>
      )}
      {hasRoute(catalog, 'gm.player_search') && (
        <Card withBorder>
          <PlayerNameSearch api={api} />
        </Card>
      )}
    </Stack>
  );
}
