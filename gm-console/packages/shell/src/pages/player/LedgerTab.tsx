import { call } from '../../api/client';
import { JsonPagedList } from '../../components/JsonPagedList';
import { PAGE_SIZE } from '../../components/PagedList';
import type { GmPlayerTabProps } from '../../navigation/registry';

/** Resource ledger of a player (`economy.ledger`, paged, JSON text; shown only where the economy module serves it, D11). */
export function LedgerTab({ api, playerId }: GmPlayerTabProps) {
  return (
    <JsonPagedList
      queryKey={['gm', 'economy_ledger', playerId]}
      itemLabel="Ledger entry"
      emptyText="No ledger entries."
      fetchPage={(cursor) =>
        call(api.GET('/gm/v1/players/{playerId}/ledger', { params: { path: { playerId }, query: { pageSize: PAGE_SIZE, cursor } } }))
      }
    />
  );
}
