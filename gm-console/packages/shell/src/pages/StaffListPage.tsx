import { Anchor, Stack, Table, Title } from '@mantine/core';
import { Link } from 'react-router';
import { call } from '../api/client';
import { PAGE_SIZE, PagedList } from '../components/PagedList';
import type { components } from '../generated/gm-api';
import type { GmPageProps } from '../navigation/registry';
import { staffPath } from './paths';

type StaffView = components['schemas']['StaffView'];

/** Staff list (`gm.staff_list`, paged, FR-071). Read only (FR-074): no create, grant, disable, enable or revoke here. */
export function StaffListPage({ api }: GmPageProps) {
  return (
    <Stack>
      <Title order={1} size="h2">
        Staff
      </Title>
      <PagedList<StaffView>
        queryKey={['gm', 'staff_list']}
        fetchPage={(cursor) => call(api.GET('/gm/v1/staff', { params: { query: { pageSize: PAGE_SIZE, cursor } } }))}
        emptyText="No staff members."
        renderItems={(items) => (
          <div className="gm-table-scroll">
            <Table aria-label="Staff members">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Staff id</Table.Th>
                  <Table.Th>Display name</Table.Th>
                  <Table.Th>Status</Table.Th>
                  <Table.Th>Permissions</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {items.map((staff) => (
                  <Table.Tr key={staff.staffId}>
                    <Table.Td>
                      <Anchor component={Link} to={staffPath(staff.staffId)}>
                        {staff.staffId}
                      </Anchor>
                    </Table.Td>
                    <Table.Td>{staff.displayName}</Table.Td>
                    <Table.Td>{staff.status}</Table.Td>
                    <Table.Td>{staff.permissions.join(', ')}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </div>
        )}
      />
    </Stack>
  );
}
