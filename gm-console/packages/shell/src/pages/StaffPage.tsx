import { Stack, Table, Tabs, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router';
import { call } from '../api/client';
import { JsonPagedList } from '../components/JsonPagedList';
import { PAGE_SIZE } from '../components/PagedList';
import { QueryState } from '../components/QueryState';
import { hasRoute, type GmPageProps } from '../navigation/registry';

/** A staff member (`gm.staff`) with the audit tab (`gm.staff_audit`, paged) when served (FR-071). Read only (FR-074). */
export function StaffPage({ api, catalog }: GmPageProps) {
  const { staffId = '' } = useParams();
  const query = useQuery({
    queryKey: ['gm', 'staff', staffId],
    queryFn: () => call(api.GET('/gm/v1/staff/{staffId}', { params: { path: { staffId } } })),
  });

  return (
    <Stack>
      <Title order={1} size="h2">
        Staff {staffId}
      </Title>
      <QueryState query={query} notFound="Staff member not found.">
        {(staff) => (
          <Table aria-label="Staff member" variant="vertical" maw="40rem">
            <Table.Tbody>
              <Table.Tr>
                <Table.Th scope="row">Staff id</Table.Th>
                <Table.Td>{staff.staffId}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Display name</Table.Th>
                <Table.Td>{staff.displayName}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Email</Table.Th>
                <Table.Td>{staff.email ?? '—'}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Status</Table.Th>
                <Table.Td>{staff.status}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">SSO bound</Table.Th>
                <Table.Td>{staff.oidcBound ? 'yes' : 'no'}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Permissions</Table.Th>
                <Table.Td>{staff.permissions.join(', ') || '—'}</Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Th scope="row">Updated at (UTC)</Table.Th>
                <Table.Td>{staff.updatedAtUtc ?? '—'}</Table.Td>
              </Table.Tr>
            </Table.Tbody>
          </Table>
        )}
      </QueryState>
      {query.isSuccess && hasRoute(catalog, 'gm.staff_audit') && (
        <Tabs defaultValue="audit" keepMounted={false}>
          <Tabs.List>
            <Tabs.Tab value="audit">Audit</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="audit" pt="sm">
            <JsonPagedList
              queryKey={['gm', 'staff_audit', staffId]}
              itemLabel="Audit entry"
              emptyText="No audit entries."
              fetchPage={(cursor) =>
                call(api.GET('/gm/v1/staff/{staffId}/audit', { params: { path: { staffId }, query: { pageSize: PAGE_SIZE, cursor } } }))
              }
            />
          </Tabs.Panel>
        </Tabs>
      )}
    </Stack>
  );
}
