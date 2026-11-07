import { Alert, Button, Stack, Table, Title } from '@mantine/core';
import type { GmConsoleConfig, GmMe } from '../types';

export interface BlockScreenProps {
  config: GmConsoleConfig;
  me: GmMe;
  onSignOut: () => void;
}

/** Q19 (FR-055): the console points at another game or environment than its config.json. Nothing loads; only sign-out. */
export function BlockScreen({ config, me, onSignOut }: BlockScreenProps) {
  return (
    <main className="gm-center">
      <Stack maw="40rem" w="100%">
        <Title order={1} size="h2">
          Wrong server
        </Title>
        <Alert color="red" role="alert">
          This console is configured for another game or environment than the GM API it reached. Nothing is loaded; sign out and
          use the console of this environment.
        </Alert>
        <Table aria-label="Expected and actual server">
          <Table.Thead>
            <Table.Tr>
              <Table.Th />
              <Table.Th>Console (config.json)</Table.Th>
              <Table.Th>Server (/me)</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            <Table.Tr>
              <Table.Th scope="row">Game</Table.Th>
              <Table.Td>{config.gameId}</Table.Td>
              <Table.Td>{me.gameId}</Table.Td>
            </Table.Tr>
            <Table.Tr>
              <Table.Th scope="row">Environment</Table.Th>
              <Table.Td>{config.environmentId}</Table.Td>
              <Table.Td>{me.environmentId}</Table.Td>
            </Table.Tr>
          </Table.Tbody>
        </Table>
        <Button onClick={onSignOut} variant="default" w="fit-content">
          Sign out
        </Button>
      </Stack>
    </main>
  );
}
