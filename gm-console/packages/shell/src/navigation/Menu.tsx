import { NavLink, Stack, Text } from '@mantine/core';
import { Link, useLocation } from 'react-router';
import type { MenuGroup } from './registry';

/** The navigation: visible pages by group (FR-066). */
export function Menu({ groups }: { groups: MenuGroup[] }) {
  const location = useLocation();
  return (
    <Stack gap="xs">
      {groups.map(({ group, pages }) => (
        <div key={group} role="group" aria-label={group}>
          <Text size="xs" c="dimmed" tt="uppercase" fw={700} px="sm">
            {group}
          </Text>
          {pages.map((page) => (
            <NavLink
              key={page.id}
              component={Link}
              to={page.path}
              label={page.title}
              active={location.pathname === page.path || location.pathname.startsWith(page.path + '/')}
            />
          ))}
        </div>
      ))}
    </Stack>
  );
}
