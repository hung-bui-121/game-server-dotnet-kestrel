import { Stack, Title } from '@mantine/core';
import { hasRoute, type GmPageProps } from '../navigation/registry';
import { GateCard } from './ops/GateCard';
import { HealthCard } from './ops/HealthCard';
import { VersionCard } from './ops/VersionCard';

/**
 * Operations (FR-072): health, version and gate cards, each only when served and each its own read (A10). Read only. A plain
 * stack: Mantine's SimpleGrid injects a responsive <style> element, which the strict CSP blocks (V3).
 */
export function OpsPage({ api, catalog }: GmPageProps) {
  return (
    <Stack>
      <Title order={1} size="h2">
        Operations
      </Title>
      <Stack gap="md">
        {hasRoute(catalog, 'gm.health') && <HealthCard api={api} />}
        {hasRoute(catalog, 'gm.ops_version') && <VersionCard api={api} />}
        {hasRoute(catalog, 'gm.ops_gate') && <GateCard api={api} />}
      </Stack>
    </Stack>
  );
}
