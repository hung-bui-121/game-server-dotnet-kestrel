import { Stack } from '@mantine/core';
import { JsonView } from './JsonView';
import { PagedList, type Page } from './PagedList';

export interface JsonPagedListProps {
  queryKey: readonly unknown[];
  fetchPage: (cursor: string | undefined) => Promise<Page<Record<string, unknown>>>;
  /** Accessible name of each entry ("Audit entry 1", …). */
  itemLabel: string;
  emptyText?: string;
}

/** A paged list of JSON objects (audit, ledger), each shown as JSON text (FR-073, FR-076). */
export function JsonPagedList({ queryKey, fetchPage, itemLabel, emptyText }: JsonPagedListProps) {
  return (
    <PagedList
      queryKey={queryKey}
      fetchPage={fetchPage}
      emptyText={emptyText}
      renderItems={(items) => (
        <Stack gap="xs">
          {items.map((item, index) => (
            <JsonView key={index} value={item} label={`${itemLabel} ${index + 1}`} />
          ))}
        </Stack>
      )}
    />
  );
}
