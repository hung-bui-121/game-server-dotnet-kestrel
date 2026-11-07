import { Card, Table, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { call, type GmClient } from '../../api/client';
import { QueryState } from '../../components/QueryState';

/** Health report (`gm.health`): overall status and one row per check. */
export function HealthCard({ api }: { api: GmClient }) {
  const query = useQuery({ queryKey: ['gm', 'health'], queryFn: () => call(api.GET('/gm/v1/health')) });
  return (
    <Card withBorder component="section" aria-label="Health">
      <Title order={2} size="h4">
        Health
      </Title>
      <QueryState query={query}>
        {(report) => (
          <>
            <Text size="sm" data-testid="health-status">
              Status: {report.status} ({String(report.durationMs)} ms)
            </Text>
            <div className="gm-table-scroll">
              <Table aria-label="Health checks">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Check</Table.Th>
                    <Table.Th>Status</Table.Th>
                    <Table.Th>Description</Table.Th>
                    <Table.Th>Duration (ms)</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {report.entries.map((entry) => (
                    <Table.Tr key={entry.name}>
                      <Table.Td>{entry.name}</Table.Td>
                      <Table.Td>{entry.status}</Table.Td>
                      <Table.Td>{entry.description ?? '—'}</Table.Td>
                      <Table.Td>{String(entry.durationMs)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
          </>
        )}
      </QueryState>
    </Card>
  );
}
