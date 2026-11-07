import { Card, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { call, type GmClient } from '../../api/client';
import { JsonView } from '../../components/JsonView';
import { QueryState } from '../../components/QueryState';

/** Client gate (`gm.ops_gate`): maintenance and minimum client versions. Read only: the controls come with GM-3 (FR-074). */
export function GateCard({ api }: { api: GmClient }) {
  const query = useQuery({ queryKey: ['gm', 'ops_gate'], queryFn: () => call(api.GET('/gm/v1/ops/gate')) });
  return (
    <Card withBorder component="section" aria-label="Gate">
      <Title order={2} size="h4">
        Gate
      </Title>
      <QueryState query={query}>
        {(gate) => (
          <>
            <Text size="sm" data-testid="maintenance">
              Maintenance: {gate.maintenance.enabled ? 'on' : 'off'}
              {gate.maintenance.message ? ` — ${gate.maintenance.message}` : ''}
            </Text>
            <Text size="sm">Minimum client versions</Text>
            <JsonView value={gate.minClientVersions} label="Minimum client versions" />
          </>
        )}
      </QueryState>
    </Card>
  );
}
