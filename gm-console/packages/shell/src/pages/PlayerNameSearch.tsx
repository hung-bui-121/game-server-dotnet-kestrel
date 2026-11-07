import { Anchor, Button, Group, Table, TextInput } from '@mantine/core';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { call, type GmClient } from '../api/client';
import { PAGE_SIZE, PagedList } from '../components/PagedList';
import type { components } from '../generated/gm-api';
import { playerPath } from './paths';

type SearchItem = components['schemas']['GmPlayerSearchItem'];

/** Search by exact display name (`gm.player_search`, paged). The name stays in memory (FR-061). */
export function PlayerNameSearch({ api }: { api: GmClient }) {
  const [value, setValue] = useState('');
  const [submitted, setSubmitted] = useState<string | undefined>(undefined);

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const trimmed = value.trim();
    setSubmitted(trimmed === '' ? undefined : trimmed);
  };

  return (
    <>
      <form onSubmit={submit} aria-label="Display name search">
        <Group align="flex-end" gap="sm" wrap="wrap">
          <TextInput label="Display name" description="Exact display name" value={value} onChange={(e) => setValue(e.currentTarget.value)} />
          <Button type="submit">Search</Button>
        </Group>
      </form>
      {submitted !== undefined && (
        <PagedList<SearchItem>
          queryKey={['gm', 'player_search', submitted]}
          fetchPage={(cursor) =>
            call(api.GET('/gm/v1/players/search', { params: { query: { displayName: submitted, pageSize: PAGE_SIZE, cursor } } }))
          }
          emptyText="No player has this display name."
          renderItems={(items) => (
            <div className="gm-table-scroll">
              <Table aria-label="Players found">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Display name</Table.Th>
                    <Table.Th>Player id</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {items.map((item) => (
                    <Table.Tr key={item.playerId}>
                      <Table.Td>{item.displayName}</Table.Td>
                      <Table.Td>
                        <Anchor component={Link} to={playerPath(item.playerId)}>
                          {item.playerId}
                        </Anchor>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
          )}
        />
      )}
    </>
  );
}
