import { Anchor, Table } from '@mantine/core';
import { Link } from 'react-router';
import { call } from '../../api/client';
import { PAGE_SIZE, PagedList } from '../../components/PagedList';
import type { components } from '../../generated/gm-api';
import type { GmPlayerTabProps } from '../../navigation/registry';
import { receiptPath } from '../paths';

type ReceiptSummary = components['schemas']['GmReceiptSummary'];

/** Receipts of a player (`gm.player_receipts`, paged); each row opens `#/receipts/{receiptId}` (FR-069, FR-070). */
export function ReceiptsTab({ api, playerId }: GmPlayerTabProps) {
  return (
    <PagedList<ReceiptSummary>
      queryKey={['gm', 'player_receipts', playerId]}
      fetchPage={(cursor) =>
        call(api.GET('/gm/v1/players/{playerId}/receipts', { params: { path: { playerId }, query: { pageSize: PAGE_SIZE, cursor } } }))
      }
      emptyText="No receipts."
      renderItems={(items) => (
        <div className="gm-table-scroll">
          <Table aria-label="Receipts">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Receipt</Table.Th>
                <Table.Th>Operation</Table.Th>
                <Table.Th>Actor</Table.Th>
                <Table.Th>Result</Table.Th>
                <Table.Th>Committed at (UTC)</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((r) => (
                <Table.Tr key={r.receiptId}>
                  <Table.Td>
                    <Anchor component={Link} to={receiptPath(r.receiptId)}>
                      {r.receiptId}
                    </Anchor>
                  </Table.Td>
                  <Table.Td>{r.operation}</Table.Td>
                  <Table.Td>
                    {r.actorType}:{r.actorId}
                  </Table.Td>
                  <Table.Td>{r.resultCode}</Table.Td>
                  <Table.Td>{r.committedAtUtc}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </div>
      )}
    />
  );
}
