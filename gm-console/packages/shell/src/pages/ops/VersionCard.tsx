import { Card, Table, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { call, type GmClient } from '../../api/client';
import { QueryState } from '../../components/QueryState';

/** Server version (`gm.ops_version`). */
export function VersionCard({ api }: { api: GmClient }) {
  const query = useQuery({ queryKey: ['gm', 'ops_version'], queryFn: () => call(api.GET('/gm/v1/ops/version')) });
  return (
    <Card withBorder component="section" aria-label="Version">
      <Title order={2} size="h4">
        Version
      </Title>
      <QueryState query={query}>
        {(version) => (
          <Table aria-label="Server version" variant="vertical">
            <Table.Tbody>
              <Table.Tr>
                <Table.Th scope="row">Framework</Table.Th>
                <Table.Td data-testid="framework-version">{version.frameworkVersion}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Game</Table.Th>
                <Table.Td>
                  {version.gameId} {version.gameVersion}
                </Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Environment</Table.Th>
                <Table.Td>{version.environmentId}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Instance</Table.Th>
                <Table.Td>{version.instanceId}</Table.Td>
              </Table.Tr>
            </Table.Tbody>
          </Table>
        )}
      </QueryState>
    </Card>
  );
}
