import { Alert, Anchor } from '@mantine/core';
import { Link } from 'react-router';
import { HOME_PATH } from '../session/SessionProvider';

/** A page reached by URL that the staff member's permissions or this server's catalog do not include (FR-066). Calls nothing. */
export function NotAvailable() {
  return (
    <Alert color="gray" title="Not available" role="alert">
      This page is not available: your permissions or this GM API do not include it.{' '}
      <Anchor component={Link} to={HOME_PATH}>
        Back to the start page
      </Anchor>
    </Alert>
  );
}
