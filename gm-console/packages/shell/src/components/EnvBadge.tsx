import { Badge } from '@mantine/core';

/** The production environment id: shown in red everywhere (design Q7, FR-060). */
export const LIVE_ENVIRONMENT = 'live';

export function isLive(environmentId: string): boolean {
  return environmentId.toLowerCase() === LIVE_ENVIRONMENT;
}

export function EnvBadge({ environmentId }: { environmentId: string }) {
  const live = isLive(environmentId);
  return (
    <Badge color={live ? 'red' : 'blue'} variant="filled" data-tone={live ? 'danger' : 'normal'} aria-label={`Environment ${environmentId}`}>
      {environmentId}
    </Badge>
  );
}
