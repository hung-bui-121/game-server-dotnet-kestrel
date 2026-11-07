import { call } from '../../api/client';
import { JsonPagedList } from '../../components/JsonPagedList';
import { PAGE_SIZE } from '../../components/PagedList';
import type { GmPlayerTabProps } from '../../navigation/registry';

/** GM audit entries about a player (`gm.player_audit`, paged, JSON text). */
export function PlayerAuditTab({ api, playerId }: GmPlayerTabProps) {
  return (
    <JsonPagedList
      queryKey={['gm', 'player_audit', playerId]}
      itemLabel="Audit entry"
      emptyText="No audit entries."
      fetchPage={(cursor) =>
        call(api.GET('/gm/v1/players/{playerId}/audit', { params: { path: { playerId }, query: { pageSize: PAGE_SIZE, cursor } } }))
      }
    />
  );
}
