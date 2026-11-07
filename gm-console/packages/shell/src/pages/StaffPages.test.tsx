import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { READ_ROUTES } from '../test/fixtures';
import { renderPage } from '../test/renderPage';
import { StaffListPage } from './StaffListPage';
import { StaffPage } from './StaffPage';

const staff = (staffId: string, extra: Record<string, unknown> = {}) => ({
  staffId,
  displayName: `${staffId} name`,
  email: null,
  status: 'Active',
  disabled: false,
  oidcBound: false,
  permissions: ['gm.access', 'staff.read'],
  updatedAtUtc: null,
  ...extra,
});

/** GM-2 offers no staff mutation (FR-074): the only buttons are tabs and "Load more". */
function expectNoMutationControl() {
  const labels = screen.queryAllByRole('button').map((b) => b.textContent);
  expect(labels.filter((l) => l !== 'Load more')).toEqual([]);
  for (const word of [/create/i, /grant/i, /disable/i, /enable/i, /revoke/i]) {
    expect(screen.queryByRole('button', { name: word })).toBeNull();
  }
}

describe('Staff pages (FR-071, FR-074, US7-5, US7-7)', () => {
  it('lists staff page by page with links to the detail page', async () => {
    const { fake, view } = renderPage((v) => <StaffListPage api={v.api} me={v.me} catalog={v.catalog} />, '/staff', '/staff', {
      routes: {
        'GET /gm/v1/staff': (request) =>
          request.search.includes('cursor=next')
            ? { body: { items: [staff('ops-1')], nextCursor: null } }
            : { body: { items: [staff('admin')], nextCursor: 'next' } },
      },
    });

    const table = await screen.findByRole('table', { name: 'Staff members' });
    expect(within(table).getAllByRole('row')[1]?.textContent).toBe('adminadmin nameActivegm.access, staff.read');
    expectNoMutationControl();
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByRole('link', { name: 'ops-1' })).toBeInTheDocument();
    expect(fake.requests.map((r) => r.search)).toEqual(['?pageSize=50', '?pageSize=50&cursor=next']);

    await userEvent.click(screen.getByRole('link', { name: 'admin' }));
    expect(view.path()).toBe('/staff/admin');
  });

  it('shows a staff member and the audit tab', async () => {
    const { fake } = renderPage((v) => <StaffPage api={v.api} me={v.me} catalog={v.catalog} />, '/staff/admin', '/staff/:staffId', {
      routes: {
        'GET /gm/v1/staff/admin': { body: staff('admin', { email: 'a@example.com', oidcBound: true }) },
        'GET /gm/v1/staff/admin/audit': { body: { items: [{ action: 'gm.staff_grant' }], nextCursor: null } },
      },
    });

    const details = await screen.findByRole('table', { name: 'Staff member' });
    expect(details).toHaveTextContent('a@example.com');
    expect(details).toHaveTextContent('SSO boundyes');
    expect((await screen.findByLabelText('Audit entry 1')).textContent).toContain('gm.staff_grant');
    expect(fake.calls()).toEqual(['GET /gm/v1/staff/admin', 'GET /gm/v1/staff/admin/audit']);
    expectNoMutationControl();
  });

  it('has no audit tab when gm.staff_audit is not served', async () => {
    const operations = Object.fromEntries(Object.entries(READ_ROUTES).filter(([name]) => name !== 'gm.staff_audit')) as typeof READ_ROUTES;
    const { fake } = renderPage((v) => <StaffPage api={v.api} me={v.me} catalog={v.catalog} />, '/staff/admin', '/staff/:staffId', {
      routes: { 'GET /gm/v1/staff/admin': { body: staff('admin') } },
      operations,
    });

    await screen.findByRole('table', { name: 'Staff member' });
    expect(screen.queryByRole('tab', { name: 'Audit' })).toBeNull();
    expect(fake.calls()).toEqual(['GET /gm/v1/staff/admin']);
  });
});
