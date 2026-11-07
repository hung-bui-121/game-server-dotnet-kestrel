import { Stack, Table, Tabs, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router';
import { call } from '../api/client';
import { JsonView } from '../components/JsonView';
import { QueryState } from '../components/QueryState';
import { useConsole } from '../navigation/ConsoleContext';
import { isVisible, type GmPageProps } from '../navigation/registry';

/**
 * A player (FR-069, US7-3): every aggregate (id, exists, client-visible, rev, schema versions, size, updated at), the deletion
 * state, each state as JSON text, then the visible player tabs (Receipts, Audit, Ledger), each its own read (A10).
 */
export function PlayerPage({ api, me, catalog }: GmPageProps) {
  const { playerId = '' } = useParams();
  const { registry } = useConsole();
  const query = useQuery({
    queryKey: ['gm', 'player', playerId],
    queryFn: () => call(api.GET('/gm/v1/players/{playerId}', { params: { path: { playerId } } })),
  });
  const tabs = registry.playerTabs.filter((tab) => isVisible(tab, me, catalog));

  return (
    <Stack>
      <Title order={1} size="h2">
        Player {playerId}
      </Title>
      <QueryState query={query} notFound="Player not found.">
        {(player) => (
          <Stack>
            <div className="gm-table-scroll">
              <Table aria-label="Aggregates">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Aggregate</Table.Th>
                    <Table.Th>Exists</Table.Th>
                    <Table.Th>Client-visible</Table.Th>
                    <Table.Th>Rev</Table.Th>
                    <Table.Th>Schema (code / stored)</Table.Th>
                    <Table.Th>Size (bytes)</Table.Th>
                    <Table.Th>Updated at (UTC)</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {player.aggregates.map((a) => (
                    <Table.Tr key={a.id}>
                      <Table.Td>{a.id}</Table.Td>
                      <Table.Td>{a.exists ? 'yes' : 'no'}</Table.Td>
                      <Table.Td>{a.clientVisible ? 'yes' : 'no'}</Table.Td>
                      <Table.Td>{String(a.rev)}</Table.Td>
                      <Table.Td>
                        {String(a.schemaVersion)} / {String(a.storedSchemaVersion)}
                      </Table.Td>
                      <Table.Td>{String(a.sizeBytes)}</Table.Td>
                      <Table.Td>{a.updatedAtUtc ?? '—'}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
            <Title order={2} size="h4">
              Deletion
            </Title>
            {player.deletion ? <JsonView value={player.deletion} label="Deletion state" /> : <Text size="sm">No deletion requested.</Text>}
            {player.unreadable && player.unreadable.length > 0 && (
              <>
                <Title order={2} size="h4">
                  Unreadable aggregates
                </Title>
                <JsonView value={player.unreadable} label="Unreadable aggregates" />
              </>
            )}
            <Title order={2} size="h4">
              States
            </Title>
            {player.aggregates.map((a) => (
              <div key={a.id}>
                <Text size="sm" fw={600}>
                  {a.id}
                </Text>
                <JsonView value={a.state} label={`State ${a.id}`} />
              </div>
            ))}
          </Stack>
        )}
      </QueryState>
      {query.isSuccess && tabs.length > 0 && (
        <Tabs defaultValue={tabs[0]?.id} keepMounted={false}>
          <Tabs.List>
            {tabs.map((tab) => (
              <Tabs.Tab key={tab.id} value={tab.id}>
                {tab.title}
              </Tabs.Tab>
            ))}
          </Tabs.List>
          {tabs.map((tab) => (
            <Tabs.Panel key={tab.id} value={tab.id} pt="sm">
              <tab.component api={api} me={me} catalog={catalog} playerId={playerId} />
            </Tabs.Panel>
          ))}
        </Tabs>
      )}
    </Stack>
  );
}
